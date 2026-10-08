using System.Collections.Concurrent;
using Motiva.Application.Common;
using Motiva.Application.Economy;
using Motiva.Application.Ports;
using Motiva.Application.Reads;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Parallel invariants (T3-11/T3-12/T3-13): barriers start branches together;
/// assertions are invariant to ordering — exact totals, exactly one Posted.</summary>
[Collection("functional")]
public sealed class ConcurrencyTests(MotivaFunctionalFixture fixture)
{
    [Fact]
    public async Task e04_two_competitors_single_budget_exactly_one_posted()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(1 + 100);
        await world.CreateEmployeeAsync(2 + 100);
        var setup = await world.CreatePublishedCampaignAsync("E04", ("T", 1, "Month", 5, new[] { (a, 10L) }));
        await world.AllocateAsync(setup.CampaignId, a, 10, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = Task.Run(async () => { await barrier.Task; return await world.SendEventAsync("src", "X1", 101, setup.TaskId, 1); });
        var t2 = Task.Run(async () => { await barrier.Task; return await world.SendEventAsync("src", "X2", 102, setup.TaskId, 1); });
        barrier.SetResult();
        var results = await Task.WhenAll(t1, t2);

        var bodies = results.Select(r => TestWorld.ParseJson(r.Body)).ToList();
        var granted = bodies.Count(b => b.GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString() == "Granted");
        var declined = bodies.Count(b => b.GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString() == "DeclinedInsufficientBudget");
        Assert.Equal(1, granted);
        Assert.Equal(1, declined);
        Assert.Equal(0, await world.BudgetAvailableAsync(setup.CampaignId, a));
        var wallets = await Task.WhenAll(world.WalletAsync(101), world.WalletAsync(102));
        Assert.Equal(10, wallets.Sum(w => w["A"]));
    }

    [Fact]
    public async Task e04_n_way_competition_never_overspends()
    {
        const int n = 5;
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        for (var i = 0; i < n; i++)
        {
            await world.CreateEmployeeAsync(200 + i);
        }

        var setup = await world.CreatePublishedCampaignAsync("E04N", ("T", 1, "Month", 1, new[] { (a, 4L) }));
        await world.AllocateAsync(setup.CampaignId, a, 10, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(async () => { await barrier.Task; return await world.SendEventAsync("src", "N" + i, 200 + i, setup.TaskId, 1); }))
            .ToArray();
        barrier.SetResult();
        var bodies = (await Task.WhenAll(tasks)).Select(r => TestWorld.ParseJson(r.Body)).ToList();

        var granted = bodies.Count(b => b.GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString() == "Granted");
        Assert.Equal(2, granted); // 10 / 4 = two full rewards
        Assert.Equal(2, await world.BudgetAvailableAsync(setup.CampaignId, a));
    }

    [Fact]
    public async Task parallel_events_same_employee_same_task_serialize_correctly()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(300);
        var setup = await world.CreatePublishedCampaignAsync("PAR", ("T", 10, "Month", 3, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 6)
            .Select(i => Task.Run(async () => { await barrier.Task; return await world.SendEventAsync("src", "P" + i, 300, setup.TaskId, 2); }))
            .ToArray();
        barrier.SetResult();
        var bodies = (await Task.WhenAll(tasks)).Select(r => TestWorld.ParseJson(r.Body)).ToList();

        // Sum of credited deltas equals min(goal, sum of deltas) = 10; exactly one completion.
        Assert.Equal(10, bodies.Sum(b => b.GetProperty("creditedDelta").GetInt64()));
        Assert.Single(bodies, b => b.GetProperty("completion").ValueKind != System.Text.Json.JsonValueKind.Null);
        var completions = bodies.Count(b => b.GetProperty("completion").ValueKind != System.Text.Json.JsonValueKind.Null);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task edge02_one_achievement_via_two_milestones_under_parallel_completions()
    {
        // EDGE-02 (T3-11): two PARALLEL completions of two tasks of one stream cross the
        // thresholds 10 and 20 that both lead to achievement X — X is granted exactly once
        // (B30.2 PK per season) and no threshold is lost (§3.8).
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(400);
        var x = await world.CreateAchievementAsync("X");
        var campaigns = world.Resolve<Motiva.Application.Campaigns.CampaignsService>();
        var content = world.Resolve<Motiva.Application.Campaigns.CampaignContentService>();
        var create = await campaigns.CreateAsync(world.Admin, "EDG2", "n", null, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero), null, "c-edge2", CancellationToken.None);
        var campaignId = TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!;
        var stream = await content.CreateStreamAsync(world.Admin, Guid.Parse(campaignId), "S1", "Stream", "s-edge2", CancellationToken.None);
        var streamId = TestWorld.ParseJson(stream.Body).GetProperty("id").GetString()!;

        // Task A awards 10 stream points, task B 15: the two completions cross threshold 10
        // (sum 10) and threshold 20 (sum 25) — 20 lies between the two sums (§3.8).
        var taskA = await content.CreateTaskAsync(world.Admin, Guid.Parse(streamId), "TA", "A", null, 1,
            Motiva.Domain.Periods.PeriodKind.Day, 10, [], null, "t-a", CancellationToken.None);
        var taskB = await content.CreateTaskAsync(world.Admin, Guid.Parse(streamId), "TB", "B", null, 1,
            Motiva.Domain.Periods.PeriodKind.Day, 15, [], null, "t-b", CancellationToken.None);
        var taskAId = TestWorld.ParseJson(taskA.Body).GetProperty("id").GetString()!;
        var taskBId = TestWorld.ParseJson(taskB.Body).GetProperty("id").GetString()!;

        await content.CreateMilestoneAsync(world.Admin, Guid.Parse(streamId), 10, x, "ms-10", CancellationToken.None); await content.CreateMilestoneAsync(world.Admin, Guid.Parse(streamId), 20, x, "ms-20", CancellationToken.None);
        var current = await campaigns.GetAsync(world.Admin, Guid.Parse(campaignId), CancellationToken.None);
        await campaigns.PatchAsync(world.Admin, Guid.Parse(campaignId), ETags.Format(current.Version), null, null, null,
            Motiva.Application.Ports.CampaignStatus.Published, CancellationToken.None);
        await world.CreateGrantAsync("src", "Progress", Guid.Parse(campaignId));

        // Two completions started together by the barrier — one per task of the SAME stream.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeA = Task.Run(async () =>
        {
            await barrier.Task;
            return await world.SendEventAsync("src", "E1", 400, Guid.Parse(taskAId), 1);
        });
        var completeB = Task.Run(async () =>
        {
            await barrier.Task;
            return await world.SendEventAsync("src", "E2", 400, Guid.Parse(taskBId), 1);
        });
        barrier.SetResult();
        await Task.WhenAll(completeA, completeB);

