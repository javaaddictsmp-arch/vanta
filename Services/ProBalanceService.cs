using System.Diagnostics;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// ProBalance-style automation (the idea popularised by Process Lasso): while a game runs,
/// background processes that hog the CPU get their priority lowered to Below Normal for a
/// while, then put back once they calm down. It never touches the game, system processes,
/// anti-cheat, or anything on your exclusion list, and it never suspends or kills anything.
/// </summary>
public sealed class ProBalanceService
{
    private sealed class Watch
    {
        public TimeSpan LastCpu;
        public int HotSeconds;
        public int CalmSeconds;
        public bool Lowered;
    }

    private readonly Dictionary<int, Watch> _watch = new();
    private CancellationTokenSource? _cts;

    /// <summary>Raised when a process is lowered or restored, for the activity log.</summary>
    public event Action<string>? Activity;

    public int Restrained => _watch.Count(w => w.Value.Lowered);

    public void Start(Func<int, bool> isGamePid, BoostState state, Action saveState,
                      double thresholdPercent, IReadOnlyCollection<string> exclusions)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var cpuCount = Environment.ProcessorCount;
        var excluded = new HashSet<string>(exclusions, StringComparer.OrdinalIgnoreCase);

        _ = Task.Run(async () =>
        {
            var last = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }

                var now = DateTime.UtcNow;
                var wall = (now - last).TotalMilliseconds * cpuCount;
                last = now;

                var seen = new HashSet<int>();
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            seen.Add(p.Id);
                            var name = ProcessUtil.SafeName(p);
                            if (p.Id <= 4 || isGamePid(p.Id) || ProcessUtil.IsProtected(name) || excluded.Contains(name))
                                continue;

                            TimeSpan cpu;
                            try { cpu = p.TotalProcessorTime; }
                            catch { continue; } // access denied: can't measure, can't change either

                            if (!_watch.TryGetValue(p.Id, out var w))
                            {
                                _watch[p.Id] = new Watch { LastCpu = cpu };
                                continue;
                            }

                            var usage = (cpu - w.LastCpu).TotalMilliseconds / wall * 100.0;
                            w.LastCpu = cpu;

                            if (usage >= thresholdPercent) { w.HotSeconds++; w.CalmSeconds = 0; }
                            else if (usage < thresholdPercent / 2) { w.CalmSeconds++; w.HotSeconds = 0; }

                            if (!w.Lowered && w.HotSeconds >= 2)
                            {
                                var old = ProcessUtil.TrySetPriority(p, ProcessPriorityClass.BelowNormal);
                                if (old is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High)
                                {
                                    w.Lowered = true;
                                    var t = ProcessUtil.Track(p);
                                    t.OriginalPriority = old.ToString();
                                    lock (state) state.PriorityChanges.Add(t);
                                    saveState();
                                    Activity?.Invoke($"Restrained {name} ({usage:0}% CPU)");
                                }
                                else if (old != null && old != ProcessPriorityClass.BelowNormal)
                                {
                                    ProcessUtil.TrySetPriority(p, old.Value); // was Idle etc.: leave as it was
                                }
                            }
                            else if (w.Lowered && w.CalmSeconds >= 5)
                            {
                                RestoreOne(p.Id, state);
                                w.Lowered = false;
                                saveState();
                                Activity?.Invoke($"Released {name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"ProBalance: {ex.Message}");
                        }
                    }
                }

                foreach (var gone in _watch.Keys.Where(k => !seen.Contains(k)).ToList()) _watch.Remove(gone);
            }
        }, token);
    }

    private static void RestoreOne(int pid, BoostState state)
    {
        TrackedProcess? t;
        lock (state) t = state.PriorityChanges.LastOrDefault(x => x.Pid == pid);
        if (t == null) return;

        try
        {
            if (ProcessUtil.IsSameProcess(t) && ProcessUtil.ParsePriority(t.OriginalPriority) is { } prio)
            {
                using var p = Process.GetProcessById(pid);
                ProcessUtil.TrySetPriority(p, prio);
            }
        }
        catch { /* gone */ }

        lock (state) state.PriorityChanges.Remove(t);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _watch.Clear();
    }
}

/// <summary>RAM helpers with honest scope.</summary>
public static class MemoryTools
{
    /// <summary>Available RAM as % of total.</summary>
    public static double AvailablePercent()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref mem) ? mem.ullAvailPhys * 100.0 / mem.ullTotalPhys : 100;
    }

    /// <summary>
    /// Empties the standby list (cached file data Windows would hand out anyway). It does NOT
    /// give you "more RAM"; it only helps the rare case where the cache isn't released fast
    /// enough and a game stutters while memory is nearly full.
    /// </summary>
    public static bool PurgeStandbyList()
    {
        NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege");
        var cmd = NativeMethods.MemoryPurgeStandbyList;
        var status = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int));
        if (status != 0) AppLog.Warn($"Standby purge failed: 0x{status:X8}");
        return status == 0;
    }
}
