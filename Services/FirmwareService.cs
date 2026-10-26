using System.Management;
using System.Net;
using System.Text;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// BIOS / UEFI guidance. Windows cannot (and Vanta will not) change firmware settings, so this
/// reads what Windows can see, explains each setting in plain English and says where to find
/// it for your board's brand. "Reboot to BIOS" uses Windows' own `shutdown /r /fw`.
/// </summary>
public sealed class FirmwareService
{
    public enum BoardVendor { Unknown, Asus, Msi, Gigabyte, AsRock, Other }

    public static BoardVendor VendorOf(string board)
    {
        var b = board.ToUpperInvariant();
        if (b.Contains("ASUS")) return BoardVendor.Asus;
        if (b.Contains("MICRO-STAR") || b.Contains("MSI")) return BoardVendor.Msi;
        if (b.Contains("GIGABYTE") || b.Contains("AORUS")) return BoardVendor.Gigabyte;
        if (b.Contains("ASROCK")) return BoardVendor.AsRock;
        return string.IsNullOrWhiteSpace(board) || board == "Unknown" ? BoardVendor.Unknown : BoardVendor.Other;
    }

    public async Task<List<BiosCheck>> ScanAsync(SystemSummary s)
    {
        var v = VendorOf(s.Motherboard);
        var amd = s.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || s.CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        var intel = s.CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        var list = new List<BiosCheck>();

        void Add(Func<BiosCheck> f, string what)
        {
            try { list.Add(f()); }
            catch (Exception ex) { AppLog.Warn($"BIOS check '{what}' failed: {ex.Message}"); }
        }

        // ---- Memory profile (biggest free win on many PCs) ---------------------------------
        Add(() =>
        {
            var profile = amd ? "EXPO / DOCP" : "XMP";
            if (s.RamRatedMhz <= 0 || s.RamConfiguredMhz <= 0)
                return new BiosCheck
                {
                    Title = $"Memory profile ({profile})", Severity = Severity.Info, Detected = "Speed not reported by this PC",
                    Why = "RAM usually runs slower than the speed on the box until you turn on its profile in the BIOS.",
                    HowTo = MemoryHowTo(v, amd), Evidence = EvidenceRating.Proven
                };
            var off = s.RamConfiguredMhz + 50 < s.RamRatedMhz;
            return new BiosCheck
            {
                Title = $"Memory profile ({profile})",
                Severity = off ? Severity.Warning : Severity.Good,
                Detected = off ? $"Running {s.RamConfiguredMhz} MHz, rated {s.RamRatedMhz} MHz: profile looks OFF"
                               : $"Running at rated speed ({s.RamConfiguredMhz} MHz)",
                Why = "Your RAM is sold at a speed it only reaches when its profile is enabled. Faster RAM gives noticeably " +
                      "higher FPS in CPU-heavy games (often 5-15% on Ryzen). It is the single most common free fix.",
                HowTo = MemoryHowTo(v, amd) + " If the PC fails to boot afterwards, it resets itself after a few tries; then try the profile's lower speed.",
                Evidence = EvidenceRating.Proven
            };
        }, "memory");

        // ---- Resizable BAR ------------------------------------------------------------------
        if (s.GpuVendor == "nvidia")
        {
            var n = await AppServices.Gpu.ReadNvidiaAsync();
            Add(() => new BiosCheck
            {
                Title = "Resizable BAR (ReBAR / Smart Access Memory)",
                Severity = n == null ? Severity.Info : n.ReBar.StartsWith("Enabled") ? Severity.Good
                         : n.ReBar.StartsWith("Not supported") ? Severity.Info : Severity.Warning,
                Detected = n?.ReBar ?? "Could not read (nvidia-smi not found)",
                Why = "Lets the CPU access all of the graphics card's memory at once. Worth a few percent in some games on supported cards.",
                HowTo = ReBarHowTo(v),
                Evidence = EvidenceRating.Situational
            }, "rebar");

            if (n?.PcieGen is { } gen && n.PcieGenMax is { } max)
                Add(() => new BiosCheck
                {
                    Title = "Graphics card PCIe link",
                    Severity = Severity.Info,
                    Detected = $"Gen {gen} of {max}, x{n.PcieWidth} (cards drop to a lower gen at idle; check while gaming)",
                    Why = "If the card runs at x8 or x4 instead of x16 under load it may sit in the wrong slot or a riser is limiting it.",
                    HowTo = "Make sure the card is in the top full-length slot. In the BIOS, PCIe slot speed should be 'Auto'.",
                    Evidence = EvidenceRating.Situational
                }, "pcie");
        }
        else if (s.GpuVendor is "amd" or "intel")
        {
            Add(() => new BiosCheck
            {
                Title = s.GpuVendor == "amd" ? "Smart Access Memory (Resizable BAR)" : "Resizable BAR (required for Intel Arc)",
                Severity = s.GpuVendor == "intel" ? Severity.Warning : Severity.Info,
                Detected = "Can't be read reliably from Windows; check in the GPU control panel",
                Why = s.GpuVendor == "intel"
                    ? "Intel Arc cards lose a lot of performance without Resizable BAR."
                    : "Lets the CPU access all of the graphics card's memory. A few percent in some games.",
                HowTo = ReBarHowTo(v), Evidence = s.GpuVendor == "intel" ? EvidenceRating.Proven : EvidenceRating.Situational
            }, "rebar");
        }

        // ---- BIOS mode, Secure Boot, TPM (Windows 11 / anti-cheat requirements) -------------
        Add(() =>
        {
            // GetFirmwareType: 1 = legacy BIOS, 2 = UEFI.
            var type = NativeMethods.GetFirmwareType(out var t) ? t : 0;
            var uefi = type == 2;
            return new BiosCheck
            {
                Title = "Boot mode",
                Severity = uefi ? Severity.Good : type == 1 ? Severity.Warning : Severity.Info,
                Detected = uefi ? "UEFI" : type == 1 ? "Legacy BIOS / CSM" : "Unknown",
                Why = "Windows 11, Secure Boot and some anti-cheats (Valorant, newer Call of Duty) need UEFI mode.",
                HowTo = "Switching an existing install from Legacy to UEFI needs the disk converted first (Microsoft's mbr2gpt tool). Don't just flip the BIOS setting.",
                Evidence = EvidenceRating.Proven
            };
        }, "uefi");

        Add(() =>
        {
            var sb = RegistryService.Read("HKLM", @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            var on = sb.Exists && sb.Value == "1";
            return new BiosCheck
            {
                Title = "Secure Boot",
                Severity = on ? Severity.Good : Severity.Info,
                Detected = on ? "On" : sb.Exists ? "Off" : "Not available (legacy boot)",
                Why = "Required by Windows 11 and by Riot Vanguard (Valorant/League) and FACEIT on some systems. It has no effect on FPS.",
                HowTo = BootSecurityHowTo(v), Evidence = EvidenceRating.Proven
            };
        }, "secureboot");

        Add(() =>
        {
            ManagementObject? tpm;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm",
                    "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm");
                tpm = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                return new BiosCheck
                {
                    Title = "TPM (fTPM / PTT)", Severity = Severity.Info,
                    Detected = "Needs admin rights to read (run Vanta as administrator)",
                    Why = "Needed for Windows 11 and some anti-cheats. No effect on FPS.",
                    HowTo = amd ? "Usually under Advanced > AMD fTPM configuration (or 'Security')." : "Usually under Advanced/Security > Intel Platform Trust Technology (PTT).",
                    Evidence = EvidenceRating.Proven
                };
            }
            var enabled = tpm != null && tpm["IsEnabled_InitialValue"] is true;
            var spec = tpm?["SpecVersion"]?.ToString()?.Split(',')[0].Trim() ?? "";
            return new BiosCheck
            {
                Title = "TPM (fTPM / PTT)",
                Severity = enabled ? Severity.Good : Severity.Info,
                Detected = tpm == null ? "No TPM found (or it is turned off in the BIOS)" : enabled ? $"On, version {spec}" : "Present but off",
                Why = "Needed for Windows 11 and some anti-cheats. No effect on FPS. (Old firmware TPMs on Ryzen could cause rare stutter; a BIOS update fixes that.)",
                HowTo = amd ? "Usually under Advanced > AMD fTPM configuration (or 'Security')." : "Usually under Advanced/Security > Intel Platform Trust Technology (PTT).",
                Evidence = EvidenceRating.Proven
            };
        }, "tpm");

