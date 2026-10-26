using System.Text.Json;
using System.Text.Json.Serialization;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Per-game boost profiles (profiles.json). Games without their own profile use the
/// "default" profile. Profiles export/import as plain JSON so they can be shared.
/// </summary>
public sealed class ProfileService
{
    public const string DefaultId = "default";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private Dictionary<string, BoostProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.ProfilesFile))
            {
                var list = JsonSerializer.Deserialize<List<BoostProfile>>(File.ReadAllText(AppPaths.ProfilesFile), Json) ?? new();
                _profiles = list.Where(p => !string.IsNullOrWhiteSpace(p.GameId))
                                .GroupBy(p => p.GameId, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read profiles.json, using defaults", ex);
        }

        if (!_profiles.ContainsKey(DefaultId))
            _profiles[DefaultId] = new BoostProfile { GameId = DefaultId, GameName = "Default (all games)" };
    }

    public BoostProfile Default
    {
        get { lock (_gate) return _profiles[DefaultId]; }
    }

    public bool HasOwn(string gameId)
    {
        lock (_gate) return _profiles.ContainsKey(gameId);
    }

    /// <summary>The game's own profile, or the default one.</summary>
    public BoostProfile For(string gameId)
    {
        lock (_gate) return _profiles.TryGetValue(gameId, out var p) ? p : _profiles[DefaultId];
    }

    /// <summary>Gets (creating from the default if needed) the editable profile for a game.</summary>
    public BoostProfile GetOrCreate(GameEntry g)
    {
        lock (_gate)
        {
            if (_profiles.TryGetValue(g.Id, out var p)) return p;
            var created = _profiles[DefaultId].Clone(g.Id, g.Name);
            _profiles[g.Id] = created;
            return created;
        }
    }

    public void ResetToDefault(string gameId)
    {
        lock (_gate)
        {
            if (gameId != DefaultId) _profiles.Remove(gameId);
        }
        Save();
    }

    public void Save()
    {
        try
        {
            lock (_gate)
                File.WriteAllText(AppPaths.ProfilesFile, JsonSerializer.Serialize(_profiles.Values.ToList(), Json));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save profiles", ex);
        }
    }

    public void Export(string file, IEnumerable<BoostProfile> profiles, string description)
    {
        var lib = new ProfileLibraryFile
        {
            Author = Environment.UserName,
            Description = description,
            Profiles = profiles.ToList()
        };
        File.WriteAllText(file, JsonSerializer.Serialize(lib, Json));
    }

    /// <summary>
    /// Imports a profile file. Profiles are matched to installed games by id, then by name.
    /// A file with a single profile and no match is applied to <paramref name="fallbackGame"/>.
    /// Returns how many were imported.
    /// </summary>
    public int Import(string file, IReadOnlyList<GameEntry> games, GameEntry? fallbackGame)
    {
        var text = File.ReadAllText(file);
        List<BoostProfile> incoming;
        try
        {
            incoming = JsonSerializer.Deserialize<ProfileLibraryFile>(text, Json)?.Profiles ?? new();
            if (incoming.Count == 0)
            {
                var single = JsonSerializer.Deserialize<BoostProfile>(text, Json);
                if (single != null) incoming.Add(single);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This is not a Vanta profile file: " + ex.Message);
        }

        var count = 0;
        lock (_gate)
        {
            foreach (var p in incoming)
            {
                Sanitize(p);
                var target = games.FirstOrDefault(g => g.Id.Equals(p.GameId, StringComparison.OrdinalIgnoreCase))
                             ?? games.FirstOrDefault(g => g.Name.Equals(p.GameName, StringComparison.OrdinalIgnoreCase));

                if (target == null && p.GameId.Equals(DefaultId, StringComparison.OrdinalIgnoreCase))
                {
                    _profiles[DefaultId] = p.Clone(DefaultId, "Default (all games)");
                    count++;
                }
                else if (target != null)
                {
                    _profiles[target.Id] = p.Clone(target.Id, target.Name);
                    count++;
                }
                else if (incoming.Count == 1 && fallbackGame != null)
                {
                    _profiles[fallbackGame.Id] = p.Clone(fallbackGame.Id, fallbackGame.Name);
                    count++;
                }
            }
        }
        Save();
        return count;
    }

    /// <summary>Shared files are untrusted: clamp anything that could be abused.</summary>
    private static void Sanitize(BoostProfile p)
    {
        if (p.TimerResolution100ns is < 0 or > 156_250) p.TimerResolution100ns = 0;
        p.BackgroundApps = p.BackgroundApps
            .Where(b => !string.IsNullOrWhiteSpace(b.ProcessName) && !ProcessUtil.IsProtected(b.ProcessName))
            .Select(b => { b.ProcessName = Path.GetFileNameWithoutExtension(b.ProcessName.Trim()); return b; })
            .ToList();
    }
}

/// <summary>Persistent per-app priority/affinity rules, applied whenever the EXE starts while Vanta runs.</summary>
public sealed class ProcessRuleService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<ProcessRule> Rules { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.RulesFile))
                Rules = JsonSerializer.Deserialize<List<ProcessRule>>(File.ReadAllText(AppPaths.RulesFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read process rules", ex);
        }
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.RulesFile, JsonSerializer.Serialize(Rules, Json)); }
        catch (Exception ex) { AppLog.Error("Could not save process rules", ex); }
    }

    public ProcessRule? Match(string processName) =>
        Rules.FirstOrDefault(r => r.Enabled && r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
}
