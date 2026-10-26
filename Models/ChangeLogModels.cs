namespace TuneUp.Models;

/// <summary>The value of one registry item before Vanta changed it.</summary>
public sealed class OpSnapshot
{
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>False when the value did not exist; undo then deletes it.</summary>
    public bool Existed { get; set; }

    /// <summary>RegistryValueKind name of the previous value.</summary>
    public string? PreviousKind { get; set; }
    public string? PreviousValue { get; set; }

    /// <summary>Set when this snapshot belongs to a service start type.</summary>
    public string? ServiceName { get; set; }
}

public sealed class ChangeLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Apply, Revert, RevertAll</summary>
    public string Action { get; set; } = "Apply";

    public string TweakId { get; set; } = "";
    public string TweakName { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Folder holding .reg exports taken before this change.</summary>
    public string? BackupFolder { get; set; }

    public List<OpSnapshot> Snapshots { get; set; } = new();

    /// <summary>True once this Apply entry has been undone.</summary>
    public bool Undone { get; set; }
}

public sealed class EngineResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool RebootRequired { get; init; }
    public bool SignOutRequired { get; init; }

    public static EngineResult Ok(string message, bool reboot = false, bool signOut = false) =>
        new() { Success = true, Message = message, RebootRequired = reboot, SignOutRequired = signOut };

    public static EngineResult Fail(string message) => new() { Success = false, Message = message };
}
