using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuneUp.Services;

public sealed record PingTarget(string Name, string Host);
public sealed record Hop(int Number, string Address, string HostName, long? Ms);
public sealed record DnsProvider(string Name, string Primary, string Secondary);
public sealed record DnsResult(string Name, string Server, double? MedianMs, int Failures);
public sealed record AdapterInfo(string Id, string Name, string Description, string Type, string Speed, List<string> Dns, bool DnsIsStatic);
public sealed record NicProperty(string Adapter, string DisplayName, string Value, List<string> Choices, string Advice);
public sealed record WifiInfo(string Ssid, int Signal, string Band, int Channel, string Radio, Dictionary<int, int> ChannelUse, string Suggestion);
public sealed record QosPolicy(string Name, string App, string Action);

public sealed class SpeedResult
{
    public double DownMbps { get; init; }
    public double UpMbps { get; init; }
    public double IdleMs { get; init; }
    public double LoadedMs { get; init; }
    public string Grade { get; init; } = "";
    public string Verdict { get; init; } = "";
}

/// <summary>
/// Network tools. Only the test buttons contact the internet (pings, a Cloudflare speed test,
/// DNS lookups), and only when clicked. Settings changes use Windows' own netsh/NetAdapter tools
/// and keep the original values so they can be restored.
/// </summary>
public sealed class NetworkService
{
    // Many games (Fortnite, Apex, Valorant, Rocket League...) run on AWS or similar clouds, so
    // region endpoints are a good stand-in for "how far am I from the game servers".
    public static readonly PingTarget[] Targets =
    {
        new("Cloudflare (1.1.1.1)", "1.1.1.1"),
        new("Google DNS (8.8.8.8)", "8.8.8.8"),
        new("US East (Virginia)", "dynamodb.us-east-1.amazonaws.com"),
        new("US Central (Ohio)", "dynamodb.us-east-2.amazonaws.com"),
        new("US West (Oregon)", "dynamodb.us-west-2.amazonaws.com"),
        new("EU West (London)", "dynamodb.eu-west-2.amazonaws.com"),
        new("EU Central (Frankfurt)", "dynamodb.eu-central-1.amazonaws.com"),
        new("Asia (Singapore)", "dynamodb.ap-southeast-1.amazonaws.com"),
        new("Asia (Tokyo)", "dynamodb.ap-northeast-1.amazonaws.com"),
        new("Oceania (Sydney)", "dynamodb.ap-southeast-2.amazonaws.com"),
        new("South America (São Paulo)", "dynamodb.sa-east-1.amazonaws.com"),
    };

