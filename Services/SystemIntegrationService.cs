using System.IO.Compression;

namespace TuneUp.Services;

/// <summary>Start with Windows, settings backup/restore, and "reset everything to Windows defaults".</summary>
public static class SystemIntegrationService
{
    // A scheduled task (not a Run key) so Vanta can start elevated at logon without a UAC prompt every time.
    private const string StartTask = @"Vanta\Start with Windows";

    public static async Task<bool> SetStartWithWindowsAsync(bool on)
    {
        var r = on
            ? await ProcessRunner.RunAsync("schtasks.exe",
                $"/create /tn \"{StartTask}\" /tr \"\\\"{Environment.ProcessPath}\\\" --tray\" /sc onlogon /rl HIGHEST /delay 0000:20 /f", 15_000)
            : await ProcessRunner.RunAsync("schtasks.exe", $"/delete /tn \"{StartTask}\" /f", 15_000);
        AppLog.Info($"Start with Windows = {on}: {r.ExitCode}");
        return r.Ok;
    }

    public static async Task<bool> IsStartWithWindowsAsync() =>
        (await ProcessRunner.RunAsync("schtasks.exe", $"/query /tn \"{StartTask}\"", 15_000)).Ok;

    private static readonly string[] BackupFiles = { "settings.json", "profiles.json", "process-rules.json", "games.json" };
    private static readonly string[] BackupFolders = { "Catalog", "GamePresets" };

    /// <summary>Exports Vanta's settings, profiles, rules and custom catalogs (not logs or registry backups) to a .zip.</summary>
    public static void Export(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in BackupFiles)
        {
            var full = Path.Combine(AppPaths.Root, f);
            if (File.Exists(full)) zip.CreateEntryFromFile(full, f);
        }
        foreach (var folder in BackupFolders)
        {
            var dir = Path.Combine(AppPaths.Root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                zip.CreateEntryFromFile(f, $"{folder}/{Path.GetFileName(f)}");
        }
    }

    /// <summary>Restores an export. Only known file names are extracted (no paths outside the data folder).</summary>
    public static int Import(string zipPath)
    {
        var count = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var allowed = BackupFiles.Contains(name) ||
                          BackupFolders.Any(f => name.StartsWith(f + "/") && name.EndsWith(".json") && !name.Contains(".."));
            if (!allowed) continue;
            var dest = Path.Combine(AppPaths.Root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Undoes everything Vanta changed on the PC: every tweak, the power plan, DNS, adapter settings,
    /// QoS policies and scheduled tasks. Vanta's own settings and profiles are kept.
    /// </summary>
    public static async Task<List<string>> ResetEverythingAsync(IProgress<string>? progress)
    {
        var done = new List<string>();

        progress?.Report("Ending any running boost...");
        if (AppServices.Boost.IsActive) { await AppServices.Boost.DeactivateAsync("reset to defaults"); done.Add("Ended the running boost"); }

        progress?.Report("Reverting all tweaks...");
        var r = await AppServices.Engine.RevertAllAsync(progress);
        done.Add(r.Message);

        progress?.Report("Removing the Vanta power plan...");
        if (await AppServices.Power.RemoveTuneUpPlanAsync()) done.Add("Removed the Vanta Performance power plan");

        progress?.Report("Restoring DNS...");
        foreach (var a in NetworkService.Adapters().Where(a => AppServices.Settings.Current.SavedDns.ContainsKey(a.Id)))
            if (await NetworkService.RestoreDnsAsync(a, AppServices.Settings)) done.Add($"Restored DNS on {a.Name}");

        if (NetworkService.HasNicBackup)
        {
            progress?.Report("Restoring network adapter settings...");
            done.Add($"Restored {await NetworkService.RestoreNicAsync()} adapter setting(s)");
        }

        progress?.Report("Removing QoS policies...");
        foreach (var p in await NetworkService.QosPoliciesAsync())
        {
            await NetworkService.RemovePolicyAsync(p.Name);
            done.Add($"Removed QoS policy for {p.App}");
        }

        progress?.Report("Removing scheduled tasks...");
        await PrivacyToolsService.RemoveMaintenanceAsync();
        await SetStartWithWindowsAsync(false);
        AppServices.Settings.Current.StartWithWindows = false;
        AppServices.Settings.Save();
        done.Add("Removed Vanta's scheduled tasks");

        AppLog.Info("Reset everything: " + string.Join("; ", done));
        return done;
    }
}
