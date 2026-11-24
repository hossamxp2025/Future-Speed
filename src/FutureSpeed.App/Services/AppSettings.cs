using FutureSpeed.Core.Models;

namespace FutureSpeed.App.Services;

public enum AppTheme
{
    Light = 0,
    Dark = 1,
    System = 2
}

/// <summary>Everything the settings window can change, persisted as JSON.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "ar";
    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>UI zoom, 1.0 = 100%. Range 0.8 .. 1.6.</summary>
    public double UiScale { get; set; } = 1.0;

    public bool AutoTune { get; set; } = true;
    public int BufferSizeKb { get; set; } = 1024;
    public int MaxParallelFiles { get; set; } = 4;
    public int MaxConcurrentJobs { get; set; } = 1;

    public bool VerifyWithSha256 { get; set; }
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Ask;

    /// <summary>0 = unlimited.</summary>
    public int MaxSpeedMbPerSecond { get; set; }

    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveAttributes { get; set; } = true;
    public bool EnableResume { get; set; } = true;

    /// <summary>Explorer verbs open the small transfer window instead of the main one.</summary>
    public bool PreferCompactWindow { get; set; } = true;

    /// <summary>Remembers whether the user turned the Explorer menu commands on.</summary>
    public bool ExplorerIntegrationEnabled { get; set; }

    public bool PlaySoundWhenFinished { get; set; } = true;
    public bool ShowWindowsNotification { get; set; } = true;
    public bool OpenDestinationWhenFinished { get; set; }

    public ReportFormat DefaultReportFormat { get; set; } = ReportFormat.Json;
    public bool SaveReportAutomatically { get; set; } = true;

    public string? LogsDirectory { get; set; }
    public string? ReportsDirectory { get; set; }
    public string? BackupRoot { get; set; }

    public List<string> RecentDestinations { get; set; } = new();

    public TransferOptions ToTransferOptions() => new()
    {
        AutoTune = AutoTune,
        BufferSizeBytes = Math.Clamp(BufferSizeKb, 4, 16 * 1024) * 1024,
        MaxParallelFiles = Math.Clamp(MaxParallelFiles, 1, 32),
        VerifyWithSha256 = VerifyWithSha256,
        ConflictPolicy = ConflictPolicy,
        MaxBytesPerSecond = MaxSpeedMbPerSecond <= 0 ? 0 : (long)MaxSpeedMbPerSecond * 1024 * 1024,
        PreserveTimestamps = PreserveTimestamps,
        PreserveAttributes = PreserveAttributes,
        EnableResume = EnableResume
    };
}
