using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TuneUp.Controls;

/// <summary>
/// A 270-degree ring gauge drawn directly (no external chart library).
/// Value is 0-100. Text is the big centre label, SubText the small line under it.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    // Value is the target; the arc draws DisplayValue, which eases towards it so updates glide.
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, (d, e) => ((RingGauge)d).AnimateTo((double)e.NewValue)));

    private static readonly DependencyProperty DisplayValueProperty = Register("DisplayValue", typeof(double), 0.0);

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private void AnimateTo(double target)
    {
        if (double.IsNaN(target)) target = 0;
        BeginAnimation(DisplayValueProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(650)) { EasingFunction = Ease });
    }
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty SubTextProperty = Register(nameof(SubText), typeof(string), "");
    public static readonly DependencyProperty ArcBrushProperty = Register(nameof(ArcBrush), typeof(Brush), Brushes.DodgerBlue);
    public static readonly DependencyProperty TrackBrushProperty = Register(nameof(TrackBrush), typeof(Brush), new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A)));
    public static readonly DependencyProperty TextBrushProperty = Register(nameof(TextBrush), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty SubTextBrushProperty = Register(nameof(SubTextBrush), typeof(Brush), new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA3)));
    public static readonly DependencyProperty ThicknessProperty = Register(nameof(Thickness), typeof(double), 9.0);

    private static DependencyProperty Register(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(name, type, typeof(RingGauge),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush SubTextBrush { get => (Brush)GetValue(SubTextBrushProperty); set => SetValue(SubTextBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private const double StartAngle = 135;   // degrees, measured clockwise from 3 o'clock
    private const double TotalSweep = 270;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;

        var thickness = Thickness;
        var radius = Math.Min(w, h) / 2 - thickness / 2 - 1;
        var centre = new Point(w / 2, h / 2);

        // Track (full 270 degrees)
        var trackPen = new Pen(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, ArcGeometry(centre, radius, StartAngle, TotalSweep));

        // Value arc
        var shown = (double)GetValue(DisplayValueProperty);
        var fraction = Math.Clamp(double.IsNaN(shown) ? 0 : shown, 0, 100) / 100.0;
        if (fraction > 0.004)
        {
            var arcPen = new Pen(ArcBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, arcPen, ArcGeometry(centre, radius, StartAngle, TotalSweep * fraction));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        var hasSub = !string.IsNullOrEmpty(SubText);
        var mainSize = Math.Max(11, radius * 0.40);

        var main = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, mainSize, TextBrush, dpi);
        var mainY = centre.Y - main.Height / 2 - (hasSub ? radius * 0.10 : 0);
        dc.DrawText(main, new Point(centre.X - main.Width / 2, mainY));

        if (hasSub)
        {
            var sub = new FormattedText(SubText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Max(9, radius * 0.17), SubTextBrush, dpi);
            dc.DrawText(sub, new Point(centre.X - sub.Width / 2, mainY + main.Height - 1));
        }
    }

    private static StreamGeometry ArcGeometry(Point centre, double radius, double startDeg, double sweepDeg)
    {
        var start = PointOnCircle(centre, radius, startDeg);
        var end = PointOnCircle(centre, radius, startDeg + sweepDeg);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Point PointOnCircle(Point c, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
