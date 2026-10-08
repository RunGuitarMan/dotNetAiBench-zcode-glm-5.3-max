using Motiva.Application.Budgets;
using Motiva.Application.Campaigns;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Economy;

/// <summary>Manual awards (B21, H0 Q01): owner/admin/service-with-Award-grant reward an active
/// audience member of a running published campaign from its budget; declined outcomes are
/// saved results returned by the same 201 schema (T04); the lock path is budgets → wallet
/// balances (§3.1).</summary>
public sealed class ManualAwardsService(
    ITestImpediments impediments,
    IEmployeeDirectory employees,
    ICampaignCatalog campaigns,
    IResourceDirectory resources,
    IIntegrationDirectory grants,
    IBudgetLedger budgets,
    IWalletLedger wallets,
    IOperationBook operations,
    IUnitOfWork uow,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> AwardAsync(
        ActorContext actor, Guid campaignId, int masterId, Guid resourceId, long amount, string reason, string operationNumber, CancellationToken ct)
    {
        Guard.MasterId(masterId);
        Guard.ExternalNumber(operationNumber);
        Guard.PositiveAmount(amount);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "reason must be 1..2000 characters.");
        }

        var essential = CanonicalJson.Serialize(new { campaignId, masterId, resourceId, amount, reason });

        // §3.0 step 2: current rights and initiator activity first, before the number — for new
        // requests AND replays: a former owner gets 403, not the stored body (B05.1).
        var campaign = await campaigns.GetAsync(actor.CompanyId, campaignId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (actor.ActorType == ActorType.Service)
        {
            Authz.EnsureGrant(await grants.FindActiveAsync(actor.CompanyId, actor.Subject, GrantKind.Award, campaignId, resourceId, null, ct));
        }
        else
        {
            var initiator = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
            Authz.EnsureActiveUser(actor, initiator);
            if (!actor.IsAdmin && campaign.OwnerMasterId != actor.MasterId)
            {
                throw new MotivaException(ErrorCode.AuthzForbidden, "Only the current campaign owner or an administrator may award manually.");
            }
        }

        var existing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, OperationKind.ManualAward, operationNumber, ct);
        if (existing is not null)
        {
            return BudgetService.ReplayOrConflict(existing, essential);
        }

        var now = timeProvider.GetUtcNow();
        var resource = await resources.GetAsync(actor.CompanyId, resourceId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var campaignResourceIds = await campaigns.ListCampaignResourceIdsAsync(actor.CompanyId, campaignId, ct);
        if (!campaignResourceIds.Contains(resourceId))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "The resource must be part of the campaign set.");
        }

        var recipient = await employees.GetAsync(actor.CompanyId, masterId, ct);

        await using var scope = await uow.BeginAsync(ct);
        var racing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, OperationKind.ManualAward, operationNumber, ct);
        if (racing is not null)
        {
            return BudgetService.ReplayOrConflict(racing, essential);
        }

        RefusalCode? refusal = null;
        var posted = false;
        if (campaign.Status != CampaignStatus.Published || campaign.StartsAt > now || campaign.EndsAt <= now)
        {
            throw new MotivaException(ErrorCode.ConflictState, "Manual awards require a running published campaign.");
        }

        if (recipient is null || !recipient.IsActive)
        {
            refusal = RefusalCode.RecipientNotActive;
        }
        else if (resource.Status == ResourceStatus.Archived)
        {
            refusal = RefusalCode.ResourceUnavailable;
        }
        else if (!campaign.Audience.Matches(new HashSet<string>(recipient.Tags)))
        {
            refusal = RefusalCode.RecipientNotInAudience;
        }
        else
        {
            var budget = await budgets.LockAsync(actor.CompanyId, campaignId, resourceId, ct);
            if (budget.Available < amount)
            {
                refusal = RefusalCode.InsufficientBudget;
            }
            else
            {
                await budgets.AddSpendingAsync(actor.CompanyId, campaignId, resourceId, amount, ct);
                await wallets.LockBalanceAsync(actor.CompanyId, masterId, resourceId, ct);
                await wallets.ApplyDeltaAsync(actor.CompanyId, masterId, resourceId, amount, ct);
                posted = true;
            }
        }

        var operation = new OperationRec(
            Guid.NewGuid(), OperationKind.ManualAward, posted ? OperationResult.Posted : OperationResult.Declined,
            posted ? null : refusal, actor, masterId, campaignId, null, null, reason, operationNumber,
            new[] { new OperationItemRec(resourceId, resource.Code, amount, IsDebit: false) },
            now, essential, null, null);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(operation));
        operation = operation with { ResponseStatus = 201, ResponseBody = body };
        await operations.InsertAsync(operation, ct);
        await impediments.CheckpointAsync(Checkpoints.ManualAwardBeforeCommit, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/operations/" + operation.Id.ToString());
    }
}
