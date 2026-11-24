using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.ViewModels;

public sealed partial class HistoryEntryViewModel : ObservableObject
{
    public HistoryEntryViewModel(TransferResult result)
    {
        Result = result;
        Finished = result.Job.FinishedAt ?? DateTimeOffset.Now;
    }

    public TransferResult Result { get; }
    public DateTimeOffset Finished { get; }

    public string Name => Result.Job.Name;
    public JobState State => Result.FinalState;
    public TransferMode Mode => Result.Job.Mode;
    public string Destination => Result.Job.DestinationRoot;
    public long TransferredBytes => Result.TransferredBytes;
    public int CopiedFiles => Result.CopiedFiles;
    public int SkippedFiles => Result.SkippedFiles;
    public int FailedFiles => Result.FailedFiles;
    public TimeSpan Duration => Result.Duration;

    public double AverageSpeed => Duration.TotalSeconds > 0 ? TransferredBytes / Duration.TotalSeconds : 0;

    public bool HasFailures => FailedFiles > 0;

    public IReadOnlyList<TransferItem> Problems => Result.Items
        .Where(i => i.Outcome is FileOutcome.Failed or FileOutcome.VerificationFailed or FileOutcome.Skipped)
        .ToList();

    [ObservableProperty]
    private string? _reportPath;
}

/// <summary>Finished transfers, their reports, and any job that can still be resumed.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ITransferQueue _queue;
    private readonly IReportService _reports;
    private readonly IJobStateStore _stateStore;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILocalizationService _localization;
    private readonly ILogger<HistoryViewModel> _logger;

    public HistoryViewModel(
        ITransferQueue queue,
        IReportService reports,
        IJobStateStore stateStore,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        ILocalizationService localization,
        ILogger<HistoryViewModel> logger)
    {
        _queue = queue;
        _reports = reports;
        _stateStore = stateStore;
        _settings = settings;
        _dialogs = dialogs;
        _notifications = notifications;
        _localization = localization;
        _logger = logger;

        _queue.JobFinished += OnJobFinished;
    }

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new();
    public ObservableCollection<JobCheckpoint> Resumable { get; } = new();

    [ObservableProperty]
    private HistoryEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private ReportFormat _reportFormat = ReportFormat.Json;

    public bool IsEmpty => Entries.Count == 0;
    public bool HasResumable => Resumable.Count > 0;

    /// <summary>Loads checkpoints left behind by an interrupted run.</summary>
    public async Task LoadResumableAsync()
    {
        try
        {
            Resumable.Clear();
            foreach (var checkpoint in await _stateStore.LoadResumableAsync())
                Resumable.Add(checkpoint);

            OnPropertyChanged(nameof(HasResumable));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load resumable jobs.");
        }
    }

    [RelayCommand]
    private async Task ResumeAsync(JobCheckpoint? checkpoint)
    {
        if (checkpoint is null) return;

        var job = new TransferJob
        {
            Id = checkpoint.JobId,
            Name = string.IsNullOrWhiteSpace(checkpoint.Name) ? checkpoint.JobId : checkpoint.Name,
            DestinationRoot = checkpoint.DestinationRoot,
            Mode = checkpoint.Mode,
            Options = checkpoint.Options
        };

        job.Sources.AddRange(checkpoint.Sources);

        // The engine rescans and then skips whatever the checkpoint lists as finished,
        // so nothing already copied is transferred twice.
        _queue.Enqueue(job);

        Resumable.Remove(checkpoint);
        OnPropertyChanged(nameof(HasResumable));
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DiscardAsync(JobCheckpoint? checkpoint)
    {
        if (checkpoint is null) return;
        if (!_dialogs.Confirm(_localization["History_Discard"] + "?", _localization["Common_Confirm"])) return;

        await _stateStore.DeleteAsync(checkpoint.JobId);
        Resumable.Remove(checkpoint);
        OnPropertyChanged(nameof(HasResumable));
    }

    [RelayCommand]
    private async Task SaveReportAsync(HistoryEntryViewModel? entry)
    {
        entry ??= SelectedEntry;
        if (entry is null) return;

        try
        {
            var path = await _reports.SaveAsync(entry.Result, ReportFormat, _settings.ReportsDirectory);
            entry.ReportPath = path;
            _dialogs.Info(_localization.Format("History_ReportSaved", path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the report.");
            _dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void OpenReportsFolder() => _dialogs.OpenInExplorer(_settings.ReportsDirectory);

    [RelayCommand]
    private void OpenDestination(HistoryEntryViewModel? entry)
    {
        entry ??= SelectedEntry;
        if (entry is null) return;
        _dialogs.OpenInExplorer(entry.Destination);
    }

    private void OnJobFinished(object? sender, JobEventArgs e)
    {
        var application = Application.Current;
        if (application is null) return;

        application.Dispatcher.BeginInvoke(async () =>
        {
            var queued = e.Job;

            if (queued.Result is not null)
            {
                var entry = new HistoryEntryViewModel(queued.Result);
                Entries.Insert(0, entry);
                OnPropertyChanged(nameof(IsEmpty));

                if (_settings.Current.SaveReportAutomatically)
                {
                    try
                    {
                        entry.ReportPath = await _reports.SaveAsync(
                            queued.Result,
                            _settings.Current.DefaultReportFormat,
                            _settings.ReportsDirectory);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Automatic report failed.");
                    }
                }
            }

            // Notifications and the optional "open folder" live here, so they fire once
            // per job no matter which page the user is on.
            switch (queued.State)
            {
                case JobState.Completed:
                case JobState.CompletedWithErrors:
                    _notifications.NotifyCompleted(queued.Job.Name, queued.State == JobState.CompletedWithErrors);
                    if (_settings.Current.OpenDestinationWhenFinished)
                        _dialogs.OpenInExplorer(queued.Job.DestinationRoot);
                    break;

                case JobState.Failed:
                    _notifications.NotifyFailed(queued.Job.Name, queued.Error);
                    if (queued.SpaceProblem is { } problem)
                        _dialogs.ShowSpaceProblem(problem);
                    break;
            }

            await LoadResumableAsync();
        });
    }

    public string FormatSpeed(double bytesPerSecond)
        => ByteFormatter.FormatSpeed(bytesPerSecond, _localization.Culture);
}
