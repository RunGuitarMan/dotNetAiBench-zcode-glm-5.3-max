using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Competitions;

/// <summary>
/// Challenge finalization (§3.2, B33, T07): the job becomes available at endsAt and publishes
/// the immutable result within ~5 s under the same advisory lock as scoring events. Scores are
/// read with a fresh statement after the lock wait (READ COMMITTED, not a stale snapshot).
/// </summary>
public sealed class FinalizeChallengeHandler(
    ICampaignCatalog campaigns,
    ICompetitionBoard board,
    IUnitOfWork uow,
    TimeProvider timeProvider)
{
    public async Task<bool> HandleAsync(Guid companyId, Guid challengeId, CancellationToken ct)
    {
        var challenge = await campaigns.GetChallengeAsync(companyId, challengeId, ct);
        if (challenge is null)
        {
            return true; // removed with the draft — nothing to finalize
        }

        if (challenge.FinalizedAtUtc is not null)
        {
            return true; // idempotent: the result is immutable (B33.3)
        }

        await using var scope = await uow.BeginAsync(ct);
        await board.LockChallengesAsync(new[] { challengeId }, ct);

        // Fresh read after the lock wait: a concurrent event that accepted before endsAt but
        // committed after our first read must be included (B33.2).
        var fresh = await campaigns.GetChallengeAsync(companyId, challengeId, ct);
        if (fresh is null || fresh.FinalizedAtUtc is not null)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        if (now < fresh.EndsAt)
        {
            return false; // not due yet; the dispatcher retries at endsAt
        }

        var scores = await board.ListScoresAsync(challengeId, ct);
        var ranked = Domain.Competitions.Ranking.Rank(scores.Select(s => (s.MasterId, s.Score)));
        await board.SaveResultsAsync(
            challengeId, ranked.Select(r => new RankingRowRec(r.MasterId, r.Score, r.Place)).ToArray(), now, ct);
        await campaigns.MarkChallengeFinalizedAsync(companyId, challengeId, now, ct);
        await scope.CommitAsync(ct);
        return true;
    }
}
