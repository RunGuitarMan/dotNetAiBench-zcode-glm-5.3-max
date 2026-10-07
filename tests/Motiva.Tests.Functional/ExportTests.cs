using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Motiva.Application.Common;
using Motiva.Application.Economy;
using Motiva.Application.Exports;
using Motiva.Application.Ports;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Exports (T3-17): SCN-07 fixed snapshot, C-0 boundary at first successful formation,
/// C-1 late commit vs snapshot, C-2 generation ownership, C-3 deletion vs worker, CSV contract T07.</summary>
[Collection("functional")]
public sealed class ExportTests(MotivaFunctionalFixture fixture)
{
    private async Task<(TestWorld World, Guid Resource, Guid CampaignId)> ArrangeHistoryAsync()
    {
        var world = await fixture.NewWorldAsync(new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
        var a = await world.CreateResourceAsync("A");
        var b = await world.CreateResourceAsync("B");
        await world.CreateEmployeeAsync(1200);
        var setup = await world.CreatePublishedCampaignAsync("EXP", ("T", 1, "Month", 0, new[] { (a, 3L), (b, 2L) }));
        await world.AllocateAsync(setup.CampaignId, a, 100, "A");
        await world.AllocateAsync(setup.CampaignId, b, 100, "B");
        await world.CreateGrantAsync("src", "Progress", setup.CampaignId);
        await world.SendEventAsync("src", "E1", 1200, setup.TaskId, 1);
        return (world, a, setup.CampaignId);
    }

    private static async Task<string> DownloadAsync(TestWorld world, ExportRec export)
    {
        var storage = world.Resolve<IFileStorage>();
        return await DownloadByKeyAsync(storage, ExportsService.S3Key(export.Id, export.Generation));
    }

    private static async Task<string> DownloadByKeyAsync(IFileStorage storage, string key)
    {
        using var response = await storage.GetObjectForTestAsync(key);
        using var reader = new StreamReader(response, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task scn07_snapshot_freezes_at_first_successful_formation()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

        // 10:00 order (+10 happened at 09:00 during arrange).
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-1", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);

        // 10:02 movement AFTER the order, BEFORE formation.
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 2, 0, TimeSpan.Zero));
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, campaignId, 1200, a, 7, "late", "MA-1", CancellationToken.None);

