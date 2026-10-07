using Microsoft.EntityFrameworkCore;

namespace Motiva.Infrastructure.Persistence;

// EF entities are internal storage detail: DTOs of the API layer never mirror them (T02).

public sealed class CompanyRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
}

public sealed class EmployeeRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public List<EmployeeTagRow> Tags { get; set; } = new();
}

public sealed class EmployeeTagRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public string Tag { get; set; } = string.Empty;
}

public sealed class WalletRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WalletBalanceRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public Guid ResourceId { get; set; }
    public long Balance { get; set; }
}

public sealed class ResourceRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string CodeNorm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AchievementRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string CodeNorm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class PurchaseSystemRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string CodeNorm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int Version { get; set; } = 1;
    public List<PurchaseSystemResourceRow> AcceptedResources { get; set; } = new();
}

public sealed class PurchaseSystemResourceRow
{
    public Guid PurchaseSystemId { get; set; }
    public Guid ResourceId { get; set; }
}

public sealed class IntegrationGrantRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid? CampaignId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? PurchaseSystemId { get; set; }
    public string Status { get; set; } = "Active";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class CampaignRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string CodeNorm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Season { get; set; }
    public int OwnerMasterId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = "Draft";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public List<CampaignTagRow> Tags { get; set; } = new();
}

public sealed class CampaignTagRow
{
    public Guid CampaignId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
}

public sealed class CampaignResourceRow
{
    public Guid CampaignId { get; set; }
    public Guid ResourceId { get; set; }
}

public sealed class StreamRow
{
    public Guid CampaignId { get; set; }
    public Guid Id { get; set; }
    public string CodeNorm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int Version { get; set; } = 1;
}

public sealed class TaskRow
{
    public Guid StreamId { get; set; }
    public Guid Id { get; set; }
    public string CodeNorm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long Goal { get; set; }
    public string Period { get; set; } = "Month";
    public long StreamPoints { get; set; }
    public string Status { get; set; } = "Active";
    public int Version { get; set; } = 1;
    public List<TaskTagRow> Tags { get; set; } = new();
    public List<TaskRewardItemRow> RewardItems { get; set; } = new();
}

public sealed class TaskTagRow
{
    public Guid TaskId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
}

public sealed class TaskRewardItemRow
{
    public Guid TaskId { get; set; }
    public Guid ResourceId { get; set; }
    public long Amount { get; set; }
}

public sealed class MilestoneRow
{
    public Guid StreamId { get; set; }
    public Guid Id { get; set; }
    public long Threshold { get; set; }
    public Guid AchievementId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class ChallengeRow
{
    public Guid CampaignId { get; set; }
    public Guid StreamId { get; set; }
    public Guid Id { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class BudgetRow
{
    public Guid CampaignId { get; set; }
    public Guid ResourceId { get; set; }
    public long AllocatedTotal { get; set; }
    public long SpentTotal { get; set; }
    public long ReturnedTotal { get; set; }
}

public sealed class OperationRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? RefusalCode { get; set; }
    public string InitiatorType { get; set; } = string.Empty;
    public string InitiatorKey { get; set; } = string.Empty;
    public int? InitiatorMasterId { get; set; }
    public string? InitiatorSubject { get; set; }
    public bool InitiatorIsAdmin { get; set; }
    public int? MasterId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? PurchaseSystemId { get; set; }
    public Guid? OriginalOperationId { get; set; }
    public string? Reason { get; set; }
    public string SourceNumber { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? EssentialData { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public List<OperationItemRow> Items { get; set; } = new();
}

public sealed class OperationItemRow
{
    public Guid OperationId { get; set; }
    public Guid ResourceId { get; set; }
    public string ResourceCode { get; set; } = string.Empty;
    public long Amount { get; set; }
    public bool IsDebit { get; set; }
}

public sealed class ProgressEventRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string SourceSubject { get; set; } = string.Empty;
    public string EventNumber { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? RejectReason { get; set; }
    public int MasterId { get; set; }
    public Guid TaskId { get; set; }
    public DateTimeOffset? PeriodStart { get; set; }
    public long TransmittedDelta { get; set; }
    public long CreditedDelta { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public Guid? CompletionId { get; set; }
    public long? StreamPointsAdded { get; set; }
    public string? RewardOutcome { get; set; }
    public Guid? RewardOperationId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? EssentialData { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
}

public sealed class ProgressStateRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public Guid TaskId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public long Current { get; set; }
}

public sealed class CompletionRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public Guid TaskId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public Guid Id { get; set; }
    public long StreamPoints { get; set; }
    public Guid? AwardOperationId { get; set; }
    public string RewardOutcome { get; set; } = "NotProvided";
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class StreamPointsRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public Guid StreamId { get; set; }
    public int Season { get; set; }
    public long Points { get; set; }
}

public sealed class AchievementGrantRow
{
    public Guid CompanyId { get; set; }
    public int MasterId { get; set; }
    public Guid AchievementId { get; set; }
    public int Season { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

public sealed class ChallengeScoreRow
{
    public Guid ChallengeId { get; set; }
    public int MasterId { get; set; }
    public long Score { get; set; }
}

public sealed class ChallengeResultRow
{
    public Guid ChallengeId { get; set; }
    public int MasterId { get; set; }
    public long Score { get; set; }
    public int Place { get; set; }
    public DateTimeOffset FinalizedAt { get; set; }
}

public sealed class ExportRequestRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public int RequestedByMasterId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public int? TargetMasterId { get; set; }
    public Guid? ResourceId { get; set; }
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public string Status { get; set; } = "Pending";
    public int Generation { get; set; }
    public DateTimeOffset? FrozenAt { get; set; }
    public bool SnapshotSaved { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public string? S3Key { get; set; }
    public string? S3Version { get; set; }
    public long? SizeBytes { get; set; }
    public string? Checksum { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public bool CleanupIntent { get; set; }
}

public sealed class ExportOperationIdRow
{
    public Guid ExportId { get; set; }
    public Guid OperationId { get; set; }
}

public sealed class DownloadLinkRow
{
    public Guid Id { get; set; }
    public Guid ExportId { get; set; }
    public string IdemKey { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IdempotencyKeyRow
{
    public Guid CompanyId { get; set; }
    public string InitiatorKey { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? EssentialData { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OutboxJobRow
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset AvailableAt { get; set; }
    public int Attempts { get; set; }
    public string Status { get; set; } = "Pending";
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class AuditRecordRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public int? ActorMasterId { get; set; }
    public string? ActorSubject { get; set; }
    public bool ActorIsAdmin { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}
