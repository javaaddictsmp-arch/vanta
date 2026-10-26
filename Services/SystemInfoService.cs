using System.Management;
using Microsoft.Win32;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// One-shot hardware/OS inventory through WMI and a few Windows APIs.
/// Every section is wrapped separately so one failing query never blanks the whole summary.
/// </summary>
public sealed class SystemInfoService
{
    public Task<SystemSummary> ScanAsync() => Task.Run(Scan);

    private SystemSummary Scan()
    {
        var s = new SystemSummary();

        Safe("cpu", () => ReadCpu(s));
        Safe("gpu", () => ReadGpu(s));
        Safe("ram", () => ReadRam(s));
        Safe("board", () => ReadBoard(s));
        Safe("os", () => ReadOs(s));
        Safe("storage", () => ReadStorage(s));
        Safe("display", () => ReadDisplay(s));
        Safe("sysdrive", () => ReadSystemDrive(s));

        return s;
    }

    private static void Safe(string part, Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Error($"System scan section '{part}' failed", ex); }
    }

    private static IEnumerable<ManagementObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        foreach (ManagementObject obj in searcher.Get()) yield return obj;
    }

    // ---------------------------------------------------------------------------------------
    private static void ReadCpu(SystemSummary s)
    {
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            s.CpuName = (cpu["Name"]?.ToString() ?? "Unknown").Trim();
            s.CpuCores = Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
            s.CpuThreads = Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? 0);
            s.CpuMaxMhz = Convert.ToInt32(cpu["MaxClockSpeed"] ?? 0);
            break;
        }
    }

    private static void ReadGpu(SystemSummary s)
    {
        // Prefer a discrete GPU (NVIDIA / AMD Radeon) over integrated graphics.
        var candidates = Query("SELECT Name, DriverVersion FROM Win32_VideoController")
            .Select(g => new
            {
                Name = (g["Name"]?.ToString() ?? "").Trim(),
                Driver = g["DriverVersion"]?.ToString() ?? ""
            })
            .Where(g => g.Name.Length > 0 && !g.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                                          && !g.Name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pick = candidates.FirstOrDefault(g => IsDiscrete(g.Name)) ?? candidates.FirstOrDefault();
        if (pick == null) return;

        s.GpuName = pick.Name;
        s.GpuDriver = pick.Driver;
        var n = pick.Name.ToLowerInvariant();
        s.GpuVendor = n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx") ? "nvidia"
                    : n.Contains("amd") || n.Contains("radeon") ? "amd"
                    : n.Contains("intel") ? "intel" : "other";
    }

    private static bool IsDiscrete(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx") ||
               (n.Contains("radeon") && !n.Contains("graphics")) ||
               n.Contains("arc ");
    }

    private static void ReadRam(SystemSummary s)
    {
        long totalBytes = 0;
        var rated = new List<int>();
        var configured = new List<int>();
        var types = new HashSet<string>();
        var locators = new List<string>();

        foreach (var m in Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, DeviceLocator FROM Win32_PhysicalMemory"))
        {
            s.RamModules++;
            totalBytes += Convert.ToInt64(m["Capacity"] ?? 0L);
            if (m["Speed"] != null) rated.Add(Convert.ToInt32(m["Speed"]));
            if (m["ConfiguredClockSpeed"] != null) configured.Add(Convert.ToInt32(m["ConfiguredClockSpeed"]));
            if (m["SMBIOSMemoryType"] != null) types.Add(MemoryTypeName(Convert.ToInt32(m["SMBIOSMemoryType"])));
            locators.Add(m["DeviceLocator"]?.ToString() ?? "");
        }

        s.RamTotalGb = Math.Round(totalBytes / 1024.0 / 1024 / 1024, 1);
        s.RamType = string.Join("/", types.Where(t => t.Length > 0));
        s.RamRatedMhz = rated.Count > 0 ? rated.Min() : 0;
        s.RamConfiguredMhz = configured.Count > 0 ? configured.Min() : 0;

        // Channels: we can only reason from module count; the board's slot map decides the rest.
        s.RamChannels = s.RamModules switch
        {
            0 => "",
            1 => "Single module - runs in single-channel mode",
            2 => "2 modules - dual-channel if they sit in the slots your manual recommends (usually A2 + B2)",
            _ => $"{s.RamModules} modules"
        };

        // XMP / DOCP / EXPO verdict. SPD "Speed" is the rated speed, ConfiguredClockSpeed the current.
        if (s.RamRatedMhz > 0 && s.RamConfiguredMhz > 0)
        {
            if (s.RamConfiguredMhz + 50 < s.RamRatedMhz)
                s.RamStatus = $"Running at {s.RamConfiguredMhz} MHz but rated {s.RamRatedMhz} MHz - XMP/DOCP/EXPO is probably OFF";
            else
                s.RamStatus = $"Running at {s.RamConfiguredMhz} MHz (rated {s.RamRatedMhz} MHz)";
        }
        else
        {
            s.RamStatus = "Speed info not reported by this PC";
        }
    }

    private static string MemoryTypeName(int smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => ""
    };

    private static void ReadBoard(SystemSummary s)
    {
        foreach (var b in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            s.Motherboard = $"{b["Manufacturer"]} {b["Product"]}".Trim();
            break;
        }

        foreach (var b in Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            s.BiosVersion = b["SMBIOSBIOSVersion"]?.ToString() ?? "";
            var raw = b["ReleaseDate"]?.ToString();
            if (!string.IsNullOrEmpty(raw))
            {
                try { s.BiosDate = ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd"); }
                catch { s.BiosDate = raw; }
            }
            break;
        }
    }

    private static void ReadOs(SystemSummary s)
    {
        s.OsBuild = Environment.OSVersion.Version.Build;

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName")?.ToString() ?? "Windows";
        var display = key?.GetValue("DisplayVersion")?.ToString() ?? key?.GetValue("ReleaseId")?.ToString() ?? "";

        // ProductName still says "Windows 10" on Windows 11 builds, so fix it from the build number.
        if (s.OsBuild >= 22000) product = product.Replace("Windows 10", "Windows 11");
        s.OsName = $"{product} {display}".Trim();
    }

    private static void ReadStorage(SystemSummary s)
    {
        // MSFT_PhysicalDisk knows SSD vs HDD; fall back to Win32_DiskDrive if it is unavailable.
        try
        {
            foreach (var d in Query(
                         "SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk",
                         @"root\Microsoft\Windows\Storage"))
            {
                var media = Convert.ToInt32(d["MediaType"] ?? 0);
                var bus = Convert.ToInt32(d["BusType"] ?? 0);
                var type = bus == 17 ? "NVMe SSD" : media == 4 ? "SSD" : media == 3 ? "HDD" : "Unknown";

                s.Storage.Add(new StorageInfo
                {
                    Model = d["FriendlyName"]?.ToString()?.Trim() ?? "Disk",
                    SizeGb = Math.Round(Convert.ToDouble(d["Size"] ?? 0.0) / 1_000_000_000.0),
                    Type = type
                });
            }
            if (s.Storage.Count > 0) return;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"MSFT_PhysicalDisk unavailable: {ex.Message}");
        }

        foreach (var d in Query("SELECT Model, Size FROM Win32_DiskDrive"))
        {
            s.Storage.Add(new StorageInfo
            {
                Model = d["Model"]?.ToString()?.Trim() ?? "Disk",
                SizeGb = Math.Round(Convert.ToDouble(d["Size"] ?? 0.0) / 1_000_000_000.0),
                Type = "Unknown"
            });
        }
    }

    private static void ReadDisplay(SystemSummary s)
    {
        var mode = new NativeMethods.DEVMODE
        {
            dmDeviceName = new string('\0', 32),
            dmFormName = new string('\0', 32),
            dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>()
        };

        if (!NativeMethods.EnumDisplaySettings(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode)) return;

        s.RefreshCurrentHz = (int)mode.dmDisplayFrequency;
        s.Resolution = $"{mode.dmPelsWidth}x{mode.dmPelsHeight}";

        var width = mode.dmPelsWidth;
        var height = mode.dmPelsHeight;
        var max = s.RefreshCurrentHz;

        for (var i = 0; i < 2000; i++)
        {
            var m = new NativeMethods.DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>()
            };
            if (!NativeMethods.EnumDisplaySettings(null, i, ref m)) break;
            if (m.dmPelsWidth == width && m.dmPelsHeight == height)
                max = Math.Max(max, (int)m.dmDisplayFrequency);
        }

        s.RefreshMaxHz = max;
    }

    private static void ReadSystemDrive(SystemSummary s)
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var drive = new DriveInfo(root);
        s.SystemDriveFreeGb = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1);
        s.SystemDriveFreePercent = drive.TotalSize > 0
            ? Math.Round(drive.AvailableFreeSpace * 100.0 / drive.TotalSize, 1)
            : 0;
    }
}
