using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

internal static class Links
{
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"Could not open {url}: {ex.Message}"); }
    }
}

// =============================================================================================
// CPU & Power
// =============================================================================================
public partial class ProcessRuleVm : ObservableObject
{
    private readonly Action _save;
    public ProcessRuleVm(ProcessRule rule, Action save)
    {
        Rule = rule;
        _save = save;
    }

    public ProcessRule Rule { get; }
    public string ProcessName => Rule.ProcessName;
    public string Summary =>
        string.Join(", ", new[]
        {
            Rule.Priority == null ? null : $"priority {Rule.Priority}",
            Rule.Affinity == AffinityPreset.None ? null : ProfileEditorVm.AffinityOptions.First(o => o.Value == Rule.Affinity).Label
        }.Where(x => x != null));

    public bool Enabled
    {
        get => Rule.Enabled;
        set { Rule.Enabled = value; OnPropertyChanged(); _save(); }
    }
}

public partial class CpuPowerTabViewModel : ObservableObject
{
    public CpuPowerTabViewModel()
    {
        Tweaks = new TweakListVm(new[] { "power-throttling-off", "win32-priority-separation", "mmcss-games", "system-responsiveness-0" });
        foreach (var r in AppServices.Rules.Rules) Rules.Add(new ProcessRuleVm(r, SaveRules));
        var s = AppServices.Settings.Current;
        _exclusions = string.Join(", ", s.ProBalanceExclusions);
        _threshold = s.ProBalanceThresholdPercent;
        _idleSaver = s.IdleSaverBalanced;
    }

    public TweakListVm Tweaks { get; }
    public PerformancePlanSettings Plan => AppServices.Settings.Current.PlanSettings;
    public string Topology => CpuTopologyService.Get().Describe();
    public string TopologyAdvice
    {
        get
        {
            var t = CpuTopologyService.Get();
            if (t.IsDualCcdVCache)
                return "Dual-CCD X3D CPU: AMD's chipset driver + Xbox Game Bar already steer games to the V-Cache CCD. " +
                       "Only pin a game to the V-Cache CCD (in its boost profile) if you measure stutter from it landing on the other CCD.";
            if (t.IsHybrid)
                return "Intel hybrid CPU: Thread Director normally keeps games on P-cores. Pinning to P-cores can help a few older games " +
                       "that scatter threads onto E-cores; measure with the Benchmark tab before keeping it.";
            if (t.L3Groups.Count > 1)
                return "Your cores are identical but split into separate L3 cache groups (CCDs/CCXs). Pinning a game to one group can reduce " +
                       "cross-cache latency in a few titles, but it also halves the cores the game can use. Leave it to Windows unless an A/B benchmark shows a gain.";
            return "Your CPU has uniform cores, so affinity presets other than 'one thread per core' do nothing. Windows' scheduler is the right choice here.";
        }
    }

    public ObservableCollection<PowerPlan> Plans { get; } = new();
    public ObservableCollection<ProcessRuleVm> Rules { get; } = new();

    [ObservableProperty] private string _activePlanName = "";
    [ObservableProperty] private bool _tuneUpPlanExists;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _exclusions;
    [ObservableProperty] private double _threshold;
    [ObservableProperty] private bool _idleSaver;

    [ObservableProperty] private string _newRuleName = "";
    [ObservableProperty] private string? _newRulePriority = "High";
    [ObservableProperty] private AffinityPreset _newRuleAffinity = AffinityPreset.None;

    public static IReadOnlyList<Option<string?>> PriorityOptions { get; } = new[]
    {
        new Option<string?>(null, "Leave alone"),
        new Option<string?>("BelowNormal", "Below normal"),
        new Option<string?>("Normal", "Normal"),
        new Option<string?>("AboveNormal", "Above normal"),
        new Option<string?>("High", "High")
    };

    public static IReadOnlyList<Option<int>> BoostModeOptions { get; } = new[]
    {
        new Option<int>(0, "Disabled"), new Option<int>(1, "Enabled"), new Option<int>(2, "Aggressive"),
        new Option<int>(3, "Efficient enabled"), new Option<int>(4, "Efficient aggressive")
    };

