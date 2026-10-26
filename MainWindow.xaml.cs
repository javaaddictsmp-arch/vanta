using System.Windows;
using System.Windows.Interop;
using TuneUp.Services;
using TuneUp.ViewModels;
using TuneUp.Views;

namespace TuneUp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly TrayController _tray;

    /// <param name="startInTray">Started by the "start with Windows" task: load everything, then hide to the tray.</param>
    public MainWindow(bool startInTray = false)
    {
        InitializeComponent();
        DataContext = _vm;
        _tray = new TrayController(this);
        App.Tray = _tray;

        SourceInitialized += (_, _) =>
        {
            UseDarkTitleBar();
            _tray.RegisterHotkey();
        };
        Loaded += async (_, _) =>
        {
            if (startInTray) Hide();
            else _vm.MaybeShowWizard();
            try
            {
                await _vm.StartAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("Startup failed", ex);
            }
        };
        Closed += (_, _) => _tray.Dispose();
    }

    /// <summary>Makes the Windows title bar dark to match the theme (Windows 10 2004+ / 11).</summary>
    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
