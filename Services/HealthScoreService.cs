using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Explainable health score: starts at 100, every deduction is listed with the reason and the
/// fix. No made-up "error counts" and no scare wording. Checks that pass are listed too.
/// </summary>
public sealed class HealthScoreService
{
    public HealthReport Evaluate(SystemSummary sys, MetricSample? live, AppSettings settings)
    {
        var items = new List<HealthItem>();

        // ---- Memory speed (XMP/DOCP/EXPO) -------------------------------------------------
        if (sys.RamRatedMhz > 0 && sys.RamConfiguredMhz > 0)
        {
            if (sys.RamConfiguredMhz + 50 < sys.RamRatedMhz)
                items.Add(Bad("Memory", -10, Severity.Warning,
                    $"RAM runs at {sys.RamConfiguredMhz} MHz but is rated for {sys.RamRatedMhz} MHz.",
                    "Enable XMP / DOCP / EXPO in your BIOS. On Ryzen this is one of the biggest free gains."));
            else
                items.Add(Good("Memory", $"RAM runs at its rated speed ({sys.RamConfiguredMhz} MHz)."));
        }

        if (sys.RamModules == 1)
            items.Add(Bad("Memory", -5, Severity.Warning,
                "Only one RAM module detected, so memory runs in single-channel mode.",
                "Adding a matching second stick (dual-channel) noticeably helps CPU-bound games."));
        else if (sys.RamModules >= 2)
            items.Add(Good("Memory", $"{sys.RamModules} RAM modules installed (dual-channel capable)."));

        // ---- Display refresh rate ----------------------------------------------------------
        if (sys.RefreshMaxHz > 0 && sys.RefreshCurrentHz > 0)
        {
            if (sys.RefreshCurrentHz + 5 < sys.RefreshMaxHz)
                items.Add(Bad("Display", -10, Severity.Warning,
                    $"Monitor is running at {sys.RefreshCurrentHz} Hz but supports {sys.RefreshMaxHz} Hz at this resolution.",
                    "Windows Settings > Display > Advanced display > Choose a refresh rate."));
            else
                items.Add(Good("Display", $"Monitor refresh rate is {sys.RefreshCurrentHz} Hz (max for this resolution)."));
        }

        // ---- System drive space -------------------------------------------------------------
        if (sys.SystemDriveFreePercent > 0)
        {
            if (sys.SystemDriveFreePercent < 10)
                items.Add(Bad("Storage", -10, Severity.Problem,
                    $"System drive has only {sys.SystemDriveFreePercent:0}% free ({sys.SystemDriveFreeGb:0} GB).",
                    "Free up space or move games to another drive. SSDs slow down when nearly full."));
            else if (sys.SystemDriveFreePercent < 20)
                items.Add(Bad("Storage", -4, Severity.Warning,
                    $"System drive is getting full: {sys.SystemDriveFreePercent:0}% free ({sys.SystemDriveFreeGb:0} GB).",
                    "Aim to keep 15-20% free, especially on SSDs."));
            else
                items.Add(Good("Storage", $"System drive has {sys.SystemDriveFreePercent:0}% free."));
        }

        // ---- Live checks (only when sensors report) --------------------------------------
        if (live != null)
        {
            if (live.CpuTempC is { } ct)
            {
                if (ct >= 95)
                    items.Add(Bad("Thermals", -20, Severity.Problem,
                        $"CPU is at {ct:0} C, which is at or near its thermal limit.",
                        "Check the cooler mount, thermal paste and case airflow."));
                else if (ct >= settings.CpuTempAlertC)
                    items.Add(Bad("Thermals", -8, Severity.Warning,
                        $"CPU is hot right now: {ct:0} C.",
                        "Better case airflow or a stronger cooler helps sustain boost clocks."));
                else
                    items.Add(Good("Thermals", $"CPU temperature is fine ({ct:0} C)."));
            }

            if (live.GpuTempC is { } gt)
            {
                if (gt >= settings.GpuTempAlertC)
                    items.Add(Bad("Thermals", -8, Severity.Warning,
                        $"GPU is hot right now: {gt:0} C.",
                        "Use a custom fan curve in MSI Afterburner and check case airflow."));
                else
                    items.Add(Good("Thermals", $"GPU temperature is fine ({gt:0} C)."));
            }

            if (live.RamPercent >= 90)
                items.Add(Bad("Memory", -10, Severity.Warning,
                    $"RAM is {live.RamPercent:0}% full right now.",
                    "Close memory-heavy apps (browsers, launchers) before gaming."));
        }

        var score = Math.Clamp(100 + items.Sum(i => i.Points), 0, 100);
        return new HealthReport { Score = score, Items = items };
    }

    private static HealthItem Bad(string cat, int points, Severity sev, string msg, string fix) =>
        new() { Category = cat, Points = points, Severity = sev, Message = msg, Fix = fix };

    private static HealthItem Good(string cat, string msg) =>
        new() { Category = cat, Points = 0, Severity = Severity.Good, Message = msg, Fix = "" };
}
