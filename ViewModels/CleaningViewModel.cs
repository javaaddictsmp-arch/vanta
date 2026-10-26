using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class CleanItemVm : ObservableObject
{
    public CleanItemVm(CleanCategory c)
    {
        Category = c;
        _isSelected = c.DefaultOn;
    }

    public CleanCategory Category { get; }
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string Warning => Category.Warning;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _sizeText = "...";
    [ObservableProperty] private string _countText = "";

    public void Refresh()
    {
        SizeText = CleanerService.FormatSize(Category.SizeBytes);
        CountText = Category.FileCount == 0 ? "nothing to clean" : $"{Category.FileCount:N0} item(s)";
    }
}

public sealed record SizeRow(string Path, string Name, long Size, double Percent)
{
    public string SizeText => CleanerService.FormatSize(Size);
}

public sealed record DuplicateRow(string Header, List<string> Files);

public partial class CleaningViewModel : ObservableObject
{
    private readonly DispatcherTimer _memTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _cts;
    private bool _measured;

    public CleaningViewModel()
    {
        foreach (var c in CleanerService.Categories()) Items.Add(new CleanItemVm(c));
        _rootPath = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

        var o = AppServices.Ram.Options;
        _autoEnabled = o.Auto;
        _modeGameStart = o.Mode == "GameStart";
        _selectedInterval = IntervalOptions.FirstOrDefault(i => i.Value == o.IntervalMinutes) ?? IntervalOptions[2];
        _threshold = ThresholdOptions.FirstOrDefault(t => t.Value == o.ThresholdPercent) ?? ThresholdOptions[2];
        _trimBackground = o.TrimBackground;
        _includeOpenApps = o.IncludeOpenApps;
        _flushModified = o.FlushModified;
        _purgeStandby = o.PurgeStandby;
        _combinePages = o.CombinePages;
        foreach (var a in o.AllowList) AllowList.Add(a);

        _memTimer.Tick += (_, _) => RefreshMemory();
        AppServices.Ram.Cleaned += r => UiThread.Post(() =>
        {
            LastResult = r.Summary;
            LastSteps = string.Join("  ·  ", r.Steps);
            RefreshMemory();
        });
    }

