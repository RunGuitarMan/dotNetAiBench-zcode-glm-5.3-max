using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Dto;

/// <summary>Successful command result: status + canonical body (+ Location/ETag headers).
/// Replay bodies are byte-identical copies of the first response (§3.0).</summary>
public sealed record CommandResponse(int Status, string Body, string? Location = null, string? ETag = null);

public static class DtoMapper
{
    public static AudienceDto ToDto(Domain.Audiences.AudienceRule audience)
    {
        return new AudienceDto(
            audience.Any.OrderBy(t => t).ToArray(),
            audience.All.OrderBy(t => t).ToArray(),
            audience.None.OrderBy(t => t).ToArray());
    }

    public static Domain.Audiences.AudienceRule ToDomain(AudienceDto audience)
    {
        return new Domain.Audiences.AudienceRule(
            new HashSet<string>(audience.Any), new HashSet<string>(audience.All), new HashSet<string>(audience.None));
    }

    public static EmployeeDto ToDto(EmployeeRec rec) => new(rec.MasterId, rec.IsActive, rec.Tags, rec.CreatedAtUtc);

    public static ResourceDto ToDto(ResourceRec rec) => new(rec.Id, rec.Code, rec.Name, rec.Status, rec.CreatedAtUtc);

    public static AchievementDto ToDto(AchievementRec rec) => new(rec.Id, rec.Code, rec.Name, rec.Description);

    public static CampaignDto ToDto(CampaignRec rec)
        => new(rec.Id, rec.Code, rec.Name, rec.Description, rec.Season, rec.OwnerMasterId, rec.StartsAt, rec.EndsAt,
            rec.Status, ToDto(rec.Audience), rec.CreatedAtUtc, rec.PublishedAtUtc);

    public static StreamDto ToDto(StreamRec rec) => new(rec.Id, rec.CampaignId, rec.Code, rec.Name, rec.Status);

    public static RewardItemDto ToDto(RewardItemRec rec) => new(rec.ResourceId, rec.Amount);

    public static TaskDto ToDto(TaskRec rec)
        => new(rec.Id, rec.StreamId, rec.Code, rec.Name, rec.Description, rec.Goal, rec.Period.ToString(),
            rec.StreamPoints, rec.RewardItems.Select(ToDto).ToArray(), ToDto(rec.Audience), rec.Status);

    public static MilestoneDto ToDto(MilestoneRec rec) => new(rec.Id, rec.StreamId, rec.Threshold, rec.AchievementId);

    public static ChallengeDto ToDto(ChallengeRec rec)
        => new(rec.Id, rec.CampaignId, rec.StreamId, rec.StartsAt, rec.EndsAt, rec.FinalizedAtUtc);

    public static CampaignResourceDto ToDto(CampaignResourceRec rec)
        => new(rec.ResourceId, rec.ResourceCode, rec.Name, rec.Status);

    public static BudgetBalanceDto ToDto(BudgetRec rec)
        => new(rec.ResourceId, rec.ResourceCode, rec.AllocatedTotal, rec.SpentTotal, rec.ReturnedTotal, rec.Available);

    public static ActorDto ToDto(ActorContext actor)
        => new(actor.ActorType.ToString(), actor.MasterId, actor.MasterId is null ? actor.Subject : null);

    public static OperationItemDto ToDto(OperationItemRec rec)
        => new(rec.ResourceId, rec.ResourceCode, rec.Amount, rec.IsDebit ? "debit" : "credit");

    public static OperationDto ToDto(OperationRec rec)
        => new(rec.Id, rec.Kind.ToString(), rec.Result.ToString(), rec.RefusalCode?.ToString(), ToDto(rec.Initiator),
            rec.MasterId, rec.CampaignId, rec.PurchaseSystemId, rec.OriginalOperationId, rec.Reason,
            rec.SourceOperationNumber, rec.Items.Select(ToDto).ToArray(), rec.CreatedAtUtc);

    public static WalletBalanceDto ToDto(WalletBalanceRec rec)
        => new(rec.ResourceId, rec.ResourceCode, rec.Balance, rec.ResourceStatus);

    public static AchievementGrantDto ToDto(AchievementGrantRec rec)
        => new(rec.AchievementId, rec.Code, rec.Name, rec.Season, rec.GrantedAtUtc);

    public static IntegrationGrantDto ToDto(IntegrationGrantRec rec)
        => new(rec.Id, rec.Subject, rec.Kind.ToString(), rec.CampaignId, rec.ResourceId, rec.PurchaseSystemId,
            rec.Status.ToString(), rec.CreatedAtUtc, rec.RevokedAtUtc);

    public static PurchaseSystemDto ToDto(PurchaseSystemRec rec)
        => new(rec.Id, rec.Code, rec.Name, rec.Status, rec.AcceptedResourceIds);

    public static ExportDto ToDto(ExportRec rec)
        => new(rec.Id, rec.Scope.ToString(), rec.MasterId, rec.ResourceCode, rec.FromUtc, rec.ToUtc, rec.Status.ToString(),
            rec.CreatedAtUtc, rec.DataFrozenAtUtc, rec.ReadyAtUtc, rec.SizeBytes, rec.Checksum, rec.ErrorDetail);

    public static DownloadLinkDto ToDto(DownloadLinkRec rec) => new(rec.Id, rec.Url, rec.ExpiresAtUtc);

    public static AuditRecordDto ToDto(AuditRec rec)
        => new(rec.Id, ToDto(rec.Actor), rec.Action, rec.EntityType, rec.EntityId,
            rec.Changes.Select(c => new AuditChangeDto(c.Field, c.From, c.To)).ToArray(), rec.AtUtc);
}
