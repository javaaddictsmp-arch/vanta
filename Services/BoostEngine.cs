using System.Diagnostics;
using System.ServiceProcess;
using System.Windows;
using Microsoft.Win32;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Runs a boost for one game. Every step:
///   1. records what it is about to change in BoostState,
///   2. saves BoostState to disk,
///   3. makes the change.
/// So whatever happens (game crash, Vanta killed, power loss) there is always enough on disk
/// to put things back. A separate watchdog process restores immediately if Vanta dies.
/// </summary>
public sealed class BoostEngine
{
    private readonly ProfileService _profiles;
    private readonly PowerPlanService _power;
    private readonly TimerResolutionService _timer;
    private readonly SettingsService _settings;
    private readonly ProBalanceService _proBalance = new();
    private readonly KeyboardGuard _keyboard = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<int> _gamePids = new();
    private readonly List<string> _activity = new();

    private BoostState? _state;
    private BoostProfile? _profile;
    private GameEntry? _game;
    private Process? _watchdog;

    public BoostEngine(ProfileService profiles, PowerPlanService power, TimerResolutionService timer, SettingsService settings)
    {
        _profiles = profiles;
        _power = power;
        _timer = timer;
        _settings = settings;
        _proBalance.Activity += Log;
    }

    public event EventHandler? StateChanged;
    public event Action<string>? ActivityLogged;
    /// <summary>Raised on activation when the profile wants the overlay; the UI owns the window.</summary>
    public event Action<GameEntry, bool>? OverlayRequested;

    public bool IsActive => _state?.Active == true;
    public GameEntry? ActiveGame => _game;
    public BoostProfile? ActiveProfile => _profile;
    public DateTime? StartedUtc => _state?.StartedUtc;
    public IReadOnlyList<string> StepsApplied => _state?.StepsApplied.ToList() ?? new List<string>();
    public int ProBalanceRestrained => _proBalance.Restrained;

    public IReadOnlyList<string> Activity
    {
        get { lock (_activity) return _activity.ToList(); }
    }

    public bool IsGamePid(int pid)
    {
        lock (_gamePids) return _gamePids.Contains(pid);
    }

    public IReadOnlyList<int> GamePids
    {
        get { lock (_gamePids) return _gamePids.ToList(); }
    }

