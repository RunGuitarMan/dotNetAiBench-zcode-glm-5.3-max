using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Motiva.Application;
using Motiva.Application.Administration;
using Motiva.Application.Budgets;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Economy;
using Motiva.Application.Ports;
using Motiva.Application.Progress;
using Motiva.Infrastructure;

// Seed of the T09 load profile through validating application use cases (preparation is
// outside the measurement; no direct INSERTs). seed=42 keeps the state distribution stable.
var services = new ServiceCollection()
    .AddLogging()
    .AddMotivaApplication()
    .AddMotivaInfrastructure(options =>
    {
        options.PostgresConnectionString = Environment.GetEnvironmentVariable("Motiva__PostgresConnectionString")
            ?? "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand";
        options.ValkeyEndpoint = Environment.GetEnvironmentVariable("Motiva__ValkeyEndpoint") ?? "localhost:6380";
    })
    .BuildServiceProvider();

var watch = Stopwatch.StartNew();
T Resolve<T>() where T : notnull => services.GetRequiredService<T>();
void Ok(int status, string what)
{
    if (status is not (200 or 201 or 204))
    {
        throw new InvalidOperationException("seed step failed: " + what + " -> " + status);
    }
}

Guid Id(string json)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
}

var mainCompany = Guid.Parse("11111111-1111-4111-8111-111111111111");
var otherCompany = Guid.Parse("22222222-2222-4222-8222-222222222222");
var admin = new ActorContext(mainCompany, ActorType.User, 1, "admin1", true);
var otherAdmin = new ActorContext(otherCompany, ActorType.User, 1, "admin1-other", true);

foreach (var (companyId, actor) in new[] { (mainCompany, admin), (otherCompany, otherAdmin) })
{
    await Resolve<BootstrapService>().RunAsync(new[] { new BootstrapActor(actor.Subject, companyId, "user", 1, "Employee,Admin") }, "Load", CancellationToken.None);
}

Console.WriteLine($"[seed] companies ensured ({watch.Elapsed.TotalSeconds:F0}s)");

async Task CreateEmployeesAsync(Guid companyId, ActorContext actor, int from, int to)
{
    await Parallel.ForAsync(from, to, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (masterId, ct) =>
    {
        using var scope = services.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<EmployeesService>();
        try
        {
            var response = await scoped.CreateAsync(actor, masterId, Array.Empty<string>(), "seed-" + companyId.ToString("N")[..6] + "-" + masterId, ct);
            if (response.Status is not (200 or 201))
            {
                throw new InvalidOperationException("employee seed failed: " + response.Status + " " + response.Body[..Math.Min(120, response.Body.Length)]);
            }
        }
        catch (MotivaException ex) when (ex.Code == ErrorCode.ConflictState)
        {
            // already seeded
        }
    });
}

await CreateEmployeesAsync(mainCompany, admin, 2, 10_002);
await CreateEmployeesAsync(otherCompany, otherAdmin, 2, 102);
Console.WriteLine($"[seed] employees created ({watch.Elapsed.TotalSeconds:F0}s)");

var resourceIds = new List<Guid>();
foreach (var code in new[] { "SEEDSTAR", "SEEDLEARN", "SEEDCOIN" })
{
    var response = await Resolve<ResourcesService>().CreateAsync(admin, code, code, "seed-" + code, CancellationToken.None);
    if (response.Status == 201)
    {
        resourceIds.Add(Id(response.Body));
    }
    else
    {
        var page = await Resolve<ResourcesService>().ListAsync(admin, null, 100, null, CancellationToken.None);
        resourceIds.Add(page.Items.Single(r => r.Code == code).Id);
    }
}

Console.WriteLine($"[seed] resources ready ({watch.Elapsed.TotalSeconds:F0}s)");

