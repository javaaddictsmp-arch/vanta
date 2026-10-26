using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneUp.Models;
using TuneUp.Services;
using TuneUp.Views;

namespace TuneUp.ViewModels;

public sealed record Option<T>(T Value, string Label);

/// <summary>A list of tweak cards (catalog or runtime tweaks) inside a Boost Center tab.</summary>
public partial class TweakListVm : ObservableObject
{
    public TweakListVm(IEnumerable<string> catalogIds)
    {
        foreach (var id in catalogIds)
        {
            var def = AppServices.Catalog.Tweaks.FirstOrDefault(t => t.Id == id);
            if (def != null && TweakEngine.IsApplicable(def)) Items.Add(new TweakItemVm(def));
        }
        AppServices.ChangeLog.Changed += (_, _) => UiThread.Post(async () => await RefreshAsync());
        _ = RefreshAsync();
    }

    public TweakListVm() { }

    public ObservableCollection<TweakItemVm> Items { get; } = new();

    [ObservableProperty] private string _notice = "";

    public void Replace(IEnumerable<TweakDefinition> defs)
    {
        Items.Clear();
        foreach (var d in defs) Items.Add(new TweakItemVm(d));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        foreach (var item in Items.ToList())
            if (!item.IsBusy) item.IsApplied = await AppServices.Engine.IsAppliedAsync(item.Def);
    }

    [RelayCommand]
    private async Task ToggleAsync(TweakItemVm? item)
    {
        if (item == null) return;
        if (await TweaksViewModel.ToggleItemAsync(item) is { } notice) Notice = notice;
    }
}

/// <summary>The FPS Boost Center page: one view model per tab.</summary>
public partial class BoostCenterViewModel : ObservableObject
{
    private OverlayWindow? _overlay;

    public BoostCenterViewModel()
    {
        Booster = new BoosterTabViewModel();
        CpuPower = new CpuPowerTabViewModel();
        Timer = new TimerTabViewModel();
        Gpu = new GpuTabViewModel();
        Configs = new GameConfigTabViewModel();
        Latency = new LatencyTabViewModel();
        Benchmark = new BenchmarkTabViewModel(this);
        Sessions = new SessionsTabViewModel();

        AppServices.Boost.OverlayRequested += (_, show) => UiThread.Post(() => SetOverlay(show));
    }

    public BoosterTabViewModel Booster { get; }
    public CpuPowerTabViewModel CpuPower { get; }
    public TimerTabViewModel Timer { get; }
    public GpuTabViewModel Gpu { get; }
    public GameConfigTabViewModel Configs { get; }
    public LatencyTabViewModel Latency { get; }
    public BenchmarkTabViewModel Benchmark { get; }
    public SessionsTabViewModel Sessions { get; }

    [ObservableProperty] private bool _overlayVisible;
    [ObservableProperty] private int _selectedTab;

    public void SetOverlay(bool show)
    {
        if (show && _overlay == null)
        {
            _overlay = new OverlayWindow(() => AppServices.Boost.GamePids.Cast<int?>().FirstOrDefault());
            _overlay.Show();
        }
        else if (!show && _overlay != null)
        {
            _overlay.Close();
            _overlay = null;
        }
        OverlayVisible = _overlay != null;
    }

    public async Task StartAsync()
    {
        await Booster.StartAsync();
        await CpuPower.RefreshAsync();
        Timer.Refresh();
    }
}

// =============================================================================================
// Booster tab
// =============================================================================================
public partial class GameRowVm : ObservableObject
{
    public GameRowVm(GameEntry g) => Game = g;
    public GameEntry Game { get; }
    public string Name => Game.Name;
    public string Launcher => Game.Launcher;
    public string AntiCheat => Game.AntiCheat ?? "";
    public bool IsManual => Game.Launcher == "Manual";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBoosted;
    [ObservableProperty] private bool _hasOwnProfile;
}

public partial class BackgroundAppVm : ObservableObject
{
    private readonly Action _save;
    public BackgroundAppVm(BackgroundAppRule rule, Action save)
    {
        Rule = rule;
        _save = save;
    }

    public BackgroundAppRule Rule { get; }
    public string ProcessName => Rule.ProcessName;

    public BackgroundAction Action
    {
        get => Rule.Action;
        set { Rule.Action = value; OnPropertyChanged(); _save(); }
    }
}

/// <summary>Two-way wrapper around a BoostProfile; every change is saved immediately.</summary>
public partial class ProfileEditorVm : ObservableObject
{
    private readonly BoostProfile _p;

    public ProfileEditorVm(BoostProfile p)
    {
        _p = p;
        foreach (var b in p.BackgroundApps) BackgroundApps.Add(new BackgroundAppVm(b, Save));
    }

