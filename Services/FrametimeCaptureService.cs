using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TuneUp.Services;

public sealed record PresentingProcess(int Pid, string Name, double Fps);

public sealed record CaptureResult(string Source, int Pid, List<float> Frametimes, double DurationSec);

/// <summary>
/// PresentMon-style frametime capture through ETW (the same Windows event providers PresentMon
/// and CapFrameX read). Nothing is injected into the game, so it is safe with anti-cheat.
///
/// Frametime = time between consecutive Present() calls of a process ("MsBetweenPresents").
/// Sources, in order of preference per process:
///   DXGI    Microsoft-Windows-DXGI  Present_Start (42) / PresentMultiplaneOverlay_Start (55)  - DX10/11/12
///   D3D9    Microsoft-Windows-D3D9  Present_Start (1)                                         - DX9
///   DxgKrnl Microsoft-Windows-DxgKrnl Present_Info (184)                                      - fallback (some Vulkan/OpenGL)
/// Vulkan/OpenGL games are only seen when the driver presents through DXGI or DxgKrnl, which
/// recent NVIDIA/AMD drivers usually do.
/// </summary>
public sealed class FrametimeCaptureService : IDisposable
{
    private static readonly Guid DxgiGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Guid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnlGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const ulong DxgKrnlKeywords = 0x1 | 0x8000000; // Base | Present

    private static readonly string[] SourcePriority = { "DXGI", "D3D9", "DxgKrnl" };

    private sealed class Track
    {
        public double LastMs = -1;
        public readonly Queue<double> Recent = new(); // timestamps of the last ~2 s, for live FPS
        public List<float>? Recording;
    }

    private readonly object _gate = new();
    private readonly Dictionary<(int Pid, string Source), Track> _tracks = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private double _nowMs;
    private bool _recording;
    private double _recordStartMs;

    public bool IsRunning => _session != null;
    public string? LastError { get; private set; }

    /// <summary>Starts the ETW session (needs admin). Safe to call repeatedly.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_session != null) return true;
            try
            {
                // If a previous Vanta crashed, a session with this name may still exist; Create replaces it.
                _session = new TraceEventSession("Vanta-Frametimes") { StopOnDispose = true };
                _session.EnableProvider(DxgiGuid, TraceEventLevel.Informational, ulong.MaxValue);
                _session.EnableProvider(D3d9Guid, TraceEventLevel.Informational, ulong.MaxValue);
                _session.EnableProvider(DxgKrnlGuid, TraceEventLevel.Informational, DxgKrnlKeywords);

                var parser = new RegisteredTraceEventParser(_session.Source);
                parser.All += OnEvent;

                var session = _session;
                _thread = new Thread(() =>
                {
                    try { session.Source.Process(); }
                    catch (Exception ex) { AppLog.Error("Frametime ETW loop stopped", ex); }
                }) { IsBackground = true, Name = "Vanta ETW frametimes" };
                _thread.Start();

                LastError = null;
                AppLog.Info("Frametime capture session started");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Error("Could not start frametime capture (ETW)", ex);
                _session?.Dispose();
                _session = null;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _session?.Dispose(); } catch { /* ignore */ }
            _session = null;
            _tracks.Clear();
        }
    }

    private void OnEvent(TraceEvent e)
    {
        string? source = null;
        var id = (int)e.ID;
        if (e.ProviderGuid == DxgiGuid && id is 42 or 55) source = "DXGI";
        else if (e.ProviderGuid == D3d9Guid && id == 1) source = "D3D9";
        else if (e.ProviderGuid == DxgKrnlGuid && id == 184) source = "DxgKrnl";
        if (source == null) return;

        var t = e.TimeStampRelativeMSec;
        float? frame = null;
        lock (_gate)
        {
            _nowMs = t;
            var key = (e.ProcessID, source);
            if (!_tracks.TryGetValue(key, out var track))
            {
                track = new Track();
                if (_recording) track.Recording = new List<float>();
                _tracks[key] = track;
            }

            if (track.LastMs >= 0)
            {
                var ft = t - track.LastMs;
                if (ft is > 0 and < 5000) // ignore pauses longer than 5 s
                {
                    track.Recording?.Add((float)ft);
                    frame = (float)ft;
                }
            }
            track.LastMs = t;

            track.Recent.Enqueue(t);
            while (track.Recent.Count > 0 && t - track.Recent.Peek() > 2000) track.Recent.Dequeue();
        }
        if (frame != null) FrameObserved?.Invoke(e.ProcessID, source, frame.Value);
    }

    /// <summary>Every frametime seen (pid, source, ms), on the ETW thread. Used by session reports
    /// so they can record in the background without disturbing benchmark recordings.</summary>
    public event Action<int, string, float>? FrameObserved;

    /// <summary>Processes that presented frames in the last 2 seconds, busiest first.</summary>
    public List<PresentingProcess> Presenting()
    {
        lock (_gate)
        {
            return _tracks
                .Where(kv => kv.Value.Recent.Count > 1 && _nowMs - kv.Value.LastMs < 2000)
                .GroupBy(kv => kv.Key.Pid)
                .Select(g =>
                {
                    var best = g.OrderBy(kv => Array.IndexOf(SourcePriority, kv.Key.Source)).First().Value;
                    return new PresentingProcess(g.Key, NameOf(g.Key), FpsOf(best));
                })
                .Where(p => !p.Name.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Fps)
                .ToList();
        }
    }

    /// <summary>Live FPS for a process over the last ~2 s (for the overlay), null if not presenting.</summary>
    public double? LiveFps(int pid)
    {
        lock (_gate)
        {
            foreach (var src in SourcePriority)
                if (_tracks.TryGetValue((pid, src), out var t) && t.Recent.Count > 1 && _nowMs - t.LastMs < 2000)
                    return FpsOf(t);
            return null;
        }
    }

    public void BeginRecording()
    {
        lock (_gate)
        {
            _recording = true;
            _recordStartMs = _nowMs;
            foreach (var t in _tracks.Values) t.Recording = new List<float>();
        }
    }

    /// <summary>Stops recording and returns the frames of the chosen process (or the busiest one).</summary>
    public CaptureResult? EndRecording(int? pid)
    {
        lock (_gate)
        {
            _recording = false;
            var duration = (_nowMs - _recordStartMs) / 1000.0;

            var candidates = _tracks
                .Where(kv => kv.Value.Recording is { Count: >= 30 } && (pid == null || kv.Key.Pid == pid))
                .Where(kv => !NameOf(kv.Key.Pid).Equals("dwm", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var t in _tracks.Values) t.Recording = null;
            if (candidates.Count == 0) return null;

            // Per process prefer DXGI > D3D9 > DxgKrnl; across processes pick the one with most frames.
            var chosen = candidates
                .GroupBy(kv => kv.Key.Pid)
                .Select(g => g.OrderBy(kv => Array.IndexOf(SourcePriority, kv.Key.Source)).First())
                .OrderByDescending(kv => kv.Value.Recording!.Count)
                .First();

            return new CaptureResult(chosen.Key.Source, chosen.Key.Pid, chosen.Value.Recording!, duration);
        }
    }

    private static double FpsOf(Track t)
    {
        if (t.Recent.Count < 2) return 0;
        var span = t.Recent.Last() - t.Recent.Peek();
        return span <= 0 ? 0 : (t.Recent.Count - 1) * 1000.0 / span;
    }

    private static string NameOf(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return $"pid {pid}";
        }
    }

    public void Dispose() => Stop();
}
