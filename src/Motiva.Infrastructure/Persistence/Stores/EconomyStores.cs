using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;
using Npgsql;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>First-aggregate protocol (§3.1): INSERT … ON CONFLICT DO NOTHING as a separate
/// statement, then SELECT … FOR UPDATE — expected unique errors never surface because the
/// insert is conflict-tolerant, so the transaction never needs rolling back mid-flight.</summary>
public sealed class BudgetStore(MotivaDbContext db) : IBudgetLedger
{
    private sealed record BudgetLockRow(Guid CampaignId, Guid ResourceId, long AllocatedTotal, long SpentTotal, long ReturnedTotal);

    public async Task<BudgetRec> LockAsync(Guid companyId, Guid campaignId, Guid resourceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO budgets (campaign_id, resource_id, allocated_total, spent_total, returned_total)
            VALUES ({campaignId}, {resourceId}, 0, 0, 0) ON CONFLICT DO NOTHING
            """, ct);
        var row = await ReadLockedAsync(companyId, campaignId, resourceId, ct);
        var code = await ResourceCodeAsync(companyId, resourceId, ct);
        return Rec(row, code);
    }

    public async Task<BudgetRec?> GetAsync(Guid companyId, Guid campaignId, Guid resourceId, CancellationToken ct)
    {
        var row = await db.Budgets.FindAsync(new object[] { campaignId, resourceId }, ct);
        if (row is null)
        {
            return null;
        }

        var code = await ResourceCodeAsync(companyId, resourceId, ct);
        return Rec(new BudgetLockRow(row.CampaignId, row.ResourceId, row.AllocatedTotal, row.SpentTotal, row.ReturnedTotal), code);
    }

    public async Task<Page<BudgetRec>> ListAsync(Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<ResourceStore.CodeCursor>(cursor);
        var rows = await (from b in db.Budgets
                          join r in db.Resources on b.ResourceId equals r.Id
                          where b.CampaignId == campaignId && r.CompanyId == companyId
                          orderby r.CodeNorm
                          select new { b.CampaignId, b.ResourceId, b.AllocatedTotal, b.SpentTotal, b.ReturnedTotal, r.CodeNorm }).ToListAsync(ct);
        var filtered = rows
            .Where(r => boundary is null || string.Compare(r.CodeNorm, boundary.Code) > 0)
            .Take(limit + 1)
            .ToList();
        var items = filtered.Take(limit)
            .Select(r => new BudgetRec(r.CampaignId, r.ResourceId, r.CodeNorm, r.AllocatedTotal, r.SpentTotal, r.ReturnedTotal))
            .ToList();
        var next = filtered.Count > limit ? CursorCodec.Encode(new ResourceStore.CodeCursor(items[^1].ResourceCode)) : null;
        return new Page<BudgetRec>(items, next);
    }

    public Task AddAllocationAsync(Guid companyId, Guid campaignId, Guid resourceId, long amount, CancellationToken ct)
        => ApplyAsync(companyId, campaignId, resourceId, "allocated_total", amount, ct);

    public Task AddSpendingAsync(Guid companyId, Guid campaignId, Guid resourceId, long amount, CancellationToken ct)
        => ApplyAsync(companyId, campaignId, resourceId, "spent_total", amount, ct);

    public Task AddReturnAsync(Guid companyId, Guid campaignId, Guid resourceId, long amount, CancellationToken ct)
        => ApplyAsync(companyId, campaignId, resourceId, "returned_total", amount, ct);

    private async Task ApplyAsync(Guid companyId, Guid campaignId, Guid resourceId, string column, long amount, CancellationToken ct)
    {
        var sql = FormattableString.Invariant(
            $"UPDATE budgets SET {column} = {column} + @amount WHERE campaign_id = @campaign AND resource_id = @resource");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                sql,
                new[] { new NpgsqlParameter("amount", amount), new NpgsqlParameter("campaign", campaignId), new NpgsqlParameter("resource", resourceId) },
                ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw new BudgetConstraintException(campaignId, resourceId);
        }
    }

    private async Task<BudgetLockRow> ReadLockedAsync(Guid companyId, Guid campaignId, Guid resourceId, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<BudgetLockRow>($"""
                SELECT campaign_id, resource_id, allocated_total, spent_total, returned_total
                FROM budgets WHERE campaign_id = {campaignId} AND resource_id = {resourceId}
                FOR UPDATE
                """)
            .ToListAsync(ct);
        return rows[0];
    }

    private async Task<string> ResourceCodeAsync(Guid companyId, Guid resourceId, CancellationToken ct)
    {
        return await db.Resources.Where(r => r.CompanyId == companyId && r.Id == resourceId).Select(r => r.CodeNorm).FirstAsync(ct);
    }

    private static BudgetRec Rec(BudgetLockRow row, string code)
        => new(row.CampaignId, row.ResourceId, code, row.AllocatedTotal, row.SpentTotal, row.ReturnedTotal);
}

public sealed class WalletStore(MotivaDbContext db) : IWalletLedger
{
    private sealed record BalanceRow(long Balance);

    public async Task<long> LockBalanceAsync(Guid companyId, int masterId, Guid resourceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wallet_balances (company_id, master_id, resource_id, balance)
            VALUES ({companyId}, {masterId}, {resourceId}, 0) ON CONFLICT DO NOTHING
            """, ct);
        var rows = await db.Database
            .SqlQuery<BalanceRow>($"""
                SELECT balance FROM wallet_balances
                WHERE company_id = {companyId} AND master_id = {masterId} AND resource_id = {resourceId}
                FOR UPDATE
                """)
            .ToListAsync(ct);
        return rows[0].Balance;
    }

    public async Task ApplyDeltaAsync(Guid companyId, int masterId, Guid resourceId, long signedDelta, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE wallet_balances SET balance = balance + {signedDelta}
                WHERE company_id = {companyId} AND master_id = {masterId} AND resource_id = {resourceId}
                """, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw new MotivaException(ErrorCode.ValidationOverflow, "Wallet balance would become negative.");
        }
    }

    public async Task<WalletBalanceRec> GetBalanceAsync(Guid companyId, int masterId, Guid resourceId, CancellationToken ct)
    {
        var row = await db.WalletBalances.FindAsync(new object[] { companyId, masterId, resourceId }, ct);
        var code = await ResourceCodeAsync(companyId, resourceId, ct);
        return new WalletBalanceRec(resourceId, code, row?.Balance ?? 0, await StatusAsync(companyId, resourceId, ct));
    }

    public async Task<IReadOnlyList<WalletBalanceRec>> GetWalletAsync(Guid companyId, int masterId, CancellationToken ct)
    {
        // Every company resource appears, zero balances included (B06.6, B22.3).
        var rows = await (from r in db.Resources
                          join b in db.WalletBalances on new { r.Id, CompanyId = companyId, MasterId = masterId }
                              equals new { Id = b.ResourceId, b.CompanyId, b.MasterId } into balances
                          from b in balances.DefaultIfEmpty()
                          where r.CompanyId == companyId
                          orderby r.CodeNorm
                          select new { r.Id, r.CodeNorm, r.Status, Balance = b != null ? b.Balance : 0 }).ToListAsync(ct);
        return rows.Select(x => new WalletBalanceRec(x.Id, x.CodeNorm, x.Balance, Enum.Parse<ResourceStatus>(x.Status))).ToList();
    }

    private async Task<string> ResourceCodeAsync(Guid companyId, Guid resourceId, CancellationToken ct)
        => await db.Resources.Where(r => r.CompanyId == companyId && r.Id == resourceId).Select(r => r.CodeNorm).FirstAsync(ct);

    private async Task<ResourceStatus> StatusAsync(Guid companyId, Guid resourceId, CancellationToken ct)
    {
        var status = await db.Resources.Where(r => r.CompanyId == companyId && r.Id == resourceId).Select(r => r.Status).FirstAsync(ct);
        return Enum.Parse<ResourceStatus>(status);
    }
}
