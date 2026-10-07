using Motiva.Application.Administration;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Competitions;
using Motiva.Application.Dto;
using Motiva.Application.Economy;
using Motiva.Application.Ports;
using Motiva.Application.Progress;
using Motiva.Application.Reads;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>The progress vertical (T3-09): SCN-01/SCN-02 with exact numbers from the approved
/// tables, E01–E03, E09, E10, replay identity and the atomicity seam.</summary>
[Collection("functional")]
public sealed class VerticalTests(MotivaFunctionalFixture fixture)
{
    [Fact]
    public async Task scn01_full_vertical_with_exact_numbers()
    {
        var world = await fixture.NewWorldAsync();
        var resourceA = await world.CreateResourceAsync("STAR");
        var resourceB = await world.CreateResourceAsync("LEARN");
        var achievement = await world.CreateAchievementAsync("X");
        await world.CreateEmployeeAsync(123);
        var campaigns = world.Resolve<CampaignsService>();
        var content = world.Resolve<CampaignContentService>();

        // Campaign 01.03–30.06, monthly task goal 3, points 10, reward 10 STAR + 2 LEARN.
        var create = await campaigns.CreateAsync(world.Admin, "C1", "Campaign", null, 1,
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 30, 21, 0, 0, TimeSpan.Zero), null, "idem-c1", CancellationToken.None);
        var campaignId = Guid.Parse(TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!);
        await campaigns.PutResourcesAsync(world.Admin, campaignId, ETags.Format(1), new[] { resourceA, resourceB }, CancellationToken.None);
        var stream = await content.CreateStreamAsync(world.Admin, campaignId, "S1", "Stream", "idem-s1", CancellationToken.None);
        var streamId = Guid.Parse(TestWorld.ParseJson(stream.Body).GetProperty("id").GetString()!);
        var task = await content.CreateTaskAsync(world.Admin, streamId, "T1", "Task", null, 3,
            Motiva.Domain.Periods.PeriodKind.Month, 10,
            new[] { new RewardItemDto(resourceA, 10), new RewardItemDto(resourceB, 2) }, null, "idem-t1", CancellationToken.None);
        var taskId = Guid.Parse(TestWorld.ParseJson(task.Body).GetProperty("id").GetString()!);
        await content.CreateMilestoneAsync(world.Admin, streamId, 10, achievement, "idem-m1", CancellationToken.None);
        var challenge = await content.CreateChallengeAsync(world.Admin, campaignId, streamId,
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 8, 0, 0, 0, TimeSpan.Zero), "idem-ch1", CancellationToken.None);
        var challengeId = Guid.Parse(TestWorld.ParseJson(challenge.Body).GetProperty("id").GetString()!);
        var publish = await campaigns.PatchAsync(world.Admin, campaignId, ETags.Format(6), null, null, null, CampaignStatus.Published, CancellationToken.None);
        Assert.Equal(200, publish.Status);

        await world.AllocateAsync(campaignId, resourceA, 100, "B-A");
        await world.AllocateAsync(campaignId, resourceB, 50, "B-B");
        await world.CreateGrantAsync("progress-source", "Progress", campaignId);

        // When: I1 sends event E-1 (U=123, T1, +3), accepted 02.04.2026 10:00.
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero));
        var result = await world.SendEventAsync("progress-source", "E-1", 123, taskId, 3);
        Assert.Equal(201, result.Status);
        var body = TestWorld.ParseJson(result.Body);
        Assert.Equal("Accepted", body.GetProperty("result").GetString());
        Assert.Equal(3, body.GetProperty("creditedDelta").GetInt64());
        Assert.Equal(3, body.GetProperty("transmittedDelta").GetInt64());
        var completion = body.GetProperty("completion");
        Assert.Equal(10, completion.GetProperty("streamPointsAdded").GetInt64());
        Assert.Equal("Granted", completion.GetProperty("reward").GetProperty("outcome").GetString());

        // Then: budgets 90/48, wallet 10/2, achievement X once, challenge score 3.
        Assert.Equal(90, await world.BudgetAvailableAsync(campaignId, resourceA));
        Assert.Equal(48, await world.BudgetAvailableAsync(campaignId, resourceB));
        var wallet = await world.WalletAsync(123);
        Assert.Equal(10, wallet["STAR"]);
        Assert.Equal(2, wallet["LEARN"]);
        var grants = await world.Resolve<ReadService>().ListOwnAchievementsAsync(world.Employee(123), null, 100, null, CancellationToken.None);
        var achievementGrants = grants.Items.Where(g => g.Code == "X").ToList();
        Assert.Single(achievementGrants);
        Assert.Equal(2026, achievementGrants[0].Season);
        var leaderboard = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(123), challengeId, 10, CancellationToken.None);
        Assert.Single(leaderboard.Items);
        Assert.Equal(3, leaderboard.Items[0].Score);
        Assert.Equal(1, leaderboard.Items[0].Place);

        // Replay of E-1 returns the stored representation byte-identical (§3.0).
        var replay = await world.SendEventAsync("progress-source", "E-1", 123, taskId, 3);
        Assert.Equal(result.Body, replay.Body);
    }

    [Fact]
    public async Task e01_two_resources_both_credited_both_budgets_zero()
    {
        var world = await fixture.NewWorldAsync();
        var resourceA = await world.CreateResourceAsync("A");
        var resourceB = await world.CreateResourceAsync("B");
        await world.CreateEmployeeAsync(7);
        var setup = await world.CreatePublishedCampaignAsync("E01", ("T", 1, "Month", 5, new[] { (resourceA, 10L), (resourceB, 2L) }));
        await world.AllocateAsync(setup.CampaignId, resourceA, 10, "A1");
        await world.AllocateAsync(setup.CampaignId, resourceB, 2, "A2");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var result = await world.SendEventAsync("src", "E1", 7, setup.TaskId, 1);
        var body = TestWorld.ParseJson(result.Body);
        Assert.Equal("Granted", body.GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString());
        Assert.Equal(0, await world.BudgetAvailableAsync(setup.CampaignId, resourceA));
        Assert.Equal(0, await world.BudgetAvailableAsync(setup.CampaignId, resourceB));
        var wallet = await world.WalletAsync(7);
        Assert.Equal(10, wallet["A"]);
        Assert.Equal(2, wallet["B"]);
        var operations = await world.Resolve<ReadService>().ListOwnOperationsAsync(
            world.Employee(7), 100, null, null, null, resourceA, null, null, CancellationToken.None);
        Assert.Single(operations.Items);
        Assert.Equal(OperationKind.TaskReward, operations.Items[0].Kind);
        Assert.Equal(2, operations.Items[0].Items.Count);
    }

    [Fact]
    public async Task e02_shortage_of_one_position_declines_whole_package_and_stays_final()
    {
        var world = await fixture.NewWorldAsync();
        var resourceA = await world.CreateResourceAsync("A");
        var resourceB = await world.CreateResourceAsync("B");
        await world.CreateEmployeeAsync(7);
        var setup = await world.CreatePublishedCampaignAsync("E02", ("T", 5, "Month", 3, new[] { (resourceA, 8L), (resourceB, 5L) }));
        await world.AllocateAsync(setup.CampaignId, resourceA, 12, "A1");
        await world.AllocateAsync(setup.CampaignId, resourceB, 4, "A2");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var result = await world.SendEventAsync("src", "E1", 7, setup.TaskId, 5);
        var body = TestWorld.ParseJson(result.Body);
        Assert.Equal("DeclinedInsufficientBudget", body.GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString());
        Assert.Equal(12, await world.BudgetAvailableAsync(setup.CampaignId, resourceA));
        Assert.Equal(4, await world.BudgetAvailableAsync(setup.CampaignId, resourceB));
        var wallet = await world.WalletAsync(7);
        Assert.Equal(0, wallet["A"]);
        Assert.Equal(0, wallet["B"]);
        // Completion and points still recorded (the task defines 3 stream points).
        Assert.Equal(3, body.GetProperty("completion").GetProperty("streamPointsAdded").GetInt64());

        // Top up B and replay the same number: the saved decision is final (B20.3, B16.2).
        await world.AllocateAsync(setup.CampaignId, resourceB, 20, "A3");
        var replay = await world.SendEventAsync("src", "E1", 7, setup.TaskId, 5);
        Assert.Equal(result.Body, replay.Body);
        Assert.Equal(0, (await world.WalletAsync(7))["A"]);
    }

    [Fact]
    public async Task e03_min_rule_and_post_goal_event()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(9);
        var setup = await world.CreatePublishedCampaignAsync("E03", ("T", 3, "Month", 2, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var first = await world.SendEventAsync("src", "E1", 9, setup.TaskId, 2);
        var firstBody = TestWorld.ParseJson(first.Body);
        Assert.Equal(2, firstBody.GetProperty("creditedDelta").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, firstBody.GetProperty("completion").ValueKind);

        var second = await world.SendEventAsync("src", "E2", 9, setup.TaskId, 5);
        var secondBody = TestWorld.ParseJson(second.Body);
        Assert.Equal(1, secondBody.GetProperty("creditedDelta").GetInt64()); // min(3, 2+5) - 2
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, secondBody.GetProperty("completion").ValueKind);

        var third = await world.SendEventAsync("src", "E3", 9, setup.TaskId, 1);
        var thirdBody = TestWorld.ParseJson(third.Body);
        Assert.Equal(0, thirdBody.GetProperty("creditedDelta").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, thirdBody.GetProperty("completion").ValueKind);
    }

    [Fact]
    public async Task e09_challenge_counts_credited_not_transmitted()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(11);
        var resource = await world.CreateResourceAsync("A");
        var setup = await world.CreatePublishedCampaignAsync(
            "E09",
            ("T", 3, "Day", 10, new[] { (resource, 1L) }),
            challenge: (new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero)));
        await world.AllocateAsync(setup.CampaignId, resource, 10, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var result = await world.SendEventAsync("src", "E1", 11, setup.TaskId, 100);
        var body = TestWorld.ParseJson(result.Body);
        Assert.Equal(3, body.GetProperty("creditedDelta").GetInt64());
        var leaderboard = await world.Resolve<LeaderboardService>().GetPageAsync(world.Employee(11), setup.ChallengeId!.Value, 10, CancellationToken.None);
        Assert.Equal(3, leaderboard.Items.Single().Score);
    }

    [Fact]
    public async Task e10_two_milestones_two_achievements_in_one_completion()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(12);
        var x = await world.CreateAchievementAsync("X10");
        var y = await world.CreateAchievementAsync("Y20");
        var setup = await world.CreatePublishedCampaignAsync(
            "E10",
            ("T", 1, "Month", 20, null),
            milestones: new[] { (10L, x), (20L, y) });
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var result = await world.SendEventAsync("src", "E1", 12, setup.TaskId, 1);
        Assert.Equal("Accepted", TestWorld.ParseJson(result.Body).GetProperty("result").GetString());
        var grants = await world.Resolve<ReadService>().ListOwnAchievementsAsync(world.Employee(12), null, 100, null, CancellationToken.None);
        var codes = grants.Items.Select(g => g.Code).OrderBy(c => c).ToList();
        Assert.Equal(new List<string> { "X10", "Y20" }, codes);
    }

    [Fact]
    public async Task r_id_replay_returns_stored_body_with_completion_null_b16d()
    {
        // E1 gave partial progress (completion null); E2 completed the goal. Replay of E1
        // must return the stored body with completion still null (§3.0 counterexample).
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(21);
        var setup = await world.CreatePublishedCampaignAsync("RID", ("T", 3, "Month", 5, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var e1 = await world.SendEventAsync("src", "E1", 21, setup.TaskId, 1);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, TestWorld.ParseJson(e1.Body).GetProperty("completion").ValueKind);
        var e2 = await world.SendEventAsync("src", "E2", 21, setup.TaskId, 2);
        Assert.True(TestWorld.ParseJson(e2.Body).GetProperty("completion").ValueKind != System.Text.Json.JsonValueKind.Null);

        var replay = await world.SendEventAsync("src", "E1", 21, setup.TaskId, 1);
        Assert.Equal(e1.Body, replay.Body);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, TestWorld.ParseJson(replay.Body).GetProperty("completion").ValueKind);
    }

    [Fact]
    public async Task r_pr_resource_unavailability_outranks_budget_shortage_q02()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        var b = await world.CreateResourceAsync("B");
        var foreign = await world.CreateResourceAsync("FOREIGN");
        await world.CreateEmployeeAsync(22);
        var setup = await world.CreatePublishedCampaignAsync("RPR", ("T", 1, "Month", 1, new[] { (a, 5L), (b, 5L) }));
        await world.AllocateAsync(setup.CampaignId, a, 4, "A");
        await world.AllocateAsync(setup.CampaignId, b, 1, "B");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        // Archive A: unavailable in a non-empty package takes priority over any shortage.
        var resources = world.Resolve<ResourcesService>();
        var current = await resources.GetAsync(world.Admin, a, CancellationToken.None);
        await resources.PatchAsync(world.Admin, a, ETags.Format(current.Version), null, ResourceStatus.Archived, CancellationToken.None);

        var result = await world.SendEventAsync("src", "E1", 22, setup.TaskId, 1);
        var reward = TestWorld.ParseJson(result.Body).GetProperty("completion").GetProperty("reward");
        Assert.Equal("DeclinedResourceUnavailable", reward.GetProperty("outcome").GetString());

        // An unrelated archived resource must not affect other packages.
        var other = await world.CreateResourceAsync("OTHER");
        var currentOther = await resources.GetAsync(world.Admin, other, CancellationToken.None);
        await resources.PatchAsync(world.Admin, other, ETags.Format(currentOther.Version), null, ResourceStatus.Archived, CancellationToken.None);
        var setup2 = await world.CreatePublishedCampaignAsync("RPR2", ("T2", 1, "Month", 1, new[] { (b, 1L) }));
        await world.AllocateAsync(setup2.CampaignId, b, 1, "B2");
        await world.CreateGrantAsync("src2", "Progress", setup2.CampaignId);
        var ok = await world.SendEventAsync("src2", "E1", 22, setup2.TaskId, 1);
        Assert.Equal("Granted", TestWorld.ParseJson(ok.Body).GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString());

        // Empty package: NotProvided (B20.5).
        var setup3 = await world.CreatePublishedCampaignAsync("RPR3", ("T3", 1, "Month", 1, null));
        await world.CreateGrantAsync("src3", "Progress", setup3.CampaignId);
        var empty = await world.SendEventAsync("src3", "E1", 22, setup3.TaskId, 1);
        Assert.Equal("NotProvided", TestWorld.ParseJson(empty.Body).GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task technical_failure_before_commit_leaves_no_half_result_b17d()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(31);
        var setup = await world.CreatePublishedCampaignAsync("ATOMIC", ("T", 1, "Month", 10, new[] { (a, 10L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Impediments.FailOn(Checkpoints.ProgressBeforeCommit);
        await Assert.ThrowsAsync<ImpedimentFailureException>(
            () => world.SendEventAsync("src", "E1", 31, setup.TaskId, 1));

        // Nothing persisted: no event, no completion, no movements, no budget change; number free again.
        Assert.Equal(100, await world.BudgetAvailableAsync(setup.CampaignId, a));
        Assert.Equal(0, (await world.WalletAsync(31))["A"]);
        var events = await world.Resolve<ProgressEventsService>().ListOwnAsync(
            world.Employee(31), 100, null, null, null, null, null, CancellationToken.None);
        Assert.Empty(events.Items);
        fixture.Host.Impediments.Reset();
        var retry = await world.SendEventAsync("src", "E1", 31, setup.TaskId, 1);
        Assert.Equal("Accepted", TestWorld.ParseJson(retry.Body).GetProperty("result").GetString());
    }

    [Fact]
    public async Task replay_after_commit_with_lost_response_returns_saved_body_t314()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(32);
        var setup = await world.CreatePublishedCampaignAsync("LOST", ("T", 2, "Month", 5, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        fixture.Host.Impediments.PauseOn(Checkpoints.ProgressAfterCommitBeforeResponse);
        var sending = world.SendEventAsync("src", "E9", 32, setup.TaskId, 2);
        await Task.Delay(200);
        fixture.Host.Impediments.Release(Checkpoints.ProgressAfterCommitBeforeResponse);
        var original = await sending;

        var replay = await world.SendEventAsync("src", "E9", 32, setup.TaskId, 2);
        Assert.Equal(original.Body, replay.Body);
        Assert.Equal(201, replay.Status);
    }

    [Fact]
    public async Task number_conflict_with_changed_essential_data_b16c()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(33);
        await world.CreateEmployeeAsync(34);
        var setup = await world.CreatePublishedCampaignAsync("CONFLICT", ("T", 5, "Month", 0, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        await world.SendEventAsync("src", "E1", 33, setup.TaskId, 1);
        var ex = await Assert.ThrowsAsync<MotivaException>(() => world.SendEventAsync("src", "E1", 34, setup.TaskId, 1));
        Assert.Equal(ErrorCode.ConflictBusinessNumber, ex.Code);
        var ex2 = await Assert.ThrowsAsync<MotivaException>(() => world.SendEventAsync("src", "E1", 33, setup.TaskId, 2));
        Assert.Equal(ErrorCode.ConflictBusinessNumber, ex2.Code);
    }

    [Fact]
    public async Task rejected_event_occupies_number_and_replays_b16b_h0q08()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(35);
        var setup = await world.CreatePublishedCampaignAsync("REJ", ("T", 5, "Month", 0, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        // Audience mismatch: employee 36 does not exist yet -> blocked path is 404; use tags instead.
        var campaigns = world.Resolve<CampaignsService>();
        var resource = await world.CreateResourceAsync("R");
        // Rebuild with audience none:vip and employee without vip.
        var setupRestricted = await CreateRestrictedCampaignAsync(world, resource);

        var rejected = await world.SendEventAsync("src", "R1", 35, setupRestricted.TaskId, 1);
        var body = TestWorld.ParseJson(rejected.Body);
        Assert.Equal("Rejected", body.GetProperty("result").GetString());
        Assert.Equal("AudienceMismatch", body.GetProperty("rejectReason").GetString());

        // Replay returns the stored rejection.
        var replay = await world.SendEventAsync("src", "R1", 35, setupRestricted.TaskId, 1);
        Assert.Equal(rejected.Body, replay.Body);
    }

    private static async Task<TestWorld.CampaignSetup> CreateRestrictedCampaignAsync(TestWorld world, Guid resource)
    {
        // Campaign with none=vip audience: employee without the tag is outside the audience.
        var campaigns = world.Resolve<CampaignsService>();
        var content = world.Resolve<CampaignContentService>();
        var create = await campaigns.CreateAsync(world.Admin, "RESTR", "n", null, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero),
            new AudienceDto(new List<string> { "vip" }, new List<string>(), new List<string>()), "idem-restr", CancellationToken.None);
        var campaignId = Guid.Parse(TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!);
        var stream = await content.CreateStreamAsync(world.Admin, campaignId, "S", "s", "idem-sr", CancellationToken.None);
        var streamId = Guid.Parse(TestWorld.ParseJson(stream.Body).GetProperty("id").GetString()!);
        var task = await content.CreateTaskAsync(world.Admin, streamId, "T", "t", null, 5, Motiva.Domain.Periods.PeriodKind.Month, 0, [], null, "idem-tr", CancellationToken.None);
        var taskId = Guid.Parse(TestWorld.ParseJson(task.Body).GetProperty("id").GetString()!);
        await campaigns.PatchAsync(world.Admin, campaignId, ETags.Format(3), null, null, null, CampaignStatus.Published, CancellationToken.None);
        await world.CreateGrantAsync("src", "Progress", campaignId);
        return new TestWorld.CampaignSetup(campaignId, streamId, taskId);
    }
}
