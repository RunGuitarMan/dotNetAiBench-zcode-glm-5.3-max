using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Motiva.Application;
using Motiva.Application.Administration;
using Motiva.Application.Budgets;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Economy;
using Motiva.Application.Ports;
using Motiva.Application.Progress;
using Motiva.Application.Reads;
using Motiva.Infrastructure;
using Npgsql;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Deterministic test seam implementation: a named pause first signals that the
/// production path REACHED the checkpoint, then holds until the test releases it — there is no
/// silent auto-release, so a passing test proves the asserted ordering; the 120 s guard FAILS
/// the test instead of faking progress. Failures throw ImpedimentFailureException, rolling the
/// transaction back with no committed result.</summary>
public sealed class ScriptedImpediments : ITestImpediments
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(120);

    private readonly Dictionary<string, TaskCompletionSource> _checkpoints = new();
    private readonly Dictionary<string, TaskCompletionSource> _reached = new();
    private readonly HashSet<string> _failures = new();
    private readonly HashSet<string> _once = new();
    private readonly HashSet<string> _onceClaimed = new();

    public void PauseOn(string checkpoint)
    {
        _checkpoints[checkpoint] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reached[checkpoint] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Pauses only the FIRST arrival at the checkpoint; every other arrival passes
    /// through immediately — used to stop exactly one worker of a race without touching the
    /// others.</summary>
    public void PauseFirstOn(string checkpoint)
    {
        PauseOn(checkpoint);
        _once.Add(checkpoint);
    }

    public void FailOn(string checkpoint) => _failures.Add(checkpoint);

    public void Reset()
    {
        _checkpoints.Clear();
        _reached.Clear();
        _failures.Clear();
        _once.Clear();
        _onceClaimed.Clear();
    }

    public void Release(string checkpoint)
    {
        if (_checkpoints.TryGetValue(checkpoint, out var tcs))
        {
            tcs.TrySetResult();
        }
    }

    /// <summary>Resolves when the production path has reached the checkpoint; the test never
    /// guesses phases with sleeps.</summary>
    public async Task WaitReachedAsync(string checkpoint, CancellationToken ct = default)
    {
        if (!_reached.TryGetValue(checkpoint, out var tcs))
        {
            throw new InvalidOperationException("No pause registered for checkpoint " + checkpoint);
        }

        await await Task.WhenAny(tcs.Task, Task.Delay(Guard, ct));
        if (!tcs.Task.IsCompleted)
        {
            throw new TimeoutException("Checkpoint " + checkpoint + " was not reached within " + Guard);
        }
    }

    public async Task CheckpointAsync(string name, CancellationToken cancellationToken)
    {
        if (_failures.Contains(name))
        {
            throw new ImpedimentFailureException(name);
        }

        if (!_checkpoints.TryGetValue(name, out var tcs))
        {
            return;
        }

        if (_once.Contains(name) && !_onceClaimed.Add(name))
        {
            return; // a one-shot pause already holds its worker: everyone else passes through
        }

        _reached[name].TrySetResult();
        await await Task.WhenAny(tcs.Task, Task.Delay(Guard, cancellationToken));
        if (!tcs.Task.IsCompleted)
        {
            // Protective timeout: terminate a hanging test as FAILED — never proceed silently.
            throw new TimeoutException("Checkpoint " + name + " was paused but never released by the test.");
        }

        if (_failures.Contains(name))
        {
            // The failure was armed while the checkpoint was held: it fires on release.
            throw new ImpedimentFailureException(name);
        }
    }
}

/// <summary>Real infrastructure host for functional tests: PostgreSQL 17, Valkey 9 and S3 from
/// the compose stand; one database per collection, one company per test (tenant isolation).
/// Business arrange goes exclusively through application use cases (T08).</summary>
public sealed class MotivaTestHost : IAsyncLifetime
{
    private static readonly string BaseConnection = Environment.GetEnvironmentVariable("Motiva__PostgresConnectionString")
        ?? "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand";

