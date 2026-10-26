using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public sealed record RegionPing(string Name, string Result, long? Ms);

public partial class NicPropertyVm : ObservableObject
{
    private readonly NetworkViewModel _owner;
    private bool _loaded;

    public NicPropertyVm(NicProperty p, NetworkViewModel owner)
    {
        Property = p;
        _owner = owner;
        _value = p.Value;
        _loaded = true;
    }

    public NicProperty Property { get; }
    public string Title => $"{Property.DisplayName}";
    public string Adapter => Property.Adapter;
    public string Advice => Property.Advice;
    public List<string> Choices => Property.Choices;

    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value)
    {
        if (_loaded && value != Property.Value) _ = _owner.SetNicAsync(this, value);
    }
}

public partial class NetworkViewModel : ObservableObject
{
    private CancellationTokenSource? _pingCts;
    private readonly List<double> _pingValues = new();
    private int _sent, _lost;

    public NetworkViewModel()
    {
        Target = NetworkService.Targets[0];
        Tweaks = new TweakListVm(new[] { "network-throttling-off" });
    }

    public IReadOnlyList<PingTarget> Targets => NetworkService.Targets;
    public IReadOnlyList<DnsProvider> Providers => NetworkService.DnsProviders;
    public TweakListVm Tweaks { get; }

    public ObservableCollection<RegionPing> Regions { get; } = new();
    public ObservableCollection<Hop> Hops { get; } = new();
    public ObservableCollection<AdapterInfo> Adapters { get; } = new();
    public ObservableCollection<DnsResult> DnsResults { get; } = new();
    public ObservableCollection<NicPropertyVm> NicProps { get; } = new();
    public ObservableCollection<QosPolicy> Policies { get; } = new();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isBusy;

    // Ping
    [ObservableProperty] private PingTarget _target;
    [ObservableProperty] private string _customHost = "";
    [ObservableProperty] private bool _isPinging;
    [ObservableProperty] private IReadOnlyList<double> _pingGraph = Array.Empty<double>();
    [ObservableProperty] private string _pingNow = "--";
    [ObservableProperty] private string _pingStats = "";
    [ObservableProperty] private string _pingVerdict = "";
    [ObservableProperty] private string _traceStatus = "";

    // Speed
    [ObservableProperty] private string _speedStatus = "";
    [ObservableProperty] private SpeedResult? _speed;

    // DNS
    [ObservableProperty] private AdapterInfo? _selectedAdapter;
    [ObservableProperty] private DnsProvider? _selectedProvider;
    [ObservableProperty] private string _dnsStatus = "";

    // Adapter & Wi-Fi
    [ObservableProperty] private string _nicStatus = "";
    [ObservableProperty] private string _mtuStatus = "";
    [ObservableProperty] private WifiInfo? _wifi;
    [ObservableProperty] private string _wifiStatus = "";

    // QoS
    [ObservableProperty] private string _qosExe = "";
    [ObservableProperty] private double _qosMbps = 5;
    [ObservableProperty] private string _qosStatus = "";

    // Live game connection
    [ObservableProperty] private bool _followGameServer = AppServices.Settings.Current.FollowGameServer;
    [ObservableProperty] private bool _gameConnActive;
    [ObservableProperty] private string _gameConnTitle = "";
    [ObservableProperty] private string _gameConnPing = "--";
    [ObservableProperty] private string _gameConnStats = "";
    [ObservableProperty] private string _gameConnDetail = "";
    [ObservableProperty] private IReadOnlyList<double> _gameConnGraph = Array.Empty<double>();
    [ObservableProperty] private string _gameConnHint = "";

    public void Activate()
    {
        LoadAdapters();
        SelectedProvider ??= Providers[0];
        AppServices.GameConnection.Updated += OnGameConnection;
        ShowGameConnection(AppServices.GameConnection.Latest);
    }

    public void Deactivate()
    {
        StopPing();
        AppServices.GameConnection.Updated -= OnGameConnection;
    }

    partial void OnFollowGameServerChanged(bool value)
    {
        AppServices.Settings.Current.FollowGameServer = value;
        AppServices.Settings.Save();
        AppServices.FollowGameServer();
        ShowGameConnection(AppServices.GameConnection.Latest);
    }

    private void OnGameConnection(GameConnectionSnapshot s) => UiThread.Post(() => ShowGameConnection(s));

