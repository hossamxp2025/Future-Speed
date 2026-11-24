using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Models;

namespace FutureSpeed.App.ViewModels;

/// <summary>One drive card on the dashboard.</summary>
public sealed partial class DriveCardViewModel : ObservableObject
{
    public DriveCardViewModel(DriveModel model) => Model = model;

    [ObservableProperty]
    private DriveModel _model;

    public string DisplayName => Model.DisplayName;
    public string Letter => Model.Letter;
    public string FileSystem => Model.FileSystem ?? LocalizationService.Instance["Common_Unknown"];
    public long TotalBytes => Model.TotalBytes;
    public long UsedBytes => Model.UsedBytes;
    public long FreeBytes => Model.FreeBytes;
    public double UsedPercent => Model.UsedPercent;
    public bool IsReady => Model.IsReady;
    public DriveKindHint Kind => Model.Kind;
    public string RootPath => Model.RootPath;

    /// <summary>Red once the volume is nearly full, so a bad destination is obvious at a glance.</summary>
    public bool IsAlmostFull => Model.IsReady && Model.UsedPercent >= 92;

    public void Update(DriveModel model)
    {
        Model = model;
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class DrivesViewModel : ObservableObject
{
    private readonly IDriveWatcher _watcher;
    private readonly INavigationService _navigation;
    private readonly NewTransferViewModel _newTransfer;

    public DrivesViewModel(
        IDriveWatcher watcher,
        INavigationService navigation,
        NewTransferViewModel newTransfer)
    {
        _watcher = watcher;
        _navigation = navigation;
        _newTransfer = newTransfer;

        _watcher.DrivesChanged += OnDrivesChanged;

        // The notice is a formatted sentence, so it cannot follow a language switch;
        // clearing it is honest and the next hot-plug writes it in the new language.
        LocalizationService.Instance.PropertyChanged += (_, _) => DeviceNotice = null;

        Apply(_watcher.Snapshot);
    }

    public ObservableCollection<DriveCardViewModel> Drives { get; } = new();

    [ObservableProperty]
    private DriveCardViewModel? _selectedDrive;

    [ObservableProperty]
    private string? _deviceNotice;

    [RelayCommand]
    private void Refresh() => _watcher.RefreshNow();

    [RelayCommand]
    private void UseAsDestination(DriveCardViewModel? card)
    {
        if (card is null || !card.IsReady) return;

        _newTransfer.Destination = card.RootPath;
        _navigation.Go(AppPage.NewTransfer);
    }

    [RelayCommand]
    private void UseAsSource(DriveCardViewModel? card)
    {
        if (card is null || !card.IsReady) return;

        _newTransfer.AddPaths(new[] { card.RootPath });
        _navigation.Go(AppPage.NewTransfer);
    }

    private void OnDrivesChanged(object? sender, DriveChangeEventArgs e)
    {
        Apply(e.All);

        if (e.Added.Count > 0)
        {
            DeviceNotice = LocalizationService.Instance.Format(
                "Drives_NewDeviceDetected",
                string.Join(", ", e.Added.Select(d => d.DisplayName)));
        }
        else if (e.Removed.Count > 0)
        {
            DeviceNotice = LocalizationService.Instance.Format(
                "Drives_DeviceRemoved",
                string.Join(", ", e.Removed.Select(d => d.DisplayName)));
        }
    }

    private void Apply(IReadOnlyList<DriveModel> drives)
    {
        // Update in place so the cards do not flicker on every two-second poll.
        foreach (var model in drives)
        {
            var existing = Drives.FirstOrDefault(d =>
                string.Equals(d.RootPath, model.RootPath, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
                Drives.Add(new DriveCardViewModel(model));
            else
                existing.Update(model);
        }

        foreach (var stale in Drives
                     .Where(card => drives.All(m => !string.Equals(m.RootPath, card.RootPath, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            Drives.Remove(stale);
        }
    }
}
