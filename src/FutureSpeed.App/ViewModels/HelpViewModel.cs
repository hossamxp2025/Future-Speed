using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Services;

namespace FutureSpeed.App.ViewModels;

public sealed partial class HelpViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IElevationService _elevation;

    public HelpViewModel(ISettingsService settings, IDialogService dialogs, IElevationService elevation)
    {
        _settings = settings;
        _dialogs = dialogs;
        _elevation = elevation;
    }

    public string Version => typeof(HelpViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public bool IsElevated => _elevation.IsElevated;
    public string LogsDirectory => _settings.LogsDirectory;
    public string ReportsDirectory => _settings.ReportsDirectory;

    [RelayCommand]
    private void OpenLogs() => _dialogs.OpenInExplorer(_settings.LogsDirectory);

    [RelayCommand]
    private void OpenReports() => _dialogs.OpenInExplorer(_settings.ReportsDirectory);

    [RelayCommand]
    private void OpenAppData() => _dialogs.OpenInExplorer(_settings.AppDataDirectory);
}
