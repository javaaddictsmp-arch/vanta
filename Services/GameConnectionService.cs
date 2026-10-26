using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TuneUp.Services;

/// <summary>Live readings for the running game's server connection.</summary>
public sealed class GameConnectionSnapshot
{
    public string Game { get; init; } = "";
    public string Server { get; init; } = "";
    public string Protocol { get; init; } = "";
    /// <summary>What is being pinged: the server itself, or the last router before it.</summary>
    public string Measuring { get; init; } = "";
    public double? LastMs { get; init; }
    public double AvgMs { get; init; }
    public double JitterMs { get; init; }
    public double LossPercent { get; init; }
    public int Sent { get; init; }
    public IReadOnlyList<double> History { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Finds the server the running game is actually talking to and pings it live (like ExitLag's
/// connection graph). Detection: Windows' kernel network ETW events (works for UDP, which most
/// games use; needs admin), with the TCP connection table as a fallback. Many game servers ignore
/// ping, so when that happens Vanta measures to the last router that answers on the way there.
/// Read-only: it never touches or redirects the game's traffic.
/// </summary>
public sealed class GameConnectionService : IDisposable
{
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private readonly object _gate = new();
    private readonly Dictionary<(string Ip, int Port, string Proto), (int Packets, DateTime Last)> _endpoints = new();
    private readonly List<double> _history = new();
    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private HashSet<int> _pids = new();
    private string _game = "";
    private (string Ip, int Port, string Proto)? _server;
    private IPAddress? _pingTarget;
    private string _measuring = "";
    private int _sent, _lost;
    private int _udpSeen, _tcpSeen;
    private double? _last;

    public event Action<GameConnectionSnapshot>? Updated;
    public bool IsRunning => _cts != null;
    public bool EtwAvailable { get; private set; }

    public GameConnectionSnapshot? Latest { get; private set; }

    /// <summary>Starts following a game (call again with new PIDs when they change).</summary>
    public void Follow(string game, IEnumerable<int> pids)
    {
        lock (_gate)
        {
            var newPids = pids.ToHashSet();
            if (game != _game) { _endpoints.Clear(); _server = null; _pingTarget = null; ResetStats(); }
            _game = game;
            _pids = newPids;
        }
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        StartEtw();
        _ = LoopAsync(_cts.Token);
        AppLog.Info($"Following game server for {game}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        lock (_gate) { _endpoints.Clear(); _server = null; _pingTarget = null; _game = ""; ResetStats(); }
        Latest = null;
    }

    private void ResetStats()
    {
        _history.Clear();
        _sent = _lost = 0;
        _last = null;
    }

    // ---- Detection ----------------------------------------------------------------------------------
    private void StartEtw()
    {
        try
        {
            _session = new TraceEventSession("Vanta-GameNet") { StopOnDispose = true };
            _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x10 /* IPv4 */);
            var parser = new RegisteredTraceEventParser(_session.Source);
            parser.All += OnNetEvent;
            var s = _session;
            new Thread(() => { try { s.Source.Process(); } catch (Exception ex) { AppLog.Warn("Game network ETW stopped: " + ex.Message); } })
            { IsBackground = true, Name = "Vanta game network" }.Start();
            EtwAvailable = true;
        }
        catch (Exception ex)
        {
            EtwAvailable = false;
            AppLog.Warn("Kernel network events unavailable (needs admin), using the TCP table instead: " + ex.Message);
            try { _session?.Dispose(); } catch { }
            _session = null;
        }
    }

    // Kernel-Network: 10/11 = TCPv4 send/receive, 42/43 = UDPv4 send/receive.
    private void OnNetEvent(TraceEvent e)
    {
        if (e.ProviderGuid != KernelNetwork) return;
        var id = (int)e.ID;
        var proto = id is 42 or 43 ? "UDP" : id is 10 or 11 ? "TCP" : null;
        if (proto == null) return;
        try
        {
            var pid = Convert.ToInt32(e.PayloadByName("PID"));
            lock (_gate) { if (!_pids.Contains(pid)) return; }
            // Which field holds the remote end differs between event versions, so take whichever is
            // public (this PC's own address is almost always a private LAN address behind the router).
            var d = ToIp(e.PayloadByName("daddr"));
            var s = ToIp(e.PayloadByName("saddr"));
            if (d != null && IsPublic(d)) Count(d.ToString(), ToPort(e.PayloadByName("dport")), proto);
            else if (s != null && IsPublic(s)) Count(s.ToString(), ToPort(e.PayloadByName("sport")), proto);
        }
        catch { /* malformed event */ }
    }

    private static IPAddress? ToIp(object? v) => v switch
    {
        IPAddress a => a,
        string s when IPAddress.TryParse(s, out var a) => a,
        uint u => new IPAddress(u),
        int i => new IPAddress(BitConverter.GetBytes(i)),
        _ => null
    };

    /// <summary>The event's port fields are 16-bit values in network byte order (443 arrives as 0xBB01).</summary>
    private static int ToPort(object? v)
    {
        long raw = v switch
        {
            null => 0,
            string s => long.TryParse(s, out var p) ? p : 0,
            _ => Convert.ToInt64(v)
        };
        var u = unchecked((ushort)raw);
        return (ushort)((u >> 8) | (u << 8));
    }

    private void Count(string ip, int port, string proto)
    {
        if (proto == "UDP") Interlocked.Increment(ref _udpSeen); else Interlocked.Increment(ref _tcpSeen);
        // DNS, and web traffic (store, login, stats APIs on 80/443): never the match server. Pinging
        // those showed a misleadingly low "in-game" ping for Rocket League while in menus.
        if (port is 53 or 0 or 80 or 443) return;
        lock (_gate)
        {
            var key = (ip, port, proto);
            _endpoints[key] = _endpoints.TryGetValue(key, out var v) ? (v.Packets + 1, DateTime.UtcNow) : (1, DateTime.UtcNow);
        }
    }

    private void ScanTcpTable()
    {
        foreach (var (pid, ip, port) in TcpConnections())
        {
            bool ours;
            lock (_gate) ours = _pids.Contains(pid);
            if (ours && IsPublic(ip)) Count(ip.ToString(), port, "TCP");
        }
    }

    /// <summary>Picks the busiest public endpoint: UDP first (game traffic), web ports last.</summary>
    private (string Ip, int Port, string Proto)? PickServer()
    {
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            foreach (var stale in _endpoints.Where(kv => kv.Value.Last < cutoff).Select(kv => kv.Key).ToList()) _endpoints.Remove(stale);
            return _endpoints
                .OrderBy(kv => kv.Key.Proto == "UDP" ? 0 : 1)
                .ThenByDescending(kv => kv.Value.Packets)
                .Select(kv => ((string Ip, int Port, string Proto)?)kv.Key)
                .FirstOrDefault();
        }
    }

    // ---- Ping loop -----------------------------------------------------------------------------------
    private async Task LoopAsync(CancellationToken ct)
    {
        var failures = 0;
        var ticks = 0;
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (++ticks % 60 == 0) // once a minute: what traffic is actually visible (for troubleshooting)
                    AppLog.Info($"Game network for {_game}: {Interlocked.Exchange(ref _udpSeen, 0)} UDP / {Interlocked.Exchange(ref _tcpSeen, 0)} TCP packets to public addresses in the last minute " +
                                $"({(EtwAvailable ? "kernel events" : "TCP table only")}), following {(_server is { } sv ? $"{sv.Ip}:{sv.Port} {sv.Proto}" : "nothing yet")}");
                if (!EtwAvailable) ScanTcpTable();
                var server = PickServer();
                if (server != null && server != _server)
                {
                    _server = server;
                    _pingTarget = IPAddress.Parse(server.Value.Ip);
                    _measuring = "the game server";
                    failures = 0;
                    lock (_gate) ResetStats();
                    AppLog.Info($"Game server for {_game}: {server.Value.Ip}:{server.Value.Port} ({server.Value.Proto})");
                }

                if (_pingTarget != null)
                {
                    double? ms = null;
                    try
                    {
                        var r = await ping.SendPingAsync(_pingTarget, 1000);
                        if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
                    }
                    catch { }

                    if (ms == null && _measuring == "the game server" && ++failures >= 3)
                    {
                        // Server ignores ping: measure to the last router on the way that answers.
                        var hop = await LastRespondingHopAsync(_pingTarget, ct);
                        if (hop != null)
                        {
                            _pingTarget = hop;
                            _measuring = $"the last router before the server ({hop}); the server itself ignores ping";
                            lock (_gate) ResetStats();
                            continue;
                        }
                    }
                    if (ms != null) failures = 0;

                    lock (_gate)
                    {
                        _sent++;
                        if (ms == null) _lost++;
                        else { _history.Add(ms.Value); if (_history.Count > 300) _history.RemoveAt(0); }
                        _last = ms;
                    }
                    Publish();
                }
                else Publish();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Game ping loop: " + ex.Message);
            }

            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void Publish()
    {
        GameConnectionSnapshot snap;
        lock (_gate)
        {
            var h = _history.ToArray();
            var jitter = h.Length > 1 ? h.Zip(h.Skip(1), (a, b) => Math.Abs(b - a)).Average() : 0;
            snap = new GameConnectionSnapshot
            {
                Game = _game,
                Server = _server is { } s ? $"{s.Ip}:{s.Port}" : "",
                Protocol = _server?.Proto ?? "",
                Measuring = _server == null ? "" : _measuring,
                LastMs = _last,
                AvgMs = h.Length > 0 ? h.Average() : 0,
                JitterMs = jitter,
                LossPercent = _sent == 0 ? 0 : _lost * 100.0 / _sent,
                Sent = _sent,
                History = h
            };
        }
        Latest = snap;
        Updated?.Invoke(snap);
    }

    private static async Task<IPAddress?> LastRespondingHopAsync(IPAddress dest, CancellationToken ct)
    {
        IPAddress? last = null;
        using var ping = new Ping();
        for (var ttl = 1; ttl <= 30 && !ct.IsCancellationRequested; ttl++)
        {
            try
            {
                var r = await ping.SendPingAsync(dest, 800, new byte[32], new PingOptions(ttl, true));
                if (r.Status == IPStatus.TtlExpired && r.Address != null && IsPublic(r.Address)) last = r.Address;
                if (r.Status == IPStatus.Success) return r.Address;
            }
            catch { }
        }
        return last;
    }

    // ---- Helpers -------------------------------------------------------------------------------------
    private static bool IsPublic(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224 ||
                 (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>Established IPv4 TCP connections with their owning PID (TCP_TABLE_OWNER_PID_ALL).</summary>
    private static IEnumerable<(int Pid, IPAddress Ip, int Port)> TcpConnections()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        var buf = Marshal.AllocHGlobal(size);
        var list = new List<(int, IPAddress, int)>();
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return list;
            var rows = Marshal.ReadInt32(buf);
            for (var i = 0; i < rows; i++)
            {
                var row = buf + 4 + i * 24; // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                if (Marshal.ReadInt32(row) != 5) continue; // ESTABLISHED
                var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 12)));
                var portRaw = Marshal.ReadInt32(row, 16);
                var port = ((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF);
                list.Add((Marshal.ReadInt32(row, 20), remote, port));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose() => Stop();
}
