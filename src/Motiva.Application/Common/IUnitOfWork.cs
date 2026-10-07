namespace Motiva.Application.Common;

public interface IUnitOfWork
{
    Task<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One business transaction (B17): everything written through the stores inside the scope
/// commits atomically; a technical failure rolls the whole result back, leaving no half of
/// an event/completion/reward/movement behind.
/// </summary>
public interface IUnitOfWorkScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic pause/failure seam for orchestration checkpoints (stage-3 T3-09). Production
/// composition registers <see cref="NoOpImpediments"/>; tests substitute named pauses and
/// injected failures. Validation and authorization pipelines are never bypassed by the seam.
/// </summary>
public interface ITestImpediments
{
    Task CheckpointAsync(string name, CancellationToken cancellationToken);
}

public sealed class NoOpImpediments : ITestImpediments
{
    public static readonly NoOpImpediments Instance = new();

    public Task CheckpointAsync(string name, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Simulated infrastructure failure raised by the test seam: rolls the transaction back
/// without committing anything (technical failure without a committed result — H0 Q08).
/// </summary>
public sealed class ImpedimentFailureException : Exception
{
    public ImpedimentFailureException(string name)
        : base("Injected technical failure at checkpoint " + name)
    {
        Checkpoint = name;
    }

    public string Checkpoint { get; }
}

/// <summary>Named orchestration checkpoints used with <see cref="ITestImpediments"/>.</summary>
public static class Checkpoints
{
    public const string ProgressAfterLocksBeforeTime = "progress.after-locks-before-time";
    public const string ProgressBeforeCommit = "progress.before-commit";
    public const string ProgressAfterCommitBeforeResponse = "progress.after-commit-before-response";
    public const string ExportBeforeSnapshotCommit = "export.before-snapshot-commit";
    public const string ExportAfterUploadBeforeReady = "export.after-upload-before-ready";
}
