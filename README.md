<img src="Assets/vanta-logo.png" width="96" alt="Vanta logo">

# Vanta

An honest, free and open-source PC optimizer, game booster and monitor for Windows 10 and 11.
No telemetry, no ads, no registry "cleaner", no fake error counts, and everything it changes can be undone.

## Download

Get **Vanta-Setup.exe** (or the portable .zip) from the [Releases page](../../releases/latest).
Windows 10 1809+ or Windows 11, 64-bit. Vanta asks for admin rights because it changes Windows settings.

> **"Windows protected your PC"?** New apps that aren't code-signed get this warning. Click
> **More info → Run anyway**. Each release lists SHA-256 checksums (`SHA256SUMS.txt`), and you can
> always build it yourself from this source (see [Build](#build)).

To uninstall, use Windows Settings → Apps. The uninstaller asks whether to undo every change first.

## Releasing a new version (maintainers)

Bump nothing by hand: on GitHub open **Actions → Release → Run workflow**, type the version
(for example `0.4.0`) and run it. GitHub builds `Vanta.exe`, the installer (`installer/vanta.iss`,
Inno Setup) and the portable zip, and publishes them as a release. Pushing a tag like `v0.4.0` does
the same. The download page in `docs/` is served by GitHub Pages and always links the latest release.

---

*Everything below is the technical documentation.* (Working name during development was "TuneUp";
code namespaces still use it. Data in `%LocalAppData%\TuneUp` is moved to `%LocalAppData%\Vanta`
automatically on first start.)

**Design:** dark theme with an accent gradient (pick the accent in Settings), animated buttons,
switches, navigation and tabs, and a plain-English layer for non-technical users:
every page and tab opens with an "In plain English" explainer, every tweak shows a one-line
summary plus a beginner verdict (Recommended / Worth a try / Only if it fits your PC /
Skip unless curious / Experts only) next to the honest Risk and Evidence ratings, with the
technical text under "How it works and the evidence". The Dashboard has a 3-step "Get started" card.
Logo: `Assets/vanta-logo.png`; regenerate the EXE icon with `Assets/make-icon.ps1`.

**Phase 1:** project skeleton, tweak engine with backup + undo, JSON tweak catalog,
Dashboard, Monitoring, Change Log, Settings.

**Phase 4: everything else**

| Page | What it does |
|---|---|
| Cleaning & RAM | Junk cleaner with sizes measured first (temp, Windows Update / Delivery Optimization caches, crash dumps, thumbnails, browser caches without touching passwords/cookies, shader caches with a stutter warning, Recycle Bin, Windows.old via Disk Cleanup). Biggest files, folder sizes, SHA-256 duplicate finder with Recycle Bin removal. Game library sizes with SSD/HDD per drive, TRIM status, "Optimize system drive" (defrag /O). RAM: usage + commit/page-file pressure, top memory users, leak watch (private memory climbing 10+ min), standby cleaner (manual or automatic below a threshold), working-set trim (marked Marginal). |
| Startup & Services | Startup apps from Run keys, Startup folders and logon tasks, with advice tags; disabled via the same StartupApproved flags as Task Manager, through the undo pipeline. Curated services list only (critical services never offered) with Gaming / Work / Minimal presets. Process manager: CPU/memory, priority, low disk priority, freeze/resume, end task (protected processes refused), add to boost suspend list. |
| Network & Ping | Live ping/jitter/loss monitor to cloud game regions or any host (ICMP with TCP fallback), "ping all regions", traceroute, Cloudflare speed test with bufferbloat grade, DNS changer with raw-UDP benchmark and restore of the original, flush DNS / reset stack, NIC advanced properties (interrupt moderation, EEE/Green Ethernet, power saving) with original-value backup, adapter power saving off, MTU test, Wi-Fi channel advice, QoS upload limit / DSCP priority per app. |
| Privacy & Tools | 13 privacy/quality-of-life tweaks in the catalog (advertising ID, tailored experiences, Start suggestions + silent app installs, lock screen tips, required-only diagnostics, Bing in Start, activity history, feedback prompts, Copilot, Recall, file extensions, Edge startup boost). Curated built-in app removal with warnings. Installed programs with uninstall and leftover-folder scan. SFC / DISM / CHKDSK with live output, battery report, shareable HTML system report, weekly maintenance task (`Vanta.exe --maintenance`), old-game compatibility flags. |
| Settings | Simple / Full mode, "Run setup again", start with Windows to the tray (scheduled task, no UAC prompt), minimize to tray, tray menu (auto-boost, boost now, power plan), customizable Boost hotkey (default Ctrl+Alt+F10, shows when another app already owns the combination), export/import settings (.zip), "Reset everything to Windows defaults". |

**After Phase 4**

- **First-run setup:** three steps on first start (Simple or Full, quick health check with "Apply safe fixes", auto-boost / session reports / start with Windows). Skippable; Settings > Run setup again.
- **Simple mode:** hides Tweaks, Monitoring, Privacy & Tools and the expert tabs (CPU & Power, Timer, Latency, Services, Processes, Adapter & Wi-Fi, QoS). Only hidden, nothing is turned off; a link to a hidden page switches back to Full.
- **Live game connection** (Network & Ping > Ping): finds the server the running game is talking to (kernel network ETW for UDP, needs admin; TCP connection table as fallback) and pings it every second. If the server ignores ping, it measures to the last router before it and says so.
- **Session reports** (FPS Boost Center): every game session over 2 minutes gets a report: duration, boosted or not and what the boost did, avg / 1% / 0.1% FPS and stutters (background ETW, kept as a 0.1 ms histogram so long sessions stay small), FPS and ping graphs, temperatures, loads, RAM/VRAM peak, bottleneck verdict, and a comparison with the previous session of the same game (with a noise caveat). Saved as JSON in `%LocalAppData%\Vanta\Sessions`; a tray pop-up announces each one.
- `VANTA_DATA_DIR` environment variable points Vanta at another data folder (for testing a build without touching your real settings).

**Phase 3: System Scan** (runs on first visit; scanning never changes anything)

| Tab | What it does |
|---|---|
| What to fix first | One list from every check below plus the health score and Windows settings, ranked High / Medium / Low impact, each with an evidence rating and a button to the fix. |
| BIOS & Firmware | Reads what Windows can see: XMP/EXPO (RAM vs rated speed), Resizable BAR + PCIe link (NVIDIA), UEFI vs legacy, Secure Boot, TPM, SVM/VT-x, VBS/Memory Integrity, Fast Startup, BIOS age, Intel 13th/14th gen microcode warning, PBO/Curve Optimizer, power limits, fan curves, C-states, gear mode. Menu paths for ASUS / MSI / Gigabyte / ASRock. "Reboot to BIOS" (`shutdown /r /fw`) and an exportable HTML checklist. Never changes or flashes the BIOS. |
| Drivers & Updates | Graphics/chipset/network/audio/storage/USB drivers with version and age, generic-driver and missing-chipset-driver flags, official download links, driver backup (`pnputil /export-driver`), winget app upgrades installed one by one on click, pending Windows Updates (read-only). |
| Hardware Health | Drive health from Windows' storage provider (SSD wear, temperature, power-on hours, error counters), thermal/power throttling from recent sensor history, Windows Memory Diagnostic launcher, PSU warning signs. |

**Phase 2: FPS Boost Center**

| Tab | What it does |
|---|---|
| Game Booster | Detects games (Steam, Epic, GOG, Xbox, Battle.net, EA, Ubisoft, Riot, Minecraft, manual EXEs), auto-boosts on launch and restores on exit **or crash**. Per-game profiles with JSON import/export. Steps: High priority, ProBalance, CPU core presets, performance power plan, standby trim (only when RAM is low), clipboard clear, Win-key block, Sticky Keys prompts off, mute notifications, pause Search/Update, suspend/close chosen apps, timer resolution, overlay. |
| Benchmark & Bottleneck | ETW frametime capture (same providers as PresentMon), avg / 1% / 0.1% lows, stutter count, A/B compare with a run-to-run noise estimate, frametime graph, history. Bottleneck finder (CPU/GPU-bound, VRAM, RAM, thermal, power limit, disk). |
| CPU & Power | Real core topology (Intel P/E, AMD X3D V-Cache CCD, SMT), "Vanta Performance" plan via powercfg (min/max state, boost mode, EPP, core parking, USB/PCIe power), Ultimate Performance, idle saver, ProBalance settings, persistent per-app priority/affinity rules. |
| Timer | Current/finest resolution, honest Win10/Win11 per-process explanation, Sleep(1) benchmark, `powercfg /energy` requester list, Win11 GlobalTimerResolutionRequests tweak. |
| GPU & Display | Refresh-rate check, VRR FPS-cap suggestion, per-EXE GPU preference and FSO (through the undo pipeline), Game Mode/DVR/HAGS/MPO/windowed-optimizations tweaks, NVIDIA details via nvidia-smi (power limit, clocks, PCIe, ReBAR), vendor guidance, DLSS DLL viewer. |
| Game Configs | JSON-driven presets (Max FPS / Balanced / Visual) for CS2, Fortnite, Apex, Minecraft, Rocket League, League; guidance for Valorant, CoD, Overwatch 2, Roblox, GTA V, Cyberpunk, Elden Ring. Backup first, only existing keys changed, community preset import. |
| Latency | DPC/ISR sampling with worst driver named (kernel ETW), MSI-mode list/enable (Advanced, undoable), input tweaks, interrupt-affinity and HPET guidance. |

### How a boost stays safe

`Services/BoostEngine.cs` writes `%LocalAppData%\Vanta\boost-state.json` **before** each change.
When a boost starts, Vanta launches a windowless copy of itself (`Vanta.exe --watchdog <pid>`).
If the main app dies, the watchdog restores everything from that file. If both die (power loss),
the next Vanta start restores it. Things that die with the process anyway (keyboard hook, timer
request) need no restore. Vanta never injects into games; priority/affinity changes that Windows
or an anti-cheat refuses are simply skipped.

## Build

1. Install the **.NET 8 SDK** (not just the runtime): https://dotnet.microsoft.com/download/dotnet/8.0
2. Double-click `build.bat`, or run:

```
dotnet publish TuneUp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

3. Your EXE is `publish\Vanta.exe` (single file, self-contained, asks for admin on launch).

To debug in Visual Studio, start VS **as administrator** (the manifest requires it).

## How the safety pipeline works

Every change goes through `Services/TweakEngine.cs`:

1. Restore point (once per session, if System Restore is on and the setting is enabled)
2. `reg export` of each affected key to `%LocalAppData%\Vanta\Backups\<timestamp>-<tweak>\`
3. Snapshot of each value's current state (including "did not exist")
4. Write; if any write fails, everything already written for that tweak is rolled back
5. Entry saved to `%LocalAppData%\Vanta\changelog.json`

Undo / Revert / "Revert all changes" restore the exact snapshots. If a tweak was already active
before Vanta touched it, Revert falls back to the Windows default written in the catalog.

## Adding tweaks (no code changes)

Edit `Data/tweaks.json` before building, or drop extra `.json` files into
`%LocalAppData%\Vanta\Catalog\` (same format; same `id` overrides the built-in one).
Each tweak has: `risk` (Safe / Moderate / Advanced), `evidence` (Proven / Situational / Marginal),
`evidenceNote`, `warning`, `rebootRequired`, `signOutRequired`, `appliesTo`
(Both / Win10 / Win11), `quickOptimize`, and a list of `operations`
(`registry` or `service`). Quick Optimize only ever applies tweaks that are
**Safe + Proven + quickOptimize:true**, enforced in code.

## Folder tree

```
Vanta/
  TuneUp.csproj, app.manifest, build.bat, README.md
  App.xaml(.cs)            app start, accent colour, crash handler
  MainWindow.xaml(.cs)     sidebar + page host
  Data/tweaks.json         built-in tweak catalog (embedded in the EXE)
  Models/                  enums, tweak + change-log + metric models
  Services/                Registry, Backup, RestorePoint, TweakEngine, ChangeLog,
                           HardwareMonitor, MetricsHub, SystemInfo, HealthScore, CsvLog, Settings
  ViewModels/              Main, Dashboard, Tweaks, Monitoring, ChangeLog, Settings
  Views/                   XAML pages
  Controls/                RingGauge, Sparkline (drawn directly, no chart library)
  Converters/, Themes/
```

## Step 0: what the competitor tools do, what I matched, what I skipped

Honest note: my web searches this session returned link lists only (no page content), so this
summary comes from what I already know about these tools, not from fresh reading. Treat details
about current paid-tier contents as unverified.

| Tool | Matched / planned | Skipped, and why |
|---|---|---|
| Hone | Per-game config presets (Phase 2, JSON-driven), backup/revert, honest BIOS guidance | Cloud "100k+ game" database and account system: needs a server; Vanta stays local. Bloat stripping is limited to reversible toggles |
| Razer Cortex | Auto-activate on game launch with crash-safe restore, priority, clipboard clear, Sticky Keys block (Phase 2) | "Kill processes" as a headline trick: modern Windows already schedules background load well, so it is opt-in per app, not automatic |
| Process Lasso / ParkControl | ProBalance-style limiting, persistent priority/affinity rules, power plan + core parking via powercfg (Phase 2) | Cannot match their years of rule tuning; kept simpler and always reversible |
| Wise / IObit / PC Game Booster | Startup, services and process tabs (Phase 4) | Registry cleaners and "issue counts": deliberately excluded (no measurable benefit, can break things) |
| Tier1Timer | Timer resolution with per-game Auto Mode and restore, plus a built-in benchmark (Phase 2) | On Windows 11 the global timer-resolution behavior changed (per-process), so the benchmark decides whether it helps, not a promise |
| Community tweaks | MPO, HAGS, MMCSS, Win32PrioritySeparation, network throttling, HVCI toggles (in Phase 1, rated honestly) | Several are rated **Marginal** and are off by default with an explanation. MSI mode / interrupt affinity come later as Advanced-only |

## Known limitations

- BIOS settings can only be advised on, never changed from Windows (Phase 3 adds a checklist).
- Many tweaks vary by hardware; the evidence rating says how much to trust each one.
- Turning off Memory Integrity (HVCI/VBS) lowers security and may conflict with some anti-cheat; it is Advanced with a warning.
- Game processes are only touched through normal Windows APIs (priority, affinity). Nothing is injected.
- Frametime capture and DPC sampling need admin (ETW). Vulkan/OpenGL games are captured only when the driver presents through DXGI/DxgKrnl.
- The overlay is a normal topmost window: visible over borderless/windowed games, not exclusive fullscreen.
- Timer resolution: on Windows 10 2004+ a request only affects the requesting process; Windows 11 needs the GlobalTimerResolutionRequests tweak for the per-game timer to matter.
- CPU topology handles processor group 0 only (up to 64 logical CPUs).
- Game presets only edit keys that already exist in your config file; values are starting points, not lab-verified.
- NVIDIA driver profile settings (low latency mode etc.) are guidance, not written directly (no NVAPI DRS editing).
- CPU temperature: recent LibreHardwareMonitor versions read CPU sensors through the PawnIO driver (pawnio.eu). If CPU temp shows N/A, install it. GPU readings do not need it.

## Roadmap

- Phase 2: done (see above). Not included: NVAPI driver-profile editing, stretched-resolution creation (use the GPU control panel), read-only fan-curve viewer
- Phase 3: done (see above)
- Phase 4: done (see above). Not included: disk treemap (folder-size list instead), per-process network usage, download bandwidth limiting (Windows QoS can only limit upload), automatic update check (comes with the download page)
- After Phase 4: done (see above): first-run setup, Simple mode, live game-server ping, session reports, custom hotkey
- Distribution: done: Inno Setup installer (uninstaller offers to undo every change), GitHub Actions release workflow, download page (`docs/`, GitHub Pages), opt-in update check that only links to the release page
- Next: code signing through SignPath (free for open source) so the SmartScreen warning goes away