    public static readonly DnsProvider[] DnsProviders =
    {
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("Quad9 (blocks malware)", "9.9.9.9", "149.112.112.112"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220"),
    };

    // ---- Ping ----------------------------------------------------------------------------------------
    /// <summary>ICMP ping; if the host ignores ICMP (cloud endpoints often do), time a TCP connect to 443 instead.</summary>
    public static async Task<long?> PingOnceAsync(string host, CancellationToken ct)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, 1500);
            if (r.Status == IPStatus.Success) return r.RoundtripTime;
        }
        catch { /* fall through to TCP */ }

        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            if (addrs.Length == 0) return null;
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            await sock.ConnectAsync(addrs[0], 443, cts.Token);
            return sw.ElapsedMilliseconds;
        }
        catch { return null; }
    }

    public static async Task<List<Hop>> TraceRouteAsync(string host, IProgress<Hop>? progress, CancellationToken ct)
    {
        var hops = new List<Hop>();
        IPAddress? dest;
        try { dest = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
        catch { return hops; }
        if (dest == null) return hops;

        using var ping = new Ping();
        for (var ttl = 1; ttl <= 30; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            Hop hop;
            try
            {
                var sw = Stopwatch.StartNew();
                var r = await ping.SendPingAsync(dest, 1500, new byte[32], new PingOptions(ttl, true));
                var ms = sw.ElapsedMilliseconds;
                var addr = r.Address?.ToString() ?? "*";
                var name = "";
                if (r.Address != null)
                {
                    try
                    {
                        var lookup = Dns.GetHostEntryAsync(r.Address);
                        if (await Task.WhenAny(lookup, Task.Delay(600, ct)) == lookup) name = lookup.Result.HostName;
                    }
                    catch { /* no reverse name */ }
                }
                hop = new Hop(ttl, addr, name, r.Status is IPStatus.Success or IPStatus.TtlExpired ? ms : null);
                hops.Add(hop);
                progress?.Report(hop);
                if (r.Status == IPStatus.Success) break;
            }
            catch
            {
                hop = new Hop(ttl, "*", "", null);
                hops.Add(hop);
                progress?.Report(hop);
            }
        }
        return hops;
    }

    // ---- Speed test + bufferbloat --------------------------------------------------------------------
    public static async Task<SpeedResult> SpeedTestAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vanta/0.2");

        progress?.Report("Measuring idle latency...");
        var idle = await LatencySamplesAsync(10, 200, ct);

        // Download for ~8 s while pinging, to see how much latency rises under load (bufferbloat).
        progress?.Report("Testing download speed...");
        var loadedTask = LatencySamplesAsync(30, 250, ct);
        var down = await TransferAsync(async (token, add) =>
        {
            using var resp = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=200000000", HttpCompletionOption.ResponseHeadersRead, token);
            await using var s = await resp.Content.ReadAsStreamAsync(token);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await s.ReadAsync(buf, token)) > 0) add(n);
        }, TimeSpan.FromSeconds(8), ct);
        var loaded = await loadedTask;

        progress?.Report("Testing upload speed...");
        var up = await TransferAsync(async (token, add) =>
        {
            var payload = new byte[4 * 1024 * 1024];
            while (!token.IsCancellationRequested)
            {
                using var content = new ByteArrayContent(payload);
                using var resp = await http.PostAsync("https://speed.cloudflare.com/__up", content, token);
                add(payload.Length);
            }
        }, TimeSpan.FromSeconds(6), ct);

        var idleMs = Median(idle);
        var loadedMs = Median(loaded);
        var added = loadedMs - idleMs;
        var grade = added < 30 ? "A" : added < 60 ? "B" : added < 200 ? "C" : added < 400 ? "D" : "F";
        return new SpeedResult
        {
            DownMbps = down, UpMbps = up, IdleMs = idleMs, LoadedMs = loadedMs, Grade = grade,
            Verdict = grade switch
            {
                "A" or "B" => "Latency barely rises when the connection is busy. Downloads in the background won't hurt your ping much.",
                "C" => "Latency rises noticeably under load. If someone streams or downloads while you play, expect lag spikes. Turning on SQM / Smart QoS in your router fixes this best.",
                _ => "Bad bufferbloat: ping shoots up whenever the connection is busy. Enable SQM / Smart QoS (or 'cake'/'fq_codel') in your router, or limit background downloads while gaming."
            }
        };
    }

    /// <summary>Runs a transfer for a fixed time and returns Mbps from a running byte counter.</summary>
    private static async Task<double> TransferAsync(Func<CancellationToken, Action<long>, Task> work, TimeSpan duration, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        try { await work(cts.Token, n => Interlocked.Add(ref bytes, n)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* time is up: expected */ }
        catch (Exception ex) { AppLog.Warn("Speed test transfer: " + ex.Message); }
        var total = Interlocked.Read(ref bytes);
        return total > 0 ? total * 8 / sw.Elapsed.TotalSeconds / 1_000_000 : 0;
    }

    private static async Task<List<double>> LatencySamplesAsync(int count, int intervalMs, CancellationToken ct)
    {
        var list = new List<double>();
        for (var i = 0; i < count; i++)
        {
            var ms = await PingOnceAsync("1.1.1.1", ct);
            if (ms != null) list.Add(ms.Value);
            await Task.Delay(intervalMs, ct);
        }
        return list;
    }

    private static double Median(List<double> xs) => xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt(xs.Count / 2);

    // ---- DNS ------------------------------------------------------------------------------------------
    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)) continue;
            var dns = nic.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            var ns = RegistryService.Read("HKLM", $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}", "NameServer");
            list.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description,
                nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet",
                nic.Speed > 0 ? $"{nic.Speed / 1_000_000:N0} Mbps" : "", dns, ns.Exists && !string.IsNullOrWhiteSpace(ns.Value)));
        }
        return list;
    }

    /// <summary>Sets DNS on an adapter, remembering the original (DHCP or static list) the first time.</summary>
    public static async Task<bool> SetDnsAsync(AdapterInfo a, DnsProvider p, SettingsService settings)
    {
        if (!settings.Current.SavedDns.ContainsKey(a.Id))
        {
            settings.Current.SavedDns[a.Id] = a.DnsIsStatic ? a.Dns : new List<string>();
            settings.Save();
        }
        var r1 = await ProcessRunner.RunAsync("netsh.exe", $"interface ipv4 set dnsservers name=\"{a.Name}\" static {p.Primary} primary validate=no", 20_000);
        var r2 = await ProcessRunner.RunAsync("netsh.exe", $"interface ipv4 add dnsservers name=\"{a.Name}\" {p.Secondary} index=2 validate=no", 20_000);
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", 10_000);
        AppLog.Info($"DNS on {a.Name} set to {p.Name}: {r1.ExitCode}/{r2.ExitCode}");
        return r1.Ok;
    }

    public static async Task<bool> RestoreDnsAsync(AdapterInfo a, SettingsService settings)
    {
        settings.Current.SavedDns.TryGetValue(a.Id, out var saved);
        ProcResult r;
        if (saved == null || saved.Count == 0)
            r = await ProcessRunner.RunAsync("netsh.exe", $"interface ipv4 set dnsservers name=\"{a.Name}\" source=dhcp", 20_000);
        else
        {
            r = await ProcessRunner.RunAsync("netsh.exe", $"interface ipv4 set dnsservers name=\"{a.Name}\" static {saved[0]} primary validate=no", 20_000);
            for (var i = 1; i < saved.Count; i++)
                await ProcessRunner.RunAsync("netsh.exe", $"interface ipv4 add dnsservers name=\"{a.Name}\" {saved[i]} index={i + 1} validate=no", 20_000);
        }
        if (r.Ok) { settings.Current.SavedDns.Remove(a.Id); settings.Save(); }
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", 10_000);
        return r.Ok;
    }

    /// <summary>Times real DNS lookups (raw UDP queries, so Windows' cache doesn't skew it).</summary>
    public static async Task<List<DnsResult>> BenchmarkDnsAsync(IEnumerable<(string Name, string Server)> servers, IProgress<string>? progress, CancellationToken ct)
    {
        string[] domains = { "google.com", "steamcommunity.com", "epicgames.com", "riotgames.com", "discord.com", "youtube.com" };
        var results = new List<DnsResult>();
        foreach (var (name, server) in servers)
        {
            progress?.Report($"Testing {name} ({server})...");
            var times = new List<double>();
            var fails = 0;
            for (var round = 0; round < 3; round++)
                foreach (var d in domains)
                {
                    ct.ThrowIfCancellationRequested();
                    var ms = await QueryAsync(server, d, ct);
                    if (ms == null) fails++; else times.Add(ms.Value);
                }
            results.Add(new DnsResult(name, server, times.Count == 0 ? null : Median(times), fails));
        }
        return results.OrderBy(r => r.MedianMs ?? double.MaxValue).ToList();
    }

    private static async Task<double?> QueryAsync(string server, string domain, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            var id = (ushort)Random.Shared.Next(ushort.MaxValue);
            var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in domain.Split('.')) { q.Add((byte)label.Length); q.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // end, type A, class IN
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(q.ToArray(), q.Count, new IPEndPoint(IPAddress.Parse(server), 53));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            var resp = await udp.ReceiveAsync(cts.Token);
            return resp.Buffer.Length >= 2 && resp.Buffer[0] == (byte)(id >> 8) ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch { return null; }
    }

    // ---- Adapter advanced properties (via the NetAdapter PowerShell module) ----------------------------
    private static readonly (string Pattern, string Advice)[] InterestingProps =
    {
        ("Interrupt Moderation", "Off = slightly lower latency, a bit more CPU use. Placebo/Marginal for most games."),
        ("Energy.?Efficient Ethernet|EEE|Advanced EEE", "Off can fix small latency spikes or link drops on some Realtek/Intel chips. Situational."),
        ("Green Ethernet", "Power saving that can add tiny delays. Off is fine on desktops. Situational."),
        ("Power Saving Mode|Gigabit Lite|Auto Disable Gigabit", "Power saving. Off keeps the full link speed. Situational."),
        ("Flow Control", "Leave on unless you have a specific problem."),
    };

    public static async Task<List<NicProperty>> NicPropertiesAsync()
    {
        var r = await ProcessRunner.PowerShellAsync(
            "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { Get-NetAdapterAdvancedProperty -Name $_.Name } | " +
            "Select-Object Name, DisplayName, DisplayValue, ValidDisplayValues | ConvertTo-Json -Depth 3 -Compress", 60_000);
        var list = new List<NicProperty>();
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut)) return list;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var e in items)
            {
                var display = e.GetProperty("DisplayName").GetString() ?? "";
                var match = InterestingProps.FirstOrDefault(p => Regex.IsMatch(display, p.Pattern, RegexOptions.IgnoreCase));
                if (match.Pattern == null) continue;
                var choices = e.TryGetProperty("ValidDisplayValues", out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                list.Add(new NicProperty(e.GetProperty("Name").GetString() ?? "", display, e.GetProperty("DisplayValue").GetString() ?? "", choices, match.Advice));
            }
        }
        catch (Exception ex) { AppLog.Warn("Parsing NIC properties failed: " + ex.Message); }
        return list;
    }

    private static string NicBackupFile => Path.Combine(AppPaths.Root, "nic-backup.json");

    public static async Task<bool> SetNicPropertyAsync(NicProperty p, string value)
    {
        // Remember the very first value we saw, so "Restore original" really goes back to stock.
        var backup = LoadNicBackup();
        var key = $"{p.Adapter}|{p.DisplayName}";
        if (!backup.ContainsKey(key)) { backup[key] = p.Value; File.WriteAllText(NicBackupFile, JsonSerializer.Serialize(backup)); }

        var r = await ProcessRunner.PowerShellAsync(
            $"Set-NetAdapterAdvancedProperty -Name '{Esc(p.Adapter)}' -DisplayName '{Esc(p.DisplayName)}' -DisplayValue '{Esc(value)}' -NoRestart:$false", 60_000);
        AppLog.Info($"NIC {key} -> {value}: {r.ExitCode} {r.StdErr}");
        return r.Ok && string.IsNullOrWhiteSpace(r.StdErr);
    }

    public static async Task<int> RestoreNicAsync()
    {
        var backup = LoadNicBackup();
        var n = 0;
        foreach (var (key, value) in backup)
        {
            var parts = key.Split('|');
            var r = await ProcessRunner.PowerShellAsync($"Set-NetAdapterAdvancedProperty -Name '{Esc(parts[0])}' -DisplayName '{Esc(parts[1])}' -DisplayValue '{Esc(value)}'", 60_000);
            if (r.Ok) n++;
        }
        try { File.Delete(NicBackupFile); } catch { }
        return n;
    }

    public static bool HasNicBackup => File.Exists(NicBackupFile);

    private static Dictionary<string, string> LoadNicBackup()
    {
        try { return File.Exists(NicBackupFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NicBackupFile)) ?? new() : new(); }
        catch { return new(); }
    }

    private static string Esc(string s) => s.Replace("'", "''");

    public static Task<ProcResult> DisableNicPowerSavingAsync() =>
        ProcessRunner.PowerShellAsync("Get-NetAdapter -Physical | ForEach-Object { Set-NetAdapterPowerManagement -Name $_.Name -AllowComputerToTurnOffDevice Disabled -ErrorAction SilentlyContinue }", 60_000);

    // ---- MTU -------------------------------------------------------------------------------------------
    /// <summary>Largest packet that passes without fragmenting (binary search with the DF flag).</summary>
    public static async Task<int?> FindMtuAsync(CancellationToken ct)
    {
        int lo = 1200, hi = 1472, best = 0;
        using var ping = new Ping();
        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            var mid = (lo + hi) / 2;
            var ok = false;
            try { ok = (await ping.SendPingAsync("1.1.1.1", 1500, new byte[mid], new PingOptions(64, true))).Status == IPStatus.Success; }
            catch { }
            if (ok) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best == 0 ? null : best + 28; // + IP and ICMP headers
    }

    // ---- Wi-Fi -------------------------------------------------------------------------------------------
    public static async Task<(WifiInfo? Info, string? Error)> WifiAsync()
    {
        var iface = await ProcessRunner.RunAsync("netsh.exe", "wlan show interfaces", 15_000);
        if (!iface.Ok || !iface.StdOut.Contains("SSID")) return (null, "No Wi-Fi connection (or you are on Ethernet, which is better for gaming anyway).");

        string F(string label) => Regex.Match(iface.StdOut, @"^\s*" + label + @"\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        int.TryParse(F("Signal").TrimEnd('%'), out var signal);
        int.TryParse(F("Channel"), out var channel);
        var band = F("Band");
        if (band == "") band = channel > 14 ? "5 GHz" : "2.4 GHz";

        var nets = await ProcessRunner.RunAsync("netsh.exe", "wlan show networks mode=bssid", 15_000);
        var use = new Dictionary<int, int>();
        foreach (Match m in Regex.Matches(nets.StdOut, @"^\s*Channel\s*:\s*(\d+)", RegexOptions.Multiline))
        {
            var ch = int.Parse(m.Groups[1].Value);
            use[ch] = use.GetValueOrDefault(ch) + 1;
        }

        string suggestion;
        if (channel is > 0 and <= 14)
        {
            var best = new[] { 1, 6, 11 }.OrderBy(c => Enumerable.Range(c - 2, 5).Sum(x => use.GetValueOrDefault(x))).First();
            suggestion = best == channel
                ? $"You're already on the least crowded 2.4 GHz channel ({channel}). If your router has 5 GHz, use it: it is faster and less crowded."
                : $"Channel {best} looks less crowded than your channel {channel}. Change it in your router's Wi-Fi settings, or better, connect on 5 GHz.";
        }
        else
        {
            suggestion = use.GetValueOrDefault(channel) > 2
                ? $"{use[channel]} networks share your channel {channel}. Try another 5 GHz channel in your router (36-48 or 149-165)."
                : "5 GHz with little competition on your channel. Good.";
        }
        if (signal is > 0 and < 50) suggestion = $"Weak signal ({signal}%). Moving closer, a mesh node, or an Ethernet cable will help more than any setting. " + suggestion;
        if (use.Count == 0) suggestion += " (Nearby networks are hidden: on Windows 11, Location access must be on for Wi-Fi scanning.)";

        return (new WifiInfo(F("SSID"), signal, band, channel, F("Radio type"), use, suggestion), null);
    }

    // ---- QoS (Windows policy-based QoS) ------------------------------------------------------------------
    public static async Task<List<QosPolicy>> QosPoliciesAsync()
    {
        var r = await ProcessRunner.PowerShellAsync(
            "Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object Name -like 'Vanta*' | Select-Object Name, AppPathName, DSCPAction, ThrottleRateAction | ConvertTo-Json -Compress", 30_000);
        var list = new List<QosPolicy>();
        if (string.IsNullOrWhiteSpace(r.StdOut)) return list;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var e in items)
            {
                var dscp = e.TryGetProperty("DSCPAction", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : -1;
                var thr = e.TryGetProperty("ThrottleRateAction", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                list.Add(new QosPolicy(e.GetProperty("Name").GetString() ?? "", e.TryGetProperty("AppPathName", out var a) ? a.GetString() ?? "" : "",
                    thr > 0 ? $"upload limited to {thr / 1_000_000.0:0.#} Mbps" : dscp >= 0 ? $"marked DSCP {dscp} (priority)" : ""));
            }
        }
        catch (Exception ex) { AppLog.Warn("QoS parse: " + ex.Message); }
        return list;
    }

    public static Task<ProcResult> AddPriorityPolicyAsync(string exeName) =>
        ProcessRunner.PowerShellAsync($"New-NetQosPolicy -Name 'Vanta priority {Esc(exeName)}' -AppPathNameMatchCondition '{Esc(exeName)}' -DSCPAction 46 -NetworkProfile All", 30_000);

    public static Task<ProcResult> AddUploadLimitAsync(string exeName, double mbps) =>
        ProcessRunner.PowerShellAsync($"New-NetQosPolicy -Name 'Vanta limit {Esc(exeName)}' -AppPathNameMatchCondition '{Esc(exeName)}' -ThrottleRateActionBitsPerSecond {(long)(mbps * 1_000_000)} -NetworkProfile All", 30_000);

    public static Task<ProcResult> RemovePolicyAsync(string name) =>
        ProcessRunner.PowerShellAsync($"Remove-NetQosPolicy -Name '{Esc(name)}' -Confirm:$false", 30_000);
}
