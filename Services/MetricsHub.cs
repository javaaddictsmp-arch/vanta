using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Samples hardware once per interval on a background thread and fans the result out to
/// whoever listens (Dashboard, Monitoring, CSV logger). Keeps a short rolling history.
/// </summary>
public sealed class MetricsHub : IDisposable
{
    private const int MaxHistory = 300;

    private readonly HardwareMonitorService _hardware;
    private readonly SettingsService _settings;
    private readonly List<MetricSample> _history = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MetricsHub(HardwareMonitorService hardware, SettingsService settings)
    {
        _hardware = hardware;
        _settings = settings;
    }

    /// <summary>Raised on a background thread. Subscribers must marshal to the UI thread.</summary>
    public event Action<MetricSample>? SampleReceived;

    public bool SensorsAvailable => _hardware.SensorsAvailable;

    public MetricSample? Latest
    {
        get { lock (_gate) return _history.LastOrDefault(); }
    }

    public List<MetricSample> History()
    {
        lock (_gate) return _history.ToList();
    }

    public void Start()
    {
        if (_loop != null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _loop = Task.Run(async () =>
        {
            _hardware.Open();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var sample = _hardware.Sample();
                    lock (_gate)
                    {
                        _history.Add(sample);
                        if (_history.Count > MaxHistory) _history.RemoveAt(0);
                    }
                    SampleReceived?.Invoke(sample);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Metrics loop error", ex);
                }

                try
                {
                    var interval = Math.Clamp(_settings.Current.SampleIntervalMs, 250, 10_000);
                    await Task.Delay(interval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch { /* ignore */ }
        _hardware.Dispose();
        _cts?.Dispose();
    }
}
