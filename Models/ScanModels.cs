namespace TuneUp.Models;

/// <summary>One line of the BIOS / firmware checklist.</summary>
public sealed class BiosCheck
{
    public string Title { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Info;
    /// <summary>What Vanta detected ("Enabled", "3200 of 3600 MHz", "Can't be read from Windows").</summary>
    public string Detected { get; init; } = "";
    /// <summary>Plain-English reason this matters.</summary>
    public string Why { get; init; } = "";
    /// <summary>Where to find it in the BIOS, tailored to the board vendor when known.</summary>
    public string HowTo { get; init; } = "";
    public EvidenceRating Evidence { get; init; } = EvidenceRating.Situational;
}

public sealed class DriverInfo
{
    public string Device { get; init; } = "";
    public string Class { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string Inf { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Good;
    public string Note { get; init; } = "";
    public string? Link { get; init; }

    public string DateText => Date?.ToString("yyyy-MM-dd") ?? "unknown";
    public string AgeText => Date is { } d ? AgeOf(d) : "";

    private static string AgeOf(DateTime d)
    {
        var months = (int)((DateTime.Now - d).TotalDays / 30.4);
        return months < 1 ? "this month" : months < 12 ? $"{months} mo old" : $"{months / 12.0:0.#} yr old";
    }
}

public sealed class UpdateItem
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Current { get; init; } = "";
    public string Available { get; init; } = "";
    /// <summary>"winget" or "Windows Update".</summary>
    public string Source { get; init; } = "";
    public bool IsDriver { get; init; }
}

public sealed class DiskHealth
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public double SizeGb { get; init; }
    public string Health { get; init; } = "Unknown";
    /// <summary>Percentage of rated endurance used (SSDs), null when not reported.</summary>
    public int? WearPercent { get; init; }
    public int? TemperatureC { get; init; }
    public int? TemperatureMaxC { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrors { get; init; }
    public long? WriteErrors { get; init; }
    public Severity Severity { get; init; } = Severity.Info;
    public string Verdict { get; init; } = "";

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (WearPercent is { } w) parts.Add($"{w}% of rated life used");
            if (TemperatureC is { } t) parts.Add($"{t}°C now" + (TemperatureMaxC is { } tm and > 0 ? $" (max {tm}°C)" : ""));
            if (PowerOnHours is { } h) parts.Add($"{h:N0} hours powered on ({h / 24.0 / 365:0.#} years)");
            if (ReadErrors is > 0 || WriteErrors is > 0) parts.Add($"{ReadErrors ?? 0} read / {WriteErrors ?? 0} write errors");
            return parts.Count == 0 ? "Detailed health counters need admin rights or are not reported by this drive." : string.Join("  ·  ", parts);
        }
    }
}

public enum Impact { High, Medium, Low }

/// <summary>One item in the System Scan's ranked list.</summary>
public sealed class Recommendation
{
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Fix { get; init; } = "";
    public Impact Impact { get; init; } = Impact.Medium;
    public EvidenceRating Evidence { get; init; } = EvidenceRating.Situational;
    public Severity Severity { get; init; } = Severity.Warning;
    /// <summary>Where the "Fix it" button goes: "optimize", "tab:bios", "tab:drivers", "tab:health", "nav:Tweaks", "url:..."</summary>
    public string? Action { get; init; }
    public string ActionLabel { get; init; } = "";
}

public sealed class ScanReport
{
    public DateTime FinishedAt { get; init; } = DateTime.Now;
    public SystemSummary Summary { get; init; } = new();
    public List<BiosCheck> Bios { get; init; } = new();
    public List<DriverInfo> Drivers { get; init; } = new();
    public List<DiskHealth> Disks { get; init; } = new();
    public List<Recommendation> Recommendations { get; init; } = new();
}
