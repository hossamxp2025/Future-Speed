using System.ComponentModel;
using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Utils;

/// <summary>A failure translated into something a human can act on.</summary>
/// <param name="Reason">Stable cause code.</param>
/// <param name="NativeCode">The Win32 error code when the OS supplied one, otherwise null.</param>
/// <param name="Message">A short English explanation; the UI shows its own localized text instead.</param>
public readonly record struct FailureDiagnosis(FailureReason Reason, int? NativeCode, string Message)
{
    /// <summary>"InUse (32)" — what goes into logs and the report's technical column.</summary>
    public string Code => NativeCode is { } code
        ? $"{Reason} ({code})"
        : Reason.ToString();
}

/// <summary>
/// Turns an exception from the file system into a <see cref="FailureReason"/>.
///
/// Windows reports almost everything as <see cref="IOException"/> with a Win32 code hidden
/// in the HRESULT, so the code is the reliable signal; the exception type is the fallback.
/// </summary>
public static class FailureClassifier
{
    // Win32 error codes (winerror.h) that users actually hit while copying files.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidDrive = 15;
    private const int ErrorWriteProtect = 19;
    private const int ErrorNotReady = 21;
    private const int ErrorCrc = 23;
    private const int ErrorSeek = 25;
    private const int ErrorWriteFault = 29;
    private const int ErrorReadFault = 30;
    private const int ErrorGenFailure = 31;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorNetworkUnreachable = 53;    // ERROR_BAD_NETPATH
    private const int ErrorNetworkBusy = 54;
    private const int ErrorDevNotExist = 55;
    private const int ErrorNetworkAccessDenied = 65;
    private const int ErrorBadNetName = 67;
    private const int ErrorFileExists = 80;
    private const int ErrorDiskFull = 112;
    private const int ErrorInvalidName = 123;
    private const int ErrorDirNotEmpty = 145;
    private const int ErrorBadPathname = 161;
    private const int ErrorFilenameExcedRange = 206;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorUnexpNetErr = 59;
    private const int ErrorNoSuchDevice = 433;         // ERROR_NO_SUCH_DEVICE
    private const int ErrorDeviceRemoved = 1617;
    private const int ErrorIoDevice = 1117;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorDiskCorrupt = 1393;
    private const int ErrorFileCorrupt = 1392;

    /// <summary>Classifies an exception, optionally knowing whether the source or the destination was being touched.</summary>
    public static FailureDiagnosis Classify(Exception? exception)
    {
        if (exception is null) return new FailureDiagnosis(FailureReason.Unknown, null, "Unknown failure.");

        // AggregateException and friends hide the real cause one level down.
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            return Classify(aggregate.InnerExceptions[0]);

        var native = NativeCode(exception);
        if (native is { } code)
        {
            var byCode = FromNativeCode(code);
            if (byCode != FailureReason.Unknown)
                return new FailureDiagnosis(byCode, code, Describe(byCode, exception));
        }

        var reason = exception switch
        {
            UnauthorizedAccessException => FailureReason.AccessDenied,
            PathTooLongException => FailureReason.PathTooLong,
            FileNotFoundException => FailureReason.NotFound,
            DirectoryNotFoundException => FailureReason.NotFound,
            DriveNotFoundException => FailureReason.DestinationUnavailable,
            NotSupportedException => FailureReason.InvalidName,
            ArgumentException => FailureReason.InvalidName,
            _ => FailureReason.Unknown
        };

        return new FailureDiagnosis(reason, native, Describe(reason, exception));
    }

    /// <summary>Maps a raw Win32 code; <see cref="FailureReason.Unknown"/> when it is not one we recognise.</summary>
    public static FailureReason FromNativeCode(int code) => code switch
    {
        ErrorFileNotFound or ErrorPathNotFound => FailureReason.NotFound,
        ErrorAccessDenied or ErrorNetworkAccessDenied => FailureReason.AccessDenied,
        ErrorSharingViolation or ErrorLockViolation => FailureReason.InUse,
        ErrorDiskFull or ErrorHandleDiskFull => FailureReason.DiskFull,
        ErrorWriteProtect => FailureReason.ReadOnlyTarget,
        ErrorFilenameExcedRange => FailureReason.PathTooLong,
        ErrorInvalidName or ErrorBadPathname => FailureReason.InvalidName,
        ErrorNotReady or ErrorInvalidDrive or ErrorDevNotExist or ErrorNoSuchDevice
            or ErrorDeviceNotConnected or ErrorDeviceRemoved => FailureReason.DestinationUnavailable,
        ErrorNetworkUnreachable or ErrorBadNetName or ErrorNetworkBusy or ErrorUnexpNetErr
            => FailureReason.NetworkError,
        ErrorCrc or ErrorSeek or ErrorWriteFault or ErrorReadFault or ErrorGenFailure
            or ErrorIoDevice or ErrorFileCorrupt or ErrorDiskCorrupt => FailureReason.DeviceError,
        ErrorFileExists or ErrorDirNotEmpty or ErrorNotSameDevice => FailureReason.Unknown,
        _ => FailureReason.Unknown
    };

    /// <summary>The Win32 code carried by the exception, or null.</summary>
    public static int? NativeCode(Exception exception)
    {
        if (exception is Win32Exception win32 && win32.NativeErrorCode != 0)
            return win32.NativeErrorCode;

        var hresult = exception.HResult;

        // 0x8007xxxx is a Win32 code wrapped in an HRESULT (FACILITY_WIN32).
        // The mask must be an int: as a uint literal it would widen the whole comparison
        // to long and a negative HRESULT would never match.
        if ((hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000))
            return hresult & 0xFFFF;

        return null;
    }

    private static string Describe(FailureReason reason, Exception exception) => reason switch
    {
        FailureReason.InUse => "The file is open in another program and cannot be read while it is locked.",
        FailureReason.AccessDenied => "Permission denied: the file could not be read, or the destination could not be written.",
        FailureReason.NotFound => "The file or its folder no longer exists.",
        FailureReason.PathTooLong => "The destination path exceeds the limit accepted by the file system.",
        FailureReason.DiskFull => "The destination volume is full.",
        FailureReason.DestinationUnavailable => "The destination drive is not available (removed, offline or not ready).",
        FailureReason.SourceUnavailable => "The source drive is not available (removed, offline or not ready).",
        FailureReason.ReadOnlyTarget => "The destination is write protected.",
        FailureReason.NetworkError => "The network location stopped responding.",
        FailureReason.InvalidName => "The name contains characters the destination file system does not accept.",
        FailureReason.VerificationMismatch => "The SHA-256 of the copy does not match the source.",
        FailureReason.DeviceError => "A device I/O error occurred (possibly a damaged sector).",
        FailureReason.SkippedByUser => "Skipped on request.",
        _ => exception.Message
    };
}