    public BoostProfile Profile => _p;
    public bool IsDefault => _p.GameId == ProfileService.DefaultId;
    public string Title => IsDefault ? "Default profile (used by every game without its own)" : $"Profile for {_p.GameName}";

    private void Save() => AppServices.Profiles.Save();

    private void Set<T>(T current, T value, Action<T> setter, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        setter(value);
        OnPropertyChanged(name);
        Save();
    }

    public bool AutoActivate { get => _p.AutoActivate; set => Set(_p.AutoActivate, value, v => _p.AutoActivate = v); }
    public bool HighPriority { get => _p.HighPriority; set => Set(_p.HighPriority, value, v => _p.HighPriority = v); }
    public bool DisablePriorityBoost { get => _p.DisablePriorityBoost; set => Set(_p.DisablePriorityBoost, value, v => _p.DisablePriorityBoost = v); }
    public bool ProBalance { get => _p.ProBalance; set => Set(_p.ProBalance, value, v => _p.ProBalance = v); }
    public bool UsePerformancePlan { get => _p.UsePerformancePlan; set => Set(_p.UsePerformancePlan, value, v => _p.UsePerformancePlan = v); }
    public bool TrimStandbyIfLow { get => _p.TrimStandbyIfLow; set => Set(_p.TrimStandbyIfLow, value, v => _p.TrimStandbyIfLow = v); }
    public bool ClearClipboard { get => _p.ClearClipboard; set => Set(_p.ClearClipboard, value, v => _p.ClearClipboard = v); }
    public bool BlockWindowsKey { get => _p.BlockWindowsKey; set => Set(_p.BlockWindowsKey, value, v => _p.BlockWindowsKey = v); }
    public bool BlockAccessibilityShortcuts { get => _p.BlockAccessibilityShortcuts; set => Set(_p.BlockAccessibilityShortcuts, value, v => _p.BlockAccessibilityShortcuts = v); }
    public bool MuteNotifications { get => _p.MuteNotifications; set => Set(_p.MuteNotifications, value, v => _p.MuteNotifications = v); }
    public bool PauseSearchIndexing { get => _p.PauseSearchIndexing; set => Set(_p.PauseSearchIndexing, value, v => _p.PauseSearchIndexing = v); }
    public bool PauseWindowsUpdate { get => _p.PauseWindowsUpdate; set => Set(_p.PauseWindowsUpdate, value, v => _p.PauseWindowsUpdate = v); }
    public bool ShowOverlay { get => _p.ShowOverlay; set => Set(_p.ShowOverlay, value, v => _p.ShowOverlay = v); }
    public string Notes { get => _p.Notes; set => Set(_p.Notes, value, v => _p.Notes = v); }

    public AffinityPreset Affinity
    {
        get => _p.Affinity;
        set
        {
            if (value != AffinityPreset.None && value != _p.Affinity)
            {
                var topo = CpuTopologyService.Get();
                if (topo.MaskFor(value) == null)
                {
                    MessageBox.Show(topo.WhyNot(value), "Not available on this CPU", MessageBoxButton.OK, MessageBoxImage.Information);
                    OnPropertyChanged();
                    return;
                }
            }
            Set(_p.Affinity, value, v => _p.Affinity = v);
        }
    }

    public int TimerResolution100ns { get => _p.TimerResolution100ns; set => Set(_p.TimerResolution100ns, value, v => _p.TimerResolution100ns = v); }

    public static IReadOnlyList<Option<AffinityPreset>> AffinityOptions { get; } = new[]
    {
        new Option<AffinityPreset>(AffinityPreset.None, "Let Windows decide (recommended)"),
        new Option<AffinityPreset>(AffinityPreset.PerformanceCoresOnly, "Intel hybrid: P-cores only"),
        new Option<AffinityPreset>(AffinityPreset.VCacheCcdOnly, "AMD X3D: V-Cache CCD only"),
        new Option<AffinityPreset>(AffinityPreset.FrequencyCcdOnly, "AMD multi-CCD/CCX: one cache group only"),
        new Option<AffinityPreset>(AffinityPreset.OneThreadPerCore, "One thread per core (SMT/HT off for this game)")
    };

    public static IReadOnlyList<Option<int>> TimerOptions { get; } = new[]
    {
        new Option<int>(0, "Leave alone (recommended)"),
        new Option<int>(5000, "0.5 ms"),
        new Option<int>(10000, "1.0 ms")
    };

    public static IReadOnlyList<Option<BackgroundAction>> ActionOptions { get; } = new[]
    {
        new Option<BackgroundAction>(BackgroundAction.Suspend, "Suspend (freeze, resumes after)"),
        new Option<BackgroundAction>(BackgroundAction.Close, "Close (reopens after)")
    };

    public ObservableCollection<BackgroundAppVm> BackgroundApps { get; } = new();

