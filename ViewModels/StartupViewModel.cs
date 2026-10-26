using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class StartupRowVm : ObservableObject
{
    private readonly StartupViewModel _owner;
    private bool _suppress;

    public StartupRowVm(StartupItem item, StartupViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isEnabled = item.Enabled;
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;
    public string Publisher => string.IsNullOrEmpty(Item.Publisher) ? "" : Item.Publisher;
    public string Advice => Item.Advice;
    public int AdviceLevel => Item.AdviceLevel;
    public string AdviceTag => Item.AdviceLevel switch { 0 => "Safe to disable", 2 => "Keep", _ => "Your call" };
    public string SourceText => Item.Source switch
    {
        StartupSource.Task => "Scheduled task",
        StartupSource.FolderUser or StartupSource.FolderCommon => "Startup folder",
        _ => "Registry (Run)"
    };

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusy;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress) return;
        _ = _owner.ToggleAsync(this, value);
    }

    public void SetSilently(bool value)
    {
        _suppress = true;
        IsEnabled = value;
        _suppress = false;
    }
}

public partial class ServiceRowVm : ObservableObject
{
    public ServiceRowVm(ServiceInfo info)
    {
        Info = info;
        Tweak = new TweakItemVm(ServicePresetService.Tweak(info));
        Refresh();
    }

    public ServiceInfo Info { get; }
    public TweakItemVm Tweak { get; }
    public string Display => Info.Display;
    public string What => Info.What;
    public string Warning => Info.Warning;
    public string Presets => string.Join(" · ", Info.Presets);
    public string Target => Info.Target;

    [ObservableProperty] private string _current = "";
    public bool Installed => Current != "Not installed";

    public void Refresh()
    {
        Current = ServicePresetService.CurrentStartType(Info.Name);
        Tweak.IsApplied = AppServices.Engine.IsApplied(Tweak.Def);
        OnPropertyChanged(nameof(Installed));
    }
}

public partial class StartupViewModel : ObservableObject
{
    private readonly ProcessManagerService _procs = new();
    private readonly DispatcherTimer _procTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<ProcessRow> _allProcesses = new();

    public StartupViewModel()
    {
        foreach (var s in ServicePresetService.Catalog) Services.Add(new ServiceRowVm(s));
        _procTimer.Tick += (_, _) => { if (SelectedTab == 2 && AutoRefresh) RefreshProcesses(); };
        AppServices.ChangeLog.Changed += (_, _) => UiThread.Post(() => { foreach (var s in Services) s.Refresh(); });
    }

