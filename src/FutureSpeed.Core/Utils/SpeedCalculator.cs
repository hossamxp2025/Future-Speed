namespace FutureSpeed.Core.Utils;

/// <summary>
/// Smoothed throughput and ETA. Raw per-second deltas jump around far too much to
/// display, so samples are fed through an exponential moving average and the ETA is
/// derived from that smoothed rate blended with the long-run average.
/// Time comes from a <see cref="TimeProvider"/> so this is deterministic in tests.
/// </summary>
public sealed class SpeedCalculator
{
    private readonly double _smoothing;
    private readonly TimeProvider _time;

    private long _startTimestamp;
    private bool _running;
    private TimeSpan _stoppedElapsed;

    private long _totalBytes;
    private long _lastBytes;
    private TimeSpan _lastSample;
    private double _smoothedBytesPerSecond;
    private bool _hasSample;

    /// <param name="smoothing">
    /// EMA weight for the newest sample (0 &lt; w &lt;= 1). 0.25 keeps the number readable
    /// while still reacting to a real change within a couple of seconds.
    /// </param>
    /// <param name="timeProvider">Clock source; defaults to the system clock.</param>
    public SpeedCalculator(double smoothing = 0.25, TimeProvider? timeProvider = null)
    {
        if (smoothing is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(smoothing));
        _smoothing = smoothing;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Minimum gap between accepted samples; shorter deltas are ignored (bytes still accumulate).</summary>
    public TimeSpan MinimumSampleInterval { get; init; } = TimeSpan.FromMilliseconds(400);

    public TimeSpan Elapsed => _running
        ? _time.GetElapsedTime(_startTimestamp)
        : _stoppedElapsed;

    public double CurrentBytesPerSecond => _smoothedBytesPerSecond;

    public double AverageBytesPerSecond
    {
        get
        {
            var seconds = Elapsed.TotalSeconds;
            return seconds > 0.001 ? _totalBytes / seconds : 0;
        }
    }

    public void Start()
    {
        _startTimestamp = _time.GetTimestamp();
        _running = true;
        _stoppedElapsed = TimeSpan.Zero;
        _lastSample = TimeSpan.Zero;
        _lastBytes = 0;
        _totalBytes = 0;
        _smoothedBytesPerSecond = 0;
        _hasSample = false;
    }

    public void Stop()
    {
        if (!_running) return;
        _stoppedElapsed = _time.GetElapsedTime(_startTimestamp);
        _running = false;
    }

    /// <summary>Call whenever the cumulative transferred byte count changes.</summary>
    public void Report(long cumulativeBytes)
    {
        _totalBytes = cumulativeBytes;
        var now = Elapsed;
        var delta = now - _lastSample;
        if (delta < MinimumSampleInterval) return;

        var bytes = cumulativeBytes - _lastBytes;
        if (bytes < 0) bytes = 0;

        var instant = bytes / delta.TotalSeconds;
        _smoothedBytesPerSecond = _hasSample
            ? (_smoothing * instant) + ((1 - _smoothing) * _smoothedBytesPerSecond)
            : instant;

        _hasSample = true;
        _lastBytes = cumulativeBytes;
        _lastSample = now;
    }

    /// <summary>
    /// Remaining time from the smoothed rate. Returns null until there is a usable
    /// rate, so the UI shows "--:--" instead of a nonsense number.
    /// </summary>
    public TimeSpan? EstimateRemaining(long remainingBytes)
    {
        if (remainingBytes <= 0) return TimeSpan.Zero;

        // Blend the smoothed rate with the long-run average: the average alone is too
        // slow to react, the smoothed rate alone is too eager right after a stall.
        var average = AverageBytesPerSecond;
        var rate = _smoothedBytesPerSecond > 0 && average > 0
            ? (_smoothedBytesPerSecond * 0.7) + (average * 0.3)
            : Math.Max(_smoothedBytesPerSecond, average);

        if (rate <= 1) return null;

        var seconds = remainingBytes / rate;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;

        // Cap at 99 hours so a near-stalled transfer does not print an absurd value.
        return TimeSpan.FromSeconds(Math.Min(seconds, 99 * 3600));
    }
}
