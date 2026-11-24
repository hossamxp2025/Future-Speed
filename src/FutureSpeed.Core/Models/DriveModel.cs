namespace FutureSpeed.Core.Models;

/// <summary>A mounted volume as shown on the dashboard cards.</summary>
public sealed class DriveModel
{
    public required string RootPath { get; init; }
    public string Letter => RootPath.Length > 0 ? RootPath[..1].ToUpperInvariant() : "?";
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes > 0 ? Math.Clamp(UsedBytes * 100d / TotalBytes, 0, 100) : 0;
    public DriveKindHint Kind { get; init; }
    public bool IsReady { get; init; }
    public bool IsRemovable => Kind is DriveKindHint.Usb;
    public bool IsNetwork => Kind is DriveKindHint.Network;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Letter + ":" : $"{Label} ({Letter}:)";
}
