using Motiva.Application.Ports;

namespace Motiva.Application.Dto;

/// <summary>Response DTOs of the API contract (docs/openapi.yaml). They are not EF entities and
/// they are the canonical shape stored in replay bodies.</summary>
public sealed record EmployeeDto(int MasterId, bool IsActive, IReadOnlyList<string> Tags, DateTimeOffset CreatedAtUtc);

public sealed record ResourceDto(Guid Id, string Code, string Name, ResourceStatus Status, DateTimeOffset CreatedAtUtc);

public sealed record AchievementDto(Guid Id, string Code, string Name, string? Description);

public sealed record AudienceDto(IReadOnlyList<string> Any, IReadOnlyList<string> All, IReadOnlyList<string> None);

public sealed record CampaignDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    int Season,
    int OwnerMasterId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CampaignStatus Status,
    AudienceDto Audience,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc);

public sealed record StreamDto(Guid Id, Guid CampaignId, string Code, string Name, ContentStatus Status);

public sealed record RewardItemDto(Guid ResourceId, long Amount);

public sealed record TaskDto(
    Guid Id,
    Guid StreamId,
    string Code,
    string Name,
    string? Description,
    long Goal,
    string Period,
    long StreamPoints,
    IReadOnlyList<RewardItemDto> RewardItems,
    AudienceDto Audience,
    ContentStatus Status);

public sealed record MilestoneDto(Guid Id, Guid StreamId, long Threshold, Guid AchievementId);

public sealed record ChallengeDto(
    Guid Id, Guid CampaignId, Guid StreamId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset? FinalizedAtUtc);

public sealed record CampaignResourceDto(Guid ResourceId, string ResourceCode, string Name, ResourceStatus Status);

public sealed record BudgetBalanceDto(
    Guid ResourceId, string ResourceCode, long AllocatedTotal, long SpentTotal, long ReturnedTotal, long Available);

public sealed record ActorDto(string ActorType, int? MasterId, string? Subject);

public sealed record OperationItemDto(Guid ResourceId, string ResourceCode, long Amount, string Direction);

public sealed record OperationDto(
    Guid Id,
    string Kind,
    string Result,
    string? RefusalCode,
    ActorDto Initiator,
    int? MasterId,
    Guid? CampaignId,
    Guid? PurchaseSystemId,
    Guid? OriginalOperationId,
    string? Reason,
    string SourceOperationNumber,
    IReadOnlyList<OperationItemDto> Items,
    DateTimeOffset CreatedAtUtc);

public sealed record RewardDecisionDto(string Outcome, Guid? OperationId);

public sealed record CompletionDto(
    Guid TaskId, DateTimeOffset PeriodStart, long StreamPointsAdded, RewardDecisionDto Reward, DateTimeOffset CompletedAtUtc);

public sealed record ProgressEventResultDto(
    Guid Id,
    string EventNumber,
    string Result,
    string? RejectReason,
    int MasterId,
    Guid TaskId,
    DateTimeOffset? PeriodStartUtc,
    long TransmittedDelta,
    long CreditedDelta,
    DateTimeOffset AcceptedAtUtc,
    CompletionDto? Completion);

public sealed record WalletBalanceDto(Guid ResourceId, string ResourceCode, long Balance, ResourceStatus ResourceStatus);

public sealed record WalletDto(int MasterId, IReadOnlyList<WalletBalanceDto> Balances, DateTimeOffset GeneratedAtUtc);

public sealed record AchievementGrantDto(Guid AchievementId, string Code, string Name, int Season, DateTimeOffset GrantedAtUtc);

public sealed record LeaderboardEntryDto(int MasterId, long Score, int Place);

public sealed record LeaderboardPageDto(IReadOnlyList<LeaderboardEntryDto> Items, string State, DateTimeOffset AsOfUtc);

public sealed record LeaderboardMeDto(int MasterId, bool Participating, long? Score, int? Place, string State, DateTimeOffset AsOfUtc);

public sealed record IntegrationGrantDto(
    Guid Id,
    string Subject,
    string Kind,
    Guid? CampaignId,
    Guid? ResourceId,
    Guid? PurchaseSystemId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record PurchaseSystemDto(Guid Id, string Code, string Name, ContentStatus Status, IReadOnlyList<Guid> AcceptedResourceIds);

public sealed record ExportDto(
    Guid Id,
    string Scope,
    int? MasterId,
    string? ResourceCode,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DataFrozenAtUtc,
    DateTimeOffset? ReadyAtUtc,
    long? SizeBytes,
    string? Checksum,
    string? ErrorDetail);

public sealed record DownloadLinkDto(Guid Id, string Url, DateTimeOffset ExpiresAtUtc);

public sealed record AuditChangeDto(string Field, string? From, string? To);

public sealed record AuditRecordDto(
    Guid Id, ActorDto Actor, string Action, string EntityType, string EntityId,
    IReadOnlyList<AuditChangeDto> Changes, DateTimeOffset AtUtc);

public sealed record TaskProgressDto(
    Guid TaskId, string Code, long Current, long Goal, bool Completed, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

public sealed record MilestoneProgressDto(long Threshold, Guid AchievementId, bool Earned);

public sealed record StreamProgressDto(
    Guid StreamId, string Code, long Points, IReadOnlyList<TaskProgressDto> Tasks, IReadOnlyList<MilestoneProgressDto> Milestones);

public sealed record CampaignProgressDto(Guid CampaignId, int Season, IReadOnlyList<StreamProgressDto> Streams);

public sealed record ParticipantProgressDto(int MasterId, IReadOnlyList<StreamProgressDto> Streams);
