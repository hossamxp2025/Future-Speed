using System.ComponentModel;
using System.Windows;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.App.ViewModels;
using FutureSpeed.Core.Transfer;

namespace FutureSpeed.App.Views;

/// <summary>
/// The small window Explorer opens. It owns one job and never forces the main window
/// to appear; the user can call it up from the title bar button.
/// </summary>
public partial class TransferCompactWindow : Window
{
    private readonly CompactTransferViewModel _viewModel;
    private readonly INotificationService _notifications;
    private readonly ILocalizationService _localization;
    private readonly Func<MainWindow> _mainWindowFactory;

    public TransferCompactWindow(
        CompactTransferViewModel viewModel,
        INotificationService notifications,
        ILocalizationService localization,
        Func<MainWindow> mainWindowFactory)
    {
        _viewModel = viewModel;
        _notifications = notifications;
        _localization = localization;
        _mainWindowFactory = mainWindowFactory;

        InitializeComponent();

        DataContext = viewModel;
        FlowDirection = localization.FlowDirection;

        viewModel.MainWindowRequested += (_, _) => ShowMainWindow();
        viewModel.MinimizeToTrayRequested += (_, _) => MinimizeToTray();

        localization.PropertyChanged += (_, _) => FlowDirection = localization.FlowDirection;
    }

    /// <summary>The job this window follows; used to avoid opening two windows for one job.</summary>
    public string? JobId { get; private set; }

    public void Attach(QueuedJob job)
    {
        JobId = job.Job.Id;
        _viewModel.Attach(job);
    }

    private void ShowMainWindow()
    {
        var application = Application.Current;
        if (application is null) return;

        var existing = application.Windows.OfType<MainWindow>().FirstOrDefault();
        if (existing is null)
        {
            existing = _mainWindowFactory();
            application.MainWindow = existing;
            existing.Show();
        }
        else
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Show();
        }

        existing.Activate();
    }

    private void MinimizeToTray()
    {
        Hide();
        _notifications.ShowBackgroundHint(_localization["Compact_MinimizedHint"], Restore);
    }

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window must not abandon a running transfer silently: it keeps
        // running and the tray icon brings the window back.
        if (!_viewModel.IsFinished && _viewModel.CanCancel)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        base.OnClosing(e);
    }
}
