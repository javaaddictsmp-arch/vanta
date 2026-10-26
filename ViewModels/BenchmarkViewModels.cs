using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

// =============================================================================================
// Game configs
// =============================================================================================
public partial class GameConfigRowVm : ObservableObject
{
    public GameConfigRowVm(GamePresetDefinition def)
    {
        Def = def;
        Refresh();
    }

    public GamePresetDefinition Def { get; }
    public string Name => Def.Name;
    public bool IsGuide => Def.Format == "guide";
    public IReadOnlyList<string> PresetNames => Def.Presets.Keys.ToList();
    public IReadOnlyList<string> Tips => Def.Tips;
    public string Notes => Def.Notes;

    [ObservableProperty] private string? _configPath;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _backupCount;

    public void Refresh()
    {
        ConfigPath = IsGuide ? null : GameConfigService.ResolvePaths(Def).FirstOrDefault();
        Status = IsGuide ? "Guidance only" : ConfigPath != null ? "Config found" : "Not found (launch the game once)";
        BackupCount = IsGuide ? 0 : AppServices.GameConfigs.Backups(Def).Count;
    }
}

public partial class GameConfigTabViewModel : ObservableObject
{
    public GameConfigTabViewModel() => Reload();

    public ObservableCollection<GameConfigRowVm> Games { get; } = new();

    [ObservableProperty] private GameConfigRowVm? _selected;
    [ObservableProperty] private string _message = "";

    private void Reload()
    {
        var id = Selected?.Def.Id;
        Games.Clear();
        foreach (var g in AppServices.GameConfigs.Games
                     .Select(d => new GameConfigRowVm(d))
                     .OrderBy(r => r.IsGuide)
                     .ThenByDescending(r => r.ConfigPath != null)
                     .ThenBy(r => r.Name))
            Games.Add(g);
        Selected = Games.FirstOrDefault(g => g.Def.Id == id) ?? Games.FirstOrDefault();
    }

