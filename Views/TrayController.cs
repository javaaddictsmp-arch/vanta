using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using TuneUp.Services;

namespace TuneUp.Views;

/// <summary>
/// Tray icon with quick actions and the global "Boost now" hotkey (Ctrl+Alt+B).
/// Closing the window still exits Vanta; minimizing hides it to the tray when that setting is on.
/// </summary>
public sealed class TrayController : IDisposable
{
    private const int HotkeyId = 0x5654; // "VT"
    private readonly Window _window;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _autoBoost;
    private HwndSource? _source;

    public TrayController(Window window)
    {
        _window = window;

        _autoBoost = new MenuItem { Header = "Auto-boost games", IsCheckable = true, IsChecked = AppServices.Settings.Current.BoosterEnabled };
        _autoBoost.Click += (_, _) =>
        {
            AppServices.Settings.Current.BoosterEnabled = _autoBoost.IsChecked;
            AppServices.Settings.Save();
        };

        var boostNow = new MenuItem { Header = "Boost now / stop boost" };
        boostNow.Click += async (_, _) => await ToggleBoostAsync();

        var plans = new MenuItem { Header = "Power plan" };
        foreach (var (name, guid) in new[] { ("Balanced", PowerPlanService.BalancedGuid), ("High performance", PowerPlanService.HighPerformanceGuid), ("Vanta Performance", PowerPlanService.TuneUpPlanGuid) })
        {
            var item = new MenuItem { Header = name };
            item.Click += async (_, _) =>
            {
                if (guid == PowerPlanService.TuneUpPlanGuid) await AppServices.Power.EnsureTuneUpPlanAsync(AppServices.Settings.Current.PlanSettings);
                var ok = await AppServices.Power.SetActiveAsync(guid);
                Notify(ok ? $"Power plan: {name}" : $"Could not switch to {name}");
            };
            plans.Items.Add(item);
        }

        var open = new MenuItem { Header = "Open Vanta", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowWindow();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(_autoBoost);
        menu.Items.Add(boostNow);
        menu.Items.Add(plans);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Opened += (_, _) => _autoBoost.IsChecked = AppServices.Settings.Current.BoosterEnabled;

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/vanta.ico")),
            ToolTipText = "Vanta",
            ContextMenu = menu
        };
        _icon.TrayMouseDoubleClick += (_, _) => ShowWindow();

        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized && AppServices.Settings.Current.MinimizeToTray) _window.Hide();
        };

        AppServices.Sessions.Saved += s => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var parts = new List<string> { $"{s.DurationMin:0} min" };
            if (s.AvgFps != null) parts.Add($"{s.AvgFps:0} FPS avg, {s.Low1Fps:0} 1% low");
            if (s.PingAvg != null) parts.Add($"{s.PingAvg:0} ms ping");
            Notify($"{s.Game} session report ready: {string.Join(", ", parts)}. Open FPS Boost Center > Session reports.");
        });

        AppServices.Boost.StateChanged +=(_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
            _icon.ToolTipText = AppServices.Boost.IsActive && AppServices.Boost.ActiveGame != null
                ? $"Vanta - boosting {AppServices.Boost.ActiveGame.Name}" : "Vanta");
    }

    /// <summary>Text shown in Settings about the hotkey ("ready" or why not).</summary>
    public string HotkeyStatus { get; private set; } = "";

    /// <summary>(Re)registers the Boost hotkey from settings. Call after the window has a handle. Returns success.</summary>
    public bool RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.UnregisterHotKey(hwnd, HotkeyId);
        if (_source == null)
        {
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
        }

        var s = AppServices.Settings.Current;
        if (!s.HotkeyEnabled) { HotkeyStatus = "Hotkey is off."; return false; }
        if (!HotkeyParser.TryParse(s.Hotkey, out var mods, out var vk)) { HotkeyStatus = $"'{s.Hotkey}' isn't a valid hotkey."; return false; }

        if (!NativeMethods.RegisterHotKey(hwnd, HotkeyId, mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            HotkeyStatus = $"{s.Hotkey} is already used by another app. Pick a different combination.";
            AppLog.Warn(HotkeyStatus);
            return false;
        }
        HotkeyStatus = $"{s.Hotkey} is ready: press it in a game to boost, press again to stop.";
        return true;
    }

    public void UnregisterHotkey()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        NativeMethods.UnregisterHotKey(hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _ = ToggleBoostAsync();
        }
        return IntPtr.Zero;
    }

    private async Task ToggleBoostAsync()
    {
        if (AppServices.Boost.IsActive)
        {
            await AppServices.Boost.DeactivateAsync("stopped with hotkey");
            Notify("Boost stopped. Everything was put back.");
            return;
        }
        var running = AppServices.Watcher.Running.FirstOrDefault();
        if (running == null)
        {
            Notify("No detected game is running. Start a game first (or add it in FPS Boost Center).");
            return;
        }
        await AppServices.Boost.ActivateAsync(running.Game, running.Pids);
        Notify($"Boosting {running.Game.Name}.");
    }

    public void Notify(string text) => _icon.ShowBalloonTip("Vanta", text, BalloonIcon.None);

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        UnregisterHotkey();
        _icon.Dispose();
    }
}
