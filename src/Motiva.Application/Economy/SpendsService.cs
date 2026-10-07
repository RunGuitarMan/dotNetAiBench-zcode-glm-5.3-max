using Motiva.Application.Common;
using Motiva.Application.Dto;
using Motiva.Application.Ports;

namespace Motiva.Application.Economy;

/// <summary>Spends (B23, B24): an employee spends from their own wallet, or a purchase system
/// with a Spend grant spends for a named employee. Declines are saved results; the wallet
/// balance row is the only lock (§3.1); a spend never returns funds to a campaign budget.</summary>
public sealed class SpendsService(
    IEmployeeDirectory employees,
    IResourceDirectory resources,
    IPurchaseSystemDirectory systems,
    IIntegrationDirectory grants,
    IWalletLedger wallets,
    IOperationBook operations,
    IUnitOfWork uow,
    TimeProvider timeProvider)
{
    public async Task<CommandResponse> SpendAsync(
        ActorContext actor, int? masterId, Guid purchaseSystemId, Guid resourceId, long amount, string operationNumber, CancellationToken ct)
    {
        Guard.ExternalNumber(operationNumber);
        Guard.PositiveAmount(amount);
        var essential = CanonicalJson.Serialize(new { masterId, purchaseSystemId, resourceId, amount });

        int walletOwner;
        if (actor.ActorType == ActorType.Service)
        {
            if (masterId is null)
            {
                throw new MotivaException(ErrorCode.ValidationFailed, "masterId is required for a service-initiated spend.");
            }

            Guard.MasterId(masterId.Value);
            walletOwner = masterId.Value;
            // §3.0 step 2: the exact pair grant must be active — for the new request and for replay.
            Authz.EnsureGrant(await grants.FindActiveAsync(actor.CompanyId, actor.Subject, GrantKind.Spend, null, resourceId, purchaseSystemId, ct));
        }
        else
        {
            Authz.EnsureUser(actor);
            if (masterId is not null && masterId != actor.MasterId)
            {
                throw new MotivaException(ErrorCode.AuthzForbidden, "An employee can spend only from their own wallet.");
            }

            walletOwner = actor.MasterId!.Value;
            var initiator = await employees.GetAsync(actor.CompanyId, walletOwner, ct);
            Authz.EnsureActiveUser(actor, initiator);
        }

        var existing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, OperationKind.Spend, operationNumber, ct);
        if (existing is not null)
        {
            return Replay(existing, essential);
        }

        var system = await systems.GetAsync(actor.CompanyId, purchaseSystemId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        var resource = await resources.GetAsync(actor.CompanyId, resourceId, ct) ?? throw new MotivaException(ErrorCode.NotFound);
        if (system.Status != ContentStatus.Active || !system.AcceptedResourceIds.Contains(resourceId))
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "The purchase system does not accept this resource.");
        }

        var recipient = await employees.GetAsync(actor.CompanyId, walletOwner, ct)
            ?? throw new MotivaException(ErrorCode.NotFound, "Wallet owner profile not found.");

        var now = timeProvider.GetUtcNow();
        await using var scope = await uow.BeginAsync(ct);
        var racing = await operations.FindByNumberAsync(actor.CompanyId, actor.InitiatorKey, OperationKind.Spend, operationNumber, ct);
        if (racing is not null)
        {
            return Replay(racing, essential);
        }

        RefusalCode? refusal = null;
        var posted = false;
        if (!recipient.IsActive)
        {
            // §3.3: a blocked wallet owner declines a NEW service spend as a saved result;
            // an employee-initiated spend was already rejected by 403 above.
            refusal = RefusalCode.RecipientNotActive;
        }
        else if (resource.Status == ResourceStatus.Archived)
        {
            refusal = RefusalCode.ResourceUnavailable;
        }
        else
        {
            var balance = await wallets.LockBalanceAsync(actor.CompanyId, walletOwner, resourceId, ct);
            if (balance < amount)
            {
                refusal = RefusalCode.InsufficientFunds;
            }
            else
            {
                await wallets.ApplyDeltaAsync(actor.CompanyId, walletOwner, resourceId, -amount, ct);
                posted = true;
            }
        }

        var operation = new OperationRec(
            Guid.NewGuid(), OperationKind.Spend, posted ? OperationResult.Posted : OperationResult.Declined,
            posted ? null : refusal, actor, walletOwner, null, purchaseSystemId, null, null, operationNumber,
            new[] { new OperationItemRec(resourceId, resource.Code, amount, IsDebit: true) },
            now, essential, null, null);
        var body = CanonicalJson.Serialize(DtoMapper.ToDto(operation));
        operation = operation with { ResponseStatus = 201, ResponseBody = body };
        await operations.InsertAsync(operation, ct);
        await scope.CommitAsync(ct);
        return new CommandResponse(201, body, "/api/v1/operations/" + operation.Id.ToString());
    }

    private static CommandResponse Replay(OperationRec existing, string essential)
    {
        if (!string.Equals(existing.EssentialData, essential, StringComparison.Ordinal))
        {
            throw new MotivaException(ErrorCode.ConflictBusinessNumber);
        }

        return new CommandResponse(existing.ResponseStatus ?? 201, existing.ResponseBody ?? "{}", "/api/v1/operations/" + existing.Id.ToString());
    }
}
