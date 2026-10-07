using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Exports;

/// <summary>Cleanup worker (§3.6, T07): deleted exports have their objects of ALL generations
/// and incomplete uploads removed no later than 10 minutes after dependencies recover; the
/// sweep is idempotent, driven by the persisted cleanup intent. Also purges idempotency keys
/// older than 24 h and expired download-link idempotency entries.</summary>
public sealed class CleanupHandler(
    IExportStore exports,
    IFileStorage storage,
    IIdempotencyStore idempotency,
    TimeProvider timeProvider)
{
    public async Task SweepAsync(CancellationToken ct)
    {
        foreach (var exportId in await exports.ListCleanupIntentsAsync(ct))
        {
            try
            {
                await storage.DeletePrefixAsync("exports/" + exportId.ToString() + "/", ct);
                await exports.ClearCleanupIntentAsync(exportId, ct);
            }
            catch (Exception ex) when (ex is not MotivaException)
            {
                // S3 unavailable: the intent stays; the next sweep retries after recovery.
            }
        }

        await idempotency.PurgeExpiredAsync(timeProvider.GetUtcNow().AddHours(-24), ct);
    }
}
