using Motiva.Domain.Audiences;
using Motiva.Domain.Periods;

namespace Motiva.Application.Ports;

public sealed record CampaignDraftInput(
    Guid Id,
    string CodeNorm,
    string Name,
    string? Description,
    int Season,
    int OwnerMasterId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    AudienceRule Audience,
    string TimeZoneId);

public interface ICampaignCatalog
{
    Task<CampaignRec?> GetAsync(Guid companyId, Guid campaignId, CancellationToken ct);

    Task<Page<CampaignRec>> ListAsync(
        Guid companyId, int? season, CampaignStatus? status, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, CampaignRec? Value)> CreateAsync(
        Guid companyId, CampaignDraftInput input, DateTimeOffset utcNow, CancellationToken ct);

    Task<(UpdateOutcome Outcome, CampaignRec? Value)> PatchAsync(
        Guid companyId,
        Guid campaignId,
        int expectedVersion,
        string? name,
        string? description,
        AudienceRule? audience,
        CampaignStatus? status,
        CancellationToken ct);

    /// <summary>Draft-only tombstone delete (§2.3): settings removed, financial history preserved.</summary>
    Task<UpdateOutcome> DeleteDraftAsync(Guid companyId, Guid campaignId, int expectedVersion, CancellationToken ct);

    Task<(UpdateOutcome Outcome, CampaignRec? Value)> PutOwnerAsync(
        Guid companyId, Guid campaignId, int expectedVersion, int ownerMasterId, CancellationToken ct);

    Task<IReadOnlyList<CampaignResourceRec>> ListResourcesAsync(Guid companyId, Guid campaignId, CancellationToken ct);

    Task<(UpdateOutcome Outcome, IReadOnlyList<CampaignResourceRec>? Value)> PutResourcesAsync(
        Guid companyId, Guid campaignId, int expectedVersion, IReadOnlyList<Guid> resourceIds, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListCampaignResourceIdsAsync(Guid companyId, Guid campaignId, CancellationToken ct);

    // Streams
    Task<StreamRec?> GetStreamAsync(Guid companyId, Guid streamId, CancellationToken ct);

    Task<Page<StreamRec>> ListStreamsAsync(Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, StreamRec? Value)> CreateStreamAsync(
        Guid companyId, Guid id, Guid campaignId, string codeNorm, string name, CancellationToken ct);

    Task<(UpdateOutcome Outcome, StreamRec? Value)> PatchStreamAsync(
        Guid companyId, Guid streamId, int expectedVersion, string? name, ContentStatus? status, CancellationToken ct);

    // Tasks
    Task<TaskRec?> GetTaskAsync(Guid companyId, Guid taskId, CancellationToken ct);

    Task<Page<TaskRec>> ListTasksAsync(Guid companyId, Guid streamId, int limit, string? cursor, CancellationToken ct);

    /// <summary>Task listing scoped to the reader's tags (B11): the page contains only tasks
    /// whose own audience matches; the cursor walks the underlying ordering, so sequential
    /// reads reach every visible task and no hidden one.</summary>
    Task<Page<TaskRec>> ListVisibleTasksAsync(
        Guid companyId, Guid streamId, IReadOnlyCollection<string> readerTags, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, TaskRec? Value)> CreateTaskAsync(
        Guid companyId,
        Guid id,
        Guid streamId,
        string codeNorm,
        string name,
        string? description,
        long goal,
        PeriodKind period,
        long streamPoints,
        IReadOnlyList<RewardItemRec> rewardItems,
        AudienceRule audience,
        CancellationToken ct);

    Task<(UpdateOutcome Outcome, TaskRec? Value)> PatchTaskAsync(
        Guid companyId,
        Guid taskId,
        int expectedVersion,
        string? name,
        string? description,
        long? goal,
        PeriodKind? period,
        long? streamPoints,
        IReadOnlyList<RewardItemRec>? rewardItems,
        AudienceRule? audience,
        ContentStatus? status,
        CancellationToken ct);

    // Milestones
    Task<MilestoneRec?> GetMilestoneAsync(Guid companyId, Guid milestoneId, CancellationToken ct);

    Task<Page<MilestoneRec>> ListMilestonesAsync(Guid companyId, Guid streamId, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, MilestoneRec? Value)> CreateMilestoneAsync(
        Guid companyId, Guid id, Guid streamId, long threshold, Guid achievementId, CancellationToken ct);

    Task<UpdateOutcome> DeleteMilestoneAsync(Guid companyId, Guid milestoneId, int expectedVersion, CancellationToken ct);

    // Challenges
    Task<ChallengeRec?> GetChallengeAsync(Guid companyId, Guid challengeId, CancellationToken ct);

    Task<Page<ChallengeRec>> ListChallengesAsync(Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct);

    Task<(UpdateOutcome Outcome, ChallengeRec? Value)> CreateChallengeAsync(
        Guid companyId, Guid id, Guid campaignId, Guid streamId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct);

    Task<UpdateOutcome> DeleteChallengeAsync(Guid companyId, Guid challengeId, int expectedVersion, CancellationToken ct);

    /// <summary>All challenges of a stream (for advisory locking, regardless of state).</summary>
    Task<IReadOnlyList<ChallengeRec>> ListChallengesOfStreamAsync(Guid companyId, Guid streamId, CancellationToken ct);

    /// <summary>Challenges of the stream belonging to a campaign (visibility rules use the campaign).</summary>
    Task<IReadOnlyList<ChallengeRec>> ListChallengesOfCampaignAsync(Guid companyId, Guid campaignId, CancellationToken ct);

    Task MarkChallengeFinalizedAsync(Guid companyId, Guid challengeId, DateTimeOffset finalizedAt, CancellationToken ct);
}
