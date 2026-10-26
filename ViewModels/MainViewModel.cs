using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Dashboard = new DashboardViewModel();
        Tweaks = new TweaksViewModel();
        Monitoring = new MonitoringViewModel();
        ChangeLog = new ChangeLogViewModel();
        Settings = new SettingsViewModel();
        BoostCenter = new BoostCenterViewModel();
        SystemScan = new SystemScanViewModel();
        Cleaning = new CleaningViewModel();
        Startup = new StartupViewModel();
        Network = new NetworkViewModel();
        PrivacyTools = new PrivacyToolsViewModel();

        // Glyphs are from the built-in "Segoe MDL2 Assets" icon font.
        NavItems = new List<NavItem>
        {
            new() { Title = "Dashboard",        Glyph = "", ViewModel = Dashboard },
            new() { Title = "Tweaks",           Glyph = "", ViewModel = Tweaks, AdvancedOnly = true },
            new() { Title = "Monitoring",       Glyph = "", ViewModel = Monitoring, AdvancedOnly = true },
            new() { Title = "Change Log",       Glyph = "", ViewModel = ChangeLog },

            new() { Title = "FPS Boost Center", Glyph = "", ViewModel = BoostCenter },
            new() { Title = "System Scan",      Glyph = "", ViewModel = SystemScan },
            new() { Title = "Cleaning & RAM",   Glyph = "", ViewModel = Cleaning },
            new() { Title = "Startup & Services", Glyph = "", ViewModel = Startup },
            new() { Title = "Network & Ping",   Glyph = "", ViewModel = Network },
            new() { Title = "Privacy & Tools",  Glyph = "", ViewModel = PrivacyTools, AdvancedOnly = true },
            new() { Title = "Settings",         Glyph = "", ViewModel = Settings },
        };

        RefreshNav();
        SelectedNav = NavItems[0];

        Wizard = new SetupWizardViewModel(Dashboard);
        Wizard.Closed += () => Booster_SyncFromSettings();
        Settings.RunSetup = Wizard.Open;
        Settings.CheckUpdates = () => CheckForUpdateAsync(manual: true);
        UiMode.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UiMode.IsSimple)) OnModeChanged(); };

        void Navigate(string title, int tab)
        {
            var target = NavItems.FirstOrDefault(n => n.Title == title);
            if (target is { AdvancedOnly: true } && UiMode.Instance.IsSimple) UiMode.Instance.IsSimple = false; // a link to an expert page switches to Full
            SelectedNav = target ?? SelectedNav;
            if (title == "FPS Boost Center") BoostCenter.SelectedTab = tab;
            if (title == "System Scan") SystemScan.SelectedTab = tab;
        }
        Dashboard.Navigate = Navigate;
        SystemScan.Navigate = Navigate;
        SystemScan.Optimize = () => Dashboard.QuickOptimizeCommand.ExecuteAsync(null);
    }

    public DashboardViewModel Dashboard { get; }
    public TweaksViewModel Tweaks { get; }
    public MonitoringViewModel Monitoring { get; }
    public ChangeLogViewModel ChangeLog { get; }
    public SettingsViewModel Settings { get; }
    public BoostCenterViewModel BoostCenter { get; }
    public SystemScanViewModel SystemScan { get; }
    public CleaningViewModel Cleaning { get; }
    public StartupViewModel Startup { get; }
    public NetworkViewModel Network { get; }
    public PrivacyToolsViewModel PrivacyTools { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The sidebar: every page in Full mode, the everyday ones in Simple mode.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NavItem> VisibleNavItems { get; } = new();

    public SetupWizardViewModel Wizard { get; }

    private void RefreshNav()
    {
        VisibleNavItems.Clear();
        foreach (var n in NavItems.Where(n => !n.AdvancedOnly || UiMode.Instance.IsAdvanced)) VisibleNavItems.Add(n);
    }

    private void OnModeChanged()
    {
        var current = SelectedNav;
        RefreshNav();
        SelectedNav = current != null && VisibleNavItems.Contains(current) ? current : VisibleNavItems[0];
        if (UiMode.Instance.IsSimple)
        {
            // Don't leave a now-hidden tab selected.
            if (BoostCenter.SelectedTab is 3 or 4 or 7) BoostCenter.SelectedTab = 0;
            if (Startup.SelectedTab is 1 or 2) Startup.SelectedTab = 0;
            if (Network.SelectedTab is 3 or 4) Network.SelectedTab = 0;
        }
    }

    private void Booster_SyncFromSettings()
    {
        BoostCenter.Booster.BoosterEnabled = AppServices.Settings.Current.BoosterEnabled;
        BoostCenter.Sessions.Enabled = AppServices.Settings.Current.SessionReports;
    }

    /// <summary>Called by the window once it's visible: shows setup on the very first start.</summary>
    public void MaybeShowWizard()
    {
        if (!AppServices.Settings.Current.FirstRunDone) Wizard.Open();
    }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private object? _currentViewModel;

    partial void OnSelectedNavChanged(NavItem? value) => CurrentViewModel = value?.ViewModel;

    public static bool IsAdmin
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsAdminMode { get; } = IsAdmin;
    public string StatusPill => IsAdminMode ? "Full access (admin)" : "Limited mode";
    public string VersionText => $"Vanta {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}  ·  100% local";

    public string FooterText => IsAdmin
        ? "Running as administrator"
        : "Not running as administrator - some features limited";

    /// <summary>Starts sampling, scans the system and reads the current state of every tweak.</summary>
    public async Task StartAsync()
    {
        AppServices.Hub.Start();
        AppServices.Ram.Start();
        // Restores any boost left behind by a crash first, then starts game detection.
        await BoostCenter.StartAsync();
        await Dashboard.InitializeAsync();
        await Tweaks.RefreshStatesAsync();
        _ = UpdateLoopAsync();
    }

    // ---- Update notice ---------------------------------------------------------------------------
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateText = "";
    private string _updateUrl = "";

    private async Task UpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            await CheckForUpdateAsync();
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    /// <summary>Checks GitHub (if allowed) and shows the sidebar notice. Returns a status line for Settings.</summary>
    public async Task<string> CheckForUpdateAsync(bool manual = false)
    {
        if (!UpdateService.Available) return "Update checks only work in official builds from the download page.";
        if (!manual && !AppServices.Settings.Current.CheckForUpdates) return "";
        var info = await UpdateService.CheckAsync();
        if (info == null) return manual ? $"You have the latest version (Vanta {UpdateService.Current.ToString(3)})." : "";
        _updateUrl = info.PageUrl;
        UpdateText = $"Vanta {info.Version.ToString(3)} is out";
        UpdateAvailable = true;
        return $"{UpdateText}. Click the notice in the sidebar to get it.";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenUpdate()
    {
        if (_updateUrl.StartsWith("https://github.com/"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
    }
}