    private readonly string _databaseName = "motiva_test_" + Guid.NewGuid().ToString("N")[..12];
    private string Connection => new NpgsqlConnectionStringBuilder(BaseConnection) { Database = _databaseName }.ConnectionString;

    public FakeTimeProvider Clock { get; private set; } = new(new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero));

    public ScriptedImpediments Impediments { get; } = new();

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(BaseConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE " + _databaseName, admin);
            await create.ExecuteNonQueryAsync();
        }

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Motiva.Infrastructure.Persistence.MotivaDbContext>()
            .UseNpgsql(Connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var context = new Motiva.Infrastructure.Persistence.MotivaDbContext(options))
        {
            await context.Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(BaseConnection);
        await admin.OpenAsync();
        await using var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '" + _databaseName + "'", admin);
        await terminate.ExecuteNonQueryAsync();
        await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + _databaseName, admin);
        await drop.ExecuteNonQueryAsync();
    }

    public ServiceProvider BuildServices(DateTimeOffset? clockStart = null)
    {
        // A fresh clock per world: tests move time freely without cross-test interference.
        Clock = new FakeTimeProvider(clockStart ?? new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMotivaApplication();
        services.AddMotivaInfrastructure(options =>
        {
            options.PostgresConnectionString = Connection;
            options.ValkeyEndpoint = Environment.GetEnvironmentVariable("Motiva__ValkeyEndpoint") ?? "localhost:6380";
            options.S3ServiceUrl = Environment.GetEnvironmentVariable("Motiva__S3ServiceUrl") ?? "http://localhost:9100";
            options.S3AccessKey = Environment.GetEnvironmentVariable("Motiva__S3AccessKey") ?? "motiva";
            options.S3SecretKey = Environment.GetEnvironmentVariable("Motiva__S3SecretKey") ?? "motiva-stand-secret";
            options.S3Bucket = "motiva-test-exports-" + _databaseName[^10..];
        });
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(Clock);
        services.RemoveAll<ITestImpediments>();
        services.AddSingleton<ITestImpediments>(Impediments);
        return services.BuildServiceProvider();
    }
}

/// <summary>Fluent arrange over validating use cases (stage-3 §3): every helper goes through
/// the same services the endpoints call; no DbContext/SQL in business arrange.</summary>
public sealed class TestWorld
{
    public TestWorld(IServiceProvider services, Guid companyId)
    {
        Services = services;
        CompanyId = companyId;
        Admin = new ActorContext(companyId, ActorType.User, 1, "admin", IsAdmin: true);
    }

    private IServiceProvider Services { get; }

    public Guid CompanyId { get; }

    public ActorContext Admin { get; }

    public ActorContext Employee(int masterId)
        => new(CompanyId, ActorType.User, masterId, "employee-" + masterId, IsAdmin: false);

    public ActorContext Service(string subject)
        => new(CompanyId, ActorType.Service, null, subject, IsAdmin: false);

    private readonly List<IServiceScope> _scopes = [];

