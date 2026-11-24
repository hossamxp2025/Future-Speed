using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

/// <summary>Expands the selected files/folders into a flat, validated copy plan.</summary>
public interface IFileSystemScanner
{
    /// <param name="sources">Absolute paths to files and/or directories.</param>
    /// <param name="destinationRoot">Destination folder the plan maps into.</param>
    Task<ScanResult> ScanAsync(
        IEnumerable<string> sources,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