        // ---- Virtualization + VBS -------------------------------------------------------------
        Add(() =>
        {
            bool? virt = null;
            using (var s1 = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor"))
                foreach (ManagementObject o in s1.Get()) { virt = o["VirtualizationFirmwareEnabled"] as bool?; break; }
            bool hyper = false;
            using (var s2 = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in s2.Get()) { hyper = o["HypervisorPresent"] is true; break; }

            return new BiosCheck
            {
                Title = amd ? "CPU virtualization (SVM)" : "CPU virtualization (VT-x)",
                Severity = Severity.Info,
                Detected = hyper ? "On, and Windows is using it (Hyper-V / VBS active)" : virt == true ? "On in BIOS" : virt == false ? "Off in BIOS" : "Unknown",
                Why = "Needed for WSL, Android apps, emulators and some anti-cheats. Leaving it on costs nothing by itself; the small FPS cost comes from Windows features that use it (VBS / Memory Integrity, see Tweaks).",
                HowTo = amd ? "Advanced > CPU Configuration > SVM Mode." : "Advanced > CPU Configuration > Intel Virtualization Technology.",
                Evidence = EvidenceRating.Situational
            };
        }, "virt");

        Add(() =>
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard");
            var o = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            var status = o?["VirtualizationBasedSecurityStatus"] is uint u ? (int)u : -1;
            var services = o?["SecurityServicesRunning"] as uint[] ?? Array.Empty<uint>();
            var hvci = services.Contains(2u);
            return new BiosCheck
            {
                Title = "Virtualization-based security (VBS / Memory Integrity)",
                Severity = status == 2 ? Severity.Info : Severity.Good,
                Detected = status switch
                {
                    2 => hvci ? "Running, with Memory Integrity" : "Running",
                    1 => "Enabled but not running",
                    0 => "Off",
                    _ => "Unknown"
                },
                Why = "A security layer that can cost a few percent in CPU-heavy games. It is a Windows setting, not a BIOS one: Vanta's Tweaks page has it (Experts only) with the security trade-off explained.",
                HowTo = "Windows Security > Device security > Core isolation, or Vanta > Tweaks > 'Turn off Memory Integrity'.",
                Evidence = EvidenceRating.Situational
            };
        }, "vbs");

