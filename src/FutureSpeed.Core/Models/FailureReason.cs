namespace FutureSpeed.Core.Models;

/// <summary>
/// Why a file could not be transferred, as a stable code rather than a raw OS message.
///
/// The UI turns these into a localized sentence plus a suggested action, and the report
/// groups failures by them, so a user can tell "the file was open in another program"
/// from "I am not allowed to read it" without reading an exception message.
/// </summary>
public enum FailureReason
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>Another program holds the file open and will not share it (sharing/lock violation).</summary>
    InUse = 1,

    /// <summary>The account running Future Speed may not read the source or write the destination.</summary>
    AccessDenied = 2,

    /// <summary>The file or its folder disappeared between the scan and the copy.</summary>
    NotFound = 3,

    /// <summary>The resulting path is longer than the file system accepts.</summary>
    PathTooLong = 4,

    /// <summary>The destination volume has no room left.</summary>
    DiskFull = 5,

    /// <summary>The destination drive was removed, unmounted or went offline.</summary>
    DestinationUnavailable = 6,

    /// <summary>The source drive was removed or went offline mid-transfer.</summary>
    SourceUnavailable = 7,

    /// <summary>The destination is write protected (read-only media or volume).</summary>
    ReadOnlyTarget = 8,

    /// <summary>A network share stopped responding.</summary>
    NetworkError = 9,

    /// <summary>The name contains characters the destination file system rejects.</summary>
    InvalidName = 10,

    /// <summary>The copy finished but the SHA-256 of source and destination differ.</summary>
    VerificationMismatch = 11,

    /// <summary>A bad sector or a device I/O error while reading or writing.</summary>
    DeviceError = 12,

    /// <summary>The user abandoned this single file.</summary>
    SkippedByUser = 13,

    /// <summary>Classified as a failure, but the cause does not match any known case.</summary>
    Unknown = 99
}
