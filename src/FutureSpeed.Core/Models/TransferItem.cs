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

    /// <summary>Raw message for the log; the UI shows <see cref="Reason"/> instead.</summary>
    public string? Error { get; set; }

    /// <summary>Why it failed, as a stable code the UI can localize and the report can group by.</summary>
    public FailureReason Reason { get; set; } = FailureReason.None;

    /// <summary>The Win32 error code behind the failure, when the OS supplied one.</summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>How many attempts the engine made before giving up.</summary>
    public int Attempts { get; set; }

    /// <summary>When the failure was recorded, for the report's timeline.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    public bool HasFailed => Outcome is FileOutcome.Failed or FileOutcome.VerificationFailed;

    /// <summary>"InUse (32)" — the technical code shown next to the human explanation.</summary>
    public string? ReasonCode => Reason == FailureReason.None
        ? null
        : NativeErrorCode is { } code ? $"{Reason} ({code})" : Reason.ToString();
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
