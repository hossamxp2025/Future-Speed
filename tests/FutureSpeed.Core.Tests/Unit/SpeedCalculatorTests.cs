using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class SpeedCalculatorTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void Speed_is_zero_before_any_sample()
    {
        var calculator = new SpeedCalculator(timeProvider: new FakeTimeProvider());
        calculator.Start();

        Assert.Equal(0, calculator.CurrentBytesPerSecond);
        Assert.Equal(0, calculator.AverageBytesPerSecond);
    }

    [Fact]
    public void A_steady_transfer_reports_that_steady_rate()
    {
        var time = new FakeTimeProvider();
        var calculator = new SpeedCalculator(timeProvider: time);
        calculator.Start();

        long transferred = 0;
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            transferred += 100 * Mb;
            calculator.Report(transferred);
        }

        Assert.InRange(calculator.CurrentBytesPerSecond, 99 * Mb, 101 * Mb);
        Assert.InRange(calculator.AverageBytesPerSecond, 99 * Mb, 101 * Mb);
    }

    [Fact]
    public void Samples_closer_than_the_minimum_interval_are_not_used_as_a_rate()
    {
        var time = new FakeTimeProvider();
        var calculator = new SpeedCalculator(timeProvider: time);
        calculator.Start();

        // 10 ms apart: using these as a rate would produce an absurd number.
        for (var i = 1; i <= 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            calculator.Report(i * 1024L);
        }

        Assert.Equal(0, calculator.CurrentBytesPerSecond);
    }

    [Fact]
    public void A_spike_moves_the_reported_speed_only_partway()
    {
        var time = new FakeTimeProvider();
        var calculator = new SpeedCalculator(smoothing: 0.25, timeProvider: time);
        calculator.Start();

        long transferred = 0;
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            transferred += 100 * Mb;
            calculator.Report(transferred);
        }

        var before = calculator.CurrentBytesPerSecond;

        // One second at 10x the rate.
        time.Advance(TimeSpan.FromSeconds(1));
        transferred += 1000 * Mb;
        calculator.Report(transferred);

        var after = calculator.CurrentBytesPerSecond;

        Assert.True(after > before, "the smoothed speed should react to a faster second");
        Assert.True(after < 400 * Mb, "a single spike must not be shown at full value");
    }

    [Fact]
    public void Eta_is_null_until_there_is_a_usable_rate()
    {
        var calculator = new SpeedCalculator(timeProvider: new FakeTimeProvider());
        calculator.Start();

        Assert.Null(calculator.EstimateRemaining(1000 * Mb));
    }

    [Fact]
    public void Eta_is_zero_when_nothing_is_left()
    {
        var calculator = new SpeedCalculator(timeProvider: new FakeTimeProvider());
        calculator.Start();

        Assert.Equal(TimeSpan.Zero, calculator.EstimateRemaining(0));
    }

    [Fact]
    public void Eta_matches_the_steady_rate()
    {
        var time = new FakeTimeProvider();
        var calculator = new SpeedCalculator(timeProvider: time);
        calculator.Start();

        long transferred = 0;
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            transferred += 100 * Mb;
            calculator.Report(transferred);
        }

        // 1000 MB left at ~100 MB/s is about 10 seconds.
        var eta = calculator.EstimateRemaining(1000 * Mb);

        Assert.NotNull(eta);
        Assert.InRange(eta!.Value.TotalSeconds, 9, 11);
    }

    [Fact]
    public void Eta_is_capped_for_a_near_stalled_transfer()
    {
        var time = new FakeTimeProvider();
        var calculator = new SpeedCalculator(timeProvider: time);
        calculator.Start();

        time.Advance(TimeSpan.FromSeconds(1));
        calculator.Report(10); // 10 bytes per second

        var eta = calculator.EstimateRemaining(long.MaxValue / 2);

        Assert.NotNull(eta);
        Assert.True(eta!.Value <= TimeSpan.FromHours(99));
    }

    [Fact]
    public void Elapsed_stops_advancing_after_Stop()
    {
        var time = new FakeTimeProvider();
        var calculator = new SpeedCalculator(timeProvider: time);
        calculator.Start();

        time.Advance(TimeSpan.FromSeconds(5));
        calculator.Stop();
        var atStop = calculator.Elapsed;

        time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(atStop, calculator.Elapsed);
        Assert.Equal(5, atStop.TotalSeconds, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void An_invalid_smoothing_factor_is_rejected(double smoothing)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedCalculator(smoothing));
}