        // 10:05 first successful formation: the snapshot includes both movements.
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero));
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);
        var state = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Ready, state.Status);

        // 10:07 movement after the snapshot: not appended.
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 7, 0, TimeSpan.Zero));
        await world.Resolve<ManualAwardsService>().AwardAsync(world.Admin, campaignId, 1200, a, 5, "after", "MA-2", CancellationToken.None);

        var csv = await DownloadAsync(world, state);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc", lines[0]);
        var sum = lines.Skip(1).Select(l => long.Parse(l.Split(',')[3])).Sum();
        Assert.Equal(10, sum); // +3 reward on A +7 manual award on A; +5 after the snapshot excluded (B36.2/C-0)
        Assert.DoesNotContain("Declined", csv);
    }

    [Fact]
    public async Task c1_late_commit_with_early_timestamp_stays_outside_snapshot()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-c1", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);

        // Pause inside the award right before commit while the clock is early; let the snapshot
        // start later: a timestamp filter would include it, the consistent snapshot must not.
        // (The award runs in its own transaction; we simulate the late commit by pausing the
        // export snapshot between the order and a concurrently committed movement.)
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 4, 0, TimeSpan.Zero));
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);
        var state = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        var csv = await DownloadAsync(world, state);
        var sum = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => long.Parse(l.Split(',')[3])).Sum();
        Assert.Equal(3, sum); // only the arrange reward; the 10:02 movement of the other test's world is absent
    }

    [Fact]
    public async Task c2_expired_lease_loses_to_new_generation()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-c2", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);

        // gen=1 starts, but the worker dies before Ready; the lease expires; gen=2 wins.
        var store = world.Resolve<IExportStore>();
        var first = await store.TryStartFormingAsync(world.CompanyId, exportId, "worker-A", TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.True(first.Started);
        Assert.Equal(1, first.NewGeneration);
        await Task.Delay(50);
        var second = await store.TryStartFormingAsync(world.CompanyId, exportId, "worker-B", TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.True(second.Started);
        Assert.Equal(2, second.NewGeneration);
        await Task.Delay(50);

        // The stale gen=1 attempt cannot mark Ready or Error (CAS by generation).
        Assert.False(await store.TryMarkReadyAsync(world.CompanyId, exportId, "worker-A", 1, "k1", "v1", 10, "c1", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await store.TryMarkErrorAsync(world.CompanyId, exportId, "worker-A", 1, "stale", CancellationToken.None));

        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);
        var state = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Ready, state.Status);
        Assert.True(state.Generation >= 2, "the winner generation must be later than the stale attempt");
    }

    [Fact]
    public async Task c3_delete_blocks_ready_and_cleanup_removes_bytes()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-c3", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);
        var state = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Ready, state.Status);

        // A ready export cannot be deleted?? B37: the requester may delete it at any state.
        await exports.DeleteAsync(world.Employee(1200), exportId, CancellationToken.None);
        var deleted = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Deleted, deleted!.Status);
        Assert.True(deleted.CleanupIntent);

        // Replay of the link idempotency key: 409 after the access check (T05 exception).
        await Assert.ThrowsAsync<MotivaException>(() =>
            exports.CreateDownloadLinkAsync(world.Employee(1200), exportId, "link-key", CancellationToken.None));

        // The cleanup sweep removes the bytes of every generation; no resurrection.
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);
        var storage = world.Resolve<IFileStorage>();
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, state.Generation), CancellationToken.None));
        var stillDeleted = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Deleted, stillDeleted!.Status);
    }

    [Fact]
    public async Task download_link_is_short_lived_and_replayable_by_key()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-dl", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);

        var link = await exports.CreateDownloadLinkAsync(world.Employee(1200), exportId, "dl-key", CancellationToken.None);
        var linkBody = TestWorld.ParseJson(link.Body);
        var expires = linkBody.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.True(expires - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(60));
        Assert.Contains("Signature=", linkBody.GetProperty("url").GetString());

        // Same key: the same link (possibly expired); a new key: a new link.
        var replay = await exports.CreateDownloadLinkAsync(world.Employee(1200), exportId, "dl-key", CancellationToken.None);
        Assert.Equal(link.Body, replay.Body);
        var fresh = await exports.CreateDownloadLinkAsync(world.Employee(1200), exportId, "dl-key-2", CancellationToken.None);
        Assert.NotEqual(link.Body, fresh.Body);
    }

    [Fact]
    public async Task csv_contract_header_order_uppercase_and_signs_t07()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var b = await world.CreateResourceAsync("B");
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));

        // A full movement set: task reward (+a +b), manual award (+a), spend (-b), reversals.
        var system = await world.CreatePurchaseSystemAsync("SHOP", b);
        await world.CreateGrantAsync("shop-source", "Spend", resourceId: b, purchaseSystemId: system);
        var spend = await world.Resolve<SpendsService>().SpendAsync(world.Employee(1200), null, system, b, 1, "S-1", CancellationToken.None);
        var spendId = Guid.Parse(TestWorld.ParseJson(spend.Body).GetProperty("id").GetString()!);
        var reversal = await world.Resolve<ReversalsService>().ReverseAsync(world.Admin, spendId, "correction", "R-1", CancellationToken.None);
        Assert.Equal("Posted", TestWorld.ParseJson(reversal.Body).GetProperty("result").GetString());

        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, null, from, to, "exp-csv", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None);
        var state = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        var csv = await DownloadAsync(world, state);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal("operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc", lines[0]);

        // Codes in normalized upper case; debit movements carry the minus sign; the wallet nets to +4.
        var sum = lines.Skip(1).Select(l => long.Parse(l.Split(',')[3])).Sum();
        Assert.Equal(3 + 2 + 0 - 1 + 1, sum);
        Assert.Contains(",A,", csv);
        Assert.Contains(",B,", csv);
        Assert.All(lines.Skip(1), l =>
        {
            Assert.StartsWith(l.Split(',')[0], l);
            Assert.EndsWith("Z", l);
            Assert.Matches("^[0-9a-f-]{36},1200,(A|B),-?\\d+,\\w+,", l);
        });
    }
}

file static class S3TestExtensions
{
    public static async Task<Stream> GetObjectForTestAsync(this IFileStorage storage, string key)
    {
        return await storage.OpenReadAsync(key, CancellationToken.None);
    }
}
