using Motiva.Application.Budgets;
using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Economy;

/// <summary>Full reversals (B25–B27): award packages return wallet → original budgets (whole or
/// declined); spend reversals return the full amount to the wallet without touching budgets.
/// Original row lock first, then budgets/wallet balances by resource id (§3.1). A blocked
/// recipient can be corrected only by an administrator (H0 Q03), including before replay.</summary>
public sealed class ReversalsService(
    IEmployeeDirectory employees,
    ICampaignCatalog campaigns,
    IIntegrationDirectory grants,
    IBudgetLedger budgets,
    IWalletLedger wallets,
    IOperationBook operations,
    IUnitOfWork uow,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> ReverseAsync(
        ActorContext actor, Guid originalOperationId, string reason, string operationNumber, CancellationToken ct)
    {
        Guard.ExternalNumber(operationNumber);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "reason must be 1..2000 characters.");
        }

        var essential = CanonicalJson.Serialize(new { originalOperationId, reason });
        var preview = await operations.GetAsync(actor.CompanyId, originalOperationId, ct)
            ?? throw new MotivaException(ErrorCode.NotFound);

        // §3.0 step 2: current rights before the number.
        await AuthorizeAsync(actor, preview, employees, campaigns, grants, ct);
        var existing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, ReversalKindOf(preview), operationNumber, ct);
        if (existing is not null)
        {
            return BudgetService.ReplayOrConflict(existing, essential);
        }

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var racing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, ReversalKindOf(preview), operationNumber, ct);
        if (racing is not null)
        {
            return BudgetService.ReplayOrConflict(racing, essential);
        }

        // Step 1 of the lock path: the original row, so competing reversals serialize here.
        var original = await operations.LockOriginalAsync(actor.CompanyId, originalOperationId, ct);
        var kind = ReversalKindOf(original);
        var recipient = original.MasterId
            ?? throw new MotivaException(ErrorCode.ConflictState, "The original operation has no wallet owner.");
        var recipientRec = await employees.GetAsync(actor.CompanyId, recipient, ct);

        RefusalCode? refusal = null;
        var posted = false;
        if (original.Result != OperationResult.Posted)
        {
            refusal = RefusalCode.OriginalNotPosted;
        }
        else if (await operations.FindPostedReversalAsync(actor.CompanyId, originalOperationId, ct) is not null)
        {
            refusal = RefusalCode.OriginalAlreadyReversed;
        }
        else if (kind == OperationKind.AwardReversal)
        {
            // §3.1 lock order for the reversal path: original → budgets (by resource id) →
            // wallet balances (by resource id). Whole-package check; any insufficient
            // balance declines the entire reversal (B26.3).
            var orderedItems = original.Items.OrderBy(i => i.ResourceId).ToArray();
            foreach (var item in orderedItems)
            {
                await budgets.LockAsync(actor.CompanyId, original.CampaignId!.Value, item.ResourceId, ct);
            }

            foreach (var item in orderedItems)
            {
                await wallets.LockBalanceAsync(actor.CompanyId, recipient, item.ResourceId, ct);
            }

            var insufficient = false;
            foreach (var item in orderedItems)
            {
                var balance = await wallets.GetBalanceAsync(actor.CompanyId, recipient, item.ResourceId, ct);
                if (balance.Balance < item.Amount)
                {
                    insufficient = true;
                    break;
                }
            }

            if (insufficient)
            {
                refusal = RefusalCode.InsufficientBalance;
            }
            else
            {
                foreach (var item in orderedItems)
                {
                    await wallets.ApplyDeltaAsync(actor.CompanyId, recipient, item.ResourceId, -item.Amount, ct);
                    await budgets.AddReturnAsync(actor.CompanyId, original.CampaignId!.Value, item.ResourceId, item.Amount, ct);
                }

                posted = true;
            }
        }
        else
        {
            var item = original.Items[0];
            await wallets.LockBalanceAsync(actor.CompanyId, recipient, item.ResourceId, ct);
            await wallets.ApplyDeltaAsync(actor.CompanyId, recipient, item.ResourceId, item.Amount, ct);
            posted = true;
        }

        var reversal = new OperationRec(
            Guid.NewGuid(), kind, posted ? OperationResult.Posted : OperationResult.Declined,
            posted ? null : refusal, actor, recipient, original.CampaignId, original.PurchaseSystemId,
            originalOperationId, reason, operationNumber,
            original.Items.Select(i => new OperationItemRec(i.ResourceId, i.ResourceCode, i.Amount, kind == OperationKind.AwardReversal)).ToArray(),
            now, essential, null, null);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(reversal));
        reversal = reversal with { ResponseStatus = 201, ResponseBody = body };
        await operations.InsertAsync(reversal, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/operations/" + reversal.Id.ToString());
    }

    private static OperationKind ReversalKindOf(OperationRec original)
    {
        return original.Kind switch
        {
            OperationKind.ManualAward or OperationKind.TaskReward => OperationKind.AwardReversal,
            OperationKind.Spend => OperationKind.SpendReversal,
            _ => throw new MotivaException(ErrorCode.ConflictState, "Only awards and spends can be reversed."),
        };
    }

    private static async Task AuthorizeAsync(
        ActorContext actor,
        OperationRec original,
        IEmployeeDirectory employees,
        ICampaignCatalog campaigns,
        IIntegrationDirectory grants,
        CancellationToken ct)
    {
        var recipientBlocked = original.MasterId is int master
            && await employees.GetAsync(actor.CompanyId, master, ct) is { IsActive: false };

        if (actor.ActorType == ActorType.Service)
        {
            if (original.Kind != OperationKind.Spend || original.PurchaseSystemId is null)
            {
                throw new MotivaException(ErrorCode.AuthzForbidden, "A service can reverse only its own spends.");
            }

            var item = original.Items is [var firstItem] ? firstItem : throw new MotivaException(ErrorCode.ConflictState, "Spend must have exactly one item.");
            Authz.EnsureGrant(await grants.FindActiveAsync(
                actor.CompanyId, actor.Subject, GrantKind.Spend, null, item.ResourceId, original.PurchaseSystemId, ct));
            if (recipientBlocked)
            {
                throw new MotivaException(ErrorCode.AuthzForbidden,
                    "Correction for a blocked recipient is available only to an administrator (H0 Q03).");
            }

            return;
        }

        Authz.EnsureUser(actor);
        var initiator = await employees.GetAsync(actor.CompanyId, actor.MasterId!.Value, ct);
        Authz.EnsureActiveUser(actor, initiator);
        if (actor.IsAdmin)
        {
            return;
        }

        if (original.Kind == OperationKind.Spend)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "An employee cannot reverse spends (B27.4).");
        }

        var campaign = original.CampaignId is null
            ? null
            : await campaigns.GetAsync(actor.CompanyId, original.CampaignId.Value, ct);
        if (campaign is null || campaign.OwnerMasterId != actor.MasterId || campaign.Status == CampaignStatus.Deleted)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden, "Only the current owner of the source campaign or an administrator may reverse awards.");
        }

        if (recipientBlocked)
        {
            throw new MotivaException(ErrorCode.AuthzForbidden,
                "Correction for a blocked recipient is available only to an administrator (H0 Q03).");
        }
    }
}
