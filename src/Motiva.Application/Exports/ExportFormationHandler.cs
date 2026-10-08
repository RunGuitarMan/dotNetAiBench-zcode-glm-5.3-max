using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Exports;

/// <summary>
/// Export formation worker (T07, §3.6): snapshot materialization in REPEATABLE READ at the
/// first successful start (B36.2), per-generation S3 keys, streaming CSV upload in bounded
/// multipart parts (header/upper-case codes/escaping; contract order createdAtUtc,
/// operationId, resourceCode comes from the keyset DB stream), Ready guarded by generation CAS.
/// </summary>
public sealed class ExportFormationHandler(
    IExportStore exports,
    ISnapshotRowSource rows,
    IFileStorage storage,
    ITestImpediments impediments,
    TimeProvider timeProvider)
{
    private const int PartSize = 8 * 1024 * 1024;

    /// <summary>Runs one formation attempt. Returns true when the job is FINISHED (Ready,
    /// reused bytes, deleted or already handled elsewhere); false when ANOTHER live lease owns
    /// the formation — the caller must retry the job later, never complete it silently (a
    /// crashed worker's job re-claimed while its export lease is still live would otherwise
    /// leave the export stuck in Forming forever, S-4).</summary>
    public async Task<bool> HandleAsync(Guid exportId, CancellationToken ct)
    {
        var export = await exports.LoadAsync(exportId, ct);
        if (export is null || export.Status is ExportStatus.Deleted or ExportStatus.Ready)
        {
            return true;
        }

        var leaseOwner = Environment.MachineName + "/" + Environment.ProcessId.ToString();
        var leaseDuration = TimeSpan.FromSeconds(60);
        var snapshot = await exports.TryStartFormingAsync(export.CompanyId, exportId, leaseOwner, leaseDuration, ct);
        if (!snapshot.Started)
        {
            return false; // another live lease owns the formation: retry after it expires
        }

        var generation = snapshot.NewGeneration;
        var key = ExportsService.S3Key(exportId, generation);
        try
        {
            await impediments.CheckpointAsync(Checkpoints.ExportBeforeSnapshotCommit, ct);

            // Deletion before the upload: nothing to clean up, just stop.
            var beforeUpload = await exports.LoadAsync(exportId, ct);
            if (beforeUpload is null || beforeUpload.Status == ExportStatus.Deleted)
            {
                return true;
            }

            if (await storage.ExistsAsync(key, ct))
            {
                // The per-attempt key is unique; existing bytes are our own retry — verify and reuse.
                await VerifyAndReadyAsync(export.CompanyId, exportId, leaseOwner, generation, key, ct);
                return true;
            }

            var (size, checksum) = await UploadCsvAsync(export, exportId, leaseOwner, generation, key, ct);
            await impediments.CheckpointAsync(Checkpoints.ExportAfterUploadBeforeReady, ct);

            // Deletion against a late worker: the upload is already complete, so remove our own
            // object and leave the row Deleted — no resurrection, no leftover bytes (§3.6).
            var current = await exports.LoadAsync(exportId, ct);
            if (current is null || current.Status == ExportStatus.Deleted)
            {
                try
                {
                    await storage.DeletePrefixAsync("exports/" + exportId.ToString() + "/", ct);
                }
                catch (Exception deleteEx) when (deleteEx is not MotivaException)
                {
                    // The persisted cleanup intent of the deleted export will retry.
                }

                return true;
            }

            var readied = await exports.TryMarkReadyAsync(
                export.CompanyId, exportId, leaseOwner, generation, key, "v1", size, checksum, timeProvider.GetUtcNow(), ct);
            if (readied)
            {
                // Losing attempts may have left their own objects behind (e.g. a crash between
                // upload and Ready): the winner removes every sibling under the export prefix,
                // so bytes exist for the Ready generation only (§3.6, T07).
                try
                {
                    await storage.DeleteOthersAsync("exports/" + exportId.ToString() + "/", key, ct);
                }
                catch (Exception siblingEx) when (siblingEx is not MotivaException)
                {
                    // The Ready bytes are unaffected; a leftover sibling object is removed by
                    // the cleanup sweep once this export is deleted.
                }
            }
        }
        catch (Exception ex) when (ex is not MotivaException)
        {
            await exports.TryMarkErrorAsync(export.CompanyId, exportId, leaseOwner, generation, ex.Message, ct);
            throw;
        }

        return true;
    }

    /// <summary>Streams rows from the fixed snapshot into bounded multipart parts: memory holds
    /// at most one part plus one keyset page (T07). Before every part the worker re-asserts it
    /// still owns the live formation (row not deleted, lease extended): a worker whose lease
    /// expired — or an export deleted mid-upload — aborts instead of writing bytes a later
    /// cleanup would not know about (§3.6).</summary>
    private sealed class UploadState
    {
        public long Size;
    }

    private async Task<(long Size, string Checksum)> UploadCsvAsync(
        ExportRec export, Guid exportId, string leaseOwner, int generation, string key, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var state = new UploadState();
        var parts = ProducePartsAsync(export, exportId, leaseOwner, generation, sha, state, ct);
        await storage.PutPartsAsync(key, parts, ct);
        lock (sha)
        {
            sha.TransformFinalBlock([], 0, 0);
        }

        return (state.Size, Convert.ToHexString(sha.Hash!));
    }

    /// <summary>Fence of the formation attempt: the row must not be deleted and the lease must
    /// still be ours; otherwise the upload is aborted (the multipart upload is rolled back by
    /// the storage adapter).</summary>
    private async Task EnsureStillFormingAsync(Guid exportId, string leaseOwner, int generation, CancellationToken ct)
    {
        await impediments.CheckpointAsync(Checkpoints.ExportDuringUpload, ct);
        var current = await exports.LoadAsync(exportId, ct);
        if (current is null || current.Status == ExportStatus.Deleted)
        {
            throw new InvalidOperationException("The export was deleted during formation.");
        }

        if (!await exports.TryExtendLeaseAsync(current.CompanyId, exportId, leaseOwner, generation, ct))
        {
            throw new InvalidOperationException("The formation lease was lost to another attempt.");
        }
    }

    private async IAsyncEnumerable<byte[]> ProducePartsAsync(
        ExportRec export,
        Guid exportId,
        string leaseOwner,
        int generation,
        SHA256 sha,
        UploadState state,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var part = new MemoryStream();
        var wroteHeader = false;
        await foreach (var row in rows.StreamSnapshotRowsAsync(export.CompanyId, export.Id, export.ResourceId, ct))
        {
            if (!wroteHeader)
            {
                await WriteLinesAsync(part, ["operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc"], ct);
                wroteHeader = true;
            }

            var line = string.Join(',',
                Csv(row.OperationId.ToString()),
                Csv(row.MasterId.ToString()),
                Csv(row.ResourceCode.ToUpperInvariant()),
                Csv(row.Amount.ToString()),
                Csv(row.Kind),
                Csv(row.CampaignId?.ToString() ?? string.Empty),
                Csv(row.OriginalOperationId?.ToString() ?? string.Empty),
                Csv(row.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")));
            await WriteLinesAsync(part, [line], ct);
            if (part.Length >= PartSize)
            {
                await EnsureStillFormingAsync(exportId, leaseOwner, generation, ct);
                yield return YieldPart(part, sha, state);
            }
        }

        if (!wroteHeader)
        {
            await WriteLinesAsync(part, ["operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc"], ct);
        }

        if (part.Length > 0)
        {
            await EnsureStillFormingAsync(exportId, leaseOwner, generation, ct);
            yield return YieldPart(part, sha, state);
        }
    }

    private static byte[] YieldPart(MemoryStream part, SHA256 sha, UploadState state)
    {
        var bytes = part.ToArray();
        part.SetLength(0);
        lock (sha)
        {
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        state.Size += bytes.Length;
        return bytes;
    }

    private static async Task WriteLinesAsync(MemoryStream part, IReadOnlyList<string> lines, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await part.WriteAsync(bytes, ct);
        }
    }

    private async Task VerifyAndReadyAsync(Guid companyId, Guid exportId, string leaseOwner, int generation, string key, CancellationToken ct)
    {
        var size = await storage.GetSizeAsync(key, ct);
        var checksum = await storage.ComputeChecksumAsync(key, ct);
        await exports.TryMarkReadyAsync(companyId, exportId, leaseOwner, generation, key, "v1", size, checksum, timeProvider.GetUtcNow(), ct);
    }

    private static readonly SearchValues<string> CsvSpecials =
        SearchValues.Create([",", "\"", "\n", "\r"], StringComparison.Ordinal);

    private static string Csv(string field)
    {
        return field.AsSpan().IndexOfAny(CsvSpecials) < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
