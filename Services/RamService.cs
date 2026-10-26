using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TuneUp.Services;

public sealed record MemorySnapshot(double TotalGb, double UsedGb, double AvailableGb, double CommitUsedGb, double CommitLimitGb)
{
    public double UsedPercent => TotalGb <= 0 ? 0 : UsedGb / TotalGb * 100;
    public double CommitPercent => CommitLimitGb <= 0 ? 0 : CommitUsedGb / CommitLimitGb * 100;
}

/// <summary>
/// Physical memory split the way Windows' Resource Monitor shows it.
/// InUse = used by programs; Cached = standby list (file cache Windows hands back instantly);
/// Free = zeroed/free pages. Cached/Free are null when the detailed lists can't be read (no admin).
/// </summary>
public sealed record MemoryState(double TotalGb, double InUseGb, double AvailableGb, double? CachedGb, double? FreeGb, double? ModifiedGb, bool NeedsCleaning, string Reason)
{
    public double InUsePercent => TotalGb <= 0 ? 0 : InUseGb / TotalGb * 100;
}

public sealed record ProcessMemory(int Pid, string Name, double WorkingSetMb, double PrivateMb);

public sealed record LeakSuspect(string Name, int Pid, double StartMb, double NowMb, double Minutes)
{
    public double GrowthMb => NowMb - StartMb;
}

public sealed class RamCleanResult
{
    public DateTime When { get; init; } = DateTime.Now;
    public string Trigger { get; init; } = "";
    /// <summary>Memory moved out of programs back to available (from trimming).</summary>
    public double FreedFromAppsMb { get; init; }
    /// <summary>Cached memory turned into free memory (standby purge).</summary>
    public double CacheClearedMb { get; init; }
    /// <summary>
    /// Truly free RAM gained (zero + free pages, before vs after). This is the number tools like
    /// ExitLag show as "cleaned", so the headline uses it when Windows reports the memory lists.
    /// </summary>
    public double? FreeGainedMb { get; init; }
    /// <summary>RAM in use before and after, in percent (the number Task Manager and ExitLag show).</summary>
    public double BeforePercent { get; init; }
    public double AfterPercent { get; init; }
    public int AppsTrimmed { get; init; }
    public int AppsSkipped { get; init; }
    public bool Deep { get; init; }
    public List<string> Steps { get; init; } = new();
    public TimeSpan Duration { get; init; }

    public double TotalFreedMb => FreeGainedMb ?? FreedFromAppsMb + CacheClearedMb;

    public string Summary =>
        $"{(Deep ? "Deep clean" : "Cleaned")}: RAM in use {BeforePercent:0}% → {AfterPercent:0}%, freed {TotalFreedMb / 1024.0:0.0} GB in {Duration.TotalSeconds:0.0} s " +
        $"({FreedFromAppsMb / 1024.0:0.0} GB from {AppsTrimmed} app(s), {CacheClearedMb / 1024.0:0.0} GB of cache). " +
        $"{AppsSkipped} app(s) you use or allowed were left alone.";
}

