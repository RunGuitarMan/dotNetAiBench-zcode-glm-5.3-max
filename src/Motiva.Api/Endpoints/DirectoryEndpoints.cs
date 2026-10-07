using Microsoft.AspNetCore.Mvc;
using Motiva.Application;
using Motiva.Application.Administration;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using Motiva.Application.Reads;

namespace Motiva.Api.Endpoints;

/// <summary>Employees, resources, achievements, purchase systems, integration grants, audit records.</summary>
internal static class DirectoryEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var employees = api.MapGroup("/employees");
        employees.MapGet("/", async (
            [FromServices] EmployeesService service, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] bool? active, CancellationToken ct) =>
        {
            var page = await service.ListAsync(http.Actor(), active, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        employees.MapPost("/", async (
            [FromServices] EmployeesService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] EmployeeCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(http.Actor(), body.MasterId, body.Tags ?? [], idempotencyKey, ct)).ToResult());
        employees.MapGet("/{masterId:int}", async (
            [FromServices] EmployeesService service, HttpContext http, [FromRoute] int masterId, CancellationToken ct) =>
        {
            var rec = await service.GetAsync(http.Actor(), masterId, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        employees.MapPatch("/{masterId:int}", async (
            [FromServices] EmployeesService service, HttpContext http, [FromRoute] int masterId,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] EmployeePatchRequest body, CancellationToken ct) =>
            (await service.PatchAsync(http.Actor(), masterId, ifMatch, body.IsActive, body.Tags, ct)).ToResult());

        var resources = api.MapGroup("/resources");
        resources.MapGet("/", async (
            [FromServices] CatalogService catalog, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] string? status, CancellationToken ct) =>
        {
            var (items, asOf) = await catalog.ListResourcesAsync(
                http.Actor(), status is null ? null : Enum.Parse<ResourceStatus>(status), Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Ok(new { items, nextCursor = (string?)null, asOfUtc = asOf });
        });
        resources.MapPost("/", async (
            [FromServices] ResourcesService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] ResourceCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(http.Actor(), body.Code, body.Name, idempotencyKey, ct)).ToResult());
        resources.MapGet("/{id:guid}", async (
            [FromServices] ResourcesService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        resources.MapPatch("/{id:guid}", async (
            [FromServices] ResourcesService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] ResourcePatchRequest body, CancellationToken ct) =>
            (await service.PatchAsync(http.Actor(), id, ifMatch, body.Name,
                body.Status is null ? null : Enum.Parse<ResourceStatus>(body.Status), ct)).ToResult());

        var achievements = api.MapGroup("/achievements");
        achievements.MapGet("/", async (
            [FromServices] CatalogService catalog, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var (items, asOf) = await catalog.ListAchievementsAsync(http.Actor(), Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Ok(new { items, nextCursor = (string?)null, asOfUtc = asOf });
        });
        achievements.MapPost("/", async (
            [FromServices] AchievementsService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] AchievementCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(http.Actor(), body.Code, body.Name, body.Description, idempotencyKey, ct)).ToResult());
        achievements.MapGet("/{id:guid}", async (
            [FromServices] AchievementsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        achievements.MapPatch("/{id:guid}", async (
            [FromServices] AchievementsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] AchievementPatchRequest body, CancellationToken ct) =>
            (await service.PatchAsync(http.Actor(), id, ifMatch, body.Name, body.Description, ct)).ToResult());

        var systems = api.MapGroup("/purchase-systems");
        systems.MapGet("/", async (
            [FromServices] PurchaseSystemsService service, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListAsync(http.Actor(), Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        systems.MapPost("/", async (
            [FromServices] PurchaseSystemsService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] PurchaseSystemCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(http.Actor(), body.Code, body.Name, body.AcceptedResourceIds ?? [], idempotencyKey, ct)).ToResult());
        systems.MapGet("/{id:guid}", async (
            [FromServices] PurchaseSystemsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        systems.MapPatch("/{id:guid}", async (
            [FromServices] PurchaseSystemsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, [FromBody] PurchaseSystemPatchRequest body, CancellationToken ct) =>
            (await service.PatchAsync(http.Actor(), id, ifMatch, body.Name,
                body.Status is null ? null : Enum.Parse<ContentStatus>(body.Status), body.AcceptedResourceIds, ct)).ToResult());

        var grants = api.MapGroup("/integration-grants");
        grants.MapGet("/", async (
            [FromServices] IntegrationGrantsService service, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] string? subject, CancellationToken ct) =>
        {
            var page = await service.ListAsync(http.Actor(), subject, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        grants.MapPost("/", async (
            [FromServices] IntegrationGrantsService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] IntegrationGrantCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(http.Actor(), body.Subject, Enum.Parse<GrantKind>(body.Kind),
                body.CampaignId, body.ResourceId, body.PurchaseSystemId, idempotencyKey, ct)).ToResult());
        grants.MapGet("/{id:guid}", async (
            [FromServices] IntegrationGrantsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
        {
            var rec = await service.GetAsync(http.Actor(), id, ct);
            return EndpointHelpers.Ok(DtoMapper.ToDto(rec), ETags.Format(rec.Version));
        });
        grants.MapDelete("/{id:guid}", async (
            [FromServices] IntegrationGrantsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
            (await service.RevokeAsync(http.Actor(), id, ifMatch, ct)).ToResult());

        api.MapGet("/audit-records", async (
            [FromServices] ReadService reads, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? entityType, [FromQuery] string? entityId, CancellationToken ct) =>
        {
            var page = await reads.ListAuditRecordsAsync(http.Actor(), Paging.NormalizeLimit(limit), cursor, from, to, entityType, entityId, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
    }

    public sealed record EmployeeCreateRequest(int MasterId, IReadOnlyList<string>? Tags);

    public sealed record EmployeePatchRequest(bool? IsActive, IReadOnlyList<string>? Tags);

    public sealed record ResourceCreateRequest(string Code, string Name);

    public sealed record ResourcePatchRequest(string? Name, string? Status);

    public sealed record AchievementCreateRequest(string Code, string Name, string? Description);

    public sealed record AchievementPatchRequest(string? Name, string? Description);

    public sealed record PurchaseSystemCreateRequest(string Code, string Name, IReadOnlyList<Guid>? AcceptedResourceIds);

    public sealed record PurchaseSystemPatchRequest(string? Name, string? Status, IReadOnlyList<Guid>? AcceptedResourceIds);

    public sealed record IntegrationGrantCreateRequest(
        string Subject, string Kind, Guid? CampaignId, Guid? ResourceId, Guid? PurchaseSystemId);
}
