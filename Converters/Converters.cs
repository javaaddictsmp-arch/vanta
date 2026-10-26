using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TuneUp.Models;

namespace TuneUp.Converters;

internal static class Res
{
    public static Brush Find(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

/// <summary>0 = normal (accent gradient), 1 = warning, 2 = bad.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return level switch
        {
            2 => Res.Find("BadBrush", Brushes.IndianRed),
            1 => Res.Find("WarnBrush", Brushes.Orange),
            _ => Res.Find("AccentGradientBrush", Res.Find("AccentBrush", Brushes.DodgerBlue))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Beginner verdict level (see TweakItemVm.VerdictLevel) to a colour.</summary>
public sealed class VerdictToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int i ? i : 0) switch
        {
            0 => Res.Find("GoodBrush", Brushes.SeaGreen),
            1 => Res.Find("AccentBrush", Brushes.DodgerBlue),
            2 => Res.Find("WarnBrush", Brushes.Orange),
            3 => Res.Find("MutedBrush", Brushes.Gray),
            _ => Res.Find("BadBrush", Brushes.IndianRed)
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RiskLevel r
            ? r switch
            {
                RiskLevel.Safe => Res.Find("GoodBrush", Brushes.SeaGreen),
                RiskLevel.Moderate => Res.Find("WarnBrush", Brushes.Orange),
                _ => Res.Find("BadBrush", Brushes.IndianRed)
            }
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EvidenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is EvidenceRating e
            ? e switch
            {
                EvidenceRating.Proven => Res.Find("GoodBrush", Brushes.SeaGreen),
                EvidenceRating.Situational => Res.Find("AccentBrush", Brushes.DodgerBlue),
                _ => Res.Find("MutedBrush", Brushes.Gray)
            }
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Severity s
            ? s switch
            {
                Severity.Good => Res.Find("GoodBrush", Brushes.SeaGreen),
                Severity.Warning => Res.Find("WarnBrush", Brushes.Orange),
                Severity.Problem => Res.Find("BadBrush", Brushes.IndianRed),
                _ => Res.Find("MutedBrush", Brushes.Gray)
            }
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the string is non-empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bool is false.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Recommendation impact: High = red, Medium = amber, Low = muted.</summary>
public sealed class ImpactToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Impact i
            ? i switch
            {
                Impact.High => Res.Find("BadBrush", Brushes.IndianRed),
                Impact.Medium => Res.Find("WarnBrush", Brushes.Orange),
                _ => Res.Find("MutedBrush", Brushes.Gray)
            }
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bytes (long) to "1.2 GB" style text.</summary>
public sealed class SizeToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long b ? TuneUp.Services.CleanerService.FormatSize(b) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true -> false and back (for IsEnabled="{Binding IsBusy}" style bindings).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible when the value is not null.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#4F9DFF"));
        }
        catch
        {
            return Brushes.DodgerBlue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
