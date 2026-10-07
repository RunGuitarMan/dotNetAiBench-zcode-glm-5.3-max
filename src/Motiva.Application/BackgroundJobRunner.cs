using Microsoft.Extensions.Logging;
using Motiva.Application.Common;
using Motiva.Application.Competitions;
using Motiva.Application.Exports;
using Motiva.Application.Ports;

namespace Motiva.Application;

/// <summary>
/// Outbox dispatcher orchestration (§1.3, T06): claims due jobs with FOR UPDATE SKIP LOCKED,
/// extends leases while working, executes type handlers, and completes or retries. Both API
/// processes and workers share the same semantics; a job lost with its process is picked up
/// again after the lease expires. Effects are idempotent by construction (replay-safe).
/// </summary>
public sealed class BackgroundJobRunner(
    IBackgroundJobs jobs,
    FinalizeChallengeHandler finalizeChallenge,
    ExportFormationHandler exportFormation,
    CleanupHandler cleanup,
    TimeProvider timeProvider,
    ILogger<BackgroundJobRunner> logger)
{
    public async Task RunOnceAsync(string workerId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var claimed = await jobs.ClaimAsync(workerId, batch: 10, now, ct);
        if (claimed.Count == 0)
        {
            return;
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(claimed.Select(j => j.Id).ToArray(), workerId, heartbeatCts.Token);
        try
        {
            foreach (var job in claimed)
            {
                try
                {
                    await ExecuteAsync(job, ct);
                    await jobs.CompleteAsync(job.Id, ct);
                }
                catch (RetryAtException retryAtExact)
                {
                    await jobs.FailAsync(job.Id, "not due yet", retryAtExact.RetryAt, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var retryAt = timeProvider.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(job.Attempts, 5))));
                    await jobs.FailAsync(job.Id, ex.Message, retryAt, ct);
                    Log.OutboxJobFailed(logger, job.Type, job.Id, ex);
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
        }
    }

    public async Task SweepCleanupAsync(CancellationToken ct)
    {
        await cleanup.SweepAsync(ct);
    }

    private async Task ExecuteAsync(OutboxJob job, CancellationToken ct)
    {
        switch (job.Type)
        {
            case "finalize-challenge":
                {
                    var payload = CanonicalJson.Deserialize<ChallengePayload>(job.Payload) ?? throw new InvalidOperationException("Bad payload");
                    var done = await finalizeChallenge.HandleAsync(payload.CompanyId, payload.ChallengeId, ct);
                    if (!done)
                    {
                        // endsAt not reached yet under the lock: retry at endsAt
                        throw new RetryAtException(((DateTimeOffset)payload.EndsAt).UtcDateTime);
                    }

                    break;
                }

            case "form-export":
                {
                    var payload = CanonicalJson.Deserialize<ExportPayload>(job.Payload) ?? throw new InvalidOperationException("Bad payload");
                    await exportFormation.HandleAsync(payload.ExportId, ct);
                    break;
                }

            case "cleanup-export":
                {
                    await cleanup.SweepAsync(ct);
                    break;
                }

            case "noop":
                {
                    // T3-04 smoke job used by the functional outbox tests.
                    break;
                }

            default:
                throw new InvalidOperationException("Unknown job type " + job.Type);
        }
    }

    private async Task HeartbeatAsync(Guid[] jobIds, string workerId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                await jobs.ExtendLeaseAsync(jobIds, workerId, timeProvider.GetUtcNow().AddSeconds(30), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static class Log
    {
        private static readonly Action<ILogger, string, Guid, Exception> JobFailed = LoggerMessage.Define<string, Guid>(
            LogLevel.Warning, new EventId(1, "OutboxJobFailed"), "Outbox job {JobType} {JobId} failed; retry scheduled");

        public static void OutboxJobFailed(ILogger logger, string type, Guid id, Exception ex)
            => JobFailed(logger, type, id, ex);
    }

    private sealed record ChallengePayload(Guid CompanyId, Guid ChallengeId, DateTimeOffset EndsAt);

    private sealed record ExportPayload(Guid ExportId);
}

/// <summary>Signals that the job must be retried at an exact time (e.g. finalizer before endsAt).</summary>
public sealed class RetryAtException : Exception
{
    public RetryAtException(DateTime retryAt)
    {
        RetryAt = retryAt;
    }

    public DateTime RetryAt { get; }
}
