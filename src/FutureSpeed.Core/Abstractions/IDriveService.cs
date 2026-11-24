using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

public interface IDriveService
{
    IReadOnlyList<DriveModel> GetDrives();

    /// <summary>Free space on the volume that hosts <paramref name="path"/>, or null when unknown.</summary>
    long? GetAvailableFreeSpace(string path);

    /// <summary>The volume root for a path (e.g. "D:\\" or "\\\\server\\share\\"), or null when unknown.</summary>
    string? GetVolumeRoot(string path);

    DriveModel? GetDriveFor(string path);

    /// <summary>True when both paths live on the same volume, so a rename-move is possible.</summary>
    bool AreOnSameVolume(string pathA, string pathB);

    /// <summary>Best-effort device class behind a path, used to tune buffers and parallelism.</summary>
    DriveKindHint GetKindFor(string path);
}
