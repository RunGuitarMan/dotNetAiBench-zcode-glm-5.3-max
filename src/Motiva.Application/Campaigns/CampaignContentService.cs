using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using DomainPeriod = Motiva.Domain.Periods;

namespace Motiva.Application.Campaigns;

/// <summary>Streams, tasks, milestones and challenges (B08, B11, B12, B29, B31). Content
/// composition is immutable after publication; economic task fields too (§4.5 matrix).
/// Every child mutation raises the campaign aggregate version (ETag, §4.4).</summary>
public sealed class CampaignContentService(
    ICampaignCatalog campaigns,
    IAchievementDirectory achievements,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> CreateStreamAsync(
        ActorContext actor, Guid campaignId, string? rawCode, string name, string? idempotencyKey, CancellationToken ct)
    {
        var campaign = await GetCampaignAsync(actor, campaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        EnsureDraft(campaign);
        var code = Guard.Code(rawCode);
        Guard.Name(name);
        return await CreateChildAsync(actor, campaign, "stream.create", code, idempotencyKey,
            async () =>
            {
                var id = Guid.NewGuid();
                var (outcome, value) = await campaigns.CreateStreamAsync(actor.CompanyId, id, campaignId, code, name, ct);
                return (outcome, value is null ? null : DtoMapper.ToDto(value), (object?)value, "/api/v1/streams/" + id.ToString(), value?.Version ?? 1);
            }, ct);
    }

    public async Task<CommandResponse> PatchStreamAsync(
        ActorContext actor, Guid streamId, string? ifMatch, string? name, ContentStatus? status, CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        if (name is not null)
        {
            Guard.Name(name);
        }
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, streamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await GetCampaignAsync(actor, stream.CampaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        if (status == ContentStatus.Archived && campaign.Status == CampaignStatus.Draft)
        {
            // Archiving content of a not yet published campaign is meaningless; keep the transition to Published state.
            throw new MotivaException(ErrorCode.ConflictState, "Only content of a published campaign can be archived.");
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var (outcome, value) = await campaigns.PatchStreamAsync(actor.CompanyId, streamId, version, name, status, ct);
        outcome.EnsureOk();
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("stream.patched", "Stream", streamId.ToString(), ChangesOf(("name", name), ("status", status?.ToString()))), now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<CommandResponse> CreateTaskAsync(
        ActorContext actor,
        Guid streamId,
        string? rawCode,
        string name,
        string? description,
        long goal,
        DomainPeriod.PeriodKind period,
        long streamPoints,
        IReadOnlyList<RewardItemDto> rewardItems,
        AudienceDto? audience,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, streamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await GetCampaignAsync(actor, stream.CampaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        EnsureDraft(campaign);
        var code = Guard.Code(rawCode);
        Guard.Name(name);
        Guard.Description(description);
        if (goal is < 1 or > Guard.MaxUnit)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "goal must be between 1 and 1000000000.");
        }

        if (streamPoints is < 0 or > Guard.MaxUnit)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "streamPoints must be between 0 and 1000000000.");
        }

        var items = await ValidateRewardItemsAsync(actor, campaign, rewardItems, ct);
        var audienceRule = CampaignsService.NormalizeAudience(audience);
        var essential = CanonicalJson.Serialize(new { code, streamId, goal, period = period.ToString(), streamPoints, items });
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "task.create", streamId.ToString(), idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await campaigns.CreateTaskAsync(
            actor.CompanyId, id, streamId, code, name, description, goal, period, streamPoints, items, audienceRule, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Task code already exists in the stream.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor, new AuditInput("task.created", "Task", id.ToString(), new[] { new AuditChangeRec("code", null, code) }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "task.create", streamId.ToString(), idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/tasks/" + id.ToString());
    }

    public async Task<CommandResponse> PatchTaskAsync(
        ActorContext actor,
        Guid taskId,
        string? ifMatch,
        string? name,
        string? description,
        long? goal,
        DomainPeriod.PeriodKind? period,
        long? streamPoints,
        IReadOnlyList<RewardItemDto>? rewardItems,
        AudienceDto? audience,
        ContentStatus? status,
        CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        var task = await campaigns.GetTaskAsync(actor.CompanyId, taskId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, task.StreamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await GetCampaignAsync(actor, stream.CampaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        var economicChange = goal is not null || period is not null || streamPoints is not null || rewardItems is not null;
        if (campaign.Status != CampaignStatus.Draft && economicChange)
        {
            throw new MotivaException(ErrorCode.ConflictState,
                "Economic task fields are immutable after publication (goal, period, streamPoints, rewardItems).");
        }

        if (goal is not null and (< 1 or > Guard.MaxUnit))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "goal must be between 1 and 1000000000.");
        }

        if (streamPoints is not null and (< 0 or > Guard.MaxUnit))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "streamPoints must be between 0 and 1000000000.");
        }

        var items = rewardItems is null ? null : await ValidateRewardItemsAsync(actor, campaign, rewardItems, ct);
        var audienceRule = audience is null ? null : CampaignsService.NormalizeAudience(audience);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var (outcome, value) = await campaigns.PatchTaskAsync(
            actor.CompanyId, taskId, version, name, description, goal, period, streamPoints, items, audienceRule, status, ct);
        outcome.EnsureOk();
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("task.patched", "Task", taskId.ToString(),
                ChangesOf(("name", name), ("goal", goal?.ToString()), ("period", period?.ToString()),
                    ("streamPoints", streamPoints?.ToString()), ("rewardItems", items is null ? null : "changed"),
                    ("audience", audienceRule is null ? null : "changed"), ("status", status?.ToString()))),
            now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<CommandResponse> CreateMilestoneAsync(
        ActorContext actor, Guid streamId, long threshold, Guid achievementId, string? idempotencyKey, CancellationToken ct)
    {
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, streamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await GetCampaignAsync(actor, stream.CampaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        EnsureDraft(campaign);
        if (threshold is < 1 or > Guard.MaxUnit)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "threshold must be between 1 and 1000000000.");
        }

        _ = await achievements.GetAsync(actor.CompanyId, achievementId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound, "Achievement not found.");

        var essential = CanonicalJson.Serialize(new { streamId, threshold, achievementId });
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "milestone.create", streamId.ToString(), idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await campaigns.CreateMilestoneAsync(actor.CompanyId, id, streamId, threshold, achievementId, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Milestone already exists.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("milestone.created", "Milestone", id.ToString(),
                new[] { new AuditChangeRec("threshold", null, threshold.ToString()) }),
            now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "milestone.create", streamId.ToString(), idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/milestones/" + id.ToString());
    }

    public async Task<CommandResponse> DeleteMilestoneAsync(ActorContext actor, Guid milestoneId, string? ifMatch, CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        var milestone = await campaigns.GetMilestoneAsync(actor.CompanyId, milestoneId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, milestone.StreamId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await GetCampaignAsync(actor, stream.CampaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        EnsureDraft(campaign);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var outcome = await campaigns.DeleteMilestoneAsync(actor.CompanyId, milestoneId, version, ct);
        outcome.EnsureOk();
        await audit.AppendAsync(
            actor.CompanyId, actor, new AuditInput("milestone.deleted", "Milestone", milestoneId.ToString(), Array.Empty<AuditChangeRec>()), now, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(204, string.Empty);
    }

    public async Task<CommandResponse> CreateChallengeAsync(
        ActorContext actor, Guid campaignId, Guid streamId, DateTimeOffset startsAt, DateTimeOffset endsAt, string? idempotencyKey, CancellationToken ct)
    {
        var campaign = await GetCampaignAsync(actor, campaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        EnsureDraft(campaign);
        Guard.Range(startsAt, endsAt);
        if (startsAt < campaign.StartsAt || endsAt > campaign.EndsAt)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "The challenge interval must be inside the campaign window.");
        }

        var stream = await campaigns.GetStreamAsync(actor.CompanyId, streamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (stream.CampaignId != campaignId)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "The challenge stream must belong to this campaign.");
        }

        var essential = CanonicalJson.Serialize(new { campaignId, streamId, startsAt = startsAt.UtcDateTime, endsAt = endsAt.UtcDateTime });
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "challenge.create", campaignId.ToString(), idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await campaigns.CreateChallengeAsync(actor.CompanyId, id, campaignId, streamId, startsAt, endsAt, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Identical challenge already exists.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("challenge.created", "Challenge", id.ToString(), new[] { new AuditChangeRec("streamId", null, streamId.ToString()) }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "challenge.create", campaignId.ToString(), idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/challenges/" + id.ToString());
    }

    public async Task<CommandResponse> DeleteChallengeAsync(ActorContext actor, Guid challengeId, string? ifMatch, CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        var challenge = await campaigns.GetChallengeAsync(actor.CompanyId, challengeId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await GetCampaignAsync(actor, challenge.CampaignId, ct);
        CampaignsService.EnsureCanConfigure(actor, campaign);
        EnsureDraft(campaign);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var outcome = await campaigns.DeleteChallengeAsync(actor.CompanyId, challengeId, version, ct);
        outcome.EnsureOk();
        await audit.AppendAsync(
            actor.CompanyId, actor, new AuditInput("challenge.deleted", "Challenge", challengeId.ToString(), Array.Empty<AuditChangeRec>()), now, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(204, string.Empty);
    }

    public async Task<StreamRec> GetStreamAsync(ActorContext actor, Guid streamId, CancellationToken ct)
    {
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, streamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureContentVisibleAsync(actor, stream.CampaignId, ct);
        return stream;
    }

    public async Task<Page<StreamRec>> ListStreamsAsync(ActorContext actor, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        await EnsureContentVisibleAsync(actor, campaignId, ct);
        return await campaigns.ListStreamsAsync(actor.CompanyId, campaignId, limit, cursor, ct);
    }

    public async Task<TaskRec> GetTaskAsync(ActorContext actor, Guid taskId, CancellationToken ct)
    {
        var task = await campaigns.GetTaskAsync(actor.CompanyId, taskId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, task.StreamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureContentVisibleAsync(actor, stream.CampaignId, ct);
        return task;
    }

    public async Task<Page<TaskRec>> ListTasksAsync(ActorContext actor, Guid streamId, int limit, string? cursor, CancellationToken ct)
    {
        var stream = await campaigns.GetStreamAsync(actor.CompanyId, streamId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureContentVisibleAsync(actor, stream.CampaignId, ct);
        return await campaigns.ListTasksAsync(actor.CompanyId, streamId, limit, cursor, ct);
    }

    public async Task<MilestoneRec> GetMilestoneAsync(ActorContext actor, Guid milestoneId, CancellationToken ct)
    {
        var milestone = await campaigns.GetMilestoneAsync(actor.CompanyId, milestoneId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureContentVisibleAsync(actor, milestone.StreamId, ct, byStream: true);
        return milestone;
    }

    public async Task<Page<MilestoneRec>> ListMilestonesAsync(ActorContext actor, Guid streamId, int limit, string? cursor, CancellationToken ct)
    {
        await EnsureContentVisibleAsync(actor, streamId, ct, byStream: true);
        return await campaigns.ListMilestonesAsync(actor.CompanyId, streamId, limit, cursor, ct);
    }

    public async Task<ChallengeRec> GetChallengeAsync(ActorContext actor, Guid challengeId, CancellationToken ct)
    {
        var challenge = await campaigns.GetChallengeAsync(actor.CompanyId, challengeId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureContentVisibleAsync(actor, challenge.CampaignId, ct);
        return challenge;
    }

    public async Task<Page<ChallengeRec>> ListChallengesAsync(ActorContext actor, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        await EnsureContentVisibleAsync(actor, campaignId, ct);
        return await campaigns.ListChallengesAsync(actor.CompanyId, campaignId, limit, cursor, ct);
    }

    /// <summary>Content of a published campaign is visible to its audience; drafts only to owner/admin (B09.2, B11).</summary>
    private async Task EnsureContentVisibleAsync(ActorContext actor, Guid campaignIdOrStreamId, CancellationToken ct, bool byStream = false)
    {
        Guid campaignId;
        if (byStream)
        {
            var stream = await campaigns.GetStreamAsync(actor.CompanyId, campaignIdOrStreamId, ct)
                ?? throw new MotivaException(ErrorCode.NotFound);
            campaignId = stream.CampaignId;
        }
        else
        {
            campaignId = campaignIdOrStreamId;
        }

        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (campaign.Status == CampaignStatus.Deleted)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        if (actor.IsAdmin || campaign.OwnerMasterId == actor.MasterId)
        {
            return;
        }

        if (campaign.Status == CampaignStatus.Draft)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }
    }

    private async Task<CampaignRec> GetCampaignAsync(ActorContext actor, Guid campaignId, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (campaign.Status == CampaignStatus.Deleted)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        return campaign;
    }

    private static void EnsureDraft(CampaignRec campaign)
    {
        if (campaign.Status != CampaignStatus.Draft)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Content composition is immutable after publication.");
        }
    }

    private async Task<IReadOnlyList<RewardItemRec>> ValidateRewardItemsAsync(
        ActorContext actor, CampaignRec campaign, IReadOnlyList<RewardItemDto> rewardItems, CancellationToken ct)
    {
        if (rewardItems.Count == 0)
        {
            return Array.Empty<RewardItemRec>();
        }

        var distinct = rewardItems.Select(i => i.ResourceId).Distinct().ToArray();
        if (distinct.Length != rewardItems.Count)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "Each resource may appear once in the reward list.");
        }

        foreach (var item in rewardItems)
        {
            Guard.PositiveAmount(item.Amount);
        }

        var campaignResourceIds = (await campaigns.ListCampaignResourceIdsAsync(actor.CompanyId, campaign.Id, ct)).ToHashSet();
        if (distinct.Any(id => !campaignResourceIds.Contains(id)))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "Reward resources must be part of the campaign set.");
        }

        return rewardItems.Select(i => new RewardItemRec(i.ResourceId, i.Amount)).ToArray();
    }

    private async Task<CommandResponse> CreateChildAsync(
        ActorContext actor,
        CampaignRec campaign,
        string operation,
        string code,
        string? idempotencyKey,
        Func<Task<(UpdateOutcome Outcome, object? Dto, object? Rec, string Location, int Version)>> create,
        CancellationToken ct)
    {
        var essential = CanonicalJson.Serialize(new { code, campaignId = campaign.Id });
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, operation, campaign.Id.ToString(), idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body);
        }

        var (outcome, dto, _, location, _) = await create();
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Code already exists in the parent scope.");
        }

        var body = CanonicalJson.Serialize(dto);
        await gate.CompleteAsync(actor.CompanyId, actor, operation, campaign.Id.ToString(), idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, location);
    }

    private static AuditChangeRec[] ChangesOf(params (string Field, string? Value)[] fields)
    {
        return fields.Where(f => f.Value is not null)
            .Select(f => new AuditChangeRec(f.Field, null, f.Value))
            .ToArray();
    }
}
