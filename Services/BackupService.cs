using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Exports the registry keys a tweak is about to touch into .reg files, so even if
/// Vanta's own change log were lost you could double-click the .reg to restore.
/// </summary>
public sealed class BackupService
{
    /// <summary>Creates a timestamped folder and exports every distinct key. Returns the folder.</summary>
    public async Task<string> ExportKeysAsync(string label, IEnumerable<(string Hive, string Path)> keys)
    {
        var safeLabel = new string(label.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        // Runtime tweak ids can contain whole EXE paths; keep folder names well under MAX_PATH.
        if (safeLabel.Length > 60) safeLabel = safeLabel[..40] + "_" + safeLabel[^19..];
        var folder = Path.Combine(AppPaths.Backups, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeLabel}");
        Directory.CreateDirectory(folder);

        var index = 0;
        foreach (var (hive, path) in keys.Distinct())
        {
            try
            {
                if (!RegistryService.KeyExists(hive, path)) continue; // nothing to back up yet

                index++;
                var file = Path.Combine(folder, $"{index:00}-{hive}.reg");
                var full = $"{RegistryService.HiveForRegExe(hive)}\\{path}";
                var result = await ProcessRunner.RunAsync("reg.exe", $"export \"{full}\" \"{file}\" /y", 20_000);
                if (!result.Ok)
                    AppLog.Warn($"reg export failed for {full}: {result.StdErr.Trim()}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Backup of {hive}\\{path} failed", ex);
            }
        }

        return folder;
    }
}
