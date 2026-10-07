using Microsoft.AspNetCore.Mvc;
using Motiva.Application.Common;
using Motiva.Application.Competitions;
using Motiva.Application.Dto;
using Motiva.Application.Ports;
using Motiva.Application.Progress;
using Motiva.Application.Reads;

namespace Motiva.Api.Endpoints;

/// <summary>Personal and campaign reads: wallets, operations, achievements, progress, leaderboards.</summary>
internal static class ReadingEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/me/wallet", async ([FromServices] ReadService reads, HttpContext http, CancellationToken ct)
            => EndpointHelpers.Ok(await reads.GetOwnWalletAsync(http.Actor(), ct)));

        api.MapGet("/me/operations", async (
            [FromServices] ReadService reads, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] Guid? resourceId, [FromQuery] string? kind, [FromQuery] string? result, CancellationToken ct) =>
        {
            var page = await reads.ListOwnOperationsAsync(
                http.Actor(), Paging.NormalizeLimit(limit), cursor, from, to, resourceId,
                kind is null ? null : Enum.Parse<OperationKind>(kind), result is null ? null : Enum.Parse<OperationResult>(result), ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });

        api.MapGet("/me/achievements", async (
            [FromServices] ReadService reads, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] int? season, CancellationToken ct) =>
        {
            var page = await reads.ListOwnAchievementsAsync(http.Actor(), season, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });

        api.MapGet("/me/progress-events", async (
            [FromServices] ProgressEventsService progress, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] Guid? campaignId, [FromQuery] Guid? taskId, CancellationToken ct) =>
        {
            var page = await progress.ListOwnAsync(http.Actor(), Paging.NormalizeLimit(limit), cursor, from, to, campaignId, taskId, ct);
            return EndpointHelpers.Page(page, EconomyEndpoints.MapProgressEvent);
        });

        api.MapGet("/me/campaigns/{id:guid}/progress", async (
            [FromServices] ReadService reads, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
            EndpointHelpers.Ok(await reads.GetOwnCampaignProgressAsync(http.Actor(), id, ct)));

        api.MapGet("/employees/{masterId:int}/wallet", async (
            [FromServices] ReadService reads, HttpContext http, [FromRoute] int masterId, CancellationToken ct) =>
            EndpointHelpers.Ok(await reads.GetEmployeeWalletAsync(http.Actor(), masterId, ct)));

        api.MapGet("/employees/{masterId:int}/operations", async (
            [FromServices] ReadService reads, HttpContext http, [FromRoute] int masterId,
            [FromQuery] int? limit, [FromQuery] string? cursor,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] Guid? resourceId, [FromQuery] string? kind, CancellationToken ct) =>
        {
            var page = await reads.ListEmployeeOperationsAsync(
                http.Actor(), masterId, Paging.NormalizeLimit(limit), cursor, from, to, resourceId,
                kind is null ? null : Enum.Parse<OperationKind>(kind), ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });

        api.MapGet("/operations/{id:guid}", async (
            [FromServices] ReadService reads, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
            EndpointHelpers.Ok(DtoMapper.ToDto(await reads.GetOperationAsync(http.Actor(), id, ct))));

        api.MapGet("/campaigns/{id:guid}/operations", async (
            [FromServices] ReadService reads, HttpContext http, [FromRoute] Guid id,
            [FromQuery] int? limit, [FromQuery] string? cursor,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] Guid? resourceId, CancellationToken ct) =>
        {
            var page = await reads.ListCampaignOperationsAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, from, to, resourceId, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });

        api.MapGet("/campaigns/{id:guid}/progress", async (
            [FromServices] ReadService reads, HttpContext http, [FromRoute] Guid id,
            [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] Guid? streamId, CancellationToken ct) =>
        {
            var page = await reads.ListCampaignProgressAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, streamId, ct);
            return EndpointHelpers.Page(page, p => (object)p);
        });

        api.MapGet("/challenges/{id:guid}/leaderboard", async (
            [FromServices] LeaderboardService leaderboards, HttpContext http, [FromRoute] Guid id,
            [FromQuery] int limit, CancellationToken ct) =>
            EndpointHelpers.Ok(await leaderboards.GetPageAsync(http.Actor(), id, limit, ct)));

        api.MapGet("/challenges/{id:guid}/leaderboard/me", async (
            [FromServices] LeaderboardService leaderboards, HttpContext http, [FromRoute] Guid id, CancellationToken ct) =>
            EndpointHelpers.Ok(await leaderboards.GetMeAsync(http.Actor(), id, ct)));
    }
}
