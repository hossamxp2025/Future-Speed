namespace FutureSpeed.Core.Models;

/// <summary>One planned file copy: an absolute source and the absolute destination it maps to.</summary>
public sealed class TransferItem
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }

    /// <summary>Path relative to the destination root, used for reports and the UI.</summary>
    public required string RelativePath { get; init; }

    public long Size { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public FileAttributes Attributes { get; init; }

    public FileOutcome Outcome { get; set; } = FileOutcome.Pending;
    public string? Error { get; set; }
    public string? SourceHash { get; set; }
    public string? DestinationHash { get; set; }

    /// <summary>Destination actually written (differs from <see cref="DestinationPath"/> when auto-renamed).</summary>
    public string? EffectiveDestinationPath { get; set; }

    public override string ToString() => RelativePath;
}

/// <summary>A directory that must exist at the destination (kept so empty folders survive).</summary>
public sealed class TransferDirectory
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public FileAttributes Attributes { get; init; }
}
