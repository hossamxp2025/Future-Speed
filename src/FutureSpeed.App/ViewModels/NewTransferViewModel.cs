using System.Collections.ObjectModel;
using System.IO;
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

/// <summary>
/// Builds a transfer: pick sources, pick a destination, check space, then start or queue.
/// The space check is deliberately a separate, visible step - the engine repeats it, but
/// the user gets the numbers before anything is written.
/// </summary>
public sealed partial class NewTransferViewModel : ObservableObject
{
    private readonly IFileSystemScanner _scanner;
    private readonly ISpaceValidator _spaceValidator;
    private readonly ITransferQueue _queue;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IElevationService _elevation;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _localization;
    private readonly ILogger<NewTransferViewModel> _logger;

    private ScanResult? _lastScan;
    private CancellationTokenSource? _scanCts;

    public NewTransferViewModel(
        IFileSystemScanner scanner,
        ISpaceValidator spaceValidator,
        ITransferQueue queue,
        ISettingsService settings,
        IDialogService dialogs,
        IElevationService elevation,
        INavigationService navigation,
        ILocalizationService localization,
        ILogger<NewTransferViewModel> logger)
    {
        _scanner = scanner;
        _spaceValidator = spaceValidator;
        _queue = queue;
        _settings = settings;
        _dialogs = dialogs;
        _elevation = elevation;
        _navigation = navigation;
        _localization = localization;
        _logger = logger;

        Mode = TransferMode.Copy;
        VerifyWithSha256 = settings.Current.VerifyWithSha256;
        Sources.CollectionChanged += (_, _) =>
        {
            InvalidateScan();
            OnPropertyChanged(nameof(HasSources));
            StartTransferCommand.NotifyCanExecuteChanged();
            AddToQueueCommand.NotifyCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<string> Sources { get; } = new();

    public bool HasSources => Sources.Count > 0;

    [ObservableProperty]
    private string? _destination;

    [ObservableProperty]
    private TransferMode _mode;

    [ObservableProperty]
    private bool _verifyWithSha256;

    [ObservableProperty]
    private string? _jobName;

    [ObservableProperty]
    private bool _isScheduled;

    [ObservableProperty]
    private DateTime _scheduledFor = DateTime.Now.AddMinutes(10);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _scanSummary;

    [ObservableProperty]
    private SpaceValidationResult? _spaceResult;

    [ObservableProperty]
    private string? _selectedSource;

    public IReadOnlyList<string> RecentDestinations => _settings.Current.RecentDestinations;

    partial void OnDestinationChanged(string? value)
    {
        InvalidateScan();
        StartTransferCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    partial void OnModeChanged(TransferMode value) => InvalidateScan();

    /// <summary>Adds paths from the add buttons or from a drag-and-drop payload.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = PathUtils.Normalize(raw);
            if (path.Length == 0) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (Sources.Any(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase))) continue;

            Sources.Add(path);
        }
    }

    [RelayCommand]
    private void AddFiles()
    {
        var files = _dialogs.PickFiles();
        if (files is { Length: > 0 }) AddPaths(files);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var folders = _dialogs.PickFolders();
        if (folders is { Length: > 0 }) AddPaths(folders);
    }

    [RelayCommand]
    private void RemoveSource(string? path)
    {
        if (path is null) return;
        Sources.Remove(path);
    }

    [RelayCommand]
    private void ClearSources() => Sources.Clear();