        // ---- Fast startup (Windows side of "fast boot") ------------------------------------------
        Add(() =>
        {
            var fs = RegistryService.Read("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
            var on = !fs.Exists || fs.Value == "1";
            return new BiosCheck
            {
                Title = "Fast Startup (Windows) and Fast Boot (BIOS)",
                Severity = Severity.Info,
                Detected = on ? "Windows Fast Startup is on" : "Windows Fast Startup is off",
                Why = "Both only speed up booting. Fast Startup means 'Shut down' doesn't fully restart Windows, so use Restart after driver installs. BIOS Fast Boot can make it hard to get into the BIOS; Vanta's 'Reboot to BIOS' button works either way.",
                HowTo = "Fast Startup: Control Panel > Power Options > Choose what the power buttons do. Fast Boot: BIOS > Boot.",
                Evidence = EvidenceRating.Marginal
            };
        }, "faststartup");

        // ---- CPU-specific firmware notes -------------------------------------------------------
        if (amd)
            Add(() => new BiosCheck
            {
                Title = "Precision Boost Overdrive (PBO) and Curve Optimizer",
                Severity = Severity.Info,
                Detected = "Can't be read from Windows",
                Why = s.CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase)
                    ? "On X3D chips, a negative Curve Optimizer (undervolt) can lower temperatures and hold boost clocks longer. Gains are small; stability testing is required."
                    : "PBO lets the CPU boost longer if cooling allows; a negative Curve Optimizer undervolts for cooler, steadier clocks. Typical gains 0-5%. Needs stability testing (e.g. OCCT, CoreCycler).",
                HowTo = v switch
                {
                    BoardVendor.Asus => "Advanced > AMD Overclocking > Precision Boost Overdrive (or AI Tweaker > Precision Boost Overdrive).",
                    BoardVendor.Msi => "OC > Advanced CPU Configuration > AMD Overclocking > Precision Boost Overdrive.",
                    BoardVendor.Gigabyte => "Tweaker > Advanced CPU Settings > Precision Boost Overdrive.",
                    BoardVendor.AsRock => "Advanced > AMD Overclocking > Precision Boost Overdrive.",
                    _ => "Look for 'AMD Overclocking' > 'Precision Boost Overdrive' in the advanced section."
                },
                Evidence = EvidenceRating.Situational
            }, "pbo");

