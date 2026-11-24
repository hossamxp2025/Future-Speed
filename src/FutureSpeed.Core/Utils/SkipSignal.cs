namespace FutureSpeed.Core.Utils;

/// <summary>
/// Lets the UI abandon the file that is being copied right now without touching the
/// rest of the job. The engine checks it between buffers, deletes the partial file and
/// records the item as skipped.
/// </summary>
public sealed class SkipSignal
{
    private int _requested;

    /// <summary>Raised by the UI ("skip current file").</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);

    /// <summary>Consumed by the engine: returns true once, then resets.</summary>
    public bool Consume() => Interlocked.Exchange(ref _requested, 0) == 1;

    public bool IsRequested => Volatile.Read(ref _requested) == 1;

    public void Clear() => Interlocked.Exchange(ref _requested, 0);
}

/// <summary>Thrown internally when a file is abandoned on the user's request.</summary>
public sealed class FileSkippedException : Exception
{
    public FileSkippedException(string path) : base($"File skipped by the user: {path}")
        => Path = path;

    public string Path { get; }
}
