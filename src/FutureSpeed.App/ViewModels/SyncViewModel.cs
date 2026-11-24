using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.ViewModels;

/// <summary>
/// One-way, mirror or two-way folder sync. Analysis and execution are separate steps,
/// and deleting extra files always needs an explicit confirmation.
/// </summary>
public sealed partial class SyncViewModel : ObservableObject
{
    private readonly ISyncService _sync;
    private readonly ITransferQueue _queue;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _localization;
    private readonly ILogger<SyncViewModel> _logger;

    private SyncPlan? _plan;

    public SyncViewModel(
        ISyncService sync,
        ITransferQueue queue,
        ISettingsService settings,
        IDialogService dialogs,
        INavigationService navigation,
        ILocalizationService localization,
        ILogger<SyncViewModel> logger)
    {
        _sync = sync;
        _queue = queue;
        _settings = settings;
        _dialogs = dialogs;
        _navigation = navigation;
        _localization = localization;
        _logger = logger;
    }

    public IReadOnlyList<SyncDirection> Directions { get; } = Enum.GetValues<SyncDirection>();

    [ObservableProperty] private string? _leftFolder;
    [ObservableProperty] private string? _rightFolder;
    [ObservableProperty] private SyncDirection _direction = SyncDirection.OneWay;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private int _filesToCopy;
    [ObservableProperty] private long _bytesToCopy;
    [ObservableProperty] private int _extraFiles;

    public bool HasPlan => _plan is not null && _plan.HasWork;
    public bool HasExtras => ExtraFiles > 0 && Direction == SyncDirection.Mirror;

    [RelayCommand]
    private void BrowseLeft()
    {
        var folder = _dialogs.PickFolder(_localization["Sync_Left"]);
        if (!string.IsNullOrWhiteSpace(folder)) LeftFolder = folder;
    }

    [RelayCommand]
    private void BrowseRight()
    {
        var folder = _dialogs.PickFolder(_localization["Sync_Right"]);
        if (!string.IsNullOrWhiteSpace(folder)) RightFolder = folder;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(LeftFolder) || string.IsNullOrWhiteSpace(RightFolder))
        {
            _dialogs.Error(_localization["Transfer_NoDestination"]);
            return;
        }

        IsBusy = true;
        StatusMessage = _localization["Transfer_Scanning"];

        try
        {
            _plan = await _sync.BuildPlanAsync(LeftFolder!, RightFolder!, Direction);

            if (_plan.Problems.Count > 0)
                _dialogs.Error(string.Join(Environment.NewLine, _plan.Problems.Take(10)));

            FilesToCopy = _plan.FilesToCopy;
            BytesToCopy = _plan.BytesToCopy;
            ExtraFiles = _plan.ExtraDestinationFiles.Count;

            StatusMessage = _plan.HasWork
                ? _localization.Format(
                    "Sync_Analyzed",
                    FilesToCopy,
                    ByteFormatter.FormatSize(BytesToCopy, _localization.Culture),
                    ExtraFiles)
                : _localization["Sync_NoWork"];

            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(HasExtras));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync analysis failed.");
            _dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Run()
    {
        if (_plan is null || _plan.FilesToCopy == 0)
        {
            _dialogs.Info(_localization["Sync_NoWork"]);
            return;
        }

        var options = _settings.Current.ToTransferOptions();

        // Sync must never stop to ask: the comparison already decided what changes.
        options.ConflictPolicy = ConflictPolicy.Overwrite;

        var job = new TransferJob
        {
            Name = $"Sync: {System.IO.Path.GetFileName(LeftFolder!.TrimEnd('\\'))} → {System.IO.Path.GetFileName(RightFolder!.TrimEnd('\\'))}",
            DestinationRoot = RightFolder!,
            Mode = TransferMode.Copy,
            Options = options,

            // The plan is already a complete, mapped scan result.
            Scan = _plan.Transfer
        };

        job.Sources.Add(LeftFolder!);
        if (Direction == SyncDirection.TwoWay)
            job.Sources.Add(RightFolder!);

        _queue.Enqueue(job);
        _navigation.Go(AppPage.Queue);
    }

    [RelayCommand]
    private async Task DeleteExtrasAsync()
    {
        if (_plan is null || _plan.ExtraDestinationFiles.Count == 0) return;

        if (!_dialogs.Confirm(
                _localization.Format("Sync_ConfirmDelete", _plan.ExtraDestinationFiles.Count),
                _localization["Common_Warning"]))
            return;

        IsBusy = true;
        try
        {
            var deleted = await _sync.DeleteExtrasAsync(_plan);
            StatusMessage = $"{deleted} / {_plan.ExtraDestinationFiles.Count}";
            await AnalyzeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting extra files failed.");
            _dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnDirectionChanged(SyncDirection value)
    {
        _plan = null;
        FilesToCopy = 0;
        BytesToCopy = 0;
        ExtraFiles = 0;
        StatusMessage = null;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasExtras));
    }
}
