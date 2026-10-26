namespace TuneUp.Services;

/// <summary>Creates a Windows System Restore point (if System Protection is on).</summary>
public sealed class RestorePointService
{
    public sealed record Outcome(bool Created, string Message);

    public async Task<Outcome> CreateAsync(string description)
    {
        // Checkpoint-Computer is the supported way. Windows normally allows only one automatic
        // restore point per 24h; in that case it prints a warning and we report "skipped".
        var safe = description.Replace("'", "");
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"Checkpoint-Computer -Description '{safe}' -RestorePointType MODIFY_SETTINGS";

        var result = await ProcessRunner.RunAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            timeoutMs: 120_000);

        var detail = (result.StdErr + " " + result.StdOut).Trim();

        // When Windows skips the point (24h rule) PowerShell exits 0 but prints a WARNING line.
        if (result.Ok &&
            string.IsNullOrWhiteSpace(result.StdErr) &&
            !detail.Contains("WARNING", StringComparison.OrdinalIgnoreCase) &&
            !detail.Contains("cannot be created", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("System restore point created");
            return new Outcome(true, "System restore point created.");
        }

        AppLog.Warn($"Restore point not created: {detail}");

        if (detail.Contains("24 hours", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("1440", StringComparison.OrdinalIgnoreCase))
            return new Outcome(false, "Windows already made a restore point in the last 24 hours, so it skipped a new one. Registry backups were still saved.");

        if (detail.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("System Restore", StringComparison.OrdinalIgnoreCase))
            return new Outcome(false, "System Restore is turned off on this PC, so no restore point was made. Registry backups were still saved.");

        return new Outcome(false, "Could not create a restore point. Registry backups were still saved.");
    }
}
