using System.Windows.Threading;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;

namespace FutureSpeed.App.Services;

public sealed class DriveChangeEventArgs : EventArgs
{
    public DriveChangeEventArgs(IReadOnlyList<DriveModel> added, IReadOnlyList<DriveModel> removed, IReadOnlyList<DriveModel> all)
    {
        Added = added;
        Removed = removed;
        All = all;
    }

    public IReadOnlyList<DriveModel> Added { get; }
    public IReadOnlyList<DriveModel> Removed { get; }
    public IReadOnlyList<DriveModel> All { get; }
}

public interface IDriveWatcher
{
    event EventHandler<DriveChangeEventArgs>? DrivesChanged;
    IReadOnlyList<DriveModel> Snapshot { get; }
    void Start();
    void Stop();
    void RefreshNow();
}

/// <summary>
/// Polls the volume list on the UI dispatcher. Polling (rather than a WMI event watcher)
/// keeps the app free of a permanent background COM subscription, and a 2-second tick is
/// fast enough to notice a USB stick while costing nothing measurable.
/// </summary>
public sealed class DriveWatcher : IDriveWatcher
{
    private readonly IDriveService _drives;
    private readonly DispatcherTimer _timer;
    private Dictionary<string, DriveModel> _known = new(StringComparer.OrdinalIgnoreCase);

    public DriveWatcher(IDriveService drives)
    {
        _drives = drives;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => RefreshNow();
    }

    public event EventHandler<DriveChangeEventArgs>? DrivesChanged;

    public IReadOnlyList<DriveModel> Snapshot { get; private set; } = Array.Empty<DriveModel>();

    public void Start()
    {
        RefreshNow();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void RefreshNow()
    {
        var current = _drives.GetDrives();
        var map = current.ToDictionary(d => d.RootPath, StringComparer.OrdinalIgnoreCase);

        var added = current.Where(d => !_known.ContainsKey(d.RootPath)).ToList();
        var removed = _known.Values.Where(d => !map.ContainsKey(d.RootPath)).ToList();

        _known = map;
        Snapshot = current;

        // Fire on every tick: usage figures change even when the drive set does not,
        // and the dashboard binds straight to this snapshot.
        DrivesChanged?.Invoke(this, new DriveChangeEventArgs(added, removed, current));
    }
}
