using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>Competition board with pg_advisory_xact_lock keyed by challenge id (§3.2): scoring
/// events and the finalizer serialize on the same lock; the final results table has no update path.</summary>
public sealed class CompetitionStore(MotivaDbContext db) : ICompetitionBoard
{
    public async Task LockChallengesAsync(IReadOnlyList<Guid> challengeIds, CancellationToken ct)
    {
        foreach (var id in challengeIds.OrderBy(x => x))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 0))", ct);
        }
    }

    public async Task AddScoreAsync(Guid challengeId, int masterId, long creditedDelta, CancellationToken ct)
    {
        // Participation with score 0 still creates the participant row (B31.4).
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO challenge_scores (challenge_id, master_id, score)
            VALUES ({challengeId}, {masterId}, {creditedDelta})
            ON CONFLICT (challenge_id, master_id)
                DO UPDATE SET score = challenge_scores.score + EXCLUDED.score
            """, ct);
    }

    public async Task<IReadOnlyList<ChallengeScoreRec>> ListScoresAsync(Guid challengeId, CancellationToken ct)
    {
        var rows = await db.ChallengeScores.Where(s => s.ChallengeId == challengeId).ToListAsync(ct);
        return rows.Select(r => new ChallengeScoreRec(r.ChallengeId, r.MasterId, r.Score)).ToList();
    }

    public async Task SaveResultsAsync(Guid challengeId, IReadOnlyList<RankingRowRec> results, DateTimeOffset finalizedAt, CancellationToken ct)
    {
        if (await db.ChallengeResults.AnyAsync(r => r.ChallengeId == challengeId, ct))
        {
            return; // immutable: the first finalization wins
        }

        db.ChallengeResults.AddRange(results.Select(r => new ChallengeResultRow
        {
            ChallengeId = challengeId,
            MasterId = r.MasterId,
            Score = r.Score,
            Place = r.Place,
            FinalizedAt = finalizedAt,
        }));
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> HasResultsAsync(Guid challengeId, CancellationToken ct)
        => db.ChallengeResults.AnyAsync(r => r.ChallengeId == challengeId, ct);

    public async Task<IReadOnlyList<RankingRowRec>> GetResultsAsync(Guid challengeId, CancellationToken ct)
    {
        var rows = await db.ChallengeResults.Where(r => r.ChallengeId == challengeId)
            .OrderBy(r => r.Place).ThenBy(r => r.MasterId).ToListAsync(ct);
        return rows.Select(r => new RankingRowRec(r.MasterId, r.Score, r.Place)).ToList();
    }

    public async Task<RankingRowRec?> GetOwnResultAsync(Guid challengeId, int masterId, CancellationToken ct)
    {
        var row = await db.ChallengeResults.FindAsync(new object[] { challengeId, masterId }, ct);
        return row is null ? null : new RankingRowRec(row.MasterId, row.Score, row.Place);
    }
}
