using Motiva.Application.Administration;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using NodaTime;

namespace Motiva.Application.Campaigns;

/// <summary>Campaign aggregate root (B08–B10, B18): draft settings, publication with the
/// AMB-12 content check, immutable economic fields after publication, aggregate-level
/// strong ETag (campaigns.version), draft tombstone delete preserving financial history.</summary>
public sealed class CampaignsService(
    CurrentRights rights,
    ICampaignCatalog campaigns,
    IEmployeeDirectory employees,
    IResourceDirectory resources,
    IAchievementDirectory achievementsDirectory,
    ICompanyDirectory companies,
    IBackgroundJobs jobs,
    IAuditLog audit,
    IUnitOfWork uow,
    IdempotencyGate gate,
    TimeProvider timeProvider)
{
    private const string locationPrefix = "/api/v1/campaigns/";

    public async Task<CommandResponse> CreateAsync(
        ActorContext actor,
        string? rawCode,
        string name,
        string? description,
        int ownerMasterId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        AudienceDto? audience,
        string? idempotencyKey,
        CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var code = Guard.Code(rawCode);
        Guard.Name(name);
        Guard.Description(description);
        Guard.MasterId(ownerMasterId);
        Guard.Range(startsAt, endsAt);
        var audienceRule = NormalizeAudience(audience);

        var zone = await GetZoneAsync(actor, ct);
        var seasonStart = Domain.Periods.Seasons.SeasonOf(Instant.FromDateTimeOffset(startsAt), zone);
        var seasonEnd = Domain.Periods.Seasons.SeasonOf(
            Instant.FromDateTimeOffset(endsAt).Minus(Duration.FromMilliseconds(1)), zone);
        if (seasonStart != seasonEnd)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "A campaign must fit into a single season.");
        }

        var owner = await employees.GetAsync(actor.CompanyId, ownerMasterId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound, "Owner profile not found.");
        if (!owner.IsActive)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Owner must be an active employee.");
        }

        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { code, season = seasonStart, name });
        await using var scope = await uow.BeginAsync(ct);
        var echo = await gate.BeginOrEchoAsync(actor.CompanyId, actor, "campaign.create", "-", idempotencyKey, essential, ct);
        if (echo is not null)
        {
            return new CommandResponse(echo.Status, echo.Body, echo.Location is not null ? locationPrefix + echo.Location : null);
        }

        var id = Guid.NewGuid();
        var (outcome, value) = await campaigns.CreateAsync(
            actor.CompanyId,
            new CampaignDraftInput(id, code, name, description, seasonStart, ownerMasterId, startsAt, endsAt, audienceRule, zone.Id),
            now, ct);
        if (outcome != UpdateOutcome.Ok)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Campaign code already exists in the season.");
        }

        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("campaign.created", "Campaign", id.ToString(), new[] { new AuditChangeRec("code", null, code) }), now, ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        await gate.CompleteAsync(actor.CompanyId, actor, "campaign.create", "-", idempotencyKey, 201, body, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/campaigns/" + id.ToString());
    }

    public async Task<CommandResponse> PatchAsync(
        ActorContext actor,
        Guid id,
        string? ifMatch,
        string? name,
        string? description,
        AudienceDto? audience,
        CampaignStatus? status,
        CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        if (name is not null)
        {
            Guard.Name(name);
        }
        Guard.Description(description);
        var audienceRule = audience is null ? null : NormalizeAudience(audience);
        if (name is null && description is null && audienceRule is null && status is null)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "At least one field is required.");
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureCanConfigureAsync(actor, before, ct);
        if (status == CampaignStatus.Published)
        {
            await EnsurePublishableAsync(actor, before, ct);
        }

        var (outcome, value) = await campaigns.PatchAsync(actor.CompanyId, id, version, name, description, audienceRule, status, ct);
        outcome.EnsureOk();
        var changes = new List<AuditChangeRec>();
        if (name is not null && name != before.Name)
        {
            changes.Add(new AuditChangeRec("name", before.Name, name));
        }

        if (description is not null && description != before.Description)
        {
            changes.Add(new AuditChangeRec("description", before.Description, description));
        }

        if (audienceRule is not null)
        {
            changes.Add(new AuditChangeRec("audience", null, null));
        }

        var action = "campaign.patched";
        if (status == CampaignStatus.Published && before.Status == CampaignStatus.Draft)
        {
            action = "campaign.published";
            changes.Add(new AuditChangeRec("status", "Draft", "Published"));
            await ScheduleChallengeFinalizationAsync(actor.CompanyId, id, ct);
        }

        if (status == CampaignStatus.Archived)
        {
            action = "campaign.archived";
            changes.Add(new AuditChangeRec("status", before.Status.ToString(), "Archived"));
        }

        await audit.AppendAsync(actor.CompanyId, actor, new AuditInput(action, "Campaign", id.ToString(), changes), now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<CommandResponse> DeleteDraftAsync(ActorContext actor, Guid id, string? ifMatch, CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var campaign = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureCanConfigureAsync(actor, campaign, ct);
        var outcome = await campaigns.DeleteDraftAsync(actor.CompanyId, id, version, ct);
        outcome.EnsureOk();
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("campaign.deleted", "Campaign", id.ToString(), new[] { new AuditChangeRec("status", campaign.Status.ToString(), "Deleted") }),
            now, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(204, string.Empty);
    }

    public async Task<CommandResponse> PutOwnerAsync(ActorContext actor, Guid id, string? ifMatch, int ownerMasterId, CancellationToken ct)
    {
        await rights.EnsureAdminAsync(actor, ct);
        var version = ETags.ParseRequired(ifMatch);
        Guard.MasterId(ownerMasterId);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var before = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var candidate = await employees.GetAsync(actor.CompanyId, ownerMasterId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound, "Owner profile not found.");
        if (!candidate.IsActive)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Owner candidate must be an active employee.");
        }

        var (outcome, value) = await campaigns.PutOwnerAsync(actor.CompanyId, id, version, ownerMasterId, ct);
        outcome.EnsureOk();
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("campaign.owner-changed", "Campaign", id.ToString(),
                new[] { new AuditChangeRec("ownerMasterId", before.OwnerMasterId.ToString(), ownerMasterId.ToString()) }),
            now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(value!));
        return new CommandResponse(200, body, ETag: ETags.Format(value!.Version));
    }

    public async Task<CommandResponse> PutResourcesAsync(
        ActorContext actor, Guid id, string? ifMatch, IReadOnlyList<Guid> resourceIds, CancellationToken ct)
    {
        var version = ETags.ParseRequired(ifMatch);
        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var campaign = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await EnsureCanConfigureAsync(actor, campaign, ct);
        if (campaign.Status != CampaignStatus.Draft)
        {
            throw new MotivaException(ErrorCode.ConflictState, "The resource set is immutable after publication.");
        }

        var distinct = resourceIds.Distinct().ToArray();
        var found = await resources.ListByIdsAsync(actor.CompanyId, distinct, ct);
        if (found.Count != distinct.Length)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "resourceIds must reference company resources.");
        }

        if (found.Any(r => r.Status == ResourceStatus.Archived))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "Archived resources cannot be added.");
        }

        // Existing task rewards must stay inside the new set (B09.5 immutability of composition).
        foreach (var stream in (await campaigns.ListStreamsAsync(actor.CompanyId, id, 100, null, ct)).Items)
        {
            var tasksPage = await campaigns.ListTasksAsync(actor.CompanyId, stream.Id, 100, null, ct);
            foreach (var task in tasksPage.Items)
            {
                if (task.RewardItems.Any(item => !distinct.Contains(item.ResourceId)))
                {
                    throw new MotivaException(ErrorCode.ConflictState,
                        "Task reward items reference resources outside the new set.");
                }
            }
        }

        var (outcome, value) = await campaigns.PutResourcesAsync(actor.CompanyId, id, version, distinct, ct);
        outcome.EnsureOk();
        var updated = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("campaign.resources-set", "Campaign", id.ToString(),
                new[] { new AuditChangeRec("resourceIds", null, string.Join(",", distinct.OrderBy(r => r))) }),
            now, ct);
        await scope.CommitAsync(ct);
        var body = CanonicalJson.Serialize(new { items = value!.Select(DtoMapper.ToDto).ToArray() });
        return new CommandResponse(200, body, ETag: ETags.Format(updated.Version));
    }

    public async Task<IReadOnlyList<CampaignResourceRec>> ListResourcesAsync(ActorContext actor, Guid id, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (campaign.Status == CampaignStatus.Deleted)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        await EnsureCanConfigureAsync(actor, campaign, ct);
        return await campaigns.ListResourcesAsync(actor.CompanyId, id, ct);
    }

    public async Task<CampaignRec> GetAsync(ActorContext actor, Guid id, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, id, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (campaign.Status == CampaignStatus.Deleted)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        if (campaign.Status == CampaignStatus.Draft)
        {
            if (!actor.IsAdmin && campaign.OwnerMasterId != actor.MasterId)
            {
                // Drafts are visible only to the owner and admins (B09.2).
                throw new MotivaException(ErrorCode.NotFound);
            }
        }

        return campaign;
    }

    /// <summary>Owner or admin may configure the campaign; the right follows the current owner
    /// and requires an active profile — including for replays (B05.1, §3.3).</summary>
    public async Task EnsureCanConfigureAsync(ActorContext actor, CampaignRec campaign, CancellationToken ct)
    {
        await rights.EnsureOwnerOrAdminAsync(actor, campaign, ct);
    }

    internal async Task EnsurePublishableAsync(ActorContext actor, CampaignRec campaign, CancellationToken ct)
    {
        if (campaign.Status != CampaignStatus.Draft)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Only a draft can be published.");
        }

        var problems = new List<string>();
        var owner = await employees.GetAsync(actor.CompanyId, campaign.OwnerMasterId, ct);
        if (owner is null || !owner.IsActive)
        {
            problems.Add("owner must be an active employee");
        }

        var resourceIds = (await campaigns.ListCampaignResourceIdsAsync(actor.CompanyId, campaign.Id, ct)).ToHashSet();
        var achievementIds = new HashSet<Guid>();
        string? achievementCursor = null;
        do
        {
            var page = await achievementsDirectory.ListAsync(actor.CompanyId, 100, achievementCursor, ct);
            foreach (var achievement in page.Items)
            {
                achievementIds.Add(achievement.Id);
            }

            achievementCursor = page.NextCursor;
        }
        while (achievementCursor is not null);

        string? streamCursor = null;
        do
        {
            var streamPage = await campaigns.ListStreamsAsync(actor.CompanyId, campaign.Id, 100, streamCursor, ct);
            foreach (var stream in streamPage.Items)
            {
                var tasks = await campaigns.ListTasksAsync(actor.CompanyId, stream.Id, 100, null, ct);
                foreach (var task in tasks.Items)
                {
                    foreach (var item in task.RewardItems)
                    {
                        if (!resourceIds.Contains(item.ResourceId))
                        {
                            problems.Add("task " + task.Code + " rewards a resource outside the campaign set");
                        }
                    }
                }

                var milestones = await campaigns.ListMilestonesAsync(actor.CompanyId, stream.Id, 100, null, ct);
                foreach (var milestone in milestones.Items)
                {
                    if (!achievementIds.Contains(milestone.AchievementId))
                    {
                        problems.Add("milestone of stream " + stream.Code + " references an unknown achievement");
                    }
                }
            }

            streamCursor = streamPage.NextCursor;
        }
        while (streamCursor is not null);

        var challengeList = await campaigns.ListChallengesOfCampaignAsync(actor.CompanyId, campaign.Id, ct);
        foreach (var challenge in challengeList)
        {
            var stream = await campaigns.GetStreamAsync(actor.CompanyId, challenge.StreamId, ct);
            if (stream is null || stream.CampaignId != campaign.Id)
            {
                problems.Add("challenge must target a stream of this campaign");
            }

            if (challenge.StartsAt < campaign.StartsAt || challenge.EndsAt > campaign.EndsAt || challenge.StartsAt >= challenge.EndsAt)
            {
                problems.Add("challenge interval must be inside the campaign window");
            }
        }

        if (problems.Count > 0)
        {
            throw new MotivaException(ErrorCode.ConflictPublishCheck, "Publish check failed: " + string.Join("; ", problems));
        }
    }

    private async Task ScheduleChallengeFinalizationAsync(Guid companyId, Guid campaignId, CancellationToken ct)
    {
        foreach (var challenge in await campaigns.ListChallengesOfCampaignAsync(companyId, campaignId, ct))
        {
            await jobs.EnqueueAsync(
                "finalize-challenge",
                CanonicalJson.Serialize(new ChallengePayload(companyId, challenge.Id, challenge.EndsAt)),
                challenge.EndsAt,
                ct);
        }
    }

    internal async Task<DateTimeZone> GetZoneAsync(ActorContext actor, CancellationToken ct)
    {
        var company = await companies.GetAsync(actor.CompanyId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound, "Company not found.");
        return DateTimeZoneProviders.Tzdb[company.TimeZoneId];
    }

    internal static Domain.Audiences.AudienceRule NormalizeAudience(AudienceDto? audience)
    {
        if (audience is null)
        {
            return Domain.Audiences.AudienceRule.Unrestricted;
        }

        var tags = audience.Any.Concat(audience.All).Concat(audience.None).ToArray();
        Guard.Tags(tags);
        return DtoMapper.ToDomain(audience);
    }

    private sealed record ChallengePayload(Guid CompanyId, Guid ChallengeId, DateTimeOffset EndsAt);
}
