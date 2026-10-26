using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Reads live sensors. CPU/GPU numbers come from LibreHardwareMonitor (needs admin, which the
/// manifest requests). RAM, disk and network use plain Windows APIs. Any sensor that is not
/// available on this PC simply comes back as null so the UI can show "N/A".
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private static readonly Regex CoreRegex = new(@"^(CPU )?Core #(\d+)$", RegexOptions.Compiled);
    private static readonly Regex ThreadRegex = new(@"^(CPU )?Core #(\d+) Thread #(\d+)$", RegexOptions.Compiled);

    private Computer? _computer;
    private bool _open;

    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;
    private PerformanceCounter? _diskTime;

    private long _lastRx, _lastTx;
    private DateTime _lastNetTime = DateTime.MinValue;

    public bool SensorsAvailable => _open;

    public void Open()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false
            };
            _computer.Open();
            _open = true;
            AppLog.Info("LibreHardwareMonitor opened");
        }
        catch (Exception ex)
        {
            _open = false;
            AppLog.Error("Could not open hardware sensors (CPU/GPU temps will show N/A)", ex);
        }

        // Disk counters. Counter names are localized on non-English Windows; fail soft.
        try
        {
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
            _diskTime = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
            _diskRead.NextValue(); _diskWrite.NextValue(); _diskTime.NextValue();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Disk performance counters unavailable: {ex.Message}");
            _diskRead = _diskWrite = _diskTime = null;
        }
    }

    public MetricSample Sample()
    {
        var s = new MetricSample();

        if (_open && _computer != null)
        {
            try
            {
                foreach (var hw in _computer.Hardware) UpdateRecursive(hw);
                ReadCpu(s);
                ReadGpu(s);
            }
            catch (Exception ex)
            {
                AppLog.Error("Sensor read failed", ex);
            }
        }

        ReadRam(s);
        ReadDisk(s);
        ReadNetwork(s);
        return s;
    }

    // -------------------------------------------------------------------------------------------
    private static void UpdateRecursive(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw)
    {
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
            foreach (var s in AllSensors(sub)) yield return s;
    }

    private static IEnumerable<ISensor> Of(IHardware hw, SensorType type) =>
        AllSensors(hw).Where(s => s.SensorType == type && s.Value.HasValue);

    // -------------------------------------------------------------------------------------------
    private void ReadCpu(MetricSample s)
    {
        var cpu = _computer!.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return;

        // Total load
        var total = Of(cpu, SensorType.Load).FirstOrDefault(x => x.Name == "CPU Total");
        s.CpuLoad = total?.Value ?? 0;

        // Per-thread loads (preferred) or per-core loads
        var loads = Of(cpu, SensorType.Load).ToList();
        var threads = loads.Where(x => ThreadRegex.IsMatch(x.Name)).ToList();
        var source = threads.Count > 0 ? threads : loads.Where(x => CoreRegex.IsMatch(x.Name)).ToList();

        var ordered = source
            .Select(x => new { x.Name, Value = (double)(x.Value ?? 0), Key = SortKey(x.Name) })
            .OrderBy(x => x.Key)
            .ToList();

        s.CpuThreadLoads = ordered.Select(x => x.Value).ToArray();
        s.CpuThreadNames = ordered.Select(x => ShortName(x.Name)).ToArray();

        if (total == null && s.CpuThreadLoads.Length > 0)
            s.CpuLoad = s.CpuThreadLoads.Average();

        // Temperature: prefer package/Tctl, else hottest sensor
        var temps = Of(cpu, SensorType.Temperature).ToList();
        string[] preferred = { "CPU Package", "Core (Tctl/Tdie)", "Core Max", "Package", "CPU Total" };
        var temp = preferred.Select(n => temps.FirstOrDefault(t => t.Name == n)).FirstOrDefault(t => t != null)
                   ?? temps.OrderByDescending(t => t.Value).FirstOrDefault();
        // Without the sensor driver (or admin) some CPUs report 0 instead of nothing: treat as unavailable.
        s.CpuTempC = temp?.Value is > 0 ? temp.Value : null;

        // Clock: average of core clocks
        var clocks = Of(cpu, SensorType.Clock)
            .Where(c => CoreRegex.IsMatch(c.Name))
            .Select(c => (double)c.Value!.Value)
            .ToList();
        if (clocks.Count > 0) s.CpuClockMhz = clocks.Average();

        // Power
        var power = Of(cpu, SensorType.Power).FirstOrDefault(p => p.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        s.CpuPowerW = power?.Value is > 0 ? power.Value : null;
    }

    private static int SortKey(string name)
    {
        var t = ThreadRegex.Match(name);
        if (t.Success) return int.Parse(t.Groups[2].Value) * 100 + int.Parse(t.Groups[3].Value);
        var c = CoreRegex.Match(name);
        return c.Success ? int.Parse(c.Groups[2].Value) * 100 : 0;
    }

    private static string ShortName(string name) =>
        name.Replace("CPU ", "").Replace("Core ", "C").Replace(" Thread ", " T");

    // -------------------------------------------------------------------------------------------
    private void ReadGpu(MetricSample s)
    {
        var gpu = _computer!.Hardware.FirstOrDefault(h =>
                      h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                  ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        if (gpu == null) return;

        s.GpuName = gpu.Name;
        s.GpuLoad = Of(gpu, SensorType.Load).FirstOrDefault(x => x.Name == "GPU Core")?.Value
                    ?? Of(gpu, SensorType.Load).FirstOrDefault()?.Value;

        var temps = Of(gpu, SensorType.Temperature).ToList();
        s.GpuTempC = (temps.FirstOrDefault(t => t.Name == "GPU Core") ?? temps.FirstOrDefault())?.Value;

        var clocks = Of(gpu, SensorType.Clock).ToList();
        s.GpuClockMhz = clocks.FirstOrDefault(c => c.Name == "GPU Core")?.Value;
        s.GpuMemClockMhz = clocks.FirstOrDefault(c => c.Name == "GPU Memory")?.Value;

        s.GpuPowerW = Of(gpu, SensorType.Power).FirstOrDefault()?.Value;

        var small = Of(gpu, SensorType.SmallData).ToList();
        s.GpuMemUsedMb = small.FirstOrDefault(x => x.Name == "GPU Memory Used")?.Value;
        s.GpuMemTotalMb = small.FirstOrDefault(x => x.Name == "GPU Memory Total")?.Value;
    }

    // -------------------------------------------------------------------------------------------
    private static void ReadRam(MetricSample s)
    {
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref mem)) return;

        s.RamTotalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
        s.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
        s.RamPercent = mem.dwMemoryLoad;
    }

    private void ReadDisk(MetricSample s)
    {
        try
        {
            if (_diskRead != null) s.DiskReadMBs = _diskRead.NextValue() / 1024.0 / 1024.0;
            if (_diskWrite != null) s.DiskWriteMBs = _diskWrite.NextValue() / 1024.0 / 1024.0;
            if (_diskTime != null) s.DiskActivePercent = Math.Min(100, _diskTime.NextValue());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Disk counter read failed: {ex.Message}");
        }
    }

    private void ReadNetwork(MetricSample s)
    {
        try
        {
            long rx = 0, tx = 0, speed = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var stats = nic.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
                speed = Math.Max(speed, nic.Speed);
            }

            var now = DateTime.UtcNow;
            if (_lastNetTime != DateTime.MinValue)
            {
                var seconds = (now - _lastNetTime).TotalSeconds;
                if (seconds > 0.1 && rx >= _lastRx && tx >= _lastTx)
                {
                    s.NetDownMBs = (rx - _lastRx) / seconds / 1024 / 1024;
                    s.NetUpMBs = (tx - _lastTx) / seconds / 1024 / 1024;
                }
            }

            _lastRx = rx;
            _lastTx = tx;
            _lastNetTime = now;
            s.NetLinkMbps = speed / 1_000_000.0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Network read failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* ignore */ }
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        _diskTime?.Dispose();
    }
}
