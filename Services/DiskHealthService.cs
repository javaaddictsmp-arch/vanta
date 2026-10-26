using System.Management;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Drive health from Windows' own storage provider (the same data as Get-StorageReliabilityCounter):
/// SSD wear, temperature, power-on hours and error counters, plus Windows' overall verdict.
/// Detailed counters need admin; without it you still get the overall health status.
/// </summary>
public sealed class DiskHealthService
{
    public Task<List<DiskHealth>> ScanAsync() => Task.Run(() =>
    {
        var list = new List<DiskHealth>();
        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject d in searcher.Get())
            {
                var bus = Convert.ToInt32(d["BusType"] ?? 0);
                var media = Convert.ToInt32(d["MediaType"] ?? 0);
                var type = bus == 17 ? "NVMe SSD" : media == 4 ? "SSD" : media == 3 ? "HDD" : "Drive";
                var health = Convert.ToInt32(d["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };

                int? wear = null, temp = null, tempMax = null;
                long? hours = null, readErr = null, writeErr = null;
                try
                {
                    foreach (ManagementObject c in d.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        wear = ToInt(c["Wear"]);
                        temp = ToInt(c["Temperature"]);
                        tempMax = ToInt(c["TemperatureMax"]);
                        hours = ToLong(c["PowerOnHours"]);
                        readErr = ToLong(c["ReadErrorsTotal"]);
                        writeErr = ToLong(c["WriteErrorsTotal"]);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Reliability counters unavailable for {d["FriendlyName"]}: {ex.Message}");
                }

                var (sev, verdict) = Judge(type, health, wear, temp, readErr, writeErr);
                list.Add(new DiskHealth
                {
                    Name = d["FriendlyName"]?.ToString()?.Trim() ?? "Drive",
                    Type = type,
                    SizeGb = Math.Round(Convert.ToDouble(d["Size"] ?? 0.0) / 1_000_000_000.0),
                    Health = health,
                    WearPercent = wear, TemperatureC = temp is > 0 ? temp : null, TemperatureMaxC = tempMax,
                    PowerOnHours = hours, ReadErrors = readErr, WriteErrors = writeErr,
                    Severity = sev, Verdict = verdict
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Disk health scan failed", ex);
        }
        return list;
    });

    private static (Severity, string) Judge(string type, string health, int? wear, int? temp, long? readErr, long? writeErr)
    {
        if (health == "Unhealthy")
            return (Severity.Problem, "Windows reports this drive as failing. Back up anything important now and plan to replace it.");
        if (wear is >= 90)
            return (Severity.Problem, "This SSD has used almost all of its rated write endurance. Back up and plan a replacement.");
        if (health == "Warning")
            return (Severity.Warning, "Windows reports a health warning for this drive. Back up important files and check it with the maker's tool.");
        if (wear is >= 70)
            return (Severity.Warning, "This SSD has used most of its rated endurance. It may last years more, but keep backups.");
        if (temp is >= 70)
            return (Severity.Warning, $"Running hot ({temp}°C). NVMe drives slow down when hot; a heatsink or airflow helps.");
        if (type == "HDD" && (readErr is > 0 || writeErr is > 0))
            return (Severity.Warning, "This hard drive has logged read/write errors. Keep backups; it may be starting to fail.");
        if (type == "HDD")
            return (Severity.Info, "Healthy. Hard drives are slow for games: newer games load much faster from an SSD.");
        return (Severity.Good, "Healthy.");
    }

    private static int? ToInt(object? o) => o == null ? null : Convert.ToInt32(o);
    private static long? ToLong(object? o) => o == null ? null : Convert.ToInt64(o);

    /// <summary>Opens Windows Memory Diagnostic (it asks before restarting).</summary>
    public static void OpenMemoryDiagnostic()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mdsched.exe") { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("mdsched failed: " + ex.Message); }
    }
}
