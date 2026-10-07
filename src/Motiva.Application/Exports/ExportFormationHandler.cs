using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Application.Exports;

/// <summary>
/// Export formation worker (T07, §3.6): snapshot materialization in REPEATABLE READ at the
/// first successful start (B36.2), per-generation S3 keys with put-if-absent semantics,
/// streaming CSV (header/upper-case codes/escaping, sorted by createdAtUtc, operationId,
/// resourceCode; range [from, to)), Ready transition guarded by generation CAS.
/// </summary>
public sealed class ExportFormationHandler(
    IExportStore exports,
    IExportMovementSource movements,
    IFileStorage storage,
    ITestImpediments impediments,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(Guid exportId, CancellationToken ct)
    {
        var export = await exports.LoadAsync(exportId, ct);
        if (export is null || export.Status is ExportStatus.Deleted or ExportStatus.Ready)
        {
            return;
        }

        var leaseOwner = Environment.MachineName + "/" + Environment.ProcessId.ToString();
        var leaseDuration = TimeSpan.FromSeconds(60);
        var snapshot = await exports.TryStartFormingAsync(export.CompanyId, exportId, leaseOwner, leaseDuration, ct);
        if (!snapshot.Started)
        {
            return; // another live lease owns the formation
        }

        var generation = snapshot.NewGeneration;
        var key = ExportsService.S3Key(exportId, generation);
        try
        {
            await impediments.CheckpointAsync(Checkpoints.ExportBeforeSnapshotCommit, ct);
            var operationIds = await exports.GetSnapshotOperationIdsAsync(exportId, ct);
            var ordered = await OrderSnapshotAsync(export, operationIds, ct);
            await using var buffer = new MemoryStream();
            var checksum = await WriteCsvAsync(export, ordered, buffer, ct);
            buffer.Position = 0;
            if (!await storage.ExistsAsync(key, ct))
            {
                await storage.PutAsync(key, buffer, ct);
            }

            await impediments.CheckpointAsync(Checkpoints.ExportAfterUploadBeforeReady, ct);
            var size = await storage.GetSizeAsync(key, ct);
            var storedChecksum = await storage.ComputeChecksumAsync(key, ct);
            if (!string.Equals(storedChecksum, checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stored bytes checksum mismatch.");
            }

            await exports.TryMarkReadyAsync(export.CompanyId, exportId, leaseOwner, generation, key, "v1", size, checksum, timeProvider.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is not MotivaException)
        {
            await exports.TryMarkErrorAsync(export.CompanyId, exportId, leaseOwner, generation, ex.Message, ct);
            throw;
        }
    }

    private async Task<IReadOnlyList<MovementRow>> OrderSnapshotAsync(ExportRec export, IReadOnlyList<Guid> operationIds, CancellationToken ct)
    {
        var rows = new List<MovementRow>();
        await foreach (var row in movements.StreamMovementsAsync(export.CompanyId, operationIds, export.ResourceId, ct))
        {
            rows.Add(row);
        }

        rows.Sort(static (a, b) =>
        {
            var byTime = a.CreatedAtUtc.CompareTo(b.CreatedAtUtc);
            if (byTime != 0)
            {
                return byTime;
            }

            var byId = a.OperationId.CompareTo(b.OperationId);
            return byId != 0 ? byId : string.CompareOrdinal(a.ResourceCode, b.ResourceCode);
        });
        return rows;
    }

    internal static async Task<string> WriteCsvAsync(ExportRec export, IReadOnlyList<MovementRow> rows, Stream output, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var crypto = new CryptoStream(output, sha, CryptoStreamMode.Write, leaveOpen: true);
        await using var writer = new StreamWriter(crypto, new UTF8Encoding(false), leaveOpen: true);

        async Task WriteLineAsync(string line)
        {
            await writer.WriteAsync(line.AsMemory(), ct);
            await writer.WriteAsync("\n".AsMemory(), ct);
        }

        await WriteLineAsync("operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc");
        foreach (var row in rows)
        {
            var line = string.Join(',',
                Csv(row.OperationId.ToString()),
                Csv(row.MasterId.ToString()),
                Csv(row.ResourceCode.ToUpperInvariant()),
                Csv(row.Amount.ToString()),
                Csv(row.Kind),
                Csv(row.CampaignId?.ToString() ?? string.Empty),
                Csv(row.OriginalOperationId?.ToString() ?? string.Empty),
                Csv(row.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")));
            await WriteLineAsync(line);
        }

        await writer.FlushAsync(ct);
        await crypto.FlushAsync(ct);
        await crypto.DisposeAsync();
        return Convert.ToHexString(sha.Hash!);
    }

    private static readonly SearchValues<string> CsvSpecials =
        SearchValues.Create([",", "\"", "\n", "\r"], StringComparison.Ordinal);

    private static string Csv(string field)
    {
        return field.AsSpan().IndexOfAny(CsvSpecials) < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
