using Microsoft.EntityFrameworkCore;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Persistence.Stores;

/// <summary>Streams posted wallet movements for the export CSV without materializing the whole
/// statement in memory (T07); rows arrive ordered and are re-sorted deterministically by the
/// formation handler.</summary>
public sealed class MovementSource(MotivaDbContext db) : IExportMovementSource
{
    public async IAsyncEnumerable<MovementRow> StreamMovementsAsync(
        Guid companyId, IReadOnlyList<Guid> operationIds, Guid? resourceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var chunkSize = 500;
        foreach (var chunk in operationIds.Chunk(chunkSize))
        {
            var rows = await (from o in db.Operations
                              join i in db.OperationItems on o.Id equals i.OperationId
                              where o.CompanyId == companyId && chunk.Contains(o.Id)
                                    && (resourceId == null || i.ResourceId == resourceId)
                              orderby o.CreatedAt, o.Id, i.ResourceCode
                              select new { o.Id, o.MasterId, i.ResourceCode, SignedAmount = i.IsDebit ? -i.Amount : i.Amount, o.Kind, o.CampaignId, o.OriginalOperationId, o.CreatedAt })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                yield return new MovementRow(
                    row.Id, row.MasterId!.Value, row.ResourceCode, row.SignedAmount, row.Kind, row.CampaignId, row.OriginalOperationId, row.CreatedAt);
            }
        }
    }
}
