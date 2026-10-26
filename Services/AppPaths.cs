namespace TuneUp.Services;

/// <summary>All on-disk locations Vanta uses. Everything lives under %LocalAppData%\Vanta.</summary>
public static class AppPaths
{
    /// <summary>VANTA_DATA_DIR overrides the folder (used to test a build without touching your real data).</summary>
    private static readonly string? Override = Environment.GetEnvironmentVariable("VANTA_DATA_DIR") is { Length: > 0 } d ? d : null;

    public static readonly string Root = Override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vanta");

    /// <summary>Folder used while the app was still called "TuneUp".</summary>
    private static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneUp");

    static AppPaths()
    {
        // Keep the change log (and with it every Undo), backups and settings from before the rename.
        try
        {
            if (Override != null || !Directory.Exists(LegacyRoot) || File.Exists(Path.Combine(LegacyRoot, "moved-to-vanta.txt"))) return;

            if (!Directory.Exists(Root))
            {
                try
                {
                    Directory.Move(LegacyRoot, Root);
                    return;
                }
                catch
                {
                    // Something (antivirus, an indexer, an old copy still running) holds a file open: copy instead.
                }
            }

            foreach (var file in Directory.EnumerateFiles(LegacyRoot, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(Root, Path.GetRelativePath(LegacyRoot, file));
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            File.WriteAllText(Path.Combine(LegacyRoot, "moved-to-vanta.txt"),
                $"Copied to {Root} on {DateTime.Now:g}. This old folder can be deleted.");
        }
        catch
        {
            // Never block startup over the migration; the old data stays where it is.
        }
    }

    public static string Backups => Ensure(Path.Combine(Root, "Backups"));
    public static string Logs => Ensure(Path.Combine(Root, "Logs"));
    public static string MetricLogs => Ensure(Path.Combine(Root, "MetricLogs"));
    public static string Catalog => Ensure(Path.Combine(Root, "Catalog"));

    public static string ChangeLogFile => Path.Combine(Ensure(Root), "changelog.json");
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    // Phase 2: FPS Boost Center
    public static string Benchmarks => Ensure(Path.Combine(Root, "Benchmarks"));
    public static string Sessions => Ensure(Path.Combine(Root, "Sessions"));
    public static string GamePresets =>Ensure(Path.Combine(Root, "GamePresets"));
    public static string GameConfigBackups => Ensure(Path.Combine(Root, "Backups", "GameConfigs"));
    public static string ProfilesFile => Path.Combine(Ensure(Root), "profiles.json");
    public static string GamesFile => Path.Combine(Ensure(Root), "games.json");
    public static string RulesFile => Path.Combine(Ensure(Root), "process-rules.json");
    /// <summary>Written after every boost step; its presence with Active=true means "restore me".</summary>
    public static string BoostStateFile => Path.Combine(Ensure(Root), "boost-state.json");

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
