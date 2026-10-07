using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Budgets;

/// <summary>Budget allocations (B18, H0 Q09): admin-only growth per campaign resource, allowed
/// while the campaign is Draft/Published and its window/season has not ended; every allocation
/// is an operation in the history; replay by number is exact (B24).</summary>
public sealed class BudgetService(
    ICampaignCatalog campaigns,
    IResourceDirectory resources,
    IBudgetLedger budgets,
    IOperationBook operations,
    IAuditLog audit,
    IUnitOfWork uow,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> AllocateAsync(
        ActorContext actor, Guid campaignId, Guid resourceId, long amount, string? reason, string operationNumber, CancellationToken ct)
    {
        Authz.EnsureAdmin(actor);
        Guard.ExternalNumber(operationNumber);
        Guard.PositiveAmount(amount);
        Guard.Description(reason);
        var now = timeProvider.GetUtcNow();
        var essential = CanonicalJson.Serialize(new { campaignId, resourceId, amount, reason });

        // §3.0: current authorization is already verified above; the number comes next.
        var existing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, OperationKind.BudgetAllocation, operationNumber, ct);
        if (existing is not null)
        {
            return ReplayOrConflict(existing, essential);
        }

        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (campaign.Status is not (CampaignStatus.Draft or CampaignStatus.Published))
        {
            throw new MotivaException(ErrorCode.ConflictState, "Allocations are forbidden for archived or deleted campaigns.");
        }

        if (campaign.EndsAt <= now)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Allocations are forbidden after the campaign window/season ended.");
        }

        var resource = await resources.GetAsync(actor.CompanyId, resourceId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaignResourceIds = await campaigns.ListCampaignResourceIdsAsync(actor.CompanyId, campaignId, ct);
        if (!campaignResourceIds.Contains(resourceId))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "The resource must be part of the campaign set.");
        }

        if (resource.Status == ResourceStatus.Archived)
        {
            throw new MotivaException(ErrorCode.ConflictState, "An archived resource cannot be allocated to a budget (B07.2).");
        }

        Domain.SafeMath.AddOrThrow(0, amount);
        await using var scope = await uow.BeginAsync(ct);
        // Re-check under the transaction: a concurrent allocation of the same number must replay.
        var racing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, OperationKind.BudgetAllocation, operationNumber, ct);
        if (racing is not null)
        {
            return ReplayOrConflict(racing, essential);
        }

        var budget = await budgets.LockAsync(actor.CompanyId, campaignId, resourceId, ct);
        if (!Domain.SafeMath.TryAdd(budget.AllocatedTotal, amount, out _) || budget.AllocatedTotal + amount > Guard.MaxTotal)
        {
            throw new MotivaException(ErrorCode.ValidationOverflow, "Budget total exceeds the allowed maximum.");
        }

        await budgets.AddAllocationAsync(actor.CompanyId, campaignId, resourceId, amount, ct);
        var operation = BuildOperation(actor, campaignId, resourceId, resource, amount, reason, operationNumber, now);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(operation));
        operation = operation with { ResponseStatus = 201, ResponseBody = body, EssentialData = essential };
        await operations.InsertAsync(operation, ct);
        await audit.AppendAsync(
            actor.CompanyId, actor,
            new AuditInput("budget.allocated", "Budget", campaignId + ":" + resourceId,
                new[] { new AuditChangeRec("amount", null, amount.ToString()) }),
            now, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/operations/" + operation.Id.ToString());
    }

    public async Task<Page<BudgetRec>> ListBudgetsAsync(ActorContext actor, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        EnsureOwnerOrAdmin(actor, campaign);
        return await budgets.ListAsync(actor.CompanyId, campaignId, limit, cursor, ct);
    }

    public async Task<Page<OperationRec>> ListAllocationsAsync(ActorContext actor, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        EnsureOwnerOrAdmin(actor, campaign);
        return await operations.ListAllocationsAsync(actor.CompanyId, campaignId, limit, cursor, ct);
    }

    internal static void EnsureOwnerOrAdmin(ActorContext actor, CampaignRec campaign)
    {
        if (actor.IsAdmin || campaign.OwnerMasterId == actor.MasterId)
        {
            return;
        }

        throw new MotivaException(ErrorCode.AuthzForbidden, "Only the campaign owner or an administrator may read campaign budgets.");
    }

    internal static CommandResponse ReplayOrConflict(OperationRec existing, string essential)
    {
        if (!string.Equals(existing.EssentialData, essential, StringComparison.Ordinal))
        {
            throw new MotivaException(ErrorCode.ConflictBusinessNumber);
        }

        return new CommandResponse(existing.ResponseStatus ?? 201, existing.ResponseBody ?? "{}", "/api/v1/operations/" + existing.Id.ToString());
    }

    internal static OperationRec BuildOperation(
        ActorContext actor,
        Guid? campaignId,
        Guid resourceId,
        ResourceRec resource,
        long amount,
        string? reason,
        string operationNumber,
        DateTimeOffset now,
        OperationKind kind = OperationKind.BudgetAllocation,
        OperationResult result = OperationResult.Posted,
        RefusalCode? refusal = null,
        int? masterId = null,
        Guid? purchaseSystemId = null,
        Guid? originalOperationId = null,
        bool isDebit = false)
    {
        return new OperationRec(
            Guid.NewGuid(), kind, result, refusal, actor, masterId, campaignId, purchaseSystemId, originalOperationId,
            reason, operationNumber,
            new[] { new OperationItemRec(resourceId, resource.Code, amount, isDebit) },
            now, null, null, null);
    }
}
