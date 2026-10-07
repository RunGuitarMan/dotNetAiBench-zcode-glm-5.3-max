using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using NodaTime;

namespace Motiva.Application.Competitions;

/// <summary>Leaderboards (B31–B33, H0 Q04): Live from challenge_scores with a 5 s cache; Final
/// from immutable challenge_results. Visibility: current audience until the end of the season
/// (even after campaign end/archive), afterwards only participants with their own historical
/// row, plus owner and admin.</summary>
public sealed class LeaderboardService(
    ICampaignCatalog campaigns,
    IEmployeeDirectory employees,
    ICompanyDirectory companies,
    ICompetitionBoard board,
    ICacheSnapshots cache,
    TimeProvider timeProvider)
{
    public async Task<LeaderboardPageDto> GetPageAsync(ActorContext actor, Guid challengeId, int limit, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        limit = NormalizeLeaderboardLimit(limit);
        var (challenge, campaign) = await LoadVisibleChallengeAsync(actor, challengeId, ct);

        if (challenge.FinalizedAtUtc is not null)
        {
            var rows = await board.GetResultsAsync(challengeId, ct);
            return new LeaderboardPageDto(
                rows.Take(limit).Select(r => new LeaderboardEntryDto(r.MasterId, r.Score, r.Place)).ToArray(),
                "Final", challenge.FinalizedAtUtc.Value);
        }

        var cacheKey = "leaderboard:" + challengeId + ":" + limit;
        var cached = await cache.GetAsync(cacheKey, ct);
        if (cached is not null && CanonicalJson.Deserialize<LeaderboardPageDto>(cached) is { } page)
        {
            return page;
        }

        var asOf = timeProvider.GetUtcNow();
        var liveRows = Domain.Competitions.Ranking.Rank(
            (await board.ListScoresAsync(challengeId, ct)).Select(r => (r.MasterId, r.Score)));
        var result = new LeaderboardPageDto(
            liveRows.Take(limit).Select(r => new LeaderboardEntryDto(r.MasterId, r.Score, r.Place)).ToArray(),
            "Live", asOf);
        var body = CanonicalJson.Serialize(result);
        await cache.SetAsync(cacheKey, body, asOf, TimeSpan.FromSeconds(5), ct);
        return result;
    }

    public async Task<LeaderboardMeDto> GetMeAsync(ActorContext actor, Guid challengeId, CancellationToken ct)
    {
        Authz.EnsureUser(actor);
        var (challenge, campaign) = await LoadVisibleChallengeAsync(actor, challengeId, ct);

        if (challenge.FinalizedAtUtc is not null)
        {
            var own = await board.GetOwnResultAsync(challengeId, actor.MasterId!.Value, ct);
            return new LeaderboardMeDto(
                actor.MasterId!.Value, own is not null, own?.Score, own?.Place, "Final", challenge.FinalizedAtUtc.Value);
        }

        var scores = await board.ListScoresAsync(challengeId, ct);
        var ranked = Domain.Competitions.Ranking.Rank(scores.Select(r => (r.MasterId, r.Score)))
            .FirstOrDefault(r => r.MasterId == actor.MasterId!.Value);
        var asOf = timeProvider.GetUtcNow();
        return new LeaderboardMeDto(
            actor.MasterId!.Value, ranked is not null, ranked?.Score, ranked?.Place, "Live", asOf);
    }

    private static int NormalizeLeaderboardLimit(int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "limit must be between 1 and 100.");
        }

        return limit;
    }

    private async Task<(ChallengeRec, CampaignRec)> LoadVisibleChallengeAsync(ActorContext actor, Guid challengeId, CancellationToken ct)
    {
        var challenge = await campaigns.GetChallengeAsync(actor.CompanyId, challengeId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);
        var campaign = await campaigns.GetAsync(actor.CompanyId, challenge.CampaignId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);

        if (actor.IsAdmin || campaign.OwnerMasterId == actor.MasterId)
        {
            return (challenge, campaign);
        }

        var company = await companies.GetAsync(actor.CompanyId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var zone = DateTimeZoneProviders.Tzdb[company.TimeZoneId];
        var now = Instant.FromDateTimeOffset(timeProvider.GetUtcNow());
        var seasonEnd = Domain.Periods.Seasons.SeasonEnd(campaign.Season, zone);
        if (now < seasonEnd)
        {
            // Current audience reads until the end of the season, even after campaign end/archive (H0 Q04).
            var employee = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
            if (employee is null || !employee.IsActive || !campaign.Audience.Matches(new HashSet<string>(employee.Tags)))
            {
                throw new MotivaException(ErrorCode.AuthzForbidden, "The leaderboard is available to the current campaign audience.");
            }

            return (challenge, campaign);
        }

        // After the season: the employee needs their own historical row of this exact challenge.
        var own = await board.GetOwnResultAsync(challengeId, actor.MasterId!.Value, ct);
        if (own is null)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden,
                "After the season the leaderboard is available to participants, the owner and administrators.");
        }

        return (challenge, campaign);
    }
}
