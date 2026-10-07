namespace Motiva.Application.Ports;

public enum UpdateOutcome
{
    Ok,
    NotFound,
    VersionMismatch,
    AlreadyExists,
    InvalidTransition,
}

/// <summary>Profiles, audience tags and activity (B01, B05) — profile plus wallet created in one transaction.</summary>
public interface IEmployeeDirectory
{
    Task<EmployeeRec?> GetAsync(Guid companyId, int masterId, CancellationToken ct);

    Task<Page<EmployeeRec>> ListAsync(Guid companyId, bool? active, int limit, string? cursor, CancellationToken ct);

    Task<UpdateOutcome> CreateAsync(
        Guid companyId, int masterId, IReadOnlyList<string> tags, DateTimeOffset utcNow, CancellationToken ct);

    Task<UpdateOutcome> PatchAsync(
        Guid companyId,
        int masterId,
        int expectedVersion,
        bool? isActive,
        IReadOnlyList<string>? tags,
        CancellationToken ct);
}

public interface IResourceDirectory
{
    Task<ResourceRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct);

    Task<ResourceRec?> GetByCodeAsync(Guid companyId, string codeNorm, CancellationToken ct);

    Task<Page<ResourceRec>> ListAsync(Guid companyId, ResourceStatus? status, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, ResourceRec? Value)> CreateAsync(
        Guid companyId, Guid id, string codeNorm, string name, DateTimeOffset utcNow, CancellationToken ct);

    Task<(UpdateOutcome Outcome, ResourceRec? Value)> PatchAsync(
        Guid companyId, Guid id, int expectedVersion, string? name, ResourceStatus? status, CancellationToken ct);

    /// <summary>Lists active resources by ids for wallet views and export headers.</summary>
    Task<IReadOnlyList<ResourceRec>> ListByIdsAsync(Guid companyId, IReadOnlyList<Guid> ids, CancellationToken ct);
}

public interface IAchievementDirectory
{
    Task<AchievementRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct);

    Task<Page<AchievementRec>> ListAsync(Guid companyId, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, AchievementRec? Value)> CreateAsync(
        Guid companyId, Guid id, string codeNorm, string name, string? description, CancellationToken ct);

    Task<(UpdateOutcome Outcome, AchievementRec? Value)> PatchAsync(
        Guid companyId, Guid id, int expectedVersion, string? name, string? description, CancellationToken ct);
}

public interface IPurchaseSystemDirectory
{
    Task<PurchaseSystemRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct);

    Task<Page<PurchaseSystemRec>> ListAsync(Guid companyId, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, PurchaseSystemRec? Value)> CreateAsync(
        Guid companyId, Guid id, string codeNorm, string name, IReadOnlyList<Guid> acceptedResourceIds, CancellationToken ct);

    Task<(UpdateOutcome Outcome, PurchaseSystemRec? Value)> PatchAsync(
        Guid companyId,
        Guid id,
        int expectedVersion,
        string? name,
        ContentStatus? status,
        IReadOnlyList<Guid>? acceptedResourceIds,
        CancellationToken ct);
}

public interface IIntegrationDirectory
{
    Task<IntegrationGrantRec?> GetAsync(Guid companyId, Guid id, CancellationToken ct);

    Task<Page<IntegrationGrantRec>> ListAsync(Guid companyId, string? subject, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, IntegrationGrantRec? Value)> CreateAsync(
        Guid companyId,
        Guid id,
        string subject,
        GrantKind kind,
        Guid? campaignId,
        Guid? resourceId,
        Guid? purchaseSystemId,
        DateTimeOffset utcNow,
        CancellationToken ct);

    Task<UpdateOutcome> RevokeAsync(Guid companyId, Guid id, int expectedVersion, DateTimeOffset utcNow, CancellationToken ct);

    /// <summary>An active grant for an exact target combination, or null (B04: any unissued permission is denied).</summary>
    Task<IntegrationGrantRec?> FindActiveAsync(
        Guid companyId, string subject, GrantKind kind, Guid? campaignId, Guid? resourceId, Guid? purchaseSystemId, CancellationToken ct);

    /// <summary>All active Spend grants of a subject: (purchaseSystemId, resourceId) pairs granted as a whole.</summary>
    Task<IReadOnlyList<(Guid PurchaseSystemId, Guid ResourceId)>> ListActiveSpendPairsAsync(
        Guid companyId, string subject, CancellationToken ct);
}
