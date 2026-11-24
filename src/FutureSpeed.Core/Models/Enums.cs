namespace FutureSpeed.Core.Models;

/// <summary>Copy keeps the source, Move removes it only after a verified copy.</summary>
public enum TransferMode
{
    Copy = 0,
    Move = 1
}

/// <summary>What to do when the destination file already exists.</summary>
public enum ConflictPolicy
{
    /// <summary>Ask the user (resolved through <see cref="Abstractions.IConflictResolver"/>).</summary>
    Ask = 0,
    Overwrite = 1,
    Skip = 2,
    /// <summary>Copy with an automatically generated unique name, e.g. "file (2).txt".</summary>
    AutoRename = 3,
    /// <summary>Overwrite only when the source is newer than the destination, otherwise skip.</summary>
    KeepNewest = 4,
    /// <summary>Skip when size and last-write-time match, otherwise overwrite.</summary>
    OverwriteIfDifferent = 5
}

public enum JobState
{
    Created = 0,
    Scanning = 1,
    Validating = 2,
    Running = 3,
    Paused = 4,
    Completed = 5,
    CompletedWithErrors = 6,
    Failed = 7,
    Canceled = 8,
    Queued = 9
}

public enum FileOutcome
{
    Pending = 0,
    Copied = 1,
    Moved = 2,
    Skipped = 3,
    Renamed = 4,
    Overwritten = 5,
    Failed = 6,
    VerificationFailed = 7,
    Canceled = 8
}

public enum DriveKindHint
{
    Unknown = 0,
    Hdd = 1,
    Ssd = 2,
    Nvme = 3,
    Usb = 4,
    Network = 5,
    Optical = 6,
    RamDisk = 7
}

public enum SpaceValidationStatus
{
    Sufficient = 0,
    Insufficient = 1,
    /// <summary>Free space or required size could not be determined (e.g. some network shares).</summary>
    Undeterminable = 2,
    /// <summary>The destination path is not usable (missing, read-only, inside the source, ...).</summary>
    InvalidDestination = 3
}

public enum ReportFormat
{
    Json = 0,
    Csv = 1,
    Txt = 2
}

public enum SyncDirection
{
    /// <summary>Make the destination match the source (extra destination files may be removed).</summary>
    Mirror = 0,
    /// <summary>Copy new/changed files from source to destination, never delete.</summary>
    OneWay = 1,
    /// <summary>Copy newer files in both directions, never delete.</summary>
    TwoWay = 2
}
