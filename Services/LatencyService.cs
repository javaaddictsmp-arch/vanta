using System.Management;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using TuneUp.Models;

namespace TuneUp.Services;

public sealed class MsiDevice
{
    public string Name { get; init; } = "";
    public string Class { get; init; } = "";
    public string InstanceId { get; init; } = "";
    /// <summary>true/false when known, null when the driver never wrote the key (usually line-based).</summary>
    public bool? MsiEnabled { get; init; }
    public string RegistryPath => $@"SYSTEM\CurrentControlSet\Enum\{InstanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
}

/// <summary>
/// System latency tools:
///  - DPC/ISR sampling through the kernel ETW provider (the data LatencyMon shows), with the
///    worst driver named, so you can compare before/after a tweak.
///  - MSI (message-signaled interrupts) detection and enabling for GPU / network / USB / storage
///    controllers. Enabling goes through the normal tweak pipeline (backup + undo).
/// </summary>
public sealed class LatencyService
{
    public Task<LatencyReport> SampleAsync(int seconds, string label, CancellationToken ct) => Task.Run(() =>
    {
        var dpc = new List<double>();
        var isr = new List<double>();
        var worstDpc = (Us: 0.0, Routine: 0UL);
        var worstIsr = (Us: 0.0, Routine: 0UL);

        try
        {
            using var session = new TraceEventSession("Vanta-Latency") { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                                         KernelTraceEventParser.Keywords.Interrupt);

            void OnDpc(Microsoft.Diagnostics.Tracing.Parsers.Kernel.DPCTraceData d)
            {
                var us = d.ElapsedTimeMSec * 1000.0;
                if (us is < 0 or > 1_000_000) return;
                lock (dpc)
                {
                    dpc.Add(us);
                    if (us > worstDpc.Us) worstDpc = (us, d.Routine);
                }
            }

            session.Source.Kernel.PerfInfoDPC += OnDpc;
            session.Source.Kernel.PerfInfoThreadedDPC += OnDpc;
            session.Source.Kernel.PerfInfoTimerDPC += OnDpc;
            session.Source.Kernel.PerfInfoISR += d =>
            {
                var us = d.ElapsedTimeMSec * 1000.0;
                if (us is < 0 or > 1_000_000) return;
                lock (isr)
                {
                    isr.Add(us);
                    if (us > worstIsr.Us) worstIsr = (us, d.Routine);
                }
            };

            using var stopper = new Timer(_ => session.Stop(), null, seconds * 1000, Timeout.Infinite);
            using var reg = ct.Register(() => session.Stop());
            session.Source.Process();
        }
        catch (Exception ex)
        {
            AppLog.Error("DPC/ISR sampling failed", ex);
            return new LatencyReport
            {
                Label = label,
                Error = "Could not start kernel tracing: " + ex.Message +
                        " (another tool such as LatencyMon, Process Monitor or a profiler may be using it)."
            };
        }

        var drivers = KernelDrivers();
        double P99(List<double> xs) => xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt(Math.Max(0, (int)(xs.Count * 0.99) - 1));

        var maxDpc = dpc.Count > 0 ? dpc.Max() : 0;
        var maxIsr = isr.Count > 0 ? isr.Max() : 0;
        var worst = Math.Max(maxDpc, maxIsr);

        // Thresholds in the same spirit as LatencyMon's: under ~500 us is fine for real-time audio/gaming.
        var verdict = worst switch
        {
            < 500 => "Good: no driver is holding the CPU long enough to cause audio dropouts or input hitches.",
            < 2000 => "OK: some long DPCs/ISRs. Fine for gaming; real-time audio work might crackle.",
            _ => "Problem: a driver is blocking the CPU for over 2 ms. This can cause stutter and audio crackle. Update or roll back the driver named below."
        };

        return new LatencyReport
        {
            Label = label,
            DurationSec = seconds,
            DpcCount = dpc.Count,
            IsrCount = isr.Count,
            MaxDpcUs = maxDpc,
            MaxIsrUs = maxIsr,
            P99DpcUs = P99(dpc),
            P99IsrUs = P99(isr),
            WorstDpcDriver = DriverFor(drivers, worstDpc.Routine),
            WorstIsrDriver = DriverFor(drivers, worstIsr.Routine),
            Verdict = verdict
        };
    }, ct);

