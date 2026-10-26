using System.Text.Json;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>What happened during one play session, saved when the game closes.</summary>
public sealed class GameSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Game { get; set; } = "";
    public string GameId { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public double DurationMin { get; set; }

    public bool Boosted { get; set; }
    public List<string> BoostSteps { get; set; } = new();

    // FPS (null when frametimes couldn't be captured: no admin, or the game's API isn't visible)
    public string FrameSource { get; set; } = "";
    public double? AvgFps { get; set; }
    public double? Low1Fps { get; set; }
    public double? Low01Fps { get; set; }
    public int Stutters { get; set; }
    public List<double> FpsTimeline { get; set; } = new();   // one point per 10 s

    // Hardware
    public double CpuLoadAvg { get; set; }
    public double? GpuLoadAvg { get; set; }
    public double? CpuTempAvg { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempAvg { get; set; }
    public double? GpuTempMax { get; set; }
    public double RamPeakPercent { get; set; }
    public double? VramPeakPercent { get; set; }
    public string Bottleneck { get; set; } = "";
    public List<string> BottleneckEvidence { get; set; } = new();
    public List<string> Tips { get; set; } = new();

    // Network (null when no game server was found)
    public string Server { get; set; } = "";
    public double? PingAvg { get; set; }
    public double? JitterMs { get; set; }
    public double? LossPercent { get; set; }
    public List<double> PingTimeline { get; set; } = new(); // one point per 10 s
}

/// <summary>
/// Records a report for every game session in the background (like Hone/Outplayed match summaries).
/// Frametimes are collected into a compact histogram (0.1 ms bins), so a 3-hour session costs a
/// few kilobytes, not hundreds of megabytes. Sessions shorter than 2 minutes are thrown away.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private const int BinsPerMs = 10, MaxMs = 250;
    private static readonly string[] SourcePriority = { "DXGI", "D3D9", "DxgKrnl" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class FrameStats
    {
        public readonly int[] Bins = new int[MaxMs * BinsPerMs + 1];
        public long Count;
        public double SumMs;
        public int Stutters;
        public double Ema = -1;           // running typical frametime, for stutter detection
        public int WindowFrames;
        public double WindowMs;
        public readonly List<double> Timeline = new();
    }

    private sealed class Active
    {
        public RunningGame Running = null!;
        public DateTime Start;
        public HashSet<int> Pids = new();
        public readonly Dictionary<(int, string), FrameStats> Frames = new();
        public readonly List<MetricSample> Samples = new();
        public readonly List<double> PingWindow = new();
        public readonly List<double> PingTimeline = new();
        public bool Boosted;
        public List<string> Steps = new();
        public GameConnectionSnapshot? LastNet;
    }

    private readonly GameWatcherService _watcher;
    private readonly MetricsHub _hub;
    private readonly FrametimeCaptureService _frames;
    private readonly BoostEngine _boost;
    private readonly GameConnectionService _net;
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private Active? _active;
    private DateTime _lastPingBucket;

    /// <summary>Raised (background thread) when a report has been saved.</summary>
    public event Action<GameSession>? Saved;

    public SessionRecorder(GameWatcherService watcher, MetricsHub hub, FrametimeCaptureService frames,
                           BoostEngine boost, GameConnectionService net, SettingsService settings)
    {
        _watcher = watcher;
        _hub = hub;
        _frames = frames;
        _boost = boost;
        _net = net;
        _settings = settings;
        _watcher.RunningChanged += (_, _) => OnRunningChanged();
        _hub.SampleReceived += OnSample;
        _frames.FrameObserved += OnFrame;
        _net.Updated += OnNet;
    }

    public bool IsRecording { get { lock (_gate) return _active != null; } }
    public string? RecordingGame { get { lock (_gate) return _active?.Running.Game.Name; } }

    private void OnRunningChanged()
    {
        var running = _watcher.Running.FirstOrDefault();
        Active? finished = null;
        lock (_gate)
        {
            if (_active != null && (running == null || running.Game.Id != _active.Running.Game.Id))
            {
                finished = _active;
                _active = null;
            }
            if (_active != null && running != null)
            {
                _active.Pids = running.Pids.ToHashSet(); // launcher handoff / extra processes
                _active.Running = running;
            }
            else if (running != null && _settings.Current.SessionReports)
            {
                _active = new Active { Running = running, Start = DateTime.UtcNow, Pids = running.Pids.ToHashSet() };
                AppLog.Info($"Session recording started: {running.Game.Name}");
            }
        }
        if (_active != null && !_frames.IsRunning) _frames.Start(); // needs admin; reports just skip FPS otherwise
        if (finished != null) _ = Task.Run(() => Finish(finished));
    }

    private void OnSample(MetricSample s)
    {
        lock (_gate)
        {
            if (_active == null) return;
            if (_active.Samples.Count < 6 * 60 * 60) _active.Samples.Add(s); // cap at ~6 h of 1 s samples
            if (_boost.IsActive) { _active.Boosted = true; _active.Steps = _boost.StepsApplied.ToList(); }
        }
    }

    private void OnFrame(int pid, string source, float ms)
    {
        lock (_gate)
        {
            if (_active == null || !_active.Pids.Contains(pid)) return;
            if (!_active.Frames.TryGetValue((pid, source), out var f)) _active.Frames[(pid, source)] = f = new FrameStats();
            f.Count++;
            f.SumMs += ms;
            f.Bins[Math.Min((int)(ms * BinsPerMs), f.Bins.Length - 1)]++;
            if (f.Ema > 0 && ms > f.Ema * 2 && ms - f.Ema > 8) f.Stutters++;   // same rule as benchmarks, vs a running median-ish average
            f.Ema = f.Ema < 0 ? ms : f.Ema * 0.98 + ms * 0.02;
            f.WindowFrames++;
            f.WindowMs += ms;
            if (f.WindowMs >= 10_000)
            {
                f.Timeline.Add(Math.Round(f.WindowFrames * 1000.0 / f.WindowMs, 1));
                f.WindowFrames = 0;
                f.WindowMs = 0;
            }
        }
    }

    private void OnNet(GameConnectionSnapshot snap)
    {
        lock (_gate)
        {
            if (_active == null || snap.Game != _active.Running.Game.Name || string.IsNullOrEmpty(snap.Server)) return;
            _active.LastNet = snap;
            if (snap.LastMs is { } ms) _active.PingWindow.Add(ms);
            if (DateTime.UtcNow - _lastPingBucket >= TimeSpan.FromSeconds(10) && _active.PingWindow.Count > 0)
            {
                _active.PingTimeline.Add(Math.Round(_active.PingWindow.Average(), 1));
                _active.PingWindow.Clear();
                _lastPingBucket = DateTime.UtcNow;
            }
        }
    }

    private void Finish(Active a)
    {
        try
        {
            var end = DateTime.UtcNow;
            var minutes = (end - a.Start).TotalMinutes;
            if (minutes < 2)
            {
                AppLog.Info($"Session for {a.Running.Game.Name} was under 2 minutes, not saved");
                return;
            }

            GameSession session;
            lock (_gate) session = Build(a, end);
            File.WriteAllText(Path.Combine(AppPaths.Sessions, $"{session.StartUtc:yyyyMMdd-HHmmss}-{session.Id:N}.json"),
                JsonSerializer.Serialize(session, Json));
            AppLog.Info($"Session report saved: {session.Game}, {session.DurationMin:0} min");
            Saved?.Invoke(session);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save session report", ex);
        }
    }

    private static GameSession Build(Active a, DateTime end)
    {
        var s = new GameSession
        {
            Game = a.Running.Game.Name,
            GameId = a.Running.Game.Id,
            StartUtc = a.Start,
            EndUtc = end,
            DurationMin = Math.Round((end - a.Start).TotalMinutes, 1),
            Boosted = a.Boosted,
            BoostSteps = a.Steps
        };

        // Frames: per process prefer DXGI > D3D9 > DxgKrnl, then the process with the most frames.
        var best = a.Frames
            .GroupBy(kv => kv.Key.Item1)
            .Select(g => g.OrderBy(kv => Array.IndexOf(SourcePriority, kv.Key.Item2)).First())
            .OrderByDescending(kv => kv.Value.Count)
            .FirstOrDefault();
        if (best.Value is { Count: >= 300 } f)
        {
            s.FrameSource = best.Key.Item2;
            s.AvgFps = Math.Round(f.Count * 1000.0 / f.SumMs, 1);
            s.Low1Fps = Math.Round(1000.0 / PercentileMs(f, 0.99), 1);
            s.Low01Fps = Math.Round(1000.0 / PercentileMs(f, 0.999), 1);
            s.Stutters = f.Stutters;
            s.FpsTimeline = f.Timeline.ToList();
        }

        var m = a.Samples;
        if (m.Count > 0)
        {
            s.CpuLoadAvg = Math.Round(m.Average(x => x.CpuLoad), 1);
            s.GpuLoadAvg = AvgOf(m.Select(x => x.GpuLoad));
            s.CpuTempAvg = AvgOf(m.Select(x => x.CpuTempC is > 0 ? x.CpuTempC : null));
            s.CpuTempMax = MaxOf(m.Select(x => x.CpuTempC is > 0 ? x.CpuTempC : null));
            s.GpuTempAvg = AvgOf(m.Select(x => x.GpuTempC));
            s.GpuTempMax = MaxOf(m.Select(x => x.GpuTempC));
            s.RamPeakPercent = Math.Round(m.Max(x => x.RamPercent), 1);
            s.VramPeakPercent = MaxOf(m.Select(x => x.GpuMemPercent));

            var report = BottleneckService.Analyze(m);
            s.Bottleneck = report.Verdict;
            s.BottleneckEvidence = report.Evidence;
            s.Tips = report.Fixes;
        }

        if (a.LastNet is { Sent: > 0 } net)
        {
            s.Server = net.Server;
            s.PingAvg = Math.Round(net.AvgMs, 1);
            s.JitterMs = Math.Round(net.JitterMs, 1);
            s.LossPercent = Math.Round(net.LossPercent, 1);
            s.PingTimeline = a.PingTimeline.ToList();
        }
        return s;
    }

    private static double PercentileMs(FrameStats f, double p)
    {
        var target = (long)Math.Ceiling(p * f.Count);
        long seen = 0;
        for (var i = 0; i < f.Bins.Length; i++)
        {
            seen += f.Bins[i];
            if (seen >= target) return Math.Max((i + 0.5) / BinsPerMs, 0.05);
        }
        return MaxMs;
    }

    private static double? AvgOf(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 1);
    }

    private static double? MaxOf(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Max(), 1);
    }

    // ---- History -------------------------------------------------------------------------------
    public List<GameSession> LoadHistory()
    {
        var list = new List<GameSession>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.Sessions, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<GameSession>(File.ReadAllText(file));
                if (r != null) list.Add(r);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Skipping session file {file}: {ex.Message}");
            }
        }
        return list.OrderByDescending(r => r.StartUtc).ToList();
    }

    public void Delete(GameSession s)
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.Sessions, $"*{s.Id:N}.json"))
            try { File.Delete(file); } catch { /* ignore */ }
    }

    /// <summary>Saves the running session now (used when Vanta closes mid-game).</summary>
    public void Dispose()
    {
        Active? a;
        lock (_gate) { a = _active; _active = null; }
        if (a != null) Finish(a);
    }
}
