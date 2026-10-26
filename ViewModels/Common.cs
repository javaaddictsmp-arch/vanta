using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneUp.ViewModels;

/// <summary>Posts work to the UI thread without blocking the sampling thread.</summary>
public static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}

/// <summary>One ring gauge on the dashboard. Level: 0 normal, 1 warning, 2 bad.</summary>
public partial class GaugeVm : ObservableObject
{
    public GaugeVm(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty] private double _value;
    [ObservableProperty] private string _text = "N/A";
    [ObservableProperty] private string _subText = "";
    [ObservableProperty] private int _level;

    public void SetUnavailable(string sub = "no sensor")
    {
        Value = 0;
        Text = "N/A";
        SubText = sub;
        Level = 0;
    }
}

/// <summary>One sparkline card on the Monitoring page.</summary>
public partial class MetricCardVm : ObservableObject
{
    private readonly List<double> _points = new();
    private const int MaxPoints = 120;

    public MetricCardVm(string title, double max)
    {
        Title = title;
        Max = max;
    }

    public string Title { get; }
    public double Max { get; }

    [ObservableProperty] private string _valueText = "N/A";
    [ObservableProperty] private IReadOnlyList<double> _values = Array.Empty<double>();
    [ObservableProperty] private int _level;

    public void Push(double? value, string text, int level = 0)
    {
        ValueText = text;
        Level = level;
        if (value.HasValue)
        {
            _points.Add(value.Value);
            if (_points.Count > MaxPoints) _points.RemoveAt(0);
            Values = _points.ToArray(); // new array each time so the Sparkline redraws
        }
    }
}

public sealed record SummaryRow(string Label, string Value, string Note);

public partial class CoreVm : ObservableObject
{
    public CoreVm(string name) => Name = name;
    public string Name { get; }
    [ObservableProperty] private double _load;
}

/// <summary>
/// Simple vs Full mode. Simple hides the expert pages and tabs (nothing is removed or turned off,
/// it is only hidden). XAML binds to <c>{Binding IsAdvanced, Source={x:Static vm:UiMode.Instance}}</c>.
/// </summary>
public partial class UiMode : ObservableObject
{
    public static UiMode Instance { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvanced))]
    private bool _isSimple = TuneUp.Services.AppServices.Settings?.Current.SimpleMode ?? false;

    public bool IsAdvanced => !IsSimple;

    partial void OnIsSimpleChanged(bool value)
    {
        TuneUp.Services.AppServices.Settings.Current.SimpleMode = value;
        TuneUp.Services.AppServices.Settings.Save();
    }
}

public sealed class NavItem
{
    /// <summary>Hidden in Simple mode.</summary>
    public bool AdvancedOnly { get; init; }
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string PhaseTag { get; init; } = "";
    public object ViewModel { get; init; } = null!;
}

public sealed class PlaceholderViewModel
{
    public PlaceholderViewModel(string title, string phase, string description, params string[] planned)
    {
        Title = title;
        Phase = phase;
        Description = description;
        Planned = planned;
    }

    public string Title { get; }
    public string Phase { get; }
    public string Description { get; }
    public IReadOnlyList<string> Planned { get; }
}