        if (intel)
        {
            var raptor = System.Text.RegularExpressions.Regex.IsMatch(s.CpuName, @"i[579]-1[34]\d{3}");
            Add(() => new BiosCheck
            {
                Title = raptor ? "Intel 13th/14th gen stability (microcode)" : "Intel power limits",
                Severity = raptor ? Severity.Warning : Severity.Info,
                Detected = raptor ? $"{s.CpuName.Trim()}: check that your BIOS has the latest microcode" : "Can't be read from Windows",
                Why = raptor
                    ? "Intel confirmed 13th/14th gen desktop CPUs can degrade from too-high voltage. BIOS updates with microcode 0x12B or newer fix the cause. This matters far more than any FPS tweak."
                    : "Boards often run Intel CPUs past Intel's official power limits. That adds heat for little gain; 'Intel Default Settings' is the safe baseline.",
                HowTo = raptor
                    ? "Update the BIOS from your board maker's support page, then choose the 'Intel Default Settings' profile."
                    : "Look for 'Intel Default Settings' or power limit options (PL1/PL2) under the CPU or overclocking menu.",
                Evidence = raptor ? EvidenceRating.Proven : EvidenceRating.Situational
            }, "intel");
        }

        // ---- BIOS age -----------------------------------------------------------------------------
        Add(() =>
        {
            DateTime.TryParse(s.BiosDate, out var date);
            var old = date != default && (DateTime.Now - date).TotalDays > 730;
            return new BiosCheck
            {
                Title = "BIOS version",
                Severity = old ? Severity.Warning : Severity.Info,
                Detected = $"{s.BiosVersion}" + (date != default ? $", dated {date:yyyy-MM-dd}" : ""),
                Why = old
                    ? "Your BIOS is over two years old. Updates often fix memory compatibility, stutter (e.g. AMD fTPM), stability and security. Only update if there is a newer version for your exact board."
                    : "Check your board maker's site now and then for updates that fix stability or memory compatibility.",
                HowTo = "Download it only from your board maker's support page for your exact model, use the built-in BIOS flash tool (EZ Flash, M-Flash, Q-Flash, Instant Flash) from a USB stick, and don't turn the PC off during the update. Vanta never flashes a BIOS.",
                Evidence = EvidenceRating.Situational
            };
        }, "biosage");

        // ---- Guidance-only items -------------------------------------------------------------------
        list.Add(new BiosCheck
        {
            Title = "Fan curves",
            Severity = Severity.Info,
            Detected = "Set in the BIOS or the board maker's app",
            Why = "A good fan curve keeps the CPU cool under gaming load without being loud at idle. Hot CPUs boost lower.",
            HowTo = v switch
            {
                BoardVendor.Asus => "Press F6 in the BIOS (Q-Fan Control).",
                BoardVendor.Msi => "Hardware Monitor in the BIOS (F7 for Advanced mode first).",
                BoardVendor.Gigabyte => "Smart Fan 6 (F6) in the BIOS.",
                BoardVendor.AsRock => "H/W Monitor > FAN-Tastic Tuning.",
                _ => "Look for 'Hardware Monitor' or 'Fan control' in the BIOS."
            },
            Evidence = EvidenceRating.Situational
        });

        list.Add(new BiosCheck
        {
            Title = "C-states and power saving",
            Severity = Severity.Info,
            Detected = "Leave on (recommended)",
            Why = "Old guides say to disable C-states for gaming. On modern CPUs that mainly adds heat and can even lower boost clocks. Leave them on unless you are chasing a specific latency problem.",
            HowTo = "No change needed.",
            Evidence = EvidenceRating.Marginal
        });

        if (intel && s.RamType.Contains("DDR4"))
            list.Add(new BiosCheck
            {
                Title = "Memory gear mode",
                Severity = Severity.Info,
                Detected = "Can't be read from Windows",
                Why = "On Intel 11th-14th gen with DDR4, 'Gear 1' keeps the memory controller in sync and gives lower latency than Gear 2. XMP usually picks the right one.",
                HowTo = "Memory / DRAM settings > Gear Mode (only change if you know your RAM speed supports Gear 1).",
                Evidence = EvidenceRating.Situational
            });