    [ObservableProperty] private string _newBackgroundApp = "";

    [RelayCommand]
    private void AddBackgroundApp()
    {
        var name = Path.GetFileNameWithoutExtension(NewBackgroundApp.Trim());
        if (string.IsNullOrWhiteSpace(name)) return;
        if (ProcessUtil.IsProtected(name))
        {
            MessageBox.Show($"{name} is a Windows or anti-cheat process. Vanta never suspends or closes it.",
                "Vanta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_p.BackgroundApps.Any(b => b.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        if (name.Equals("steam", StringComparison.OrdinalIgnoreCase) || name.Contains("EpicGames", StringComparison.OrdinalIgnoreCase))
            MessageBox.Show("Suspending a game launcher can break the game you launched from it (achievements, overlay, online). " +
                            "Use the launcher's own 'pause downloads while playing' setting instead.",
                "Careful", MessageBoxButton.OK, MessageBoxImage.Warning);

        var rule = new BackgroundAppRule { ProcessName = name };
        _p.BackgroundApps.Add(rule);
        BackgroundApps.Add(new BackgroundAppVm(rule, Save));
        NewBackgroundApp = "";
        Save();
    }

    [RelayCommand]
    private void RemoveBackgroundApp(BackgroundAppVm? vm)
    {
        if (vm == null) return;
        _p.BackgroundApps.Remove(vm.Rule);
        BackgroundApps.Remove(vm);
        Save();
    }
}

public partial class BoosterTabViewModel : ObservableObject
{
    public BoosterTabViewModel()
    {
        AppServices.Games.Changed += (_, _) => UiThread.Post(ReloadGames);
        AppServices.Watcher.RunningChanged += (_, _) => UiThread.Post(UpdateRunning);
        AppServices.Boost.StateChanged += (_, _) => UiThread.Post(UpdateStatus);
        AppServices.Boost.ActivityLogged += line => UiThread.Post(() =>
        {
            Activity.Insert(0, line);
            while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
        });

        _boosterEnabled = AppServices.Settings.Current.BoosterEnabled;
        Editor = new ProfileEditorVm(AppServices.Profiles.Default);
        ReloadGames();
        UpdateStatus();
    }

    public ObservableCollection<GameRowVm> Games { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();
    public ObservableCollection<string> Steps { get; } = new();
    public ObservableCollection<string> RunningProcessNames { get; } = new();

    [ObservableProperty] private GameRowVm? _selectedGame;
    [ObservableProperty] private ProfileEditorVm _editor;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private bool _isBoostActive;
    [ObservableProperty] private string _boostTitle = "No boost running";
    [ObservableProperty] private string _boostDetail = "";

    [ObservableProperty] private bool _boosterEnabled;

    public string CpuSummary => CpuTopologyService.Get().Describe();

    partial void OnBoosterEnabledChanged(bool value)
    {
        AppServices.Settings.Current.BoosterEnabled = value;
        AppServices.Settings.Save();
    }

    partial void OnSelectedGameChanged(GameRowVm? value)
    {
        Editor = value == null
            ? new ProfileEditorVm(AppServices.Profiles.Default)
            : new ProfileEditorVm(AppServices.Profiles.For(value.Game.Id));
        OnPropertyChanged(nameof(UsingDefault));
    }

    /// <summary>True when the selected game has no profile of its own yet.</summary>
    public bool UsingDefault => SelectedGame != null && !AppServices.Profiles.HasOwn(SelectedGame.Game.Id);

    public async Task StartAsync()
    {
        var restored = await BoostRestorer.RestoreStaleAsync();
        if (restored != null) StatusMessage = restored;

        await RescanAsync();
        AppServices.Watcher.Start();
    }

    private void ReloadGames()
    {
        var selectedId = SelectedGame?.Game.Id;
        Games.Clear();
        foreach (var g in AppServices.Games.Games) Games.Add(new GameRowVm(g) { HasOwnProfile = AppServices.Profiles.HasOwn(g.Id) });
        SelectedGame = Games.FirstOrDefault(g => g.Game.Id == selectedId);
        UpdateRunning();
    }

    private void UpdateRunning()
    {
        var running = AppServices.Watcher.Running.Select(r => r.Game.Id).ToHashSet();
        var boosted = AppServices.Boost.ActiveGame?.Id;
        foreach (var row in Games)
        {
            row.IsRunning = running.Contains(row.Game.Id);
            row.IsBoosted = row.Game.Id == boosted;
        }
    }

    private void UpdateStatus()
    {
        var b = AppServices.Boost;
        IsBoostActive = b.IsActive;
        Steps.Clear();
        if (b.IsActive && b.ActiveGame != null)
        {
            BoostTitle = $"Boosting {b.ActiveGame.Name}";
            var started = b.StartedUtc?.ToLocalTime().ToString("t") ?? "";
            BoostDetail = $"Started {started}. Everything below is undone automatically when the game closes, crashes, or Vanta exits.";
            foreach (var s in b.StepsApplied) Steps.Add(s);
        }
        else
        {
            BoostTitle = BoosterEnabled ? "Waiting for a game to start" : "Auto-boost is off";
            BoostDetail = BoosterEnabled
                ? "Start any detected game and the boost turns on by itself."
                : "Turn on auto-boost, or select a running game and click Boost now.";
        }
        UpdateRunning();
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsScanning = true;
        StatusMessage = "Looking for installed games...";
        try
        {
            await AppServices.Games.ScanAsync();
            StatusMessage = $"Found {AppServices.Games.Games.Count} games.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Game scan failed", ex);
            StatusMessage = "Game scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void AddGame()
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Pick the game's EXE" };
        if (dlg.ShowDialog() != true) return;
        var g = AppServices.Games.AddManual(dlg.FileName);
        StatusMessage = $"Added {g.Name}.";
    }

    [RelayCommand]
    private void RemoveGame(GameRowVm? row)
    {
        if (row is not { IsManual: true }) return;
        AppServices.Games.Remove(row.Game);
    }

    [RelayCommand]
    private async Task BoostNowAsync()
    {
        if (SelectedGame == null)
        {
            StatusMessage = "Select a game first.";
            return;
        }
        var running = AppServices.Watcher.Running.FirstOrDefault(r => r.Game.Id == SelectedGame.Game.Id);
        if (running == null)
        {
            StatusMessage = $"{SelectedGame.Name} is not running. Start it and the boost will turn on automatically (or click Boost now again).";
            return;
        }
        await AppServices.Boost.ActivateAsync(running.Game, running.Pids);
    }

    [RelayCommand]
    private async Task StopBoostAsync() => await AppServices.Boost.DeactivateAsync("stopped by you");

    [RelayCommand]
    private void CustomizeProfile()
    {
        if (SelectedGame == null) return;
        var p = AppServices.Profiles.GetOrCreate(SelectedGame.Game);
        AppServices.Profiles.Save();
        Editor = new ProfileEditorVm(p);
        SelectedGame.HasOwnProfile = true;
        OnPropertyChanged(nameof(UsingDefault));
    }

    [RelayCommand]
    private void ResetProfile()
    {
        if (SelectedGame == null) return;
        AppServices.Profiles.ResetToDefault(SelectedGame.Game.Id);
        Editor = new ProfileEditorVm(AppServices.Profiles.Default);
        SelectedGame.HasOwnProfile = false;
        OnPropertyChanged(nameof(UsingDefault));
    }

    [RelayCommand]
    private void EditDefault()
    {
        SelectedGame = null;
        Editor = new ProfileEditorVm(AppServices.Profiles.Default);
    }

    [RelayCommand]
    private void ExportProfile()
    {
        var p = Editor.Profile;
        var dlg = new SaveFileDialog
        {
            Filter = "Vanta profile (*.json)|*.json",
            FileName = $"vanta-profile-{Safe(p.GameName)}.json"
        };
        if (dlg.ShowDialog() != true) return;
        AppServices.Profiles.Export(dlg.FileName, new[] { p }, $"Boost profile for {p.GameName}");
        StatusMessage = "Profile exported. Share the file; others can import it.";
    }

    [RelayCommand]
    private void ExportAll()
    {
        var dlg = new SaveFileDialog { Filter = "Vanta profiles (*.json)|*.json", FileName = "vanta-profiles.json" };
        if (dlg.ShowDialog() != true) return;
        var all = AppServices.Games.Games.Where(g => AppServices.Profiles.HasOwn(g.Id))
            .Select(g => AppServices.Profiles.For(g.Id))
            .Prepend(AppServices.Profiles.Default);
        AppServices.Profiles.Export(dlg.FileName, all, "Vanta profile library");
        StatusMessage = "All profiles exported.";
    }

    [RelayCommand]
    private void ImportProfiles()
    {
        var dlg = new OpenFileDialog { Filter = "Vanta profiles (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = AppServices.Profiles.Import(dlg.FileName, AppServices.Games.Games, SelectedGame?.Game);
            StatusMessage = n == 0
                ? "No profiles matched your installed games. Select a game first to import a single profile onto it."
                : $"Imported {n} profile(s).";
            ReloadGames();
            OnSelectedGameChanged(SelectedGame);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RefreshProcessList()
    {
        RunningProcessNames.Clear();
        foreach (var name in Process.GetProcesses()
                     .Select(p => { using (p) return ProcessUtil.SafeName(p); })
                     .Where(n => !ProcessUtil.IsProtected(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            RunningProcessNames.Add(name);
    }

    private static string Safe(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
}
