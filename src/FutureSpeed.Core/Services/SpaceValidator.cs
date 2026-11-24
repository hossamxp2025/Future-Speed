using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Decides whether the destination volume can hold the planned transfer.
/// Existing destination files are taken into account according to the conflict policy:
/// a file that will be skipped costs nothing, one that will be overwritten only costs
/// the difference between the new and the old size.
/// </summary>
public sealed class SpaceValidator : ISpaceValidator
{
    private readonly IDriveService _drives;

    /// <summary>Safety margin kept free on the destination (file system metadata, cluster slack).</summary>
    public long SafetyMarginBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Injected existence/size probes keep this class unit-testable without a disk.</summary>
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, long> _fileSize;
    private readonly Func<string, DateTime> _fileLastWriteUtc;

    public SpaceValidator(
        IDriveService drives,
        Func<string, bool>? fileExists = null,
        Func<string, long>? fileSize = null,
        Func<string, DateTime>? fileLastWriteUtc = null)
    {
        _drives = drives;
        _fileExists = fileExists ?? (p => File.Exists(PathUtils.ToExtendedPath(p)));
        _fileSize = fileSize ?? (p =>
        {
            try { return new FileInfo(PathUtils.ToExtendedPath(p)).Length; }
            catch { return 0; }
        });
        _fileLastWriteUtc = fileLastWriteUtc ?? (p =>
        {
            try { return new FileInfo(PathUtils.ToExtendedPath(p)).LastWriteTimeUtc; }
            catch { return DateTime.MinValue; }
        });
    }

    public SpaceValidationResult Validate(ScanResult scan, string destinationRoot, TransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return new SpaceValidationResult
            {
                Status = SpaceValidationStatus.InvalidDestination,
                DestinationRoot = destinationRoot ?? string.Empty,
                Message = "Destination path is empty."
            };
        }

        var drive = _drives.GetDriveFor(destinationRoot);
        var available = _drives.GetAvailableFreeSpace(destinationRoot);

        long required = 0;
        long reclaimable = 0;

        foreach (var file in scan.Files)
        {
            var destination = file.DestinationPath;
            if (!_fileExists(destination))
            {
                required += file.Size;
                continue;
            }

            var existingSize = _fileSize(destination);

            switch (options.ConflictPolicy)
            {
                case ConflictPolicy.Skip:
                    // Nothing written.
                    break;

                case ConflictPolicy.AutoRename:
                    required += file.Size;
                    break;

                case ConflictPolicy.KeepNewest:
                    if (file.LastWriteTimeUtc > _fileLastWriteUtc(destination))
                    {
                        required += file.Size;
                        reclaimable += existingSize;
                    }
                    break;

                case ConflictPolicy.OverwriteIfDifferent:
                    if (file.Size != existingSize || file.LastWriteTimeUtc != _fileLastWriteUtc(destination))
                    {
                        required += file.Size;
                        reclaimable += existingSize;
                    }
                    break;

                case ConflictPolicy.Overwrite:
                case ConflictPolicy.Ask:
                default:
                    // "Ask" is treated as the worst case (overwrite) so the check can never
                    // let a transfer start that the user's own answer would make impossible.
                    required += file.Size;
                    reclaimable += existingSize;
                    break;
            }
        }

        // Overwriting frees the old content, but only while the copy is in flight the
        // temporary part file and the original can coexist, so reclaimed space is NOT
        // counted as available. It is reported for transparency only.
        var effectiveRequired = required + SafetyMarginBytes;

        if (available is null)
        {
            return new SpaceValidationResult
            {
                Status = SpaceValidationStatus.Undeterminable,
                RequiredBytes = effectiveRequired,
                ReclaimableBytes = reclaimable,
                AvailableBytes = 0,
                DestinationRoot = destinationRoot,
                VolumeLabel = drive?.Label,
                FileSystem = drive?.FileSystem,
                Message = "Free space on the destination could not be determined."
            };
        }

        if (!scan.IsTotalExact && required == 0 && scan.Files.Count == 0)
        {
            return new SpaceValidationResult
            {
                Status = SpaceValidationStatus.Undeterminable,
                RequiredBytes = effectiveRequired,
                ReclaimableBytes = reclaimable,
                AvailableBytes = available.Value,
                DestinationRoot = destinationRoot,
                VolumeLabel = drive?.Label,
                FileSystem = drive?.FileSystem,
                Message = "The source could not be enumerated, so the required size is unknown."
            };
        }

        var status = effectiveRequired <= available.Value
            ? SpaceValidationStatus.Sufficient
            : SpaceValidationStatus.Insufficient;

        return new SpaceValidationResult
        {
            Status = status,
            RequiredBytes = effectiveRequired,
            ReclaimableBytes = reclaimable,
            AvailableBytes = available.Value,
            DestinationRoot = destinationRoot,
            VolumeLabel = drive?.Label,
            FileSystem = drive?.FileSystem
        };
    }
}
