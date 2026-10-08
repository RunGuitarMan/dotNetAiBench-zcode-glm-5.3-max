using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging;
using Motiva.Application;
using Motiva.Application.Ports;

namespace Motiva.Worker;

/// <summary>Claims and executes outbox jobs (§5 stage-2): FinalizeChallenge at endsAt (T07 5 s
/// SLO), export formation, cleanup. Two worker processes run concurrently — claiming is
/// FOR UPDATE SKIP LOCKED with a lease, so a crashed worker's jobs are re-claimed after expiry.</summary>
public sealed class OutboxDispatcherService(
    IServiceProvider services,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = Environment.MachineName + "/" + Environment.ProcessId.ToString();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>();
                await runner.RunOnceAsync(workerId, stoppingToken);
                var jobs = scope.ServiceProvider.GetRequiredService<Motiva.Application.Ports.IBackgroundJobs>();
                Motiva.Infrastructure.MotivaMetrics.SetGauge("outbox:pending", await jobs.CountPendingAsync(stoppingToken));
                Motiva.Infrastructure.MotivaMetrics.Count("outbox:cycles");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.OnCycleFailed(logger, ex);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>Periodic cleanup sweep: deleted export bytes, expired idempotency keys (§3.6, T05).</summary>
public sealed class CleanupSweepService(IServiceProvider services, ILogger<CleanupSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>();
                await runner.SweepCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.OnSweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }


}

file static class Log
{
    private static readonly Action<ILogger, Exception> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, "DispatchCycleFailed"), "Outbox dispatch cycle failed");

    private static readonly Action<ILogger, Exception> SweepFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2, "CleanupSweepFailed"), "Cleanup sweep failed");

    public static void OnCycleFailed(ILogger logger, Exception ex) => CycleFailed(logger, ex);

    public static void OnSweepFailed(ILogger logger, Exception ex) => SweepFailed(logger, ex);
}