var random = new Random(42);
var now = TimeProvider.System.GetUtcNow();
var seasonStart = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
var seasonEnd = new DateTimeOffset(now.Year, 12, 31, 20, 0, 0, TimeSpan.Zero);
var campaignsService = Resolve<CampaignsService>();
var content = Resolve<CampaignContentService>();
var seeded = new List<(Guid CampaignId, Guid TaskId)>();
for (var i = 0; i < 10; i++)
{
    var code = "SEEDC" + i.ToString("00");
    var existing = await Resolve<ICampaignCatalog>().ListAsync(mainCompany, null, CampaignStatus.Published, 100, null, CancellationToken.None);
    var campaignRec = existing.Items.FirstOrDefault(c => c.Code == code);
    Guid campaignId;
    Guid firstTaskId = Guid.Empty;
    if (campaignRec is null)
    {
        var create = await campaignsService.CreateAsync(admin, code, "Seed campaign " + i, null, 1, seasonStart, seasonEnd, null, "seed-c-" + i, CancellationToken.None);
        Ok(create.Status, "campaign create");
        campaignId = Id(create.Body);
        var put = await campaignsService.PutResourcesAsync(admin, campaignId, ETags.Format(1), resourceIds, CancellationToken.None);
        Ok(put.Status, "resources put");
        var stream = await content.CreateStreamAsync(admin, campaignId, "S1", "Stream", "seed-s-" + i, CancellationToken.None);
        Ok(stream.Status, "stream create");
        var streamId = Id(stream.Body);
        for (var t = 0; t < 10; t++)
        {
            var rewardItems = t % 3 == 0
                ? Array.Empty<Motiva.Application.Dto.RewardItemDto>()
                : new[] { new Motiva.Application.Dto.RewardItemDto(resourceIds[t % 3], 1 + random.Next(1, 5)) };
            var task = await content.CreateTaskAsync(admin, streamId, "T" + t.ToString("00"), "Task", null, 3 + random.Next(1, 20),
                Motiva.Domain.Periods.PeriodKind.Day, random.Next(0, 11), rewardItems, null, "seed-t-" + i + "-" + t, CancellationToken.None);
            Ok(task.Status, "task create");
            if (t == 0)
            {
                firstTaskId = Id(task.Body);
            }
        }

        // One full-season challenge per campaign for the leaderboard branch of the load mix.
        var challenge = await content.CreateChallengeAsync(admin, campaignId, streamId, seasonStart, seasonEnd, "seed-ch-" + i, CancellationToken.None);
        Ok(challenge.Status, "challenge create");
        var current = await campaignsService.GetAsync(admin, campaignId, CancellationToken.None);
        var publish = await campaignsService.PatchAsync(admin, campaignId, ETags.Format(current.Version), null, null, null, CampaignStatus.Published, CancellationToken.None);
        Ok(publish.Status, "publish");
    }
    else
    {
        campaignId = campaignRec.Id;
        var streams = await content.ListStreamsAsync(admin, campaignId, 100, null, CancellationToken.None);
        var streamId0 = streams.Items[0].Id;
        var tasks = await content.ListTasksAsync(admin, streamId0, 100, null, CancellationToken.None);
        firstTaskId = tasks.Items[0].Id;
        var challenges = await content.ListChallengesAsync(admin, campaignId, 100, null, CancellationToken.None);
        if (challenges.Items.Count == 0)
        {
            // Pre-publication content only: archived campaigns cannot gain challenges; the seed
            // campaigns stay Published, so this re-run path is only for a fresh DB.
            Console.WriteLine($"[seed] warning: campaign {code} has no challenge (created after publish?)");
        }
    }

    foreach (var resourceId in resourceIds)
    {
        var allocation = await Resolve<BudgetService>().AllocateAsync(admin, campaignId, resourceId, 100_000_000, null, "SEED-B-" + i + "-" + resourceId, CancellationToken.None);
        if (allocation.Status is not (200 or 201) && !allocation.Body.Contains("already"))
        {
            Ok(allocation.Status, "budget allocate");
        }
    }

    var grants = Resolve<IntegrationGrantsService>();
    var grantResponse = await grants.CreateAsync(admin, "progress-source", GrantKind.Progress, campaignId, null, null, "seed-g-" + i, CancellationToken.None);
    if (grantResponse.Status != 201 && !grantResponse.Body.Contains("already"))
    {
        Ok(grantResponse.Status, "progress grant");
    }

    seeded.Add((campaignId, firstTaskId));
}

Console.WriteLine($"[seed] campaigns ready ({watch.Elapsed.TotalSeconds:F0}s)");

var systems = Resolve<PurchaseSystemsService>();
Guid systemId;
var systemResponse = await systems.CreateAsync(admin, "SEEDSHOP", "Seed shop", resourceIds, "seed-ps-1", CancellationToken.None);
if (systemResponse.Status == 201)
{
    systemId = Id(systemResponse.Body);
    var spendGrant = await Resolve<IntegrationGrantsService>().CreateAsync(admin, "shop-source", GrantKind.Spend, null, resourceIds[0], systemId, "seed-spend", CancellationToken.None);
    Ok(spendGrant.Status, "spend grant");
}
else
{
    var page = await systems.ListAsync(admin, 100, null, CancellationToken.None);
    systemId = page.Items.Single(s => s.Code == "SEEDSHOP").Id;
}

Console.WriteLine($"[seed] shop ready ({watch.Elapsed.TotalSeconds:F0}s)");

// 100 000 historical events through the full vertical; the completion share is reported.
var progressActor = new ActorContext(mainCompany, ActorType.Service, null, "progress-source", false);
var completions = 0L;
var eventWatch = Stopwatch.StartNew();
await Parallel.ForAsync(0, 100_000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (n, ct) =>
{
    using var scope = services.CreateScope();
    var scopedEvents = scope.ServiceProvider.GetRequiredService<ProgressEventsService>();
    var campaign = seeded[n % seeded.Count];
    var masterId = 2 + (n * 7919) % 10_000;
    var response = await scopedEvents.PostAsync(progressActor, "SEED-E-" + n, masterId, campaign.TaskId, 1 + n % 3, ct);
    if (response.Status != 201)
    {
        throw new InvalidOperationException("event seed failed " + n + ": " + response.Status + " " + response.Body[..Math.Min(160, response.Body.Length)]);
    }

    if (!response.Body.Contains("\"completion\":null"))
    {
        Interlocked.Increment(ref completions);
    }
});
Console.WriteLine($"[seed] 100k events in {eventWatch.Elapsed.TotalSeconds:F0}s; completions: {completions} ({completions / 1000.0:F1}%)");