    /// <summary>Loaded kernel drivers by base address, so a DPC routine address can be named.</summary>
    private static List<(ulong Base, string Name)> KernelDrivers()
    {
        var list = new List<(ulong, string)>();
        try
        {
            NativeMethods.EnumDeviceDrivers(null, 0, out var needed);
            var bases = new IntPtr[needed / IntPtr.Size];
            if (!NativeMethods.EnumDeviceDrivers(bases, needed, out _)) return list;
            foreach (var b in bases)
            {
                var sb = new StringBuilder(260);
                if (NativeMethods.GetDeviceDriverBaseName(b, sb, (uint)sb.Capacity) > 0)
                    list.Add(((ulong)(long)b, sb.ToString()));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Driver list unavailable: " + ex.Message);
        }
        return list.OrderBy(x => x.Item1).ToList();
    }

    private static string DriverFor(List<(ulong Base, string Name)> drivers, ulong address)
    {
        if (address == 0 || drivers.Count == 0) return "";
        var match = drivers.LastOrDefault(d => d.Base <= address);
        return match.Name ?? "";
    }

    // =========================================================================================
    // MSI mode
    // =========================================================================================
    public Task<List<MsiDevice>> ListMsiDevicesAsync() => Task.Run(() =>
    {
        var list = new List<MsiDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\%'");
            foreach (ManagementObject o in searcher.Get())
            {
                var cls = o["PNPClass"]?.ToString() ?? "";
                if (cls is not ("Display" or "Net" or "USB" or "SCSIAdapter" or "HDC" or "MEDIA")) continue;

                var id = o["PNPDeviceID"]?.ToString() ?? "";
                var dev = new MsiDevice { Name = o["Name"]?.ToString() ?? id, Class = cls, InstanceId = id };
                var v = RegistryService.Read("HKLM", dev.RegistryPath, "MSISupported");
                list.Add(new MsiDevice
                {
                    Name = dev.Name,
                    Class = cls,
                    InstanceId = id,
                    MsiEnabled = v.Exists ? v.Value == "1" : null
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Listing PCI devices failed", ex);
        }
        return list.OrderBy(d => d.Class).ThenBy(d => d.Name).ToList();
    });

    /// <summary>A runtime tweak so MSI changes get the same backup, change-log entry and undo as catalog tweaks.</summary>
    public static TweakDefinition MsiTweak(MsiDevice d) => new()
    {
        Id = "msi-mode:" + d.InstanceId.ToLowerInvariant(),
        Name = $"MSI mode: {d.Name}",
        Category = "Latency",
        Simple = "Changes how this device signals the processor. Can lower delay, but a wrong change can stop the device working.",
        Description = "Switches this device from line-based to message-signaled interrupts.",
        Risk = RiskLevel.Advanced,
        Evidence = EvidenceRating.Situational,
        EvidenceNote = "Modern GPUs and NVMe drives usually use MSI already. It can reduce interrupt latency for devices stuck on line-based interrupts. Needs a reboot.",
        Warning = "If the driver does not really support MSI the device can fail to start (no display, no network). " +
                  "Undo from the Change Log or boot into Safe Mode and revert. Only change one device at a time.",
        RebootRequired = true,
        Operations =
        {
            new TweakOperation { Kind = "registry", Hive = "HKLM", Path = d.RegistryPath, Name = "MSISupported",
                                 ValueType = "DWord", ApplyValue = "1", DefaultValue = null }
        }
    };
}
