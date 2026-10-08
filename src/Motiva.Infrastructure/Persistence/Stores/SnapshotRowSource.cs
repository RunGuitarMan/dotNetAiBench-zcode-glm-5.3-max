using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>
/// Streams snapshot rows for the export CSV straight from PostgreSQL in the contract order
/// (createdAtUtc, operationId, resourceCode) — no in-memory sorting or full materialization;
/// the keyset cursor keeps memory bounded at one page (T07).
/// </summary>
public sealed class SnapshotRowSource(MotivaDbContext db) : ISnapshotRowSource
{
    private const int PageSize = 1000;

    public async IAsyncEnumerable<MovementRow> StreamSnapshotRowsAsync(
        Guid companyId, Guid exportId, Guid? resourceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastOperationId = null;
        string? lastResourceCode = null;
        while (true)
        {
            var from = lastCreatedAt;
            var fromId = lastOperationId;
            var fromCode = lastResourceCode;
            var page = await BuildQuery(companyId, exportId, resourceId, from, fromId, fromCode)
                .Take(PageSize + 1)
                .ToListAsync(ct);
            if (page.Count == 0)
            {
                yield break;
            }

            foreach (var row in page)
            {
                yield return row;
                lastCreatedAt = row.CreatedAtUtc;
                lastOperationId = row.OperationId;
                lastResourceCode = row.ResourceCode;
            }

            if (page.Count <= PageSize)
            {
                yield break;
            }
        }
    }

    private IQueryable<MovementRow> BuildQuery(
        Guid companyId, Guid exportId, Guid? resourceId,
        DateTimeOffset? lastCreatedAt, Guid? lastOperationId, string? lastResourceCode)
    {
        // Column-level keyset predicate (server-translatable); the row constructor is only
        // the final projection.
        var baseQuery = from eoi in db.ExportOperationIds
                        join o in db.Operations on eoi.OperationId equals o.Id
                        join i in db.OperationItems on o.Id equals i.OperationId
                        where eoi.ExportId == exportId && o.CompanyId == companyId
                              && (resourceId == null || i.ResourceId == resourceId)
                        orderby o.CreatedAt, o.Id, i.ResourceCode
                        select new
                        {
                            o.CreatedAt,
                            o.Id,
                            o.MasterId,
                            i.ResourceCode,
                            i.Amount,
                            i.IsDebit,
                            o.Kind,
                            o.CampaignId,
                            o.OriginalOperationId,
                        };
        if (lastCreatedAt is null || lastOperationId is null || lastResourceCode is null)
        {
            return baseQuery.Select(x => new MovementRow(
                x.Id, x.MasterId!.Value, x.ResourceCode, x.IsDebit ? -x.Amount : x.Amount,
                x.Kind, x.CampaignId, x.OriginalOperationId, x.CreatedAt));
        }

        var boundaryTime = lastCreatedAt.Value;
        var boundaryId = lastOperationId.Value;
        var boundaryCode = lastResourceCode;
        return baseQuery
            .Where(x =>
                x.CreatedAt > boundaryTime
                || (x.CreatedAt == boundaryTime && x.Id.CompareTo(boundaryId) > 0)
                || (x.CreatedAt == boundaryTime && x.Id == boundaryId && string.Compare(x.ResourceCode, boundaryCode) > 0))
            .Select(x => new MovementRow(
                x.Id, x.MasterId!.Value, x.ResourceCode, x.IsDebit ? -x.Amount : x.Amount,
                x.Kind, x.CampaignId, x.OriginalOperationId, x.CreatedAt));
    }
}
