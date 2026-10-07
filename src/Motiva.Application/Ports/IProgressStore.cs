using Motiva.Domain.Periods;

namespace Motiva.Application.Ports;

public sealed record ProgressStateRec(Guid TaskId, int MasterId, DateTimeOffset PeriodStart, long Current);

public sealed record CompletionRec(
    Guid CompanyId, Guid Id, int MasterId, Guid TaskId, DateTimeOffset PeriodStart, long StreamPointsAdded,
    RewardOutcome RewardOutcome, Guid? RewardOperationId, DateTimeOffset CompletedAtUtc);

public sealed record ChallengeScoreRec(Guid ChallengeId, int MasterId, long Score);

public interface IProgressLog
{
    Task<ProgressEventRec?> FindByNumberAsync(Guid companyId, string sourceSubject, string eventNumber, CancellationToken ct);

    Task InsertEventAsync(ProgressEventRec progressEvent, CancellationToken ct);

    Task<Page<ProgressEventRec>> ListForEmployeeAsync(
        Guid companyId,
        int masterId,
        int limit,
        string? cursor,
        DateTimeOffset? from,
        DateTimeOffset? toUtc,
        Guid? campaignId,
        Guid? taskId,
        CancellationToken ct);

    Task<Page<ProgressEventRec>> ListForCompanyAsync(
        Guid companyId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, CancellationToken ct);

    /// <summary>Insert-then-lock of the (company, master, task, period) progress row (§3.1 first-aggregate protocol).</summary>
    Task<ProgressStateRec> LockStateAsync(
        Guid companyId, int masterId, Guid taskId, DateTimeOffset periodStart, long initialCurrent, CancellationToken ct);

    Task SaveStateAsync(Guid companyId, int masterId, Guid taskId, DateTimeOffset periodStart, long current, CancellationToken ct);

    /// <summary>Inserts a completion; returns false when one already exists for (master, task, period) (B15.1).</summary>
    Task<bool> TryInsertCompletionAsync(CompletionRec completion, CancellationToken ct);

    Task<CompletionRec?> FindCompletionAsync(
        Guid companyId, int masterId, Guid taskId, DateTimeOffset periodStart, CancellationToken ct);

    /// <summary>Insert-then-lock of stream season points (§3.1).</summary>
    Task<long> LockStreamPointsAsync(Guid companyId, int masterId, Guid streamId, int season, CancellationToken ct);

    Task AddStreamPointsAsync(Guid companyId, int masterId, Guid streamId, int season, long delta, CancellationToken ct);

    /// <summary>Points before this completion, to evaluate milestones jointly (§3.1).</summary>
    Task<IReadOnlyList<MilestoneRec>> GetCrossedMilestonesAsync(
        Guid companyId, Guid streamId, long pointsBefore, long pointsAfter, CancellationToken ct);

    /// <summary>Achievement grant; false when already granted this season (B30.2).</summary>
    Task<bool> TryGrantAchievementAsync(
        Guid companyId, int masterId, Guid achievementId, int season, DateTimeOffset grantedAt, CancellationToken ct);

    Task<Page<AchievementGrantRec>> ListAchievementsAsync(
        Guid companyId, int masterId, int? season, int limit, string? cursor, CancellationToken ct);

}

public interface ICompetitionBoard
{
    /// <summary>Advisory transaction locks for challenge ids in ascending order (§3.2).</summary>
    Task LockChallengesAsync(IReadOnlyList<Guid> challengeIds, CancellationToken ct);

    /// <summary>Adds the credited delta and registers the participant (score 0 allowed, B31.4).</summary>
    Task AddScoreAsync(Guid challengeId, int masterId, long creditedDelta, CancellationToken ct);

    Task<IReadOnlyList<ChallengeScoreRec>> ListScoresAsync(Guid challengeId, CancellationToken ct);

    /// <summary>Immutable final results; written once at finalization (B33.3).</summary>
    Task SaveResultsAsync(Guid challengeId, IReadOnlyList<RankingRowRec> results, DateTimeOffset finalizedAt, CancellationToken ct);

    Task<bool> HasResultsAsync(Guid challengeId, CancellationToken ct);

    Task<IReadOnlyList<RankingRowRec>> GetResultsAsync(Guid challengeId, CancellationToken ct);

    Task<RankingRowRec?> GetOwnResultAsync(Guid challengeId, int masterId, CancellationToken ct);
}

public sealed record RankingRowRec(int MasterId, long Score, int Place);

public sealed record MovementRow(
    Guid OperationId,
    int MasterId,
    string ResourceCode,
    long Amount,
    string Kind,
    Guid? CampaignId,
    Guid? OriginalOperationId,
    DateTimeOffset CreatedAtUtc);

/// <summary>Streams posted wallet movements for CSV export without loading everything at once (T07).</summary>
public interface IExportMovementSource
{
    IAsyncEnumerable<MovementRow> StreamMovementsAsync(
        Guid companyId, IReadOnlyList<Guid> operationIds, Guid? resourceId, CancellationToken ct);
}
