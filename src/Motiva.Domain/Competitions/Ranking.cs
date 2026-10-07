namespace Motiva.Domain.Competitions;

/// <summary>
/// Competition ranking (B32): a higher score is better; equal scores share a place and the
/// next distinct score gets a place equal to its 1-based position (100, 100, 90 → 1, 1, 3).
/// Rows with equal scores are ordered by ascending masterId.
/// </summary>
public static class Ranking
{
    public static IReadOnlyList<RankedRow> Rank(IEnumerable<(int MasterId, long Score)> rows)
    {
        var ordered = rows
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.MasterId)
            .ToList();
        var result = new List<RankedRow>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var place = i + 1;
            if (i > 0 && ordered[i].Score == ordered[i - 1].Score)
            {
                place = result[i - 1].Place;
            }

            result.Add(new RankedRow(ordered[i].MasterId, ordered[i].Score, place));
        }

        return result;
    }

    public sealed record RankedRow(int MasterId, long Score, int Place);
}
