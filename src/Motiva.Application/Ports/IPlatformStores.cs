namespace Motiva.Application.Ports;

public sealed record OutboxJob(
    Guid Id, string Type, string Payload, DateTimeOffset AvailableAt, int Attempts, string Status,
    string? LockedBy, DateTimeOffset? LockedUntil);

/// <summary>Background jobs planned atomically with the business commit (outbox pattern, §1.3).</summary>
public interface IBackgroundJobs
{
    Task<Guid> EnqueueAsync(string type, string payload, DateTimeOffset availableAt, CancellationToken ct);

    /// <summary>Claims due jobs with FOR UPDATE SKIP LOCKED and a lease (two workers safe).</summary>
    Task<IReadOnlyList<OutboxJob>> ClaimAsync(string workerId, int batch, DateTimeOffset utcNow, CancellationToken ct);

    Task ExtendLeaseAsync(IReadOnlyList<Guid> jobIds, string workerId, DateTimeOffset utcUntil, CancellationToken ct);

    Task CompleteAsync(Guid jobId, CancellationToken ct);

    Task FailAsync(Guid jobId, string errorDetail, DateTimeOffset retryNotBefore, CancellationToken ct);

    Task<int> CountPendingAsync(CancellationToken ct);
}

public sealed record AuditInput(string Action, string EntityType, string EntityId, IReadOnlyList<AuditChangeRec> Changes);

/// <summary>Append-only journal of rights/settings/budget changes (B28.3).</summary>
public interface IAuditLog
{
    Task AppendAsync(Guid companyId, Common.ActorContext actor, AuditInput input, DateTimeOffset utcNow, CancellationToken ct);

    Task<Page<AuditRec>> ListAsync(
        Guid companyId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, string? entityType,
        string? entityId, CancellationToken ct);
}

public sealed record IdempotencyBeginResult(bool Exists, string? StoredData, int? StoredStatus, string? StoredBody);

/// <summary>Idempotency-Key store for creating POSTs without a business number (T05: ≥ 24 h).</summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Inserts the key row unless present; an uncommitted concurrent insert blocks until the
    /// first transaction settles (bounded waiting). Returns the stored payload when present.
    /// </summary>
    Task<IdempotencyBeginResult> BeginAsync(
        Guid companyId, string initiatorKey, string operation, string targetId, string key, string essentialData,
        CancellationToken ct);

    Task CompleteAsync(
        Guid companyId, string initiatorKey, string operation, string targetId, string key, int status, string body,
        CancellationToken ct);

    Task<int> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct);
}

/// <summary>Valkey-backed non-personal snapshots: catalog (30 s) and live leaderboard (5 s) (§3.7).</summary>
public interface ICacheSnapshots
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string payload, DateTimeOffset asOfUtc, TimeSpan tolerance, CancellationToken ct);
}

/// <summary>Immutable export bytes in private S3 (T07): per-attempt keys, checksums, ≤ 60 s links.</summary>
public interface IFileStorage
{
    Task<bool> ExistsAsync(string key, CancellationToken ct);

    Task PutAsync(string key, Stream content, CancellationToken ct);

    Task<DownloadLinkRec> CreateDownloadLinkAsync(string key, TimeSpan lifetime, CancellationToken ct);

    Task DeletePrefixAsync(string prefix, CancellationToken ct);

    Task<string> ComputeChecksumAsync(string key, CancellationToken ct);

    Task<long> GetSizeAsync(string key, CancellationToken ct);

    /// <summary>Opens the stored bytes for verification (tests read the CSV back).</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
}

public sealed record ExportSnapshotResult(bool Started, int NewGeneration, IReadOnlyList<Guid> OperationIds);

public interface IExportStore
{
    Task<(UpdateOutcome Outcome, ExportRec? Value)> CreateAsync(
        Guid companyId,
        Guid id,
        Common.ActorContext requester,
        ExportScope scope,
        int? masterId,
        Guid? resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        DateTimeOffset utcNow,
        CancellationToken ct);

    Task<ExportRec?> GetAsync(Guid companyId, Guid exportId, CancellationToken ct);

    /// <summary>Company-agnostic load for the worker (the id is unguessable UUID v7).</summary>
    Task<ExportRec?> LoadAsync(Guid exportId, CancellationToken ct);

    Task<Page<ExportRec>> ListAsync(Guid companyId, int? requestedByMasterId, int limit, string? cursor, CancellationToken ct);

    /// <summary>
    /// Atomic Pending/Error → Forming with snapshot materialization in one REPEATABLE READ
    /// transaction (B36.2): the first successful start fixes the data set forever.
    /// </summary>
    Task<ExportSnapshotResult> TryStartFormingAsync(Guid companyId, Guid exportId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct);

    Task<bool> TryExtendLeaseAsync(Guid companyId, Guid exportId, string leaseOwner, int expectedGeneration, CancellationToken ct);

    Task<bool> TryMarkReadyAsync(
        Guid companyId, Guid exportId, string leaseOwner, int expectedGeneration, string s3Key, string s3Version,
        long sizeBytes, string checksum, DateTimeOffset utcNow, CancellationToken ct);

    Task<bool> TryMarkErrorAsync(
        Guid companyId, Guid exportId, string leaseOwner, int expectedGeneration, string errorDetail, CancellationToken ct);

    Task<bool> MarkDeletedAsync(Guid companyId, Guid exportId, Common.ActorContext actor, DateTimeOffset utcNow, CancellationToken ct);

    /// <summary>Exports with a cleanup intent — the cleanup worker sweeps their objects (§3.6).</summary>
    Task<IReadOnlyList<Guid>> ListCleanupIntentsAsync(CancellationToken ct);

    Task ClearCleanupIntentAsync(Guid exportId, CancellationToken ct);

    Task<IReadOnlyList<Guid>> GetSnapshotOperationIdsAsync(Guid exportId, CancellationToken ct);

    Task<DateTimeOffset?> GetFrozenAtAsync(Guid exportId, CancellationToken ct);

    Task<int> GetCurrentGenerationAsync(Guid exportId, CancellationToken ct);

    /// <summary>Schedules the formation job for an export (atomic with the order when possible).</summary>
    Task ScheduleFormationAsync(Guid exportId, DateTimeOffset availableAt, CancellationToken ct);

    Task<DownloadLinkRec?> FindActiveLinkAsync(Guid exportId, string idemKey, CancellationToken ct);
}
