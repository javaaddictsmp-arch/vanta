using System.Text.Json.Serialization;

namespace TuneUp.Models;

/// <summary>A game Vanta found (or you added by hand).</summary>
public sealed class GameEntry
{
    /// <summary>Stable id, e.g. "steam:730", "epic:Fortnite", "manual:c:\games\x.exe".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Steam, Epic, GOG, Xbox, Battle.net, EA, Ubisoft, Riot, Minecraft, Manual.</summary>
    public string Launcher { get; set; } = "";

    /// <summary>Any process whose EXE lives under this folder counts as the game (helpers excluded).</summary>
    public string? InstallDir { get; set; }

    /// <summary>Exact EXE paths (manual games) or bare EXE names (e.g. "javaw.exe").</summary>
    public List<string> Executables { get; set; } = new();

    /// <summary>Only for shared hosts like javaw.exe: the window title must contain this.</summary>
    public string? WindowTitleContains { get; set; }

    /// <summary>EasyAntiCheat, BattlEye, Vanguard... (detected from the install folder).</summary>
    public string? AntiCheat { get; set; }

    /// <summary>The main EXE seen the last time the game ran (used for per-EXE GPU/FSO settings).</summary>
    public string? LastExePath { get; set; }

    [JsonIgnore] public bool HasAntiCheat => !string.IsNullOrEmpty(AntiCheat);
}

public enum AffinityPreset
{
    /// <summary>Leave Windows' scheduler alone (recommended for almost everyone).</summary>
    None,
    /// <summary>Intel hybrid: run the game on P-cores only.</summary>
    PerformanceCoresOnly,
    /// <summary>AMD dual-CCD X3D: run the game on the CCD with the extra 3D V-Cache.</summary>
    VCacheCcdOnly,
    /// <summary>AMD dual-CCD: run the game on the CCD without V-Cache / the "frequency" CCD.</summary>
    FrequencyCcdOnly,
    /// <summary>One logical processor per physical core ("SMT/HT off" for this game only).</summary>
    OneThreadPerCore
}

public enum BackgroundAction { Suspend, Close }

public sealed class BackgroundAppRule
{
    /// <summary>Process name without .exe, e.g. "OneDrive", "Discord".</summary>
    public string ProcessName { get; set; } = "";
    public BackgroundAction Action { get; set; } = BackgroundAction.Suspend;
    /// <summary>For Close: start it again when the boost ends.</summary>
    public bool Reopen { get; set; } = true;
}

/// <summary>What the Game Booster does for one game. Every step can be switched off.</summary>
public sealed class BoostProfile
{
    public string GameId { get; set; } = "";
    /// <summary>For readability of exported files only.</summary>
    public string GameName { get; set; } = "";
    public string Notes { get; set; } = "";

    public bool AutoActivate { get; set; } = true;

    // Process
    public bool HighPriority { get; set; } = true;
    public bool DisablePriorityBoost { get; set; }
    public AffinityPreset Affinity { get; set; } = AffinityPreset.None;
    public bool ProBalance { get; set; } = true;

    // Power
    public bool UsePerformancePlan { get; set; } = true;

    // Memory
    public bool TrimStandbyIfLow { get; set; } = true;
    public bool ClearClipboard { get; set; }

    // Input
    public bool BlockWindowsKey { get; set; } = true;
    public bool BlockAccessibilityShortcuts { get; set; } = true;

    // Background
    public bool MuteNotifications { get; set; } = true;
    public bool PauseSearchIndexing { get; set; } = true;
    public bool PauseWindowsUpdate { get; set; }
    public List<BackgroundAppRule> BackgroundApps { get; set; } = new();

    /// <summary>0 = leave alone. 5000 = 0.5 ms, 10000 = 1 ms (100 ns units).</summary>
    public int TimerResolution100ns { get; set; }

    public bool ShowOverlay { get; set; }

    public BoostProfile Clone(string gameId, string gameName)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        var copy = System.Text.Json.JsonSerializer.Deserialize<BoostProfile>(json)!;
        copy.GameId = gameId;
        copy.GameName = gameName;
        return copy;
    }
}

/// <summary>Exported/imported profile file (one profile or a whole library).</summary>
public sealed class ProfileLibraryFile
{
    public string Format { get; set; } = "vanta-profiles/1";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public List<BoostProfile> Profiles { get; set; } = new();
}

/// <summary>A persistent "always do this for that EXE" rule, like Process Lasso.</summary>
public sealed class ProcessRule
{
    public string ProcessName { get; set; } = "";
    /// <summary>null = leave alone. Never Realtime (Vanta refuses it).</summary>
    public string? Priority { get; set; }
    public AffinityPreset Affinity { get; set; } = AffinityPreset.None;
    public bool Enabled { get; set; } = true;
}

// =============================================================================================
// Crash-safe boost state. Written to disk after every step so the watchdog (or the next start
// of Vanta) can put everything back even if Vanta was killed mid-game.
// =============================================================================================
public sealed class TrackedProcess
{
    public int Pid { get; set; }
    /// <summary>Process start time (UTC ticks) so a reused PID is never touched by mistake.</summary>
    public long StartTicks { get; set; }
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public string? OriginalPriority { get; set; }
}

public sealed class BoostState
{
    public bool Active { get; set; }
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public int TuneUpPid { get; set; }

    public string? OriginalPowerPlan { get; set; }
    public List<string> StoppedServices { get; set; } = new();
    public List<TrackedProcess> Suspended { get; set; } = new();
    public List<TrackedProcess> Closed { get; set; } = new();
    /// <summary>Priority changes made by ProBalance or the game priority step.</summary>
    public List<TrackedProcess> PriorityChanges { get; set; } = new();

    public uint? StickyKeysFlags { get; set; }
    public uint? FilterKeysFlags { get; set; }
    public uint? ToggleKeysFlags { get; set; }

    /// <summary>ToastEnabled before muting; -1 = value did not exist.</summary>
    public int? OriginalToastEnabled { get; set; }

    public List<string> StepsApplied { get; set; } = new();
}
