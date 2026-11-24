using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Utils;

/// <summary>
/// Picks a buffer size and a worker count from the device types involved.
/// The goal is bounded memory: buffers never scale with file size beyond the cap,
/// so a 200 GB file uses the same few MB as a 200 MB one.
/// </summary>
public static class TransferTuner
{
    public const int MinBuffer = 4 * 1024;             // 4 KB
    public const int MaxBuffer = 16 * 1024 * 1024;     // 16 MB
    public const int MaxWorkers = 32;

    /// <summary>Buffer size for a single file, given the slower of the two devices.</summary>
    public static int GetBufferSize(long fileSize, DriveKindHint source, DriveKindHint destination)
    {
        var kind = SlowerOf(source, destination);

        var baseBuffer = kind switch
        {
            DriveKindHint.Nvme => 4 * 1024 * 1024,
            DriveKindHint.Ssd => 2 * 1024 * 1024,
            DriveKindHint.RamDisk => 4 * 1024 * 1024,
            DriveKindHint.Hdd => 1024 * 1024,
            DriveKindHint.Usb => 512 * 1024,
            DriveKindHint.Network => 256 * 1024,
            DriveKindHint.Optical => 128 * 1024,
            _ => 1024 * 1024
        };

        // Tiny files do not benefit from a multi-megabyte buffer and it only wastes memory.
        if (fileSize > 0 && fileSize < baseBuffer)
        {
            var rounded = 1 << (int)Math.Ceiling(Math.Log2(Math.Max(MinBuffer, fileSize)));
            baseBuffer = Math.Min(baseBuffer, rounded);
        }

        return Math.Clamp(baseBuffer, MinBuffer, MaxBuffer);
    }

    /// <summary>
    /// Worker count. Spinning disks lose throughput to seeking when several files are
    /// written at once, so they stay at one; flash and network links benefit from queue depth.
    /// </summary>
    public static int GetWorkerCount(DriveKindHint source, DriveKindHint destination, int? cpuCount = null)
    {
        var cores = Math.Max(1, cpuCount ?? Environment.ProcessorCount);
        var kind = SlowerOf(source, destination);

        var workers = kind switch
        {
            DriveKindHint.Nvme => Math.Min(8, Math.Max(4, cores)),
            DriveKindHint.Ssd => Math.Min(6, Math.Max(3, cores / 2)),
            DriveKindHint.RamDisk => Math.Min(8, Math.Max(4, cores)),
            DriveKindHint.Hdd => 1,
            DriveKindHint.Usb => 2,
            DriveKindHint.Network => Math.Min(8, Math.Max(4, cores / 2)),
            DriveKindHint.Optical => 1,
            _ => 2
        };

        return Math.Clamp(workers, 1, MaxWorkers);
    }

    /// <summary>Ranks device classes by how badly they handle concurrent random access.</summary>
    public static DriveKindHint SlowerOf(DriveKindHint a, DriveKindHint b)
        => Rank(a) <= Rank(b) ? a : b;

    private static int Rank(DriveKindHint kind) => kind switch
    {
        DriveKindHint.Optical => 0,
        DriveKindHint.Network => 1,
        DriveKindHint.Usb => 2,
        DriveKindHint.Hdd => 3,
        DriveKindHint.Unknown => 4,
        DriveKindHint.Ssd => 5,
        DriveKindHint.Nvme => 6,
        DriveKindHint.RamDisk => 7,
        _ => 4
    };

    /// <summary>Resolves the effective buffer/worker values for a job, honouring manual overrides.</summary>
    public static (int BufferSize, int Workers) Resolve(
        TransferOptions options,
        long representativeFileSize,
        DriveKindHint source,
        DriveKindHint destination)
    {
        if (options.AutoTune)
        {
            return (
                GetBufferSize(representativeFileSize, source, destination),
                GetWorkerCount(source, destination));
        }

        return (
            Math.Clamp(options.BufferSizeBytes, MinBuffer, MaxBuffer),
            Math.Clamp(options.MaxParallelFiles, 1, MaxWorkers));
    }
}
