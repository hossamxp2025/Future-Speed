using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FutureSpeed.App.Localization;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.App.Converters;

/// <summary>long / double bytes -> "1.44 GB".</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long l => ByteFormatter.FormatSize(l, culture),
            int i => ByteFormatter.FormatSize(i, culture),
            double d => ByteFormatter.FormatSize((long)d, culture),
            _ => "0 B"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bytes/second -> "122 MB/s".</summary>
public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? ByteFormatter.FormatSpeed(d, culture) : ByteFormatter.FormatSpeed(0, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>TimeSpan? -> "3:14" / "1:02:33" / "--:--".</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            TimeSpan span => ByteFormatter.FormatDuration(span, culture),
            null => ByteFormatter.FormatDuration(null, culture),
            _ => "--:--"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double percent -> "42%".</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? string.Format(culture, "{0:0}%", Math.Clamp(d, 0, 100)) : "0%";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Enum -> localised label, using the converter parameter as the key prefix:
/// <c>Converter={StaticResource EnumLabel}, ConverterParameter=State_</c>.
/// </summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var prefix = parameter as string ?? string.Empty;
        return LocalizationService.Instance[prefix + value];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>Visible when the value is a non-empty string / non-zero count / true.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            bool b => b,
            int i => i > 0,
            ICollection c => c.Count > 0,
            _ => true
        };

        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            has = !has;
        if (Invert)
            has = !has;

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Multiplies a double (used for the UI scale slider label and layout widths).</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var factor = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : 1d;
        return value is double d ? d * factor : value ?? 0d;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Equality test for radio-button style enum bindings.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null &&
           string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null && targetType.IsEnum)
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);

        return Binding.DoNothing;
    }
}
