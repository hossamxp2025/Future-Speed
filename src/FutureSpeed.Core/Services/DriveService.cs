using System.Collections.Concurrent;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Enumerates volumes and classifies them (HDD / SSD / NVMe / USB / network) so the
/// tuner can pick sensible buffers. Media type detection is cached because the WMI
/// query behind it is slow.
/// </summary>
public sealed class DriveService : IDriveService
{
    private readonly ILogger<DriveService> _logger;
    private readonly ConcurrentDictionary<string, DriveKindHint> _kindCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IReadOnlyDictionary<string, DriveKindHint>> _physicalKinds;

    public DriveService(ILogger<DriveService>? logger = null)
    {
        _logger = logger ?? NullLogger<DriveService>.Instance;
        _physicalKinds = new Lazy<IReadOnlyDictionary<string, DriveKindHint>>(
            QueryPhysicalDiskKinds, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<DriveModel> GetDrives()
    {
        var result = new List<DriveModel>();

        foreach (var drive in SafeGetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    result.Add(new DriveModel
                    {
                        RootPath = drive.Name,
                        Label = null,
                        FileSystem = null,
                        TotalBytes = 0,
                        FreeBytes = 0,
                        Kind = MapDriveType(drive.DriveType),
                        IsReady = false
                    });
                    continue;
                }

                result.Add(new DriveModel
                {
                    RootPath = drive.Name,
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    FileSystem = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    Kind = ResolveKind(drive),
                    IsReady = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read drive {Drive}", drive.Name);
            }
        }

        return result;
    }

    public long? GetAvailableFreeSpace(string path)
    {
        var root = GetVolumeRoot(path);
        if (root is null) return null;

        try
        {
            var info = new DriveInfo(root);
            return info.IsReady ? info.AvailableFreeSpace : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Free space unavailable for {Path}", path);
            return null;
        }
    }

    public string? GetVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = PathUtils.Normalize(path);
            if (full.Length == 0) return null;

            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch
        {
            return null;
        }
    }

    public DriveModel? GetDriveFor(string path)
    {
        var root = GetVolumeRoot(path);
        if (root is null) return null;

        return GetDrives().FirstOrDefault(d =>
            string.Equals(d.RootPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    public bool AreOnSameVolume(string pathA, string pathB)
    {
        var a = GetVolumeRoot(pathA);
        var b = GetVolumeRoot(pathB);
        return a is not null && b is not null &&
               string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best-effort classification of the device behind a path.</summary>
    public DriveKindHint GetKindFor(string path)
    {
        var root = GetVolumeRoot(path);
        if (root is null) return DriveKindHint.Unknown;

        if (_kindCache.TryGetValue(root, out var cached) && cached != DriveKindHint.Unknown)
            return cached;

        return ResolveKindForRoot(root);
    }

    private DriveKindHint ResolveKindForRoot(string root)
    {
        try
        {
            var info = new DriveInfo(root);
            var kind = ResolveKind(info);
            _kindCache[root] = kind;
            return kind;
        }
        catch
        {
            return DriveKindHint.Unknown;
        }
    }

    private DriveKindHint ResolveKind(DriveInfo drive)
    {
        var mapped = MapDriveType(drive.DriveType);
        if (mapped != DriveKindHint.Unknown && drive.DriveType != DriveType.Fixed)
            return mapped;

        if (_kindCache.TryGetValue(drive.Name, out var cached) && cached != DriveKindHint.Unknown)
            return cached;

        var letter = drive.Name.Length > 0 ? drive.Name[..1].ToUpperInvariant() : null;
        var kind = DriveKindHint.Hdd;

        if (letter is not null && _physicalKinds.Value.TryGetValue(letter, out var physical))
            kind = physical;

        _kindCache[drive.Name] = kind;
        return kind;
    }

    private static DriveKindHint MapDriveType(DriveType type) => type switch
    {
        DriveType.Network => DriveKindHint.Network,
        DriveType.Removable => DriveKindHint.Usb,
        DriveType.CDRom => DriveKindHint.Optical,
        DriveType.Ram => DriveKindHint.RamDisk,
        DriveType.Fixed => DriveKindHint.Unknown,
        _ => DriveKindHint.Unknown
    };

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch
        {
            return Array.Empty<DriveInfo>();
        }
    }

    /// <summary>
    /// Maps drive letters to media types through the Storage WMI namespace.
    /// Any failure degrades to an empty map (everything is then treated as HDD,
    /// which is the conservative choice for buffers and parallelism).
    /// </summary>
    private IReadOnlyDictionary<string, DriveKindHint> QueryPhysicalDiskKinds()
    {
        var map = new Dictionary<string, DriveKindHint>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return map;

        try
        {
            // Partition -> disk number, then disk number -> media type / bus type.
            var diskKinds = new Dictionary<uint, DriveKindHint>();

            using (var searcher = new System.Management.ManagementObjectSearcher(
                       @"\\.\root\microsoft\windows\storage",
                       "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
            {
                foreach (var o in searcher.Get())
                {
                    using var disk = o;
                    if (!uint.TryParse(Convert.ToString(disk["DeviceId"]), out var deviceId))
                        continue;

                    var mediaType = ToUInt16(disk["MediaType"]);
                    var busType = ToUInt16(disk["BusType"]);

                    // MediaType: 3 = HDD, 4 = SSD, 5 = SCM. BusType: 17 = NVMe, 7 = USB.
                    var kind = busType == 17
                        ? DriveKindHint.Nvme
                        : busType == 7
                            ? DriveKindHint.Usb
                            : mediaType switch
                            {
                                4 => DriveKindHint.Ssd,
                                5 => DriveKindHint.Ssd,
                                3 => DriveKindHint.Hdd,
                                _ => DriveKindHint.Unknown
                            };

                    diskKinds[deviceId] = kind;
                }
            }

            using (var searcher = new System.Management.ManagementObjectSearcher(
                       @"\\.\root\microsoft\windows\storage",
                       "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            {
                foreach (var o in searcher.Get())
                {
                    using var partition = o;
                    var letter = Convert.ToString(partition["DriveLetter"]);
                    if (string.IsNullOrWhiteSpace(letter) || letter[0] == '\0')
                        continue;

                    var diskNumber = ToUInt32(partition["DiskNumber"]);
                    if (diskKinds.TryGetValue(diskNumber, out var kind) && kind != DriveKindHint.Unknown)
                        map[letter[..1].ToUpperInvariant()] = kind;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Storage WMI query failed; falling back to conservative device profiles.");
        }

        return map;
    }

    private static ushort ToUInt16(object? value)
    {
        try { return value is null ? (ushort)0 : Convert.ToUInt16(value); }
        catch { return 0; }
    }

    private static uint ToUInt32(object? value)
    {
        try { return value is null ? 0u : Convert.ToUInt32(value); }
        catch { return 0u; }
    }
}
