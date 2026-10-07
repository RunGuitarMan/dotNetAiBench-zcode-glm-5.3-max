using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>The single economic book: insert with the unique business-number index; reads for
/// histories with keyset pagination and per-actor filters (§3.3).</summary>
public sealed class OperationStore(MotivaDbContext db) : IOperationBook
{
    public Task<OperationRec?> FindByNumberAsync(
        Guid companyId, string initiatorKey, OperationKind kind, string operationNumber, CancellationToken ct)
    {
        return FirstOrDefaultAsync(db.Operations
            .Include(o => o.Items)
            .Where(o => o.CompanyId == companyId && o.InitiatorKey == initiatorKey
                        && o.Kind == kind.ToString() && o.SourceNumber == operationNumber), ct);
    }

    public Task<OperationRec?> GetAsync(Guid companyId, Guid operationId, CancellationToken ct)
    {
        return FirstOrDefaultAsync(db.Operations
            .Include(o => o.Items)
            .Where(o => o.CompanyId == companyId && o.Id == operationId), ct);
    }

    public async Task<OperationRec> LockOriginalAsync(Guid companyId, Guid operationId, CancellationToken ct)
    {
        // Row-level lock of the original: competing reversals serialize here (§3.1 step 1).
        var rows = await db.Database
            .SqlQuery<IdRow>($@"SELECT id AS ""Id"" FROM operations WHERE company_id = {companyId} AND id = {operationId} FOR UPDATE")
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            throw new MotivaException(ErrorCode.NotFound);
        }