    partial void OnExclusionsChanged(string value)
    {
        AppServices.Settings.Current.ProBalanceExclusions = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFileNameWithoutExtension).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        AppServices.Settings.Save();
    }

    partial void OnThresholdChanged(double value)
    {
        AppServices.Settings.Current.ProBalanceThresholdPercent = Math.Clamp(value, 2, 50);
        AppServices.Settings.Save();
    }

    partial void OnIdleSaverChanged(bool value)
    {
        AppServices.Settings.Current.IdleSaverBalanced = value;
        AppServices.Settings.Save();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var plans = await AppServices.Power.ListAsync();
            Plans.Clear();
            foreach (var p in plans) Plans.Add(p);
            ActivePlanName = plans.FirstOrDefault(p => p.Active)?.Name ?? "Unknown";
            TuneUpPlanExists = plans.Any(p => p.Guid == PowerPlanService.TuneUpPlanGuid);
        }
        catch (Exception ex)
        {
            AppLog.Error("Power plan refresh failed", ex);
        }
    }

    [RelayCommand]
    private async Task ApplyPlanAsync()
    {
        if (Plan.DisableIdleStates &&
            MessageBox.Show("Disabling CPU idle states keeps every core awake: more heat, fan noise and power draw, " +
                            "and on many CPUs lower boost clocks because of the extra heat. It rarely improves FPS. Continue?",
                "Advanced setting", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Plan.DisableIdleStates = false;
            OnPropertyChanged(nameof(Plan));
            return;
        }

        IsBusy = true;
        try
        {
            AppServices.Settings.Save();
            var ok = await AppServices.Power.EnsureTuneUpPlanAsync(Plan);
            Status = ok
                ? "Vanta Performance plan saved. Boosts switch to it while a game runs and switch back afterwards."
                : "Could not create the plan (see log).";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemovePlanAsync()
    {
        IsBusy = true;
        try
        {
            Status = await AppServices.Power.RemoveTuneUpPlanAsync() ? "Vanta plan removed." : "Could not remove the plan.";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddUltimateAsync()
    {
        IsBusy = true;
        try
        {
            var guid = await AppServices.Power.AddUltimatePerformanceAsync();
            Status = guid != null
                ? "Ultimate Performance is now in your plan list. It mainly keeps the CPU from downclocking; on desktops the difference to High Performance is tiny."
                : "Windows did not allow adding Ultimate Performance on this edition.";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetActivePlanAsync(PowerPlan? plan)
    {
        if (plan == null) return;
        await AppServices.Power.SetActiveAsync(plan.Guid);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UnhideParkingAsync()
    {
        await AppServices.Power.UnhideCoreParkingAsync();
        Status = "Core parking settings are now visible in Control Panel > Power Options > Advanced settings.";
    }

    [RelayCommand]
    private void AddRule()
    {
        var name = Path.GetFileNameWithoutExtension(NewRuleName.Trim());
        if (string.IsNullOrEmpty(name)) return;
        if (ProcessUtil.IsProtected(name))
        {
            Status = $"{name} is a protected Windows or anti-cheat process; Vanta won't make rules for it.";
            return;
        }
        if (NewRulePriority == null && NewRuleAffinity == AffinityPreset.None)
        {
            Status = "Pick a priority or an affinity preset for the rule.";
            return;
        }

        var rule = new ProcessRule { ProcessName = name, Priority = NewRulePriority, Affinity = NewRuleAffinity };
        AppServices.Rules.Rules.RemoveAll(r => r.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        AppServices.Rules.Rules.Add(rule);
        foreach (var old in Rules.Where(r => r.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()) Rules.Remove(old);
        Rules.Add(new ProcessRuleVm(rule, SaveRules));
        NewRuleName = "";
        SaveRules();
        Status = $"Rule saved for {name}. It applies whenever {name} starts while Vanta is running.";
    }

    [RelayCommand]
    private void RemoveRule(ProcessRuleVm? vm)
    {
        if (vm == null) return;
        AppServices.Rules.Rules.Remove(vm.Rule);
        Rules.Remove(vm);
        SaveRules();
    }

    private void SaveRules()
    {
        AppServices.Rules.Save();
        Task.Run(AppServices.Watcher.ReapplyRules);
    }
}

// =============================================================================================
// Timer resolution
// =============================================================================================
public partial class TimerTabViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public TimerTabViewModel()
    {
        Tweaks = new TweakListVm(new[] { "global-timer-resolution" });
    }

    public TweakListVm Tweaks { get; }

    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _behavior = "";
    [ObservableProperty] private string _benchmarkResult = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _requestersText = "";

    public ObservableCollection<TimerRequest> Requesters { get; } = new();

    public void Refresh()
    {
        var t = AppServices.Timer.Query();
        Current = $"Current {t.CurrentMs:0.###} ms   (finest possible {t.FinestMs:0.###} ms, default {t.CoarsestMs:0.###} ms)";

        if (!TimerResolutionService.IsPerProcessBehavior)
            Behavior = "This Windows version still uses a global timer: a per-game timer setting affects the whole system while the game runs.";
        else if (TimerResolutionService.IsWindows11 && TimerResolutionService.GlobalRequestsEnabled)
            Behavior = "Global timer requests are enabled (Windows 11 registry setting). A per-game timer in a boost profile affects the whole system.";
        else if (TimerResolutionService.IsWindows11)
            Behavior = "Windows 11 keeps each program's timer request to itself. Without the setting below, Vanta's per-game timer cannot change the game's timer. Most games already request 1 ms on their own.";
        else
            Behavior = "Windows 10 2004+ keeps each program's timer request to itself, and there is no supported switch to undo that on Windows 10. Vanta's per-game timer therefore cannot change your game's timer here; most games already request 1 ms themselves.";
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        BenchmarkResult = "Measuring Sleep(1) accuracy (about 2 seconds)...";
        try
        {
            var (before, after) = await AppServices.Timer.BenchmarkAsync(5000, _cts.Token);
            var diff = before.AvgMs - after.AvgMs;
            var perProcess = TimerResolutionService.IsPerProcessBehavior && !TimerResolutionService.GlobalRequestsEnabled;
            BenchmarkResult =
                $"Without asking for a finer timer:  Sleep(1) took {before.AvgMs:0.00} ms on average, 99% under {before.P99Ms:0.00} ms\n" +
                $"Asking for 0.5 ms:                 Sleep(1) took {after.AvgMs:0.00} ms on average, 99% under {after.P99Ms:0.00} ms\n\n" +
                (perProcess && before.AvgMs > before.ResolutionMs * 3
                    ? $"Windows reports a {before.ResolutionMs:0.###} ms system timer (some other program asked for it), yet Vanta's own Sleep(1) took " +
                      $"{before.AvgMs:0.0} ms until it asked for itself. That is the per-process behaviour in action: a program only gets a fine timer if it " +
                      "requests one. Games that need it already do, so the per-game timer setting will not change your games unless you enable the global setting below.\n\n"
                    : "") +
                (diff > 0.3
                    ? "A finer timer makes short waits more precise. That only matters for games/engines that sleep between frames (some frame limiters). Confirm with an A/B benchmark in game."
                    : "Practically no difference here: the timer is already fine-grained. You can leave the per-game timer off.");
        }
        catch (OperationCanceledException)
        {
            BenchmarkResult = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task FindRequestersAsync()
    {
        IsBusy = true;
        RequestersText = "Running a 5-second 'powercfg /energy' trace...";
        Requesters.Clear();
        try
        {
            var list = await TimerResolutionService.FindRequestersAsync(CancellationToken.None);
            foreach (var r in list.OrderBy(r => r.PeriodMs)) Requesters.Add(r);
            RequestersText = list.Count == 0
                ? "No program is requesting a finer timer right now (or the report could not be read)."
                : "These programs are asking Windows for a finer timer right now:";
        }
        catch (Exception ex)
        {
            RequestersText = "Could not run powercfg: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// =============================================================================================
// GPU & Display
// =============================================================================================
public partial class GpuTabViewModel : ObservableObject
{
    private SystemSummary? _summary;
    private CancellationTokenSource? _dlssCts;

    public GpuTabViewModel()
    {
        Tweaks = new TweakListVm(new[] { "game-mode-on", "game-dvr-off", "hags-off", "mpo-disable", "fso-global-off", "windowed-game-optimizations" });
        PerGameTweaks = new TweakListVm();
        AppServices.Games.Changed += (_, _) => UiThread.Post(() => OnPropertyChanged(nameof(Games)));
    }

    public TweakListVm Tweaks { get; }
    public TweakListVm PerGameTweaks { get; }
    public IReadOnlyList<GameEntry> Games => AppServices.Games.Games;
    public ObservableCollection<string> Exes { get; } = new();
    public ObservableCollection<DlssDll> DlssDlls { get; } = new();
    public ObservableCollection<string> VendorTips { get; } = new();

    [ObservableProperty] private GameEntry? _selectedGame;
    [ObservableProperty] private string? _selectedExe;
    [ObservableProperty] private string _displayText = "Click Refresh to read your GPU and display.";
    [ObservableProperty] private string _displayWarning = "";
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _vendorTitle = "";
    [ObservableProperty] private string _nvidiaText = "";
    [ObservableProperty] private string _dlssStatus = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isNvidia;
    [ObservableProperty] private bool _isAmd;
    [ObservableProperty] private bool _isIntel;

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        Exes.Clear();
        PerGameTweaks.Replace(Array.Empty<TweakDefinition>());
        if (value == null) return;
        foreach (var e in GpuDisplayService.CandidateExes(value)) Exes.Add(e);
        SelectedExe = Exes.FirstOrDefault();
    }

    partial void OnSelectedExeChanged(string? value)
    {
        PerGameTweaks.Replace(value == null
            ? Array.Empty<TweakDefinition>()
            : new[] { GpuDisplayService.GpuPreferenceTweak(value), GpuDisplayService.FsoTweak(value) });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            _summary = await AppServices.SystemInfo.ScanAsync();
            var s = _summary;
            DisplayText = $"{s.Resolution} at {s.RefreshCurrentHz} Hz (monitor supports up to {s.RefreshMaxHz} Hz)";
            DisplayWarning = s.RefreshMaxHz > s.RefreshCurrentHz && s.RefreshCurrentHz > 0
                ? $"Your monitor is running below its maximum refresh rate. Set it to {s.RefreshMaxHz} Hz in Settings > Display > Advanced display. This is one of the most noticeable free upgrades."
                : "";
            if (s.RefreshCurrentHz > 0)
                DisplayText += $"\nWith G-Sync/FreeSync, cap FPS at about {GpuDisplayService.VrrCap(s.RefreshCurrentHz)} to stay inside the VRR range " +
                               "(refresh − refresh²/3600, the formula NVIDIA Reflex uses). Without VRR, a cap near your typical lows gives the smoothest frametimes.";

            Vendor = s.GpuVendor;
            IsNvidia = s.GpuVendor == "nvidia";
            IsAmd = s.GpuVendor == "amd";
            IsIntel = s.GpuVendor == "intel";
            VendorTitle = $"{s.GpuName}  (driver {s.GpuDriver})";
            FillVendorTips();

            if (IsNvidia)
            {
                var n = await AppServices.Gpu.ReadNvidiaAsync();
                NvidiaText = n == null
                    ? "nvidia-smi was not found, so live NVIDIA details are unavailable."
                    : $"Power: {n.PowerDrawW:0} W now, limit {n.PowerLimitW:0} W (max {n.PowerMaxW:0} W)\n" +
                      $"Core clock: {n.Clock} MHz now, max {n.MaxClock} MHz\n" +
                      $"PCIe: gen {n.PcieGen} of {n.PcieGenMax} x{n.PcieWidth} (it drops at idle to save power; check under load)\n" +
                      $"Resizable BAR: {n.ReBar}";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("GPU tab refresh failed", ex);
            DisplayText = "Could not read GPU/display info: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FillVendorTips()
    {
        VendorTips.Clear();
        string[] tips = Vendor switch
        {
            "nvidia" => new[]
            {
                "Low Latency Mode: 'On' for games without NVIDIA Reflex. If the game has Reflex, use Reflex in game instead (it overrides this).",
                "Power management mode: 'Normal' is fine on desktops; 'Prefer maximum performance' only helps if clocks drop in light games. Situational.",
                "Shader cache size: 'Driver default' or 10 GB+. Never 'Disabled' (causes stutter).",
                "Texture filtering quality 'High performance': Marginal, usually under 1%.",
                "V-Sync: Off in game. With G-Sync: V-Sync On in the NVIDIA Control Panel + an FPS cap a few frames under refresh.",
                "DLSS: use the NVIDIA App's DLSS override to use a newer model instead of swapping DLL files (swapping can trigger anti-cheat in online games).",
                "Clean driver install: NVIDIA's installer 'Perform a clean installation' is enough for most issues; DDU (in Safe Mode) only for real driver problems."
            },
            "amd" => new[]
            {
                "Radeon Anti-Lag: On for GPU-bound games without their own latency tech. Anti-Lag 2 in supported games is better.",
                "Radeon Chill and Boost: off for competitive games (Boost lowers resolution dynamically).",
                "Shader cache: AMD Optimized. Use 'Reset Shader Cache' in Adrenalin only if a game stutters after a driver update.",
                "FreeSync: enable in Adrenalin and on the monitor's OSD, then cap FPS a few frames under refresh.",
                "Enhanced Sync: off unless you play far above refresh without FreeSync."
            },
            "intel" => new[]
            {
                "Intel Arc: keep the driver current; performance in DX9/DX11 games improved a lot through driver updates.",
                "Enable Resizable BAR in BIOS: Arc GPUs lose a lot of performance without it.",
                "Use XeSS where the game offers it."
            },
            _ => new[] { "No dedicated GPU vendor detected." }
        };
        foreach (var t in tips) VendorTips.Add(t);
    }

    [RelayCommand]
    private void OpenDriverPage()
    {
        Links.Open(Vendor switch
        {
            "nvidia" => "https://www.nvidia.com/Download/index.aspx",
            "amd" => "https://www.amd.com/en/support/download/drivers.html",
            "intel" => "https://www.intel.com/content/www/us/en/download-center/home.html",
            _ => "https://www.google.com/search?q=gpu+driver+download"
        });
    }

    [RelayCommand]
    private void OpenAfterburner() => Links.Open("https://www.msi.com/Landing/afterburner/graphics-cards");

    [RelayCommand]
    private void OpenGraphicsSettings() => Links.Open("ms-settings:display-advancedgraphics");

    [RelayCommand]
    private async Task ScanDlssAsync()
    {
        _dlssCts?.Cancel();
        _dlssCts = new CancellationTokenSource();
        DlssDlls.Clear();
        DlssStatus = "Scanning game folders for DLSS files...";
        try
        {
            var list = await AppServices.Gpu.ScanDlssAsync(AppServices.Games.Games, _dlssCts.Token);
            foreach (var d in list.OrderBy(d => d.Game)) DlssDlls.Add(d);
            DlssStatus = list.Count == 0 ? "No DLSS DLLs found in your detected games." : $"{list.Count} DLSS file(s) found.";
        }
        catch (OperationCanceledException)
        {
            DlssStatus = "Cancelled.";
        }
    }
}

// =============================================================================================
// Latency
// =============================================================================================
public partial class MsiRowVm : ObservableObject
{
    public MsiRowVm(MsiDevice d)
    {
        Device = d;
        Tweak = new TweakItemVm(LatencyService.MsiTweak(d));
    }

    public MsiDevice Device { get; }
    public TweakItemVm Tweak { get; }
    public string Name => Device.Name;
    public string Class => Device.Class;
    public string Status => Device.MsiEnabled switch
    {
        true => "MSI on",
        false => "Line-based",
        null => "Not set (driver default)"
    };
}

public partial class LatencyTabViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public LatencyTabViewModel()
    {
        Tweaks = new TweakListVm(new[] { "mouse-accel-off", "sticky-keys-prompt-off" });
    }

    public TweakListVm Tweaks { get; }
    public ObservableCollection<MsiRowVm> Devices { get; } = new();
    public ObservableCollection<LatencyReport> Reports { get; } = new();

    public static IReadOnlyList<Option<int>> DurationOptions { get; } = new[]
    {
        new Option<int>(10, "10 seconds"), new Option<int>(30, "30 seconds"), new Option<int>(60, "60 seconds")
    };

    [ObservableProperty] private int _duration = 10;
    [ObservableProperty] private string _label = "Before";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _msiNotice = "";

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        Devices.Clear();
        foreach (var d in await AppServices.Latency.ListMsiDevicesAsync()) Devices.Add(new MsiRowVm(d));
        foreach (var row in Devices) row.Tweak.IsApplied = await AppServices.Engine.IsAppliedAsync(row.Tweak.Def);
    }

    [RelayCommand]
    private async Task ToggleMsiAsync(MsiRowVm? row)
    {
        if (row == null) return;
        if (await TweaksViewModel.ToggleItemAsync(row.Tweak) is { } n) MsiNotice = n;
    }

    [RelayCommand]
    private async Task SampleAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = $"Sampling DPC/ISR latency for {Duration} s. Use the PC normally (or play) meanwhile...";
        try
        {
            var r = await AppServices.Latency.SampleAsync(Duration, string.IsNullOrWhiteSpace(Label) ? "Run" : Label, _cts.Token);
            Reports.Insert(0, r);
            Status = string.IsNullOrEmpty(r.Error) ? r.Verdict : r.Error;
            if (Label == "Before") Label = "After";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
