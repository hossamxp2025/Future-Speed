using System.Globalization;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class ByteFormatterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1073741824, "1.00 GB")]
    public void Sizes_use_binary_units(long bytes, string expected)
        => Assert.Equal(expected, ByteFormatter.FormatSize(bytes, Invariant));

    [Fact]
    public void A_negative_size_keeps_its_sign()
        => Assert.StartsWith("-", ByteFormatter.FormatSize(-2048, Invariant));

    [Theory]
    [InlineData(0, "0 MB/s")]
    [InlineData(1048576, "1.00 MB/s")]
    [InlineData(1073741824, "1.00 GB/s")]
    public void Speeds_are_formatted_per_second(double bytesPerSecond, string expected)
        => Assert.Equal(expected, ByteFormatter.FormatSpeed(bytesPerSecond, Invariant));

    [Fact]
    public void An_invalid_speed_does_not_crash_the_display()
    {
        Assert.Equal("0 MB/s", ByteFormatter.FormatSpeed(double.NaN, Invariant));
        Assert.Equal("0 MB/s", ByteFormatter.FormatSpeed(double.PositiveInfinity, Invariant));
    }

    [Fact]
    public void Durations_switch_to_hours_when_needed()
    {
        Assert.Equal("--:--", ByteFormatter.FormatDuration(null, Invariant));
        Assert.Equal("0:45", ByteFormatter.FormatDuration(TimeSpan.FromSeconds(45), Invariant));
        Assert.Equal("3:05", ByteFormatter.FormatDuration(TimeSpan.FromSeconds(185), Invariant));
        Assert.Equal("1:00:00", ByteFormatter.FormatDuration(TimeSpan.FromHours(1), Invariant));
    }
}

public sealed class PauseTokenTests
{
    [Fact]
    public async Task A_default_token_never_blocks()
    {
        await PauseToken.None.WaitWhilePausedAsync();
        Assert.False(PauseToken.None.IsPaused);
    }

    [Fact]
    public async Task Pause_blocks_until_resume()
    {
        var source = new PauseTokenSource();
        source.Pause();

        var waiter = source.Token.WaitWhilePausedAsync();
        Assert.False(waiter.IsCompleted);
        Assert.True(source.IsPaused);

        source.Resume();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(source.IsPaused);
    }

    [Fact]
    public async Task Cancelling_releases_a_paused_worker()
    {
        var source = new PauseTokenSource();
        source.Pause();

        using var cts = new CancellationTokenSource();
        var waiter = source.Token.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public async Task Resuming_twice_is_harmless()
    {
        var source = new PauseTokenSource();
        source.Pause();
        source.Resume();
        source.Resume();

        await source.Token.WaitWhilePausedAsync();
    }
}

public sealed class RateLimiterTests
{
    [Fact]
    public async Task No_limit_means_no_waiting()
    {
        var limiter = new RateLimiter(0);
        Assert.False(limiter.IsEnabled);

        var start = DateTime.UtcNow;
        for (var i = 0; i < 1000; i++)
            await limiter.WaitAsync(1024 * 1024);

        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_negative_limit_is_treated_as_unlimited()
        => Assert.False(new RateLimiter(-5).IsEnabled);

    [Fact]
    public void The_limit_can_be_changed_at_run_time()
    {
        var limiter = new RateLimiter(0);
        limiter.BytesPerSecond = 2 * 1024 * 1024;

        Assert.True(limiter.IsEnabled);
        Assert.Equal(2 * 1024 * 1024, limiter.BytesPerSecond);

        limiter.BytesPerSecond = 0;
        Assert.False(limiter.IsEnabled);
    }

    [Fact]
    public async Task A_limit_actually_slows_the_transfer_down()
    {
        // 1 MB/s budget, 3 MB requested: the first burst is free, the rest must wait.
        var limiter = new RateLimiter(1024 * 1024);
        var start = DateTime.UtcNow;

        for (var i = 0; i < 3; i++)
            await limiter.WaitAsync(1024 * 1024);

        var elapsed = DateTime.UtcNow - start;
        Assert.True(elapsed > TimeSpan.FromMilliseconds(900), $"expected throttling, took {elapsed}");
    }
}
