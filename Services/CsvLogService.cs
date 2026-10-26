using System.Globalization;
using System.Text;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>Optional: writes every sample to a CSV in %LocalAppData%\Vanta\MetricLogs.</summary>
public sealed class CsvLogService : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public bool IsLogging { get; private set; }
    public string? CurrentFile { get; private set; }

    public void Start(MetricsHub hub)
    {
        lock (_gate)
        {
            if (IsLogging) return;

            CurrentFile = Path.Combine(AppPaths.MetricLogs, $"metrics-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _writer = new StreamWriter(CurrentFile, false, new UTF8Encoding(false)) { AutoFlush = true };
            _writer.WriteLine(
                "time,cpu_load_pct,cpu_temp_c,cpu_clock_mhz,cpu_power_w," +
                "gpu_load_pct,gpu_temp_c,gpu_clock_mhz,gpu_mem_clock_mhz,gpu_power_w,gpu_mem_used_mb," +
                "ram_used_gb,ram_pct,disk_read_mbs,disk_write_mbs,net_down_mbs,net_up_mbs");
            IsLogging = true;
        }

        hub.SampleReceived += OnSample;
    }

    public void Stop(MetricsHub hub)
    {
        hub.SampleReceived -= OnSample;
        lock (_gate)
        {
            IsLogging = false;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OnSample(MetricSample s)
    {
        lock (_gate)
        {
            if (_writer == null) return;
            try
            {
                var line = string.Join(",",
                    s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    F(s.CpuLoad), F(s.CpuTempC), F(s.CpuClockMhz), F(s.CpuPowerW),
                    F(s.GpuLoad), F(s.GpuTempC), F(s.GpuClockMhz), F(s.GpuMemClockMhz), F(s.GpuPowerW), F(s.GpuMemUsedMb),
                    F(s.RamUsedGb), F(s.RamPercent), F(s.DiskReadMBs), F(s.DiskWriteMBs), F(s.NetDownMBs), F(s.NetUpMBs));
                _writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                AppLog.Error("CSV write failed", ex);
            }
        }
    }

    private static string F(double? v) =>
        v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
