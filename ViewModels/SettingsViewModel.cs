using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private bool _loading;

    public SettingsViewModel()
    {
        var s = AppServices.Settings.Current;
        _createRestorePoint = s.CreateRestorePoint;
        _minimizeToTray = s.MinimizeToTray;
        _hotkeyEnabled = s.HotkeyEnabled;
        _startWithWindows = s.StartWithWindows;
    }

    public IReadOnlyList<string> Accents { get; } = new[]
    {
        // Vanta violet (default), electric blue, mint, hot pink, amber, crimson
        "#7C5CFF", "#3B82F6", "#10B981", "#EC4899", "#F59E0B", "#EF4444"
    };

    public string DataFolder => AppPaths.Root;
    public string Version => $"Vanta {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";

    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _hotkeyEnabled;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public async Task ActivateAsync()
    {
        RefreshHotkeyStatus();
        CheckForUpdates = AppServices.Settings.Current.CheckForUpdates; // the setup wizard may have changed it
        _loading = true;
        StartWithWindows = await SystemIntegrationService.IsStartWithWindowsAsync();
        _loading = false;
    }

    partial void OnCreateRestorePointChanged(bool value)
    {
        AppServices.Settings.Current.CreateRestorePoint = value;
        AppServices.Settings.Save();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        AppServices.Settings.Current.MinimizeToTray = value;
        AppServices.Settings.Save();
    }

    partial void OnHotkeyEnabledChanged(bool value)
    {
        AppServices.Settings.Current.HotkeyEnabled = value;
        AppServices.Settings.Save();
        App.Tray?.RegisterHotkey();
        HotkeyStatus = App.Tray?.HotkeyStatus ?? "";
    }

    [ObservableProperty] private string _hotkeyText = AppServices.Settings.Current.Hotkey;
    [ObservableProperty] private string _hotkeyStatus = "";

    /// <summary>Called by the view when a new combination is pressed in the hotkey box.</summary>
    public void SetHotkey(string combo)
    {
        HotkeyText = combo;
        AppServices.Settings.Current.Hotkey = combo;
        AppServices.Settings.Save();
        App.Tray?.RegisterHotkey();
        HotkeyStatus = App.Tray?.HotkeyStatus ?? "";
    }

    public void RefreshHotkeyStatus() => HotkeyStatus = App.Tray?.HotkeyStatus ?? "";

    public UiMode Mode => UiMode.Instance;

    /// <summary>Set by MainViewModel: opens the first-run setup again.</summary>
    public Action? RunSetup { get; set; }

    [RelayCommand]
    private void OpenSetup() => RunSetup?.Invoke();

    // Updates
    public Func<Task<string>>? CheckUpdates { get; set; }
    public bool UpdatesAvailable => UpdateService.Available;
    [ObservableProperty] private bool _checkForUpdates = AppServices.Settings.Current.CheckForUpdates;
    [ObservableProperty] private string _updateStatus = "";

    partial void OnCheckForUpdatesChanged(bool value)
    {
        AppServices.Settings.Current.CheckForUpdates = value;
        AppServices.Settings.Save();
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        UpdateStatus = "Checking...";
        UpdateStatus = CheckUpdates == null ? "" : await CheckUpdates();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _ = Task.Run(async () =>
        {
            var ok = await SystemIntegrationService.SetStartWithWindowsAsync(value);
            UiThread.Post(() =>
            {
                AppServices.Settings.Current.StartWithWindows = value && ok;
                AppServices.Settings.Save();
                Status = ok ? value ? "Vanta will start in the tray when you log in (as admin, without a prompt each time)." : "Vanta won't start with Windows."
                            : "Could not change it (needs admin rights).";
            });
        });
    }

    [RelayCommand]
    private void SetAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        App.ApplyAccent(hex);
        AppServices.Settings.Current.AccentColor = hex;
        AppServices.Settings.Save();
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });

    [RelayCommand]
    private void OpenCatalogFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Catalog}\"") { UseShellExecute = true });

    [RelayCommand]
    private void ExportSettings()
    {
        var dlg = new SaveFileDialog { Filter = "Vanta backup (*.zip)|*.zip", FileName = $"Vanta settings {DateTime.Now:yyyy-MM-dd}.zip" };
        if (dlg.ShowDialog() != true) return;
        AppServices.Settings.Save();
        AppServices.Profiles.Save();
        SystemIntegrationService.Export(dlg.FileName);
        Status = "Settings, game profiles, rules and custom catalogs exported.";
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var dlg = new OpenFileDialog { Filter = "Vanta backup (*.zip)|*.zip" };
        if (dlg.ShowDialog() != true) return;
        if (MessageBox.Show("Replace your current Vanta settings and profiles with the ones in this backup?\n\nVanta closes afterwards; open it again to load them.",
                "Import settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var n = SystemIntegrationService.Import(dlg.FileName);
            MessageBox.Show($"Imported {n} file(s). Vanta will now close; start it again to use them.", "Import settings");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Status = "Import failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ResetEverythingAsync()
    {
        if (MessageBox.Show(
                "Put Windows back the way it was before Vanta?\n\nThis undoes every tweak, removes the Vanta power plan, restores DNS and network adapter settings, " +
                "removes Vanta's QoS policies and scheduled tasks. Your Vanta settings and game profiles are kept.\n\nSome changes need a restart to fully take effect.",
                "Reset everything", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var done = await SystemIntegrationService.ResetEverythingAsync(new Progress<string>(m => Status = m));
            Status = "Done. " + string.Join(". ", done) + ". Restart Windows to finish.";
            _loading = true;
            StartWithWindows = false;
            _loading = false;
        }
        finally { IsBusy = false; }
    }
}
