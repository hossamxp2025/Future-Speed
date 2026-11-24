using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;

namespace FutureSpeed.App.ViewModels;

/// <summary>One row in the compact window's file list.</summary>
public sealed partial class TransferFileRowViewModel : ObservableObject
{
    public TransferFileRowViewModel(TransferItem item)
    {
        Item = item;
        _outcome = item.Outcome;
    }

    public TransferItem Item { get; }

    public string Name => System.IO.Path.GetFileName(Item.SourcePath);
    public string Source => Item.SourcePath;
    public string Destination => Item.EffectiveDestinationPath ?? Item.DestinationPath;
    public long Size => Item.Size;

    [ObservableProperty]
    private FileOutcome _outcome;

    public bool HasFailed => Outcome is FileOutcome.Failed or FileOutcome.VerificationFailed;
    public string? Error => Item.Error;

    public void Refresh()
    {
        Outcome = Item.Outcome;
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(Destination));
    }
}

/// <summary>
/// The view model behind the small transfer window that Explorer opens. It watches one
/// job in the queue; the main window never has to exist for it to work.
/// </summary>
public sealed partial class CompactTransferViewModel : ObservableObject
{
    private readonly ITransferQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly IDriveService _drives;
    private readonly ILocalizationService _localization;

    private QueuedJob? _job;

    public CompactTransferViewModel(
        ITransferQueue queue,
        IDialogService dialogs,
        IDriveService drives,
        ILocalizationService localization)
    {
        _queue = queue;
        _dialogs = dialogs;
        _drives = drives;
        _localization = localization;

        _queue.JobProgress += OnJobEvent;
        _queue.JobStarted += OnJobEvent;
        _queue.JobFinished += OnJobEvent;
    }

    public ObservableCollection<TransferFileRowViewModel> Files { get; } = new();

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private TransferMode _mode = TransferMode.Copy;
    [ObservableProperty] private JobState _state = JobState.Created;
    [ObservableProperty] private bool _isPaused;

    [ObservableProperty] private string? _currentFileName;
    [ObservableProperty] private string? _currentSource;
    [ObservableProperty] private string? _currentDestination;

    [ObservableProperty] private double _filePercent;
    [ObservableProperty] private double _totalPercent;

    [ObservableProperty] private double _currentSpeed;
    [ObservableProperty] private double _averageSpeed;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private TimeSpan? _eta;

    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _transferredBytes;
    [ObservableProperty] private long _remainingBytes;

    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _completedFiles;
    [ObservableProperty] private int _remainingFiles;
    [ObservableProperty] private int _failedFiles;
    [ObservableProperty] private int _skippedFiles;

    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private bool _verificationEnabled;

    [ObservableProperty] private long _destinationFreeBytes;
    [ObservableProperty] private string? _destinationRoot;
    [ObservableProperty] private string? _statusMessage;

    public bool IsFinished => _job?.IsFinished ?? false;
    public bool CanPause => State == JobState.Running && !IsPaused;
    public bool CanResume => IsPaused;
    public bool CanSkip => State is JobState.Running or JobState.Paused;
    public bool CanCancel => _job is not null && !_job.IsFinished;
    public bool HasFailures => FailedFiles > 0;

    /// <summary>Raised when the user asks for the full application window.</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>Raised when the user asks to hide the window into the notification area.</summary>
    public event EventHandler? MinimizeToTrayRequested;

    /// <summary>Raised when the job ends, so the window can show its final state.</summary>
    public event EventHandler? Finished;

    public void Attach(QueuedJob job)
    {
        _job = job;
        Title = job.Job.Name;
        Mode = job.Job.Mode;
        VerificationEnabled = job.Job.Options.VerifyWithSha256;
        DestinationRoot = job.Job.DestinationRoot;
        DestinationFreeBytes = _drives.GetAvailableFreeSpace(job.Job.DestinationRoot) ?? 0;

        RefreshFromJob();
    }

    private void OnJobEvent(object? sender, JobEventArgs e)
    {
        if (_job is null || e.Job.Job.Id != _job.Job.Id) return;

        var application = Application.Current;
        if (application is null) return;

        if (application.Dispatcher.CheckAccess()) RefreshFromJob();
        else application.Dispatcher.BeginInvoke(RefreshFromJob);
    }

    private void RefreshFromJob()
    {
        if (_job is null) return;

        var snapshot = _job.Progress;
        var wasFinished = IsFinished;

        State = _job.State;
        IsPaused = _job.IsPaused;

        CurrentFileName = snapshot.CurrentFileName;
        CurrentSource = snapshot.CurrentFilePath;
        CurrentDestination = snapshot.CurrentFileDestination;

        FilePercent = snapshot.CurrentFilePercent;
        TotalPercent = snapshot.PercentComplete;

        CurrentSpeed = snapshot.CurrentBytesPerSecond;
        AverageSpeed = snapshot.AverageBytesPerSecond;
        Elapsed = snapshot.Elapsed;
        Eta = snapshot.Eta;

        TotalBytes = snapshot.TotalBytes;
        TransferredBytes = snapshot.TransferredBytes;
        RemainingBytes = snapshot.RemainingBytes;

        TotalFiles = snapshot.TotalFiles;
        CompletedFiles = snapshot.CompletedFiles;
        RemainingFiles = snapshot.RemainingFiles;
        FailedFiles = snapshot.FailedFiles;
        SkippedFiles = snapshot.SkippedFiles;

        IsVerifying = snapshot.IsVerifying;

        SyncFiles();

        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasFailures));

        if (_job.Error is { Length: > 0 }) StatusMessage = _job.Error;

        if (!wasFinished && IsFinished)
        {
            DestinationFreeBytes = _drives.GetAvailableFreeSpace(_job.Job.DestinationRoot) ?? DestinationFreeBytes;
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SyncFiles()
    {
        var items = _job?.Job.Scan?.Files;
        if (items is null) return;

        if (Files.Count != items.Count)
        {
            Files.Clear();
            foreach (var item in items)
                Files.Add(new TransferFileRowViewModel(item));
            return;
        }

        foreach (var row in Files) row.Refresh();
    }

    [RelayCommand]
    private void Pause()
    {
        if (_job is null) return;
        _queue.PauseJob(_job.Job.Id);
        RefreshFromJob();
    }

    [RelayCommand]
    private void Resume()
    {
        if (_job is null) return;
        _queue.ResumeJob(_job.Job.Id);
        RefreshFromJob();
    }

    [RelayCommand]
    private void SkipCurrentFile()
    {
        if (_job is null) return;
        _queue.SkipCurrentFile(_job.Job.Id);
        StatusMessage = _localization["Compact_SkipRequested"];
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_job is null) return;

        // Cancelling never deletes a source: the confirmation says so explicitly.
        if (!_dialogs.Confirm(_localization["Compact_ConfirmCancel"], _localization["Common_Cancel"])) return;

        _queue.CancelJob(_job.Job.Id);
        RefreshFromJob();
    }

    [RelayCommand]
    private void RetryFailed()
    {
        if (_job is null) return;

        var retry = _queue.RetryFailed(_job.Job.Id);
        if (retry is null)
        {
            _dialogs.Info(_localization["History_NoErrors"]);
            return;
        }

        // Follow the retry job in this same window.
        Attach(retry);
    }

    [RelayCommand]
    private void OpenDestination()
    {
        if (_job is null) return;
        _dialogs.OpenInExplorer(_job.Job.DestinationRoot);
    }

    [RelayCommand]
    private void OpenMainWindow() => MainWindowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void MinimizeToTray() => MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty);
}