/// <summary>
/// Smart RAM cleaner, modelled on what actually works (Mem Reduct, ISLC, Windows' memory lists):
///  1. Normal: trim only BACKGROUND programs: tray apps, updaters, idle Windows services. Skipped:
///     anything with an open window (the apps you're using), the running game, anything busy right
///     now, anti-cheat, and your allow list. Deep: trim every app except the game, the app in front and
///     the allow list, then squeeze everything system-wide including Windows' compressed memory (not
///     mid-game). Trimmed memory isn't lost: an app takes back what it needs when it next runs.
///  2. Flush the modified list (memory waiting to be written to disk).
///  3. Purge the low-priority standby cache first; only purge the whole standby list if free RAM is
///     still low (ISLC's approach, avoids throwing away useful cache).
///  4. Optional: combine identical memory pages (Windows 10+).
/// Honest scope: Windows already manages RAM well. Cleaning helps when RAM is genuinely tight, e.g.
/// a game plus a browser on 8-16 GB, where it can prevent stutter from paging.
/// </summary>
public sealed class RamService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Dictionary<int, (string Name, List<(DateTime T, double Mb)> Samples)> _leakSamples = new();
    private readonly SemaphoreSlim _cleanGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private DateTime _lastAutoClean = DateTime.MinValue;
    private DateTime _lastLeakSample = DateTime.MinValue;
    private HashSet<string> _runningGames = new();

    public RamService(SettingsService settings) => _settings = settings;

    private const string GameStartTrigger = "a game started";

    public RamCleanerSettings Options => _settings.Current.RamCleaner;

    /// <summary>Raised (on a background thread) after every clean, manual or automatic.</summary>
    public event Action<RamCleanResult>? Cleaned;

    // ---- Readings ----------------------------------------------------------------------------------
    public static MemorySnapshot Snapshot()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref m);
        const double gb = 1024.0 * 1024 * 1024;
        // ullTotalPageFile = commit limit (RAM + page file); minus available = committed.
        return new MemorySnapshot(m.ullTotalPhys / gb, (m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullAvailPhys / gb,
            (m.ullTotalPageFile - m.ullAvailPageFile) / gb, m.ullTotalPageFile / gb);
    }

    public MemoryState State()
    {
        var s = Snapshot();
        double? cached = null, free = null, modified = null;
        var lists = ReadMemoryLists();
        if (lists != null)
        {
            var page = Environment.SystemPageSize / 1024.0 / 1024 / 1024;
            free = (lists[0] + lists[1]) * page;               // zero + free pages
            modified = (lists[2] + lists[3]) * page;           // modified + modified-no-write
            cached = lists.Skip(5).Take(8).Sum(x => (double)x) * page; // standby, all priorities
        }

        var threshold = Options.ThresholdPercent;
        string reason;
        bool needs;
        if (s.UsedPercent >= threshold)
        {
            needs = true;
            reason = $"{s.UsedPercent:0}% of RAM is in use (your limit is {threshold}%).";
        }
        else if (free is { } f && cached is { } c && f < s.TotalGb * 0.05 && c > s.TotalGb * 0.25)
        {
            needs = true;
            reason = "Almost no free RAM left: most of it is stuck in cache that isn't being released quickly.";
        }
        else
        {
            needs = false;
            reason = $"{s.UsedPercent:0}% in use, plenty available.";
        }
        return new MemoryState(s.TotalGb, s.UsedGb, s.AvailableGb, cached, free, modified, needs, reason);
    }

    /// <summary>Raw page counts from SystemMemoryListInformation, or null without the privilege.</summary>
    private static long[]? ReadMemoryLists()
    {
        const int count = 22;
        var size = count * IntPtr.Size;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege");
            if (NativeMethods.NtQuerySystemInformation(NativeMethods.SystemMemoryListInformation, buf, size, out _) != 0) return null;
            var values = new long[count];
            for (var i = 0; i < count; i++) values[i] = Marshal.ReadIntPtr(buf, i * IntPtr.Size).ToInt64();
            return values;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static List<ProcessMemory> TopProcesses(int count)
    {
        var list = new List<ProcessMemory>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add(new ProcessMemory(p.Id, p.ProcessName, p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0)); }
                catch { /* exited / denied */ }
            }
        }
        return list.OrderByDescending(x => x.PrivateMb).Take(count).ToList();
    }

    // ---- Clean -------------------------------------------------------------------------------------
    public async Task<RamCleanResult?> CleanAsync(string trigger)
    {
        if (!await _cleanGate.WaitAsync(0)) return null; // already cleaning
        try
        {
            return await Task.Run(() => CleanCore(trigger));
        }
        finally
        {
            _cleanGate.Release();
        }
    }

    private RamCleanResult CleanCore(string trigger)
    {
        var o = Options;
        var sw = Stopwatch.StartNew();
        var steps = new List<string>();
        var before = Snapshot();
        var stateBefore = State();
        var cacheBefore = stateBefore.CachedGb;
        int trimmed = 0, skipped = 0;
        var deep = o.IncludeOpenApps;

        NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege");
        NativeMethods.EnablePrivilege("SeIncreaseQuotaPrivilege");

        if (o.TrimBackground)
        {
            (trimmed, skipped) = TrimApps(o, deep);
            steps.Add(deep ? $"Trimmed {trimmed} app(s)" : $"Trimmed {trimmed} background app(s)");
        }

        if (deep)
        {
            // Windows compresses trimmed memory into its MemCompression store instead of writing it to
            // disk, so per-app trimming alone leaves that memory "in use". Only the system-wide command
            // reaches it. It squeezes every app, so skip it mid-game (the game would stutter while it
            // pulls its memory back); right when a game starts it's fine, the game is still loading.
            var game = AppServices.Watcher?.Running.FirstOrDefault();
            if (game == null || trigger == GameStartTrigger)
            {
                if (SetMemoryList(NativeMethods.MemoryEmptyWorkingSets)) steps.Add("Squeezed every app and Windows' compressed memory");
            }
            else steps.Add($"Didn't squeeze everything because {game.Game.Name} is running (it would stutter)");

            if (NativeMethods.SetSystemFileCacheSize(UIntPtr.MaxValue, UIntPtr.MaxValue, 0)) steps.Add("Trimmed the system file cache");
        }

        if (o.FlushModified && SetMemoryList(NativeMethods.MemoryFlushModifiedList))
            steps.Add("Wrote pending memory to disk (modified list)");

        if (o.PurgeStandby)
        {
            if (SetMemoryList(NativeMethods.MemoryPurgeLowPriorityStandbyList)) steps.Add("Cleared low-priority cache");
            else steps.Add("Couldn't clear the cache: run Vanta as administrator for this step");
            // Smart: only throw away the rest of the cache if free memory is still tight (ISLC-style).
            // Deep: always clear it, like ExitLag / Mem Reduct.
            var st = State();
            if ((deep || st.FreeGb is { } free && free < st.TotalGb * 0.10) && SetMemoryList(NativeMethods.MemoryPurgeStandbyList))
                steps.Add(deep ? "Cleared all cache (deep clean)" : "Cleared the rest of the cache (free RAM was still low)");
        }

        if (o.CombinePages)
        {
            var info = new NativeMethods.MEMORY_COMBINE_INFORMATION_EX();
            if (NativeMethods.NtSetSystemInformation(NativeMethods.SystemCombinePhysicalMemoryInformation, ref info, Marshal.SizeOf<NativeMethods.MEMORY_COMBINE_INFORMATION_EX>()) == 0)
                steps.Add($"Combined {info.PagesCombined} duplicate memory page(s)");
        }

        var after = Snapshot();
        var stateAfter = State();
        var cacheAfter = stateAfter.CachedGb;
        var result = new RamCleanResult
        {
            Trigger = trigger,
            FreedFromAppsMb = Math.Max(0, (after.AvailableGb - before.AvailableGb) * 1024),
            CacheClearedMb = cacheBefore is { } cb && cacheAfter is { } ca ? Math.Max(0, (cb - ca) * 1024) : 0,
            FreeGainedMb = stateBefore.FreeGb is { } fb && stateAfter.FreeGb is { } fa ? Math.Max(0, (fa - fb) * 1024) : null,
            BeforePercent = before.UsedPercent,
            AfterPercent = after.UsedPercent,
            AppsTrimmed = trimmed,
            AppsSkipped = skipped,
            Deep = deep,
            Steps = steps,
            Duration = sw.Elapsed
        };

        o.LastCleanupUtc = DateTime.UtcNow;
        _settings.Save();
        AppLog.Info($"RAM clean ({trigger}): {result.Summary} Steps: {string.Join("; ", steps)}");
        Cleaned?.Invoke(result);
        return result;
    }

    private static bool SetMemoryList(int command)
    {
        var cmd = command;
        var status = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int));
        if (status != 0) AppLog.Warn($"Memory list command {command} failed: 0x{status:X8} (needs admin)");
        return status == 0;
    }

    /// <summary>
    /// Empties working sets. Normal: background processes only (no visible window, not busy right now),
    /// including Windows' own background services. Deep: every app except the game, the app in front
    /// and the allow list. Returns (trimmed, skipped).
    /// </summary>
    private (int, int) TrimApps(RamCleanerSettings o, bool deep)
    {
        var allow = new HashSet<string>(o.AllowList.Select(a => Path.GetFileNameWithoutExtension(a.Trim())), StringComparer.OrdinalIgnoreCase);
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var foregroundPid);
        var gamePids = AppServices.Watcher?.Running.SelectMany(r => r.Pids).ToHashSet() ?? new HashSet<int>();
        var self = Environment.ProcessId;

        // Two CPU samples half a second apart: anything doing work right now is left alone.
        var procs = Process.GetProcesses();
        var cpu1 = new Dictionary<int, TimeSpan>();
        foreach (var p in procs) { try { cpu1[p.Id] = p.TotalProcessorTime; } catch { } }
        Thread.Sleep(500);

        int trimmed = 0, skipped = 0;
        foreach (var p in procs)
        {
            using (p)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == self) continue;
                    var name = ProcessUtil.SafeName(p);
                    if (ProcessUtil.IsNeverTrim(name)) continue;

                    var inUse = allow.Contains(name) || p.Id == foregroundPid || gamePids.Contains(p.Id) ||
                                (!deep && p.MainWindowHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(p.MainWindowHandle));
                    // "Busy" = more than 10% of one core during the sample (normal clean only). Lower
                    // thresholds caught almost every app, since idle apps still tick now and then.
                    if (!inUse && !deep && cpu1.TryGetValue(p.Id, out var t1))
                    {
                        try { inUse = (p.TotalProcessorTime - t1).TotalMilliseconds >= 50; } catch { }
                    }
                    if (inUse) { skipped++; continue; }
                    if (p.WorkingSet64 < 20L * 1024 * 1024) continue; // not worth it

                    var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_QUOTA, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try { if (NativeMethods.EmptyWorkingSet(h)) trimmed++; }
                    finally { NativeMethods.CloseHandle(h); }
                }
                catch { /* exited or denied */ }
            }
        }
        return (trimmed, skipped);
    }

    // ---- Routine -----------------------------------------------------------------------------------
    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        if (AppServices.Watcher != null) AppServices.Watcher.RunningChanged += (_, _) => OnGamesChanged();
    }

    private void OnGamesChanged()
    {
        var now = AppServices.Watcher.Running.Select(r => r.Game.Id).ToHashSet();
        var started = now.Except(_runningGames).Any();
        _runningGames = now;
        if (!started || !Options.Auto || Options.Mode != "GameStart") return;
        if (DateTime.Now - _lastAutoClean < TimeSpan.FromMinutes(2)) return;
        _lastAutoClean = DateTime.Now;
        _ = CleanAsync(GameStartTrigger);
    }

    private void Tick()
    {
        try
        {
            if (DateTime.Now - _lastLeakSample >= TimeSpan.FromSeconds(30))
            {
                _lastLeakSample = DateTime.Now;
                SampleLeaks();
            }

            var o = Options;
            if (!o.Auto || o.Mode != "NeedsCleaning") return;
            if (DateTime.Now - _lastAutoClean < TimeSpan.FromMinutes(Math.Max(5, o.IntervalMinutes))) return;
            if (!State().NeedsCleaning) return;

            _lastAutoClean = DateTime.Now;
            _ = CleanAsync("RAM needed cleaning");
        }
        catch (Exception ex)
        {
            AppLog.Warn("RAM routine tick failed: " + ex.Message);
        }
    }

    // ---- Leak detector: private memory that keeps climbing for 10+ minutes ------------------------------
    private void SampleLeaks()
    {
        var now = DateTime.Now;
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    alive.Add(p.Id);
                    var mb = p.PrivateMemorySize64 / 1048576.0;
                    if (mb < 150 && !_leakSamples.ContainsKey(p.Id)) continue; // ignore small processes
                    if (!_leakSamples.TryGetValue(p.Id, out var entry))
                        _leakSamples[p.Id] = entry = (p.ProcessName, new List<(DateTime, double)>());
                    entry.Samples.Add((now, mb));
                    if (entry.Samples.Count > 120) entry.Samples.RemoveAt(0); // ~1 hour at 30 s
                }
                catch { /* denied */ }
            }
        }
        foreach (var dead in _leakSamples.Keys.Where(k => !alive.Contains(k)).ToList()) _leakSamples.Remove(dead);
    }

    /// <summary>Processes whose private memory rose in at least 80% of samples and grew by 300 MB+.</summary>
    public List<LeakSuspect> LeakSuspects()
    {
        var list = new List<LeakSuspect>();
        foreach (var (pid, (name, samples)) in _leakSamples.ToList())
        {
            if (samples.Count < 20) continue; // need 10 minutes
            var rises = 0;
            for (var i = 1; i < samples.Count; i++) if (samples[i].Mb > samples[i - 1].Mb) rises++;
            var growth = samples[^1].Mb - samples[0].Mb;
            if (rises >= (samples.Count - 1) * 0.8 && growth >= 300)
                list.Add(new LeakSuspect(name, pid, samples[0].Mb, samples[^1].Mb, (samples[^1].T - samples[0].T).TotalMinutes));
        }
        return list.OrderByDescending(l => l.GrowthMb).ToList();
    }

    public TimeSpan WatchedFor => _leakSamples.Values.Select(v => v.Samples.Count).DefaultIfEmpty(0).Max() * TimeSpan.FromSeconds(30);

    public void Dispose() => _timer?.Dispose();
}
