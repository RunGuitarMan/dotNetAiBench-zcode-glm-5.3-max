using Microsoft.AspNetCore.Mvc;
using Motiva.Application.Budgets;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Economy;
using Motiva.Application.Ports;
using Motiva.Application.Progress;

namespace Motiva.Api.Endpoints;

/// <summary>Economic operations: budget allocations, manual awards, spends, reversals and progress events.</summary>
internal static class EconomyEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/campaigns/{id:guid}/budget-allocations", async (
            [FromServices] BudgetService service, HttpContext http, [FromRoute] Guid id,
            [FromBody] BudgetAllocationRequest body, CancellationToken ct) =>
            (await service.AllocateAsync(http.Actor(), id, body.ResourceId, body.Amount, body.Reason, body.OperationNumber, ct)).ToResult());
        api.MapGet("/campaigns/{id:guid}/budget-allocations", async (
            [FromServices] BudgetService service, HttpContext http, [FromRoute] Guid id,
            [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListAllocationsAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });
        api.MapGet("/campaigns/{id:guid}/budgets", async (
            [FromServices] BudgetService service, HttpContext http, [FromRoute] Guid id,
            [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        {
            var page = await service.ListBudgetsAsync(http.Actor(), id, Paging.NormalizeLimit(limit), cursor, ct);
            return EndpointHelpers.Page(page, DtoMapper.ToDto);
        });

        api.MapPost("/campaigns/{id:guid}/manual-awards", async (
            [FromServices] ManualAwardsService service, HttpContext http, [FromRoute] Guid id,
            [FromBody] ManualAwardRequest body, CancellationToken ct) =>
            (await service.AwardAsync(http.Actor(), id, body.MasterId, body.ResourceId, body.Amount, body.Reason, body.OperationNumber, ct)).ToResult());

        api.MapPost("/spends", async (
            [FromServices] SpendsService service, HttpContext http,
            [FromBody] SpendRequest body, CancellationToken ct) =>
            (await service.SpendAsync(http.Actor(), body.MasterId, body.PurchaseSystemId, body.ResourceId, body.Amount, body.OperationNumber, ct)).ToResult());

        api.MapPost("/reversals", async (
            [FromServices] ReversalsService service, HttpContext http,
            [FromBody] ReversalRequest body, CancellationToken ct) =>
            (await service.ReverseAsync(http.Actor(), body.OriginalOperationId, body.Reason, body.OperationNumber, ct)).ToResult());

        api.MapPost("/progress-events", async (
            [FromServices] ProgressEventsService service, HttpContext http,
            [FromBody] ProgressEventRequest body, CancellationToken ct) =>
            (await service.PostAsync(http.Actor(), body.EventNumber, body.MasterId, body.TaskId, body.Delta, ct)).ToResult());
        api.MapGet("/progress-events", async (
            [FromServices] ProgressEventsService service, HttpContext http,
            [FromQuery] int? limit, [FromQuery] string? cursor,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        {
            var page = await service.ListCompanyAsync(http.Actor(), Paging.NormalizeLimit(limit), cursor, from, to, ct);
            return EndpointHelpers.Page(page, MapProgressEvent);
        });
    }

    internal static object MapProgressEvent(ProgressEventRec rec)
    {
        CompletionDto? completion = null;
        if (rec.CompletionId is not null)
        {
            completion = new CompletionDto(
                rec.TaskId, rec.PeriodStartUtc ?? rec.AcceptedAtUtc, rec.StreamPointsAdded ?? 0,
                new RewardDecisionDto((rec.RewardOutcome ?? RewardOutcome.NotProvided).ToString(), rec.RewardOperationId),
                rec.AcceptedAtUtc);
        }

        return new ProgressEventResultDto(
            rec.Id, rec.EventNumber, rec.Result.ToString(), rec.RejectReason?.ToString(), rec.MasterId, rec.TaskId,
            rec.PeriodStartUtc, rec.TransmittedDelta, rec.CreditedDelta, rec.AcceptedAtUtc, completion);
    }

    public sealed record BudgetAllocationRequest(Guid ResourceId, long Amount, string OperationNumber, string? Reason);

    public sealed record ManualAwardRequest(int MasterId, Guid ResourceId, long Amount, string Reason, string OperationNumber);

    public sealed record SpendRequest(int? MasterId, Guid PurchaseSystemId, Guid ResourceId, long Amount, string OperationNumber);

    public sealed record ReversalRequest(Guid OriginalOperationId, string Reason, string OperationNumber);

    public sealed record ProgressEventRequest(string EventNumber, int MasterId, Guid TaskId, long Delta);
}
