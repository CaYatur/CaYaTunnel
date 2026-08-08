using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CaYaTunnel.Ui;

/// <summary>Collapses the element when the bound value is false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Collapses the element when the bound string is null or blank.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Collapses the element when the bound collection is empty — used for empty states.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value as int? ?? 0;
        var visible = count > 0;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Shows a page only when it is the selected one.</summary>
public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Binds a navigation button's checked state to the selected page, in both directions.
/// <para>
/// Without this the sidebar tracks clicks rather than state, so anything that changes the page
/// in code — a failed gateway start sending the operator to Settings, say — leaves the highlight
/// pointing at the wrong entry.
/// </para>
/// </summary>
public sealed class PageSelectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter?.ToString() ?? Binding.DoNothing : Binding.DoNothing;
}

/// <summary>Byte counts as something a person can read at a glance.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Format(System.Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture));

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{size.ToString(size >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>"3 minutes ago" style timestamps.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset moment ? Format(moment) : "—";

    public static string Format(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.UtcNow - moment.ToUniversalTime();

        return elapsed switch
        {
            { TotalSeconds: < 10 } => "just now",
            { TotalMinutes: < 1 } => $"{elapsed.Seconds}s ago",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}m ago",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours}h ago",
            { TotalDays: < 30 } => $"{(int)elapsed.TotalDays}d ago",
            _ => moment.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
