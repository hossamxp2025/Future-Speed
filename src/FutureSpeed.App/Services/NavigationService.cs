namespace FutureSpeed.App.Services;

public enum AppPage
{
    Drives = 0,
    NewTransfer = 1,
    Backup = 2,
    Sync = 3,
    Queue = 4,
    History = 5,
    Settings = 6,
    Help = 7
}

public interface INavigationService
{
    AppPage Current { get; }
    event EventHandler<AppPage>? Navigated;
    void Go(AppPage page);
}

/// <summary>
/// A tiny event bus for page changes. It exists so leaf view models (a drive card asking
/// to start a transfer, for instance) can navigate without holding a reference to the
/// shell view model, which would be a dependency cycle.
/// </summary>
public sealed class NavigationService : INavigationService
{
    public AppPage Current { get; private set; } = AppPage.Drives;

    public event EventHandler<AppPage>? Navigated;

    public void Go(AppPage page)
    {
        Current = page;
        Navigated?.Invoke(this, page);
    }
}
