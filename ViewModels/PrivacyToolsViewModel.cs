using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class CompatFlagVm : ObservableObject
{
    public CompatFlagVm(string flag, string label) { Flag = flag; Label = label; }
    public string Flag { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isChecked;
}

public partial class PrivacyToolsViewModel : ObservableObject
{
    private CancellationTokenSource? _toolCts;
    private List<InstalledProgram> _allPrograms = new();

    public PrivacyToolsViewModel()
    {
        Privacy = new TweakListVm(AppServices.Catalog.Tweaks.Where(t => t.Category == "Privacy").Select(t => t.Id));
        foreach (var (flag, label) in PrivacyToolsService.CompatFlags) CompatFlags.Add(new CompatFlagVm(flag, label));
        HasBattery = PrivacyToolsService.HasBattery();
    }

    public TweakListVm Privacy { get; }
    public ObservableCollection<StoreApp> StoreApps { get; } = new();
    public ObservableCollection<InstalledProgram> Programs { get; } = new();
    public ObservableCollection<Leftover> Leftovers { get; } = new();
    public ObservableCollection<CompatFlagVm> CompatFlags { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _appsStatus = "";
    [ObservableProperty] private string _programsStatus = "";
    [ObservableProperty] private string _programFilter = "";
    [ObservableProperty] private InstalledProgram? _selectedProgram;
    [ObservableProperty] private string _toolLog = "";
    [ObservableProperty] private bool _toolRunning;
    [ObservableProperty] private string _toolsStatus = "";
    [ObservableProperty] private bool _maintenanceOn;
    [ObservableProperty] private string _compatExe = "";
    [ObservableProperty] private string _compatStatus = "";
    public bool HasBattery { get; }

    partial void OnProgramFilterChanged(string value) => FilterPrograms();

    public async Task ActivateAsync()
    {
        MaintenanceOn = await PrivacyToolsService.MaintenanceScheduledAsync();
        if (_allPrograms.Count == 0) LoadPrograms();
    }

    // ---- Store apps -------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task LoadAppsAsync()
    {
        IsBusy = true;
        AppsStatus = "Looking for removable apps...";
        StoreApps.Clear();
        try
        {
            foreach (var a in await PrivacyToolsService.StoreAppsAsync()) StoreApps.Add(a);
            AppsStatus = StoreApps.Count == 0 ? "None of the commonly removed apps are installed. Nice and clean." : $"{StoreApps.Count} removable app(s) found. Apps with a note are ones some people still use.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveAppAsync(StoreApp? app)
    {
        if (app == null) return;
        var text = $"Remove {app.Friendly}?" + (string.IsNullOrEmpty(app.Note) ? "" : $"\n\n{app.Note}") +
                   "\n\nYou can reinstall it any time from the Microsoft Store.";
        if (MessageBox.Show(text, "Remove app", MessageBoxButton.YesNo, app.Caution ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            if (await PrivacyToolsService.RemoveStoreAppAsync(app)) { StoreApps.Remove(app); AppsStatus = $"Removed {app.Friendly}."; }
            else AppsStatus = $"Could not remove {app.Friendly}.";
        }
        finally { IsBusy = false; }
    }

    // ---- Programs ----------------------------------------------------------------------------------------
    [RelayCommand]
    private void LoadPrograms()
    {
        _allPrograms = PrivacyToolsService.Programs();
        FilterPrograms();
        ProgramsStatus = $"{_allPrograms.Count} programs installed ({CleanerService.FormatSize(_allPrograms.Sum(p => p.SizeKb) * 1024)} reported).";
    }

    private void FilterPrograms()
    {
        Programs.Clear();
        foreach (var p in _allPrograms.Where(p => string.IsNullOrWhiteSpace(ProgramFilter) ||
                                                  p.Name.Contains(ProgramFilter, StringComparison.OrdinalIgnoreCase) ||
                                                  p.Publisher.Contains(ProgramFilter, StringComparison.OrdinalIgnoreCase)))
            Programs.Add(p);
    }

    [RelayCommand]
    private void Uninstall(InstalledProgram? p)
    {
        if (p == null) return;
        if (MessageBox.Show($"Start the uninstaller for {p.Name}?\n\nIt opens the program's own uninstaller. Afterwards, use 'Find leftovers' to check for folders it left behind.",
                "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { PrivacyToolsService.StartUninstall(p); SelectedProgram = p; ProgramsStatus = $"Uninstaller for {p.Name} started."; }
        catch (Exception ex) { ProgramsStatus = "Could not start the uninstaller: " + ex.Message; }
    }

    [RelayCommand]
    private async Task FindLeftoversAsync(InstalledProgram? p)
    {
        p ??= SelectedProgram;
        if (p == null) return;
        SelectedProgram = p;
        Leftovers.Clear();
        foreach (var l in await PrivacyToolsService.LeftoversAsync(p)) Leftovers.Add(l);
        ProgramsStatus = Leftovers.Count == 0
            ? $"No leftover folders found for {p.Name}."
            : $"Folders that may belong to {p.Name}. Check them before removing: a folder named after the publisher can be shared by its other programs.";
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void RecycleLeftover(Leftover? l)
    {
        if (l == null) return;
        if (MessageBox.Show($"Move this folder to the Recycle Bin?\n\n{l.Path}\n\nOnly do this if the program is uninstalled.", "Remove leftover",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { PrivacyToolsService.RecycleFolder(l.Path); Leftovers.Remove(l); }
        catch (Exception ex) { ProgramsStatus = "Could not remove it: " + ex.Message; }
    }

    // ---- Tools -------------------------------------------------------------------------------------------
    private async Task RunToolAsync(string title, string exe, string args, int minutes)
    {
        if (ToolRunning) return;
        ToolRunning = true;
        _toolCts = new CancellationTokenSource();
        ToolLog = $"{title}\n";
        try
        {
            var code = await ProcessRunner.RunStreamingAsync(exe, args, line => UiThread.Post(() =>
            {
                // SFC/DISM print progress as repeated percentage lines: keep the log readable.
                var trimmed = line.Trim();
                if (trimmed.Length == 0) return;
                var lines = ToolLog.TrimEnd('\n').Split('\n').ToList();
                if (lines.Count > 0 && (trimmed.Contains('%') && lines[^1].Contains('%'))) lines[^1] = trimmed; else lines.Add(trimmed);
                ToolLog = string.Join("\n", lines.TakeLast(200)) + "\n";
            }), minutes * 60_000, _toolCts.Token);
            ToolLog += code == 0 ? "\nFinished." : $"\nFinished with code {code} (admin rights are required for these tools).";
        }
        finally { ToolRunning = false; }
    }

    [RelayCommand]
    private Task RunSfcAsync() => RunToolAsync("System File Checker: checks and repairs Windows' own files (10-20 min)...", "sfc.exe", "/scannow", 60);

    [RelayCommand]
    private Task RunDismAsync() => RunToolAsync("DISM: repairs the Windows component store that SFC uses (can take 20+ min, uses Windows Update)...", "dism.exe", "/Online /Cleanup-Image /RestoreHealth", 120);

    [RelayCommand]
    private Task RunChkdskAsync() => RunToolAsync("CHKDSK online scan of the system drive (no restart needed)...", "chkdsk.exe",
        $"{(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\")[..2]} /scan", 60);

    [RelayCommand]
    private void StopTool() => _toolCts?.Cancel();

    [RelayCommand]
    private async Task BatteryReportAsync()
    {
        var file = await PrivacyToolsService.BatteryReportAsync();
        if (file != null) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        else ToolsStatus = "Could not create the battery report.";
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var dlg = new SaveFileDialog { Filter = "Web page (*.html)|*.html", FileName = "Vanta system report.html" };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        ToolsStatus = "Collecting system information...";
        try
        {
            var report = AppServices.Scan.Last ?? await AppServices.Scan.RunAsync();
            await File.WriteAllTextAsync(dlg.FileName, PrivacyToolsService.SystemReportHtml(report));
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            ToolsStatus = "Report saved. It contains hardware and settings info but no serial numbers or personal files.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleMaintenanceAsync()
    {
        var r = MaintenanceOn ? await PrivacyToolsService.RemoveMaintenanceAsync() : await PrivacyToolsService.ScheduleMaintenanceAsync();
        MaintenanceOn = await PrivacyToolsService.MaintenanceScheduledAsync();
        ToolsStatus = r.Ok
            ? MaintenanceOn ? "Weekly clean-up scheduled for Sundays at 12:00 (temp files, update cache, crash dumps). It runs silently and is logged."
                            : "Weekly clean-up removed."
            : "Could not change the scheduled task (needs admin rights).";
    }

    [RelayCommand]
    private void BrowseCompatExe()
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Pick the game or program" };
        if (dlg.ShowDialog() != true) return;
        CompatExe = dlg.FileName;
        // Show what is already set for this EXE.
        var current = RegistryService.Read("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", CompatExe).Value ?? "";
        foreach (var f in CompatFlags) f.IsChecked = current.Split(' ').Contains(f.Flag);
    }

    [RelayCommand]
    private async Task ApplyCompatAsync()
    {
        if (!File.Exists(CompatExe)) { CompatStatus = "Pick a program first."; return; }
        var flags = CompatFlags.Where(f => f.IsChecked).Select(f => f.Flag).ToList();
        var def = PrivacyToolsService.CompatTweak(CompatExe, flags);

        // Replace any earlier Vanta compatibility change for this EXE so Undo stays exact.
        if (AppServices.ChangeLog.LatestActiveApply(def.Id) != null) await AppServices.Engine.RevertAsync(def);
        if (flags.Count == 0) { CompatStatus = "Compatibility settings cleared."; return; }

        var r = await AppServices.Engine.ApplyAsync(def);
        CompatStatus = r.Success ? $"Saved. Start {Path.GetFileName(CompatExe)} again for it to take effect. Undo from the Change Log." : r.Message;
    }
}
