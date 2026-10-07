using Microsoft.AspNetCore.Mvc;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Exports;
using Motiva.Application.Ports;

namespace Motiva.Api.Endpoints;

/// <summary>Export orders, status, deletion and download links (B35–B37).</summary>
internal static class ExportEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var exports = api.MapGroup("/exports");
        exports.MapPost("/", async (
            [FromServices] ExportsService service, HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromBody] ExportCreateRequest body, CancellationToken ct) =>
            (await service.CreateAsync(
                http.Actor(), Enum.Parse<ExportScope>(body.Scope), body.MasterId, body.ResourceId,
                body.FromUtc, body.ToUtc, idempotencyKey, ct)).ToResult());
        exports.MapGet("/", async (
            [FromServices] ExportsService service, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListAsync(http.Actor(), Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        exports.MapGet("/{id:guid}", async (
            [FromServices] ExportsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
            EndpointHelpers.Ok(DtoMapper.ToDto(await service.GetAsync(http.Actor(), id, ct))));
        exports.MapDelete("/{id:guid}", async (
            [FromServices] ExportsService service, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
            (await service.DeleteAsync(http.Actor(), id, ct)).ToResult());
        exports.MapPost("/{id:guid}/download-links", async (
            [FromServices] ExportsService service, HttpContext http, [FromRoute] Guid id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
            (await service.CreateDownloadLinkAsync(http.Actor(), id, idempotencyKey, ct)).ToResult());
    }

    public sealed record ExportCreateRequest(
        string Scope, int? MasterId, Guid? ResourceId, DateTimeOffset FromUtc, DateTimeOffset ToUtc);
}
