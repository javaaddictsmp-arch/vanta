using System.Text.Json;

namespace TuneUp.Services;

public sealed class AppSettings
{
    public string AccentColor { get; set; } = "#7C5CFF";
    public bool CreateRestorePoint { get; set; } = true;
    public int CpuTempAlertC { get; set; } = 85;
    public int GpuTempAlertC { get; set; } = 83;
    public int SampleIntervalMs { get; set; } = 1000;

    // ---- Phase 2: Game Booster ----
    /// <summary>Master switch: auto-activate boosts when a detected game starts.</summary>
    public bool BoosterEnabled { get; set; } = true;
    /// <summary>A background process above this share of total CPU for 2 s gets restrained.</summary>
    public double ProBalanceThresholdPercent { get; set; } = 10;
    public List<string> ProBalanceExclusions { get; set; } = new() { "obs64", "obs32", "Discord", "steam", "steamwebhelper" };
    /// <summary>Idle saver: after a game, switch to Balanced instead of whatever plan was active before.</summary>
    public bool IdleSaverBalanced { get; set; }
    public PerformancePlanSettings PlanSettings { get; set; } = new();

    // ---- Phase 4 ----
    public RamCleanerSettings RamCleaner { get; set; } = new();
    /// <summary>Category ids the weekly maintenance task cleans.</summary>
    public List<string> MaintenanceCategories { get; set; } = new() { "temp", "wu", "do", "dumps" };
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool HotkeyEnabled { get; set; } = true;
    /// <summary>Boost now / stop hotkey, e.g. "Ctrl+Alt+F10". Recorded in Settings.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F10";
    /// <summary>Setup wizard has been completed (or skipped).</summary>
    public bool FirstRunDone { get; set; }
    /// <summary>Simple mode hides expert pages and tabs.</summary>
    public bool SimpleMode { get; set; }
    /// <summary>Record a report for every game session.</summary>
    public bool SessionReports { get; set; } = true;
    /// <summary>Automatically follow the running game's server in Network &amp; Ping.</summary>
    public bool FollowGameServer { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Previous DNS servers per adapter (empty list = DHCP), so DNS changes can be undone.</summary>
    public Dictionary<string, List<string>> SavedDns { get; set; } = new();
}

/// <summary>RAM cleaner (Cleaning &amp; RAM > Memory) options.</summary>
public sealed class RamCleanerSettings
{
    public bool Auto { get; set; }
    /// <summary>"GameStart" or "NeedsCleaning".</summary>
    public string Mode { get; set; } = "NeedsCleaning";
    /// <summary>For "NeedsCleaning": clean at most this often.</summary>
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>"Needs cleaning" when this much RAM is in use.</summary>
    public int ThresholdPercent { get; set; } = 80;
    /// <summary>Process names (without .exe) that are never trimmed.</summary>
    public List<string> AllowList { get; set; } = new() { "Discord", "Medal", "MedalEncoder", "Everything", "obs64", "Spotify" };
    public DateTime? LastCleanupUtc { get; set; }

    public bool TrimBackground { get; set; } = true;
    /// <summary>Also trim apps that have an open window (off: apps you're using stay untouched).</summary>
    public bool IncludeOpenApps { get; set; }
    public bool FlushModified { get; set; } = true;
    public bool PurgeStandby { get; set; } = true;
    public bool CombinePages { get; set; }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
                if (loaded != null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read settings, using defaults", ex);
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, Json));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save settings", ex);
        }
    }
}
