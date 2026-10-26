using System.Diagnostics;
using TuneUp.Models;

namespace TuneUp.Services;

public sealed record RunningGame(GameEntry Game, IReadOnlyList<int> Pids);

/// <summary>
/// Polls the process list every 2 seconds (cheap: only new PIDs get their path looked up) and:
///  - starts a boost when a detected game launches (if the booster is on and the profile allows),
///  - adds new game processes to the running boost (launcher handoffs),
///  - ends the boost a few seconds after the last game process is gone (normal exit OR crash),
///  - applies persistent per-app priority/affinity rules to any matching process.
/// </summary>
public sealed class GameWatcherService
{
    /// <summary>EXEs inside a game folder that are not the game itself.</summary>
    private static readonly string[] HelperFragments =
    {
        "crash", "report", "setup", "unins", "redist", "prereq", "dxsetup", "easyanticheat", "battleye",
        "beservice", "launcher", "updater", "webhelper", "cefprocess", "qtwebengine", "socialclub",
        "eac_", "anticheat", "bootstrap", "install", "helper", "overlay", "dotnet"
    };

    private readonly GameDetectionService _games;
    private readonly BoostEngine _engine;
    private readonly ProfileService _profiles;
    private readonly ProcessRuleService _rules;
    private readonly SettingsService _settings;

    private readonly Dictionary<int, (string? Path, string Name)> _known = new();
    private readonly Dictionary<string, List<int>> _running = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _gameGoneSince;
    private CancellationTokenSource? _cts;

    public GameWatcherService(GameDetectionService games, BoostEngine engine, ProfileService profiles,
                              ProcessRuleService rules, SettingsService settings)
    {
        _games = games;
        _engine = engine;
        _profiles = profiles;
        _rules = rules;
        _settings = settings;
    }

    public event EventHandler? RunningChanged;

    public IReadOnlyList<RunningGame> Running
    {
        get
        {
            lock (_running)
                return _running.Select(kv => (Game: _games.Find(kv.Key), Pids: kv.Value.ToList()))
                               .Where(x => x.Game != null)
                               .Select(x => new RunningGame(x.Game!, x.Pids))
                               .ToList();
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await PollAsync(); }
                catch (Exception ex) { AppLog.Error("Game watcher poll failed", ex); }

                try { await Task.Delay(2000, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    public void Stop() => _cts?.Cancel();

    private async Task PollAsync()
    {
        var games = _games.Games;
        var alive = new HashSet<int>();
        var nowRunning = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var newPids = new List<(int Pid, string Name)>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                alive.Add(p.Id);
                if (!_known.TryGetValue(p.Id, out var info))
                {
                    info = (ProcessUtil.GetImagePath(p.Id), ProcessUtil.SafeName(p));
                    _known[p.Id] = info;
                    newPids.Add((p.Id, info.Name));
                }

                if (info.Path == null) continue;
                var game = Match(games, info.Path, p);
                if (game == null) continue;

                if (!nowRunning.TryGetValue(game.Id, out var list)) nowRunning[game.Id] = list = new List<int>();
                list.Add(p.Id);
            }
        }

        foreach (var dead in _known.Keys.Where(k => !alive.Contains(k)).ToList()) _known.Remove(dead);

        foreach (var (pid, name) in newPids) ApplyRule(pid, name);

        bool changed;
        lock (_running)
        {
            changed = nowRunning.Count != _running.Count ||
                      nowRunning.Any(kv => !_running.TryGetValue(kv.Key, out var old) || !old.SequenceEqual(kv.Value));
            _running.Clear();
            foreach (var kv in nowRunning) _running[kv.Key] = kv.Value;
        }
        if (changed) RunningChanged?.Invoke(this, EventArgs.Empty);

        await DriveBoostAsync(nowRunning);
    }

    private async Task DriveBoostAsync(Dictionary<string, List<int>> nowRunning)
    {
        if (_engine.IsActive && _engine.ActiveGame is { } active)
        {
            if (nowRunning.TryGetValue(active.Id, out var pids))
            {
                _gameGoneSince = null;
                foreach (var pid in pids) _engine.AddGamePid(pid);
                foreach (var gone in _engine.GamePids.Where(x => !pids.Contains(x))) _engine.RemoveGamePid(gone);
            }
            else
            {
                // Grace period: covers launcher -> game handoffs and quick restarts.
                _gameGoneSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _gameGoneSince > TimeSpan.FromSeconds(6))
                {
                    _gameGoneSince = null;
                    await _engine.DeactivateAsync("game closed");
                }
            }
            return;
        }

        if (!_settings.Current.BoosterEnabled || nowRunning.Count == 0) return;

        foreach (var (gameId, pids) in nowRunning)
        {
            var game = _games.Find(gameId);
            if (game == null || !_profiles.For(gameId).AutoActivate) continue;
            await _engine.ActivateAsync(game, pids);
            break;
        }
    }

    private static GameEntry? Match(IReadOnlyList<GameEntry> games, string path, Process p)
    {
        var file = Path.GetFileName(path);
        foreach (var g in games)
        {
            foreach (var exe in g.Executables)
            {
                var hit = exe.Contains('\\')
                    ? exe.Equals(path, StringComparison.OrdinalIgnoreCase)
                    : exe.Equals(file, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;

                if (g.WindowTitleContains == null) return g;
                try
                {
                    if (p.MainWindowTitle.Contains(g.WindowTitleContains, StringComparison.OrdinalIgnoreCase)) return g;
                }
                catch { /* no window yet */ }
            }

            if (g.InstallDir != null && GameDetectionService.IsUnder(path, g.InstallDir) && !IsHelper(file))
                return g;
        }
        return null;
    }

    public static bool IsHelper(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return !lower.EndsWith(".exe") || HelperFragments.Any(lower.Contains);
    }

    private void ApplyRule(int pid, string name)
    {
        var rule = _rules.Match(name);
        if (rule == null || ProcessUtil.IsProtected(name)) return;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (ProcessUtil.ParsePriority(rule.Priority) is { } prio && ProcessUtil.TrySetPriority(proc, prio) != null)
                AppLog.Info($"Rule: {name} priority {prio}");

            if (rule.Affinity != AffinityPreset.None && CpuTopologyService.Get().MaskFor(rule.Affinity) is { } mask)
                ProcessUtil.TrySetAffinity(proc, mask);
        }
        catch (ArgumentException)
        {
            // exited already
        }
    }

    /// <summary>Re-applies all rules to processes already running (after editing rules).</summary>
    public void ReapplyRules()
    {
        foreach (var p in Process.GetProcesses())
            using (p) ApplyRule(p.Id, ProcessUtil.SafeName(p));
    }
}