    [RelayCommand]
    private void ApplyPreset(string? preset)
    {
        if (Selected?.ConfigPath == null || preset == null) return;

        var note = Selected.Def.PresetNotes.TryGetValue(preset, out var n) ? $"\n\n{n}" : "";
        if (MessageBox.Show(
                $"Apply '{preset}' to {Selected.Name}?{note}\n\nClose the game first, otherwise it may overwrite the file when it exits. " +
                "A backup is made first and you can restore it from here.",
                "Apply game preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var r = AppServices.GameConfigs.Apply(Selected.Def, preset, Selected.ConfigPath);
        Message = r.Message + (r.Missing.Count > 0 ? $"\nNot in your file: {string.Join(", ", r.Missing.Take(8))}{(r.Missing.Count > 8 ? "..." : "")}" : "");
        Selected.Refresh();
    }

    [RelayCommand]
    private void RestoreBackup()
    {
        if (Selected == null) return;
        try
        {
            var target = AppServices.GameConfigs.RestoreLatest(Selected.Def);
            Message = $"Restored your previous {Path.GetFileName(target)}.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        Selected.Refresh();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected?.ConfigPath == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Selected.ConfigPath}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void ImportPresets()
    {
        var dlg = new OpenFileDialog { Filter = "Game preset file (*.json)|*.json", Title = "Import community presets" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = AppServices.GameConfigs.InstallCommunityFile(dlg.FileName);
            Message = $"Imported {n} game preset(s). Review the values before applying: community files are not checked by Vanta.";
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

// =============================================================================================
// Benchmark + bottleneck finder
// =============================================================================================
public sealed class ResultRowVm
{
    public ResultRowVm(BenchmarkResult r) => Result = r;
    public BenchmarkResult Result { get; }
    public string Title => $"{Result.Label} - {Result.Game}";
    public string When => Result.TimestampUtc.ToLocalTime().ToString("g");
    public string Summary =>
        $"{Result.AvgFps:0} avg   {Result.Low1Fps:0} 1% low   {Result.Low01Fps:0} 0.1% low   {Result.StutterCount} stutters   {Result.DurationSec:0} s ({Result.Source})";
}

public partial class BenchmarkTabViewModel : ObservableObject
{
    private readonly BoostCenterViewModel _parent;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<MetricSample> _samples = new();
    private CancellationTokenSource? _cts;

    public BenchmarkTabViewModel(BoostCenterViewModel parent)
    {
        _parent = parent;
        _poll.Tick += (_, _) => RefreshTargets();
        foreach (var r in AppServices.Benchmarks.LoadHistory()) History.Add(new ResultRowVm(r));
        CompareA = History.Skip(1).FirstOrDefault();
        CompareB = History.FirstOrDefault();
    }

    public ObservableCollection<PresentingProcess> Targets { get; } = new();
    public ObservableCollection<ResultRowVm> History { get; } = new();
    public ObservableCollection<string> BottleneckEvidence { get; } = new();
    public ObservableCollection<string> BottleneckFixes { get; } = new();

    public static IReadOnlyList<Option<int>> DurationOptions { get; } = new[]
    {
        new Option<int>(30, "30 s"), new Option<int>(60, "60 s (best)"), new Option<int>(120, "2 min")
    };

    [ObservableProperty] private PresentingProcess? _target;
    [ObservableProperty] private int _duration = 60;
    [ObservableProperty] private string _label = "A (before)";
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _status = "Start your game, then click 'Find games' to pick it. Nothing is injected into the game.";
    [ObservableProperty] private double _progress;

    [ObservableProperty] private ResultRowVm? _selectedResult;
    [ObservableProperty] private IReadOnlyList<double> _graph = Array.Empty<double>();
    [ObservableProperty] private string _graphCaption = "";

    [ObservableProperty] private ResultRowVm? _compareA;
    [ObservableProperty] private ResultRowVm? _compareB;
    [ObservableProperty] private string _comparison = "";

    [ObservableProperty] private string _bottleneckVerdict = "";
    [ObservableProperty] private bool _isAnalyzing;

    public bool OverlayVisible => _parent.OverlayVisible;

    partial void OnSelectedResultChanged(ResultRowVm? value)
    {
        if (value == null) { Graph = Array.Empty<double>(); GraphCaption = ""; return; }
        Graph = Downsample(value.Result.Frametimes, 600);
        GraphCaption = $"Frametimes for {value.Title}: median {value.Result.MedianFrametimeMs:0.0} ms, worst {value.Result.MaxFrametimeMs:0.0} ms. Spikes are stutters.";
    }

    partial void OnCompareAChanged(ResultRowVm? value) => UpdateComparison();
    partial void OnCompareBChanged(ResultRowVm? value) => UpdateComparison();

    private void UpdateComparison()
    {
        if (CompareA == null || CompareB == null || CompareA == CompareB)
        {
            Comparison = "Pick two different runs to compare (A = before, B = after a change).";
            return;
        }
        var c = BenchmarkService.Compare(CompareA.Result, CompareB.Result);
        Comparison =
            $"Average FPS {c.AvgFpsDiffPct:+0.0;-0.0}%    1% low {c.Low1DiffPct:+0.0;-0.0}%    0.1% low {c.Low01DiffPct:+0.0;-0.0}%    stutters {c.StutterDiff:+0;-0;0}\n\n{c.Verdict}";
    }

    /// <summary>Keeps the spikes: each bucket shows its slowest frame.</summary>
    private static double[] Downsample(List<float> ft, int buckets)
    {
        if (ft.Count <= buckets) return ft.Select(x => (double)x).ToArray();
        var size = (double)ft.Count / buckets;
        var result = new double[buckets];
        for (var i = 0; i < buckets; i++)
        {
            var from = (int)(i * size);
            var to = Math.Min(ft.Count, (int)((i + 1) * size));
            double max = 0;
            for (var j = from; j < to; j++) max = Math.Max(max, ft[j]);
            result[i] = max;
        }
        return result;
    }

    [RelayCommand]
    private void FindGames()
    {
        if (!AppServices.Frametimes.Start())
        {
            Status = "Could not start frametime capture: " + AppServices.Frametimes.LastError;
            return;
        }
        _poll.Start();
        Status = "Listening for games that draw frames... (they appear within a second or two)";
        RefreshTargets();
    }

    private void RefreshTargets()
    {
        var list = AppServices.Frametimes.Presenting();
        var keep = Target?.Pid;
        Targets.Clear();
        foreach (var p in list) Targets.Add(p);
        Target = Targets.FirstOrDefault(t => t.Pid == keep) ?? (keep == null ? Targets.FirstOrDefault() : Target);
    }

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        if (IsCapturing) return;
        if (!AppServices.Frametimes.Start())
        {
            Status = "Could not start frametime capture: " + AppServices.Frametimes.LastError;
            return;
        }

        var pid = Target?.Pid;
        var name = Target?.Name ?? "";
        _cts = new CancellationTokenSource();
        IsCapturing = true;
        Progress = 0;
        lock (_samples) _samples.Clear();
        AppServices.Hub.SampleReceived += OnSample;

        try
        {
            for (var i = 5; i > 0; i--)
            {
                Status = $"Starting in {i}... switch to the game and play a repeatable scene.";
                await Task.Delay(1000, _cts.Token);
            }

            AppServices.Frametimes.BeginRecording();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < Duration)
            {
                await Task.Delay(250, _cts.Token);
                Progress = sw.Elapsed.TotalSeconds / Duration * 100;
                Status = $"Recording {Label}: {Duration - (int)sw.Elapsed.TotalSeconds} s left";
            }

            var capture = AppServices.Frametimes.EndRecording(pid);
            if (capture == null)
            {
                Status = "No frames were captured. Make sure the game is running and drawing (not minimized), and pick it in the list.";
                return;
            }

            if (string.IsNullOrEmpty(name))
            {
                try { using var p = Process.GetProcessById(capture.Pid); name = p.ProcessName; }
                catch { name = $"pid {capture.Pid}"; }
            }
            var game = AppServices.Watcher.Running.FirstOrDefault(r => r.Pids.Contains(capture.Pid))?.Game.Name ?? name;

            var result = BenchmarkService.Analyze(capture, game, name, Label);
            List<MetricSample> samples;
            lock (_samples) samples = _samples.ToList();
            var report = BottleneckService.Analyze(samples);
            result.Bottleneck = report.Verdict;
            ShowBottleneck(report);

            AppServices.Benchmarks.Save(result);
            var row = new ResultRowVm(result);
            History.Insert(0, row);
            SelectedResult = row;

            if (Label.StartsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                CompareA = row;
                Label = "B (after)";
                Status = $"Run A saved: {result.AvgFps:0} FPS average, {result.Low1Fps:0} 1% low. Now change ONE thing (a profile, a tweak) and record B in the same scene.";
            }
            else
            {
                CompareB = row;
                Status = $"Run saved: {result.AvgFps:0} FPS average, {result.Low1Fps:0} 1% low.";
            }
            UpdateComparison();
        }
        catch (OperationCanceledException)
        {
            AppServices.Frametimes.EndRecording(pid);
            Status = "Capture cancelled.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Benchmark failed", ex);
            Status = "Benchmark failed: " + ex.Message;
        }
        finally
        {
            AppServices.Hub.SampleReceived -= OnSample;
            IsCapturing = false;
            Progress = 0;
        }
    }

    private void OnSample(MetricSample s)
    {
        lock (_samples) _samples.Add(s);
    }

    [RelayCommand]
    private void CancelCapture() => _cts?.Cancel();

    [RelayCommand]
    private void DeleteResult(ResultRowVm? row)
    {
        if (row == null) return;
        AppServices.Benchmarks.Delete(row.Result);
        History.Remove(row);
        if (CompareA == row) CompareA = null;
        if (CompareB == row) CompareB = null;
    }

    [RelayCommand]
    private async Task AnalyzeBottleneckAsync()
    {
        IsAnalyzing = true;
        BottleneckVerdict = "Sampling sensors for 20 seconds. Play the game meanwhile...";
        BottleneckEvidence.Clear();
        BottleneckFixes.Clear();
        lock (_samples) _samples.Clear();
        AppServices.Hub.SampleReceived += OnSample;
        try
        {
            await Task.Delay(20_000);
        }
        finally
        {
            AppServices.Hub.SampleReceived -= OnSample;
            IsAnalyzing = false;
        }
        List<MetricSample> samples;
        lock (_samples) samples = _samples.ToList();
        ShowBottleneck(BottleneckService.Analyze(samples));
    }

    private void ShowBottleneck(BottleneckReport r)
    {
        BottleneckVerdict = r.Verdict;
        BottleneckEvidence.Clear();
        BottleneckFixes.Clear();
        foreach (var e in r.Evidence) BottleneckEvidence.Add(e);
        foreach (var f in r.Fixes) BottleneckFixes.Add(f);
    }

    [RelayCommand]
    private void ToggleOverlay()
    {
        _parent.SetOverlay(!_parent.OverlayVisible);
        OnPropertyChanged(nameof(OverlayVisible));
    }
}