    public ObservableCollection<CleanItemVm> Items { get; } = new();
    public ObservableCollection<SizeRow> SizeRows { get; } = new();
    public ObservableCollection<DuplicateRow> Duplicates { get; } = new();
    public ObservableCollection<GameInstall> Games { get; } = new();
    public ObservableCollection<ProcessMemory> TopProcesses { get; } = new();
    public ObservableCollection<LeakSuspect> Leaks { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _selectedTotal = "";

    // Find space
    [ObservableProperty] private string _rootPath;
    [ObservableProperty] private int _minSizeMb = 500;
    [ObservableProperty] private string _findStatus = "";

    // Games & drives
    [ObservableProperty] private string _gamesStatus = "";
    [ObservableProperty] private string _trimStatus = "";
    [ObservableProperty] private string _optimizeLog = "";

    // Memory: RAM cleaner
    public ObservableCollection<string> AllowList { get; } = new();
    public ObservableCollection<string> RunningProcessNames { get; } = new();

    public static IReadOnlyList<Option<int>> IntervalOptions { get; } = new[]
    {
        new Option<int>(15, "At most every 15 min"), new Option<int>(30, "At most every 30 min"),
        new Option<int>(60, "Once per hour"), new Option<int>(120, "Every 2 hours")
    };

    public static IReadOnlyList<Option<int>> ThresholdOptions { get; } = new[]
    {
        new Option<int>(70, "70% in use"), new Option<int>(75, "75% in use"), new Option<int>(80, "80% in use (recommended)"),
        new Option<int>(85, "85% in use"), new Option<int>(90, "90% in use")
    };

    [ObservableProperty] private bool _needsCleaning;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusReason = "";
    [ObservableProperty] private string _lastCleanupText = "";
    // Amounts for the animated bar (GB)
    [ObservableProperty] private double _barInUse;
    [ObservableProperty] private double _barCached;
    [ObservableProperty] private double _barFree = 1;
    [ObservableProperty] private string _inUseText = "";
    [ObservableProperty] private string _cachedText = "";
    [ObservableProperty] private string _freeText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private bool _hasCacheDetail;
    [ObservableProperty] private string _commitLine = "";
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private string _lastSteps = "";
    [ObservableProperty] private string _leakStatus = "";
    [ObservableProperty] private string _newAllowApp = "";
    [ObservableProperty] private string _adminWarning = "";

    [ObservableProperty] private bool _autoEnabled;
    [ObservableProperty] private bool _modeGameStart;
    [ObservableProperty] private Option<int> _selectedInterval;
    [ObservableProperty] private Option<int> _threshold;
    [ObservableProperty] private bool _trimBackground;
    [ObservableProperty] private bool _includeOpenApps;
    [ObservableProperty] private bool _flushModified;
    [ObservableProperty] private bool _purgeStandby;
    [ObservableProperty] private bool _combinePages;

    public bool ModeNeedsCleaning
    {
        get => !ModeGameStart;
        set { if (value) ModeGameStart = false; }
    }

    private RamCleanerSettings Ram => AppServices.Ram.Options;
    private static void SaveSettings() => AppServices.Settings.Save();

    partial void OnAutoEnabledChanged(bool value) { Ram.Auto = value; SaveSettings(); }
    partial void OnModeGameStartChanged(bool value) { Ram.Mode = value ? "GameStart" : "NeedsCleaning"; SaveSettings(); OnPropertyChanged(nameof(ModeNeedsCleaning)); }
    partial void OnSelectedIntervalChanged(Option<int> value) { Ram.IntervalMinutes = value.Value; SaveSettings(); }
    partial void OnThresholdChanged(Option<int> value) { Ram.ThresholdPercent = value.Value; SaveSettings(); RefreshMemory(); }
    partial void OnTrimBackgroundChanged(bool value) { Ram.TrimBackground = value; SaveSettings(); }
    partial void OnIncludeOpenAppsChanged(bool value) { Ram.IncludeOpenApps = value; SaveSettings(); }
    partial void OnFlushModifiedChanged(bool value) { Ram.FlushModified = value; SaveSettings(); }
    partial void OnPurgeStandbyChanged(bool value) { Ram.PurgeStandby = value; SaveSettings(); }
    partial void OnCombinePagesChanged(bool value) { Ram.CombinePages = value; SaveSettings(); }

    /// <summary>Called when the page is shown: measure once, start the live memory readout.</summary>
    public async Task ActivateAsync()
    {
        _memTimer.Start();
        RefreshMemory();
        RefreshTopProcesses();
        if (!_measured) await MeasureAsync();
    }

    public void Deactivate() => _memTimer.Stop();

    // ---- Clean up ------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Measuring how much each item takes up...";
        try
        {
            foreach (var i in Items) i.SizeText = "...";
            await AppServices.Cleaner.MeasureAsync(Items.Select(i => i.Category), CancellationToken.None);
            foreach (var i in Items) i.Refresh();
            _measured = true;
            UpdateTotal();
            Status = "Pick what to clean. Nothing is deleted until you click Clean.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void UpdateTotal()
    {
        var total = Items.Where(i => i.IsSelected && i.Category.Kind == CleanKind.Files || i.IsSelected && i.Category.Kind == CleanKind.RecycleBin)
                         .Sum(i => i.Category.SizeBytes);
        SelectedTotal = $"{CleanerService.FormatSize(total)} selected";
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0 || IsBusy) return;

        var warnings = selected.Where(i => !string.IsNullOrEmpty(i.Warning)).Select(i => $"• {i.Name}: {i.Warning}").ToList();
        var text = $"Clean {selected.Count} item(s), about {SelectedTotal.Replace(" selected", "")}?\n\n" +
                   (warnings.Count > 0 ? string.Join("\n", warnings) + "\n\n" : "") +
                   "Files that are in use are skipped. Honest note: this frees disk space; it does not make games faster unless your drive was nearly full.";
        if (MessageBox.Show(text, "Clean up", MessageBoxButton.YesNo,
                warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var (freed, deleted, skipped) = await AppServices.Cleaner.CleanAsync(selected.Select(i => i.Category),
                new Progress<string>(m => Status = m), CancellationToken.None);
            Status = $"Freed {CleanerService.FormatSize(freed)} ({deleted:N0} files)." + (skipped > 0 ? $" {skipped:N0} file(s) were in use and left alone." : "");
        }
        finally
        {
            IsBusy = false;
        }
        _measured = false;
        await MeasureAsync();
    }

    // ---- Find space -----------------------------------------------------------------------------------
    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Pick a drive or folder to search", InitialDirectory = RootPath };
        if (dlg.ShowDialog() == true) RootPath = dlg.FolderName;
    }

    [RelayCommand]
    private async Task FindBigAsync()
    {
        if (!Directory.Exists(RootPath)) { FindStatus = "That folder does not exist."; return; }
        _cts = new CancellationTokenSource();
        IsBusy = true;
        SizeRows.Clear();
        Duplicates.Clear();
        FindStatus = "Searching (big drives can take a minute)...";
        try
        {
            var files = await CleanerService.FindBigFilesAsync(RootPath, MinSizeMb * 1048576L, 60,
                new Progress<int>(n => FindStatus = $"Searching... {n:N0} files checked"), _cts.Token);
            var max = files.Count > 0 ? files[0].Size : 1;
            foreach (var f in files) SizeRows.Add(new SizeRow(f.Path, Path.GetFileName(f.Path), f.Size, f.Size * 100.0 / max));
            FindStatus = files.Count == 0 ? $"No files bigger than {MinSizeMb} MB found." : $"The {files.Count} biggest files over {MinSizeMb} MB.";
        }
        catch (OperationCanceledException) { FindStatus = "Stopped."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task BreakdownAsync()
    {
        if (!Directory.Exists(RootPath)) { FindStatus = "That folder does not exist."; return; }
        _cts = new CancellationTokenSource();
        IsBusy = true;
        SizeRows.Clear();
        Duplicates.Clear();
        FindStatus = "Adding up folder sizes...";
        try
        {
            var rows = await CleanerService.BreakdownAsync(RootPath, _cts.Token);
            var max = rows.Count > 0 ? Math.Max(1, rows[0].Size) : 1;
            foreach (var r in rows.Take(40)) SizeRows.Add(new SizeRow(r.Path, Path.GetFileName(r.Path), r.Size, r.Size * 100.0 / max));
            FindStatus = $"What takes up space in {RootPath}. Click a folder's 'Open' to look inside.";
        }
        catch (OperationCanceledException) { FindStatus = "Stopped."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        if (!Directory.Exists(RootPath)) { FindStatus = "That folder does not exist."; return; }
        _cts = new CancellationTokenSource();
        IsBusy = true;
        SizeRows.Clear();
        Duplicates.Clear();
        try
        {
            var groups = await CleanerService.FindDuplicatesAsync(RootPath, 1048576, new Progress<string>(m => FindStatus = m), _cts.Token);
            foreach (var g in groups.Take(100))
                Duplicates.Add(new DuplicateRow($"{g.Files.Count} identical copies of {CleanerService.FormatSize(g.Size)} " +
                                                $"({CleanerService.FormatSize(g.Size * (g.Files.Count - 1))} wasted)", g.Files));
            var wasted = groups.Sum(g => g.Size * (g.Files.Count - 1));
            FindStatus = groups.Count == 0 ? "No duplicate files (1 MB or larger) found." :
                $"{groups.Count} sets of identical files, {CleanerService.FormatSize(wasted)} could be saved. Compared by content (SHA-256), not just name.";
        }
        catch (OperationCanceledException) { FindStatus = "Stopped."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLocation(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var target = path.EndsWith("(files in this folder)") ? Path.GetDirectoryName(path)! : path;
        var args = File.Exists(target) ? $"/select,\"{target}\"" : $"\"{target}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Recycle(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (MessageBox.Show($"Move this file to the Recycle Bin?\n\n{path}\n\nYou can restore it from the Recycle Bin.",
                "Move to Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            CleanerService.SendToRecycleBin(path);
            var row = SizeRows.FirstOrDefault(r => r.Path == path);
            if (row != null) SizeRows.Remove(row);
            foreach (var d in Duplicates.Where(d => d.Files.Contains(path)).ToList())
            {
                Duplicates.Remove(d);
                var rest = d.Files.Where(f => f != path).ToList();
                if (rest.Count > 1) Duplicates.Add(d with { Files = rest });
            }
            FindStatus = $"Moved {Path.GetFileName(path)} to the Recycle Bin.";
        }
        catch (Exception ex)
        {
            FindStatus = "Could not move it: " + ex.Message;
        }
    }

    // ---- Games & drives -----------------------------------------------------------------------------------
    [RelayCommand]
    private async Task ScanGamesAsync()
    {
        IsBusy = true;
        GamesStatus = "Measuring game folders...";
        Games.Clear();
        try
        {
            var list = await CleanerService.GameSizesAsync(AppServices.Games.Games, CancellationToken.None);
            foreach (var g in list) Games.Add(g);
            var onHdd = list.Where(g => g.DriveKind == "HDD").ToList();
            GamesStatus = $"{list.Count} games, {CleanerService.FormatSize(list.Sum(g => g.Size))} in total." +
                          (onHdd.Count > 0 ? $" {onHdd.Count} are on a hard drive (HDD): moving the ones you play to an SSD speeds up loading and reduces stutter." : "");
            TrimStatus = await CleanerService.TrimStatusAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OptimizeDrivesAsync()
    {
        IsBusy = true;
        OptimizeLog = "";
        var drive = (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\")[..2];
        try
        {
            // /O = the right action per drive type: TRIM ("retrim") for SSDs, defragment for HDDs.
            await ProcessRunner.RunStreamingAsync("defrag.exe", $"{drive} /O /U", line => UiThread.Post(() => OptimizeLog += line.Trim() + "\n"), 60 * 60_000);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenStorageSense() => Links.Open("ms-settings:storagesense");

    // ---- Memory ------------------------------------------------------------------------------------------
    private void RefreshMemory()
    {
        var st = AppServices.Ram.State();
        NeedsCleaning = st.NeedsCleaning;
        StatusText = st.NeedsCleaning ? "Needs cleaning" : "Optimized";
        StatusReason = st.Reason;

        // Bar: in use | cache (standby) | free. Without admin the cache/free split isn't readable.
        HasCacheDetail = st.CachedGb.HasValue;
        var cached = st.CachedGb ?? 0;
        var free = st.FreeGb ?? st.AvailableGb;
        BarInUse = st.InUseGb;
        BarCached = cached;
        BarFree = Math.Max(0, st.TotalGb - st.InUseGb - cached);
        InUseText = $"{st.InUseGb:0.0} GB";
        CachedText = $"{cached:0.0} GB";
        FreeText = HasCacheDetail ? $"{free:0.0} GB" : $"{st.AvailableGb:0.0} GB";
        TotalText = $"{st.TotalGb:0} GB total";

        AdminWarning = !MainViewModel.IsAdmin && Ram.PurgeStandby && cached >= 1
            ? $"{cached:0.0} GB is sitting in cache, and clearing it is where most of the freeable memory is, but Windows only allows it for admin apps. Close Vanta and start it with 'Run as administrator'."
            : "";

        var last = Ram.LastCleanupUtc;
        LastCleanupText = last is { } l ? $"Last cleanup {l.ToLocalTime():g}" : "Not cleaned yet";

        var s = RamService.Snapshot();
        CommitLine = $"Committed memory: {s.CommitUsedGb:0.0} of {s.CommitLimitGb:0.0} GB (RAM + page file, {s.CommitPercent:0}%)" +
                     (s.CommitPercent > 90 ? ". Very high: apps may crash with 'out of memory'. Close programs or let Windows manage a bigger page file." : "");
    }

    [RelayCommand]
    private async Task CleanRamAsync()
    {
        if (IsCleaning) return;
        IsCleaning = true;
        LastResult = "Cleaning...";
        try
        {
            var r = await AppServices.Ram.CleanAsync("manual");
            if (r == null) LastResult = "A cleanup is already running.";
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private void AddAllowApp(string? name)
    {
        var app = Path.GetFileNameWithoutExtension((name ?? NewAllowApp).Trim());
        if (string.IsNullOrWhiteSpace(app) || AllowList.Contains(app, StringComparer.OrdinalIgnoreCase)) return;
        AllowList.Add(app);
        Ram.AllowList = AllowList.ToList();
        SaveSettings();
        NewAllowApp = "";
    }

    [RelayCommand]
    private void RemoveAllowApp(string? name)
    {
        if (name == null) return;
        AllowList.Remove(name);
        Ram.AllowList = AllowList.ToList();
        SaveSettings();
    }

    [RelayCommand]
    private void RefreshRunningNames()
    {
        RunningProcessNames.Clear();
        foreach (var n in Process.GetProcesses().Select(p => { using (p) return ProcessUtil.SafeName(p); })
                     .Where(n => !ProcessUtil.IsProtected(n) && !AllowList.Contains(n, StringComparer.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            RunningProcessNames.Add(n);
    }

    [RelayCommand]
    private void RefreshTopProcesses()
    {
        TopProcesses.Clear();
        foreach (var p in RamService.TopProcesses(12)) TopProcesses.Add(p);

        Leaks.Clear();
        foreach (var l in AppServices.Ram.LeakSuspects()) Leaks.Add(l);
        var watched = AppServices.Ram.WatchedFor;
        LeakStatus = watched < TimeSpan.FromMinutes(10)
            ? $"Watching memory use in the background ({watched.TotalMinutes:0} of 10 minutes needed before leaks can be judged)."
            : Leaks.Count == 0 ? $"No leaks spotted in the last {watched.TotalMinutes:0} minutes." : "These keep growing without coming back down:";
    }

}
