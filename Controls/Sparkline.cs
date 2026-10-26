using System.Windows;
using System.Windows.Media;

namespace TuneUp.Controls;

/// <summary>
/// A lightweight line chart for rolling history. Replace the Values array to redraw.
/// Max = 0 means auto-scale to the data; otherwise the fixed top of the chart (e.g. 100 for %).
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10) return;

        // Faint horizontal guide lines
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (var i = 1; i <= 3; i++)
        {
            var y = h * i / 4.0;
            dc.DrawLine(guide, new Point(0, y), new Point(w, y));
        }

        var values = Values;
        if (values == null || values.Count < 2) return;

        var max = Max > 0 ? Max : Math.Max(1, values.Max() * 1.15);
        var stepX = w / Math.Max(1, values.Count - 1);

        Point ToPoint(int i)
        {
            var v = Math.Clamp(values[i], 0, max);
            return new Point(i * stepX, h - 2 - v / max * (h - 4));
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(ToPoint(0), false, false);
            for (var i = 1; i < values.Count; i++) ctx.LineTo(ToPoint(i), true, true);
        }
        line.Freeze();

        // Soft fill under the line
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (var i = 0; i < values.Count; i++) ctx.LineTo(ToPoint(i), true, false);
            ctx.LineTo(new Point((values.Count - 1) * stepX, h), true, false);
        }
        fill.Freeze();

        var fillBrush = (Brush)Stroke.CloneCurrentValue();
        fillBrush.Opacity = 0.15;
        dc.DrawGeometry(fillBrush, null, fill);

        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round }, line);
    }
}
