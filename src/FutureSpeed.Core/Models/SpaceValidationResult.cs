namespace FutureSpeed.Core.Models;

/// <summary>Result of the pre-flight free-space check on the destination volume.</summary>
public sealed class SpaceValidationResult
{
    public required SpaceValidationStatus Status { get; init; }

    /// <summary>Bytes that will actually be written, after subtracting files that will be skipped.</summary>
    public long RequiredBytes { get; init; }

    /// <summary>Bytes reclaimed by overwriting existing destination files.</summary>
    public long ReclaimableBytes { get; init; }

    public long AvailableBytes { get; init; }

    public long MissingBytes => Math.Max(0, RequiredBytes - AvailableBytes);

    public string DestinationRoot { get; init; } = string.Empty;
    public string? VolumeLabel { get; init; }
    public string? FileSystem { get; init; }

    public string? Message { get; init; }

    public bool CanProceed => Status is SpaceValidationStatus.Sufficient or SpaceValidationStatus.Undeterminable;

    /// <summary>True when the user must explicitly accept the risk (size could not be determined).</summary>
    public bool RequiresOverride => Status == SpaceValidationStatus.Undeterminable;
}
