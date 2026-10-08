using Motiva.Application.Administration;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Economy;
using Motiva.Application.Exports;
using Motiva.Application.Ports;
using Motiva.Application.Progress;
using Motiva.Application.Reads;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>R1 regression tests for the review findings: current rights on every access
/// (D01–D04), audience on details (D05), archive lifecycle (D06), declined reward operation
/// (D17), idempotency essentials (D08), outbox reclaim (D10) and the cleanup race (D11).</summary>
[Collection("functional")]
public sealed class AccessRightsR1Tests(MotivaFunctionalFixture fixture)
{
    [Fact]
    public async Task d01_blocked_admin_cannot_write_or_read()
    {
        var world = await fixture.NewWorldAsync();
        var employees = world.Resolve<EmployeesService>();

        // Block the administrator's own profile.
        var profile = await employees.GetAsync(world.Admin, 1, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 1, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        // A blocked initiator gets 403 for writes AND reads, without any effect (B05.1).
        var write = await Assert.ThrowsAsync<MotivaException>(
            () => world.Resolve<ResourcesService>().CreateAsync(world.Admin, "BLK", "x", "k", CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzEmployeeNotActive, write.Code);
        var read = await Assert.ThrowsAsync<MotivaException>(
            () => world.Resolve<ResourcesService>().ListAsync(world.Admin, null, 10, null, CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzEmployeeNotActive, read.Code);
    }

    [Fact]
    public async Task d01_blocked_user_cannot_read_own_history()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(950);
        var setup = await world.CreatePublishedCampaignAsync("BRD", ("T", 5, "Day", 1, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        await world.SendEventAsync("src", "B1", 950, setup.TaskId, 1);

        var employees = world.Resolve<EmployeesService>();
        var profile = await employees.GetAsync(world.Admin, 950, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 950, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        await Assert.ThrowsAsync<MotivaException>(
            () => world.Resolve<ProgressEventsService>().ListOwnAsync(world.Employee(950), 10, null, null, null, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<MotivaException>(
            () => world.Resolve<ReadService>().GetOwnWalletAsync(world.Employee(950), CancellationToken.None));
        await Assert.ThrowsAsync<MotivaException>(
            () => world.Resolve<ReadService>().ListOwnAchievementsAsync(world.Employee(950), null, 10, null, CancellationToken.None));

        // The history itself survives (B05.3): the events remain in the store.
        var adminHistory = await world.Resolve<ProgressEventsService>().ListCompanyAsync(world.Admin, 10, null, null, null, CancellationToken.None);
        Assert.Single(adminHistory.Items);
    }

    [Fact]
    public async Task d03_export_scope_requires_current_admin_role()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(960);
        var a = await world.CreateResourceAsync("A");
        var setup = await world.CreatePublishedCampaignAsync("EXP3", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        await world.SendEventAsync("src", "E1", 960, setup.TaskId, 1);

        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var order = await exports.CreateAsync(world.Admin, ExportScope.Company, null, a, from, to, "x1", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);
        var state = await exports.GetAsync(world.Admin, exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Ready, state.Status);

        // Same masterId, Admin role revoked: the Company scope closes (B37.1 current rights).
        var plainUser = world.Employee(1);
        var denied = await Assert.ThrowsAsync<MotivaException>(
            () => exports.CreateDownloadLinkAsync(plainUser, exportId, "k1", CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzForbidden, denied.Code);

        // A different admin of the same company never gets the resource: requester-only.
        var order2 = await exports.CreateAsync(world.Admin, ExportScope.Own, null, a, from, to, "x2", CancellationToken.None);
        var export2 = Guid.Parse(TestWorld.ParseJson(order2.Body).GetProperty("id").GetString()!);
        var anotherAdmin = new ActorContext(world.CompanyId, ActorType.User, 999, "other-admin", IsAdmin: true);
        await world.CreateEmployeeAsync(999);
        await Assert.ThrowsAsync<MotivaException>(
            () => exports.GetAsync(anotherAdmin, export2, CancellationToken.None));
        await Assert.ThrowsAsync<MotivaException>(
            () => exports.CreateDownloadLinkAsync(anotherAdmin, export2, "k2", CancellationToken.None));
    }

    [Fact]
    public async Task d04_former_owner_gets_403_before_replay()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(970);
        await world.CreateEmployeeAsync(971);
        var setup = await world.CreatePublishedCampaignAsync("OWNR", ("T", 5, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");

        // 971 is the owner (a plain employee); the award is made by 971.
        var campaigns = world.Resolve<CampaignsService>();
        var appointed = await campaigns.GetAsync(world.Admin, setup.CampaignId, CancellationToken.None);
        await campaigns.PutOwnerAsync(world.Admin, setup.CampaignId, ETags.Format(appointed.Version), 971, CancellationToken.None);
        var award = await world.Resolve<ManualAwardsService>().AwardAsync(world.Employee(971), setup.CampaignId, 970, a, 5, "r", "MA-1", CancellationToken.None);
        Assert.Equal(201, award.Status);

        // Ownership moves to 970: the replay of the former owner is refused BEFORE the number (§3.0).
        var current = await campaigns.GetAsync(world.Admin, setup.CampaignId, CancellationToken.None);
        await campaigns.PutOwnerAsync(world.Admin, setup.CampaignId, ETags.Format(current.Version), 970, CancellationToken.None);
        var replay = await Assert.ThrowsAsync<MotivaException>(
            () => world.Resolve<ManualAwardsService>().AwardAsync(world.Employee(971), setup.CampaignId, 970, a, 5, "r", "MA-1", CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzForbidden, replay.Code);

        // The new owner's replay right works with a NEW number (same number belongs to the old initiator scope).
        var fresh = await world.Resolve<ManualAwardsService>().AwardAsync(world.Employee(970), setup.CampaignId, 970, a, 5, "r", "MA-2", CancellationToken.None);
        Assert.Equal(201, fresh.Status);
    }

    [Fact]
    public async Task d05_task_details_outside_audience_are_forbidden()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(980); // no vip tag
        await world.CreateEmployeeAsync(981, "vip");
        var a = await world.CreateResourceAsync("A");
        // Campaign restricted to vip; the task itself unrestricted — campaign audience governs.
        var campaigns = world.Resolve<CampaignsService>();
        var content = world.Resolve<CampaignContentService>();
        var create = await campaigns.CreateAsync(world.Admin, "VIP", "n", null, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero),
            new AudienceDto(new List<string> { "vip" }, new List<string>(), new List<string>()), "c1", CancellationToken.None);
        var campaignId = Guid.Parse(TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!);
        await campaigns.PutResourcesAsync(world.Admin, campaignId, ETags.Format(1), new[] { a }, CancellationToken.None);
        var stream = await content.CreateStreamAsync(world.Admin, campaignId, "S", "s", "s1", CancellationToken.None);
        var streamId = Guid.Parse(TestWorld.ParseJson(stream.Body).GetProperty("id").GetString()!);
        var task = await content.CreateTaskAsync(world.Admin, streamId, "T", "t", null, 5, Motiva.Domain.Periods.PeriodKind.Day, 0, [], null, "t1", CancellationToken.None);
        var taskId = Guid.Parse(TestWorld.ParseJson(task.Body).GetProperty("id").GetString()!);
        var current = await campaigns.GetAsync(world.Admin, campaignId, CancellationToken.None);
        await campaigns.PatchAsync(world.Admin, campaignId, ETags.Format(current.Version), null, null, null, CampaignStatus.Published, CancellationToken.None);

        // Details are unavailable outside the audience; the audience member reads them (B11).
        await Assert.ThrowsAsync<MotivaException>(() => content.GetTaskAsync(world.Employee(980), taskId, CancellationToken.None));
        await Assert.ThrowsAsync<MotivaException>(() => content.GetStreamAsync(world.Employee(980), streamId, CancellationToken.None));
        var visible = await content.GetTaskAsync(world.Employee(981), taskId, CancellationToken.None);
        Assert.Equal("T", visible.Code);
    }

    [Fact]
    public async Task d06_archive_is_irreversible_and_rejected_in_new_settings()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        var resources = world.Resolve<ResourcesService>();

        // Archived resource cannot enter purchase-system settings (B07.2).
        var current = await resources.GetAsync(world.Admin, a, CancellationToken.None);
        await resources.PatchAsync(world.Admin, a, ETags.Format(current.Version), null, ResourceStatus.Archived, CancellationToken.None);
        await Assert.ThrowsAsync<MotivaException>(() => world.CreatePurchaseSystemAsync("SHOPX", a));

        // Task archive is irreversible: no unarchive, no other change (§4.5).
        var b = await world.CreateResourceAsync("B");
        await world.CreateEmployeeAsync(990);
        var setup = await world.CreatePublishedCampaignAsync("ARCH", ("T", 3, "Day", 1, new[] { (b, 1L) }));
        var content = world.Resolve<CampaignContentService>();
        var archived = await content.PatchTaskAsync(world.Admin, setup.TaskId, ETags.Format(1), null, null, null, null, null, null, null, ContentStatus.Archived, CancellationToken.None);
        Assert.Equal(200, archived.Status);
        var task = await content.GetTaskAsync(world.Admin, setup.TaskId, CancellationToken.None);
        await Assert.ThrowsAsync<MotivaException>(
            () => content.PatchTaskAsync(world.Admin, setup.TaskId, ETags.Format(task.Version), "renamed", null, null, null, null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task d17_declined_reward_is_a_saved_operation()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        var b = await world.CreateResourceAsync("B");
        await world.CreateEmployeeAsync(991);
        var setup = await world.CreatePublishedCampaignAsync("D17", ("T", 5, "Month", 3, new[] { (a, 8L), (b, 5L) }));
        await world.AllocateAsync(setup.CampaignId, a, 12, "A1");
        await world.AllocateAsync(setup.CampaignId, b, 4, "B1");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var result = await world.SendEventAsync("src", "E1", 991, setup.TaskId, 5);
        var reward = TestWorld.ParseJson(result.Body).GetProperty("completion").GetProperty("reward");
        Assert.Equal("DeclinedInsufficientBudget", reward.GetProperty("outcome").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, reward.GetProperty("operationId").ValueKind);

        // The decline is visible in the operation history with both attempted positions (T04, stage-2 §3.5).
        var operations = await world.Resolve<ReadService>().ListOwnOperationsAsync(
            world.Employee(991), 100, null, null, null, a, OperationKind.TaskReward, OperationResult.Declined, CancellationToken.None);
        var declined = operations.Items.Single(o => o.Kind == OperationKind.TaskReward);
        Assert.Equal(OperationResult.Declined, declined.Result);
        Assert.Equal(RefusalCode.InsufficientBudget, declined.RefusalCode);
        Assert.Equal(2, declined.Items.Count);
    }

    [Fact]
    public async Task d08_changed_essential_data_with_same_key_conflicts()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(992);
        var campaigns = world.Resolve<CampaignsService>();
        var content = world.Resolve<CampaignContentService>();
        var create = await campaigns.CreateAsync(world.Admin, "IDEM", "n", null, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero), null, "c1", CancellationToken.None);
        var campaignId = Guid.Parse(TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!);
        await campaigns.PutResourcesAsync(world.Admin, campaignId, ETags.Format(1), new[] { a }, CancellationToken.None);

        var key = "same-key";
        var first = await content.CreateStreamAsync(world.Admin, campaignId, "S1", "First", key, CancellationToken.None);
        Assert.Equal(201, first.Status);
        // Same key with a different name: conflict, not a replay (T05).
        var second = await Assert.ThrowsAsync<MotivaException>(
            () => content.CreateStreamAsync(world.Admin, campaignId, "S1", "Second", key, CancellationToken.None));
        Assert.Equal(ErrorCode.ConflictIdempotencyData, second.Code);
    }

    [Fact]
    public async Task d10_running_job_with_expired_lease_is_reclaimed()
    {
        var world = await fixture.NewWorldAsync();
        var jobs = world.Resolve<IBackgroundJobs>();
        var jobId = await jobs.EnqueueAsync("noop", "{}", fixture.Host.Clock.GetUtcNow().AddSeconds(-1), CancellationToken.None);

        // Simulate a dead worker: a Running job whose lease has expired (crash before Complete).
        await jobs.ClaimAsync("dead-worker", 10, fixture.Host.Clock.GetUtcNow(), CancellationToken.None);
        var claimed = await jobs.ClaimAsync("probe", 10, fixture.Host.Clock.GetUtcNow().AddSeconds(-60), CancellationToken.None);
        Assert.DoesNotContain(claimed, j => j.Id == jobId); // claim is in the past: lease still live

        // After the lease expires another worker reclaims it (T06).
        var reclaimed = await jobs.ClaimAsync("alive-worker", 10, fixture.Host.Clock.GetUtcNow().AddSeconds(120), CancellationToken.None);
        Assert.Contains(reclaimed, j => j.Id == jobId);
        await jobs.CompleteAsync(jobId, CancellationToken.None);
    }

    [Fact]
    public async Task d11_cleanup_waits_for_live_lease_and_late_upload_removes_itself()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(993);
        var a = await world.CreateResourceAsync("A");
        var setup = await world.CreatePublishedCampaignAsync("CLNR", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A1");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        await world.SendEventAsync("src", "E1", 993, setup.TaskId, 1);

        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var order = await exports.CreateAsync(world.Employee(993), ExportScope.Own, null, a, from, to, "k1", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);

        // Worker A starts forming and pauses after the snapshot; delete arrives meanwhile.
        fixture.Host.Impediments.PauseOn(Checkpoints.ExportBeforeSnapshotCommit);
        var workerA = Task.Run(() => world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None));
        await Task.Delay(300);
        await exports.DeleteAsync(world.Employee(993), exportId, CancellationToken.None);

        // Sweep during the race (the row is already Deleted) — then the late worker resumes.
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);
        fixture.Host.Impediments.Release(Checkpoints.ExportBeforeSnapshotCommit);
        await workerA;
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);

        // Invariants of the race: never Ready again, no bytes left behind, no pending intent (§3.6).
        var final = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Deleted, final!.Status);
        Assert.False(final.CleanupIntent);
        var storage = world.Resolve<IFileStorage>();
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 1), CancellationToken.None));
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 2), CancellationToken.None));
    }
}
