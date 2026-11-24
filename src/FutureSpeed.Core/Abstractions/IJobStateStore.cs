using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

/// <summary>Everything needed to resume a job after a crash or a restart.</summary>
public sealed class JobCheckpoint
{
    public required string JobId { get; init; }
    public string Name { get; set; } = string.Empty;
    public List<string> Sources { get; init; } = new();
    public string DestinationRoot { get; set; } = string.Empty;
    public TransferMode Mode { get; set; }
    public TransferOptions Options { get; set; } = new();
    public JobState State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public long TransferredBytes { get; set; }

    /// <summary>Relative paths already finished (copied, moved, skipped) so they are not redone.</summary>
    public List<string> CompletedRelativePaths { get; init; } = new();

    /// <summary>Relative paths that failed, offered for retry.</summary>
    public List<string> FailedRelativePaths { get; init; } = new();
}

public interface IJobStateStore
{
    Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<JobCheckpoint?> LoadAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobCheckpoint>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Checkpoints of jobs that were running when the app stopped.</summary>
    Task<IReadOnlyList<JobCheckpoint>> LoadResumableAsync(CancellationToken cancellationToken = default);
}
