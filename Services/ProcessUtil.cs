using System.Diagnostics;
using System.Text;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Safe helpers for touching other processes. Everything here goes through normal Windows
/// APIs: nothing is injected, and if Windows (or an anti-cheat driver) refuses access we just
/// skip that process.
/// </summary>
public static class ProcessUtil
{
    /// <summary>Never suspended, closed, deprioritized or re-pinned, whatever a profile says.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "audiodg", "conhost", "dllhost",
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "smartscreen", "MemCompression",
        "spoolsv", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "LogonUI", "WUDFHost", "dasHost", "Secure System", "vmmem", "vmcompute",
        // anti-cheat services and clients: touching these can get you kicked
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "BattlEye", "vgc", "vgtray",
        "FACEIT", "FaceitService", "faceitclient", "EAAntiCheat.GameServiceLauncher", "EAAntiCheat.GameService",
        "PnkBstrA", "PnkBstrB", "nProtect", "GameGuard", "xigncode", "mhyprot2", "atvi-randgrid_sr", "Ricochet",
        // our own processes
        "Vanta"
    };

    public static bool IsProtected(string processName) => Protected.Contains(processName);

    /// <summary>
    /// Never memory-trimmed by the RAM cleaner. Much shorter than <see cref="Protected"/>: trimming only
    /// moves memory a process isn't using right now out of RAM (nothing is paused or closed), so
    /// Windows' own background processes are fine to trim. Excluded: anti-cheat (can get you kicked),
    /// the audio engine (trimming it can crackle) and VM memory (WSL / Hyper-V manage their own).
    /// </summary>
    private static readonly HashSet<string> NeverTrim = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "audiodg", "vmmem", "vmmemWSL", "Vanta",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "BattlEye", "vgc", "vgtray",
        "FACEIT", "FaceitService", "faceitclient", "EAAntiCheat.GameServiceLauncher", "EAAntiCheat.GameService",
        "PnkBstrA", "PnkBstrB", "nProtect", "GameGuard", "xigncode", "mhyprot2", "atvi-randgrid_sr", "Ricochet"
    };

    public static bool IsNeverTrim(string processName) => NeverTrim.Contains(processName);

    public static string? GetImagePath(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>Process start time as UTC ticks, 0 if unreadable.</summary>
    public static long GetStartTicks(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return NativeMethods.GetProcessTimes(h, out var creation, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(creation).Ticks
                : 0;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>True when the PID still belongs to the same process we recorded.</summary>
    public static bool IsSameProcess(TrackedProcess t)
    {
        var ticks = GetStartTicks(t.Pid);
        return ticks != 0 && (t.StartTicks == 0 || Math.Abs(ticks - t.StartTicks) < TimeSpan.TicksPerSecond);
    }

    public static TrackedProcess Track(Process p) => new()
    {
        Pid = p.Id,
        StartTicks = GetStartTicks(p.Id),
        Name = SafeName(p),
        Path = GetImagePath(p.Id)
    };

    public static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    public static bool Suspend(int pid) => SuspendResume(pid, suspend: true);
    public static bool Resume(int pid) => SuspendResume(pid, suspend: false);

    private static bool SuspendResume(int pid, bool suspend)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            return (suspend ? NativeMethods.NtSuspendProcess(h) : NativeMethods.NtResumeProcess(h)) == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>Sets priority, refusing Realtime. Returns the previous priority, or null if not allowed.</summary>
    public static ProcessPriorityClass? TrySetPriority(Process p, ProcessPriorityClass priority)
    {
        if (priority == ProcessPriorityClass.RealTime) priority = ProcessPriorityClass.High;
        try
        {
            var old = p.PriorityClass;
            if (old != priority) p.PriorityClass = priority;
            return old;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Priority change refused for {SafeName(p)} ({p.Id}): {ex.Message}");
            return null;
        }
    }

    public static bool TrySetAffinity(Process p, ulong mask)
    {
        if (mask == 0) return false;
        try
        {
            p.ProcessorAffinity = (IntPtr)(long)mask;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Affinity change refused for {SafeName(p)} ({p.Id}): {ex.Message}");
            return false;
        }
    }

    public static ProcessPriorityClass? ParsePriority(string? s) =>
        Enum.TryParse<ProcessPriorityClass>(s, true, out var p) ? p : null;
}