    [RelayCommand]
    private void SetDestination(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) Destination = path;
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var folder = _dialogs.PickFolder(_localization["Transfer_Destination"]);
        if (!string.IsNullOrWhiteSpace(folder)) Destination = folder;
    }

    private bool CanAnalyze() => HasSources && !string.IsNullOrWhiteSpace(Destination) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (!Validate()) return;

        IsBusy = true;
        StatusMessage = _localization["Transfer_Scanning"];
        SpaceResult = null;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        try
        {
            var scan = await _scanner.ScanAsync(Sources, Destination!, null, _scanCts.Token);
            _lastScan = scan;

            ScanSummary = _localization.Format(
                "Transfer_Summary",
                scan.FileCount,
                ByteFormatter.FormatSize(scan.TotalBytes, _localization.Culture));

            SpaceResult = _spaceValidator.Validate(scan, Destination!, BuildOptions());

            StatusMessage = SpaceResult.Status switch
            {
                SpaceValidationStatus.Sufficient => _localization["Space_Sufficient"],
                SpaceValidationStatus.Insufficient => _localization["Space_Insufficient"],
                SpaceValidationStatus.Undeterminable => _localization["Space_Undeterminable"],
                _ => SpaceResult.Message
            };
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed.");
            _dialogs.Error(ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
            StartTransferCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStart() => HasSources && !string.IsNullOrWhiteSpace(Destination) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartTransferAsync() => await EnqueueAsync(startNow: true);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task AddToQueueAsync() => await EnqueueAsync(startNow: false);

    private async Task EnqueueAsync(bool startNow)
    {
        if (!Validate()) return;

        // Space is always validated before a job is queued: the user sees the numbers
        // here, and the engine refuses again at run time if things changed meanwhile.
        if (_lastScan is null || SpaceResult is null)
            await AnalyzeAsync();

        if (_lastScan is null) return;

        if (SpaceResult is { Status: SpaceValidationStatus.Insufficient })
        {
            _dialogs.ShowSpaceProblem(SpaceResult);
            return;
        }

        if (SpaceResult is { RequiresOverride: true } && !_dialogs.ShowSpaceProblem(SpaceResult))
            return;

        if (_elevation.RequiresElevation(Destination!))
        {
            if (!_dialogs.Confirm(_localization["Error_NeedsAdmin"], _localization["Error_Title"]))
                return;

            if (_elevation.RestartElevated())
                return;
        }

        var job = new TransferJob
        {
            Name = string.IsNullOrWhiteSpace(JobName)
                ? BuildDefaultName()
                : JobName!.Trim(),
            DestinationRoot = Destination!,
            Mode = Mode,
            Options = BuildOptions(),
            Scan = _lastScan,
            ScheduledFor = IsScheduled ? new DateTimeOffset(ScheduledFor) : null
        };

        job.Sources.AddRange(Sources);

        _queue.Enqueue(job, startImmediately: startNow || IsScheduled);
        RememberDestination(Destination!);

        // The plan belongs to the queued job now; the next transfer starts clean.
        _lastScan = null;
        SpaceResult = null;
        ScanSummary = null;
        StatusMessage = null;
        JobName = null;

        _navigation.Go(AppPage.Queue);
    }

    private bool Validate()
    {
        if (!HasSources)
        {
            _dialogs.Error(_localization["Transfer_NoSources"]);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Destination))
        {
            _dialogs.Error(_localization["Transfer_NoDestination"]);
            return false;
        }

        var problems = PathUtils.ValidateDestinationAgainstSources(Destination!, Sources);
        if (problems.Count > 0)
        {
            _dialogs.Error(
                _localization["Error_SameSourceAndDestination"] + Environment.NewLine + string.Join(Environment.NewLine, problems),
                _localization["Error_InvalidDestination"]);
            return false;
        }

        return true;
    }

    private TransferOptions BuildOptions()
    {
        var options = _settings.Current.ToTransferOptions();
        options.VerifyWithSha256 = VerifyWithSha256;
        return options;
    }

    private string BuildDefaultName()
    {
        var first = Sources.FirstOrDefault() ?? string.Empty;
        var label = Path.GetFileName(first.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(label)) label = first;

        var extra = Sources.Count > 1 ? $" +{Sources.Count - 1}" : string.Empty;
        return $"{label}{extra} → {Path.GetFileName(Destination!.TrimEnd('\\'))}";
    }

    private void RememberDestination(string destination)
    {
        var recents = _settings.Current.RecentDestinations;
        recents.RemoveAll(d => string.Equals(d, destination, StringComparison.OrdinalIgnoreCase));
        recents.Insert(0, destination);
        if (recents.Count > 10) recents.RemoveRange(10, recents.Count - 10);
        _settings.Save();
        OnPropertyChanged(nameof(RecentDestinations));
    }

    private void InvalidateScan()
    {
        _lastScan = null;
        SpaceResult = null;
        ScanSummary = null;
    }
}