    /// <summary>A fresh scope per resolution: parallel use cases never share a DbContext (T06).</summary>
    public T Resolve<T>()
        where T : notnull
    {
        var scope = Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public async Task<int> CreateEmployeeAsync(int masterId, params string[] tags)
    {
        await Resolve<EmployeesService>().CreateAsync(Admin, masterId, tags, "emp-" + masterId + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        return masterId;
    }

    public async Task<Guid> CreateResourceAsync(string code)
    {
        var response = await Resolve<ResourcesService>().CreateAsync(Admin, code, code + " name", "res-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        return Guid.Parse(ParseJson(response.Body).GetProperty("id").GetString()!);
    }

    public async Task<Guid> CreateAchievementAsync(string code)
    {
        var response = await Resolve<AchievementsService>().CreateAsync(Admin, code, code + " name", null, "ach-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        return Guid.Parse(ParseJson(response.Body).GetProperty("id").GetString()!);
    }

    public static System.Text.Json.JsonElement ParseJson(string json)
        => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    public async Task<Guid> CreatePurchaseSystemAsync(string code, params Guid[] resources)
    {
        var response = await Resolve<PurchaseSystemsService>().CreateAsync(Admin, code, code + " name", resources, "ps-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        return Guid.Parse(ParseJson(response.Body).GetProperty("id").GetString()!);
    }

    public async Task<Guid> CreateGrantAsync(string subject, string kind, Guid? campaignId = null, Guid? resourceId = null, Guid? purchaseSystemId = null)
    {
        var response = await Resolve<IntegrationGrantsService>().CreateAsync(Admin, subject, Enum.Parse<GrantKind>(kind), campaignId, resourceId, purchaseSystemId, "grant-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        return Guid.Parse(ParseJson(response.Body).GetProperty("id").GetString()!);
    }

    public async Task<CampaignSetup> CreatePublishedCampaignAsync(
        string code,
        (string Code, long Goal, string Period, long StreamPoints, (Guid Resource, long Amount)[]? Rewards)? task = null,
        IReadOnlyList<string>? employeeTags = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        IReadOnlyList<(long Threshold, Guid Achievement)>? milestones = null,
        (DateTimeOffset StartsAt, DateTimeOffset EndsAt)? challenge = null,
        AudienceDto? audience = null)
    {
        var campaigns = Resolve<CampaignsService>();
        var content = Resolve<CampaignContentService>();
        var start = startsAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = endsAt ?? new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero);
        var create = await campaigns.CreateAsync(Admin, code, code + " name", null, Admin.MasterId!.Value, start, end, audience, "camp-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        var campaignId = Guid.Parse(ParseJson(create.Body).GetProperty("id").GetString()!);
        var version = 1;

        var taskSpec = task ?? (code + "-T1", 3L, "Month", 10L, null);
        var rewardIds = new List<Guid>();
        if (taskSpec.Rewards is { Length: > 0 } rewards)
        {
            rewardIds.AddRange(rewards.Select(r => r.Resource).Distinct());
        }

        var resourcesPut = await campaigns.PutResourcesAsync(Admin, campaignId, ETags.Format(version), rewardIds, CancellationToken.None);
        version++;
        var stream = await content.CreateStreamAsync(Admin, campaignId, "S1", "Stream", "stream-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        version++;
        var rewardDtos = taskSpec.Rewards?.Select(r => new Motiva.Application.Dto.RewardItemDto(r.Resource, r.Amount)).ToArray() ?? [];
        var taskResponse = await content.CreateTaskAsync(
            Admin, Guid.Parse(ParseJson(stream.Body).GetProperty("id").GetString()!), taskSpec.Code, taskSpec.Code + " name", null,
            taskSpec.Goal, Enum.Parse<Motiva.Domain.Periods.PeriodKind>(taskSpec.Period), taskSpec.StreamPoints, rewardDtos, null,
            "task-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        version++;
        var taskId = Guid.Parse(ParseJson(taskResponse.Body).GetProperty("id").GetString()!);
        var streamId = Guid.Parse(ParseJson(stream.Body).GetProperty("id").GetString()!);
        Guid? challengeId = null;
        if (milestones is not null)
        {
            foreach (var milestone in milestones)
            {
                await content.CreateMilestoneAsync(Admin, streamId, milestone.Threshold, milestone.Achievement, "ms-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
                version++;
            }
        }

        if (challenge is { } interval)
        {
            var challengeResponse = await content.CreateChallengeAsync(Admin, campaignId, streamId, interval.StartsAt, interval.EndsAt, "ch-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
            version++;
            challengeId = Guid.Parse(ParseJson(challengeResponse.Body).GetProperty("id").GetString()!);
        }

        var patch = await campaigns.PatchAsync(Admin, campaignId, ETags.Format(version), null, null, null, CampaignStatus.Published, CancellationToken.None);
        Assert.Equal(200, patch.Status);
        return new CampaignSetup(campaignId, streamId, taskId, challengeId);
    }

    public async Task<Guid> PublishWithChallengeAsync(string code, DateTimeOffset challengeStart, DateTimeOffset challengeEnd, (string Code, long Goal, string Period, long StreamPoints, (Guid Resource, long Amount)[]? Rewards)? task = null)
    {
        var campaigns = Resolve<CampaignsService>();
        var content = Resolve<CampaignContentService>();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-2);
        var create = await campaigns.CreateAsync(Admin, code, code + " name", null, 1, start, end, null, "cc-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        var campaignId = Guid.Parse(ParseJson(create.Body).GetProperty("id").GetString()!);
        var version = 1;
        var taskSpec = task ?? (code + "-T1", 3L, "Day", 10L, null);
        var rewardIds = taskSpec.Rewards?.Select(r => r.Resource).Distinct().ToList() ?? [];
        if (rewardIds.Count > 0)
        {
            await campaigns.PutResourcesAsync(Admin, campaignId, ETags.Format(version), rewardIds, CancellationToken.None);
            version++;
        }

        var stream = await content.CreateStreamAsync(Admin, campaignId, "S1", "Stream", "scs-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        version++;
        var streamId = Guid.Parse(ParseJson(stream.Body).GetProperty("id").GetString()!);
        var rewardDtos = taskSpec.Rewards?.Select(r => new Motiva.Application.Dto.RewardItemDto(r.Resource, r.Amount)).ToArray() ?? [];
        var taskResponse = await content.CreateTaskAsync(Admin, streamId, taskSpec.Code, taskSpec.Code, null, taskSpec.Goal,
            Enum.Parse<Motiva.Domain.Periods.PeriodKind>(taskSpec.Period), taskSpec.StreamPoints, rewardDtos, null,
            "sct-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        version++;
        var taskId = Guid.Parse(ParseJson(taskResponse.Body).GetProperty("id").GetString()!);
        var challenge = await content.CreateChallengeAsync(Admin, campaignId, streamId, challengeStart, challengeEnd, "ch-" + code + "-" + CompanyId.ToString("N")[..6], CancellationToken.None);
        version++;
        var challengeId = Guid.Parse(ParseJson(challenge.Body).GetProperty("id").GetString()!);
        var patch = await campaigns.PatchAsync(Admin, campaignId, ETags.Format(version), null, null, null, CampaignStatus.Published, CancellationToken.None);
        Assert.Equal(200, patch.Status);
        return challengeId;
    }

    public async Task AllocateAsync(Guid campaignId, Guid resourceId, long amount, string number)
    {
        var response = await Resolve<BudgetService>().AllocateAsync(Admin, campaignId, resourceId, amount, null, number, CancellationToken.None);
        Assert.Equal(201, response.Status);
    }

    public Task<Motiva.Application.Dto.CommandResponse> SendEventAsync(string subject, string eventNumber, int masterId, Guid taskId, long delta)
        => Resolve<ProgressEventsService>().PostAsync(Service(subject), eventNumber, masterId, taskId, delta, CancellationToken.None);

    public async Task<Dictionary<string, long>> WalletAsync(int masterId)
    {
        var wallet = await Resolve<ReadService>().GetOwnWalletAsync(Employee(masterId), CancellationToken.None);
        return wallet.Balances.ToDictionary(b => b.ResourceCode, b => b.Balance);
    }

    public async Task<long> BudgetAvailableAsync(Guid campaignId, Guid resourceId)
    {
        var page = await Resolve<BudgetService>().ListBudgetsAsync(Admin, campaignId, 100, null, CancellationToken.None);
        var budget = page.Items.Single(b => b.ResourceId == resourceId);
        return budget.Available;
    }

    public sealed record CampaignSetup(Guid CampaignId, Guid StreamId, Guid TaskId, Guid? ChallengeId = null);
}
