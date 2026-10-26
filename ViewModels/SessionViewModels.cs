using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public sealed record StatTile(string Label, string Value, string Sub);

public sealed class SessionRowVm
{
    public SessionRowVm(GameSession s) => Session = s;
    public GameSession Session { get; }
    public string Title => Session.Game;
    public string When => $"{Session.StartUtc.ToLocalTime():g}  ·  {Duration(Session.DurationMin)}";
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Session.AvgFps != null) parts.Add($"{Session.AvgFps:0} FPS avg   {Session.Low1Fps:0} 1% low");
            if (Session.PingAvg != null) parts.Add($"{Session.PingAvg:0} ms ping");
            if (parts.Count == 0) parts.Add("hardware stats only");
            return string.Join("   ", parts);
        }
    }
    public bool Boosted => Session.Boosted;

    public static string Duration(double min) => min >= 60 ? $"{(int)(min / 60)} h {min % 60:0} min" : $"{min:0} min";
}

/// <summary>FPS Boost Center > Session reports: one report per play session, recorded in the background.</summary>
public partial class SessionsTabViewModel : ObservableObject
{
    public SessionsTabViewModel()
    {
        _enabled = AppServices.Settings.Current.SessionReports;
        AppServices.Sessions.Saved += _ => UiThread.Post(Reload);
        AppServices.Watcher.RunningChanged += (_, _) => UiThread.Post(UpdateRecording);
        Reload();
        UpdateRecording();
    }

