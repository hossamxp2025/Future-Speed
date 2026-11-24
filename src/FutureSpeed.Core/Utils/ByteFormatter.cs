using System.Globalization;

namespace FutureSpeed.Core.Utils;

public static class ByteFormatter
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formats a size using binary units, e.g. 1536 -> "1.50 KB".</summary>
    public static string FormatSize(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) return "-" + FormatSize(-bytes, culture);
        if (bytes < 1024) return string.Format(culture, "{0} {1}", bytes, SizeUnits[0]);

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var decimals = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return string.Format(culture, "{0} {1}", Math.Round(value, decimals).ToString("N" + decimals, culture), SizeUnits[unit]);
    }

    /// <summary>Formats throughput, e.g. 1_500_000 -> "1.43 MB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            return string.Format(culture, "0 {0}", "MB/s");

        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        double value = bytesPerSecond;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var decimals = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return string.Format(culture, "{0} {1}", Math.Round(value, decimals).ToString("N" + decimals, culture), units[unit]);
    }

    /// <summary>Formats a duration as h:mm:ss (or m:ss below one hour).</summary>
    public static string FormatDuration(TimeSpan? span, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (span is null) return "--:--";
        var t = span.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? string.Format(culture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format(culture, "{0}:{1:00}", t.Minutes, t.Seconds);
    }
}
