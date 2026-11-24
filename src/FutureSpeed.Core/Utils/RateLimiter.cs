namespace FutureSpeed.Core.Utils;

/// <summary>
/// Token-bucket throughput cap shared by all copy workers. Workers ask for the number
/// of bytes they are about to write and are delayed until the budget allows it.
/// A zero or negative limit disables throttling entirely (no locking cost on the hot path).
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private long _bytesPerSecond;
    private double _available;
    private long _lastTimestamp;

    public RateLimiter(long bytesPerSecond = 0, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _available = _bytesPerSecond;
        _lastTimestamp = _time.GetTimestamp();
    }

    public bool IsEnabled => Volatile.Read(ref _bytesPerSecond) > 0;

    public long BytesPerSecond
    {
        get => Volatile.Read(ref _bytesPerSecond);
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                _available = Math.Min(_available, _bytesPerSecond);
                _lastTimestamp = _time.GetTimestamp();
            }
        }
    }

    /// <summary>Blocks (asynchronously) until <paramref name="bytes"/> may be transferred.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0 || !IsEnabled) return;

        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                var limit = _bytesPerSecond;
                if (limit <= 0) return;

                var now = _time.GetTimestamp();
                var elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
                _lastTimestamp = now;

                // Refill, capped at one second worth of burst.
                _available = Math.Min(limit, _available + (elapsed * limit));

                if (_available >= bytes)
                {
                    _available -= bytes;
                    return;
                }

                var deficit = bytes - _available;
                delay = TimeSpan.FromSeconds(deficit / limit);
            }

            if (delay < TimeSpan.FromMilliseconds(1))
                delay = TimeSpan.FromMilliseconds(1);
            if (delay > TimeSpan.FromSeconds(1))
                delay = TimeSpan.FromSeconds(1);

            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
