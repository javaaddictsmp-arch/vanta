using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private SystemSummary? _summary;

    public DashboardViewModel()
    {
        Cpu = new GaugeVm("CPU load");
        Gpu = new GaugeVm("GPU load");
        Ram = new GaugeVm("Memory");
        Vram = new GaugeVm("VRAM");
        CpuTemp = new GaugeVm("CPU temperature");
        GpuTemp = new GaugeVm("GPU temperature");
        Disk = new GaugeVm("Disk activity");
        Net = new GaugeVm("Network");
        Gauges = new[] { Cpu, Gpu, Ram, Vram, CpuTemp, GpuTemp, Disk, Net };

        AppServices.Hub.SampleReceived += s => UiThread.Post(() => ApplySample(s));
    }

    public GaugeVm Cpu { get; }
    public GaugeVm Gpu { get; }
    public GaugeVm Ram { get; }
    public GaugeVm Vram { get; }
    public GaugeVm CpuTemp { get; }
    public GaugeVm GpuTemp { get; }
    public GaugeVm Disk { get; }
    public GaugeVm Net { get; }
    public IReadOnlyList<GaugeVm> Gauges { get; }

    public ObservableCollection<SummaryRow> Summary { get; } = new();
    public ObservableCollection<HealthItem> HealthItems { get; } = new();

    [ObservableProperty] private int _healthScore;
    [ObservableProperty] private string _healthLabel = "Scanning...";
    [ObservableProperty] private int _healthLevel;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    /// <summary>Set by MainViewModel: opens a page by its sidebar title, optionally a Boost Center tab index.</summary>
    public Action<string, int>? Navigate { get; set; }

    [RelayCommand]
    private void OpenBooster() => Navigate?.Invoke("FPS Boost Center", 0);

    [RelayCommand]
    private void OpenBenchmark() => Navigate?.Invoke("FPS Boost Center", 2);

    [RelayCommand]
    private void OpenTweaks() => Navigate?.Invoke("Tweaks", 0);

    // ---------------------------------------------------------------------------------------
    public async Task InitializeAsync() => await ScanAsync();

    [RelayCommand]
    private async Task FullScanAsync() => await ScanAsync();

    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning hardware and settings...";
        try
        {
            _summary = await AppServices.SystemInfo.ScanAsync();
            FillSummary(_summary);
            RefreshHealth();
            StatusMessage = $"Scan finished at {DateTime.Now:t}.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Full scan failed", ex);
            StatusMessage = "The scan hit a problem. Details are in the log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task QuickOptimizeAsync()
    {
        // Engine-side rule: only Safe + Proven + flagged tweaks. A catalog typo can't widen this.
        var candidates = AppServices.Catalog.Tweaks
            .Where(t => t.QuickOptimize && t.Risk == RiskLevel.Safe && t.Evidence == EvidenceRating.Proven)
            .Where(TweakEngine.IsApplicable)
            .ToList();

        var pending = candidates.Where(t => !AppServices.Engine.IsApplied(t)).ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "Nothing to do: every proven, safe tweak is already applied.";
            return;
        }

        var list = string.Join("\n", pending.Select(t => "  - " + t.Name));
        var answer = MessageBox.Show(
            "Quick Optimize only applies changes that are low-risk and have a real, measurable effect.\n\n" +
            list +
            "\n\nBefore changing anything Vanta creates a restore point (if System Restore is on) and saves " +
            "a registry backup. You can undo everything from the Change Log page.\n\nContinue?",
            "Quick Optimize", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusMessage = m);
            var r = await AppServices.Engine.ApplyManyAsync(pending, progress);

            var parts = new List<string> { $"Applied {r.Applied} tweak(s)" };
            if (r.Failed > 0) parts.Add($"{r.Failed} failed (see log)");
            if (r.SignOut) parts.Add("sign out and back in for the mouse change to take effect");
            if (r.Reboot) parts.Add("restart Windows to finish");
            StatusMessage = string.Join(". ", parts) + ".";

            if (AppServices.Engine.LastRestorePointMessage is { } rp)
                StatusMessage += " " + rp;
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick optimize failed", ex);
            StatusMessage = "Quick Optimize stopped because of an error. Details are in the log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------------------------------
    private void FillSummary(SystemSummary s)
    {
        Summary.Clear();
        Summary.Add(new("CPU", s.CpuName, $"{s.CpuCores} cores / {s.CpuThreads} threads"));
        Summary.Add(new("GPU", s.GpuName, string.IsNullOrEmpty(s.GpuDriver) ? "" : $"driver {s.GpuDriver}"));
        Summary.Add(new("Memory", $"{s.RamTotalGb:0.#} GB {s.RamType}".Trim(), s.RamStatus));
        if (!string.IsNullOrEmpty(s.RamChannels)) Summary.Add(new("Memory layout", s.RamChannels, ""));
        Summary.Add(new("Motherboard", s.Motherboard, ""));
        Summary.Add(new("BIOS", s.BiosVersion, string.IsNullOrEmpty(s.BiosDate) ? "" : $"dated {s.BiosDate}"));
        Summary.Add(new("Windows", s.OsName, $"build {s.OsBuild}"));

        foreach (var d in s.Storage)
            Summary.Add(new("Storage", d.Model, $"{d.SizeGb:0} GB - {d.Type}"));

        if (s.RefreshCurrentHz > 0)
            Summary.Add(new("Display", $"{s.Resolution} @ {s.RefreshCurrentHz} Hz", s.RefreshMaxHz > 0 ? $"max {s.RefreshMaxHz} Hz at this resolution" : ""));
    }

    private void RefreshHealth()
    {
        if (_summary == null) return;

        var report = AppServices.Health.Evaluate(_summary, AppServices.Hub.Latest, AppServices.Settings.Current);

        HealthItems.Clear();
        // Problems first, then passed checks.
        foreach (var item in report.Items.OrderByDescending(i => i.Severity)) HealthItems.Add(item);

        HealthScore = report.Score;
        HealthLevel = report.Score >= 85 ? 0 : report.Score >= 65 ? 1 : 2;
        HealthLabel = report.Score >= 85 ? "Looking good" : report.Score >= 65 ? "Room to improve" : "Needs attention";
    }

    // ---------------------------------------------------------------------------------------
    private int _healthTick;

    private void ApplySample(MetricSample s)
    {
        var cfg = AppServices.Settings.Current;

        Cpu.Value = s.CpuLoad;
        Cpu.Text = $"{s.CpuLoad:0}%";
        Cpu.SubText = s.CpuClockMhz is { } clk ? $"{clk / 1000.0:0.00} GHz" : "";
        Cpu.Level = s.CpuLoad >= 95 ? 1 : 0;

        if (s.GpuLoad is { } gl)
        {
            Gpu.Value = gl;
            Gpu.Text = $"{gl:0}%";
            Gpu.SubText = s.GpuClockMhz is { } gc ? $"{gc:0} MHz" : "";
            Gpu.Level = 0;
        }
        else Gpu.SetUnavailable();

        Ram.Value = s.RamPercent;
        Ram.Text = $"{s.RamPercent:0}%";
        Ram.SubText = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB";
        Ram.Level = s.RamPercent >= 90 ? 1 : 0;

        if (s.GpuMemPercent is { } vp && s.GpuMemUsedMb is { } used && s.GpuMemTotalMb is { } total)
        {
            Vram.Value = vp;
            Vram.Text = $"{used / 1024.0:0.0} GB";
            Vram.SubText = $"of {total / 1024.0:0.0} GB";
            Vram.Level = vp >= 92 ? 1 : 0;
        }
        else Vram.SetUnavailable();

        if (s.CpuTempC is { } ct)
        {
            CpuTemp.Value = ct;
            CpuTemp.Text = $"{ct:0}°C";
            CpuTemp.SubText = s.CpuPowerW is { } cp ? $"{cp:0} W" : "";
            CpuTemp.Level = ct >= cfg.CpuTempAlertC ? 2 : ct >= cfg.CpuTempAlertC - 10 ? 1 : 0;
        }
        else CpuTemp.SetUnavailable("see Monitoring");

        if (s.GpuTempC is { } gt)
        {
            GpuTemp.Value = gt;
            GpuTemp.Text = $"{gt:0}°C";
            GpuTemp.SubText = s.GpuPowerW is { } gp ? $"{gp:0} W" : "";
            GpuTemp.Level = gt >= cfg.GpuTempAlertC ? 2 : gt >= cfg.GpuTempAlertC - 10 ? 1 : 0;
        }
        else GpuTemp.SetUnavailable();

        if (s.DiskActivePercent is { } da)
        {
            Disk.Value = da;
            Disk.Text = $"{da:0}%";
            var readMb = s.DiskReadMBs ?? 0;
            var writeMb = s.DiskWriteMBs ?? 0;
            Disk.SubText = $"R {readMb:0.0}  W {writeMb:0.0} MB/s";
            Disk.Level = 0;
        }
        else Disk.SetUnavailable("no counter");

        var mbps = (s.NetDownMBs + s.NetUpMBs) * 8.0;
        Net.Value = s.NetLinkMbps > 0 ? Math.Min(100, mbps / s.NetLinkMbps * 100.0) : 0;
        Net.Text = $"{s.NetDownMBs:0.0}";
        Net.SubText = $"MB/s down, {s.NetUpMBs:0.0} up";

        // Re-evaluate health every ~5 samples so live temperature checks stay current.
        if (_summary != null && ++_healthTick % 5 == 0) RefreshHealth();
    }
}
