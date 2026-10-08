using Motiva.Application.Administration;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Economy;
using Motiva.Application.Exports;
using Motiva.Application.Ports;
using Motiva.Application.Reads;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>R2 regression tests for the review findings of F: blocked-admin data leak (D01),
/// audience in task lists (D05), archived resources in new task settings (D06), atomic child
/// If-Match (D07), full campaign essentials + replay order (D08), employee replay Location
/// (D09) and the foreign-wallet spend guard (M5 of the review's semantic probes).</summary>
[Collection("functional")]
public sealed class R2RegressionTests(MotivaFunctionalFixture fixture)
{
    [Fact]
    public async Task d01_blocked_admin_cannot_read_foreign_operation_detail()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(7);
        var a = await world.CreateResourceAsync("A");
        var setup = await world.CreatePublishedCampaignAsync("D01D", ("T", 1, "Month", 0, new[] { (a, 5L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A1");
        var award = await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 7, a, 5, "manual", "MA-1", CancellationToken.None);
        var operationId = Guid.Parse(TestWorld.ParseJson(award.Body).GetProperty("id").GetString()!);

        // The admin's own profile is blocked; the SAME still-valid token identity must not read
        // the foreign financial operation anymore (B05.1 — the exact R1 leak trace).
        var employees = world.Resolve<EmployeesService>();
        var profile = await employees.GetAsync(world.Admin, 1, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 1, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        var reads = world.Resolve<ReadService>();
        var detail = await Assert.ThrowsAsync<MotivaException>(() => reads.GetOperationAsync(world.Admin, operationId, CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzEmployeeNotActive, detail.Code);
        var list = await Assert.ThrowsAsync<MotivaException>(
            () => reads.ListEmployeeOperationsAsync(world.Admin, 7, 10, null, null, null, null, null, CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzEmployeeNotActive, list.Code);

        // The operation itself survives for its owner (B05.3): no data loss, only lost access.
        var own = await reads.ListOwnOperationsAsync(world.Employee(7), 10, null, null, null, null, null, null, CancellationToken.None);
        Assert.Contains(own.Items, o => o.Id == operationId);
    }

    [Fact]
    public async Task d01_blocked_user_cannot_read_campaign_detail_or_catalog()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(940);
        var setup = await world.CreatePublishedCampaignAsync("D01C", ("T", 1, "Month", 0, null));
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);

        var employees = world.Resolve<EmployeesService>();
        var profile = await employees.GetAsync(world.Admin, 940, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 940, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        var campaigns = world.Resolve<CampaignsService>();
        var detail = await Assert.ThrowsAsync<MotivaException>(() => campaigns.GetAsync(world.Employee(940), setup.CampaignId, CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzEmployeeNotActive, detail.Code);
        var catalog = world.Resolve<CatalogService>();
        await Assert.ThrowsAsync<MotivaException>(
            () => catalog.ListCampaignsAsync(world.Employee(940), null, null, 10, null, CancellationToken.None));
        await Assert.ThrowsAsync<MotivaException>(
            () => catalog.ListResourcesAsync(world.Employee(940), null, 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task d05_task_list_excludes_out_of_audience_tasks_and_pages_losslessly()
    {
        var world = await fixture.NewWorldAsync();
        await world.CreateEmployeeAsync(930); // no vip
        await world.CreateEmployeeAsync(931, "vip");
        var content = world.Resolve<CampaignContentService>();
        var campaigns = world.Resolve<CampaignsService>();

        // Unrestricted campaign; TWO tasks in one stream: one unrestricted, one vip-only.
        var create = await campaigns.CreateAsync(world.Admin, "VIPL", "n", null, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero), null, "c1", CancellationToken.None);
        var campaignId = Guid.Parse(TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!);
        var stream = await content.CreateStreamAsync(world.Admin, campaignId, "S", "s", "s1", CancellationToken.None);
        var streamId = Guid.Parse(TestWorld.ParseJson(stream.Body).GetProperty("id").GetString()!);
        var open = await content.CreateTaskAsync(world.Admin, streamId, "OPEN", "open", null, 5,
            Motiva.Domain.Periods.PeriodKind.Day, 0, [], null, "t-open", CancellationToken.None);
        var openId = Guid.Parse(TestWorld.ParseJson(open.Body).GetProperty("id").GetString()!);
        var vipTask = await content.CreateTaskAsync(world.Admin, streamId, "VIP", "vip", null, 5,
            Motiva.Domain.Periods.PeriodKind.Day, 0, [], new AudienceDto(["vip"], [], []), "t-vip", CancellationToken.None);
        var vipId = Guid.Parse(TestWorld.ParseJson(vipTask.Body).GetProperty("id").GetString()!);
        var current = await campaigns.GetAsync(world.Admin, campaignId, CancellationToken.None);
        await campaigns.PatchAsync(world.Admin, campaignId, ETags.Format(current.Version), null, null, null, CampaignStatus.Published, CancellationToken.None);

        // Detail of the vip task is forbidden for 930 (R1 fix) — and so is the LIST: the page
        // for a non-vip reader must not contain it, while pagination still reaches every
        // visible task (page size 1 forces the cursor walk).
        await Assert.ThrowsAsync<MotivaException>(() => content.GetTaskAsync(world.Employee(930), vipId, CancellationToken.None));
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await content.ListTasksAsync(world.Employee(930), streamId, 1, cursor, CancellationToken.None);
            Assert.DoesNotContain(page.Items, t => t.Id == vipId);
            seen.AddRange(page.Items.Select(t => t.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal([openId], seen);

        // The vip reader sees both tasks through the same walk.
        var vipSeen = new List<Guid>();
        cursor = null;
        do
        {
            var page = await content.ListTasksAsync(world.Employee(931), streamId, 1, cursor, CancellationToken.None);
            vipSeen.AddRange(page.Items.Select(t => t.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Contains(openId, vipSeen);
        Assert.Contains(vipId, vipSeen);

        // The admin/owner view is unfiltered (§3.3).
        var adminPage = await content.ListTasksAsync(world.Admin, streamId, 10, null, CancellationToken.None);
        Assert.Equal(2, adminPage.Items.Count);
    }

    [Fact]
    public async Task d06_new_task_settings_reject_archived_resource()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        var campaigns = world.Resolve<CampaignsService>();
        var content = world.Resolve<CampaignContentService>();
        var resources = world.Resolve<ResourcesService>();

        // A DRAFT campaign already carries the resource; the resource is archived afterwards.
        var create = await campaigns.CreateAsync(world.Admin, "ARCHT", "n", null, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero), null, "c1", CancellationToken.None);
        var campaignId = Guid.Parse(TestWorld.ParseJson(create.Body).GetProperty("id").GetString()!);
        await campaigns.PutResourcesAsync(world.Admin, campaignId, ETags.Format(1), new[] { a }, CancellationToken.None);
        var stream = await content.CreateStreamAsync(world.Admin, campaignId, "S", "s", "s1", CancellationToken.None);
        var streamId = Guid.Parse(TestWorld.ParseJson(stream.Body).GetProperty("id").GetString()!);
        var current = await resources.GetAsync(world.Admin, a, CancellationToken.None);
        await resources.PatchAsync(world.Admin, a, ETags.Format(current.Version), null, ResourceStatus.Archived, CancellationToken.None);

        // Creating a NEW task rewarding the archived resource is closed (B07), even though the
        // resource is still part of the campaign set.
        var createRejected = await Assert.ThrowsAsync<MotivaException>(() => content.CreateTaskAsync(
            world.Admin, streamId, "T1", "t", null, 5, Motiva.Domain.Periods.PeriodKind.Day, 0,
            new[] { new Motiva.Application.Dto.RewardItemDto(a, 1L) }, null, "t1", CancellationToken.None));
        Assert.Equal(ErrorCode.ValidationFailed, createRejected.Code);

        // The same rule governs modification: patching an existing draft task's rewards to the
        // archived resource is rejected too.
        var task = await content.CreateTaskAsync(world.Admin, streamId, "T2", "t", null, 5,
            Motiva.Domain.Periods.PeriodKind.Day, 0, [], null, "t2", CancellationToken.None);
        var taskId = Guid.Parse(TestWorld.ParseJson(task.Body).GetProperty("id").GetString()!);
        await Assert.ThrowsAsync<MotivaException>(() => content.PatchTaskAsync(
            world.Admin, taskId, ETags.Format(1), null, null, null, null, null,
            new[] { new Motiva.Application.Dto.RewardItemDto(a, 1L) }, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task d07_concurrent_task_patch_with_one_if_match_yields_single_success()
    {
        var world = await fixture.NewWorldAsync();
        var setup = await world.CreatePublishedCampaignAsync("D07T", ("T", 3, "Day", 1, null));
        var content = world.Resolve<CampaignContentService>();
        var task = await content.GetTaskAsync(world.Admin, setup.TaskId, CancellationToken.None);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8)
            .Select(i => Task.Run(async () =>
            {
                await barrier.Task;
                try
                {
                    var response = await world.Resolve<CampaignContentService>().PatchTaskAsync(
                        world.Admin, setup.TaskId, ETags.Format(task.Version), "name-" + i, null, null, null, null, null, null, null, CancellationToken.None);
                    return (Status: response.Status, Failed: false);
                }
                catch (MotivaException ex)
                {
                    return (Status: 0, Failed: ex.Code == ErrorCode.PreconditionFailed);
                }
            }))
            .ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r.Status == 200)); // exactly one transition of v1
        Assert.Equal(7, results.Count(r => r.Failed));        // all others are 412 Preconditions
        // Confirmed by a FRESH scope (an untracked context) — not by the tracked arrange entity.
        var after = await world.Resolve<CampaignContentService>().GetTaskAsync(world.Admin, setup.TaskId, CancellationToken.None);
        Assert.Equal(2, after.Version);
        Assert.StartsWith("name-", after.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task d08_campaign_essentials_cover_description_and_window()
    {
        var world = await fixture.NewWorldAsync();
        var campaigns = world.Resolve<CampaignsService>();
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero);
        var key = "camp-key";
        var first = await campaigns.CreateAsync(world.Admin, "ESS", "first", null, 1, start, end, null, key, CancellationToken.None);
        Assert.Equal(201, first.Status);

        // Changed description with the same key: conflict, not a replay of the stored 201 (T05).
        var byDescription = await Assert.ThrowsAsync<MotivaException>(
            () => campaigns.CreateAsync(world.Admin, "ESS", "first", "changed", 1, start, end, null, key, CancellationToken.None));
        Assert.Equal(ErrorCode.ConflictIdempotencyData, byDescription.Code);

        // Changed startsAt (same season): conflict as well.
        var byStart = await Assert.ThrowsAsync<MotivaException>(
            () => campaigns.CreateAsync(world.Admin, "ESS", "first", null, 1, start.AddDays(5), end, null, key, CancellationToken.None));
        Assert.Equal(ErrorCode.ConflictIdempotencyData, byStart.Code);

        // An identical replay returns the stored result with the original body and Location.
        var replay = await campaigns.CreateAsync(world.Admin, "ESS", "first", null, 1, start, end, null, key, CancellationToken.None);
        Assert.Equal(201, replay.Status);
        Assert.Equal(first.Body, replay.Body);
        Assert.NotNull(replay.Location);
        Assert.Equal(first.Location, replay.Location);
    }

    [Fact]
    public async Task d08_committed_campaign_replay_survives_a_later_owner_block()
    {
        var world = await fixture.NewWorldAsync();
        var campaigns = world.Resolve<CampaignsService>();
        var employees = world.Resolve<EmployeesService>();
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero);

        // Owner 920 is active at commit time and blocked before the replay: the owner
        // PRECONDITION is re-evaluated only for NEW creates — the committed replay returns the
        // stored 201 (§3.0 order: rights → saved result → business preconditions).
        await world.CreateEmployeeAsync(920);
        var first = await campaigns.CreateAsync(world.Admin, "OWNB", "n", null, 920, start, end, null, "own-key", CancellationToken.None);
        Assert.Equal(201, first.Status);
        var profile = await employees.GetAsync(world.Admin, 920, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 920, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        var replay = await campaigns.CreateAsync(world.Admin, "OWNB", "n", null, 920, start, end, null, "own-key", CancellationToken.None);
        Assert.Equal(201, replay.Status);
        Assert.Equal(first.Body, replay.Body);

        // A NEW create with a blocked owner is still refused by the precondition.
        await Assert.ThrowsAsync<MotivaException>(
            () => campaigns.CreateAsync(world.Admin, "OWNB2", "n", null, 920, start, end, null, "own-key-2", CancellationToken.None));
    }

    [Fact]
    public async Task d09_employee_creation_replay_keeps_location_header()
    {
        var world = await fixture.NewWorldAsync();
        var employees = world.Resolve<EmployeesService>();
        const int masterId = 925;

        var first = await employees.CreateAsync(world.Admin, masterId, Array.Empty<string>(), "emp-key", CancellationToken.None);
        Assert.Equal(201, first.Status);
        Assert.Equal("/api/v1/employees/" + masterId, first.Location);

        // The replay keeps the FULL contract of the original 201: status, body AND Location.
        var replay = await employees.CreateAsync(world.Admin, masterId, Array.Empty<string>(), "emp-key", CancellationToken.None);
        Assert.Equal(201, replay.Status);
        Assert.Equal(first.Body, replay.Body);
        Assert.Equal("/api/v1/employees/" + masterId, replay.Location);
    }

    [Fact]
    public async Task m5_user_spend_from_a_foreign_wallet_is_forbidden_and_has_no_effect()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(910);
        await world.CreateEmployeeAsync(911);
        var setup = await world.CreatePublishedCampaignAsync("M5", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 1000, "A1");
        var system = await world.CreatePurchaseSystemAsync("SHOP", a);
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 910, a, 10, "top-up", "MA-1", CancellationToken.None);
        await world.CreateGrantAsync("shop-source", "Spend", resourceId: a, purchaseSystemId: system);

        // Employee 911 names the wallet of 910: forbidden, and the victim's wallet and history
        // are untouched (B23 — the mutation the review planted passed all R1 suites).
        var rejected = await Assert.ThrowsAsync<MotivaException>(() => world.Resolve<SpendsService>().SpendAsync(
            world.Employee(911), 910, system, a, 3, "S-FOR", CancellationToken.None));
        Assert.Equal(ErrorCode.AuthzForbidden, rejected.Code);
        Assert.Equal(10, (await world.WalletAsync(910))["A"]);
        Assert.DoesNotContain((await world.WalletAsync(911)), b => b.Key == "A" && b.Value != 0);
        var history = await world.Resolve<ReadService>().ListOwnOperationsAsync(
            world.Employee(910), 10, null, null, null, null, null, null, CancellationToken.None);
        Assert.DoesNotContain(history.Items, o => o.Kind == OperationKind.Spend);

        // The service actor with a live grant MAY spend from that wallet (B23.2) — the positive
        // control proving the guard targets wallet ownership, not the operation itself.
        var serviceSpend = await world.Resolve<SpendsService>().SpendAsync(
            world.Service("shop-source"), 910, system, a, 3, "S-OK", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(serviceSpend.Body).GetProperty("result").GetString());
    }
}