        return await FirstOrDefaultAsync(db.Operations
            .Include(o => o.Items)
            .Where(o => o.CompanyId == companyId && o.Id == operationId), ct) ?? throw new MotivaException(ErrorCode.NotFound);
    }

    public async Task InsertAsync(OperationRec operation, CancellationToken ct)
    {
        var row = new OperationRow
        {
            CompanyId = operation.Initiator.CompanyId,
            Id = operation.Id,
            Kind = operation.Kind.ToString(),
            Result = operation.Result.ToString(),
            RefusalCode = operation.RefusalCode?.ToString(),
            InitiatorType = operation.Initiator.ActorType.ToString(),
            InitiatorKey = operation.Initiator.InitiatorKey,
            InitiatorMasterId = operation.Initiator.MasterId,
            InitiatorSubject = operation.Initiator.Subject,
            InitiatorIsAdmin = operation.Initiator.IsAdmin,
            MasterId = operation.MasterId,
            CampaignId = operation.CampaignId,
            PurchaseSystemId = operation.PurchaseSystemId,
            OriginalOperationId = operation.OriginalOperationId,
            Reason = operation.Reason,
            SourceNumber = operation.SourceOperationNumber,
            CreatedAt = operation.CreatedAtUtc,
            EssentialData = operation.EssentialData,
            ResponseStatus = operation.ResponseStatus,
            ResponseBody = operation.ResponseBody,
            Items = operation.Items
                .Select(i => new OperationItemRow { OperationId = operation.Id, ResourceId = i.ResourceId, ResourceCode = i.ResourceCode, Amount = i.Amount, IsDebit = i.IsDebit })
                .ToList(),
        };
        db.Operations.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Page<OperationRec>> ListForEmployeeAsync(
        Guid companyId, int masterId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId,
        OperationKind? kind, OperationResult? result, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<TimeIdCursor>(cursor);
        var query = db.Operations.Include(o => o.Items)
            .Where(o => o.CompanyId == companyId && o.MasterId == masterId);
        query = Filter(query, boundary, from, toUtc, kind, result);
        if (resourceId is { } resource)
        {
            query = query.Where(o => o.Items.Any(i => i.ResourceId == resource));
        }

        return await ToPageAsync(query, limit, ct);
    }

    public async Task<Page<OperationRec>> ListForCampaignAsync(
        Guid companyId, Guid campaignId, int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<TimeIdCursor>(cursor);
        var query = db.Operations.Include(o => o.Items)
            .Where(o => o.CompanyId == companyId && o.CampaignId == campaignId);
        query = Filter(query, boundary, from, toUtc, null, null);
        if (resourceId is { } resource)
        {
            query = query.Where(o => o.Items.Any(i => i.ResourceId == resource));
        }

        return await ToPageAsync(query, limit, ct);
    }

    public async Task<Page<OperationRec>> ListForSpendPairsAsync(
        Guid companyId, IReadOnlyList<(Guid PurchaseSystemId, Guid ResourceId)> pairs, int limit, string? cursor,
        DateTimeOffset? from, DateTimeOffset? toUtc, Guid? resourceId, OperationKind? kind, OperationResult? result, CancellationToken ct)
    {
        // The DB narrows by the necessary condition (systems × resources of the granted pairs);
        // the exact whole-pair check (§3.3 forbids cross-combining) is applied in memory while
        // filling the page, so the result never contains a spurious combination.
        var systemIds = pairs.Select(p => p.PurchaseSystemId).Distinct().ToList();
        var resourceIds = pairs.Select(p => p.ResourceId).Distinct().ToList();
        var pairSet = pairs.Select(p => (p.PurchaseSystemId, p.ResourceId)).ToHashSet();
        var boundary = CursorCodec.Decode<TimeIdCursor>(cursor);
        var emitted = new List<OperationRec>();
        TimeIdCursor? last = boundary;
        while (emitted.Count <= limit)
        {
            var query = db.Operations.Include(o => o.Items)
                .Where(o => o.CompanyId == companyId
                            && (o.Kind == OperationKind.Spend.ToString() || o.Kind == OperationKind.SpendReversal.ToString())
                            && o.PurchaseSystemId != null && systemIds.Contains(o.PurchaseSystemId!.Value)
                            && o.Items.Any(i => resourceIds.Contains(i.ResourceId)));
            query = Filter(query, last, from, toUtc, kind, result);
            if (resourceId is { } resource)
            {
                query = query.Where(o => o.Items.Any(i => i.ResourceId == resource));
            }

            var rows = await query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Take(100).ToListAsync(ct);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                if (row.Items.Any(i => row.PurchaseSystemId is { } system && pairSet.Contains((system, i.ResourceId))))
                {
                    var rec = ToRec(row);
                    emitted.Add(rec);
                    last = new TimeIdCursor(row.CreatedAt, row.Id);
                    if (emitted.Count > limit)
                    {
                        break;
                    }
                }
                else
                {
                    last = new TimeIdCursor(row.CreatedAt, row.Id);
                }
            }

            if (rows.Count < 100)
            {
                break;
            }
        }

        var hasMore = emitted.Count > limit;
        var page = emitted.Take(limit).ToList();
        var next = hasMore && page.Count > 0 ? CursorCodec.Encode(new TimeIdCursor(page[^1].CreatedAtUtc, page[^1].Id)) : null;
        return new Page<OperationRec>(page, next);
    }

    public async Task<Page<OperationRec>> ListAllocationsAsync(
        Guid companyId, Guid campaignId, int limit, string? cursor, CancellationToken ct)
    {
        var boundary = CursorCodec.Decode<TimeIdCursor>(cursor);
        var query = db.Operations.Include(o => o.Items)
            .Where(o => o.CompanyId == companyId && o.CampaignId == campaignId
                        && o.Kind == OperationKind.BudgetAllocation.ToString());
        query = Filter(query, boundary, null, null, null, null);
        return await ToPageAsync(query, limit, ct);
    }

    public async Task<OperationRec?> FindPostedReversalAsync(Guid companyId, Guid originalOperationId, CancellationToken ct)
    {
        return await FirstOrDefaultAsync(db.Operations
            .Where(o => o.CompanyId == companyId && o.OriginalOperationId == originalOperationId
                        && o.Result == OperationResult.Posted.ToString()
                        && (o.Kind == OperationKind.AwardReversal.ToString() || o.Kind == OperationKind.SpendReversal.ToString())), ct);
    }

    public async Task<IReadOnlyList<Guid>> ListMovementOperationIdsAsync(
        Guid companyId, int? masterId, DateTimeOffset from, DateTimeOffset toUtc, Guid? resourceId, CancellationToken ct)
    {
        var kinds = new[] { OperationKind.TaskReward, OperationKind.ManualAward, OperationKind.Spend, OperationKind.SpendReversal, OperationKind.AwardReversal }
            .Select(k => k.ToString()).ToArray();
        var query = db.Operations
            .Where(o => o.CompanyId == companyId && o.Result == OperationResult.Posted.ToString()
                        && kinds.Contains(o.Kind) && o.CreatedAt >= from && o.CreatedAt < toUtc);
        if (masterId is { } master)
        {
            query = query.Where(o => o.MasterId == master);
        }

        if (resourceId is { } resource)
        {
            query = query.Where(o => o.Items.Any(i => i.ResourceId == resource));
        }

        return await query.Select(o => o.Id).ToListAsync(ct);
    }

    private static IQueryable<OperationRow> Filter(
        IQueryable<OperationRow> query, TimeIdCursor? boundary, DateTimeOffset? from, DateTimeOffset? toUtc,
        OperationKind? kind, OperationResult? result)
    {
        if (boundary is not null)
        {
            query = query.Where(o => o.CreatedAt > boundary.At || (o.CreatedAt == boundary.At && o.Id.CompareTo(boundary.Id) > 0));
        }

        if (from is { } fromValue)
        {
            query = query.Where(o => o.CreatedAt >= fromValue);
        }

        if (toUtc is { } toValue)
        {
            query = query.Where(o => o.CreatedAt < toValue);
        }

        if (kind is { } kindValue)
        {
            query = query.Where(o => o.Kind == kindValue.ToString());
        }

        if (result is { } resultValue)
        {
            query = query.Where(o => o.Result == resultValue.ToString());
        }

        return query;
    }

    private static async Task<Page<OperationRec>> ToPageAsync(IQueryable<OperationRow> query, int limit, CancellationToken ct)
    {
        var rows = await query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToRec).ToList();
        var next = rows.Count > limit ? CursorCodec.Encode(new TimeIdCursor(rows[limit - 1].CreatedAt, rows[limit - 1].Id)) : null;
        return new Page<OperationRec>(items, next);
    }

    internal static async Task<OperationRec?> FirstOrDefaultAsync(IQueryable<OperationRow> query, CancellationToken ct)
    {
        var row = await query.FirstOrDefaultAsync(ct);
        return row is null ? null : ToRec(row);
    }

    internal static OperationRec ToRec(OperationRow row)
    {
        var actor = new ActorContext(
            row.CompanyId,
            Enum.Parse<ActorType>(row.InitiatorType),
            row.InitiatorMasterId,
            row.InitiatorSubject ?? string.Empty,
            row.InitiatorIsAdmin);
        return new OperationRec(
            row.Id,
            Enum.Parse<OperationKind>(row.Kind),
            Enum.Parse<OperationResult>(row.Result),
            row.RefusalCode is null ? null : Enum.Parse<RefusalCode>(row.RefusalCode),
            actor,
            row.MasterId,
            row.CampaignId,
            row.PurchaseSystemId,
            row.OriginalOperationId,
            row.Reason,
            row.SourceNumber,
            row.Items.OrderBy(i => i.ResourceId)
                .Select(i => new OperationItemRec(i.ResourceId, i.ResourceCode, i.Amount, i.IsDebit)).ToList(),
            row.CreatedAt,
            row.EssentialData,
            row.ResponseStatus,
            row.ResponseBody);
    }

    internal sealed record TimeIdCursor(DateTimeOffset At, Guid Id);

    private sealed record IdRow(Guid Id);
}
