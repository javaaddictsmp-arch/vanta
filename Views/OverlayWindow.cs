using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TuneUp.Services;

namespace TuneUp.Views;

/// <summary>
/// Small always-on-top, click-through stats overlay. It is a normal desktop window (no hooking
/// into the game), so it is anti-cheat safe, but it only shows over borderless/windowed games;
/// exclusive fullscreen draws over it.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        LineHeight = 17
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Func<int?> _gamePid;

    public OverlayWindow(Func<int?> gamePid)
    {
        _gamePid = gamePid;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 12;
        Top = 12;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 10, 12, 16)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _text
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
                  NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        };

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            AppServices.Frametimes.Start();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var pid = _gamePid();
        double? fps = null;
        if (pid != null) fps = AppServices.Frametimes.LiveFps(pid.Value);
        fps ??= AppServices.Frametimes.Presenting().FirstOrDefault()?.Fps;

        var s = AppServices.Hub.Latest;
        var lines = new List<string>
        {
            fps is { } f ? $"FPS  {f,5:0}   {1000 / Math.Max(f, 1),5:0.0} ms" : "FPS    --"
        };
        if (s != null)
        {
            lines.Add($"CPU  {s.CpuLoad,4:0}%  {Temp(s.CpuTempC)}");
            if (s.GpuLoad.HasValue) lines.Add($"GPU  {s.GpuLoad,4:0}%  {Temp(s.GpuTempC)}  {s.GpuClockMhz:0} MHz");
            if (s.GpuMemUsedMb.HasValue) lines.Add($"VRAM {s.GpuMemUsedMb / 1024:0.0} / {s.GpuMemTotalMb / 1024:0.0} GB");
            lines.Add($"RAM  {s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB");
        }
        _text.Text = string.Join("\n", lines);
    }

    private static string Temp(double? c) => c.HasValue ? $"{c:0}°C" : "";
}
