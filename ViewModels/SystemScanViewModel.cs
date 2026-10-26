using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class UpdateRowVm : ObservableObject
{
    public UpdateRowVm(UpdateItem item) => Item = item;
    public UpdateItem Item { get; }
    public string Name => Item.Name;
    public string Versions => string.IsNullOrEmpty(Item.Available) ? (Item.IsDriver ? "Driver update" : "") : $"{Item.Current}  →  {Item.Available}";
    public bool CanInstall => Item.Source == "winget";

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
}

public partial class SystemScanViewModel : ObservableObject
{
    private SystemSummary? _summary;

    public ObservableCollection<Recommendation> Recommendations { get; } = new();
    public ObservableCollection<BiosCheck> BiosChecks { get; } = new();
    public ObservableCollection<DriverInfo> Drivers { get; } = new();
    public ObservableCollection<DiskHealth> Disks { get; } = new();
    public ObservableCollection<UpdateRowVm> WingetUpdates { get; } = new();
    public ObservableCollection<UpdateRowVm> WindowsUpdates { get; } = new();

    /// <summary>Set by MainViewModel.</summary>
    public Action<string, int>? Navigate { get; set; }
    public Func<Task>? Optimize { get; set; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string _status = "Click 'Scan my PC' to check everything. Nothing is changed by scanning.";
    [ObservableProperty] private string _scoreLine = "";
    [ObservableProperty] private string _boardLine = "";

    [ObservableProperty] private string _wingetStatus = "";
    [ObservableProperty] private string _windowsUpdateStatus = "";
    [ObservableProperty] private string _driverBackupStatus = "";
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private string _thermalText = "";

    public int HighCount => Recommendations.Count(r => r.Impact == Impact.High && r.Severity != Severity.Good);
    public int MediumCount => Recommendations.Count(r => r.Impact == Impact.Medium);
    public int LowCount => Recommendations.Count(r => r.Impact == Impact.Low && r.Severity != Severity.Good);

    /// <summary>Runs the first scan when the page is opened for the first time.</summary>
    public async Task EnsureScannedAsync()
    {
        if (!HasScanned && !IsScanning) await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var report = await AppServices.Scan.RunAsync(new Progress<string>(m => Status = m));
            _summary = report.Summary;

            Recommendations.Clear();
            foreach (var r in report.Recommendations) Recommendations.Add(r);
            BiosChecks.Clear();
            foreach (var b in report.Bios) BiosChecks.Add(b);
            Drivers.Clear();
            foreach (var d in report.Drivers) Drivers.Add(d);
            Disks.Clear();
            foreach (var d in report.Disks) Disks.Add(d);

            OnPropertyChanged(nameof(HighCount));
            OnPropertyChanged(nameof(MediumCount));
            OnPropertyChanged(nameof(LowCount));
            ScoreLine = HighCount + MediumCount == 0
                ? "Nothing important to fix."
                : $"{HighCount} high-impact and {MediumCount} medium-impact item(s) worth your time.";
            BoardLine = $"{report.Summary.Motherboard}  ·  BIOS {report.Summary.BiosVersion} {report.Summary.BiosDate}";
            Status = $"Scan finished at {report.FinishedAt:t}.";
            HasScanned = true;
            CheckThermals();
        }
        catch (Exception ex)
        {
            AppLog.Error("System scan failed", ex);
            Status = "The scan hit a problem: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task FixAsync(Recommendation? r)
    {
        if (r?.Action == null) return;
        var a = r.Action;
        if (a == "optimize" && Optimize != null) { await Optimize(); await ScanAsync(); }
        else if (a.StartsWith("tab:")) SelectedTab = a[4..] switch { "bios" => 1, "drivers" => 2, "health" => 3, _ => 0 };
        else if (a.StartsWith("nav:")) Navigate?.Invoke(a[4..], a.Contains("FPS") && r.ActionLabel.Contains("CPU") ? 2 : 0);
        else if (a.StartsWith("url:")) Links.Open(a[4..]);
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Links.Open(url);
    }

    // ---- BIOS --------------------------------------------------------------------------------
    [RelayCommand]
    private async Task RebootToBiosAsync()
    {
        if (MessageBox.Show(
                "Restart now and open your BIOS / UEFI settings?\n\nSave your work first: the PC restarts in 5 seconds. " +
                "Tip: export the BIOS checklist first so you can follow it on your phone.",
                "Reboot to BIOS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var r = await FirmwareService.RebootToFirmwareAsync();
        if (!r.Ok)
            MessageBox.Show("Windows could not restart into the firmware settings (this needs UEFI boot and admin rights).\n\n" +
                            "Instead: restart and press Del or F2 repeatedly while the PC starts.\n\n" + r.StdErr,
                "Reboot to BIOS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ExportChecklist()
    {
        if (_summary == null) return;
        var dlg = new SaveFileDialog { Filter = "Web page (*.html)|*.html", FileName = "BIOS checklist.html" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, FirmwareService.ToHtml(_summary, BiosChecks));
        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenSupportPage()
    {
        if (_summary != null) Links.Open(FirmwareService.SupportSearchUrl(_summary));
    }

    // ---- Drivers & updates ---------------------------------------------------------------------
    [RelayCommand]
    private async Task CheckWingetAsync()
    {
        IsCheckingUpdates = true;
        WingetStatus = "Asking winget which apps have updates (can take a minute)...";
        WingetUpdates.Clear();
        try
        {
            var (items, error) = await DriverService.WingetUpgradesAsync(CancellationToken.None);
            foreach (var i in items) WingetUpdates.Add(new UpdateRowVm(i));
            WingetStatus = error ?? (items.Count == 0 ? "All your winget-managed apps are up to date." : $"{items.Count} app update(s) available.");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private async Task UpgradeAsync(UpdateRowVm? row)
    {
        if (row is not { CanInstall: true } || row.IsBusy) return;
        if (MessageBox.Show($"Update {row.Name} to {row.Item.Available} with winget?\n\nThe app's own installer runs from winget's official source. Close the app first if it is open.",
                "Update app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        row.IsBusy = true;
        row.Status = "Updating...";
        var r = await DriverService.WingetUpgradeAsync(row.Item.Id, CancellationToken.None);
        row.Status = r.Ok ? "Updated" : "Failed (see log)";
        if (!r.Ok) AppLog.Warn($"winget upgrade {row.Item.Id} failed: {r.StdOut} {r.StdErr}");
        row.IsBusy = false;
    }

    [RelayCommand]
    private async Task CheckWindowsUpdateAsync()
    {
        IsCheckingUpdates = true;
        WindowsUpdateStatus = "Checking Windows Update (this can take a few minutes)...";
        WindowsUpdates.Clear();
        try
        {
            var (items, error) = await DriverService.PendingWindowsUpdatesAsync();
            foreach (var i in items.OrderByDescending(i => i.IsDriver)) WindowsUpdates.Add(new UpdateRowVm(i));
            WindowsUpdateStatus = error ?? (items.Count == 0 ? "Windows is up to date." : $"{items.Count} pending update(s). Install them from Windows Update.");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenWindowsUpdate() => Links.Open("ms-settings:windowsupdate");

    [RelayCommand]
    private async Task BackupDriversAsync()
    {
        DriverBackupStatus = "Exporting driver packages with pnputil (a few minutes)...";
        var (ok, folder, _) = await AppServices.Scan.Drivers.BackupAsync(CancellationToken.None);
        DriverBackupStatus = ok ? $"Saved to {folder}. To reinstall later: Device Manager > Update driver > Browse my computer > pick this folder."
                                : "Driver backup failed (it needs admin rights). See the log for details.";
        if (ok) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // ---- Health ------------------------------------------------------------------------------------
    [RelayCommand]
    private void MemoryDiagnostic() => DiskHealthService.OpenMemoryDiagnostic();

    [RelayCommand]
    private void CheckThermals()
    {
        var history = AppServices.Hub.History();
        if (history.Count < 5) { ThermalText = "Not enough sensor data yet. Leave Vanta open for a minute (ideally while gaming) and check again."; return; }

        var report = BottleneckService.Analyze(history);
        var cpuMax = history.Where(h => h.CpuTempC.HasValue).Select(h => h.CpuTempC!.Value).DefaultIfEmpty(0).Max();
        var gpuMax = history.Where(h => h.GpuTempC.HasValue).Select(h => h.GpuTempC!.Value).DefaultIfEmpty(0).Max();
        var minutes = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
        var throttle = report.Verdict.Contains("Thermal") || report.Verdict.Contains("power limit");

        ThermalText = $"Last {Math.Max(1, minutes):0} min: CPU peak {(cpuMax > 0 ? $"{cpuMax:0}°C" : "n/a")}, GPU peak {(gpuMax > 0 ? $"{gpuMax:0}°C" : "n/a")}. " +
                      (throttle ? report.Verdict + ". " + string.Join(" ", report.Fixes.Take(2))
                                : "No sign of thermal or power throttling. For a real test, play a demanding game for 10 minutes and check again.");
    }
}
