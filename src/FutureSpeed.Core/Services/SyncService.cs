using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Services;

public sealed class SyncPlan
{
    /// <summary>Files that must be copied, already mapped to their destinations.</summary>
    public ScanResult Transfer { get; } = new();

    /// <summary>Files present at the destination but not at the source (mirror mode only).</summary>
    public List<string> ExtraDestinationFiles { get; } = new();

    public List<string> Problems { get; } = new();

    public long BytesToCopy => Transfer.TotalBytes;
    public int FilesToCopy => Transfer.FileCount;
    public bool HasWork => FilesToCopy > 0 || ExtraDestinationFiles.Count > 0;
}

public interface ISyncService
{
    Task<SyncPlan> BuildPlanAsync(
        string leftFolder,
        string rightFolder,
        SyncDirection direction,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the extra destination files of a mirror plan. Call only after the user confirms.</summary>
    Task<int> DeleteExtrasAsync(SyncPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// Compares two folders and produces a copy plan. Comparison is size + last-write time
/// (2-second tolerance, because FAT32 and some network shares store coarser times);
/// content hashing is left to the transfer engine's verification step.
/// </summary>
public sealed class SyncService : ISyncService
{
    private readonly ILogger<SyncService> _logger;

    public SyncService(ILogger<SyncService>? logger = null)
        => _logger = logger ?? NullLogger<SyncService>.Instance;

    public Task<SyncPlan> BuildPlanAsync(
        string leftFolder,
        string rightFolder,
        SyncDirection direction,
        CancellationToken cancellationToken = default)
        => Task.Run(() => BuildPlan(leftFolder, rightFolder, direction, cancellationToken), cancellationToken);

    private SyncPlan BuildPlan(string leftFolder, string rightFolder, SyncDirection direction, CancellationToken cancellationToken)
    {
        var plan = new SyncPlan();
        var left = PathUtils.Normalize(leftFolder);
        var right = PathUtils.Normalize(rightFolder);

        if (!Directory.Exists(PathUtils.ToExtendedPath(left)))
        {
            plan.Problems.Add($"Source folder does not exist: {left}");
            return plan;
        }

        if (PathUtils.PathsEqual(left, right) || PathUtils.IsInside(left, right) || PathUtils.IsInside(right, left))
        {
            plan.Problems.Add("The two folders overlap; choose folders that do not contain each other.");
            return plan;
        }

        Directory.CreateDirectory(PathUtils.ToExtendedPath(right));

        var leftFiles = Index(left, plan, cancellationToken);
        var rightFiles = Index(right, plan, cancellationToken);

        // Source -> destination.
        foreach (var (relative, source) in leftFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = PathUtils.CombineSecure(right, relative);
            if (rightFiles.TryGetValue(relative, out var existing) && IsSame(source, existing))
                continue;

            if (direction == SyncDirection.TwoWay && existing is not null &&
                existing.LastWriteTimeUtc > source.LastWriteTimeUtc.AddSeconds(2))
                continue; // the destination copy is newer; handled by the reverse pass

            plan.Transfer.Files.Add(ToItem(source, destinationPath, relative));
        }

        // Destination -> source (two-way only).
        if (direction == SyncDirection.TwoWay)
        {
            foreach (var (relative, source) in rightFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (leftFiles.TryGetValue(relative, out var existing))
                {
                    if (IsSame(source, existing)) continue;
                    if (source.LastWriteTimeUtc <= existing.LastWriteTimeUtc.AddSeconds(2)) continue;
                }

                var destinationPath = PathUtils.CombineSecure(left, relative);
                plan.Transfer.Files.Add(ToItem(source, destinationPath, relative));
            }
        }

        // Extra files at the destination (mirror only).
        if (direction == SyncDirection.Mirror)
        {
            foreach (var (relative, file) in rightFiles)
            {
                if (!leftFiles.ContainsKey(relative))
                    plan.ExtraDestinationFiles.Add(PathUtils.FromExtendedPath(file.FullName));
            }
        }

        // Folder structure needed on the destination side.
        foreach (var directory in EnumerateDirectories(left, plan, cancellationToken))
        {
            var directoryPath = PathUtils.FromExtendedPath(directory.FullName);
            var relative = PathUtils.GetRelativePath(left, directoryPath);
            if (relative.Length == 0) continue;

            plan.Transfer.Directories.Add(new TransferDirectory
            {
                SourcePath = directoryPath,
                DestinationPath = PathUtils.CombineSecure(right, relative),
                LastWriteTimeUtc = directory.LastWriteTimeUtc,
                Attributes = directory.Attributes
            });
        }

        plan.Transfer.TotalBytes = plan.Transfer.Files.Sum(f => f.Size);
        return plan;
    }

    public Task<int> DeleteExtrasAsync(SyncPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return Task.Run(() =>
        {
            var deleted = 0;
            foreach (var path in plan.ExtraDestinationFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var extended = PathUtils.ToExtendedPath(path);
                    if (!File.Exists(extended)) continue;

                    var attributes = File.GetAttributes(extended);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);

                    File.Delete(extended);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete extra file {Path}", path);
                }
            }

            return deleted;
        }, cancellationToken);
    }

    private static TransferItem ToItem(FileInfo source, string destinationPath, string relative) => new()
    {
        SourcePath = PathUtils.FromExtendedPath(source.FullName),
        DestinationPath = destinationPath,
        RelativePath = relative,
        Size = source.Length,
        LastWriteTimeUtc = source.LastWriteTimeUtc,
        Attributes = source.Attributes
    };

    private static bool IsSame(FileInfo a, FileInfo? b)
        => b is not null &&
           a.Length == b.Length &&
           Math.Abs((a.LastWriteTimeUtc - b.LastWriteTimeUtc).TotalSeconds) <= 2;

    private Dictionary<string, FileInfo> Index(string root, SyncPlan plan, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            try
            {
                foreach (var entry in new DirectoryInfo(PathUtils.ToExtendedPath(current)).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                    if (entry is DirectoryInfo dir)
                    {
                        pending.Push(PathUtils.FromExtendedPath(dir.FullName));
                    }
                    else if (entry is FileInfo file && !PathUtils.IsPartialPath(file.Name))
                    {
                        var relative = PathUtils.GetRelativePath(root, PathUtils.FromExtendedPath(file.FullName));
                        map[relative] = file;
                    }
                }
            }
            catch (Exception ex)
            {
                plan.Problems.Add($"{current}: {ex.Message}");
            }
        }

        return map;
    }

    private IEnumerable<DirectoryInfo> EnumerateDirectories(string root, SyncPlan plan, CancellationToken cancellationToken)
    {
        var results = new List<DirectoryInfo>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            try
            {
                foreach (var dir in new DirectoryInfo(PathUtils.ToExtendedPath(current)).EnumerateDirectories())
                {
                    if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    results.Add(dir);
                    pending.Push(PathUtils.FromExtendedPath(dir.FullName));
                }
            }
            catch (Exception ex)
            {
                plan.Problems.Add($"{current}: {ex.Message}");
            }
        }

        return results;
    }
}
