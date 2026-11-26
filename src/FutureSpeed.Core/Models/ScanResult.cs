namespace FutureSpeed.Core.Models;

/// <summary>Outcome of enumerating the selected sources.</summary>
public sealed class ScanResult
{
    public List<TransferItem> Files { get; } = new();
    public List<TransferDirectory> Directories { get; } = new();

    /// <summary>Paths that could not be enumerated (access denied, too long, removed mid-scan).</summary>
    public List<ScanProblem> Problems { get; } = new();

    public long TotalBytes { get; set; }
    public int FileCount => Files.Count;

    /// <summary>True when at least one source could not be fully enumerated, so TotalBytes is a lower bound.</summary>
    public bool IsTotalExact => Problems.Count == 0;
}

/// <summary>A source path that could not even be enumerated, so it was never offered to the copy phase.</summary>
/// <param name="Path">The full path that failed.</param>
/// <param name="Reason">The message from the file system.</param>
/// <param name="Kind">Stable cause code, so the UI and the report treat it like any other failure.</param>
public sealed record ScanProblem(string Path, string Reason, FailureReason Kind = FailureReason.Unknown);
