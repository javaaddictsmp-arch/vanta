namespace TuneUp.Models;

/// <summary>One snapshot of live hardware readings. Null = sensor not available.</summary>
public sealed class MetricSample
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    // CPU
    public double CpuLoad { get; set; }
    public double[] CpuThreadLoads { get; set; } = Array.Empty<double>();
    public string[] CpuThreadNames { get; set; } = Array.Empty<string>();
    public double? CpuTempC { get; set; }
    public double? CpuClockMhz { get; set; }
    public double? CpuPowerW { get; set; }

    // GPU
    public string? GpuName { get; set; }
    public double? GpuLoad { get; set; }
    public double? GpuTempC { get; set; }
    public double? GpuClockMhz { get; set; }
    public double? GpuMemClockMhz { get; set; }
    public double? GpuPowerW { get; set; }
    public double? GpuMemUsedMb { get; set; }
    public double? GpuMemTotalMb { get; set; }

    // RAM
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double RamPercent { get; set; }

    // Disk (all physical disks combined)
    public double? DiskReadMBs { get; set; }
    public double? DiskWriteMBs { get; set; }
    public double? DiskActivePercent { get; set; }

    // Network (all active adapters combined)
    public double NetDownMBs { get; set; }
    public double NetUpMBs { get; set; }
    public double NetLinkMbps { get; set; }

    public double? GpuMemPercent =>
        GpuMemUsedMb.HasValue && GpuMemTotalMb is > 0
            ? GpuMemUsedMb.Value / GpuMemTotalMb.Value * 100.0
            : null;
}

public sealed class StorageInfo
{
    public string Model { get; set; } = "";
    public double SizeGb { get; set; }
    public string Type { get; set; } = "Unknown"; // SSD / HDD / NVMe
}

public sealed class SystemSummary
{
    public string CpuName { get; set; } = "Unknown";
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public int CpuMaxMhz { get; set; }

    public string GpuName { get; set; } = "Unknown";
    public string GpuDriver { get; set; } = "";
    public string GpuVendor { get; set; } = ""; // nvidia / amd / intel / other

    public double RamTotalGb { get; set; }
    public int RamModules { get; set; }
    public string RamType { get; set; } = "";
    public int RamRatedMhz { get; set; }
    public int RamConfiguredMhz { get; set; }
    public string RamStatus { get; set; } = "";   // human readable XMP/EXPO verdict
    public string RamChannels { get; set; } = "";

    public string Motherboard { get; set; } = "Unknown";
    public string BiosVersion { get; set; } = "";
    public string BiosDate { get; set; } = "";

    public string OsName { get; set; } = "";
    public int OsBuild { get; set; }
    public bool IsWindows11 => OsBuild >= 22000;

    public List<StorageInfo> Storage { get; set; } = new();

    public int RefreshCurrentHz { get; set; }
    public int RefreshMaxHz { get; set; }
    public string Resolution { get; set; } = "";

    public double SystemDriveFreePercent { get; set; }
    public double SystemDriveFreeGb { get; set; }
}

public enum Severity { Info, Good, Warning, Problem }

public sealed class HealthItem
{
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string Fix { get; init; } = "";
    public int Points { get; init; }          // 0 or negative
    public Severity Severity { get; init; }
}

public sealed class HealthReport
{
    public int Score { get; init; }
    public List<HealthItem> Items { get; init; } = new();
}
