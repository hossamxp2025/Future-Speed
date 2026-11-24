namespace FutureSpeed.Core.Models;

/// <summary>An immutable snapshot of a running job, published a few times per second.</summary>
public sealed class TransferProgress
{
    public string JobId { get; init; } = string.Empty;
    public JobState State { get; init; }

    public long TotalBytes { get; init; }
    public long TransferredBytes { get; init; }
    public long RemainingBytes => Math.Max(0, TotalBytes - TransferredBytes);

    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public int FailedFiles { get; init; }
    public int RemainingFiles => Math.Max(0, TotalFiles - CompletedFiles - SkippedFiles - FailedFiles);

    /// <summary>Smoothed instantaneous throughput in bytes per second.</summary>
    public double CurrentBytesPerSecond { get; init; }

    /// <summary>Average throughput over the whole job in bytes per second.</summary>
    public double AverageBytesPerSecond { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Null when it cannot be estimated yet.</summary>
    public TimeSpan? Eta { get; init; }

    public double PercentComplete => TotalBytes > 0
        ? Math.Clamp(TransferredBytes * 100d / TotalBytes, 0, 100)
        : (TotalFiles > 0 ? Math.Clamp(CompletedFiles * 100d / TotalFiles, 0, 100) : 0);

    public string? CurrentFileName { get; init; }
    public string? CurrentFilePath { get; init; }

    /// <summary>Destination of the file being written right now.</summary>
    public string? CurrentFileDestination { get; init; }

    /// <summary>Bytes written so far of the current file, and its total size.</summary>
    public long CurrentFileBytes { get; init; }
    public long CurrentFileSize { get; init; }

    public double CurrentFilePercent => CurrentFileSize > 0
        ? Math.Clamp(CurrentFileBytes * 100d / CurrentFileSize, 0, 100)
        : 0;

    /// <summary>Set during the scan/verify phases so the UI can show an indeterminate state.</summary>
    public string? PhaseLabel { get; init; }

    public bool IsVerifying { get; init; }

    public static TransferProgress Empty { get; } = new();
}
