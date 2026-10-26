using System.Windows;
using System.Windows.Media;
using TuneUp.Services;

namespace TuneUp;

public partial class App : Application
{
    /// <summary>Accent colour shipped with Phase 1; switched to the Vanta brand colour on first run.</summary>
    private const string OldDefaultAccent = "#4F9DFF";
    public const string DefaultAccent = "#7C5CFF";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "Vanta.exe --watchdog PID": windowless helper that restores a boost if the main app dies.
        if (e.Args.Length == 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out var parentPid))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await BoostEngine.RunWatchdogAsync(parentPid); }
            catch (Exception ex) { AppLog.Error("Watchdog failed", ex); }
            Shutdown();
            return;
        }

        // "Vanta.exe --maintenance": run by the weekly scheduled task, cleans silently and exits.
        if (e.Args.Length == 1 && e.Args[0] == "--maintenance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var settings = new SettingsService();
                settings.Load();
                await PrivacyToolsService.RunMaintenanceAsync(settings);
            }
            catch (Exception ex) { AppLog.Error("Maintenance run failed", ex); }
            Shutdown();
            return;
        }

        // "Vanta.exe --reset": run by the uninstaller when you choose to undo Vanta's changes.
        if (e.Args.Length == 1 && e.Args[0] == "--reset")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                AppServices.Init();
                await SystemIntegrationService.ResetEverythingAsync(null);
            }
            catch (Exception ex) { AppLog.Error("Reset from uninstaller failed", ex); }
            Shutdown();
            return;
        }

        // One Vanta at a time: two copies would fight over the boost, its save file and the ETW sessions.
        // A second launch just brings the running window to the front.
        if (!ClaimSingleInstance())
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                "Vanta hit an unexpected problem but is still running.\n\n" + args.Exception.Message +
                "\n\nDetails were saved to the log in %LocalAppData%\\Vanta\\Logs.",
                "Vanta", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppServices.Init();
        if (string.Equals(AppServices.Settings.Current.AccentColor, OldDefaultAccent, StringComparison.OrdinalIgnoreCase))
        {
            AppServices.Settings.Current.AccentColor = DefaultAccent;
            AppServices.Settings.Save();
        }
        ApplyAccent(AppServices.Settings.Current.AccentColor);

        new MainWindow(startInTray: e.Args.Contains("--tray")).Show();
    }

    private static Mutex? _instanceMutex;

    /// <summary>
    /// True if this is the only Vanta for this data folder (so an isolated VANTA_DATA_DIR test copy
    /// doesn't collide with the real one). Otherwise signals the running copy to show itself.
    /// </summary>
    private static bool ClaimSingleInstance()
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..16];
        var showName = $@"Local\Vanta.Show.{id}";
        try
        {
            _instanceMutex = new Mutex(true, $@"Local\Vanta.Instance.{id}", out var createdNew);
            if (!createdNew)
            {
                try { if (EventWaitHandle.TryOpenExisting(showName, out var show)) using (show) show.Set(); } catch { }
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false; // another copy (running with different rights) owns it
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showName);
        new Thread(() =>
        {
            while (showEvent.WaitOne())
                Current?.Dispatcher.BeginInvoke(() => Tray?.ShowWindow());
        }) { IsBackground = true, Name = "Vanta single instance" }.Start();
        return true;
    }

    /// <summary>The tray icon controller (for balloon notifications from anywhere).</summary>
    public static TuneUp.Views.TrayController? Tray { get; set; }

    protected override void OnExit(ExitEventArgs e)
    {
        if (AppServices.Settings != null) AppServices.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Swaps the accent everywhere (all accent uses are DynamicResource): a solid brush, a soft
    /// tint for selected backgrounds, and a two-colour gradient whose second stop is the accent
    /// shifted around the colour wheel (violet -> cyan for the default).
    /// </summary>
    public static void ApplyAccent(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var end = ShiftHue(color, -65);

            Current.Resources["AccentColor"] = color;
            Current.Resources["AccentBrush"] = Frozen(new SolidColorBrush(Lighten(color, 0.12)));
            Current.Resources["AccentSoftBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)));
            Current.Resources["AccentMidBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B)));
            Current.Resources["AccentGradientBrush"] = Frozen(new LinearGradientBrush(color, end, new Point(0, 0), new Point(1, 1)));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Invalid accent colour '{hex}': {ex.Message}");
        }
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private static Color ShiftHue(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0, l = (max + min) / 2, s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        if (d != 0)
            h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        h = ((h + degrees) % 360 + 360) % 360;

        double cc = (1 - Math.Abs(2 * l - 1)) * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - cc / 2;
        var (r1, g1, b1) = h switch
        {
            < 60 => (cc, x, 0.0), < 120 => (x, cc, 0.0), < 180 => (0.0, cc, x),
            < 240 => (0.0, x, cc), < 300 => (x, 0.0, cc), _ => (cc, 0.0, x)
        };
        return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }
}