    // =========================================================================================
    public async Task ActivateAsync(GameEntry game, IReadOnlyCollection<int> pids)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsActive)
            {
                if (_game?.Id == game.Id) { foreach (var pid in pids) AddGamePidCore(pid); return; }
                await DeactivateCoreAsync("another game started");
            }

            _game = game;
            _profile = _profiles.For(game.Id);
            var p = _profile;
            _state = new BoostState
            {
                Active = true,
                GameId = game.Id,
                GameName = game.Name,
                StartedUtc = DateTime.UtcNow,
                TuneUpPid = Environment.ProcessId
            };
            Save();
            StartWatchdog();
            Log($"Boost started for {game.Name}" + (_profiles.HasOwn(game.Id) ? "" : " (default profile)"));

            lock (_gamePids) _gamePids.Clear();
            foreach (var pid in pids) AddGamePidCore(pid);

            if (p.UsePerformancePlan)
                await StepAsync("Performance power plan", async () =>
                {
                    var current = await _power.GetActiveAsync();
                    if (current == PowerPlanService.TuneUpPlanGuid) return false;
                    if (!await _power.EnsureTuneUpPlanAsync(_settings.Current.PlanSettings)) return false;
                    _state.OriginalPowerPlan = _settings.Current.IdleSaverBalanced ? PowerPlanService.BalancedGuid : current;
                    Save();
                    return await _power.SetActiveAsync(PowerPlanService.TuneUpPlanGuid);
                });

            if (p.PauseSearchIndexing) await StopServicesAsync("Paused Windows Search indexing", "WSearch");
            if (p.PauseWindowsUpdate) await StopServicesAsync("Paused Windows Update downloads", "wuauserv", "UsoSvc", "DoSvc");

            foreach (var rule in p.BackgroundApps) HandleBackgroundApp(rule);

            if (p.MuteNotifications)
                Step("Muted notifications", () =>
                {
                    const string path = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
                    var cur = RegistryService.Read("HKCU", path, "ToastEnabled");
                    _state.OriginalToastEnabled = cur.Exists && cur.Value != null ? RegistryService.ParseDword(cur.Value) : -1;
                    Save();
                    RegistryService.Write("HKCU", path, "ToastEnabled", RegistryValueKind.DWord, "0");
                    return true;
                });

            if (p.BlockAccessibilityShortcuts)
                Step("Sticky/Filter/Toggle Keys prompts off", () =>
                {
                    KeyboardGuard.DisableAccessibilityShortcuts(_state);
                    Save();
                    return true;
                });

            if (p.BlockWindowsKey)
                Step("Windows key blocked while the game is in front", () =>
                {
                    Application.Current?.Dispatcher.Invoke(() => _keyboard.BlockWindowsKey(IsGamePid));
                    return true;
                });

            if (p.TrimStandbyIfLow)
                Step("Standby list trimmed (RAM was low)", () =>
                    MemoryTools.AvailablePercent() < 15 && MemoryTools.PurgeStandbyList());

            if (p.ClearClipboard)
                Step("Clipboard cleared", () =>
                {
                    Application.Current?.Dispatcher.Invoke(Clipboard.Clear);
                    return true;
                });

            if (p.TimerResolution100ns > 0)
                Step($"Timer resolution {p.TimerResolution100ns / 10_000.0:0.###} ms requested", () =>
                {
                    _timer.Request((uint)p.TimerResolution100ns);
                    return true;
                });

            if (p.ProBalance)
                Step("ProBalance watching background CPU hogs", () =>
                {
                    _proBalance.Start(IsGamePid, _state, Save,
                        _settings.Current.ProBalanceThresholdPercent, _settings.Current.ProBalanceExclusions);
                    return true;
                });

            if (p.ShowOverlay) OverlayRequested?.Invoke(game, true);
            Save();
        }
        catch (Exception ex)
        {
            AppLog.Error("Boost activation failed", ex);
            Log("Boost hit an error: " + ex.Message);
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A new process of the running game appeared (launcher handoff, child process).</summary>
    public void AddGamePid(int pid)
    {
        if (!IsActive || IsGamePid(pid)) return;
        AddGamePidCore(pid);
        Save();
    }

    private void AddGamePidCore(int pid)
    {
        lock (_gamePids) _gamePids.Add(pid);
        if (_state == null || _profile == null) return;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = ProcessUtil.SafeName(proc);

            if (_game != null && _game.LastExePath == null)
            {
                _game.LastExePath = ProcessUtil.GetImagePath(pid);
                if (_game.LastExePath != null) AppServices.Games.Remember(_game);
            }

            if (_profile.HighPriority)
            {
                var old = ProcessUtil.TrySetPriority(proc, ProcessPriorityClass.High);
                if (old != null)
                {
                    var t = ProcessUtil.Track(proc);
                    t.OriginalPriority = old.ToString();
                    lock (_state) _state.PriorityChanges.Add(t);
                    AddStep($"{name}: priority High");
                }
                else Log($"{name}: Windows did not allow a priority change (normal for some anti-cheat games), skipped");
            }

            if (_profile.DisablePriorityBoost)
            {
                try { proc.PriorityBoostEnabled = false; AddStep($"{name}: dynamic priority boost off"); }
                catch { /* not allowed */ }
            }

            if (_profile.Affinity != AffinityPreset.None)
            {
                var topo = CpuTopologyService.Get();
                var mask = topo.MaskFor(_profile.Affinity);
                if (mask == null) Log($"Affinity preset skipped: {topo.WhyNot(_profile.Affinity)}");
                else if (ProcessUtil.TrySetAffinity(proc, mask.Value)) AddStep($"{name}: CPU affinity {_profile.Affinity}");
            }
        }
        catch (ArgumentException)
        {
            // already exited
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Game process setup failed for {pid}: {ex.Message}");
        }
    }

    public void RemoveGamePid(int pid)
    {
        lock (_gamePids) _gamePids.Remove(pid);
    }

    // =========================================================================================
    public async Task DeactivateAsync(string reason)
    {
        await _gate.WaitAsync();
        try
        {
            await DeactivateCoreAsync(reason);
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DeactivateCoreAsync(string reason)
    {
        if (_state == null || !_state.Active) return;

        _proBalance.Stop();
        // BeginInvoke (not Invoke): during app shutdown the UI thread is waiting on us.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) _keyboard.Unblock();
        else _ = dispatcher.BeginInvoke(_keyboard.Unblock);
        _timer.Release();
        if (_game != null) OverlayRequested?.Invoke(_game, false);

        await BoostRestorer.RestoreAsync(_state, reason);
        StopWatchdog();

        var minutes = (DateTime.UtcNow - _state.StartedUtc).TotalMinutes;
        Log($"Boost ended for {_state.GameName} after {minutes:0} min ({reason}). Everything was put back.");

        _state = null;
        _profile = null;
        _game = null;
        lock (_gamePids) _gamePids.Clear();
    }

    // =========================================================================================
    private async Task StopServicesAsync(string label, params string[] services)
    {
        var stopped = new List<string>();
        foreach (var name in services)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop) continue;

                _state!.StoppedServices.Add(name);
                Save();
                sc.Stop();
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)));
                stopped.Add(name);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not stop service {name}: {ex.Message}");
            }
        }
        if (stopped.Count > 0) AddStep($"{label} ({string.Join(", ", stopped)})");
    }

    private void HandleBackgroundApp(BackgroundAppRule rule)
    {
        if (ProcessUtil.IsProtected(rule.ProcessName)) return;

        foreach (var proc in Process.GetProcessesByName(rule.ProcessName))
        {
            using (proc)
            {
                if (IsGamePid(proc.Id)) continue;
                var t = ProcessUtil.Track(proc);

                if (rule.Action == BackgroundAction.Suspend)
                {
                    _state!.Suspended.Add(t);
                    Save();
                    if (ProcessUtil.Suspend(proc.Id)) AddStep($"Suspended {t.Name}");
                    else
                    {
                        _state.Suspended.Remove(t);
                        Log($"Could not suspend {t.Name} (access denied)");
                    }
                }
                else
                {
                    // Graceful close only (like clicking X). We never force-kill: that can lose data.
                    try
                    {
                        if (rule.Reopen && t.Path != null && !_state!.Closed.Any(c => c.Name == t.Name))
                        {
                            _state.Closed.Add(t);
                            Save();
                        }
                        if (proc.CloseMainWindow()) AddStep($"Asked {t.Name} to close");
                        else Log($"{t.Name} has no window to close; use Suspend for it instead");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not close {t.Name}: {ex.Message}");
                    }
                }
            }
        }
    }

    private void Step(string label, Func<bool> action)
    {
        try
        {
            if (action()) AddStep(label);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Boost step '{label}' failed: {ex.Message}");
            Log($"Skipped: {label} ({ex.Message})");
        }
    }

    private async Task StepAsync(string label, Func<Task<bool>> action)
    {
        try
        {
            if (await action()) AddStep(label);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Boost step '{label}' failed: {ex.Message}");
            Log($"Skipped: {label} ({ex.Message})");
        }
    }

    private void AddStep(string label)
    {
        if (_state == null) return;
        lock (_state) _state.StepsApplied.Add(label);
        Log(label);
    }

    private void Save()
    {
        if (_state == null) return;
        lock (_state) BoostStateStore.Save(_state);
    }

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_activity)
        {
            _activity.Insert(0, line);
            if (_activity.Count > 200) _activity.RemoveAt(_activity.Count - 1);
        }
        AppLog.Info("Boost: " + message);
        ActivityLogged?.Invoke(line);
    }

    // =========================================================================================
    // Watchdog: a second, windowless copy of Vanta that restores the boost if this one dies.
    // =========================================================================================
    private void StartWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

            _watchdog = Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not start boost watchdog (restore will happen on next Vanta start instead): " + ex.Message);
        }
    }

    private void StopWatchdog()
    {
        try
        {
            if (_watchdog is { HasExited: false }) _watchdog.Kill();
        }
        catch { /* ignore */ }
        _watchdog?.Dispose();
        _watchdog = null;
    }

    /// <summary>Entry point for "Vanta.exe --watchdog PID".</summary>
    public static async Task RunWatchdogAsync(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // parent already gone
        }

        var state = BoostStateStore.Load();
        if (state is { Active: true } && state.TuneUpPid == parentPid)
            await BoostRestorer.RestoreAsync(state, "Vanta closed unexpectedly (watchdog)");
    }

    /// <summary>App is closing normally: end the boost first so nothing is left changed.</summary>
    public void ShutdownBlocking()
    {
        if (!IsActive) return;
        _keyboard.Unblock(); // we are on the UI thread here
        try { Task.Run(() => DeactivateAsync("Vanta closed")).Wait(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { AppLog.Error("Boost shutdown failed", ex); }
    }
}
