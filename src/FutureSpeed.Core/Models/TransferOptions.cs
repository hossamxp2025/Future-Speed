namespace FutureSpeed.Core.Models;

/// <summary>
/// Tunables for a single transfer job. Defaults are the "automatic" profile:
/// buffer and parallelism are chosen per device at run time.
/// </summary>
public sealed class TransferOptions
{
    /// <summary>When true, <see cref="BufferSizeBytes"/> and <see cref="MaxParallelFiles"/> are auto-tuned.</summary>
    public bool AutoTune { get; set; } = true;

    /// <summary>Explicit buffer size, used when <see cref="AutoTune"/> is false. Clamped to 4 KB .. 16 MB.</summary>
    public int BufferSizeBytes { get; set; } = 1024 * 1024;

    /// <summary>Explicit worker count, used when <see cref="AutoTune"/> is false. Clamped to 1 .. 32.</summary>
    public int MaxParallelFiles { get; set; } = 4;

    /// <summary>Compute and compare SHA-256 of source and destination after each file.</summary>
    public bool VerifyWithSha256 { get; set; }

    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Ask;

    /// <summary>Global throughput cap in bytes per second. 0 = unlimited.</summary>
    public long MaxBytesPerSecond { get; set; }

    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveAttributes { get; set; } = true;

    /// <summary>Skip files that cannot be read instead of failing the job.</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Number of automatic retries per file before it is reported as failed.</summary>
    public int RetryCount { get; set; } = 1;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Use a fast same-volume rename for Move when possible. Disabled automatically
    /// when verification is requested, because a rename cannot be verified byte-wise.
    /// </summary>
    public bool AllowSameVolumeFastMove { get; set; } = true;

    /// <summary>Write each file to a temporary "*.fspart" file and rename on success (crash safe).</summary>
    public bool UseTemporaryPartFiles { get; set; } = true;

    /// <summary>Persist job state so an interrupted job can be resumed after a restart.</summary>
    public bool EnableResume { get; set; } = true;

    public TransferOptions Clone() => (TransferOptions)MemberwiseClone();
}
