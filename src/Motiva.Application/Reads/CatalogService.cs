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
    CurrentRights rights,
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
        // B05.1: catalog reads require an active profile too — a blocked initiator (even an
        // admin claim in the token) gets 403 before any campaign data is returned.
        await rights.EnsureActiveEmployeeAsync(actor, ct);
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

    public async Task<(IReadOnlyList<ResourceDto> Items, string? NextCursor)> ListResourcesAsync(
        ActorContext actor, ResourceStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        await rights.EnsureActiveEmployeeAsync(actor, ct);
        var (page, asOf) = await GetCatalogPageAsync(
            actor, "catalog:resources:" + actor.CompanyId + ":" + (status?.ToString() ?? "all"),
            () => resources.ListAsync(actor.CompanyId, status, MaxCatalog, null, ct),
            r => DtoMapper.ToDto(r), ct);
        return Slice(page, limit, cursor, asOf);
    }

    public async Task<(IReadOnlyList<AchievementDto> Items, string? NextCursor)> ListAchievementsAsync(
        ActorContext actor, int limit, string? cursor, CancellationToken ct)
    {
        await rights.EnsureActiveEmployeeAsync(actor, ct);
        var (page, asOf) = await GetCatalogPageAsync(
            actor, "catalog:achievements:" + actor.CompanyId,
            () => achievements.ListAsync(actor.CompanyId, MaxCatalog, null, ct),
            a => DtoMapper.ToDto(a), ct);
        return Slice(page, limit, cursor, asOf);
    }

    private const int MaxCatalog = 100;

    private sealed record OffsetCursor(int Offset);

    private sealed record CatalogPage<T>(IReadOnlyList<T> Items, DateTimeOffset AsOfUtc);

    private async Task<(CatalogPage<TDto> Page, DateTimeOffset AsOf)> GetCatalogPageAsync<TRec, TDto>(
        ActorContext actor, string key, Func<Task<Page<TRec>>> fetch, Func<TRec, TDto> map, CancellationToken ct)
        where TRec : notnull
    {
        var asOf = timeProvider.GetUtcNow();
        var cached = await cache.GetAsync(key, ct);
        if (cached is not null && CanonicalJson.Deserialize<CatalogPage<TDto>>(cached) is { } cachedPage
            && cachedPage.AsOfUtc >= asOf - TimeSpan.FromSeconds(30))
        {
            return (cachedPage, cachedPage.AsOfUtc);
        }

        var fetched = await fetch();
        var page = new CatalogPage<TDto>(fetched.Items.Select(map).ToArray(), asOf);
        await cache.SetAsync(key, CanonicalJson.Serialize(page), asOf, TimeSpan.FromSeconds(30), ct);
        return (page, asOf);
    }

    /// <summary>Offset slicing with an honest nextCursor: present only when more items exist (T04).</summary>
    private static (IReadOnlyList<TDto> Items, string? NextCursor) Slice<TDto>(
        CatalogPage<TDto> page, int limit, string? cursor, DateTimeOffset asOf)
    {
        var offset = CursorCodec.Decode<OffsetCursor>(cursor)?.Offset ?? 0;
        var items = page.Items.Skip(offset).Take(limit + 1).ToList();
        var hasMore = items.Count > limit;
        var result = items.Take(limit).ToArray();
        var next = hasMore ? CursorCodec.Encode(new OffsetCursor(offset + limit)) : null;
        return (result, next);
    }
}
