using System.Diagnostics;
using System.Management;
using System.Net;
using System.Text;
using System.Text.Json;
using TuneUp.Models;

namespace TuneUp.Services;

public sealed record StoreApp(string Package, string FullName, string Friendly, string Note, bool Caution);
public sealed record InstalledProgram(string Name, string Publisher, string Version, string InstallDate, long SizeKb, string UninstallString, string? InstallLocation)
{
    public string SizeText => SizeKb > 0 ? CleanerService.FormatSize(SizeKb * 1024) : "";
}
public sealed record Leftover(string Path, long Size);

/// <summary>Debloat (Store apps), uninstaller with leftover scan, maintenance tools and reports.</summary>
public static class PrivacyToolsService
{
    // ---- Store apps ------------------------------------------------------------------------------------
    /// <summary>Apps people commonly remove. Anything not on this list is never offered, so system apps are safe.</summary>
    private static readonly (string Package, string Friendly, string Note, bool Caution)[] Removable =
    {
        ("Clipchamp.Clipchamp", "Clipchamp video editor", "", false),
        ("Microsoft.BingNews", "News", "", false),
        ("Microsoft.BingWeather", "Weather", "", false),
        ("Microsoft.BingSearch", "Bing Search", "", false),
        ("Microsoft.GetHelp", "Get Help", "", false),
        ("Microsoft.Getstarted", "Tips", "", false),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "", false),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", "Doesn't remove Office itself, only the launcher app.", false),
        ("Microsoft.People", "People", "", false),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", "", false),
        ("Microsoft.Todos", "Microsoft To Do", "", false),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", "", false),
        ("Microsoft.WindowsMaps", "Maps", "", false),
        ("Microsoft.ZuneVideo", "Movies & TV", "", false),
        ("Microsoft.ZuneMusic", "Media Player / Groove", "You'll need another media player for music files.", true),
        ("Microsoft.549981C3F5F10", "Cortana", "", false),
        ("Microsoft.Copilot", "Copilot app", "", false),
        ("Microsoft.Windows.DevHome", "Dev Home", "", false),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", "Needed if someone helps you remotely with Quick Assist.", true),
        ("Microsoft.YourPhone", "Phone Link", "Needed to use your phone from the PC.", true),
        ("MicrosoftTeams", "Teams (personal)", "", false),
        ("MSTeams", "Teams (new)", "Keep it if you use Teams for school or work.", true),
        ("Microsoft.OutlookForWindows", "Outlook (new)", "Keep it if it's your mail app.", true),
        ("Microsoft.MicrosoftStickyNotes", "Sticky Notes", "", true),
        ("Microsoft.Windows.Photos", "Photos", "You'll need another photo viewer.", true),
        ("Microsoft.GamingApp", "Xbox app", "Needed for Game Pass PC games. Only remove if you don't use Game Pass.", true),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Removes Win+G overlay and its FPS counter/clips. Game Mode still works.", true),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", "Needed for Windows Mixed Reality headsets.", true),
        ("Microsoft.SkypeApp", "Skype", "", false),
        ("king.com.CandyCrushSaga", "Candy Crush Saga", "", false),
        ("king.com.CandyCrushSodaSaga", "Candy Crush Soda", "", false),
        ("SpotifyAB.SpotifyMusic", "Spotify (Store version)", "", true),
        ("Disney.37853FC22B2CE", "Disney+", "", false),
    };

    public static async Task<List<StoreApp>> StoreAppsAsync()
    {
        var r = await ProcessRunner.PowerShellAsync("Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable } | Select-Object Name, PackageFullName | ConvertTo-Json -Compress", 90_000);
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.StdOut) ? "[]" : r.StdOut.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var e in items) installed[e.GetProperty("Name").GetString() ?? ""] = e.GetProperty("PackageFullName").GetString() ?? "";
        }
        catch (Exception ex) { AppLog.Warn("Appx list parse failed: " + ex.Message); }

        return Removable.Where(x => installed.ContainsKey(x.Package))
                        .Select(x => new StoreApp(x.Package, installed[x.Package], x.Friendly, x.Note, x.Caution))
                        .OrderBy(x => x.Caution).ThenBy(x => x.Friendly).ToList();
    }

    public static async Task<bool> RemoveStoreAppAsync(StoreApp app)
    {
        var r = await ProcessRunner.PowerShellAsync($"Remove-AppxPackage -Package '{app.FullName.Replace("'", "''")}'", 180_000);
        AppLog.Info($"Removed Store app {app.Package}: {r.ExitCode} {r.StdErr}");
        return r.Ok && string.IsNullOrWhiteSpace(r.StdErr);
    }

    // ---- Installed programs ------------------------------------------------------------------------------
    public static List<InstalledProgram> Programs()
    {
        var list = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
                 {
                     ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
                 })
        {
            using var root = RegistryService.OpenBase(hive);
            using var un = root.OpenSubKey(path);
            if (un == null) continue;
            foreach (var sub in un.GetSubKeyNames())
            {
                using var k = un.OpenSubKey(sub);
                var name = k?.GetValue("DisplayName") as string;
                var uninstall = k?.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstall)) continue;
                if (k!.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") != null) continue; // updates/patches
                var size = k.GetValue("EstimatedSize") is int kb ? kb : 0;
                list[name] = new InstalledProgram(name, k.GetValue("Publisher") as string ?? "", k.GetValue("DisplayVersion") as string ?? "",
                    FormatDate(k.GetValue("InstallDate") as string), size, uninstall, (k.GetValue("InstallLocation") as string)?.Trim('"'));
            }
        }
        return list.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FormatDate(string? yyyymmdd) =>
        yyyymmdd is { Length: 8 } ? $"{yyyymmdd[..4]}-{yyyymmdd[4..6]}-{yyyymmdd[6..]}" : "";

    /// <summary>Starts the program's own uninstaller (you see its normal window and choices).</summary>
    public static void StartUninstall(InstalledProgram p)
    {
        var cmd = p.UninstallString.Trim();
        var psi = cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("msiexec.exe", cmd[(cmd.IndexOf(' ') + 1)..].Replace("/I{", "/X{")) { UseShellExecute = true }
            : new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(psi);
        AppLog.Info($"Started uninstaller for {p.Name}");
    }

    /// <summary>Folders the program may have left behind. Only shown; nothing is deleted automatically.</summary>
    public static Task<List<Leftover>> LeftoversAsync(InstalledProgram p) => Task.Run(() =>
    {
        var names = new[] { p.Name, p.Publisher, p.Name.Split(' ')[0] }
            .Where(n => n.Length >= 4).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        var found = new List<string>();
        if (!string.IsNullOrEmpty(p.InstallLocation) && Directory.Exists(p.InstallLocation)) found.Add(p.InstallLocation);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var n in names)
            {
                var candidate = Path.Combine(root, n);
                if (Directory.Exists(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase)) found.Add(candidate);
            }

        var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return found.Select(f =>
        {
            long size = 0;
            try { foreach (var fi in new DirectoryInfo(f).EnumerateFiles("*", opts)) size += fi.Length; } catch { }
            return new Leftover(f, size);
        }).ToList();
    });

    public static void RecycleFolder(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    // ---- Battery / reports ---------------------------------------------------------------------------------
    public static bool HasBattery()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            return s.Get().Count > 0;
        }
        catch { return false; }
    }

    public static async Task<string?> BatteryReportAsync()
    {
        var file = Path.Combine(AppPaths.Root, "battery-report.html");
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/batteryreport /output \"{file}\"", 60_000);
        return r.Ok && File.Exists(file) ? file : null;
    }

    /// <summary>A shareable HTML report (for forums / support). Contains no serial numbers or user names.</summary>
    public static string SystemReportHtml(ScanReport report)
    {
        static string E(string? x) => WebUtility.HtmlEncode(x ?? "");
        var s = report.Summary;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=\"utf-8\"><title>System report</title><style>body{font-family:Segoe UI,Arial;max-width:900px;margin:30px auto;color:#111}" +
                  "table{border-collapse:collapse;width:100%;margin:8px 0 20px}td,th{border-bottom:1px solid #ddd;padding:6px 8px;text-align:left;font-size:14px}h2{margin-top:28px}</style>");
        sb.Append($"<h1>System report</h1><p>Made by Vanta on {report.FinishedAt:yyyy-MM-dd HH:mm}</p><h2>Hardware</h2><table>");
        void Row(string k, string? v) => sb.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");
        Row("CPU", $"{s.CpuName} ({s.CpuCores} cores / {s.CpuThreads} threads)");
        Row("GPU", $"{s.GpuName} (driver {s.GpuDriver})");
        Row("Memory", $"{s.RamTotalGb:0.#} GB {s.RamType} - {s.RamStatus}");
        Row("Motherboard", s.Motherboard);
        Row("BIOS", $"{s.BiosVersion} {s.BiosDate}");
        Row("Windows", $"{s.OsName} (build {s.OsBuild})");
        Row("Display", $"{s.Resolution} @ {s.RefreshCurrentHz} Hz (max {s.RefreshMaxHz} Hz)");
        foreach (var d in s.Storage) Row("Storage", $"{d.Model} - {d.SizeGb:0} GB {d.Type}");
        sb.Append("</table><h2>Recommendations</h2><table><tr><th>Impact</th><th>Item</th><th>Fix</th></tr>");
        foreach (var r in report.Recommendations) sb.Append($"<tr><td>{r.Impact}</td><td>{E(r.Title)}</td><td>{E(r.Fix)}</td></tr>");
        sb.Append("</table><h2>BIOS checks</h2><table>");
        foreach (var b in report.Bios) sb.Append($"<tr><th>{E(b.Title)}</th><td>{E(b.Detected)}</td></tr>");
        sb.Append("</table><h2>Drivers</h2><table><tr><th>Device</th><th>Version</th><th>Date</th><th>Note</th></tr>");
        foreach (var d in report.Drivers) sb.Append($"<tr><td>{E(d.Device)}</td><td>{E(d.Version)}</td><td>{E(d.DateText)}</td><td>{E(d.Note)}</td></tr>");
        sb.Append("</table><h2>Drives</h2><table>");
        foreach (var d in report.Disks) sb.Append($"<tr><th>{E(d.Name)}</th><td>{E(d.Health)} - {E(d.Details)}</td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }

    // ---- Scheduled maintenance -----------------------------------------------------------------------------
    private const string MaintenanceTask = @"Vanta\Weekly maintenance";

    public static async Task<bool> MaintenanceScheduledAsync() =>
        (await ProcessRunner.RunAsync("schtasks.exe", $"/query /tn \"{MaintenanceTask}\"", 15_000)).Ok;

    public static Task<ProcResult> ScheduleMaintenanceAsync() =>
        ProcessRunner.RunAsync("schtasks.exe",
            $"/create /tn \"{MaintenanceTask}\" /tr \"\\\"{Environment.ProcessPath}\\\" --maintenance\" /sc weekly /d SUN /st 12:00 /rl HIGHEST /f", 15_000);

    public static Task<ProcResult> RemoveMaintenanceAsync() =>
        ProcessRunner.RunAsync("schtasks.exe", $"/delete /tn \"{MaintenanceTask}\" /f", 15_000);

    /// <summary>Entry point for "Vanta.exe --maintenance": silent clean of the chosen categories.</summary>
    public static async Task RunMaintenanceAsync(SettingsService settings)
    {
        var cats = CleanerService.Categories().Where(c => settings.Current.MaintenanceCategories.Contains(c.Id) && c.Kind == CleanKind.Files).ToList();
        var cleaner = new CleanerService();
        await cleaner.MeasureAsync(cats, CancellationToken.None);
        var (freed, deleted, _) = await cleaner.CleanAsync(cats, null, CancellationToken.None);
        AppLog.Info($"Weekly maintenance: freed {CleanerService.FormatSize(freed)} ({deleted} files)");
    }

    // ---- Compatibility helper --------------------------------------------------------------------------------
    public static readonly (string Flag, string Label)[] CompatFlags =
    {
        ("RUNASADMIN", "Always run as administrator"),
        ("DISABLEDXMAXIMIZEDWINDOWEDMODE", "Disable fullscreen optimizations"),
        ("HIGHDPIAWARE", "Fix blurry/tiny old games on high-DPI screens (application scaling)"),
        ("WIN7RTM", "Windows 7 compatibility mode"),
        ("WIN8RTM", "Windows 8 compatibility mode"),
        ("WINXPSP3", "Windows XP (SP3) compatibility mode"),
        ("16BITCOLOR", "Reduced colour mode (16-bit), for very old games"),
        ("640X480", "Run in 640 x 480, for very old games"),
    };

    public static TweakDefinition CompatTweak(string exe, IEnumerable<string> flags) => new()
    {
        Id = "compat:" + exe.ToLowerInvariant(),
        Name = $"Compatibility settings for {Path.GetFileName(exe)}",
        Category = "Compatibility",
        Simple = "The same options as the Compatibility tab in the EXE's Properties.",
        Description = "Writes Windows' AppCompat layer flags for this program: " + string.Join(", ", flags),
        Risk = RiskLevel.Safe,
        Evidence = EvidenceRating.Situational,
        Operations =
        {
            new TweakOperation { Hive = "HKCU", Path = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                                 Name = exe, ValueType = "String", ApplyValue = "~ " + string.Join(" ", flags), DefaultValue = null }
        }
    };
}
