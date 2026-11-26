using CommunityToolkit.Mvvm.ComponentModel;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;

namespace FutureSpeed.App.ViewModels;

/// <summary>
/// UI projection of a queued job. Values are pushed in from the engine's progress
/// snapshots, so the view binds to plain properties and never touches the engine.
/// </summary>
public sealed partial class JobItemViewModel : ObservableObject
{
    public JobItemViewModel(QueuedJob job, FailureListViewModel? failures = null)
    {
        Queued = job;
        Failures = failures;
        Failures?.Track(job);
        Refresh();
    }

    public QueuedJob Queued { get; }

    /// <summary>
    /// The files this job could not transfer, with their full paths and reasons.
    /// Null only in tests that do not care about failures.
    /// </summary>
    public FailureListViewModel? Failures { get; }

    public string Id => Queued.Job.Id;
    public string Name => Queued.Job.Name;
    public TransferMode Mode => Queued.Job.Mode;
    public string Destination => Queued.Job.DestinationRoot;
    public string SourcesSummary => string.Join(" · ", Queued.Job.Sources);

    [ObservableProperty] private JobState _state;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _transferredBytes;
    [ObservableProperty] private long _remainingBytes;
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _completedFiles;
    [ObservableProperty] private int _remainingFiles;
    [ObservableProperty] private int _skippedFiles;
    [ObservableProperty] private int _failedFiles;
    [ObservableProperty] private double _currentSpeed;
    [ObservableProperty] private double _averageSpeed;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private TimeSpan? _eta;
    [ObservableProperty] private string? _currentFileName;
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private DateTimeOffset? _scheduledFor;

    public bool IsActive => State is JobState.Running or JobState.Paused or JobState.Scanning or JobState.Validating;
    public bool IsFinished => Queued.IsFinished;
    public bool CanPause => State == JobState.Running && !IsPaused;
    public bool CanResume => IsPaused || State is JobState.Canceled or JobState.Failed or JobState.CompletedWithErrors;
    public bool CanCancel => !IsFinished;
    public bool HasFailures => FailedFiles > 0;

    public void Refresh()
    {
        var snapshot = Queued.Progress;

        State = Queued.State;
        IsPaused = Queued.IsPaused;
        Error = Queued.Error;
        ScheduledFor = Queued.Job.ScheduledFor;

        Percent = snapshot.PercentComplete;
        TotalBytes = snapshot.TotalBytes;
        TransferredBytes = snapshot.TransferredBytes;
        RemainingBytes = snapshot.RemainingBytes;
        TotalFiles = snapshot.TotalFiles;
        CompletedFiles = snapshot.CompletedFiles;
        RemainingFiles = snapshot.RemainingFiles;
        SkippedFiles = snapshot.SkippedFiles;
        FailedFiles = snapshot.FailedFiles;
        CurrentSpeed = snapshot.CurrentBytesPerSecond;
        AverageSpeed = snapshot.AverageBytesPerSecond;
        Elapsed = snapshot.Elapsed;
        Eta = snapshot.Eta;
        CurrentFileName = snapshot.CurrentFileName;
        CurrentFilePath = snapshot.CurrentFilePath;
        IsVerifying = snapshot.IsVerifying;

        Failures?.Update();

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasFailures));
    }
}
