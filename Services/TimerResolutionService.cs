using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TuneUp.Services;

public sealed record TimerInfo(double CoarsestMs, double FinestMs, double CurrentMs);

public sealed record SleepBenchmark(double RequestedMs, double ResolutionMs, double AvgMs, double P99Ms, double MaxMs)
{
    public double AvgErrorMs => AvgMs - RequestedMs;
}

public sealed record TimerRequest(string Process, double PeriodMs);

/// <summary>
/// Timer resolution, honestly:
///  - Windows 10 2004 and later: a process's timer request only affects that process. So a
///    tool like this changing it "globally" no longer works on its own.
///  - Windows 11 / Server 2022: setting HKLM\...\Session Manager\kernel\GlobalTimerResolutionRequests=1
///    (catalog tweak "global-timer-resolution", needs a reboot) brings back the old global behaviour.
///  - Many games already request 1 ms themselves.
/// The built-in benchmark measures Sleep(1) accuracy so you can see whether it changes anything.
/// </summary>
public sealed class TimerResolutionService
{
    private readonly object _gate = new();
    private uint _held; // resolution we currently hold, 0 = none

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    public static bool IsPerProcessBehavior => Environment.OSVersion.Version.Build >= 19041;

    public static bool GlobalRequestsEnabled
    {
        get
        {
            var v = RegistryService.Read("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests");
            return v.Exists && v.Value == "1";
        }
    }

    public TimerInfo Query()
    {
        NativeMethods.NtQueryTimerResolution(out var min, out var max, out var cur);
        // "Minimum" resolution is the coarsest (largest) value, "maximum" the finest.
        return new TimerInfo(min / 10_000.0, max / 10_000.0, cur / 10_000.0);
    }

    public bool IsHeld => _held != 0;
    public double HeldMs => _held / 10_000.0;

    /// <summary>Requests a resolution (100 ns units). Released automatically if Vanta exits or crashes.</summary>
    public double Request(uint hundredNs)
    {
        lock (_gate)
        {
            if (_held != 0) NativeMethods.NtSetTimerResolution(_held, false, out _);
            var status = NativeMethods.NtSetTimerResolution(hundredNs, true, out var actual);
            _held = status == 0 ? hundredNs : 0;
            if (status != 0) AppLog.Warn($"NtSetTimerResolution failed: 0x{status:X8}");
            else AppLog.Info($"Timer resolution requested {hundredNs / 10_000.0} ms, now {actual / 10_000.0} ms");
            return actual / 10_000.0;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_held == 0) return;
            NativeMethods.NtSetTimerResolution(_held, false, out _);
            AppLog.Info("Timer resolution released");
            _held = 0;
        }
    }

    /// <summary>Measures how long Sleep(1) really takes, at the current resolution.</summary>
    public static SleepBenchmark MeasureSleep(int iterations, double resolutionMs, CancellationToken ct)
    {
        var samples = new double[iterations];
        var sw = new Stopwatch();
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            sw.Restart();
            Thread.Sleep(1);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return new SleepBenchmark(1, resolutionMs, samples.Average(), samples[(int)(iterations * 0.99) - 1], samples[^1]);
    }

    /// <summary>Runs the Sleep(1) test at the current setting, then while holding the given resolution.</summary>
    public Task<(SleepBenchmark Before, SleepBenchmark After)> BenchmarkAsync(uint hundredNs, CancellationToken ct) =>
        Task.Run(() =>
        {
            var wasHeld = _held;
            if (wasHeld != 0) Release();

            var before = MeasureSleep(300, Query().CurrentMs, ct);
            var actual = Request(hundredNs);
            try
            {
                var after = MeasureSleep(300, actual, ct);
                return (before, after);
            }
            finally
            {
                Release();
                if (wasHeld != 0) Request(wasHeld);
            }
        }, ct);

    /// <summary>
    /// Asks Windows which programs are currently requesting a finer timer, using a short
    /// powercfg /energy trace (the same report Microsoft documents for this).
    /// </summary>
    public static async Task<List<TimerRequest>> FindRequestersAsync(CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"vanta-energy-{Guid.NewGuid():N}.html");
        try
        {
            await ProcessRunner.RunAsync("powercfg.exe", $"/energy /duration 5 /output \"{file}\"", 90_000, ct);
            if (!File.Exists(file)) return new();

            var text = Regex.Replace(await File.ReadAllTextAsync(file, ct), "<[^>]+>", "\n");
            text = System.Net.WebUtility.HtmlDecode(text);

            // Each block: "Platform Timer Resolution:Outstanding Timer Request" ... "Requested Period  10000"
            // ... "Requesting Process Path  \Device\HarddiskVolume3\...\game.exe"
            var list = new List<TimerRequest>();
            foreach (Match m in Regex.Matches(text,
                         @"Requested Period\s+(\d+).*?Requesting Process (?:Path|Image Name)\s+([^\n]+)",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var period = int.Parse(m.Groups[1].Value) / 10_000.0;
                var proc = Path.GetFileName(m.Groups[2].Value.Trim());
                list.Add(new TimerRequest(proc, period));
            }
            return list.DistinctBy(x => (x.Process, x.PeriodMs)).ToList();
        }
        finally
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
    }
}
