using System.Diagnostics;
using System.Text.RegularExpressions;
using TuneUp.Models;

namespace TuneUp.Services;

public enum StartupSource { RegistryUser, RegistryMachine, RegistryMachine32, FolderUser, FolderCommon, Task }

public sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Publisher { get; init; } = "";
    public StartupSource Source { get; init; }
    public string Location { get; init; } = "";
    public bool Enabled { get; set; }
    public string Advice { get; init; } = "";
    /// <summary>0 = usually safe to disable, 1 = your call, 2 = keep.</summary>
    public int AdviceLevel { get; init; } = 1;
    /// <summary>Scheduled task path (Task source only).</summary>
    public string? TaskPath { get; init; }
}

/// <summary>
/// Startup apps from the Run keys, Startup folders and logon scheduled tasks. Registry and
/// folder items are disabled exactly like Task Manager does it (the StartupApproved flags), so
/// nothing is deleted and every change goes through the backup / Change Log pipeline.
/// </summary>
public static class StartupService
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static readonly (string Match, int Level, string Advice)[] Known =
    {
        ("SecurityHealth", 2, "Windows Security tray icon. Keep."),
        ("Malwarebytes", 2, "Security software. Keep."),
        ("RtkAud|Realtek|RAVCpl", 2, "Audio driver helper. Keep, or some sound features stop working."),
        ("NVIDIA|NvBackend|nvcontainer", 2, "Part of the graphics driver. Keep."),
        ("AMD|Radeon", 2, "Part of the graphics/chipset software. Keep."),
        ("igfx|Intel", 2, "Intel driver helper. Keep."),
        ("Logi|Razer|Corsair|iCUE|NZXT|HyperX|NGENUITY|Glorious|SteelSeries|Wooting", 1, "Device software (lighting, macros, DPI). Keep it if you use those features; otherwise safe to disable."),
        ("Steam", 0, "Opens Steam when you log in. Safe to disable: open Steam when you want to play."),
        ("EpicGames", 0, "Opens the Epic launcher at login. Safe to disable."),
        ("Battle\\.net", 0, "Opens Battle.net at login. Safe to disable."),
        ("EADesktop|EA app|Origin", 0, "Opens the EA app at login. Safe to disable."),
        ("Ubisoft|UplayLauncher", 0, "Opens Ubisoft Connect at login. Safe to disable."),
        ("Discord", 0, "Starts Discord at login. Safe to disable if you don't need it the moment the PC starts."),
        ("Spotify", 0, "Starts Spotify at login. Safe to disable."),
        ("Teams|Skype|Zoom", 0, "Chat/meeting app at login. Safe to disable unless you need it for work."),
        ("OneDrive", 1, "Keeps your OneDrive files in sync. Disable only if you don't use OneDrive."),
        ("Overwolf|Medal|Outplayed", 0, "Game overlay/clipping app. Safe to disable; it also uses CPU and memory while gaming."),
        ("Opera|Chrome|Edge|Brave|Firefox", 0, "Browser 'startup boost' that preloads it at login. Safe to disable."),
        ("Adobe|AdobeGC|CCXProcess|Creative Cloud", 0, "Adobe background updater/sync. Safe to disable; Creative Cloud still works when you open it."),
        ("CCleaner|Wallpaper", 1, "Convenience app. Your call."),
        ("ExitLag|WTFast|Voicemod|MorphVOX", 1, "Starts at login for convenience. Safe to disable if you open it when needed."),
    };

    public static List<StartupItem> List()
    {
        var list = new List<StartupItem>();
        ReadRun(list, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryUser, "Run");
        ReadRun(list, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryMachine, "Run");
        ReadRun(list, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryMachine32, "Run32");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.FolderUser, "HKCU");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.FolderCommon, "HKLM");
        try { ReadTasks(list); } catch (Exception ex) { AppLog.Warn("Reading logon tasks failed: " + ex.Message); }
        return list.OrderBy(i => i.AdviceLevel).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupItem> list, string hive, string path, StartupSource src, string approvedKey)
    {
        using var root = RegistryService.OpenBase(hive);
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            var cmd = key.GetValue(name)?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            list.Add(Make(name, cmd, src, $"{hive}\\{path}", IsApprovedEnabled(hive, approvedKey, name)));
        }
    }

    private static void ReadFolder(List<StartupItem> list, string folder, StartupSource src, string hive)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            list.Add(Make(Path.GetFileName(f), f, src, folder, IsApprovedEnabled(hive, "StartupFolder", Path.GetFileName(f))));
    }

    /// <summary>Logon/boot tasks that aren't Microsoft's own (Task Scheduler COM API).</summary>
    private static void ReadTasks(List<StartupItem> list)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) return;
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        void Walk(dynamic folder)
        {
            string path = folder.Path;
            if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
            foreach (dynamic t in folder.GetTasks(1)) // 1 = include hidden
            {
                try
                {
                    var logon = false;
                    foreach (dynamic trig in t.Definition.Triggers) { int tt = trig.Type; if (tt is 8 or 9) logon = true; } // 8 boot, 9 logon
                    if (!logon) continue;
                    string cmd = "";
                    foreach (dynamic a in t.Definition.Actions) { try { cmd = a.Path + " " + a.Arguments; } catch { } break; }
                    string name = t.Name;
                    if (name.StartsWith("Vanta", StringComparison.OrdinalIgnoreCase)) continue;
                    var item = Make(name, cmd.Trim(), StartupSource.Task, "Task Scheduler" + path, (bool)t.Enabled);
                    list.Add(new StartupItem
                    {
                        Name = item.Name, Command = item.Command, Publisher = item.Publisher, Source = StartupSource.Task, Location = item.Location,
                        Enabled = item.Enabled, Advice = item.Advice, AdviceLevel = item.AdviceLevel, TaskPath = (string)t.Path
                    });
                }
                catch { /* unreadable task */ }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub);
        }
        Walk(svc.GetFolder("\\"));
    }

    private static StartupItem Make(string name, string cmd, StartupSource src, string location, bool enabled)
    {
        var (level, advice) = Advise(name + " " + cmd);
        return new StartupItem
        {
            Name = name, Command = cmd, Source = src, Location = location, Enabled = enabled,
            Publisher = PublisherOf(cmd), AdviceLevel = level, Advice = advice
        };
    }

    private static (int, string) Advise(string text)
    {
        foreach (var (match, level, advice) in Known)
            if (Regex.IsMatch(text, match, RegexOptions.IgnoreCase)) return (level, advice);
        return (1, "Not in Vanta's list. Look it up before disabling; disabling is harmless to try and easy to undo.");
    }

    private static string PublisherOf(string cmd)
    {
        try
        {
            var m = Regex.Match(cmd, "^\"([^\"]+)\"|^(\\S+\\.exe)", RegexOptions.IgnoreCase);
            var exe = Environment.ExpandEnvironmentVariables(m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : cmd);
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>StartupApproved: first byte even (02/06) or no value = enabled; odd (03/07) = disabled.</summary>
    private static bool IsApprovedEnabled(string hive, string sub, string name)
    {
        var v = RegistryService.Read(hive, Approved + sub, name);
        if (!v.Exists || v.Value == null) return true;
        try { return (Convert.FromBase64String(v.Value)[0] & 1) == 0; } catch { return true; }
    }

    /// <summary>A runtime tweak that flips the StartupApproved flag (logged + undoable like any tweak).</summary>
    public static TweakDefinition? ToggleTweak(StartupItem item, bool enable)
    {
        var (hive, sub) = item.Source switch
        {
            StartupSource.RegistryUser => ("HKCU", "Run"),
            StartupSource.RegistryMachine => ("HKLM", "Run"),
            StartupSource.RegistryMachine32 => ("HKLM", "Run32"),
            StartupSource.FolderUser => ("HKCU", "StartupFolder"),
            StartupSource.FolderCommon => ("HKLM", "StartupFolder"),
            _ => ("", "")
        };
        if (hive == "") return null;
        var bytes = new byte[12];
        bytes[0] = enable ? (byte)2 : (byte)3;
        return new TweakDefinition
        {
            Id = $"startup:{(enable ? "on" : "off")}:{hive}:{sub}:{item.Name}".ToLowerInvariant(),
            Name = $"{(enable ? "Enable" : "Disable")} startup app: {item.Name}",
            Category = "Startup",
            Risk = RiskLevel.Safe,
            Evidence = EvidenceRating.Proven,
            Description = "Same switch as Task Manager's Startup tab. The app itself is untouched.",
            Operations = { new TweakOperation { Hive = hive, Path = Approved + sub, Name = item.Name, ValueType = "Binary", ApplyValue = Convert.ToBase64String(bytes), DefaultValue = null } }
        };
    }

    public static bool SetTaskEnabled(string taskPath, bool enabled)
    {
        try
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            svc.Connect();
            var folderPath = taskPath[..Math.Max(1, taskPath.LastIndexOf('\\'))];
            dynamic task = svc.GetFolder(folderPath).GetTask(taskPath[(taskPath.LastIndexOf('\\') + 1)..]);
            task.Enabled = enabled;
            AppLog.Info($"Task {taskPath} enabled={enabled}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not change task {taskPath}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>One service on Vanta's curated list (nothing outside this list is ever offered).</summary>
public sealed record ServiceInfo(string Name, string Display, string What, string Target, string[] Presets, string Warning);

public static class ServicePresetService
{
    public const string Gaming = "Gaming", Work = "Work", Minimal = "Minimal";

    /// <summary>
    /// Curated: only services that are safe to switch off for most people. Critical services
    /// (networking, audio, updates, security, display, Bluetooth, installers) are deliberately absent.
    /// </summary>
    public static readonly ServiceInfo[] Catalog =
    {
        new("DiagTrack", "Connected User Experiences and Telemetry", "Sends usage and diagnostic data to Microsoft.", "Disabled", new[] { Gaming, Work, Minimal }, ""),
        new("MapsBroker", "Downloaded Maps Manager", "Updates offline maps for the Windows Maps app.", "Disabled", new[] { Gaming, Work, Minimal }, "Offline maps in the Maps app stop updating."),
        new("RetailDemo", "Retail Demo Service", "Only used for shop display PCs.", "Disabled", new[] { Gaming, Work, Minimal }, ""),
        new("wisvc", "Windows Insider Service", "Only needed if you test Windows Insider builds.", "Disabled", new[] { Gaming, Work, Minimal }, "Don't disable if you are a Windows Insider."),
        new("Fax", "Fax", "Sending and receiving faxes.", "Disabled", new[] { Gaming, Minimal }, ""),
        new("WMPNetworkSvc", "Windows Media Player Network Sharing", "Shares your media library over the network.", "Disabled", new[] { Gaming, Work, Minimal }, ""),
        new("SEMgrSvc", "Payments and NFC/SE Manager", "Tap-to-pay with NFC (rare on desktops).", "Disabled", new[] { Gaming, Minimal }, ""),
        new("PhoneSvc", "Phone Service", "Telephony features for Phone Link calls.", "Manual", new[] { Minimal }, "Calls through Phone Link may stop working."),
        new("lfsvc", "Geolocation Service", "Gives apps your location.", "Manual", new[] { Minimal }, "Weather, Maps and Find My Device lose location."),
        new("TrkWks", "Distributed Link Tracking Client", "Keeps shortcuts working when files move between network drives.", "Manual", new[] { Minimal }, ""),
        new("XblAuthManager", "Xbox Live Auth Manager", "Signs in to Xbox services.", "Manual", new[] { Work, Minimal }, "Needed for Game Pass and Xbox games: keep it in the Gaming preset."),
        new("XblGameSave", "Xbox Live Game Save", "Cloud saves for Xbox/Game Pass games.", "Manual", new[] { Work, Minimal }, "Needed for Game Pass cloud saves."),
        new("XboxNetApiSvc", "Xbox Live Networking Service", "Networking for Xbox games and party chat.", "Manual", new[] { Work, Minimal }, "Needed for Xbox multiplayer."),
        new("Spooler", "Print Spooler", "Printing.", "Manual", new[] { Minimal }, "You can't print until it's turned back on."),
        new("WbioSrvc", "Windows Biometric Service", "Fingerprint and face sign-in (Windows Hello).", "Manual", new[] { Minimal }, "Windows Hello fingerprint/face sign-in stops working."),
        new("WSearch", "Windows Search (indexing)", "Indexes files so Start and Explorer searches are instant.", "Manual", new[] { Minimal }, "Searching for files in Start and Explorer becomes slow."),
    };

    public static string CurrentStartType(string service)
    {
        var v = RegistryService.Read("HKLM", $@"SYSTEM\CurrentControlSet\Services\{service}", "Start");
        if (!v.Exists || v.Value == null) return "Not installed";
        return v.Value switch { "2" => "Automatic", "3" => "Manual", "4" => "Disabled", "0" => "Boot", "1" => "System", _ => v.Value };
    }

    public static TweakDefinition Tweak(ServiceInfo s) => new()
    {
        Id = $"svc:{s.Name}:{s.Target}".ToLowerInvariant(),
        Name = $"{s.Display}: set to {s.Target}",
        Category = "Services",
        Simple = s.What,
        Description = $"Sets the '{s.Display}' service ({s.Name}) to {s.Target}. Undo puts back its previous start type.",
        Risk = string.IsNullOrEmpty(s.Warning) ? RiskLevel.Safe : RiskLevel.Moderate,
        Evidence = EvidenceRating.Marginal,
        EvidenceNote = "Turning services off rarely changes FPS on its own; the benefit is less background activity and data sharing.",
        Warning = s.Warning,
        Operations = { new TweakOperation { Kind = "service", Name = s.Name, ApplyValue = s.Target, DefaultValue = "Manual" } }
    };
}

public sealed class ProcessRow
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public double Cpu { get; init; }
    public double MemoryMb { get; init; }
    public string Priority { get; init; } = "";
    public bool Protected { get; init; }
    public string? Path { get; init; }
}

/// <summary>Process list with CPU% measured over a short interval.</summary>
public sealed class ProcessManagerService
{
    private Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastTime = DateTime.MinValue;

    public List<ProcessRow> Snapshot()
    {
        var now = DateTime.UtcNow;
        var wall = (now - _lastTime).TotalMilliseconds * Environment.ProcessorCount;
        var cpuNow = new Dictionary<int, TimeSpan>();
        var rows = new List<ProcessRow>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                double cpu = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    cpuNow[p.Id] = t;
                    if (_lastCpu.TryGetValue(p.Id, out var prev) && wall > 0) cpu = (t - prev).TotalMilliseconds / wall * 100;
                }
                catch { /* denied */ }

                string prio;
                try { prio = p.PriorityClass.ToString(); } catch { prio = "-"; }
                long mem;
                try { mem = p.PrivateMemorySize64; } catch { mem = 0; }
                var name = ProcessUtil.SafeName(p);
                rows.Add(new ProcessRow
                {
                    Pid = p.Id, Name = name, Cpu = Math.Max(0, cpu), MemoryMb = mem / 1048576.0, Priority = prio,
                    Protected = ProcessUtil.IsProtected(name)
                });
            }
        }
        _lastCpu = cpuNow;
        _lastTime = now;
        return rows.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.MemoryMb).ToList();
    }

    /// <summary>I/O priority: 0 very low, 1 low, 2 normal. Background apps at low I/O priority stop hogging the disk.</summary>
    public static bool SetIoPriority(int pid, int level)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return NativeMethods.NtSetInformationProcess(h, NativeMethods.ProcessIoPriority, ref level, sizeof(int)) == 0; }
        finally { NativeMethods.CloseHandle(h); }
    }
}
