using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Walks the selected sources and produces a flat plan. Enumeration is iterative
/// (no recursion) so very deep trees cannot blow the stack, and every I/O call is
/// guarded: an unreadable folder becomes a reported problem, not a failed scan.
/// </summary>
public sealed class FileSystemScanner : IFileSystemScanner
{
    private readonly ILogger<FileSystemScanner> _logger;

    public FileSystemScanner(ILogger<FileSystemScanner>? logger = null)
        => _logger = logger ?? NullLogger<FileSystemScanner>.Instance;

    /// <summary>Skip reparse points by default so junctions/symlinks cannot create loops.</summary>
    public bool FollowReparsePoints { get; init; }

    public Task<ScanResult> ScanAsync(
        IEnumerable<string> sources,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(sources, destinationRoot, progress, cancellationToken), cancellationToken);

    private ScanResult Scan(
        IEnumerable<string> sources,
        string destinationRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new ScanResult();
        var destination = PathUtils.Normalize(destinationRoot);

        foreach (var rawSource in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = PathUtils.Normalize(rawSource);
            if (source.Length == 0) continue;

            try
            {
                if (File.Exists(source))
                {
                    AddFile(result, source, destination, Path.GetFileName(source));
                }
                else if (Directory.Exists(source))
                {
                    var folderName = Path.GetFileName(source.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(folderName))
                    {
                        // A volume root ("D:\") has no name; use the label-free letter.
                        folderName = source.Replace(":", string.Empty).Replace("\\", string.Empty);
                        if (string.IsNullOrEmpty(folderName)) folderName = "Volume";
                    }

                    ScanDirectory(result, source, destination, folderName, progress, cancellationToken);
                }
                else
                {
                    result.Problems.Add(new ScanProblem(source, "Source does not exist."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scan failed for {Source}", source);
                result.Problems.Add(new ScanProblem(source, ex.Message));
            }
        }

        result.TotalBytes = result.Files.Sum(f => f.Size);
        return result;
    }

    private void ScanDirectory(
        ScanResult result,
        string rootSource,
        string destinationRoot,
        string destinationFolderName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var destinationBase = PathUtils.CombineSecure(destinationRoot, destinationFolderName);

        // Record the root folder itself so an empty selection still creates it.
        TryAddDirectory(result, rootSource, destinationBase);

        var pending = new Stack<string>();
        pending.Push(rootSource);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            progress?.Report(current);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(PathUtils.ToExtendedPath(current), "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                result.Problems.Add(new ScanProblem(current, ex.Message));
                continue;
            }

            foreach (var rawEntry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = PathUtils.FromExtendedPath(rawEntry);

                try
                {
                    var attributes = File.GetAttributes(PathUtils.ToExtendedPath(entry));

                    if (attributes.HasFlag(FileAttributes.ReparsePoint) && !FollowReparsePoints)
                    {
                        result.Problems.Add(new ScanProblem(entry, "Skipped link / reparse point."));
                        continue;
                    }

                    var relative = PathUtils.GetRelativePath(rootSource, entry);
                    var destinationPath = PathUtils.CombineSecure(
                        destinationBase, relative);

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        TryAddDirectory(result, entry, destinationPath);
                        pending.Push(entry);
                    }
                    else
                    {
                        var info = new FileInfo(PathUtils.ToExtendedPath(entry));
                        result.Files.Add(new TransferItem
                        {
                            SourcePath = entry,
                            DestinationPath = destinationPath,
                            RelativePath = Path.Combine(destinationFolderName, relative),
                            Size = info.Exists ? info.Length : 0,
                            LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
                            Attributes = attributes
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Problems.Add(new ScanProblem(entry, ex.Message));
                }
            }
        }
    }

    private static void AddFile(ScanResult result, string sourceFile, string destinationRoot, string fileName)
    {
        var info = new FileInfo(PathUtils.ToExtendedPath(sourceFile));
        var destinationPath = PathUtils.CombineSecure(destinationRoot, fileName);

        result.Files.Add(new TransferItem
        {
            SourcePath = sourceFile,
            DestinationPath = destinationPath,
            RelativePath = fileName,
            Size = info.Exists ? info.Length : 0,
            LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
            Attributes = info.Exists ? info.Attributes : FileAttributes.Normal
        });
    }

    private static void TryAddDirectory(ScanResult result, string sourceDirectory, string destinationDirectory)
    {
        try
        {
            var info = new DirectoryInfo(PathUtils.ToExtendedPath(sourceDirectory));
            result.Directories.Add(new TransferDirectory
            {
                SourcePath = sourceDirectory,
                DestinationPath = destinationDirectory,
                LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
                Attributes = info.Exists ? info.Attributes : FileAttributes.Directory
            });
        }
        catch (Exception ex)
        {
            result.Problems.Add(new ScanProblem(sourceDirectory, ex.Message));
        }
    }
}
