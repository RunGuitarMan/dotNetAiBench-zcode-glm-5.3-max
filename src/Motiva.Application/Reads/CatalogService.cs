using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using NodaTime;

namespace Motiva.Application.Reads;

/// <summary>Catalog reads with the Valkey snapshot layer (§3.7): non-personal pages cached
/// with a 30 s tolerance, asOfUtc mandatory, expired or dead-born values treated as a miss,
/// bounded wait on cache failure with a PostgreSQL fallback. Personal audience filtering is
/// applied in memory after the fetch; rights are never taken from the cache.</summary>
public sealed class CatalogService(
    ICampaignCatalog campaigns,
    IEmployeeDirectory employees,
    ICompanyDirectory companies,
    IResourceDirectory resources,
    IAchievementDirectory achievements,
    ICacheSnapshots cache,
    TimeProvider timeProvider)
{
    public async Task<(IReadOnlyList<CampaignDto> Items, DateTimeOffset AsOfUtc)> ListCampaignsAsync(
        ActorContext actor, int? season, CampaignStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        var company = await companies.GetAsync(actor.CompanyId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var zone = DateTimeZoneProviders.Tzdb[company.TimeZoneId];
        var currentSeason = Domain.Periods.Seasons.SeasonOf(Instant.FromDateTimeOffset(timeProvider.GetUtcNow()), zone);

        Page<CampaignRec> page;
        if (actor.IsAdmin)
        {
            page = await campaigns.ListAsync(actor.CompanyId, season, status, limit, cursor, ct);
            return (page.Items.Select(DtoMapper.ToDto).ToArray(), timeProvider.GetUtcNow());
        }

        // Employees: published campaigns of the requested/current season, filtered by audience (B11.7).
        var effectiveSeason = season ?? currentSeason;
        var effectiveStatus = status == CampaignStatus.Published ? status : CampaignStatus.Published;
        if (status is not null and not CampaignStatus.Published)
        {
            return (Array.Empty<CampaignDto>(), timeProvider.GetUtcNow());
        }

        var employee = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        if (employee is null)
        {
            return (Array.Empty<CampaignDto>(), timeProvider.GetUtcNow());
        }

        var ownerDrafts = status is null
            ? await campaigns.ListAsync(actor.CompanyId, effectiveSeason, CampaignStatus.Draft, 100, null, ct)
            : new Page<CampaignRec>(Array.Empty<CampaignRec>(), null);
        var published = await campaigns.ListAsync(actor.CompanyId, effectiveSeason, CampaignStatus.Published, 100, cursor, ct);
        var tags = new HashSet<string>(employee.Tags);
        var visible = published.Items
            .Where(c => c.Audience.Matches(tags))
            .Select(DtoMapper.ToDto)
            .ToList();
        var ownDrafts = ownerDrafts.Items
            .Where(c => c.OwnerMasterId == actor.MasterId)
            .Select(DtoMapper.ToDto);
        var items = status == CampaignStatus.Published
            ? visible
            : ownDrafts.Concat(visible).ToList();
        items = items.OrderBy(c => c.Code).Take(limit).ToList();
        return (items, timeProvider.GetUtcNow());
    }

    public async Task<(IReadOnlyList<ResourceDto> Items, DateTimeOffset AsOfUtc)> ListResourcesAsync(
        ActorContext actor, ResourceStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        var asOf = timeProvider.GetUtcNow();
        var key = "catalog:resources:" + actor.CompanyId + ":" + (status?.ToString() ?? "all");
        var cached = await cache.GetAsync(key, ct);
        if (cached is not null && CanonicalJson.Deserialize<CatalogPage<ResourceDto>>(cached) is { } page && page.AsOfUtc >= asOf - TimeSpan.FromSeconds(30))
        {
            return (page.Items.Skip(OffsetOf(cursor)).Take(limit).ToArray(), page.AsOfUtc);
        }

        var fetched = await resources.ListAsync(actor.CompanyId, status, 100, null, ct);
        var dto = fetched.Items.Select(DtoMapper.ToDto).ToArray();
        var payload = new CatalogPage<ResourceDto>(dto, asOf);
        await cache.SetAsync(key, CanonicalJson.Serialize(payload), asOf, TimeSpan.FromSeconds(30), ct);
        return (dto.Skip(OffsetOf(cursor)).Take(limit).ToArray(), asOf);
    }

    public async Task<(IReadOnlyList<AchievementDto> Items, DateTimeOffset AsOfUtc)> ListAchievementsAsync(
        ActorContext actor, int limit, string? cursor, CancellationToken ct)
    {
        var asOf = timeProvider.GetUtcNow();
        var key = "catalog:achievements:" + actor.CompanyId;
        var cached = await cache.GetAsync(key, ct);
        if (cached is not null && CanonicalJson.Deserialize<CatalogPage<AchievementDto>>(cached) is { } page && page.AsOfUtc >= asOf - TimeSpan.FromSeconds(30))
        {
            return (page.Items.Skip(OffsetOf(cursor)).Take(limit).ToArray(), page.AsOfUtc);
        }

        var fetched = await achievements.ListAsync(actor.CompanyId, 100, null, ct);
        var dto = fetched.Items.Select(DtoMapper.ToDto).ToArray();
        var payload = new CatalogPage<AchievementDto>(dto, asOf);
        await cache.SetAsync(key, CanonicalJson.Serialize(payload), asOf, TimeSpan.FromSeconds(30), ct);
        return (dto.Skip(OffsetOf(cursor)).Take(limit).ToArray(), asOf);
    }

    private static int OffsetOf(string? cursor)
    {
        return CursorCodec.Decode<OffsetCursor>(cursor)?.Offset ?? 0;
    }

    private sealed record OffsetCursor(int Offset);

    private sealed record CatalogPage<T>(IReadOnlyList<T> Items, DateTimeOffset AsOfUtc);
}
