using System.Globalization;
using System.Text.Json;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Services;

public sealed record BackupSetInfo(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    long TotalBytes,
    int FileCount,
    bool Verified);

public interface IBackupService
{
    /// <summary>
    /// Builds a file-level backup job: every source is copied into
    /// &lt;root&gt;\&lt;set name&gt;\&lt;yyyy-MM-dd_HHmmss&gt;, so sets never overwrite each other.
    /// </summary>
    TransferJob CreateBackupJob(string setName, IEnumerable<string> sources, string backupRoot, TransferOptions options);

    Task WriteManifestAsync(TransferJob job, Abstractions.TransferResult result, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupSetInfo>> ListBackupSetsAsync(string backupRoot, CancellationToken cancellationToken = default);
}

/// <summary>
/// File/folder level backup on top of the normal transfer engine. Each run is a new
/// timestamped set with a manifest, which keeps restores and later disk-imaging work
/// honest about what a set actually contains.
/// </summary>
public sealed class BackupService : IBackupService
{
    public const string ManifestFileName = "futurespeed-backup.json";

    public TransferJob CreateBackupJob(string setName, IEnumerable<string> sources, string backupRoot, TransferOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentNullException.ThrowIfNull(options);

        var safeName = PathUtils.SanitizeFileName(setName);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var destination = Path.Combine(PathUtils.Normalize(backupRoot), safeName, stamp);

        var job = new TransferJob
        {
            Name = $"{safeName} · {stamp}",
            DestinationRoot = destination,
            Mode = TransferMode.Copy,
            Options = options.Clone()
        };

        // A backup must never consume an existing set, so conflicts are impossible by
        // construction; keep the policy explicit anyway.
        job.Options.ConflictPolicy = ConflictPolicy.AutoRename;
        job.Sources.AddRange(sources.Select(PathUtils.Normalize).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

        return job;
    }

    public async Task WriteManifestAsync(
        TransferJob job,
        Abstractions.TransferResult result,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(PathUtils.ToExtendedPath(job.DestinationRoot));

        var manifest = new
        {
            product = "Future Speed File Transfer",
            manifestVersion = 1,
            setName = job.Name,
            createdAt = job.FinishedAt ?? DateTimeOffset.Now,
            sources = job.Sources,
            destination = job.DestinationRoot,
            verified = job.Options.VerifyWithSha256 && result.FailedFiles == 0,
            totals = new
            {
                files = result.Items.Count,
                copied = result.CopiedFiles,
                skipped = result.SkippedFiles,
                failed = result.FailedFiles,
                bytes = result.TransferredBytes
            },
            files = result.Items.Select(i => new
            {
                path = i.RelativePath,
                size = i.Size,
                outcome = i.Outcome.ToString(),
                sha256 = i.DestinationHash
            })
        };

        var path = Path.Combine(job.DestinationRoot, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        await File.WriteAllTextAsync(PathUtils.ToExtendedPath(path), json, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<BackupSetInfo>> ListBackupSetsAsync(string backupRoot, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<BackupSetInfo>>(() =>
        {
            var sets = new List<BackupSetInfo>();
            var root = PathUtils.Normalize(backupRoot);
            if (!Directory.Exists(PathUtils.ToExtendedPath(root))) return sets;

            foreach (var manifestPath in Directory.EnumerateFiles(
                         PathUtils.ToExtendedPath(root), ManifestFileName, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    var rootElement = document.RootElement;
                    var folder = Path.GetDirectoryName(PathUtils.FromExtendedPath(manifestPath))!;

                    sets.Add(new BackupSetInfo(
                        rootElement.TryGetProperty("setName", out var n) ? n.GetString() ?? folder : folder,
                        folder,
                        rootElement.TryGetProperty("createdAt", out var c) && c.TryGetDateTimeOffset(out var created)
                            ? created
                            : Directory.GetCreationTime(folder),
                        rootElement.TryGetProperty("totals", out var t) && t.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0,
                        rootElement.TryGetProperty("totals", out var t2) && t2.TryGetProperty("files", out var f) ? f.GetInt32() : 0,
                        rootElement.TryGetProperty("verified", out var v) && v.GetBoolean()));
                }
                catch
                {
                    // A damaged manifest should not hide the other sets.
                }
            }

            return sets.OrderByDescending(s => s.CreatedAt).ToList();
        }, cancellationToken);
}
