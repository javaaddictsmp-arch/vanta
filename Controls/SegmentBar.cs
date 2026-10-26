using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TuneUp.Controls;

/// <summary>
/// Rounded "in use | cached | free" bar. Segment changes glide with easing instead of jumping,
/// and IsBusy runs a soft shimmer across the filled part (used while the RAM cleaner works).
/// Drawn directly, so it stays cheap even while animating.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty FirstProperty = Target(nameof(First));
    public static readonly DependencyProperty SecondProperty = Target(nameof(Second));
    public static readonly DependencyProperty RestProperty = Target(nameof(Rest));

    public static readonly DependencyProperty FirstBrushProperty = Visual(nameof(FirstBrush), Brushes.MediumPurple);
    public static readonly DependencyProperty SecondBrushProperty = Visual(nameof(SecondBrush), Brushes.SlateBlue);
    public static readonly DependencyProperty TrackBrushProperty = Visual(nameof(TrackBrush), new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x29)));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(SegmentBar), new PropertyMetadata(false, (d, _) => ((SegmentBar)d).UpdateShimmer()));

    // Animated, normalised (0..1) widths actually drawn.
    private static readonly DependencyProperty ShownFirstProperty = Animated("ShownFirst");
    private static readonly DependencyProperty ShownSecondProperty = Animated("ShownSecond");
    private static readonly DependencyProperty ShimmerProperty = Animated("Shimmer");

    private static DependencyProperty Target(string name) =>
        DependencyProperty.Register(name, typeof(double), typeof(SegmentBar), new PropertyMetadata(0.0, (d, _) => ((SegmentBar)d).AnimateTo()));

    private static DependencyProperty Visual(string name, Brush fallback) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty Animated(string name) =>
        DependencyProperty.Register(name, typeof(double), typeof(SegmentBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Amount for the first (left) segment, e.g. GB in use.</summary>
    public double First { get => (double)GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    /// <summary>Amount for the second segment, e.g. GB cached.</summary>
    public double Second { get => (double)GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    /// <summary>Amount left empty, e.g. GB free.</summary>
    public double Rest { get => (double)GetValue(RestProperty); set => SetValue(RestProperty, value); }

    public Brush FirstBrush { get => (Brush)GetValue(FirstBrushProperty); set => SetValue(FirstBrushProperty, value); }
    public Brush SecondBrush { get => (Brush)GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private void AnimateTo()
    {
        var total = First + Second + Rest;
        if (total <= 0) return;
        var duration = TimeSpan.FromMilliseconds(750);
        BeginAnimation(ShownFirstProperty, new DoubleAnimation(First / total, duration) { EasingFunction = Ease });
        BeginAnimation(ShownSecondProperty, new DoubleAnimation(Second / total, duration) { EasingFunction = Ease });
    }

    private void UpdateShimmer()
    {
        if (IsBusy)
            BeginAnimation(ShimmerProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            BeginAnimation(ShimmerProperty, null);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 2) return;
        var r = h / 2;
        var bounds = new Rect(0, 0, w, h);

        dc.DrawRoundedRectangle(TrackBrush, null, bounds, r, r);
        dc.PushClip(new RectangleGeometry(bounds, r, r));

        var f1 = Math.Clamp((double)GetValue(ShownFirstProperty), 0, 1);
        var f2 = Math.Clamp((double)GetValue(ShownSecondProperty), 0, 1 - f1);
        var w1 = w * f1;
        var w2 = w * f2;

        if (w2 > 0) dc.DrawRectangle(SecondBrush, null, new Rect(Math.Max(0, w1 - r), 0, w2 + r, h));
        if (w1 > 0) dc.DrawRoundedRectangle(FirstBrush, null, new Rect(0, 0, w1, h), r, r);

        // Soft gloss on the top half for a bit of depth.
        var gloss = new LinearGradientBrush(Color.FromArgb(40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        dc.DrawRectangle(gloss, null, new Rect(0, 0, w1 + w2, h / 2));

        if (IsBusy)
        {
            var filled = w1 + w2;
            var band = Math.Max(60, filled * 0.25);
            var x = (double)GetValue(ShimmerProperty) * (filled + band) - band;
            var shimmer = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                }
            };
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, filled, h)));
            dc.DrawRectangle(shimmer, null, new Rect(x, 0, band, h));
            dc.Pop();
        }

        dc.Pop();
    }
}