// Wallet top-ups: EVERY seeded employee gets funds — the load mix spends 1 unit from a random
// wallet, and a Posted business result requires sufficient funds (T09: "достаточно средств").
await Parallel.ForAsync(2, 10_002, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (masterId, ct) =>
{
    using var scope = services.CreateScope();
    var awards = scope.ServiceProvider.GetRequiredService<ManualAwardsService>();
    var campaign = seeded[masterId % seeded.Count];
    try
    {
        var response = await awards.AwardAsync(admin, campaign.CampaignId, masterId, resourceIds[0], 100, "seed spend funds", "SEED-MA-" + masterId, ct);
        if (response.Status != 201)
        {
            throw new InvalidOperationException("top-up failed " + masterId + ": " + response.Status + " " + response.Body[..Math.Min(160, response.Body.Length)]);
        }
    }
    catch (MotivaException ex) when (ex.Code == ErrorCode.ConflictBusinessNumber)
    {
        // already seeded
    }
});
// 100 000 historical movements: manual awards of 1 unit through the use case (B24 numbers).
var awardWatch = Stopwatch.StartNew();
var awardActor = new ActorContext(mainCompany, ActorType.User, 1, "admin1", true);
await Parallel.ForAsync(0, 100_000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (n, ct) =>
{
    using var scope = services.CreateScope();
    var awards = scope.ServiceProvider.GetRequiredService<ManualAwardsService>();
    var campaign = seeded[n % seeded.Count];
    var masterId = 2 + (n * 7919) % 10_000;
    var resource = resourceIds[n % resourceIds.Count];
    try
    {
        var response = await awards.AwardAsync(awardActor, campaign.CampaignId, masterId, resource, 1, "seed movement", "SEED-M-" + n, ct);
        if (response.Status is not (200 or 201))
        {
            throw new InvalidOperationException("movement seed failed " + n + ": " + response.Status + " " + response.Body[..Math.Min(160, response.Body.Length)]);
        }
    }
    catch (MotivaException ex) when (ex.Code == ErrorCode.ConflictBusinessNumber)
    {
        // already seeded
    }
});
Console.WriteLine($"[seed] 100k movements in {awardWatch.Elapsed.TotalSeconds:F0}s");

// T09 two-competitor pair: a DEDICATED published campaign whose task rewards exactly the whole
// remaining budget — two parallel completions race for the last unit (goal 3 → delta 3, reward
// 10, budget exactly 10). A fresh code per run keeps the remainder reproducible (E04 under load).
var raceCode = "SEEDRACE" + DateTime.UtcNow.ToString("MMddHHmmss");
var raceCreate = await campaignsService.CreateAsync(admin, raceCode, "Race pair", null, 1, seasonStart, seasonEnd, null, "seed-race-" + raceCode, CancellationToken.None);
Ok(raceCreate.Status, "race campaign create");
var raceCampaignId = Id(raceCreate.Body);
await campaignsService.PutResourcesAsync(admin, raceCampaignId, ETags.Format(1), [resourceIds[0]], CancellationToken.None);
var raceStream = await content.CreateStreamAsync(admin, raceCampaignId, "S1", "Race stream", "seed-race-s", CancellationToken.None);
Ok(raceStream.Status, "race stream create");
var raceTask = await content.CreateTaskAsync(
    admin, Id(raceStream.Body), "TR", "Race task", null, 3, Motiva.Domain.Periods.PeriodKind.Day, 0,
    new[] { new Motiva.Application.Dto.RewardItemDto(resourceIds[0], 10L) }, null, "seed-race-t", CancellationToken.None);
Ok(raceTask.Status, "race task create");
var raceBudget = await Resolve<BudgetService>().AllocateAsync(admin, raceCampaignId, resourceIds[0], 10, null, "SEED-RACE-BUDGET-" + raceCode, CancellationToken.None);
Ok(raceBudget.Status, "race budget allocate (exactly the reward — the last remainder)");
var raceGrant = await Resolve<IntegrationGrantsService>().CreateAsync(admin, "progress-source", GrantKind.Progress, raceCampaignId, null, null, "seed-race-g", CancellationToken.None);
if (raceGrant.Status != 201 && !raceGrant.Body.Contains("already"))
{
    Ok(raceGrant.Status, "race progress grant");
}

Console.WriteLine($"[seed] race pair ready: campaign {raceCode}, task goal 3, reward 10, budget remainder exactly 10");
Console.WriteLine($"[seed] complete in {watch.Elapsed.TotalSeconds:F0}s");
