using Motiva.Domain.Audiences;

namespace Motiva.Application.Ports;

public enum ResourceStatus
{
    Active,
    Archived,
}

public enum CampaignStatus
{
    Draft,
    Published,
    Archived,
    Deleted,
}

public enum ContentStatus
{
    Active,
    Archived,
}

public enum GrantKind
{
    Progress,
    Award,
    Spend,
}

public enum GrantStatus
{
    Active,
    Revoked,
}

public enum OperationKind
{
    BudgetAllocation,
    ManualAward,
    TaskReward,
    Spend,
    AwardReversal,
    SpendReversal,
}

public enum OperationResult
{
    Posted,
    Declined,
}

public enum RefusalCode
{
    InsufficientBudget,
    InsufficientFunds,
    InsufficientBalance,
    ResourceUnavailable,
    RecipientNotActive,
    RecipientNotInAudience,
    OriginalAlreadyReversed,
    OriginalNotPosted,
}

public enum ProgressEventOutcome
{
    Accepted,
    Rejected,
}

public enum ProgressRejectReason
{
    EmployeeNotActive,
    AudienceMismatch,
    CampaignClosed,
    TargetArchived,
}

public enum RewardOutcome
{
    Granted,
    NotProvided,
    DeclinedInsufficientBudget,
    DeclinedResourceUnavailable,
}

public enum ExportStatus
{
    Pending,
    Forming,
    Ready,
    Error,
    Deleted,
}

public enum ExportScope
{
    Own,
    Employee,
    Company,
}

public sealed record EmployeeRec(
    int MasterId,
    bool IsActive,
    IReadOnlyList<string> Tags,
    int Version,
    DateTimeOffset CreatedAtUtc);

public sealed record ResourceRec(
    Guid Id,
    string Code,
    string Name,
    ResourceStatus Status,
    int Version,
    DateTimeOffset CreatedAtUtc);

public sealed record AchievementRec(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    int Version);

public sealed record PurchaseSystemRec(
    Guid Id,
    string Code,
    string Name,
    ContentStatus Status,
    IReadOnlyList<Guid> AcceptedResourceIds,
    int Version);

public sealed record IntegrationGrantRec(
    Guid Id,
    string Subject,
    GrantKind Kind,
    Guid? CampaignId,
    Guid? ResourceId,
    Guid? PurchaseSystemId,
    GrantStatus Status,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record CampaignRec(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    int Season,
    int OwnerMasterId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CampaignStatus Status,
    AudienceRule Audience,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc);

public sealed record StreamRec(
    Guid Id,
    Guid CampaignId,
    string Code,
    string Name,
    ContentStatus Status,
    int Version);

public sealed record RewardItemRec(Guid ResourceId, long Amount);

public sealed record TaskRec(
    Guid Id,
    Guid StreamId,
    string Code,
    string Name,
    string? Description,
    long Goal,
    Domain.Periods.PeriodKind Period,
    long StreamPoints,
    IReadOnlyList<RewardItemRec> RewardItems,
    AudienceRule Audience,
    ContentStatus Status,
    int Version);

public sealed record MilestoneRec(
    Guid Id,
    Guid StreamId,
    long Threshold,
    Guid AchievementId,
    int Version);

public sealed record ChallengeRec(
    Guid Id,
    Guid CampaignId,
    Guid StreamId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset? FinalizedAtUtc,
    int Version);

public sealed record CampaignResourceRec(Guid ResourceId, string ResourceCode, string Name, ResourceStatus Status);

public sealed record BudgetRec(
    Guid CampaignId,
    Guid ResourceId,
    string ResourceCode,
    long AllocatedTotal,
    long SpentTotal,
    long ReturnedTotal)
{
    public long Available => AllocatedTotal - SpentTotal + ReturnedTotal;
}

public sealed record OperationItemRec(Guid ResourceId, string ResourceCode, long Amount, bool IsDebit);

public sealed record OperationRec(
    Guid Id,
    OperationKind Kind,
    OperationResult Result,
    RefusalCode? RefusalCode,
    Common.ActorContext Initiator,
    int? MasterId,
    Guid? CampaignId,
    Guid? PurchaseSystemId,
    Guid? OriginalOperationId,
    string? Reason,
    string SourceOperationNumber,
    IReadOnlyList<OperationItemRec> Items,
    DateTimeOffset CreatedAtUtc,
    string? EssentialData,
    int? ResponseStatus,
    string? ResponseBody);

public sealed record ProgressEventRec(
    Guid CompanyId,
    string SourceSubject,
    Guid Id,
    string EventNumber,
    ProgressEventOutcome Result,
    ProgressRejectReason? RejectReason,
    int MasterId,
    Guid TaskId,
    DateTimeOffset? PeriodStartUtc,
    long TransmittedDelta,
    long CreditedDelta,
    DateTimeOffset AcceptedAtUtc,
    Guid? CompletionId,
    long? StreamPointsAdded,
    RewardOutcome? RewardOutcome,
    Guid? RewardOperationId,
    string? EssentialData,
    int? ResponseStatus,
    string? ResponseBody);

public sealed record WalletBalanceRec(Guid ResourceId, string ResourceCode, long Balance, ResourceStatus ResourceStatus);

public sealed record AchievementGrantRec(Guid AchievementId, string Code, string Name, int Season, DateTimeOffset GrantedAtUtc);

public sealed record ExportRec(
    Guid CompanyId,
    Guid Id,
    int RequestedByMasterId,
    ExportScope Scope,
    int? MasterId,
    string? ResourceCode,
    Guid? ResourceId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    ExportStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DataFrozenAtUtc,
    DateTimeOffset? ReadyAtUtc,
    long? SizeBytes,
    string? Checksum,
    string? ErrorDetail,
    int Generation,
    bool CleanupIntent);

public sealed record DownloadLinkRec(Guid Id, string Url, DateTimeOffset ExpiresAtUtc);

public sealed record AuditChangeRec(string Field, string? From, string? To);

public sealed record AuditRec(
    Guid Id,
    Common.ActorContext Actor,
    string Action,
    string EntityType,
    string EntityId,
    IReadOnlyList<AuditChangeRec> Changes,
    DateTimeOffset AtUtc);