    private void ShowGameConnection(GameConnectionSnapshot? s)
    {
        var running = AppServices.Watcher.Running.FirstOrDefault();
        GameConnActive = s != null && !string.IsNullOrEmpty(s.Server);
        if (!FollowGameServer) { GameConnHint = "Turned off. Switch it on to see your real in-game ping while you play."; GameConnActive = false; return; }
        if (running == null) { GameConnHint = "No game running. Start a game (and join a match) and Vanta finds the server it's connected to."; GameConnActive = false; return; }
        if (!GameConnActive)
        {
            GameConnHint = $"{running.Game.Name} is running. Looking for its server: join a match or lobby." +
                           (AppServices.GameConnection.EtwAvailable ? "" : " Without admin rights Vanta can only see TCP connections, and most games use UDP, so run Vanta as administrator for this.");
            return;
        }

        GameConnHint = "";
        GameConnTitle = $"{s!.Game}  ·  {s.Server} ({s.Protocol})";
        GameConnPing = s.LastMs is { } ms ? $"{ms:0} ms" : "timeout";
        GameConnGraph = s.History.TakeLast(120).ToArray();
        GameConnStats = s.History.Count > 0
            ? $"avg {s.AvgMs:0} ms   jitter {s.JitterMs:0.0} ms   loss {s.LossPercent:0.#}% ({s.Sent} sent)"
            : "waiting for replies...";
        GameConnDetail = $"Measuring to {s.Measuring}." +
                         (s.Measuring.StartsWith("the last router") ? " The real in-game ping is usually a few ms higher." : "");
    }

    private string Host => string.IsNullOrWhiteSpace(CustomHost) ? Target.Host : CustomHost.Trim();