    public ObservableCollection<StartupRowVm> StartupItems { get; } = new();
    public ObservableCollection<ServiceRowVm> Services { get; } = new();
    public ObservableCollection<ProcessRow> Processes { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _startupStatus = "";
    [ObservableProperty] private string _servicesStatus = "";
    [ObservableProperty] private string _processStatus = "";
    [ObservableProperty] private string _processFilter = "";
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private ProcessRow? _selectedProcess;

    public static IReadOnlyList<string> PriorityChoices { get; } = new[] { "Idle", "BelowNormal", "Normal", "AboveNormal", "High" };

    partial void OnProcessFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedTabChanged(int value) { if (value == 2) RefreshProcesses(); }

    public void Activate()
    {
        LoadStartup();
        foreach (var s in Services) s.Refresh();
        _procTimer.Start();
    }

    public void Deactivate() => _procTimer.Stop();

    // ---- Startup -----------------------------------------------------------------------------------
    [RelayCommand]
    private void LoadStartup()
    {
        StartupItems.Clear();
        try
        {
            foreach (var i in StartupService.List()) StartupItems.Add(new StartupRowVm(i, this));
            var on = StartupItems.Count(i => i.IsEnabled);
            var safe = StartupItems.Count(i => i.IsEnabled && i.AdviceLevel == 0);
            StartupStatus = $"{on} of {StartupItems.Count} startup items are on." + (safe > 0 ? $" {safe} of them are usually safe to turn off." : "");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup list failed", ex);
            StartupStatus = "Could not read startup items: " + ex.Message;
        }
    }

    public async Task ToggleAsync(StartupRowVm row, bool enable)
    {
        row.IsBusy = true;
        try
        {
            bool ok;
            if (row.Item.Source == StartupSource.Task)
            {
                ok = row.Item.TaskPath != null && StartupService.SetTaskEnabled(row.Item.TaskPath, enable);
            }
            else
            {
                var off = StartupService.ToggleTweak(row.Item, false)!;
                EngineResult r;
                if (enable && AppServices.ChangeLog.LatestActiveApply(off.Id) != null)
                    r = await AppServices.Engine.RevertAsync(off); // undo our own disable exactly
                else
                    r = await AppServices.Engine.ApplyAsync(StartupService.ToggleTweak(row.Item, enable)!);
                ok = r.Success;
                if (!ok) StartupStatus = r.Message;
            }

            if (ok)
            {
                row.Item.Enabled = enable;
                StartupStatus = $"{row.Name} will {(enable ? "" : "no longer ")}start with Windows (takes effect next sign-in).";
            }
            else row.SetSilently(!enable);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenTaskManagerStartup() =>
        Process.Start(new ProcessStartInfo("taskmgr.exe", "/0 /startup") { UseShellExecute = true });

    // ---- Services ------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task ToggleServiceAsync(ServiceRowVm? row)
    {
        if (row == null) return;
        if (await TweaksViewModel.ToggleItemAsync(row.Tweak) is { } n) ServicesStatus = n;
        row.Refresh();
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(string? preset)
    {
        if (preset == null) return;
        var rows = Services.Where(s => s.Installed && s.Info.Presets.Contains(preset) && !s.Tweak.IsApplied).ToList();
        if (rows.Count == 0) { ServicesStatus = $"Everything in the {preset} preset is already set."; return; }

        var warnings = rows.Where(r => !string.IsNullOrEmpty(r.Warning)).Select(r => $"• {r.Display}: {r.Warning}").ToList();
        var text = $"Apply the {preset} preset? This changes {rows.Count} service(s):\n\n" +
                   string.Join("\n", rows.Select(r => $"  - {r.Display} → {r.Target}")) +
                   (warnings.Count > 0 ? "\n\nThings to know:\n" + string.Join("\n", warnings) : "") +
                   "\n\nHonest note: this mainly cuts background activity; FPS gains are small. Every change can be undone here or in the Change Log.";
        if (MessageBox.Show(text, $"{preset} preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var result = await AppServices.Engine.ApplyManyAsync(rows.Select(r => r.Tweak.Def), new Progress<string>(m => ServicesStatus = m));
        foreach (var r in Services) r.Refresh();
        ServicesStatus = $"{preset} preset: {result.Applied} changed" + (result.Failed > 0 ? $", {result.Failed} failed (needs admin?)" : "") + ".";
    }

    // ---- Processes -------------------------------------------------------------------------------------
    [RelayCommand]
    private void RefreshProcesses()
    {
        var keep = SelectedProcess?.Pid;
        _allProcesses = _procs.Snapshot();
        ApplyFilter();
        SelectedProcess = Processes.FirstOrDefault(p => p.Pid == keep);
        ProcessStatus = $"{_allProcesses.Count} processes. Protected Windows and anti-cheat processes can't be changed.";
    }

    private void ApplyFilter()
    {
        var keep = SelectedProcess?.Pid;
        Processes.Clear();
        foreach (var p in _allProcesses.Where(p => string.IsNullOrWhiteSpace(ProcessFilter) || p.Name.Contains(ProcessFilter, StringComparison.OrdinalIgnoreCase)).Take(200))
            Processes.Add(p);
        SelectedProcess = Processes.FirstOrDefault(p => p.Pid == keep);
    }

    private bool Guard(out Process? proc)
    {
        proc = null;
        if (SelectedProcess == null) { ProcessStatus = "Select a process first."; return false; }
        if (SelectedProcess.Protected) { ProcessStatus = $"{SelectedProcess.Name} is a protected Windows or anti-cheat process; Vanta leaves it alone."; return false; }
        try { proc = Process.GetProcessById(SelectedProcess.Pid); return true; }
        catch { ProcessStatus = "That process has already closed."; return false; }
    }

    [RelayCommand]
    private void SetPriority(string? priority)
    {
        if (priority == null || !Guard(out var p)) return;
        using (p)
        {
            var target = Enum.Parse<ProcessPriorityClass>(priority);
            ProcessStatus = ProcessUtil.TrySetPriority(p!, target) != null
                ? $"{SelectedProcess!.Name}: priority {priority} (until it restarts; use CPU & Power > rules to make it permanent)."
                : "Windows did not allow that change.";
        }
        RefreshProcesses();
    }

    [RelayCommand]
    private void SetIoLow()
    {
        if (!Guard(out var p)) return;
        p!.Dispose();
        ProcessStatus = ProcessManagerService.SetIoPriority(SelectedProcess!.Pid, 1)
            ? $"{SelectedProcess.Name}: low disk priority. It still works, but gives way when a game needs the drive."
            : "Windows did not allow that change.";
    }

    [RelayCommand]
    private void Suspend()
    {
        if (!Guard(out var p)) return;
        p!.Dispose();
        ProcessStatus = ProcessUtil.Suspend(SelectedProcess!.Pid)
            ? $"{SelectedProcess.Name} is frozen. Click Resume to unfreeze it (or it resumes when restarted)."
            : "Could not suspend it (access denied).";
    }

    [RelayCommand]
    private void Resume()
    {
        if (SelectedProcess == null) return;
        ProcessStatus = ProcessUtil.Resume(SelectedProcess.Pid) ? $"{SelectedProcess.Name} resumed." : "Could not resume it.";
    }

    [RelayCommand]
    private void EndTask()
    {
        if (!Guard(out var p)) return;
        using (p)
        {
            if (MessageBox.Show($"End {SelectedProcess!.Name}?\n\nUnsaved work in it will be lost. Vanta first asks it to close normally.",
                    "End task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                if (!p!.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
                ProcessStatus = $"{SelectedProcess.Name} ended.";
            }
            catch (Exception ex) { ProcessStatus = "Could not end it: " + ex.Message; }
        }
        RefreshProcesses();
    }

    [RelayCommand]
    private void AddToBoostList()
    {
        if (!Guard(out var p)) return;
        p!.Dispose();
        var profile = AppServices.Profiles.Default;
        if (profile.BackgroundApps.Any(b => b.ProcessName.Equals(SelectedProcess!.Name, StringComparison.OrdinalIgnoreCase)))
        {
            ProcessStatus = $"{SelectedProcess!.Name} is already on the boost list.";
            return;
        }
        profile.BackgroundApps.Add(new BackgroundAppRule { ProcessName = SelectedProcess!.Name, Action = BackgroundAction.Suspend });
        AppServices.Profiles.Save();
        ProcessStatus = $"{SelectedProcess.Name} will be suspended during boosts (default profile; edit in FPS Boost Center).";
    }
}