        // Problems first, then warnings, then the rest.
        return list.OrderByDescending(c => c.Severity == Severity.Problem)
                   .ThenByDescending(c => c.Severity == Severity.Warning)
                   .ThenBy(c => c.Severity == Severity.Good)
                   .ToList();
    }

    private static string MemoryHowTo(BoardVendor v, bool amd) => v switch
    {
        BoardVendor.Asus => "ASUS: press F7 for Advanced mode > AI Tweaker > Ai Overclock Tuner > " + (amd ? "EXPO I / D.O.C.P." : "XMP I") + ". (On ASUS EZ Mode it is the 'XMP/EXPO' box.)",
        BoardVendor.Msi => "MSI: on the main screen click the 'XMP' / 'A-XMP' / 'EXPO' button, or OC > Extreme Memory Profile.",
        BoardVendor.Gigabyte => "Gigabyte: Tweaker > Extreme Memory Profile (X.M.P.) > Profile 1 (shown as EXPO on AM5).",
        BoardVendor.AsRock => "ASRock: OC Tweaker > DRAM Configuration > Load XMP / EXPO Setting > Profile 1.",
        _ => "In the BIOS look for 'XMP', 'EXPO', 'DOCP' or 'Memory Profile' (often on the first screen) and pick Profile 1."
    };

    private static string ReBarHowTo(BoardVendor v) =>
        "Turn on both 'Above 4G Decoding' and 'Re-Size BAR Support' (needs UEFI boot / CSM off). " + v switch
        {
            BoardVendor.Asus => "ASUS: Advanced > PCI Subsystem Settings.",
            BoardVendor.Msi => "MSI: Settings > Advanced > PCI Subsystem Settings.",
            BoardVendor.Gigabyte => "Gigabyte: Settings > IO Ports.",
            BoardVendor.AsRock => "ASRock: Advanced > Chipset Configuration.",
            _ => "Usually under Advanced > PCI / PCIe settings."
        };

    private static string BootSecurityHowTo(BoardVendor v) => v switch
    {
        BoardVendor.Asus => "ASUS: Boot > Secure Boot > OS Type 'Windows UEFI mode'.",
        BoardVendor.Msi => "MSI: Settings > Security > Secure Boot.",
        BoardVendor.Gigabyte => "Gigabyte: Boot > Secure Boot.",
        BoardVendor.AsRock => "ASRock: Security > Secure Boot.",
        _ => "Usually under Boot or Security > Secure Boot (CSM must be off)."
    };

    public static string SupportSearchUrl(SystemSummary s) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString($"{s.Motherboard} BIOS download support");

    /// <summary>Restarts straight into the UEFI setup screen (Windows' documented `shutdown /fw`).</summary>
    public static Task<ProcResult> RebootToFirmwareAsync() =>
        ProcessRunner.RunAsync("shutdown.exe", "/r /fw /t 5 /c \"Vanta: restarting into your BIOS / UEFI settings\"", 15_000);

    // ---- Export ---------------------------------------------------------------------------------
    public static string ToHtml(SystemSummary s, IEnumerable<BiosCheck> checks)
    {
        static string E(string x) => WebUtility.HtmlEncode(x);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=\"utf-8\"><title>BIOS checklist</title>")
          .Append("<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:860px;margin:32px auto;color:#111}" +
                  "h1{margin:0}small{color:#666}.c{border:1px solid #ddd;border-radius:10px;padding:12px 16px;margin:12px 0}" +
                  ".t{font-weight:600}.Warning{border-left:6px solid #e8a000}.Problem{border-left:6px solid #d33}" +
                  ".Good{border-left:6px solid #2a8}.Info{border-left:6px solid #88a}p{margin:6px 0}input{margin-right:8px}</style>")
          .Append($"<h1>BIOS checklist</h1><small>{E(s.Motherboard)} · BIOS {E(s.BiosVersion)} {E(s.BiosDate)} · {E(s.CpuName)} · made by Vanta {DateTime.Now:yyyy-MM-dd}</small>");
        foreach (var c in checks)
            sb.Append($"<div class=\"c {c.Severity}\"><div class=\"t\"><input type=\"checkbox\">{E(c.Title)}</div>")
              .Append($"<p><b>Detected:</b> {E(c.Detected)}</p><p>{E(c.Why)}</p><p><b>Where:</b> {E(c.HowTo)}</p></div>");
        sb.Append("<p><small>Change one setting at a time and make sure the PC boots before changing the next. " +
                  "If it won't boot, clearing CMOS (see your manual) resets the BIOS to defaults.</small></p>");
        return sb.ToString();
    }
}
