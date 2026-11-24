namespace FutureSpeed.Core.Abstractions;

public sealed record DiskImagingCapabilities(
    bool CanCreateImage,
    bool CanRestoreImage,
    bool CanCloneDisk,
    string Reason);

/// <summary>
/// Seam for a future disk-imaging / cloning module. The first release deliberately
/// ships no implementation that writes to raw volumes: that work needs a kernel-level
/// snapshot (VSS), elevation, and its own isolated test rig. Everything the UI needs
/// is expressed here so the feature can be added without touching the file engine.
/// </summary>
public interface IDiskImagingProvider
{
    DiskImagingCapabilities GetCapabilities();

    Task CreateImageAsync(string volumeRoot, string imagePath, IProgress<double>? progress, CancellationToken cancellationToken);
    Task RestoreImageAsync(string imagePath, string volumeRoot, IProgress<double>? progress, CancellationToken cancellationToken);
    Task CloneDiskAsync(string sourceVolumeRoot, string targetVolumeRoot, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// The shipped provider: reports that imaging is unavailable and refuses every raw
/// write. This is intentional — a half-tested raw-disk writer can destroy a system
/// volume, so the UI shows the feature as planned instead of pretending it works.
/// </summary>
public sealed class UnavailableDiskImagingProvider : IDiskImagingProvider
{
    public const string UnavailableReason =
        "Disk imaging and cloning are not enabled in this release. " +
        "They require a VSS snapshot, elevated privileges and an isolated test rig before raw writes can be allowed.";

    public DiskImagingCapabilities GetCapabilities()
        => new(CanCreateImage: false, CanRestoreImage: false, CanCloneDisk: false, UnavailableReason);

    public Task CreateImageAsync(string volumeRoot, string imagePath, IProgress<double>? progress, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task RestoreImageAsync(string imagePath, string volumeRoot, IProgress<double>? progress, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task CloneDiskAsync(string sourceVolumeRoot, string targetVolumeRoot, IProgress<double>? progress, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);
}
