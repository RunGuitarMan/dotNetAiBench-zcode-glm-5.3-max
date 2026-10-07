using System.Data;
using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>Export persistence (§3.6): the snapshot materializes in one REPEATABLE READ
/// transaction that atomically moves Pending/Error → Forming, bumps generation and freezes
/// the operation id set; Ready/Error/lease transitions are guarded by generation CAS.</summary>
public sealed class ExportStore(MotivaDbContext db) : IExportStore
{
    public async Task<(UpdateOutcome, ExportRec?)> CreateAsync(
        Guid companyId, Guid id, ActorContext requester, ExportScope scope, int? masterId, Guid? resourceId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset utcNow, CancellationToken ct)
    {
        var row = new ExportRequestRow
        {
            CompanyId = companyId,
            Id = id,
            RequestedByMasterId = requester.MasterId ?? 0,
            Scope = scope.ToString(),
            TargetMasterId = masterId,
            ResourceId = resourceId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            CreatedAt = utcNow,
        };
        db.ExportRequests.Add(row);
        await db.SaveChangesAsync(ct);
        return (UpdateOutcome.Ok, ToRec(row));
    }

    public async Task<ExportRec?> GetAsync(Guid companyId, Guid exportId, CancellationToken ct)
    {
        var row = await db.ExportRequests.FindAsync(new object[] { exportId }, ct);
        return row is null || row.CompanyId != companyId ? null : ToRec(row);
    }

    public async Task<ExportRec?> LoadAsync(Guid exportId, CancellationToken ct)
    {
        var row = await db.ExportRequests.FindAsync(new object[] { exportId }, ct);
        return row is null ? null : ToRec(row);
    }

    public async Task<Page<ExportRec>> ListAsync(Guid companyId, int? requestedByMasterId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<OperationStore.TimeIdCursor>(cursor);
        var query = db.ExportRequests.Where(e => e.CompanyId == companyId);
        if (requestedByMasterId is { } master)
        {
            query = query.Where(e => e.RequestedByMasterId == master);
        }

        if (boundary is not null)
        {
            query = query.Where(e => e.CreatedAt > boundary.At || (e.CreatedAt == boundary.At && e.Id.CompareTo(boundary.Id) > 0));
        }

        var rows = await query.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit
            ? CursorCodec.Encode(new OperationStore.TimeIdCursor(rows[limit - 1].CreatedAt, rows[limit - 1].Id))
            : null;
        return new Page<ExportRec>(items, next);
    }

    public async Task<ExportSnapshotResult> TryStartFormingAsync(
        Guid companyId, Guid exportId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var row = await db.ExportRequests
            .FromSql($@"SELECT * FROM export_requests WHERE id = {exportId} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(ct);
        if (row is null || row.Status is "Deleted" or "Ready")
        {
            await transaction.RollbackAsync(ct);
            return new ExportSnapshotResult(false, 0, Array.Empty<Guid>());
        }

        if (row.Status == "Forming" && row.LeaseUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            // A live lease owns the formation; an expired one may be taken over (generation++).
            await transaction.RollbackAsync(ct);
            return new ExportSnapshotResult(false, 0, Array.Empty<Guid>());
        }

        var generation = row.Generation + 1;
        var frozenAt = DateTimeOffset.UtcNow;
        Guid[] operationIds;
        if (!row.SnapshotSaved)
        {
            // B36.2: the data set is fixed at the first successful start of formation — one
            // consistent REPEATABLE READ snapshot; later movements never extend it.
            var snapshotIds = await db.Database
                .SqlQuery<Guid>($"""
                    SELECT o.id FROM operations o
                    WHERE o.company_id = {row.CompanyId} AND o.result = 'Posted'
                      AND o.kind IN ('TaskReward', 'ManualAward', 'Spend', 'SpendReversal', 'AwardReversal')
                      AND o.created_at >= {row.FromUtc} AND o.created_at < {row.ToUtc}
                      AND ({row.TargetMasterId}::int IS NULL OR o.master_id = {row.TargetMasterId})
                      AND ({row.ResourceId}::uuid IS NULL OR EXISTS (
                          SELECT 1 FROM operation_items oi WHERE oi.operation_id = o.id AND oi.resource_id = {row.ResourceId}))
                    ORDER BY o.created_at, o.id
                    """)
                .ToListAsync(ct);
            operationIds = [.. snapshotIds];
        }
        else
        {
            operationIds = [.. await db.ExportOperationIds
                .Where(x => x.ExportId == exportId)
                .Select(x => x.OperationId)
                .ToListAsync(ct)];
        }

        if (!row.SnapshotSaved)
        {
            db.ExportOperationIds.AddRange(operationIds.Select(id => new ExportOperationIdRow { ExportId = exportId, OperationId = id }));
            row.SnapshotSaved = true;
            row.FrozenAt = frozenAt;
        }

        row.Status = "Forming";
        row.Generation = generation;
        row.LeaseOwner = leaseOwner;
        row.LeaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration);
        row.ErrorDetail = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ExportSnapshotResult(true, generation, operationIds);
    }