    public ObservableCollection<SessionRowVm> History { get; } = new();
    public ObservableCollection<StatTile> Tiles { get; } = new();
    public ObservableCollection<string> Evidence { get; } = new();
    public ObservableCollection<string> Tips { get; } = new();
    public ObservableCollection<string> BoostSteps { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _recordingStatus = "";
    [ObservableProperty] private SessionRowVm? _selected;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _bottleneck = "";
    [ObservableProperty] private string _comparison = "";
    [ObservableProperty] private string _fpsNote = "";
    [ObservableProperty] private IReadOnlyList<double> _fpsGraph = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _pingGraph = Array.Empty<double>();
    [ObservableProperty] private bool _hasPing;
    [ObservableProperty] private bool _hasFps;

    public bool IsEmpty => History.Count == 0;

    partial void OnEnabledChanged(bool value)
    {
        AppServices.Settings.Current.SessionReports = value;
        AppServices.Settings.Save();
        UpdateRecording();
    }

    private void UpdateRecording()
    {
        var running = AppServices.Watcher.Running.FirstOrDefault();
        RecordingStatus = !Enabled ? "Session reports are off."
            : AppServices.Sessions.IsRecording ? $"Recording {AppServices.Sessions.RecordingGame} right now. The report appears here when you close the game."
            : running != null ? $"{running.Game.Name} is running; recording starts with the next session."
            : "Waiting for a game. Every session longer than 2 minutes gets a report.";
    }

    public void Reload()
    {
        var id = Selected?.Session.Id;
        History.Clear();
        foreach (var s in AppServices.Sessions.LoadHistory()) History.Add(new SessionRowVm(s));
        Selected = History.FirstOrDefault(r => r.Session.Id == id) ?? History.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        UpdateRecording();
    }

    partial void OnSelectedChanged(SessionRowVm? value)
    {
        Tiles.Clear();
        Evidence.Clear();
        Tips.Clear();
        BoostSteps.Clear();
        Comparison = "";
        if (value == null) { Headline = ""; Bottleneck = ""; FpsGraph = PingGraph = Array.Empty<double>(); HasFps = HasPing = false; return; }

        var s = value.Session;
        Headline = $"{s.Game}  ·  {s.StartUtc.ToLocalTime():dddd d MMM, HH:mm}  ·  {SessionRowVm.Duration(s.DurationMin)}" +
                   (s.Boosted ? "  ·  boosted" : "  ·  not boosted");

        HasFps = s.AvgFps != null;
        if (HasFps)
        {
            Tiles.Add(new StatTile("Average FPS", $"{s.AvgFps:0}", $"{s.FrameSource}"));
            Tiles.Add(new StatTile("1% low", $"{s.Low1Fps:0}", "worst moments"));
            Tiles.Add(new StatTile("0.1% low", $"{s.Low01Fps:0}", "rare hitches"));
            Tiles.Add(new StatTile("Stutters", $"{s.Stutters}", $"{s.Stutters / Math.Max(s.DurationMin, 1):0.#} per min"));
            FpsNote = "";
        }
        else
        {
            FpsNote = "No FPS for this session: Vanta needs admin rights to read frametimes, and some games (OpenGL, some Vulkan) don't show up.";
        }
        FpsGraph = s.FpsTimeline;

        HasPing = s.PingAvg != null;
        if (HasPing)
        {
            Tiles.Add(new StatTile("Ping", $"{s.PingAvg:0} ms", s.Server));
            Tiles.Add(new StatTile("Jitter / loss", $"{s.JitterMs:0.#} ms", $"{s.LossPercent:0.#}% packets lost"));
        }
        PingGraph = s.PingTimeline;

        if (s.CpuTempMax != null) Tiles.Add(new StatTile("CPU temp", $"{s.CpuTempMax:0} °C", $"avg {s.CpuTempAvg:0} °C"));
        if (s.GpuTempMax != null) Tiles.Add(new StatTile("GPU temp", $"{s.GpuTempMax:0} °C", $"avg {s.GpuTempAvg:0} °C"));
        Tiles.Add(new StatTile("CPU / GPU load", $"{s.CpuLoadAvg:0}% / {(s.GpuLoadAvg is { } g ? $"{g:0}%" : "n/a")}", "average"));
        Tiles.Add(new StatTile("RAM / VRAM peak", $"{s.RamPeakPercent:0}% / {(s.VramPeakPercent is { } v ? $"{v:0}%" : "n/a")}", "highest"));

        Bottleneck = s.Bottleneck;
        foreach (var e in s.BottleneckEvidence) Evidence.Add(e);
        foreach (var t in s.Tips) Tips.Add(t);
        foreach (var b in s.BoostSteps) BoostSteps.Add(b);

        Comparison = Compare(s, History.Select(r => r.Session).Where(x => x.GameId == s.GameId && x.StartUtc < s.StartUtc).FirstOrDefault());
    }

    private static string Compare(GameSession now, GameSession? prev)
    {
        if (prev == null) return "First recorded session of this game. Play again and Vanta compares the two.";
        static string Pct(double? a, double? b) => a is > 0 && b != null ? $"{(b.Value - a.Value) / a.Value * 100:+0;-0;0}%" : "n/a";

        var parts = new List<string>();
        if (now.AvgFps != null && prev.AvgFps != null) parts.Add($"average FPS {Pct(prev.AvgFps, now.AvgFps)}, 1% lows {Pct(prev.Low1Fps, now.Low1Fps)}");
        if (now.PingAvg != null && prev.PingAvg != null) parts.Add($"ping {now.PingAvg - prev.PingAvg:+0;-0;0} ms");
        if (now.GpuTempMax != null && prev.GpuTempMax != null && Math.Abs(now.GpuTempMax.Value - prev.GpuTempMax.Value) >= 1) parts.Add($"GPU peak {now.GpuTempMax - prev.GpuTempMax:+0;-0;0} °C");
        if (parts.Count == 0) return "";
        var boostNote = now.Boosted != prev.Boosted ? (now.Boosted ? " This one was boosted, the last one wasn't." : " The last one was boosted, this one wasn't.") : "";
        return $"Compared with your previous {now.Game} session ({prev.StartUtc.ToLocalTime():d MMM}): {string.Join(", ", parts)}.{boostNote} " +
               "Different maps, modes and player counts change FPS a lot, so only trust differences above about 10%, or use the A/B benchmark for a fair test.";
    }

    [RelayCommand]
    private void Delete(SessionRowVm? row)
    {
        if (row == null) return;
        AppServices.Sessions.Delete(row.Session);
        History.Remove(row);
        if (Selected == null || Selected == row) Selected = History.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void OpenFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.Sessions}\"") { UseShellExecute = true });
}
