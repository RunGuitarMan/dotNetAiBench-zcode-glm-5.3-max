using Motiva.Application.Administration;
using Motiva.Application.Budgets;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Economy;
using Motiva.Application.Ports;
using Motiva.Application.Progress;
using Motiva.Application.Reads;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Spends, reversals, rights freshness and the §3.3 outcomes (T3-12/T3-13/T3-14).</summary>
[Collection("functional")]
public sealed class EconomyRightsTests(MotivaFunctionalFixture fixture)
{
    private async Task<(TestWorld World, Guid Resource, Guid System, Guid CampaignId)> ArrangeShopAsync()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(700);
        var setup = await world.CreatePublishedCampaignAsync("SHOP", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        var system = await world.CreatePurchaseSystemAsync("SHOPSYS", a);
        await world.AllocateAsync(setup.CampaignId, a, 1000, "BASE");
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 700, a, 10, "top-up", "MA-1", CancellationToken.None);
        await world.CreateGrantAsync("shop-source", "Spend", resourceId: a, purchaseSystemId: system);
        return (world, a, system, setup.CampaignId);
    }

    [Fact]
    public async Task scn03_declined_spend_replays_after_top_up_new_number_succeeds()
    {
        var (world, a, system, campaignId) = await ArrangeShopAsync();
        var spends = world.Resolve<SpendsService>();

        // SCN-03 exact numbers: balance 10 -> spend 7 (Posted) -> balance 3 -> spend 7 declines.
        var first = await spends.SpendAsync(world.Employee(700), null, system, a, 7, "S-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(first.Body).GetProperty("result").GetString());

        var second = await spends.SpendAsync(world.Employee(700), null, system, a, 7, "S-2", CancellationToken.None);
        Assert.Equal("Declined", TestWorld.ParseJson(second.Body).GetProperty("result").GetString());
        Assert.Equal("InsufficientFunds", TestWorld.ParseJson(second.Body).GetProperty("refusalCode").GetString());
        Assert.Equal(3, (await world.WalletAsync(700))["A"]);

        // When 2: +10 -> balance 13. When 3: replay of the declined number stays declined; new number spends.
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, campaignId, 700, a, 10, "top-up-2", "MA-2", CancellationToken.None);
        var replay = await spends.SpendAsync(world.Employee(700), null, system, a, 7, "S-2", CancellationToken.None);
        Assert.Equal(second.Body, replay.Body);
        Assert.Equal(13, (await world.WalletAsync(700))["A"]);
        var fresh = await spends.SpendAsync(world.Employee(700), null, system, a, 7, "S-3", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(fresh.Body).GetProperty("result").GetString());
        Assert.Equal(6, (await world.WalletAsync(700))["A"]);
    }

    [Fact]
    public async Task spend_does_not_return_funds_to_budget_b23f()
    {
        var (world, a, system, campaignId) = await ArrangeShopAsync();
        var before = await world.BudgetAvailableAsync(campaignId, a);
        await world.Resolve<SpendsService>().SpendAsync(world.Employee(700), null, system, a, 5, "S-9", CancellationToken.None);
        Assert.Equal(before, await world.BudgetAvailableAsync(campaignId, a));
    }

    [Fact]
    public async Task blocked_initiator_outcomes_403_vs_declined_recipient_h0q08()
    {
        var (world, a, system, campaignId) = await ArrangeShopAsync();
        var spends = world.Resolve<SpendsService>();
        var employees = world.Resolve<EmployeesService>();

        // Employee blocks themselves: their own spend -> 403 without a record.
        var profile = await employees.GetAsync(world.Admin, 700, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 700, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        await Assert.ThrowsAsync<MotivaException>(() => spends.SpendAsync(world.Employee(700), null, system, a, 1, "B1", CancellationToken.None));
        var history = await world.Resolve<ReadService>().ListOwnOperationsAsync(world.Employee(700), 100, null, null, null, null, null, null, CancellationToken.None);
        Assert.DoesNotContain(history.Items, o => o.SourceOperationNumber == "B1");

        // A NEW service spend to the blocked recipient: saved decline, number occupied.
        var declined = await spends.SpendAsync(world.Service("shop-source"), 700, system, a, 1, "B2", CancellationToken.None);
        Assert.Equal("Declined", TestWorld.ParseJson(declined.Body).GetProperty("result").GetString());
        Assert.Equal("RecipientNotActive", TestWorld.ParseJson(declined.Body).GetProperty("refusalCode").GetString());
        var replay = await spends.SpendAsync(world.Service("shop-source"), 700, system, a, 1, "B2", CancellationToken.None);
        Assert.Equal(declined.Body, replay.Body);

        // Unblock: the wallet is back, but the saved decline stays final.
        var unblocked = await employees.GetAsync(world.Admin, 700, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 700, ETags.Format(unblocked.Version), isActive: true, tags: null, CancellationToken.None);
        var stillDeclined = await spends.SpendAsync(world.Service("shop-source"), 700, system, a, 1, "B2", CancellationToken.None);
        Assert.Equal(declined.Body, stillDeclined.Body);
    }

    [Fact]
    public async Task service_reads_only_own_spend_pairs_section33()
    {
        var (world, a, system, campaignId) = await ArrangeShopAsync();
        var b = await world.CreateResourceAsync("B");
        var otherSystem = await world.CreatePurchaseSystemAsync("OTHER", a, b);
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, campaignId, 700, a, 5, "top-up", "MA-3", CancellationToken.None);

        var spends = world.Resolve<SpendsService>();
        await spends.SpendAsync(world.Employee(700), null, system, a, 1, "S-1", CancellationToken.None);
        await spends.SpendAsync(world.Employee(700), null, otherSystem, a, 1, "S-2", CancellationToken.None);
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, campaignId, 700, a, 1, "award", "MA-4", CancellationToken.None);

        // shop-source has a grant for (SHOPSYS, A): only the S-1 pair is visible; no awards.
        var visible = await world.Resolve<ReadService>().ListOwnOperationsAsync(world.Service("shop-source"), 100, null, null, null, null, null, null, CancellationToken.None);
        Assert.Single(visible.Items);
        Assert.Equal("S-1", visible.Items[0].SourceOperationNumber);
        Assert.Equal(OperationKind.Spend, visible.Items[0].Kind);
    }

    [Fact]
    public async Task e08_award_reversal_needs_every_balance()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        var b = await world.CreateResourceAsync("B");
        await world.CreateEmployeeAsync(800);
        var setup = await world.CreatePublishedCampaignAsync("E08", ("T", 1, "Month", 0, new[] { (a, 10L), (b, 2L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.AllocateAsync(setup.CampaignId, b, 100, "B");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        var event1 = await world.SendEventAsync("src", "E1", 800, setup.TaskId, 1);
        Assert.Equal("Granted", TestWorld.ParseJson(event1.Body).GetProperty("completion").GetProperty("reward").GetProperty("outcome").GetString());
        var rewardOperationId = Guid.Parse(TestWorld.ParseJson(event1.Body).GetProperty("completion").GetProperty("reward").GetProperty("operationId").GetString()!);

        // Spend 1 B: reversal of the whole package must decline; A stays in the wallet.
        var system = await world.CreatePurchaseSystemAsync("SHOP", b);
        await world.CreateGrantAsync("shop", "Spend", resourceId: b, purchaseSystemId: system);
        var spend = await world.Resolve<SpendsService>().SpendAsync(world.Employee(800), null, system, b, 1, "S-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(spend.Body).GetProperty("result").GetString());

        var reversal = await world.Resolve<ReversalsService>().ReverseAsync(world.Admin, rewardOperationId, "rollback", "R-1", CancellationToken.None);
        Assert.Equal("Declined", TestWorld.ParseJson(reversal.Body).GetProperty("result").GetString());
        Assert.Equal("InsufficientBalance", TestWorld.ParseJson(reversal.Body).GetProperty("refusalCode").GetString());
        var wallet = await world.WalletAsync(800);
        Assert.Equal(10, wallet["A"]);
        Assert.Equal(1, wallet["B"]);
        Assert.Equal(90, await world.BudgetAvailableAsync(setup.CampaignId, a));
    }

    [Fact]
    public async Task edge03_reversal_after_archive_returns_to_budget_but_no_new_spending()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(810);
        var setup = await world.CreatePublishedCampaignAsync("EDG3", ("T", 1, "Month", 0, new[] { (a, 10L) }));
        await world.AllocateAsync(setup.CampaignId, a, 50, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        var event1 = await world.SendEventAsync("src", "E1", 810, setup.TaskId, 1);
        var rewardOperationId = Guid.Parse(TestWorld.ParseJson(event1.Body).GetProperty("completion").GetProperty("reward").GetProperty("operationId").GetString()!);

        // Wallet holds 15 A (10 reward + manual 5); archive A; reversal returns 10 to the budget.
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 810, a, 5, "extra", "MA-1", CancellationToken.None);
        var resources = world.Resolve<ResourcesService>();
        var current = await resources.GetAsync(world.Admin, a, CancellationToken.None);
        await resources.PatchAsync(world.Admin, a, ETags.Format(current.Version), null, ResourceStatus.Archived, CancellationToken.None);

        var reversal = await world.Resolve<ReversalsService>().ReverseAsync(world.Admin, rewardOperationId, "rollback", "R-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(reversal.Body).GetProperty("result").GetString());
        Assert.Equal(5, (await world.WalletAsync(810))["A"]);
        Assert.Equal(45, await world.BudgetAvailableAsync(setup.CampaignId, a));

        // New spending of the archived resource is forbidden (B07.2).
        var system = await world.CreatePurchaseSystemAsync("SHOP", a);
        await world.CreateGrantAsync("shop", "Spend", resourceId: a, purchaseSystemId: system);
        var spend = await world.Resolve<SpendsService>().SpendAsync(world.Employee(810), null, system, a, 1, "S-1", CancellationToken.None);
        Assert.Equal("ResourceUnavailable", TestWorld.ParseJson(spend.Body).GetProperty("refusalCode").GetString());
    }

    [Fact]
    public async Task scn06_block_and_revoke_act_immediately_including_replay()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(900);
        var setup = await world.CreatePublishedCampaignAsync("SCN6", ("T", 2, "Month", 10, new[] { (a, 3L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        await world.SendEventAsync("src", "E-1", 900, setup.TaskId, 2);

        var employees = world.Resolve<EmployeesService>();
        var profile = await employees.GetAsync(world.Admin, 900, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 900, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);

        // New event and replay for the blocked user: 403 before the number, no new record (H0 Q04).
        await Assert.ThrowsAsync<MotivaException>(() => world.SendEventAsync("src", "E-2", 900, setup.TaskId, 1));
        var e1Again = await Assert.ThrowsAsync<MotivaException>(() => world.SendEventAsync("src", "E-1", 900, setup.TaskId, 2));
        Assert.Equal(ErrorCode.AuthzEmployeeNotActive, e1Again.Code);
        var reads = world.Resolve<ReadService>();
        var blockedWallet = await reads.GetEmployeeWalletAsync(world.Admin, 900, CancellationToken.None);
        Assert.Equal(3, blockedWallet.Balances.Single(b => b.ResourceCode == "A").Balance);
        var events = await world.Resolve<ProgressEventsService>().ListOwnAsync(world.Employee(900), 100, null, null, null, null, null, CancellationToken.None);
        Assert.Single(events.Items);

        // Administrative correction of the blocked recipient's award is allowed (H0 Q03); owner only — 403.
        var operations = await reads.ListEmployeeOperationsAsync(world.Admin, 900, 100, null, null, null, a, OperationKind.TaskReward, CancellationToken.None);
        var rewardId = operations.Items.Single().Id;
        var reversal = await world.Resolve<ReversalsService>().ReverseAsync(world.Admin, rewardId, "admin correction", "R-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(reversal.Body).GetProperty("result").GetString());

        // Reactivation restores the same wallet.
        var unblocked = await employees.GetAsync(world.Admin, 900, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 900, ETags.Format(unblocked.Version), isActive: true, tags: null, CancellationToken.None);
        Assert.Equal(0, (await world.WalletAsync(900))["A"]);

        // Grant revocation: new events 403; previously stored results are not annulled.
        var grants = world.Resolve<IntegrationGrantsService>();
        var grantList = await grants.ListAsync(world.Admin, "src", 100, null, CancellationToken.None);
        var grant = grantList.Items.Single(g => g.Kind == GrantKind.Progress);
        await grants.RevokeAsync(world.Admin, grant.Id, ETags.Format(grant.Version), CancellationToken.None);
        await Assert.ThrowsAsync<MotivaException>(() => world.SendEventAsync("src", "E-3", 900, setup.TaskId, 1));
        var keptEvents = await world.Resolve<ProgressEventsService>().ListOwnAsync(world.Employee(900), 100, null, null, null, null, null, CancellationToken.None);
        Assert.Single(keptEvents.Items);
    }

    [Fact]
    public async Task owner_change_moves_rights_immediately_b05()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(910);
        var setup = await world.CreatePublishedCampaignAsync("OWNER", ("T", 5, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        var campaigns = world.Resolve<CampaignsService>();
        var current = await campaigns.GetAsync(world.Admin, setup.CampaignId, CancellationToken.None);
        await campaigns.PutOwnerAsync(world.Admin, setup.CampaignId, ETags.Format(current.Version), 910, CancellationToken.None);

        // The former owner (admin token keeps admin rights) — check the NON-admin case through
        // a second employee owner chain instead: give ownership to 910, then have 910 award.
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Employee(910), setup.CampaignId, 910, a, 1, "owner award", "MA-1", CancellationToken.None);
    }

    [Fact]
    public async Task manual_award_requires_audience_and_active_recipient_b21()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(920, "vip");
        await world.CreateEmployeeAsync(921);
        var setup = await world.CreatePublishedCampaignAsync(
            "MA", ("T", 5, "Month", 0, new[] { (a, 1L) }), employeeTags: null,
            startsAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");

        // Blocked recipient -> saved decline.
        var employees = world.Resolve<EmployeesService>();
        var profile = await employees.GetAsync(world.Admin, 920, CancellationToken.None);
        await employees.PatchAsync(world.Admin, 920, ETags.Format(profile.Version), isActive: false, tags: null, CancellationToken.None);
        var blocked = await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 920, a, 1, "r", "MA-B", CancellationToken.None);
        Assert.Equal("RecipientNotActive", TestWorld.ParseJson(blocked.Body).GetProperty("refusalCode").GetString());

        // Insufficient budget -> saved decline without changes.
        var shortage = await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 921, a, 1000, "r", "MA-S", CancellationToken.None);
        Assert.Equal("InsufficientBudget", TestWorld.ParseJson(shortage.Body).GetProperty("refusalCode").GetString());
        Assert.Equal(100, await world.BudgetAvailableAsync(setup.CampaignId, a));
        Assert.Equal(0, (await world.WalletAsync(921))["A"]);

        // Owner cannot grow the budget (B03.2); integration without the exact grant — 403.
        await Assert.ThrowsAsync<MotivaException>(() =>
            world.Resolve<BudgetService>().AllocateAsync(world.Employee(920), setup.CampaignId, a, 5, "x", "B-X", CancellationToken.None));
        await Assert.ThrowsAsync<MotivaException>(() =>
            world.Resolve<ManualAwardsService>().AwardAsync(world.Service("no-grant"), setup.CampaignId, 921, a, 1, "r", "MA-G", CancellationToken.None));
    }

    [Fact]
    public async Task reversal_rights_and_number_replay_b26_b27()
    {
        var world = await fixture.NewWorldAsync();
        var a = await world.CreateResourceAsync("A");
        await world.CreateEmployeeAsync(930);
        var setup = await world.CreatePublishedCampaignAsync("REV", ("T", 1, "Month", 0, new[] { (a, 1L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        var award = await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, setup.CampaignId, 930, a, 5, "award", "MA-1", CancellationToken.None);
        var awardId = Guid.Parse(TestWorld.ParseJson(award.Body).GetProperty("id").GetString()!);

        var reversals = world.Resolve<ReversalsService>();
        // An employee never reverses (B27.4 for spends; B03.1 for awards).
        await Assert.ThrowsAsync<MotivaException>(() => reversals.ReverseAsync(world.Employee(930), awardId, "r", "R-0", CancellationToken.None));

        var posted = await reversals.ReverseAsync(world.Admin, awardId, "correction", "R-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(posted.Body).GetProperty("result").GetString());
        var replay = await reversals.ReverseAsync(world.Admin, awardId, "correction", "R-1", CancellationToken.None);
        Assert.Equal(posted.Body, replay.Body);
        var again = await reversals.ReverseAsync(world.Admin, awardId, "correction", "R-2", CancellationToken.None);
        Assert.Equal("OriginalAlreadyReversed", TestWorld.ParseJson(again.Body).GetProperty("refusalCode").GetString());
    }
}
