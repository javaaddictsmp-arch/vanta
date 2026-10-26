using System.Diagnostics;
using System.Globalization;
using TuneUp.Models;

namespace TuneUp.Services;

public sealed record DlssDll(string Game, string File, string Version, string Kind);

public sealed class NvidiaInfo
{
    public string Name { get; init; } = "";
    public string Driver { get; init; } = "";
    public double? PowerDrawW { get; init; }
    public double? PowerLimitW { get; init; }
    public double? PowerMaxW { get; init; }
    public int? Clock { get; init; }
    public int? MaxClock { get; init; }
    public int? PcieGen { get; init; }
    public int? PcieGenMax { get; init; }
    public int? PcieWidth { get; init; }
    public string ReBar { get; init; } = "Unknown";
}

/// <summary>
/// GPU and display helpers. Per-EXE settings are "runtime tweaks" so they go through the same
/// backup/change-log/undo pipeline as everything else.
/// </summary>
public sealed class GpuDisplayService
{
    // HKCU\Software\Microsoft\DirectX\UserGpuPreferences: value name = full EXE path,
    // data "GpuPreference=2;" = High performance (what Settings > System > Display > Graphics writes).
    private const string GpuPrefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    // AppCompat layers: "~ DISABLEDXMAXIMIZEDWINDOWEDMODE" is the "Disable fullscreen optimizations"
    // checkbox on the EXE's Properties > Compatibility tab.
    private const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static TweakDefinition GpuPreferenceTweak(string exe) => new()
    {
        Id = "gpu-pref:" + exe.ToLowerInvariant(),
        Name = $"High-performance GPU for {Path.GetFileName(exe)}",
        Category = "GPU & Display",
        Simple = "Makes sure this game uses your real graphics card instead of the weaker one built into the processor.",
        Description = "Tells Windows to run this game on the high-performance GPU (same as Settings > Display > Graphics).",
        Risk = RiskLevel.Safe,
        Evidence = EvidenceRating.Situational,
        EvidenceNote = "Matters on laptops and PCs with both integrated and dedicated graphics. On a desktop with one GPU it changes nothing.",
        Operations =
        {
            new TweakOperation { Hive = "HKCU", Path = GpuPrefPath, Name = exe, ValueType = "String", ApplyValue = "GpuPreference=2;", DefaultValue = null }
        }
    };

    public static TweakDefinition FsoTweak(string exe)
    {
        // Keep other compatibility flags (e.g. RUNASADMIN) that are already set for this EXE.
        var current = RegistryService.Read("HKCU", LayersPath, exe);
        var flags = (current.Value ?? "").Replace("~", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!flags.Contains(FsoFlag, StringComparer.OrdinalIgnoreCase)) flags.Add(FsoFlag);

        return new TweakDefinition
        {
            Id = "fso-exe:" + exe.ToLowerInvariant(),
            Name = $"Disable fullscreen optimizations for {Path.GetFileName(exe)}",
            Category = "GPU & Display",
            Simple = "An old fix that can reduce input lag or alt-tab problems in some older games.",
            Description = "Same as ticking 'Disable fullscreen optimizations' in the EXE's Compatibility tab.",
            Risk = RiskLevel.Safe,
            Evidence = EvidenceRating.Situational,
            EvidenceNote = "Helped some older DX9/DX11 games with input lag or alt-tab issues. On current Windows 11 builds most games are better left with optimizations on.",
            Operations =
            {
                new TweakOperation { Hive = "HKCU", Path = LayersPath, Name = exe, ValueType = "String",
                                     ApplyValue = "~ " + string.Join(" ", flags), DefaultValue = null }
            }
        };
    }