        // Both completions happened and both thresholds were crossed — the grant is single.
        var grants = await world.Resolve<ReadService>().ListOwnAchievementsAsync(world.Employee(400), null, 100, null, CancellationToken.None);
        var xGrants = grants.Items.Where(g => g.Code == "X").ToList();
        Assert.Single(xGrants);
        var progress = await world.Resolve<ReadService>().GetOwnCampaignProgressAsync(world.Employee(400), Guid.Parse(campaignId), CancellationToken.None);
        var streamPoints = progress.Streams.Single().Points;
        Assert.Equal(25, streamPoints); // 10 + 15: both completions counted exactly once
    }

    [Fact]
    public async Task e05_two_parallel_spends_one_posted_one_declined()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(500);
        var setup = await world.CreatePublishedCampaignAsync("E05", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 1000, "BASE");
        var system = await world.CreatePurchaseSystemAsync("SHOP", a);
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 500, a, 10, "top-up", "MA-1", CancellationToken.None);
        await world.CreateGrantAsync("shop-source", "Spend", resourceId: a, purchaseSystemId: system);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var employeeSpend = Task.Run(async () =>
        {
            await barrier.Task;
            return await world.Resolve<SpendsService>().SpendAsync(world.Employee(500), null, system, a, 7, "S-1", CancellationToken.None);
        });
        var serviceSpend = Task.Run(async () =>
        {
            await barrier.Task;
            return await world.Resolve<SpendsService>().SpendAsync(world.Service("shop-source"), 500, system, a, 7, "S-2", CancellationToken.None);
        });
        barrier.SetResult();
        var results = await Task.WhenAll(employeeSpend, serviceSpend);
        Assert.Contains(results, r => TestWorld.ParseJson(r.Body).GetProperty("result").GetString() == "Posted");
        Assert.Contains(results, r => TestWorld.ParseJson(r.Body).GetProperty("result").GetString() == "Declined");
        Assert.Equal(3, (await world.WalletAsync(500))["A"]);
    }

    [Fact]
    public async Task e07_two_parallel_reversals_exactly_one_return()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(600);
        var setup = await world.CreatePublishedCampaignAsync("E07", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 1000, "BASE");
        var system = await world.CreatePurchaseSystemAsync("SHOP", a);
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 600, a, 10, "top-up", "MA-1", CancellationToken.None);
        await world.CreateGrantAsync("shop-source", "Spend", resourceId: a, purchaseSystemId: system);

        var spend = await world.Resolve<SpendsService>().SpendAsync(world.Employee(600), null, system, a, 7, "S-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(spend.Body).GetProperty("result").GetString());
        var spendId = Guid.Parse(TestWorld.ParseJson(spend.Body).GetProperty("id").GetString()!);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminReversal = Task.Run(async () =>
        {
            await barrier.Task;
            return await world.Resolve<ReversalsService>().ReverseAsync(world.Admin, spendId, "wrong charge", "C-1", CancellationToken.None);
        });
        var serviceReversal = Task.Run(async () =>
        {
            await barrier.Task;
            return await world.Resolve<ReversalsService>().ReverseAsync(world.Service("shop-source"), spendId, "customer request", "C-2", CancellationToken.None);
        });
        barrier.SetResult();
        var results = await Task.WhenAll(adminReversal, serviceReversal);
        Assert.Contains(results, r => TestWorld.ParseJson(r.Body).GetProperty("result").GetString() == "Posted");
        var declinedCodes = results
            .Where(r => TestWorld.ParseJson(r.Body).GetProperty("result").GetString() == "Declined")
            .Select(r => TestWorld.ParseJson(r.Body).GetProperty("refusalCode").GetString())
            .ToList();
        Assert.All(declinedCodes, code => Assert.Equal("OriginalAlreadyReversed", code));
        Assert.Equal(10, (await world.WalletAsync(600))["A"]); // 3 + 7 returned once
    }
}
