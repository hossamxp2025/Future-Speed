using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Abstractions;

public sealed record TransferResult(
    TransferJob Job,
    JobState FinalState,
    long TransferredBytes,
    int CopiedFiles,
    int SkippedFiles,
    int FailedFiles,
    TimeSpan Duration,
    IReadOnlyList<TransferItem> Items)
{
    public bool HasFailures => FailedFiles > 0;
}

/// <summary>Runs a <see cref="TransferJob"/>: scan, space check, copy/move, optional verify.</summary>
public interface ITransferEngine
{
    /// <summary>
    /// Executes the job. The job's <see cref="TransferJob.Scan"/> is reused when already present
    /// (so a resumed or pre-validated job is not re-enumerated).
    /// </summary>
    Task<TransferResult> ExecuteAsync(
        TransferJob job,
        IProgress<TransferProgress>? progress,
        PauseToken pauseToken,
        SkipSignal skipSignal,
        CancellationToken cancellationToken);

    /// <summary>Convenience overload for callers that never skip a single file.</summary>
    Task<TransferResult> ExecuteAsync(
        TransferJob job,
        IProgress<TransferProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
        => ExecuteAsync(job, progress, pauseToken, new SkipSignal(), cancellationToken);
}