    /// <summary>Candidate main EXEs in a game folder: biggest non-helper EXEs first.</summary>
    public static List<string> CandidateExes(GameEntry g)
    {
        var list = new List<string>();
        if (g.LastExePath != null && File.Exists(g.LastExePath)) list.Add(g.LastExePath);
        list.AddRange(g.Executables.Where(e => e.Contains('\\') && File.Exists(e)));

        if (g.InstallDir != null && Directory.Exists(g.InstallDir))
        {
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
                list.AddRange(Directory.EnumerateFiles(g.InstallDir, "*.exe", opts)
                    .Where(f => !GameWatcherService.IsHelper(Path.GetFileName(f)))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .Take(6));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"EXE scan of {g.InstallDir}: {ex.Message}");
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Scans game folders for NVIDIA DLSS DLLs and reads their versions.</summary>
    public Task<List<DlssDll>> ScanDlssAsync(IEnumerable<GameEntry> games, CancellationToken ct) => Task.Run(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nvngx_dlss.dll"] = "DLSS Super Resolution",
            ["nvngx_dlssg.dll"] = "DLSS Frame Generation",
            ["nvngx_dlssd.dll"] = "DLSS Ray Reconstruction"
        };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
        var found = new List<DlssDll>();

        foreach (var g in games.Where(x => x.InstallDir != null && Directory.Exists(x.InstallDir)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var f in Directory.EnumerateFiles(g.InstallDir!, "nvngx_dlss*.dll", opts))
                {
                    var kind = names.TryGetValue(Path.GetFileName(f), out var k) ? k : Path.GetFileName(f);
                    var ver = FileVersionInfo.GetVersionInfo(f).FileVersion?.Replace(',', '.').Replace(" ", "") ?? "?";
                    found.Add(new DlssDll(g.Name, f, ver, kind));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"DLSS scan of {g.Name}: {ex.Message}");
            }
        }
        return found;
    }, ct);

    /// <summary>Reads NVIDIA GPU details with nvidia-smi (ships with the driver).</summary>
    public async Task<NvidiaInfo?> ReadNvidiaAsync()
    {
        var smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(smi)) smi = "nvidia-smi.exe";

        var q = await ProcessRunner.RunAsync(smi,
            "--query-gpu=name,driver_version,power.draw,power.limit,power.max_limit,clocks.gr,clocks.max.gr,pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,memory.total " +
            "--format=csv,noheader,nounits", 15_000);
        if (!q.Ok || string.IsNullOrWhiteSpace(q.StdOut)) return null;

        var f = q.StdOut.Split('\n')[0].Split(',').Select(x => x.Trim()).ToArray();
        double? D(int i) => i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        int? I(int i) => D(i) is { } v ? (int)v : null;

        // Resizable BAR: with ReBAR on, the BAR1 aperture is about the size of VRAM; without it, 256 MB.
        // NVIDIA only supports it on RTX 30-series and newer (plus RTX workstation cards).
        var name = f.ElementAtOrDefault(0) ?? "";
        var rebarCapable = System.Text.RegularExpressions.Regex.IsMatch(name, @"RTX\s*(3\d|4\d|5\d|A\d|PRO)\d*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var rebar = rebarCapable ? "Unknown" : "Not supported by this GPU (NVIDIA needs an RTX 30-series or newer). Nothing to enable.";
        var mem = await ProcessRunner.RunAsync(smi, "-q -d MEMORY", 15_000);
        var bar = System.Text.RegularExpressions.Regex.Match(mem.StdOut, @"BAR1 Memory Usage\s*\r?\n\s*Total\s*:\s*(\d+)\s*MiB");
        if (rebarCapable && bar.Success && D(10) is { } vram)
        {
            var bar1 = double.Parse(bar.Groups[1].Value, CultureInfo.InvariantCulture);
            rebar = bar1 >= vram * 0.9 ? "Enabled" : bar1 <= 512 ? "Disabled (enable 'Above 4G Decoding' and 'Re-Size BAR' in BIOS)" : $"Partial ({bar1:0} MiB)";
        }

        return new NvidiaInfo
        {
            Name = f.ElementAtOrDefault(0) ?? "",
            Driver = f.ElementAtOrDefault(1) ?? "",
            PowerDrawW = D(2),
            PowerLimitW = D(3),
            PowerMaxW = D(4),
            Clock = I(5),
            MaxClock = I(6),
            PcieGen = I(7),
            PcieGenMax = I(8),
            PcieWidth = I(9),
            ReBar = rebar
        };
    }

    /// <summary>
    /// FPS cap suggestion for VRR (G-Sync/FreeSync): stay a few frames under refresh so frames never
    /// hit the V-Sync ceiling. refresh - refresh²/3600 is the formula NVIDIA Reflex itself uses.
    /// </summary>
    public static int VrrCap(int refreshHz) =>
        refreshHz <= 0 ? 0 : (int)Math.Floor(refreshHz - refreshHz * (double)refreshHz / 3600.0);
}
