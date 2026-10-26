namespace TuneUp.Models;

/// <summary>
/// One game in the in-game config catalog (Data/gamepresets.json). Adding a game is just a new
/// JSON entry: where its config file lives, what format it is, and the values per preset.
///
/// Formats:
///   ini    - [Section] Key=Value         (keys written as "Section/Key")
///   kv     - "key"   "value"             (Source-engine style: CS2 cs2_video.txt, Apex videoconfig.txt)
///   colon  - key:value                   (Minecraft options.txt)
///   guide  - no file edits, notes only   (games that store settings server-side or in binary files)
///
/// Safety: by default only keys that already exist in your file are changed (onlyExistingKeys),
/// so a wrong key in a preset can never inject junk into a game's config.
/// </summary>
public sealed class GamePresetDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Format { get; set; } = "guide";

    /// <summary>
    /// Candidate paths. Tokens: %LOCALAPPDATA%, %APPDATA%, %USERPROFILE%, {documents}, {steam}, {savedgames}.
    /// A "*" segment matches any one folder (e.g. a Steam account id).
    /// </summary>
    public List<string> Paths { get; set; } = new();

    public bool OnlyExistingKeys { get; set; } = true;
    public string Notes { get; set; } = "";

    /// <summary>Extra advice shown with the game (mods, launch options, JVM args...).</summary>
    public List<string> Tips { get; set; } = new();

    /// <summary>Key: "Max FPS", "Balanced", "Visual quality". Value: key -> value.</summary>
    public Dictionary<string, Dictionary<string, string>> Presets { get; set; } = new();

    public Dictionary<string, string> PresetNotes { get; set; } = new();
}

public sealed class GamePresetCatalog
{
    public List<GamePresetDefinition> Games { get; set; } = new();
}
