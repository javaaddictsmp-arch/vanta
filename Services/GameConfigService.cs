using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TuneUp.Models;

namespace TuneUp.Services;

public sealed record ConfigApplyResult(bool Success, string Message, int Changed, IReadOnlyList<string> Missing, string? BackupFile);

/// <summary>
/// Edits games' own config files with tested-by-format, data-driven presets. Always backs the
/// file up first, keeps its encoding and line endings, and by default only changes keys that
/// already exist in your file. Editing your own config files is not something anti-cheat cares
/// about; nothing touches the running game.
/// </summary>
public sealed class GameConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public List<GamePresetDefinition> Games { get; private set; } = new();

    public void Load()
    {
        var byId = new Dictionary<string, GamePresetDefinition>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("gamepresets.json");
            if (stream != null)
                foreach (var g in JsonSerializer.Deserialize<GamePresetCatalog>(stream, Options)?.Games ?? new())
                    byId[g.Id] = g;
        }
        catch (Exception ex)
        {
            AppLog.Error("Built-in game preset catalog unreadable", ex);
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.GamePresets, "*.json"))
        {
            try
            {
                foreach (var g in ImportFile(file)) byId[g.Id] = g;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Skipping game preset file {file}", ex);
            }
        }

        Games = byId.Values.OrderBy(g => g.Name).ToList();
    }

    /// <summary>Parses a community preset file (same format as the catalog).</summary>
    public static List<GamePresetDefinition> ImportFile(string file)
    {
        var cat = JsonSerializer.Deserialize<GamePresetCatalog>(File.ReadAllText(file), Options);
        var games = cat?.Games ?? new();
        if (games.Count == 0)
        {
            var single = JsonSerializer.Deserialize<GamePresetDefinition>(File.ReadAllText(file), Options);
            if (single != null && !string.IsNullOrEmpty(single.Id)) games.Add(single);
        }
        return games.Where(g => !string.IsNullOrWhiteSpace(g.Id) && g.Format is "ini" or "kv" or "colon" or "guide").ToList();
    }

    /// <summary>Copies an imported file into the user preset folder so it loads next time too.</summary>
    public int InstallCommunityFile(string file)
    {
        var games = ImportFile(file);
        if (games.Count == 0) throw new InvalidDataException("No valid game presets found in that file.");
        File.Copy(file, Path.Combine(AppPaths.GamePresets, Path.GetFileName(file)), overwrite: true);
        Load();
        return games.Count;
    }

    // ---- Path resolution ---------------------------------------------------------------------
    public static List<string> ResolvePaths(GamePresetDefinition g)
    {
        var results = new List<string>();
        foreach (var pattern in g.Paths)
        {
            var expanded = Expand(pattern);
            if (expanded == null) continue;
            results.AddRange(Glob(expanded));
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderByDescending(File.GetLastWriteTimeUtc) // the most recently used account first
                      .ToList();
    }

    private static string? Expand(string pattern)
    {
        var s = Environment.ExpandEnvironmentVariables(pattern)
            .Replace("{documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            .Replace("{savedgames}", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"));
        if (s.Contains("{steam}"))
        {
            var steam = GameDetectionService.SteamPath();
            if (steam == null) return null;
            s = s.Replace("{steam}", steam);
        }
        return s.Replace('/', '\\');
    }

    /// <summary>Expands "*" folder segments (one level each). The file name itself may not be a wildcard.</summary>
    private static IEnumerable<string> Glob(string path)
    {
        var parts = path.Split('\\');
        IEnumerable<string> current = new[] { parts[0] + "\\" };

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var last = i == parts.Length - 1;
            current = current.SelectMany(dir =>
            {
                if (part == "*")
                {
                    try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : new List<string>(); }
                    catch { return new List<string>(); }
                }
                return new List<string> { Path.Combine(dir, part) };
            }).ToList();

            if (!last) current = current.Where(Directory.Exists).ToList();
        }
        return current.Where(File.Exists);
    }

    // ---- Apply / restore ---------------------------------------------------------------------
    public ConfigApplyResult Apply(GamePresetDefinition g, string presetName, string file)
    {
        if (g.Format == "guide") return new(false, "This game has no editable config file; follow the notes instead.", 0, Array.Empty<string>(), null);
        if (!g.Presets.TryGetValue(presetName, out var values)) return new(false, "Unknown preset.", 0, Array.Empty<string>(), null);
        if (!File.Exists(file)) return new(false, "Config file not found. Launch the game once so it creates it.", 0, Array.Empty<string>(), null);

        var attrs = File.GetAttributes(file);
        var backup = BackupFile(g, file);
        try
        {
            Encoding encoding;
            string text;
            using (var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = reader.ReadToEnd();
                encoding = reader.CurrentEncoding;
            }

            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

            var (changed, missing) = g.Format switch
            {
                "ini" => EditIni(lines, values, g.OnlyExistingKeys),
                "kv" => EditKv(lines, values, g.OnlyExistingKeys),
                "colon" => EditColon(lines, values, g.OnlyExistingKeys),
                _ => (0, new List<string>())
            };

            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            File.WriteAllText(file, string.Join(newline, lines), encoding);
            File.SetAttributes(file, attrs);

            AppLog.Info($"Game config {g.Id} '{presetName}': {changed} values changed, backup {backup}");
            var msg = $"Applied '{presetName}' to {Path.GetFileName(file)}: {changed} setting(s) changed.";
            if (missing.Count > 0) msg += $" {missing.Count} key(s) were not in your file and were left alone.";
            return new(true, msg, changed, missing, backup);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Applying game preset {g.Id} failed", ex);
            try { File.Copy(backup, file, true); File.SetAttributes(file, attrs); } catch { /* best effort */ }
            return new(false, "Could not edit the file (is the game still running?): " + ex.Message, 0, Array.Empty<string>(), backup);
        }
    }

    private static string BackupFile(GamePresetDefinition g, string file)
    {
        var dir = Path.Combine(AppPaths.GameConfigBackups, Safe(g.Id));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(file)}");
        File.Copy(file, dest, overwrite: true);
        File.WriteAllText(dest + ".source", file); // remembers where it came from
        return dest;
    }

    public List<string> Backups(GamePresetDefinition g)
    {
        var dir = Path.Combine(AppPaths.GameConfigBackups, Safe(g.Id));
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".source")).OrderByDescending(f => f).ToList()
            : new List<string>();
    }

    public string RestoreLatest(GamePresetDefinition g)
    {
        var latest = Backups(g).FirstOrDefault() ?? throw new InvalidOperationException("No backup for this game yet.");
        var target = File.ReadAllText(latest + ".source").Trim();
        var attrs = File.Exists(target) ? File.GetAttributes(target) : FileAttributes.Normal;
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
        File.Copy(latest, target, overwrite: true);
        File.SetAttributes(target, attrs);
        File.Delete(latest);
        File.Delete(latest + ".source");
        return target;
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    // ---- Format editors ------------------------------------------------------------------------
    private static (int, List<string>) EditIni(List<string> lines, Dictionary<string, string> values, bool onlyExisting)
    {
        var changed = 0;
        var missing = new List<string>();

        foreach (var (fullKey, value) in values)
        {
            var slash = fullKey.LastIndexOf('/');
            var section = slash > 0 ? fullKey[..slash] : "";
            var key = slash > 0 ? fullKey[(slash + 1)..] : fullKey;

            var inSection = section == "";
            var sectionEnd = -1;
            var found = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    if (inSection && section != "") { sectionEnd = i; break; }
                    inSection = t[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;

                var eq = t.IndexOf('=');
                if (eq <= 0 || !t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                found = true;
                var existingKey = t[..eq].Trim();
                if (t[(eq + 1)..].Trim() != value)
                {
                    lines[i] = $"{existingKey}={value}";
                    changed++;
                }
            }

            if (found) continue;
            if (onlyExisting) { missing.Add(fullKey); continue; }

            if (sectionEnd >= 0) lines.Insert(sectionEnd, $"{key}={value}");
            else if (inSection) lines.Add($"{key}={value}");
            else { lines.Add($"[{section}]"); lines.Add($"{key}={value}"); }
            changed++;
        }
        return (changed, missing);
    }

    private static (int, List<string>) EditKv(List<string> lines, Dictionary<string, string> values, bool onlyExisting)
    {
        var changed = 0;
        var missing = new List<string>();
        foreach (var (key, value) in values)
        {
            var rx = new Regex("^(\\s*\"" + Regex.Escape(key) + "\"\\s+\")([^\"]*)(\".*)$", RegexOptions.IgnoreCase);
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var m = rx.Match(lines[i]);
                if (!m.Success) continue;
                found = true;
                if (m.Groups[2].Value != value)
                {
                    lines[i] = m.Groups[1].Value + value + m.Groups[3].Value;
                    changed++;
                }
            }

            if (found) continue;
            if (onlyExisting) { missing.Add(key); continue; }

            var close = lines.FindLastIndex(l => l.Trim() == "}");
            var line = $"\t\"{key}\"\t\t\"{value}\"";
            if (close >= 0) lines.Insert(close, line); else lines.Add(line);
            changed++;
        }
        return (changed, missing);
    }

    private static (int, List<string>) EditColon(List<string> lines, Dictionary<string, string> values, bool onlyExisting)
    {
        var changed = 0;
        var missing = new List<string>();
        foreach (var (key, value) in values)
        {
            var idx = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            if (idx < 0)
            {
                if (onlyExisting) { missing.Add(key); continue; }
                lines.Add($"{key}:{value}");
                changed++;
                continue;
            }
            if (lines[idx] != $"{key}:{value}")
            {
                lines[idx] = $"{key}:{value}";
                changed++;
            }
        }
        return (changed, missing);
    }
}
