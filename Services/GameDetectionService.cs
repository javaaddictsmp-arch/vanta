using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Finds installed games by reading each launcher's own install records (no internet, no
/// account). A game is matched at runtime by "any EXE under its install folder", so we don't
/// need to know the exact game EXE in advance (handy for games with launcher stubs).
/// </summary>
public sealed class GameDetectionService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private List<GameEntry> _games = new();

    /// <summary>Manual games plus remembered per-game data (last EXE), persisted in games.json.</summary>
    private List<GameEntry> _saved = new();

    public event EventHandler? Changed;

    public IReadOnlyList<GameEntry> Games
    {
        get { lock (_gate) return _games.ToList(); }
    }

    public GameEntry? Find(string id)
    {
        lock (_gate) return _games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public void LoadSaved()
    {
        try
        {
            if (File.Exists(AppPaths.GamesFile))
                _saved = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(AppPaths.GamesFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read games.json", ex);
            _saved = new();
        }

        // Show manual games straight away, before the (slower) launcher scan.
        lock (_gate) _games = _saved.Where(g => g.Launcher == "Manual").ToList();
    }

    public Task ScanAsync() => Task.Run(Scan);

    private void Scan()
    {
        var found = new List<GameEntry>();
        Safe("Steam", () => found.AddRange(ScanSteam()));
        Safe("Epic", () => found.AddRange(ScanEpic()));
        Safe("GOG", () => found.AddRange(ScanGog()));
        Safe("Ubisoft", () => found.AddRange(ScanUbisoft()));
        Safe("Uninstall", () => found.AddRange(ScanUninstallPublishers()));
        Safe("Xbox", () => found.AddRange(ScanXbox()));
        found.AddRange(KnownProcessGames());

        // De-duplicate by install folder (e.g. a game listed by both Steam and Uninstall).
        var unique = new List<GameEntry>();
        foreach (var g in found)
        {
            if (g.InstallDir != null &&
                unique.Any(u => u.InstallDir != null && SamePath(u.InstallDir, g.InstallDir))) continue;
            if (unique.Any(u => u.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase))) continue;
            unique.Add(g);
        }

        foreach (var g in unique)
        {
            g.AntiCheat ??= DetectAntiCheat(g);
            var saved = _saved.FirstOrDefault(s => s.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase));
            if (saved?.LastExePath != null) g.LastExePath = saved.LastExePath;
        }

        unique.AddRange(_saved.Where(s => s.Launcher == "Manual" &&
                                          !unique.Any(u => u.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase))));

        lock (_gate) _games = unique.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AppLog.Info($"Game scan: {unique.Count} games");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public GameEntry AddManual(string exePath)
    {
        var g = new GameEntry
        {
            Id = "manual:" + exePath.ToLowerInvariant(),
            Name = Path.GetFileNameWithoutExtension(exePath),
            Launcher = "Manual",
            Executables = { exePath },
            LastExePath = exePath
        };
        g.AntiCheat = DetectAntiCheat(new GameEntry { InstallDir = Path.GetDirectoryName(exePath) });

        lock (_gate)
        {
            _games.RemoveAll(x => x.Id == g.Id);
            _games.Add(g);
            _games = _games.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        Remember(g);
        Changed?.Invoke(this, EventArgs.Empty);
        return g;
    }

    public void Remove(GameEntry g)
    {
        lock (_gate) _games.RemoveAll(x => x.Id == g.Id);
        _saved.RemoveAll(x => x.Id == g.Id);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores data we want to keep between runs (manual entries, last EXE).</summary>
    public void Remember(GameEntry g)
    {
        lock (_gate)
        {
            _saved.RemoveAll(x => x.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase));
            _saved.Add(g);
        }
        Save();
    }

    private void Save()
    {
        try
        {
            lock (_gate) File.WriteAllText(AppPaths.GamesFile, JsonSerializer.Serialize(_saved, Json));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save games.json", ex);
        }
    }

    // =========================================================================================
    // Launchers
    // =========================================================================================
    public static string? SteamPath()
    {
        // HKCU\Software\Valve\Steam\SteamPath uses forward slashes.
        var v = RegistryService.Read("HKCU", @"Software\Valve\Steam", "SteamPath");
        if (v.Exists && v.Value != null) return v.Value.Replace('/', '\\');
        var lm = RegistryService.Read("HKLM", @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        return lm.Exists ? lm.Value : null;
    }

    private static readonly Regex VdfPath = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> SteamNonGames = new() { "228980", "1070560", "1391110", "1628350", "1493710", "431960" };

    private static IEnumerable<GameEntry> ScanSteam()
    {
        var steam = SteamPath();
        if (steam == null || !Directory.Exists(steam)) yield break;

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath.Matches(File.ReadAllText(vdf)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;

            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }

                var appId = AcfValue(text, "appid");
                var name = AcfValue(text, "name");
                var dir = AcfValue(text, "installdir");
                if (appId == null || name == null || dir == null || SteamNonGames.Contains(appId)) continue;
                if (name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase)) continue;

                var full = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(full)) continue;

                yield return new GameEntry { Id = "steam:" + appId, Name = name, Launcher = "Steam", InstallDir = full };
            }
        }
    }

    private static string? AcfValue(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static IEnumerable<GameEntry> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            GameEntry? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var loc = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                var app = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;

                if (name != null && loc != null && !incomplete && Directory.Exists(loc) &&
                    !name.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase))
                    g = new GameEntry { Id = "epic:" + (app ?? name), Name = name, Launcher = "Epic", InstallDir = loc };
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Epic manifest {item}: {ex.Message}");
            }
            if (g != null) yield return g;
        }
    }

    private static IEnumerable<GameEntry> ScanGog()
    {
        using var lm = RegistryService.OpenBase("HKLM");
        using var games = lm.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games == null) yield break;

        foreach (var id in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(id);
            var name = k?.GetValue("gameName") as string;
            var path = k?.GetValue("path") as string;
            if (name == null || path == null || !Directory.Exists(path)) continue;
            yield return new GameEntry { Id = "gog:" + id, Name = name, Launcher = "GOG", InstallDir = path };
        }
    }

    private static IEnumerable<GameEntry> ScanUbisoft()
    {
        using var lm = RegistryService.OpenBase("HKLM");
        using var installs = lm.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs == null) yield break;

        foreach (var id in installs.GetSubKeyNames())
        {
            using var k = installs.OpenSubKey(id);
            var dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (dir == null || !Directory.Exists(dir)) continue;
            yield return new GameEntry { Id = "ubisoft:" + id, Name = Path.GetFileName(dir), Launcher = "Ubisoft", InstallDir = dir };
        }
    }

    /// <summary>Battle.net, EA and Riot games register normal uninstall entries with their publisher name.</summary>
    private static IEnumerable<GameEntry> ScanUninstallPublishers()
    {
        var publishers = new (string Publisher, string Launcher, string[] Skip)[]
        {
            ("Blizzard Entertainment", "Battle.net", new[] { "Battle.net" }),
            ("Electronic Arts", "EA", new[] { "EA app", "Origin", "EA Desktop" }),
            ("Riot Games", "Riot", new[] { "Riot Vanguard", "Riot Client" }),
        };

        var results = new List<GameEntry>();
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var lm = RegistryService.OpenBase("HKLM");
            using var un = lm.OpenSubKey(root);
            if (un == null) continue;

            foreach (var sub in un.GetSubKeyNames())
            {
                using var k = un.OpenSubKey(sub);
                var pub = k?.GetValue("Publisher") as string;
                var name = k?.GetValue("DisplayName") as string;
                var loc = (k?.GetValue("InstallLocation") as string)?.Trim('"').TrimEnd('\\');
                if (pub == null || name == null || string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) continue;

                var match = publishers.FirstOrDefault(p => pub.Contains(p.Publisher, StringComparison.OrdinalIgnoreCase));
                if (match.Publisher == null) continue;
                if (match.Skip.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

                results.Add(new GameEntry
                {
                    Id = $"{match.Launcher.ToLowerInvariant()}:{sub}",
                    Name = name,
                    Launcher = match.Launcher,
                    InstallDir = loc
                });
            }
        }
        return results;
    }

    /// <summary>Xbox app / Game Pass PC games install to X:\XboxGames\Name\Content by default.</summary>
    private static IEnumerable<GameEntry> ScanXbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("GameSave", StringComparison.OrdinalIgnoreCase)) continue;
                var content = Path.Combine(dir, "Content");
                yield return new GameEntry
                {
                    Id = "xbox:" + name.ToLowerInvariant(),
                    Name = name,
                    Launcher = "Xbox",
                    InstallDir = Directory.Exists(content) ? content : dir
                };
            }
        }
    }

    /// <summary>Games identified by process name instead of folder.</summary>
    private static IEnumerable<GameEntry> KnownProcessGames()
    {
        var mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (Directory.Exists(mc))
            yield return new GameEntry
            {
                Id = "minecraft:java",
                Name = "Minecraft: Java Edition",
                Launcher = "Minecraft",
                Executables = { "javaw.exe" },
                WindowTitleContains = "Minecraft"
            };

        var bedrock = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe");
        if (Directory.Exists(bedrock))
            yield return new GameEntry
            {
                Id = "minecraft:bedrock",
                Name = "Minecraft (Bedrock)",
                Launcher = "Minecraft",
                Executables = { "Minecraft.Windows.exe" }
            };
    }

    // =========================================================================================
    private static string? DetectAntiCheat(GameEntry g)
    {
        if (g.Id.Contains("valorant", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("VALORANT", StringComparison.OrdinalIgnoreCase)) return "Riot Vanguard";
        if (g.Name.Contains("League of Legends", StringComparison.OrdinalIgnoreCase)) return "Riot Vanguard";
        if (g.Name.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase)) return "Ricochet";
        if (g.InstallDir == null || !Directory.Exists(g.InstallDir)) return null;

        try
        {
            var dirs = Directory.EnumerateDirectories(g.InstallDir)
                .Concat(Directory.EnumerateDirectories(g.InstallDir).SelectMany(SafeSubDirs))
                .Select(Path.GetFileName)
                .ToList();

            if (dirs.Any(d => d!.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))) return "EasyAntiCheat";
            if (dirs.Any(d => d!.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))) return "BattlEye";
            if (dirs.Any(d => d!.Contains("EAAntiCheat", StringComparison.OrdinalIgnoreCase))) return "EA Javelin";
            if (Directory.EnumerateFiles(g.InstallDir, "EasyAntiCheat*", SearchOption.TopDirectoryOnly).Any()) return "EasyAntiCheat";
        }
        catch
        {
            // unreadable folder: unknown
        }
        return null;
    }

    private static IEnumerable<string> SafeSubDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string file, string dir)
    {
        var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return Path.GetFullPath(file).StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    private static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLog.Error($"Game scan ({what}) failed", ex); }
    }
}
