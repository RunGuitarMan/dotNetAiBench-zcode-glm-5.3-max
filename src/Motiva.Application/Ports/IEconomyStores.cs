namespace Motiva.Application.Ports;

/// <summary>
/// Budget ledger of (campaign × resource) pairs (B18–B19). All locking methods use
/// SELECT … FOR UPDATE with the §3.1 insert-then-lock protocol; the CHECK constraint
/// allocated − spent + returned ≥ 0 is the final guard.
/// </summary>
public interface IBudgetLedger
{
    /// <summary>Inserts the pair row if absent (no-op on conflict) and locks it FOR UPDATE.</summary>
    Task<BudgetRec> LockAsync(Guid companyId, Guid campaignId, Guid resourceId, CancellationToken ct);

    Task<BudgetRec?> GetAsync(Guid companyId, Guid campaignId, Guid resourceId, CancellationToken ct);

    Task<Page<BudgetRec>> ListAsync(Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct);

    Task AddAllocationAsync(Guid companyId, Guid campaignId, Guid resourceId, long amount, CancellationToken ct);

    /// <summary>Spends from the budget; throws <see cref="BudgetConstraintException"/> if the CHECK would fail.</summary>
    Task AddSpendingAsync(Guid companyId, Guid campaignId, Guid resourceId, long amount, CancellationToken ct);

    Task AddReturnAsync(Guid companyId, Guid campaignId, Guid resourceId, long amount, CancellationToken ct);
}

/// <summary>Raised when the invariant allocated − spent + returned ≥ 0 is violated (defect-level condition).</summary>
public sealed class BudgetConstraintException : Exception
{
    public BudgetConstraintException(Guid campaignId, Guid resourceId)
        : base($"Budget invariant violated for campaign {campaignId} resource {resourceId}.")
    {
    }
}

/// <summary>Wallet balances (B22): one row per (wallet, resource), balance ≥ 0 enforced by CHECK.</summary>
public interface IWalletLedger
{
    /// <summary>Inserts the balance row if absent and locks it FOR UPDATE (§3.1 insert-then-lock).</summary>
    Task<long> LockBalanceAsync(Guid companyId, int masterId, Guid resourceId, CancellationToken ct);

    /// <summary>Applies a signed delta to a locked balance; throws on the balance ≥ 0 CHECK.</summary>
    Task ApplyDeltaAsync(Guid companyId, int masterId, Guid resourceId, long signedDelta, CancellationToken ct);

    Task<WalletBalanceRec> GetBalanceAsync(Guid companyId, int masterId, Guid resourceId, CancellationToken ct);

    /// <summary>Wallet view: balances over all company resources (zero for never-touched resources).</summary>
    Task<IReadOnlyList<WalletBalanceRec>> GetWalletAsync(Guid companyId, int masterId, CancellationToken ct);
}

/// <summary>The single economic book of operations (B24–B28), the source of replay bodies.</summary>
public interface IOperationBook
{
    /// <summary>
    /// Finds an operation by the business number scope (company, initiator, kind, number)
    /// — campaign is deliberately not part of the key (§3.0).
    /// </summary>
    Task<OperationRec?> FindByNumberAsync(
        Guid companyId, string initiatorKey, OperationKind kind, string operationNumber, CancellationToken ct);

    Task<OperationRec?> GetAsync(Guid companyId, Guid operationId, CancellationToken ct);

    /// <summary>Locks the original operation row FOR UPDATE — step 1 of the reversal path (§3.1).</summary>
    Task<OperationRec> LockOriginalAsync(Guid companyId, Guid operationId, CancellationToken ct);

    Task InsertAsync(OperationRec operation, CancellationToken ct);

    Task<Page<OperationRec>> ListForEmployeeAsync(
        Guid companyId,
        int masterId,
        int limit,
        string? cursor,
        DateTimeOffset? from,
        DateTimeOffset? toUtc,
        Guid? resourceId,
        OperationKind? kind,
        OperationResult? result,
        CancellationToken ct);

    Task<Page<OperationRec>> ListForCampaignAsync(
        Guid companyId,
        Guid campaignId,
        int limit,
        string? cursor,
        DateTimeOffset? from,
        DateTimeOffset? toUtc,
        Guid? resourceId,
        CancellationToken ct);

    /// <summary>Spend/SpendReversal rows of given (system, resource) pairs of the company, any initiator (§3.3).</summary>
    Task<Page<OperationRec>> ListForSpendPairsAsync(
        Guid companyId,
        IReadOnlyList<(Guid PurchaseSystemId, Guid ResourceId)> pairs,
        int limit,
        string? cursor,
        DateTimeOffset? from,
        DateTimeOffset? toUtc,
        Guid? resourceId,
        OperationKind? kind,
        OperationResult? result,
        CancellationToken ct);

    Task<Page<OperationRec>> ListAllocationsAsync(
        Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct);

    /// <summary>Posted reversal of an original, if any (≤ 1 enforced by partial unique index).</summary>
    Task<OperationRec?> FindPostedReversalAsync(Guid companyId, Guid originalOperationId, CancellationToken ct);

    /// <summary>All wallet-affecting Posted movements for export, ordered for CSV streaming.</summary>
    Task<IReadOnlyList<Guid>> ListMovementOperationIdsAsync(
        Guid companyId, int? masterId, DateTimeOffset from, DateTimeOffset toUtc, Guid? resourceId, CancellationToken ct);
}
