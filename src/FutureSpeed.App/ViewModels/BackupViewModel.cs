using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.ViewModels;

/// <summary>
/// File-level backup: a timestamped set per run, with a manifest. Disk imaging is
/// surfaced as a planned capability with the reason it is not enabled yet.
/// </summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly IBackupService _backup;
    private readonly ITransferQueue _queue;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IDiskImagingProvider _imaging;
    private readonly ILocalizationService _localization;
    private readonly ILogger<BackupViewModel> _logger;

    public BackupViewModel(
        IBackupService backup,
        ITransferQueue queue,
        ISettingsService settings,
        IDialogService dialogs,
        IDiskImagingProvider imaging,
        ILocalizationService localization,
        ILogger<BackupViewModel> logger)
    {
        _backup = backup;
        _queue = queue;
        _settings = settings;
        _dialogs = dialogs;
        _imaging = imaging;
        _localization = localization;
        _logger = logger;

        BackupRoot = settings.DefaultBackupRoot;
        SetName = "Documents";

        _queue.JobFinished += OnJobFinished;
    }

    public ObservableCollection<string> Sources { get; } = new();
    public ObservableCollection<BackupSetInfo> Sets { get; } = new();

    [ObservableProperty] private string _setName = string.Empty;
    [ObservableProperty] private string _backupRoot = string.Empty;
    [ObservableProperty] private bool _verify = true;
    [ObservableProperty] private string? _statusMessage;

    public bool ImagingAvailable => _imaging.GetCapabilities().CanCreateImage;
    public string ImagingReason => _localization["Backup_ImagingUnavailable"];

    public bool CanCreate => Sources.Count > 0 && !string.IsNullOrWhiteSpace(BackupRoot) && !string.IsNullOrWhiteSpace(SetName);

    [RelayCommand]
    private void AddFolder()
    {
        var folders = _dialogs.PickFolders(_localization["Common_AddFolder"]);
        if (folders is null || folders.Length == 0) return;

        foreach (var folder in folders)
        {
            var normalized = PathUtils.Normalize(folder);
            if (normalized.Length == 0) continue;
            if (!Sources.Any(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase)))
                Sources.Add(normalized);
        }

        OnPropertyChanged(nameof(CanCreate));
    }

    [RelayCommand]
    private void AddFiles()
    {
        var files = _dialogs.PickFiles();
        if (files is null) return;

        foreach (var file in files)
        {
            var normalized = PathUtils.Normalize(file);
            if (!Sources.Any(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase)))
                Sources.Add(normalized);
        }

        OnPropertyChanged(nameof(CanCreate));
    }

    [RelayCommand]
    private void RemoveSource(string? path)
    {
        if (path is null) return;
        Sources.Remove(path);
        OnPropertyChanged(nameof(CanCreate));
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var folder = _dialogs.PickFolder(_localization["Backup_Root"]);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            BackupRoot = folder;
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    [RelayCommand]
    private void Create()
    {
        if (!CanCreate) return;

        try
        {
            var options = _settings.Current.ToTransferOptions();
            options.VerifyWithSha256 = Verify;

            var job = _backup.CreateBackupJob(SetName, Sources, BackupRoot, options);
            _queue.Enqueue(job);

            StatusMessage = job.DestinationRoot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the backup job.");
            _dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshSetsAsync()
    {
        try
        {
            Sets.Clear();
            foreach (var set in await _backup.ListBackupSetsAsync(BackupRoot))
                Sets.Add(set);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list backup sets.");
        }
    }

    [RelayCommand]
    private void OpenSet(BackupSetInfo? set)
    {
        if (set is null) return;
        _dialogs.OpenInExplorer(set.Path);
    }

    [RelayCommand]
    private void ShowImagingInfo() => _dialogs.Info(ImagingReason, _localization["Backup_Imaging"]);

    private void OnJobFinished(object? sender, JobEventArgs e)
    {
        // Write the manifest for backup jobs only: they are the ones created here, which
        // is what makes a set self-describing (and restorable later).
        var job = e.Job.Job;
        if (e.Job.Result is null) return;
        if (!PathUtils.IsInside(BackupRoot, job.DestinationRoot)) return;

        _ = WriteManifestAsync(e.Job);
    }

    private async Task WriteManifestAsync(QueuedJob queued)
    {
        try
        {
            if (queued.Result is null) return;

            await _backup.WriteManifestAsync(queued.Job, queued.Result);
            await RefreshSetsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the backup manifest.");
        }
    }
}
