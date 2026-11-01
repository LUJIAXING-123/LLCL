using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LLCL.Core.Link;

/// <summary>局域网发现到的一个可加入世界。</summary>
public sealed class LanWorld
{
    public string Motd { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Source { get; set; } = "broadcast"; // broadcast / scan
    public int OnlinePlayers { get; set; }
    public int MaxPlayers { get; set; }
    public string? HostName { get; set; }

    public string Display => string.IsNullOrEmpty(Motd) ? $"世界 ({Port})" : $"{Motd} · {Version} · {Port}";
}

/// <summary>
/// 局域网多人游戏扫描器。
/// 同时使用两种方式：
/// 1. 监听 Minecraft 局域网广播（224.0.2.60:4445 组播，[MOTD]..[/MOTD][AD]port[/AD]）；
/// 2. 主动探测本机回环与常见范围端口。
/// </summary>
public sealed class LanScanner : IDisposable
{
    private static readonly IPAddress MulticastV4 = IPAddress.Parse("224.0.2.60");
    private static readonly IPAddress MulticastV6 = IPAddress.Parse("ff75:230::60");
    private const int McBroadcastPort = 4445;

    private readonly HashSet<int> _foundPorts = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _pingGate = new(32);
    private bool _disposed;

    public event Action<LanWorld>? OnWorldFound;

    public List<LanWorld> Worlds { get; } = [];

    /// <summary>开始扫描，持续指定时间后返回发现的世界。</summary>
    public async Task<List<LanWorld>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        lock (_lock) { Worlds.Clear(); _foundPorts.Clear(); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);

        var tasks = new List<Task>
        {
            _ListenBroadcastAsync(cts.Token),
            _ProbeLocalAsync(cts.Token)
        };

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 正常结束 */ }
        catch (Exception ex) { Logger.Warn($"局域网扫描异常: {ex.Message}"); }

        lock (_lock)
        {
            return Worlds.OrderByDescending(w => w.OnlinePlayers).ToList();
        }
    }

    private async Task _ListenBroadcastAsync(CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, McBroadcastPort));
            try { client.JoinMulticastGroup(MulticastV4); } catch { }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await client.ReceiveAsync(ct).ConfigureAwait(false);
                    _HandleBroadcast(Encoding.UTF8.GetString(result.Buffer));
                }
                catch (OperationCanceledException) { throw; }
                catch (SocketException) { await Task.Delay(100, ct).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"局域网广播监听失败（端口 {McBroadcastPort} 可能被占用）: {ex.Message}");
        }
    }

    private void _HandleBroadcast(string message)
    {
        var motd = _ExtractTag(message, "MOTD");
        var ad = _ExtractTag(message, "AD");
        if (ad is null) return;

        int port;
        try { port = int.Parse(ad.Trim()); }
        catch { return; }

        _TryAddWorld(motd, port, "broadcast");
    }

    private static string? _ExtractTag(string message, string tag)
    {
        var start = message.IndexOf($"[{tag}]", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += tag.Length + 2;
        var end = message.IndexOf($"[/{tag}]", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        return message[start..end].Trim();
    }

    /// <summary>探测本机回环及局域网可达主机上的 Minecraft 端口。</summary>
    private async Task _ProbeLocalAsync(CancellationToken ct)
    {
        var ports = new List<int>();

        // 本地监听端口（最可靠的“局域网开放”来源）
        try
        {
            var listeners = IPGlobalPropertiesListeners();
            foreach (var port in listeners)
                if (port is >= 1024 and <= 65535 && port != McBroadcastPort) ports.Add(port);
        }
        catch (Exception ex)
        {
            Logger.Warn($"枚举本地监听端口失败: {ex.Message}");
        }

        // 常见局域网世界端口
        foreach (var common in new[] { 25565, 25566, 25567, 25568, 25569, 25570, 5555, 19132 })
            ports.Add(common);

        foreach (var port in ports.Distinct())
        {
            if (ct.IsCancellationRequested) return;
            await _TryPingAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
        }
    }

    private static List<int> IPGlobalPropertiesListeners()
    {
        var result = new List<int>();
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            foreach (var info in props.GetActiveTcpListeners())
                if (info.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(info.Port);
        }
        catch
        {
        }

        return result;
    }

    private async Task _TryPingAsync(IPAddress address, int port, CancellationToken ct)
    {
        await _pingGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await McPing.PingAsync(address.ToString(), port, TimeSpan.FromMilliseconds(1800), ct)
                .ConfigureAwait(false);
            if (result is null) return;

            _TryAddWorld(result.Description, port, "scan", result.Version, result.Online ?? 0, result.Max ?? 0);
        }
        finally
        {
            _pingGate.Release();
        }
    }

    private void _TryAddWorld(string? motd, int port, string source, string? version = null,
        int online = 0, int max = 0)
    {
        lock (_lock)
        {
            if (!_foundPorts.Add(port)) return;

            var world = new LanWorld
            {
                Motd = motd ?? string.Empty,
                Version = version ?? string.Empty,
                Port = port,
                Source = source,
                OnlinePlayers = online,
                MaxPlayers = max
            };

            Worlds.Add(world);
            try { OnWorldFound?.Invoke(world); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pingGate.Dispose();
    }
}
