using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.ViewModels;

/// <summary>
/// Edits a working copy of the settings and writes it on Save; language, theme and UI
/// scale are applied live so the user can see what they picked before saving.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogs;
    private readonly IDemoDataService _demoData;
    private readonly ITransferQueue _queue;
    private readonly IExplorerIntegrationManager _integration;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        ILocalizationService localization,
        IDialogService dialogs,
        IDemoDataService demoData,
        ITransferQueue queue,
        IExplorerIntegrationManager integration,
        ILogger<SettingsViewModel> logger)
    {
        _integration = integration;
        _settings = settings;
        _theme = theme;
        _localization = localization;
        _dialogs = dialogs;
        _demoData = demoData;
        _queue = queue;
        _logger = logger;

        LoadFromSettings();
        RefreshIntegrationStatus();
    }

    public IReadOnlyList<string> Languages { get; } = new[] { "ar", "en" };
    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<ConflictPolicy> ConflictPolicies { get; } = Enum.GetValues<ConflictPolicy>();
    public IReadOnlyList<ReportFormat> ReportFormats { get; } = Enum.GetValues<ReportFormat>();

    [ObservableProperty] private string _language = "ar";
    [ObservableProperty] private AppTheme _selectedTheme = AppTheme.Light;
    [ObservableProperty] private double _uiScale = 1.0;
    [ObservableProperty] private bool _autoTune = true;
    [ObservableProperty] private int _bufferSizeKb = 1024;
    [ObservableProperty] private int _maxParallelFiles = 4;
    [ObservableProperty] private int _maxConcurrentJobs = 1;
    [ObservableProperty] private bool _verifyWithSha256;
    [ObservableProperty] private ConflictPolicy _conflictPolicy = ConflictPolicy.Ask;
    [ObservableProperty] private int _maxSpeedMbPerSecond;
    [ObservableProperty] private bool _preserveTimestamps = true;
    [ObservableProperty] private bool _preserveAttributes = true;
    [ObservableProperty] private bool _enableResume = true;
    [ObservableProperty] private bool _playSoundWhenFinished = true;
    [ObservableProperty] private bool _showWindowsNotification = true;
    [ObservableProperty] private bool _openDestinationWhenFinished;
    [ObservableProperty] private ReportFormat _defaultReportFormat = ReportFormat.Json;
    [ObservableProperty] private bool _saveReportAutomatically = true;
    [ObservableProperty] private string? _logsDirectory;
    [ObservableProperty] private string? _reportsDirectory;
    [ObservableProperty] private string? _backupRoot;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;

    // ---- Windows integration ------------------------------------------------
    [ObservableProperty] private bool _preferCompactWindow = true;
    [ObservableProperty] private bool _integrationInstalled;
    [ObservableProperty] private bool _integrationForAllUsers;
    [ObservableProperty] private string _integrationStatusText = string.Empty;
    [ObservableProperty] private string? _integrationKeysPreview;

    public string IntegrationLimitNote => _localization["Integration_Limit"];

    public void RefreshIntegrationStatus()
    {
        var scope = IntegrationForAllUsers ? IntegrationScope.AllUsers : IntegrationScope.CurrentUser;
        var status = _integration.GetStatus(scope);

        IntegrationInstalled = status.IsInstalled;
        IntegrationStatusText = status.State switch
        {
            IntegrationState.Installed => _localization["Integration_Installed"],
            IntegrationState.Stale => _localization["Integration_Stale"],
            _ => _localization["Integration_NotInstalled"]
        };

        IntegrationKeysPreview = string.Join(
            Environment.NewLine,
            ExplorerIntegrationService.VerbKeys(scope).Select(k => @"HKCU\" + k));

        OnPropertyChanged(nameof(IntegrationLimitNote));
    }

    [RelayCommand]
    private void EnableIntegration()
    {
        var scope = IntegrationForAllUsers ? IntegrationScope.AllUsers : IntegrationScope.CurrentUser;

        // Nothing is written before the user has seen exactly what will change.
        var message = _localization["Integration_Consent"]
                      + Environment.NewLine + Environment.NewLine
                      + _localization["Integration_Keys"] + ":" + Environment.NewLine
                      + IntegrationKeysPreview;

        if (!_dialogs.Confirm(message, _localization["Integration_ConsentTitle"]))
            return;

        if (_integration.Enable(scope))
        {
            _settings.Current.ExplorerIntegrationEnabled = true;
            _settings.Save();
            StatusMessage = _localization["Integration_Done"];
        }
        else
        {
            StatusMessage = scope == IntegrationScope.AllUsers
                ? _localization["Integration_NeedsAdmin"]
                : _localization["Integration_Failed"];
        }

        RefreshIntegrationStatus();
    }

    [RelayCommand]
    private void DisableIntegration()
    {
        if (!_dialogs.Confirm(_localization["Integration_ConsentRemove"], _localization["Integration_ConsentTitle"]))
            return;

        _integration.DisableEverywhere();
        _settings.Current.ExplorerIntegrationEnabled = false;
        _settings.Save();

        StatusMessage = _localization["Integration_Done"];
        RefreshIntegrationStatus();
    }

    partial void OnIntegrationForAllUsersChanged(bool value) => RefreshIntegrationStatus();

    /// <summary>Raised after a save so the shell can re-apply the scale / direction.</summary>
    public event EventHandler? Applied;

    // Language and theme apply immediately: they are visual choices, and a preview is
    // more useful than a modal "are you sure".
    partial void OnLanguageChanged(string value) => _localization.SetLanguage(value);

    partial void OnSelectedThemeChanged(AppTheme value) => _theme.Apply(value);

    partial void OnUiScaleChanged(double value) => Applied?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Save()
    {
        var settings = new AppSettings
        {
            Language = Language,
            Theme = SelectedTheme,
            UiScale = UiScale,
            AutoTune = AutoTune,
            BufferSizeKb = BufferSizeKb,
            MaxParallelFiles = MaxParallelFiles,
            MaxConcurrentJobs = MaxConcurrentJobs,
            VerifyWithSha256 = VerifyWithSha256,
            ConflictPolicy = ConflictPolicy,
            MaxSpeedMbPerSecond = MaxSpeedMbPerSecond,
            PreserveTimestamps = PreserveTimestamps,
            PreserveAttributes = PreserveAttributes,
            EnableResume = EnableResume,
            PlaySoundWhenFinished = PlaySoundWhenFinished,
            ShowWindowsNotification = ShowWindowsNotification,
            OpenDestinationWhenFinished = OpenDestinationWhenFinished,
            DefaultReportFormat = DefaultReportFormat,
            SaveReportAutomatically = SaveReportAutomatically,
            PreferCompactWindow = PreferCompactWindow,
            ExplorerIntegrationEnabled = _settings.Current.ExplorerIntegrationEnabled,
            LogsDirectory = LogsDirectory,
            ReportsDirectory = ReportsDirectory,
            BackupRoot = BackupRoot,
            RecentDestinations = _settings.Current.RecentDestinations
        };

        _settings.Replace(settings);
        _queue.MaxConcurrentJobs = settings.MaxConcurrentJobs;

        LoadFromSettings();
        StatusMessage = _localization["Settings_Saved"];
        Applied?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Reset()
    {
        _settings.Replace(new AppSettings { RecentDestinations = _settings.Current.RecentDestinations });
        LoadFromSettings();
        _localization.SetLanguage(Language);
        _theme.Apply(SelectedTheme);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void BrowseLogs()
    {
        var folder = _dialogs.PickFolder(_localization["Settings_LogsDirectory"]);
        if (!string.IsNullOrWhiteSpace(folder)) LogsDirectory = folder;
    }

    [RelayCommand]
    private void BrowseReports()
    {
        var folder = _dialogs.PickFolder(_localization["Settings_ReportsDirectory"]);
        if (!string.IsNullOrWhiteSpace(folder)) ReportsDirectory = folder;
    }

    [RelayCommand]
    private void BrowseBackupRoot()
    {
        var folder = _dialogs.PickFolder(_localization["Settings_BackupRoot"]);
        if (!string.IsNullOrWhiteSpace(folder)) BackupRoot = folder;
    }

    [RelayCommand]
    private void OpenLogs() => _dialogs.OpenInExplorer(_settings.LogsDirectory);

    [RelayCommand]
    private async Task CreateDemoDataAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _demoData.CreateAsync();
            StatusMessage = _localization.Format("Settings_DemoCreated", result.FileCount, result.Root);
            _dialogs.OpenInExplorer(_demoData.DemoRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo data creation failed.");
            _dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadFromSettings()
    {
        var current = _settings.Current;

        Language = current.Language;
        SelectedTheme = current.Theme;
        UiScale = current.UiScale;
        AutoTune = current.AutoTune;
        BufferSizeKb = current.BufferSizeKb;
        MaxParallelFiles = current.MaxParallelFiles;
        MaxConcurrentJobs = current.MaxConcurrentJobs;
        VerifyWithSha256 = current.VerifyWithSha256;
        ConflictPolicy = current.ConflictPolicy;
        MaxSpeedMbPerSecond = current.MaxSpeedMbPerSecond;
        PreserveTimestamps = current.PreserveTimestamps;
        PreserveAttributes = current.PreserveAttributes;
        EnableResume = current.EnableResume;
        PlaySoundWhenFinished = current.PlaySoundWhenFinished;
        ShowWindowsNotification = current.ShowWindowsNotification;
        OpenDestinationWhenFinished = current.OpenDestinationWhenFinished;
        DefaultReportFormat = current.DefaultReportFormat;
        SaveReportAutomatically = current.SaveReportAutomatically;
        PreferCompactWindow = current.PreferCompactWindow;
        LogsDirectory = current.LogsDirectory ?? _settings.LogsDirectory;
        ReportsDirectory = current.ReportsDirectory ?? _settings.ReportsDirectory;
        BackupRoot = current.BackupRoot ?? _settings.DefaultBackupRoot;
    }
}
