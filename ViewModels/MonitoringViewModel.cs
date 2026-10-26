using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private readonly MetricCardVm _cpuLoad = new("CPU load %", 100);
    private readonly MetricCardVm _cpuTemp = new("CPU temperature °C", 100);
    private readonly MetricCardVm _gpuLoad = new("GPU load %", 100);
    private readonly MetricCardVm _gpuTemp = new("GPU temperature °C", 100);
    private readonly MetricCardVm _ram = new("Memory %", 100);
    private readonly MetricCardVm _vram = new("VRAM %", 100);
    private readonly MetricCardVm _disk = new("Disk MB/s (read + write)", 0);
    private readonly MetricCardVm _net = new("Network MB/s (down)", 0);

    public MonitoringViewModel()
    {
        Cards = new[] { _cpuLoad, _cpuTemp, _gpuLoad, _gpuTemp, _ram, _vram, _disk, _net };

        _cpuTempLimit = AppServices.Settings.Current.CpuTempAlertC;
        _gpuTempLimit = AppServices.Settings.Current.GpuTempAlertC;

        AppServices.Hub.SampleReceived += s => UiThread.Post(() => Apply(s));
    }

    public IReadOnlyList<MetricCardVm> Cards { get; }
    public ObservableCollection<CoreVm> Cores { get; } = new();

    [ObservableProperty] private string _alertText = "";
    [ObservableProperty] private string _sensorNote = "";
    [ObservableProperty] private string _logFile = "";
    [ObservableProperty] private bool _isLogging;
    [ObservableProperty] private int _cpuTempLimit;
    [ObservableProperty] private int _gpuTempLimit;

    partial void OnIsLoggingChanged(bool value)
    {
        if (value)
        {
            AppServices.Csv.Start(AppServices.Hub);
            LogFile = AppServices.Csv.CurrentFile ?? "";
        }
        else
        {
            AppServices.Csv.Stop(AppServices.Hub);
            LogFile = "";
        }
    }

    partial void OnCpuTempLimitChanged(int value)
    {
        AppServices.Settings.Current.CpuTempAlertC = value;
        AppServices.Settings.Save();
    }

    partial void OnGpuTempLimitChanged(int value)
    {
        AppServices.Settings.Current.GpuTempAlertC = value;
        AppServices.Settings.Save();
    }

    [RelayCommand]
    private void OpenLogFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.MetricLogs}\"") { UseShellExecute = true });

    // ---------------------------------------------------------------------------------------
    private void Apply(MetricSample s)
    {
        _cpuLoad.Push(s.CpuLoad, $"{s.CpuLoad:0}%", s.CpuLoad >= 95 ? 1 : 0);

        _cpuTemp.Push(s.CpuTempC,
            s.CpuTempC is { } ct ? $"{ct:0}°C" : "N/A",
            s.CpuTempC is { } c2 && c2 >= CpuTempLimit ? 2 : 0);

        _gpuLoad.Push(s.GpuLoad, s.GpuLoad is { } gl ? $"{gl:0}%" : "N/A");

        _gpuTemp.Push(s.GpuTempC,
            s.GpuTempC is { } gt ? $"{gt:0}°C" : "N/A",
            s.GpuTempC is { } g2 && g2 >= GpuTempLimit ? 2 : 0);

        _ram.Push(s.RamPercent, $"{s.RamPercent:0}%  ({s.RamUsedGb:0.0} GB)", s.RamPercent >= 90 ? 1 : 0);

        _vram.Push(s.GpuMemPercent,
            s.GpuMemPercent is { } vp ? $"{vp:0}%  ({(s.GpuMemUsedMb ?? 0) / 1024.0:0.0} GB)" : "N/A");

        var disk = (s.DiskReadMBs ?? 0) + (s.DiskWriteMBs ?? 0);
        _disk.Push(s.DiskReadMBs.HasValue ? disk : null, s.DiskReadMBs.HasValue ? $"{disk:0.0} MB/s" : "N/A");

        _net.Push(s.NetDownMBs, $"{s.NetDownMBs:0.00} MB/s down, {s.NetUpMBs:0.00} up");

        UpdateCores(s);
        UpdateAlerts(s);

        if (!AppServices.Hub.SensorsAvailable)
            SensorNote = "CPU/GPU sensors could not be opened. Make sure Vanta is running as administrator.";
        else if (s.CpuTempC == null)
            SensorNote = "CPU temperature is unavailable. Recent LibreHardwareMonitor versions read CPU sensors through the " +
                         "PawnIO driver (pawnio.eu). Install it and restart Vanta. GPU readings do not need it.";
        else
            SensorNote = "";
    }

    private void UpdateCores(MetricSample s)
    {
        if (Cores.Count != s.CpuThreadLoads.Length)
        {
            Cores.Clear();
            foreach (var name in s.CpuThreadNames) Cores.Add(new CoreVm(name));
        }

        for (var i = 0; i < Cores.Count && i < s.CpuThreadLoads.Length; i++)
            Cores[i].Load = s.CpuThreadLoads[i];
    }

    private void UpdateAlerts(MetricSample s)
    {
        var alerts = new List<string>();

        if (s.CpuTempC is { } ct && ct >= CpuTempLimit)
            alerts.Add($"CPU is at {ct:0}°C (alert limit {CpuTempLimit}°C)" +
                       (ct >= 95 ? " - at this level it is likely thermal throttling" : ""));

        if (s.GpuTempC is { } gt && gt >= GpuTempLimit)
            alerts.Add($"GPU is at {gt:0}°C (alert limit {GpuTempLimit}°C)");

        if (s.RamPercent >= 92)
            alerts.Add($"Memory is {s.RamPercent:0}% full");

        AlertText = string.Join("   |   ", alerts);
    }
}