    // ---- Ping monitor ----------------------------------------------------------------------------------
    [RelayCommand]
    private async Task StartPingAsync()
    {
        StopPing();
        _pingCts = new CancellationTokenSource();
        var token = _pingCts.Token;
        _pingValues.Clear();
        _sent = _lost = 0;
        IsPinging = true;
        PingVerdict = "";
        try
        {
            while (!token.IsCancellationRequested)
            {
                var ms = await NetworkService.PingOnceAsync(Host, token);
                _sent++;
                if (ms == null) _lost++;
                else
                {
                    _pingValues.Add(ms.Value);
                    if (_pingValues.Count > 120) _pingValues.RemoveAt(0);
                }
                UpdatePingStats(ms);
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsPinging = false; }
    }

    private void UpdatePingStats(long? last)
    {
        PingGraph = _pingValues.ToArray();
        PingNow = last is { } l ? $"{l} ms" : "timeout";
        if (_pingValues.Count < 2) return;
        var jitter = _pingValues.Zip(_pingValues.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        var loss = _sent == 0 ? 0 : _lost * 100.0 / _sent;
        PingStats = $"avg {_pingValues.Average():0} ms   min {_pingValues.Min():0}   max {_pingValues.Max():0}   jitter {jitter:0.0} ms   loss {loss:0.#}% ({_sent} sent)";
        PingVerdict = loss >= 2 ? "Packet loss: you'll see rubber-banding or teleporting. Check cables/Wi-Fi first; if it persists, contact your ISP."
                    : jitter >= 15 ? "High jitter: your ping jumps around, which feels worse than a steady higher ping. Wi-Fi or a busy connection (see Speed tab) is the usual cause."
                    : _pingValues.Average() > 120 ? "Stable but far: that region is a long way from you. Pick servers closer to you in-game."
                    : "Looks healthy: steady ping, no loss.";
    }

    [RelayCommand]
    private void StopPing()
    {
        _pingCts?.Cancel();
        _pingCts = null;
    }

    [RelayCommand]
    private async Task PingAllAsync()
    {
        IsBusy = true;
        Regions.Clear();
        try
        {
            foreach (var t in Targets)
            {
                var samples = new List<long>();
                for (var i = 0; i < 4; i++)
                {
                    var ms = await NetworkService.PingOnceAsync(t.Host, CancellationToken.None);
                    if (ms != null) samples.Add(ms.Value);
                }
                var avg = samples.Count > 0 ? (long?)samples.Average() : null;
                Regions.Add(new RegionPing(t.Name, avg is { } a ? $"{a} ms" : "no reply", avg));
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TraceAsync()
    {
        IsBusy = true;
        Hops.Clear();
        TraceStatus = $"Tracing the route to {Host}...";
        try
        {
            var hops = await NetworkService.TraceRouteAsync(Host, new Progress<Hop>(h => Hops.Add(h)), CancellationToken.None);
            TraceStatus = hops.Count == 0 ? "Could not resolve that address." :
                $"{hops.Count} hops. A hop with a big jump in ms that stays high for every hop after it shows where the slowdown starts. Hops showing '*' just don't answer pings (that's normal).";
        }
        finally { IsBusy = false; }
    }

    // ---- Speed ------------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task SpeedTestAsync()
    {
        IsBusy = true;
        Speed = null;
        try
        {
            Speed = await NetworkService.SpeedTestAsync(new Progress<string>(m => SpeedStatus = m), CancellationToken.None);
            SpeedStatus = "Done. The test used Cloudflare's public speed test servers.";
        }
        catch (Exception ex) { SpeedStatus = "Speed test failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ---- DNS --------------------------------------------------------------------------------------------
    [RelayCommand]
    private void LoadAdapters()
    {
        var keep = SelectedAdapter?.Id;
        Adapters.Clear();
        foreach (var a in NetworkService.Adapters()) Adapters.Add(a);
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == keep) ?? Adapters.FirstOrDefault();
    }

    public bool CanRestoreDns => SelectedAdapter != null && AppServices.Settings.Current.SavedDns.ContainsKey(SelectedAdapter.Id);
    partial void OnSelectedAdapterChanged(AdapterInfo? value) => OnPropertyChanged(nameof(CanRestoreDns));

    [RelayCommand]
    private async Task BenchmarkDnsAsync()
    {
        IsBusy = true;
        DnsResults.Clear();
        try
        {
            var servers = Providers.Select(p => (p.Name, p.Primary)).ToList();
            if (SelectedAdapter != null)
                servers.InsertRange(0, SelectedAdapter.Dns.Take(2).Select(d => ($"Current ({SelectedAdapter.Name})", d)));
            foreach (var r in await NetworkService.BenchmarkDnsAsync(servers, new Progress<string>(m => DnsStatus = m), CancellationToken.None))
                DnsResults.Add(r);
            DnsStatus = "Lower is better. DNS speed only affects how fast names are looked up (opening sites, joining matchmaking); it does not change your in-game ping.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SetDnsAsync()
    {
        if (SelectedAdapter == null || SelectedProvider == null) return;
        if (MessageBox.Show($"Use {SelectedProvider.Name} DNS ({SelectedProvider.Primary}, {SelectedProvider.Secondary}) on {SelectedAdapter.Name}?\n\nVanta remembers your current DNS so 'Restore my DNS' puts it back.",
                "Change DNS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            DnsStatus = await NetworkService.SetDnsAsync(SelectedAdapter, SelectedProvider, AppServices.Settings)
                ? $"{SelectedAdapter.Name} now uses {SelectedProvider.Name} DNS." : "Could not change DNS (needs admin rights).";
            LoadAdapters();
            OnPropertyChanged(nameof(CanRestoreDns));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreDnsAsync()
    {
        if (SelectedAdapter == null) return;
        IsBusy = true;
        try
        {
            DnsStatus = await NetworkService.RestoreDnsAsync(SelectedAdapter, AppServices.Settings)
                ? "Your previous DNS settings are back." : "Could not restore DNS (needs admin rights).";
            LoadAdapters();
            OnPropertyChanged(nameof(CanRestoreDns));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        var r = await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", 15_000);
        DnsStatus = r.Ok ? "DNS cache cleared. Fixes 'site can't be reached' after a website or server moved." : "Could not flush DNS.";
    }

    [RelayCommand]
    private async Task ResetStackAsync()
    {
        if (MessageBox.Show("Reset Windows networking (Winsock and TCP/IP) to defaults?\n\nThis fixes many 'connected but no internet' problems. It removes custom network settings (like a static IP) and needs a restart.",
                "Reset network", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ProcessRunner.RunAsync("netsh.exe", "winsock reset", 30_000);
        var r = await ProcessRunner.RunAsync("netsh.exe", "int ip reset", 30_000);
        DnsStatus = r.Ok ? "Network reset done. Restart your PC to finish." : "Reset failed (needs admin rights).";
    }

    // ---- Adapter & Wi-Fi -------------------------------------------------------------------------------
    [RelayCommand]
    private async Task LoadNicAsync()
    {
        IsBusy = true;
        NicStatus = "Reading network adapter settings...";
        NicProps.Clear();
        try
        {
            foreach (var p in await NetworkService.NicPropertiesAsync()) NicProps.Add(new NicPropertyVm(p, this));
            NicStatus = NicProps.Count == 0
                ? "No adjustable latency settings found on your adapter (or Vanta needs admin rights)."
                : "Changing a setting briefly disconnects the network (a few seconds). The first original value is saved for 'Restore originals'.";
        }
        finally { IsBusy = false; }
        OnPropertyChanged(nameof(HasNicBackup));
    }

    public bool HasNicBackup => NetworkService.HasNicBackup;

    public async Task SetNicAsync(NicPropertyVm vm, string value)
    {
        NicStatus = $"Setting {vm.Title} to {value}...";
        NicStatus = await NetworkService.SetNicPropertyAsync(vm.Property, value)
            ? $"{vm.Title} set to {value}." : $"Could not change {vm.Title} (needs admin rights).";
        OnPropertyChanged(nameof(HasNicBackup));
    }

    [RelayCommand]
    private async Task RestoreNicAsync()
    {
        IsBusy = true;
        try
        {
            var n = await NetworkService.RestoreNicAsync();
            NicStatus = $"Restored {n} adapter setting(s) to their original values.";
        }
        finally { IsBusy = false; }
        await LoadNicAsync();
    }

    [RelayCommand]
    private async Task DisableNicPowerSavingAsync()
    {
        var r = await NetworkService.DisableNicPowerSavingAsync();
        NicStatus = r.Ok ? "Windows will no longer turn off your network adapter to save power." : "Could not change it (needs admin rights).";
    }

    [RelayCommand]
    private async Task MtuTestAsync()
    {
        MtuStatus = "Testing the largest packet size that gets through without splitting...";
        var mtu = await NetworkService.FindMtuAsync(CancellationToken.None);
        MtuStatus = mtu switch
        {
            null => "Could not test (pings to 1.1.1.1 are blocked on your network).",
            >= 1500 => $"MTU {mtu}: the standard maximum. Nothing to change.",
            _ => $"MTU {mtu}: lower than the usual 1500 (common with PPPoE/fibre or VPNs). Windows usually detects this itself; only change your adapter MTU if you have connection problems."
        };
    }

    [RelayCommand]
    private async Task WifiCheckAsync()
    {
        var (info, error) = await NetworkService.WifiAsync();
        Wifi = info;
        WifiStatus = error ?? info!.Suggestion;
    }

    // ---- QoS ---------------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task LoadQosAsync()
    {
        Policies.Clear();
        foreach (var p in await NetworkService.QosPoliciesAsync()) Policies.Add(p);
    }

    private string? QosExeName()
    {
        var exe = Path.GetFileName(QosExe.Trim());
        if (string.IsNullOrEmpty(exe)) { QosStatus = "Type the program's EXE name, e.g. steam.exe"; return null; }
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
    }

    [RelayCommand]
    private async Task AddPriorityAsync()
    {
        if (QosExeName() is not { } exe) return;
        var r = await NetworkService.AddPriorityPolicyAsync(exe);
        QosStatus = r.Ok && string.IsNullOrWhiteSpace(r.StdErr) ? $"{exe} traffic is now marked as high priority (DSCP 46)." : "Could not add the policy (needs admin rights).";
        await LoadQosAsync();
    }

    [RelayCommand]
    private async Task AddLimitAsync()
    {
        if (QosExeName() is not { } exe) return;
        var r = await NetworkService.AddUploadLimitAsync(exe, Math.Max(0.5, QosMbps));
        QosStatus = r.Ok && string.IsNullOrWhiteSpace(r.StdErr) ? $"{exe} can now upload at most {QosMbps:0.#} Mbps." : "Could not add the limit (needs admin rights).";
        await LoadQosAsync();
    }

    [RelayCommand]
    private async Task RemovePolicyAsync(QosPolicy? p)
    {
        if (p == null) return;
        await NetworkService.RemovePolicyAsync(p.Name);
        await LoadQosAsync();
    }
}
