using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Runs every check (hardware, BIOS, drivers, drives, Windows settings) and turns the results
/// into one list sorted by real-world impact, each with an honest evidence rating and a
/// direct way to fix it. Nothing is changed by scanning.
/// </summary>
public sealed class ScanService
{
    private readonly FirmwareService _firmware = new();
    private readonly DriverService _drivers = new();
    private readonly DiskHealthService _disks = new();

    public DriverService Drivers => _drivers;
    public ScanReport? Last { get; private set; }

    public async Task<ScanReport> RunAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Reading your hardware...");
        var summary = await AppServices.SystemInfo.ScanAsync();

        progress?.Report("Checking BIOS / firmware settings...");
        var bios = await _firmware.ScanAsync(summary);

        progress?.Report("Checking drivers...");
        List<DriverInfo> drivers;
        try { drivers = await _drivers.ListAsync(summary); }
        catch (Exception ex) { AppLog.Error("Driver scan failed", ex); drivers = new(); }

        progress?.Report("Checking drive health...");
        var disks = await _disks.ScanAsync();

        progress?.Report("Checking Windows settings...");
        var activePlan = (await AppServices.Power.ListAsync()).FirstOrDefault(p => p.Active)?.Name ?? "";

        var recs = Build(summary, bios, drivers, disks, activePlan);
        Last = new ScanReport { Summary = summary, Bios = bios, Drivers = drivers, Disks = disks, Recommendations = recs };
        AppLog.Info($"System scan: {recs.Count} recommendations");
        return Last;
    }

    private static List<Recommendation> Build(SystemSummary s, List<BiosCheck> bios, List<DriverInfo> drivers,
                                              List<DiskHealth> disks, string activePlan)
    {
        var recs = new List<Recommendation>();

        // ---- BIOS ----------------------------------------------------------------------------
        foreach (var b in bios.Where(b => b.Severity is Severity.Warning or Severity.Problem))
        {
            var impact = b.Title.StartsWith("Memory profile") || b.Title.Contains("13th/14th") || b.Title.Contains("Intel Arc")
                ? Impact.High
                : b.Title.StartsWith("Resizable") || b.Title.StartsWith("Smart Access") ? Impact.Medium : Impact.Low;
            recs.Add(new Recommendation
            {
                Category = "BIOS", Title = b.Title, Detail = b.Detected + ". " + b.Why, Fix = b.HowTo,
                Impact = impact, Evidence = b.Evidence, Severity = b.Severity, Action = "tab:bios", ActionLabel = "Open BIOS guide"
            });
        }

        // ---- Hardware / settings from the health check (skip the XMP item: BIOS covers it) ---------
        var health = AppServices.Health.Evaluate(s, AppServices.Hub.Latest, AppServices.Settings.Current);
        foreach (var h in health.Items.Where(h => h.Points < 0 && !h.Message.Contains("rated for")))
        {
            var (impact, evidence) = h.Category switch
            {
                "Display" => (Impact.High, EvidenceRating.Proven),
                "Thermals" => (Impact.High, EvidenceRating.Proven),
                "Memory" when h.Message.Contains("single-channel") => (Impact.Medium, EvidenceRating.Proven),
                "Storage" => (Impact.Medium, EvidenceRating.Situational),
                _ => (Impact.Low, EvidenceRating.Situational)
            };
            recs.Add(new Recommendation
            {
                Category = h.Category, Title = h.Message, Fix = h.Fix, Impact = impact, Evidence = evidence, Severity = h.Severity
            });
        }

        // ---- Drivers -----------------------------------------------------------------------------
        foreach (var d in drivers.Where(d => d.Severity is Severity.Warning or Severity.Problem))
        {
            var gpu = d.Class == "Graphics";
            recs.Add(new Recommendation
            {
                Category = "Drivers",
                Title = gpu ? (d.Severity == Severity.Problem ? "Graphics driver is a generic Windows driver" : "Graphics driver is out of date")
                            : $"{d.Device}: {d.Provider}",
                Detail = d.Note,
                Fix = d.Link != null ? "Download from the official page (button below), then restart." : "See the Drivers tab.",
                Impact = gpu && d.Severity == Severity.Problem ? Impact.High : Impact.Medium,
                Evidence = gpu ? EvidenceRating.Proven : EvidenceRating.Situational,
                Severity = d.Severity,
                Action = d.Link != null ? "url:" + d.Link : "tab:drivers",
                ActionLabel = d.Link != null ? "Official download page" : "Open drivers"
            });
        }

        // ---- Drives ------------------------------------------------------------------------------
        foreach (var d in disks.Where(d => d.Severity is Severity.Warning or Severity.Problem))
            recs.Add(new Recommendation
            {
                Category = "Storage", Title = $"{d.Name}: {d.Health}", Detail = d.Details, Fix = d.Verdict,
                Impact = d.Severity == Severity.Problem ? Impact.High : Impact.Medium,
                Evidence = EvidenceRating.Proven, Severity = d.Severity, Action = "tab:health", ActionLabel = "Open drive health"
            });
        if (disks.Any(d => d.Type == "HDD") && !disks.All(d => d.Type == "HDD"))
            recs.Add(new Recommendation
            {
                Category = "Storage", Title = "You have a hard drive (HDD) as well as an SSD",
                Detail = "Games installed on the hard drive load slower and can stutter while streaming new areas.",
                Fix = "Keep the games you play most on the SSD (Steam/Epic can move installs without re-downloading).",
                Impact = Impact.Low, Evidence = EvidenceRating.Proven, Severity = Severity.Info
            });

        // ---- Windows settings --------------------------------------------------------------------
        var pending = AppServices.Catalog.Tweaks
            .Where(t => t.QuickOptimize && t.Risk == RiskLevel.Safe && t.Evidence == EvidenceRating.Proven)
            .Where(TweakEngine.IsApplicable)
            .Where(t => !AppServices.Engine.IsApplied(t))
            .ToList();
        if (pending.Count > 0)
            recs.Add(new Recommendation
            {
                Category = "Windows", Title = $"{pending.Count} recommended tweak(s) not applied yet",
                Detail = string.Join(", ", pending.Select(t => t.Name)),
                Fix = "'Optimize now' applies only safe, proven tweaks, shows you the list first, and can be undone.",
                Impact = Impact.Medium, Evidence = EvidenceRating.Proven, Severity = Severity.Warning,
                Action = "optimize", ActionLabel = "Optimize now"
            });

        if (activePlan.Contains("saver", StringComparison.OrdinalIgnoreCase))
            recs.Add(new Recommendation
            {
                Category = "Windows", Title = $"Power plan is '{activePlan}'",
                Detail = "Power saver limits CPU speed, which directly lowers FPS.",
                Fix = "Switch to Balanced or High performance, or let the Game Booster switch plans while you play.",
                Impact = Impact.High, Evidence = EvidenceRating.Proven, Severity = Severity.Warning,
                Action = "nav:FPS Boost Center", ActionLabel = "Open CPU & Power"
            });

        if (!AppServices.Settings.Current.BoosterEnabled)
            recs.Add(new Recommendation
            {
                Category = "Windows", Title = "Auto-boost is off",
                Detail = "The Game Booster only runs when you click 'Boost now'.",
                Fix = "Turn on 'Auto-boost when a game starts' in FPS Boost Center.",
                Impact = Impact.Low, Evidence = EvidenceRating.Situational, Severity = Severity.Info,
                Action = "nav:FPS Boost Center", ActionLabel = "Open Game Booster"
            });

        if (recs.Count == 0)
            recs.Add(new Recommendation
            {
                Category = "All good", Title = "Nothing important to fix",
                Detail = "Your hardware, drivers and settings look right. Further gains come from in-game settings (see Game Configs).",
                Impact = Impact.Low, Evidence = EvidenceRating.Proven, Severity = Severity.Good
            });

        return recs.OrderBy(r => r.Impact).ThenByDescending(r => r.Severity == Severity.Problem).ToList();
    }
}
