using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.App.ViewModels;

/// <summary>
/// The shell: top-bar navigation, the live "current operation" panel, and the
/// start-up resume prompt. Page view models are injected, so each page keeps its
/// own state while the user moves around.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ITransferQueue _queue;
    private readonly IDriveWatcher _driveWatcher;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly Func<FailureListViewModel> _failures;

    public MainViewModel(
        INavigationService navigation,
        ITransferQueue queue,
        IDriveWatcher driveWatcher,
        ISettingsService settings,
        IThemeService theme,
        IDialogService dialogs,
        ILocalizationService localization,
        Func<FailureListViewModel> failures,
        DrivesViewModel drives,
        NewTransferViewModel newTransfer,
        BackupViewModel backup,
        SyncViewModel sync,
        QueueViewModel queuePage,
        HistoryViewModel history,
        SettingsViewModel settingsPage,
        HelpViewModel help)
    {
        _navigation = navigation;
        _queue = queue;
        _driveWatcher = driveWatcher;
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _localization = localization;
        _failures = failures;

        Drives = drives;
        NewTransfer = newTransfer;
        Backup = backup;
        Sync = sync;
        Queue = queuePage;
        History = history;
        Settings = settingsPage;
        Help = help;

        UiScale = settings.Current.UiScale;
        Settings.Applied += (_, _) => UiScale = _settings.Current.UiScale;

        _navigation.Navigated += (_, page) => CurrentPage = page;
        _queue.JobProgress += (_, _) => RefreshActiveJob();
        _queue.JobStarted += (_, _) => RefreshActiveJob();
        _queue.JobFinished += (_, _) => RefreshActiveJob();
        _queue.QueueChanged += (_, _) => RefreshActiveJob();

        _driveWatcher.DrivesChanged += (_, e) =>
        {
            TotalDrives = e.All.Count;
            OnPropertyChanged(nameof(DrivesSummary));
        };
    }

    public DrivesViewModel Drives { get; }
    public NewTransferViewModel NewTransfer { get; }
    public BackupViewModel Backup { get; }
    public SyncViewModel Sync { get; }
    public QueueViewModel Queue { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public HelpViewModel Help { get; }

    [ObservableProperty]
    private AppPage _currentPage = AppPage.Drives;

    [ObservableProperty]
    private JobItemViewModel? _activeJob;

    [ObservableProperty]
    private double _uiScale = 1.0;

    [ObservableProperty]
    private int _totalDrives;

    [ObservableProperty]
    private int _activeJobCount;

    public FlowDirection FlowDirection => _localization.FlowDirection;

    public string DrivesSummary => $"{TotalDrives}";

    public bool IsArabic => _localization.Language == "ar";

    public bool HasActiveJob => ActiveJob is not null;

    /// <summary>Called by the shell once the window is up.</summary>
    public async Task InitializeAsync()
    {
        _driveWatcher.Start();
        await History.LoadResumableAsync();

        if (History.HasResumable &&
            _dialogs.Confirm(_localization["History_ResumeQuestion"], _localization["History_Resumable"]))
        {
            _navigation.Go(AppPage.History);
        }
    }

    [RelayCommand]
    private void Go(string? page)
    {
        if (page is null) return;
        if (Enum.TryParse<AppPage>(page, ignoreCase: true, out var target))
            _navigation.Go(target);
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        var next = _localization.Language == "ar" ? "en" : "ar";
        _localization.SetLanguage(next);
        _settings.Current.Language = next;
        _settings.Save();

        OnPropertyChanged(nameof(FlowDirection));
        OnPropertyChanged(nameof(IsArabic));
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _theme.IsDark ? AppTheme.Light : AppTheme.Dark;
        _theme.Apply(next);
        _settings.Current.Theme = next;
        _settings.Save();
    }

    [RelayCommand]
    private void PauseActive()
    {
        if (ActiveJob is null) return;
        _queue.PauseJob(ActiveJob.Id);
        ActiveJob.Refresh();
    }

    [RelayCommand]
    private void ResumeActive()
    {
        if (ActiveJob is null) return;
        _queue.ResumeJob(ActiveJob.Id);
        ActiveJob.Refresh();
    }

    [RelayCommand]
    private void CancelActive()
    {
        if (ActiveJob is null) return;
        if (!_dialogs.Confirm(_localization["Queue_ConfirmCancel"], _localization["Common_Cancel"])) return;

        _queue.CancelJob(ActiveJob.Id);
        ActiveJob.Refresh();
    }

    [RelayCommand]
    private void OpenActiveDestination()
    {
        if (ActiveJob is null) return;
        _dialogs.OpenInExplorer(ActiveJob.Destination);
    }

    public string FormatSize(long bytes) => ByteFormatter.FormatSize(bytes, _localization.Culture);

    private void RefreshActiveJob()
    {
        var application = Application.Current;
        if (application is null) return;

        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(RefreshActiveJob);
            return;
        }

        var running = _queue.Jobs.FirstOrDefault(j =>
            j.State is JobState.Running or JobState.Paused or JobState.Scanning or JobState.Validating);

        ActiveJobCount = _queue.Jobs.Count(j => !j.IsFinished);

        if (running is null)
        {
            if (ActiveJob is not null) ActiveJob = null;
            OnPropertyChanged(nameof(HasActiveJob));
            return;
        }

        // Reuse the queue page's item view model so both views show identical numbers.
        var item = Queue.Jobs.FirstOrDefault(j => j.Id == running.Job.Id);
        if (item is null)
        {
            item = new JobItemViewModel(running, _failures());
        }
        else
        {
            item.Refresh();
        }

        if (!ReferenceEquals(ActiveJob, item))
            ActiveJob = item;

        OnPropertyChanged(nameof(HasActiveJob));
    }
}
