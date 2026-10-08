using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Motiva.Application.Common;
using Motiva.Application.Ports;
using Npgsql;

namespace Motiva.Infrastructure.Persistence.Stores;

public sealed class UnitOfWork(MotivaDbContext db) : IUnitOfWork
{
    public async Task<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken)
    {
        var scope = new UnitOfWorkScope(db);
        await scope.BeginAsync(cancellationToken);
        return scope;
    }

    private sealed class UnitOfWorkScope(MotivaDbContext db) : IUnitOfWorkScope
    {
        private IDbContextTransaction? _transaction;

        public async Task BeginAsync(CancellationToken ct)
        {
            _transaction = await db.Database.BeginTransactionAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (_transaction is null)
            {
                throw new InvalidOperationException("Transaction not started.");
            }

            await _transaction.CommitAsync(ct);
            await _transaction.DisposeAsync();
            _transaction = null;
            db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is not null)
            {
                try
                {
                    await _transaction.RollbackAsync();
                }
                catch
                {
                    // Disposal after a failure: nothing to preserve.
                }

                await _transaction.DisposeAsync();
                _transaction = null;
                db.ChangeTracker.Clear();
            }
        }
    }
}

public sealed class OutboxStore(MotivaDbContext db) : IBackgroundJobs
{
    public async Task<Guid> EnqueueAsync(string type, string payload, DateTimeOffset availableAt, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        db.OutboxJobs.Add(new OutboxJobRow
        {
            Id = id,
            Type = type,
            Payload = payload,
            AvailableAt = availableAt,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    private sealed record ClaimRow(Guid Id, string Type, string Payload, DateTimeOffset AvailableAt, int Attempts, string Status);

    public async Task<IReadOnlyList<OutboxJob>> ClaimAsync(string workerId, int batch, DateTimeOffset utcNow, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_jobs SET locked_by = {workerId}, locked_until = {utcNow.AddSeconds(30)}, status = 'Running', attempts = attempts + 1
            WHERE id IN (
                SELECT id FROM outbox_jobs
                WHERE (status = 'Pending' AND available_at <= {utcNow})
                   OR (status = 'Running' AND locked_until < {utcNow})
                ORDER BY available_at
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED)
            """, ct);
        var rows = await db.Database.SqlQuery<ClaimRow>($"""
            SELECT id, type, payload::text AS payload, available_at, attempts, status
            FROM outbox_jobs WHERE locked_by = {workerId} AND status = 'Running'
            """).ToListAsync(ct);
        return rows.Select(r => new OutboxJob(r.Id, r.Type, r.Payload, r.AvailableAt, r.Attempts, r.Status, workerId, utcNow.AddSeconds(30))).ToList();
    }

    public Task ExtendLeaseAsync(IReadOnlyList<Guid> jobIds, string workerId, DateTimeOffset utcUntil, CancellationToken ct)
    {
        return db.OutboxJobs
            .Where(j => jobIds.Contains(j.Id) && j.LockedBy == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedUntil, utcUntil), ct);
    }

    public Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        return db.OutboxJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "Done").SetProperty(j => j.LockedBy, (string?)null), ct);
    }

    public Task FailAsync(Guid jobId, string errorDetail, DateTimeOffset retryNotBefore, CancellationToken ct)
    {
        return db.OutboxJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "Pending")
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.AvailableAt, retryNotBefore)
                .SetProperty(j => j.Error, errorDetail)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
    }

    public Task<int> CountPendingAsync(CancellationToken ct)
        => db.OutboxJobs.CountAsync(j => j.Status == "Pending", ct);
}

public sealed class AuditStore(MotivaDbContext db) : IAuditLog
{
    public async Task AppendAsync(Guid companyId, ActorContext actor, AuditInput input, DateTimeOffset utcNow, CancellationToken ct)
    {
        db.AuditRecords.Add(new AuditRecordRow
        {
            CompanyId = companyId,
            Id = Guid.NewGuid(),
            ActorType = actor.ActorType.ToString(),
            ActorMasterId = actor.MasterId,
            ActorSubject = actor.Subject,
            ActorIsAdmin = actor.IsAdmin,
            Action = input.Action,
            EntityType = input.EntityType,
            EntityId = input.EntityId,
            ChangesJson = CanonicalJson.Serialize(input.Changes.Select(c => new { c.Field, From = c.From, To = c.To }).ToArray()),
            CreatedAt = utcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Page<AuditRec>> ListAsync(
        Guid companyId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, string? entityType, string? entityId, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<OperationStore.TimeIdCursor>(cursor);
        var query = db.AuditRecords.Where(a => a.CompanyId == companyId);
        if (boundary is not null)
        {
            query = query.Where(a => a.CreatedAt > boundary.At || (a.CreatedAt == boundary.At && a.Id.CompareTo(boundary.Id) > 0));
        }

        if (from is { } fromValue)
        {
            query = query.Where(a => a.CreatedAt >= fromValue);
        }

        if (toUtc is { } toValue)
        {
            query = query.Where(a => a.CreatedAt < toValue);
        }

        if (entityType is not null)
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (entityId is not null)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        var rows = await query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit
            ? CursorCodec.Encode(new OperationStore.TimeIdCursor(rows[limit - 1].CreatedAt, rows[limit - 1].Id))
            : null;
        return new Page<AuditRec>(items, next);
    }

    private static AuditRec ToRec(AuditRecordRow row)
    {
        var actor = new ActorContext(
            row.CompanyId, Enum.Parse<ActorType>(row.ActorType), row.ActorMasterId, row.ActorSubject ?? string.Empty, row.ActorIsAdmin);
        var changes = CanonicalJson.Deserialize<AuditChangeDto[]>(row.ChangesJson) ?? Array.Empty<AuditChangeDto>();
        return new AuditRec(
            row.Id, actor, row.Action, row.EntityType, row.EntityId,
            changes.Select(c => new AuditChangeRec(c.Field, c.From, c.To)).ToList(), row.CreatedAt);
    }

    private sealed record AuditChangeDto(string Field, string? From, string? To);
}

public sealed class IdempotencyStore(MotivaDbContext db) : IIdempotencyStore
{
    public async Task<IdempotencyBeginResult> BeginAsync(
        Guid companyId, string initiatorKey, string operation, string targetId, string key, string essentialData, CancellationToken ct)
    {
        // A concurrent uncommitted insert of the same PK blocks here until the first writer
        // commits or aborts (bounded waiting per T05); ON CONFLICT avoids raising an error.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_keys (company_id, initiator_key, operation, target_id, key, essential_data, created_at)
            VALUES ({companyId}, {initiatorKey}, {operation}, {targetId}, {key}, {essentialData}, {DateTimeOffset.UtcNow})
            ON CONFLICT DO NOTHING
            """, ct);
        var row = await db.IdempotencyKeys.FindAsync(
            new object[] { companyId, initiatorKey, operation, targetId, key }, ct);
        if (row?.ResponseBody is null)
        {
            return new IdempotencyBeginResult(false, null, null, null);
        }

        return new IdempotencyBeginResult(true, row.EssentialData, row.ResponseStatus, row.ResponseBody);
    }

    public async Task CompleteAsync(
        Guid companyId, string initiatorKey, string operation, string targetId, string key, int status, string body, CancellationToken ct)
    {
        await db.IdempotencyKeys
            .Where(k => k.CompanyId == companyId && k.InitiatorKey == initiatorKey && k.Operation == operation
                        && k.TargetId == targetId && k.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ResponseStatus, status).SetProperty(k => k.ResponseBody, body), ct);
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        return db.IdempotencyKeys.Where(k => k.CreatedAt < olderThan).ExecuteDeleteAsync(ct);
    }
}
