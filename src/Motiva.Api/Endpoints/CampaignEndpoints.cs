using Microsoft.AspNetCore.Mvc;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using Motiva.Application.Reads;
using Motiva.Domain.Periods;

namespace Motiva.Api.Endpoints;

/// <summary>Campaign aggregate and content: campaigns, owner, resources, streams, tasks, milestones, challenges.</summary>
internal static class CampaignEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var campaigns = api.MapGroup("/campaigns");
        campaigns.MapGet("/", async (
            [FromServices] CatalogService catalog, [FromServices] ICampaignCatalog store, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] int? season, [FromQuery] string? status, CancellationToken ct) =>
        {
            var (items, asOf) = await catalog.ListCampaignsAsync(
                http.Actor(), season, status is null ? null : Enum.Parse<CampaignStatus>(status), Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Ok(new { items, nextCursor = (string?)null, asOfUtc = asOf });
        });
        campaigns.MapPost("/", async (
            [FromServices] CampaignsService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] CampaignCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(
                http.Actor(), body.Code, body.Name, body.Description, body.OwnerMasterId,
                body.StartsAt, body.EndsAt, body.Audience, idempotencyKey, ct)).ToResult());
        campaigns.MapGet("/{id:guid}", async (
            [FromServices] CampaignsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        campaigns.MapPatch("/{id:guid}", async (
            [FromServices] CampaignsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] CampaignPatchRequest body, CancellationToken ct) =>
            (await service.PatchAsync(
                http.Actor(), id, ifMatch, body.Name, body.Description, body.Audience,
                body.Status is null ? null : Enum.Parse<CampaignStatus>(body.Status), ct)).ToResult());
        campaigns.MapDelete("/{id:guid}", async (
            [FromServices] CampaignsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
            (await service.DeleteDraftAsync(http.Actor(), id, ifMatch, ct)).ToResult());
        campaigns.MapPut("/{id:guid}/owner", async (
            [FromServices] CampaignsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] CampaignOwnerPutRequest body, CancellationToken ct) =>
            (await service.PutOwnerAsync(http.Actor(), id, ifMatch, body.MasterId, ct)).ToResult());
        campaigns.MapGet("/{id:guid}/resources", async (
            [FromServices] CampaignsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var items = await service.ListResourcesAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(new { items = items.Select(DtoMapper.ToDto).ToArray() });
        });
        campaigns.MapPut("/{id:guid}/resources", async (
            [FromServices] CampaignsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] CampaignResourcesPutRequest body, CancellationToken ct) =>
            (await service.PutResourcesAsync(http.Actor(), id, ifMatch, body.ResourceIds, ct)).ToResult());
        campaigns.MapPost("/{id:guid}/streams", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] StreamCreateRequest body, CancellationToken ct) =>
            (await service.CreateStreamAsync(http.Actor(), id, body.Code, body.Name, idempotencyKey, ct)).ToResult());
        campaigns.MapGet("/{id:guid}/streams", async (
            [FromServices] CampaignContentService service, HttpContext http,
            [FromRoute] Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListStreamsAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        campaigns.MapPost("/{id:guid}/challenges", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] ChallengeCreateRequest body, CancellationToken ct) =>
            (await service.CreateChallengeAsync(http.Actor(), id, body.StreamId, body.StartsAt, body.EndsAt, idempotencyKey, ct)).ToResult());
        campaigns.MapGet("/{id:guid}/challenges", async (
            [FromServices] CampaignContentService service, HttpContext http,
            [FromRoute] Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListChallengesAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });

        api.MapGet("/streams/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetStreamAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        api.MapPatch("/streams/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] StreamPatchRequest body, CancellationToken ct) =>
            (await service.PatchStreamAsync(http.Actor(), id, ifMatch, body.Name,
                body.Status is null ? null : Enum.Parse<ContentStatus>(body.Status), ct)).ToResult());
        api.MapGet("/streams/{id:guid}/tasks", async (
            [FromServices] CampaignContentService service, HttpContext http,
            [FromRoute] Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListTasksAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        api.MapPost("/streams/{id:guid}/tasks", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] TaskCreateRequest body, CancellationToken ct) =>
            (await service.CreateTaskAsync(
                http.Actor(), id, body.Code, body.Name, body.Description, body.Goal,
                Enum.Parse<PeriodKind>(body.Period), body.StreamPoints,
                body.RewardItems?.Select(r => new RewardItemDto(r.ResourceId, r.Amount)).ToArray() ?? [],
                body.Audience, idempotencyKey, ct)).ToResult());
        api.MapGet("/streams/{id:guid}/milestones", async (
            [FromServices] CampaignContentService service, HttpContext http,
            [FromRoute] Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListMilestonesAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        api.MapPost("/streams/{id:guid}/milestones", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] MilestoneCreateRequest body, CancellationToken ct) =>
            (await service.CreateMilestoneAsync(http.Actor(), id, body.Threshold, body.AchievementId, idempotencyKey, ct)).ToResult());

        api.MapGet("/tasks/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetTaskAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        api.MapPatch("/tasks/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] TaskPatchRequest body, CancellationToken ct) =>
            (await service.PatchTaskAsync(
                http.Actor(), id, ifMatch, body.Name, body.Description, body.Goal,
                body.Period is null ? null : Enum.Parse<PeriodKind>(body.Period), body.StreamPoints,
                body.RewardItems?.Select(r => new RewardItemDto(r.ResourceId, r.Amount)).ToArray(),
                body.Audience,
                body.Status is null ? null : Enum.Parse<ContentStatus>(body.Status), ct)).ToResult());

        api.MapGet("/milestones/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetMilestoneAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        api.MapDelete("/milestones/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
            (await service.DeleteMilestoneAsync(http.Actor(), id, ifMatch, ct)).ToResult());

        api.MapGet("/challenges/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetChallengeAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        api.MapDelete("/challenges/{id:guid}", async (
            [FromServices] CampaignContentService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
            (await service.DeleteChallengeAsync(http.Actor(), id, ifMatch, ct)).ToResult());
    }

    public sealed record CampaignCreateRequest(
        string Code, string Name, string? Description, int OwnerMasterId,
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, AudienceDto? Audience);

    public sealed record CampaignPatchRequest(string? Name, string? Description, AudienceDto? Audience, string? Status);

    public sealed record CampaignOwnerPutRequest(int MasterId);

    public sealed record CampaignResourcesPutRequest(IReadOnlyList<Guid> ResourceIds);

    public sealed record StreamCreateRequest(string Code, string Name);

    public sealed record StreamPatchRequest(string? Name, string? Status);

    public sealed record TaskCreateRequest(
        string Code, string Name, string? Description, long Goal, string Period, long StreamPoints,
        IReadOnlyList<RewardItemRequest>? RewardItems, AudienceDto? Audience);

    public sealed record TaskPatchRequest(
        string? Name, string? Description, long? Goal, string? Period, long? StreamPoints,
        IReadOnlyList<RewardItemRequest>? RewardItems, AudienceDto? Audience, string? Status);

    public sealed record RewardItemRequest(Guid ResourceId, long Amount);

    public sealed record MilestoneCreateRequest(long Threshold, Guid AchievementId);

    public sealed record ChallengeCreateRequest(Guid StreamId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
}
