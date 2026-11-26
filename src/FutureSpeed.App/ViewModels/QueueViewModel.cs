using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Transfer;

namespace FutureSpeed.App.ViewModels;

/// <summary>
/// The queue page: order, priority, pause / resume / cancel, retry of failed files.
/// All queue events arrive on worker threads and are marshalled to the dispatcher here,
/// which is the only place in the UI that needs to know that.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly ITransferQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly Func<FailureListViewModel> _failures;

    public QueueViewModel(
        ITransferQueue queue,
        IDialogService dialogs,
        ISettingsService settings,
        ILocalizationService localization,
        Func<FailureListViewModel> failures)
    {
        _queue = queue;
        _dialogs = dialogs;
        _settings = settings;
        _localization = localization;
        _failures = failures;

        _queue.MaxConcurrentJobs = settings.Current.MaxConcurrentJobs;

        _queue.JobAdded += OnQueueMutated;
        _queue.JobStarted += OnQueueMutated;
        _queue.JobFinished += OnQueueMutated;
        _queue.QueueChanged += (_, _) => Dispatch(Sync);
        _queue.JobProgress += OnJobProgress;

        Sync();
    }

    public ObservableCollection<JobItemViewModel> Jobs { get; } = new();

    [ObservableProperty]
    private JobItemViewModel? _selectedJob;

    public bool IsEmpty => Jobs.Count == 0;

    [RelayCommand]
    private void Pause(JobItemViewModel? job)
    {
        if (job is null) return;
        _queue.PauseJob(job.Id);
        job.Refresh();
    }

    [RelayCommand]
    private void Resume(JobItemViewModel? job)
    {
        if (job is null) return;
        _queue.ResumeJob(job.Id);
        job.Refresh();
    }

    [RelayCommand]
    private void Cancel(JobItemViewModel? job)
    {
        if (job is null) return;
        if (!_dialogs.Confirm(_localization["Queue_ConfirmCancel"], _localization["Common_Cancel"])) return;

        _queue.CancelJob(job.Id);
        job.Refresh();
    }

    [RelayCommand]
    private void Remove(JobItemViewModel? job)
    {
        if (job is null) return;
        if (!_dialogs.Confirm(_localization["Queue_ConfirmRemove"], _localization["Common_Remove"])) return;

        _queue.RemoveJob(job.Id);
    }

    [RelayCommand]
    private void MoveUp(JobItemViewModel? job)
    {
        if (job is null) return;
        _queue.MoveUp(job.Id);
    }

    [RelayCommand]
    private void MoveDown(JobItemViewModel? job)
    {
        if (job is null) return;
        _queue.MoveDown(job.Id);
    }

    [RelayCommand]
    private void RetryFailed(JobItemViewModel? job)
    {
        if (job is null) return;

        var retry = _queue.RetryFailed(job.Id);
        if (retry is null)
            _dialogs.Info(_localization["History_NoErrors"]);
    }

    [RelayCommand]
    private void ClearFinished() => _queue.ClearFinished();

    [RelayCommand]
    private void OpenDestination(JobItemViewModel? job)
    {
        if (job is null) return;
        _dialogs.OpenInExplorer(job.Destination);
    }

    private void OnJobProgress(object? sender, JobEventArgs e)
        => Dispatch(() =>
        {
            var item = Jobs.FirstOrDefault(j => j.Id == e.Job.Job.Id);
            item?.Refresh();
        });

    private void OnQueueMutated(object? sender, JobEventArgs e) => Dispatch(Sync);

    private void Sync()
    {
        var snapshot = _queue.Jobs;

        // Rebuild order to match the queue, reusing existing item view models.
        for (var i = 0; i < snapshot.Count; i++)
        {
            var queued = snapshot[i];
            var existingIndex = IndexOf(queued.Job.Id);

            if (existingIndex < 0)
            {
                Jobs.Insert(Math.Min(i, Jobs.Count), new JobItemViewModel(queued, _failures()));
                continue;
            }

            Jobs[existingIndex].Refresh();
            if (existingIndex != i)
                Jobs.Move(existingIndex, Math.Min(i, Jobs.Count - 1));
        }

        foreach (var stale in Jobs.Where(j => snapshot.All(q => q.Job.Id != j.Id)).ToList())
            Jobs.Remove(stale);

        OnPropertyChanged(nameof(IsEmpty));
    }

    private int IndexOf(string jobId)
    {
        for (var i = 0; i < Jobs.Count; i++)
            if (Jobs[i].Id == jobId) return i;

        return -1;
    }

    private static void Dispatch(Action action)
    {
        var application = Application.Current;
        if (application is null) return;

        if (application.Dispatcher.CheckAccess()) action();
        else application.Dispatcher.BeginInvoke(action);
    }
}
