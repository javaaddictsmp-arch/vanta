using System.Management;
using System.Text.RegularExpressions;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Driver inventory and update checks. Vanta never downloads drivers itself: it flags old or
/// generic drivers and links to the official vendor page, lists winget app upgrades you can
/// install one by one, and reads pending Windows Updates (installing those stays in Windows Update).
/// </summary>
public sealed class DriverService
{
    /// <summary>Device classes that matter for gaming; the rest of the hundreds of drivers is noise.</summary>
    private static readonly Dictionary<string, string> KeyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DISPLAY"] = "Graphics",
        ["NET"] = "Network",
        ["MEDIA"] = "Audio",
        ["AUDIOENDPOINT"] = "Audio",
        ["HDC"] = "Storage controller",
        ["SCSIADAPTER"] = "Storage controller",
        ["BLUETOOTH"] = "Bluetooth",
        ["USB"] = "USB controller",
        ["SYSTEM"] = "Chipset / system"
    };

    public Task<List<DriverInfo>> ListAsync(SystemSummary s) => Task.Run(() =>
    {
        var raw = new List<(string Name, string Class, string Provider, string Version, DateTime? Date, string Inf)>();
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver"))
        {
            foreach (ManagementObject o in searcher.Get())
            {
                var cls = o["DeviceClass"]?.ToString() ?? "";
                var name = o["DeviceName"]?.ToString() ?? "";
                if (!KeyClasses.ContainsKey(cls) || string.IsNullOrWhiteSpace(name)) continue;

                DateTime? date = null;
                var d = o["DriverDate"]?.ToString();
                if (!string.IsNullOrEmpty(d)) { try { date = ManagementDateTimeConverter.ToDateTime(d); } catch { /* bad date */ } }

                raw.Add((name, cls.ToUpperInvariant(), o["DriverProviderName"]?.ToString() ?? "", o["DriverVersion"]?.ToString() ?? "", date, o["InfName"]?.ToString() ?? ""));
            }
        }

        var list = new List<DriverInfo>();
        foreach (var r in raw)
        {
            // SYSTEM has hundreds of generic Microsoft entries; keep only vendor chipset drivers.
            if (r.Class == "SYSTEM" && !IsChipsetVendor(r.Provider)) continue;
            if (r.Class is "USB" or "AUDIOENDPOINT" or "HDC" or "SCSIADAPTER" && r.Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;

            var (sev, note, link) = Judge(r.Class, r.Name, r.Provider, r.Date, s);
            list.Add(new DriverInfo
            {
                Device = r.Name, Class = KeyClasses[r.Class], Provider = r.Provider, Version = r.Version,
                Date = r.Date, Inf = r.Inf, Severity = sev, Note = note, Link = link
            });
        }

        // One row per chipset vendor is plenty (AMD/Intel register dozens of "devices").
        var chipset = list.Where(d => d.Class == "Chipset / system").GroupBy(d => d.Provider)
                          .Select(g => g.OrderByDescending(x => x.Date).First()).ToList();
        list.RemoveAll(d => d.Class == "Chipset / system");
        list.AddRange(chipset);

        // Missing chipset driver is worth flagging on its own (power plans, X3D core steering, USB).
        var amd = s.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        var intel = s.CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        if ((amd && !chipset.Any(c => c.Provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || c.Provider.StartsWith("AMD", StringComparison.OrdinalIgnoreCase))) ||
            (intel && !chipset.Any(c => c.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))))
        {
            list.Add(new DriverInfo
            {
                Device = amd ? "AMD chipset driver" : "Intel chipset / Management Engine drivers",
                Class = "Chipset / system", Provider = "not installed", Version = "-",
                Severity = Severity.Warning,
                Note = amd ? "Install AMD's chipset driver: it brings the Ryzen power plan support and, on X3D CPUs, the driver that steers games to the right cores."
                           : "Install Intel's chipset and ME drivers from your board's support page.",
                Link = amd ? "https://www.amd.com/en/support/download/drivers.html" : "https://www.intel.com/content/www/us/en/support/detect.html"
            });
        }

        return list.OrderByDescending(d => d.Severity == Severity.Problem)
                   .ThenByDescending(d => d.Severity == Severity.Warning)
                   .ThenBy(d => d.Class == "Graphics" ? 0 : d.Class == "Chipset / system" ? 1 : 2) // most important first
                   .ThenBy(d => d.Class).ThenBy(d => d.Device).ToList();
    });

    private static bool IsChipsetVendor(string provider) =>
        provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || provider.StartsWith("AMD", StringComparison.OrdinalIgnoreCase) ||
        provider.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    private static (Severity, string, string?) Judge(string cls, string name, string provider, DateTime? date, SystemSummary s)
    {
        var years = date is { } d ? (DateTime.Now - d).TotalDays / 365.0 : 0;
        var generic = provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

        if (cls == "DISPLAY")
        {
            var link = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "https://www.nvidia.com/Download/index.aspx"
                     : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "https://www.amd.com/en/support/download/drivers.html"
                     : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "https://www.intel.com/content/www/us/en/download-center/home.html" : null;
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || generic)
                return (Severity.Problem, "Generic Windows driver: games will run badly. Install the official driver.", link);
            if (years > 1)
                return (Severity.Warning, $"About {years:0.#} years old. New GPU drivers often add performance fixes for recent games.", link);
            if (years > 0.4)
                return (Severity.Info, "A few months old. Updating is optional unless a game you play got a fix.", link);
            return (Severity.Good, "Recent.", link);
        }

        // Bluetooth/USB audio profiles, virtual adapters etc. always use Microsoft's driver and that is correct.
        // Only the generic stand-ins for onboard audio / Ethernet / Wi-Fi chips are worth mentioning.
        if (generic && cls == "MEDIA" && name.Contains("High Definition Audio Device", StringComparison.OrdinalIgnoreCase))
            return (Severity.Info, "Generic Windows audio driver for your onboard sound chip. The board maker's Realtek driver can add features (and fix crackling); optional if sound works.", null);
        if (generic && cls == "NET" && !Regex.IsMatch(name, "Bluetooth|Virtual|WAN Miniport|Kernel Debug|Microsoft|Hyper-V|Wi-Fi Direct", RegexOptions.IgnoreCase))
            return (Severity.Info, "Generic Windows network driver. The vendor driver (Realtek/Intel/Killer, on your board's support page) can be more stable.", null);
        if (generic) return (Severity.Good, "", null);

        if (years > 4)
            return (Severity.Info, $"About {years:0} years old. Fine if it works; update from your board/laptop support page if you have problems.", null);

        return (Severity.Good, "", null);
    }

    /// <summary>Exports every third-party driver package with Windows' own pnputil, for reinstalling later.</summary>
    public async Task<(bool Ok, string Folder, string Output)> BackupAsync(CancellationToken ct)
    {
        var folder = Path.Combine(AppPaths.Backups, "Drivers", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var r = await ProcessRunner.RunAsync("pnputil.exe", $"/export-driver * \"{folder}\"", 15 * 60_000, ct);
        return (r.Ok, folder, r.StdOut.Length > 2000 ? r.StdOut[^2000..] : r.StdOut);
    }

    // =========================================================================================
    // winget
    // =========================================================================================
    public static async Task<(List<UpdateItem> Items, string? Error)> WingetUpgradesAsync(CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("winget.exe",
            "upgrade --accept-source-agreements --disable-interactivity", 180_000, ct);
        if (r.ExitCode == -1 && r.StdErr.Length > 0)
            return (new(), "winget is not available on this PC (install 'App Installer' from the Microsoft Store).");

        var lines = r.StdOut.Replace("\r", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var headerIdx = lines.FindIndex(l => Regex.IsMatch(l, @"^Name\s+Id\s+Version\s+Available"));
        if (headerIdx < 0) return (new(), null); // "No installed package found matching input criteria." etc.

        var header = lines[headerIdx];
        int cId = header.IndexOf("Id", StringComparison.Ordinal), cVer = header.IndexOf("Version", StringComparison.Ordinal),
            cAvail = header.IndexOf("Available", StringComparison.Ordinal), cSrc = header.IndexOf("Source", StringComparison.Ordinal);

        var items = new List<UpdateItem>();
        foreach (var line in lines.Skip(headerIdx + 2))
        {
            if (line.Length < cAvail || Regex.IsMatch(line, @"^\d+ upgrades? available") || line.StartsWith("The following")) break;
            string Col(int from, int to) => from < line.Length ? line[from..Math.Min(to < 0 ? line.Length : to, line.Length)].Trim() : "";
            var id = Col(cId, cVer);
            if (string.IsNullOrEmpty(id) || id.Contains(' ')) continue;
            items.Add(new UpdateItem
            {
                Name = Col(0, cId).TrimEnd('…'), Id = id, Current = Col(cVer, cAvail),
                Available = Col(cAvail, cSrc), Source = "winget"
            });
        }
        return (items, null);
    }

    /// <summary>Upgrades one package through winget (official sources only, user-triggered).</summary>
    public static Task<ProcResult> WingetUpgradeAsync(string id, CancellationToken ct) =>
        ProcessRunner.RunAsync("winget.exe",
            $"upgrade --id \"{id}\" -e --accept-package-agreements --accept-source-agreements --disable-interactivity",
            20 * 60_000, ct);

    // =========================================================================================
    // Windows Update (read-only search through the Windows Update Agent COM API)
    // =========================================================================================
    public static Task<(List<UpdateItem> Items, string? Error)> PendingWindowsUpdatesAsync() => Task.Run(() =>
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type == null) return (new List<UpdateItem>(), (string?)"Windows Update Agent not available.");
            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");

            var items = new List<UpdateItem>();
            foreach (dynamic u in result.Updates)
            {
                string title = u.Title;
                int kind = u.Type; // 1 = software, 2 = driver
                items.Add(new UpdateItem { Name = title, Source = "Windows Update", IsDriver = kind == 2 });
            }
            return (items, (string?)null);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Windows Update search failed: " + ex.Message);
            return (new List<UpdateItem>(), (string?)("Could not check Windows Update: " + ex.Message));
        }
    });
}