    public Task<bool> TryExtendLeaseAsync(Guid companyId, Guid exportId, string leaseOwner, int expectedGeneration, CancellationToken ct)
    {
        return db.ExportRequests
            .Where(e => e.Id == exportId && e.Generation == expectedGeneration
                        && e.LeaseOwner == leaseOwner && e.Status == "Forming")
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(60)), ct)
            .ContinueWith(t => t.Result > 0);
    }

    public async Task<bool> TryMarkReadyAsync(
        Guid companyId, Guid exportId, string leaseOwner, int expectedGeneration, string s3Key, string s3Version,
        long sizeBytes, string checksum, DateTimeOffset utcNow, CancellationToken ct)
    {
        var affected = await db.ExportRequests
            .Where(e => e.Id == exportId && e.Generation == expectedGeneration && e.LeaseOwner == leaseOwner
                        && e.Status == "Forming")
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "Ready")
                .SetProperty(e => e.S3Key, s3Key)
                .SetProperty(e => e.S3Version, s3Version)
                .SetProperty(e => e.SizeBytes, sizeBytes)
                .SetProperty(e => e.Checksum, checksum)
                .SetProperty(e => e.ReadyAt, utcNow), ct);
        return affected > 0;
    }

    public async Task<bool> TryMarkErrorAsync(Guid companyId, Guid exportId, string leaseOwner, int expectedGeneration, string errorDetail, CancellationToken ct)
    {
        var affected = await db.ExportRequests
            .Where(e => e.Id == exportId && e.Generation == expectedGeneration && e.LeaseOwner == leaseOwner
                        && e.Status == "Forming")
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "Error")
                .SetProperty(e => e.ErrorDetail, errorDetail), ct);
        return affected > 0;
    }

    public async Task<bool> MarkDeletedAsync(Guid companyId, Guid exportId, ActorContext actor, DateTimeOffset utcNow, CancellationToken ct)
    {
        await db.DownloadLinks.Where(l => l.ExportId == exportId).ExecuteDeleteAsync(ct);
        var affected = await db.ExportRequests
            .Where(e => e.Id == exportId && e.CompanyId == companyId && e.Status != "Deleted")
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "Deleted")
                .SetProperty(e => e.CleanupIntent, true), ct);
        return affected > 0;
    }

    public Task<IReadOnlyList<Guid>> ListCleanupIntentsAsync(CancellationToken ct)
        => db.ExportRequests.Where(e => e.CleanupIntent).Select(e => e.Id).ToListAsync(ct).ContinueWith(t => (IReadOnlyList<Guid>)t.Result);

    public Task ClearCleanupIntentAsync(Guid exportId, CancellationToken ct)
        => db.ExportRequests.Where(e => e.Id == exportId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CleanupIntent, false), ct);

    public Task<IReadOnlyList<Guid>> GetSnapshotOperationIdsAsync(Guid exportId, CancellationToken ct)
        => db.ExportOperationIds.Where(x => x.ExportId == exportId).Select(x => x.OperationId).ToListAsync(ct).ContinueWith(t => (IReadOnlyList<Guid>)t.Result);

    public async Task<DateTimeOffset?> GetFrozenAtAsync(Guid exportId, CancellationToken ct)
    {
        return await db.ExportRequests.Where(e => e.Id == exportId).Select(e => e.FrozenAt).FirstOrDefaultAsync(ct);
    }

    public async Task<int> GetCurrentGenerationAsync(Guid exportId, CancellationToken ct)
    {
        return await db.ExportRequests.Where(e => e.Id == exportId).Select(e => e.Generation).FirstOrDefaultAsync(ct);
    }

    public Task ScheduleFormationAsync(Guid exportId, DateTimeOffset availableAt, CancellationToken ct)
        => EnqueueAsync(db, "form-export", CanonicalJson.Serialize(new { exportId }), availableAt, ct);

    public async Task<DownloadLinkRec?> FindActiveLinkAsync(Guid exportId, string idemKey, CancellationToken ct)
    {
        var row = await db.DownloadLinks.FirstOrDefaultAsync(l => l.ExportId == exportId && l.IdemKey == idemKey, ct);
        return row is null ? null : new DownloadLinkRec(row.Id, string.Empty, row.ExpiresAt);
    }

    internal static async Task EnqueueAsync(MotivaDbContext db, string type, string payload, DateTimeOffset availableAt, CancellationToken ct)
    {
        db.OutboxJobs.Add(new OutboxJobRow { Id = Guid.NewGuid(), Type = type, Payload = payload, AvailableAt = availableAt, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    internal static ExportRec ToRec(ExportRequestRow row)
        => new(row.CompanyId, row.Id, row.RequestedByMasterId, Enum.Parse<ExportScope>(row.Scope), row.TargetMasterId,
            null, row.ResourceId, row.FromUtc, row.ToUtc, Enum.Parse<ExportStatus>(row.Status), row.CreatedAt,
            row.FrozenAt, row.ReadyAt, row.SizeBytes, row.Checksum, row.ErrorDetail, row.Generation, row.CleanupIntent);
}
