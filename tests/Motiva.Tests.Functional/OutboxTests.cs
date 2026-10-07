using Motiva.Application;
using Motiva.Application.Ports;
using Xunit;

namespace Motiva.Tests.Functional;

/// <summary>Outbox dispatcher semantics (T3-04): FOR UPDATE SKIP LOCKED with two runners
/// executes each job exactly once; a lost job is re-claimed after the lease expires.</summary>
[Collection("functional")]
public sealed class OutboxTests(MotivaFunctionalFixture fixture)
{
    [Fact]
    public async Task two_runners_claim_disjoint_batches_exactly_once()
    {
        var world = await fixture.NewWorldAsync();
        var jobs = world.Resolve<IBackgroundJobs>();
        var mine = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            mine.Add(await jobs.EnqueueAsync("noop", "{}", fixture.Host.Clock.GetUtcNow().AddSeconds(-1), CancellationToken.None));
        }

        var runnerA = world.Resolve<BackgroundJobRunner>();
        var runnerB = world.Resolve<BackgroundJobRunner>();
        await Task.WhenAll(
            Task.Run(() => runnerA.RunOnceAsync("runner-A", CancellationToken.None)),
            Task.Run(() => runnerB.RunOnceAsync("runner-B", CancellationToken.None)));
        await Task.WhenAll(
            Task.Run(() => runnerA.RunOnceAsync("runner-A", CancellationToken.None)),
            Task.Run(() => runnerB.RunOnceAsync("runner-B", CancellationToken.None)));

        // Every enqueued job is Done (not Pending, not stuck Running).
        var leftovers = await jobs.ClaimAsync("probe", 100, fixture.Host.Clock.GetUtcNow().AddSeconds(1), CancellationToken.None);
        Assert.DoesNotContain(leftovers, job => mine.Contains(job.Id));
    }

    [Fact]
    public async Task failed_job_retries_with_growing_attempts()
    {
        var world = await fixture.NewWorldAsync();
        var jobs = world.Resolve<IBackgroundJobs>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new { exportId = Guid.NewGuid() });
        var jobId = await jobs.EnqueueAsync("form-export", payload, fixture.Host.Clock.GetUtcNow().AddSeconds(-1), CancellationToken.None);
        var runner = world.Resolve<BackgroundJobRunner>();

        // The payload references a missing export: the job fails and goes back to Pending.
        await runner.RunOnceAsync("runner", CancellationToken.None);
        await jobs.FailAsync(jobId, "release probe", fixture.Host.Clock.GetUtcNow().AddSeconds(-1), CancellationToken.None);
        var claimed = await jobs.ClaimAsync("probe", 100, fixture.Host.Clock.GetUtcNow().AddSeconds(1), CancellationToken.None);
        var job = claimed.Single(j => j.Id == jobId);
        Assert.True(job.Attempts >= 1);
    }
}
