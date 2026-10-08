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
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None); // bool result: true = finished
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

        // A movement with an early createdAt whose COMMIT is delayed beyond the snapshot start:
        // the award holds its transaction open (before-commit seam) while the snapshot forms.
        // A timestamp filter would include the row; the consistent MVCC snapshot must not (B36/T07).
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 2, 0, TimeSpan.Zero));
        fixture.Host.Impediments.PauseOn(Checkpoints.ManualAwardBeforeCommit);
        var lateAward = Task.Run(() => world.Resolve<ManualAwardsService>().AwardAsync(
            world.Admin, campaignId, 1200, a, 7, "late commit", "MA-LATE", CancellationToken.None));
        await fixture.Host.Impediments.WaitReachedAsync(Checkpoints.ManualAwardBeforeCommit); // the award holds its open transaction

        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero));
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None); // bool result: true = finished
        fixture.Host.Impediments.Release(Checkpoints.ManualAwardBeforeCommit);
        await lateAward;

        var state = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        var csv = await DownloadAsync(world, state);
        var sum = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => long.Parse(l.Split(',')[3])).Sum();
        Assert.Equal(3, sum); // only the earlier committed reward; the late-committed 10:02 movement is excluded
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

        // Worker A runs the REAL handler path and stops (hard pause) after its upload; the lease
        // is 60 s of service time — the fake clock advances past it deterministically. The pause
        // is one-shot: only A stops there, worker B passes through — B's Ready provably happens
        // BEFORE A resumes.
        fixture.Host.Impediments.PauseFirstOn(Checkpoints.ExportAfterUploadBeforeReady);
        var workerA = Task.Run(() => world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None));
        await fixture.Host.Impediments.WaitReachedAsync(Checkpoints.ExportAfterUploadBeforeReady); // A: upload done, pre-Ready

        // Lease expiry by the service clock: worker B takes over through the same handler path.
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(61));
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None); // bool result: true = finished
        var winner = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Ready, winner.Status);
        Assert.Equal(2, winner.Generation);

        // ONLY NOW is worker A released; it CANNOT overwrite the winner: the Ready CAS by
        // generation fails and the losing attempt's object is removed by the winner.
        fixture.Host.Impediments.Release(Checkpoints.ExportAfterUploadBeforeReady);
        await workerA;
        var afterStale = await exports.GetAsync(world.Employee(1200), exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Ready, afterStale.Status);
        Assert.Equal(2, afterStale.Generation);

        // The winner's bytes are what downloads return; the stale gen-1 object is gone (§3.6).
        var csv = await DownloadAsync(world, winner);
        Assert.StartsWith("operationId,masterId", csv);
        var storage = world.Resolve<IFileStorage>();
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 1), CancellationToken.None));
        Assert.True(await storage.ExistsAsync(ExportsService.S3Key(exportId, 2), CancellationToken.None));
    }

    /// <summary>S-3 exact trace of the R1 review: the worker completes its upload and stops
    /// before Ready; the export is deleted; a cleanup sweep runs DURING the live lease (must
    /// not clear the intent); the worker then fails after the upload; the sweep after the lease
    /// expiry still removes the uploaded object — the guarantee never depends on the late
    /// worker finishing successfully.</summary>
    [Fact]
    public async Task d11_delete_during_upload_then_worker_failure_still_cleans_bytes()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-d11a", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);

        // The worker passes the pre-upload check, uploads its bytes and pauses before Ready.
        fixture.Host.Impediments.PauseOn(Checkpoints.ExportAfterUploadBeforeReady);
        var worker = Task.Run(() => world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None));
        await fixture.Host.Impediments.WaitReachedAsync(Checkpoints.ExportAfterUploadBeforeReady);
        var storage = world.Resolve<IFileStorage>();
        Assert.True(await storage.ExistsAsync(ExportsService.S3Key(exportId, 1), CancellationToken.None)); // the upload is real

        // The export is deleted and a cleanup sweep runs while the formation lease is live:
        // the intent must SURVIVE, otherwise the already-uploaded bytes would be orphaned.
        await exports.DeleteAsync(world.Employee(1200), exportId, CancellationToken.None);
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);
        var duringLease = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.True(duringLease!.CleanupIntent);
        Assert.True(await storage.ExistsAsync(ExportsService.S3Key(exportId, 1), CancellationToken.None));

        // The late worker fails after the upload (crash before Ready); after the lease expiry
        // the sweep removes its object with no help from the worker: no bytes, no intent (§3.6).
        fixture.Host.Impediments.FailOn(Checkpoints.ExportAfterUploadBeforeReady);
        fixture.Host.Impediments.Release(Checkpoints.ExportAfterUploadBeforeReady);
        await Assert.ThrowsAsync<ImpedimentFailureException>(() => worker);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(61));
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);
        var final = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Deleted, final!.Status);
        Assert.False(final.CleanupIntent);
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 1), CancellationToken.None));
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 2), CancellationToken.None));
    }

    /// <summary>A worker frozen mid-formation (pause of the whole seam, lease kept alive by the
    /// worker's clock) is fenced: after the deletion it aborts its own upload instead of
    /// writing bytes a settled cleanup would not know about.</summary>
    [Fact]
    public async Task d11_frozen_worker_aborts_upload_after_delete()
    {
        var (world, a, campaignId) = await ArrangeHistoryAsync();
        var exports = world.Resolve<ExportsService>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        fixture.Host.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var order = await exports.CreateAsync(world.Employee(1200), ExportScope.Own, null, a, from, to, "exp-d11b", CancellationToken.None);
        var exportId = Guid.Parse(TestWorld.ParseJson(order.Body).GetProperty("id").GetString()!);

        fixture.Host.Impediments.PauseOn(Checkpoints.ExportDuringUpload);
        var worker = Task.Run(() => world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None));
        await fixture.Host.Impediments.WaitReachedAsync(Checkpoints.ExportDuringUpload);

        // Deletion overtakes the frozen worker; it stays paused well past its lease.
        await exports.DeleteAsync(world.Employee(1200), exportId, CancellationToken.None);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);

        // The zombie worker resumes: the per-part fence rejects the upload (row deleted) —
        // the multipart upload is aborted, no object is ever completed.
        fixture.Host.Impediments.Release(Checkpoints.ExportDuringUpload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker);
        var storage = world.Resolve<IFileStorage>();
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 1), CancellationToken.None));
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, 2), CancellationToken.None));
        var final = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Deleted, final!.Status);
        Assert.False(final.CleanupIntent); // the post-expiry sweep settled the intent
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
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None); // bool result: true = finished
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

        // The sweep during the live formation lease keeps the intent (bytes may still be
        // written); after the lease expiry it removes the bytes of every generation for good.
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);
        var stillDeletedRow = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.True(stillDeletedRow!.CleanupIntent);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(61));
        await world.Resolve<CleanupHandler>().SweepAsync(CancellationToken.None);
        var storage = world.Resolve<IFileStorage>();
        Assert.False(await storage.ExistsAsync(ExportsService.S3Key(exportId, state.Generation), CancellationToken.None));
        var stillDeleted = await world.Resolve<IExportStore>().LoadAsync(exportId, CancellationToken.None);
        Assert.Equal(ExportStatus.Deleted, stillDeleted!.Status);
        Assert.False(stillDeleted.CleanupIntent);
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
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None); // bool result: true = finished

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
        await world.Resolve<ExportFormationHandler>().HandleAsync(exportId, CancellationToken.None); // bool result: true = finished
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
