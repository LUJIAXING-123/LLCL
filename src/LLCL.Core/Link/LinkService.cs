using System.Net;
using System.Net.Sockets;

namespace LLCL.Core.Link;

public enum LinkState
{
    Idle,
    Initializing,
    Scanning,
    Creating,
    Joining,
    Connected,
    Leaving,
    Error
}

public sealed class LinkEventArgs : EventArgs
{
    public required LinkState State { get; init; }
    public string? Message { get; init; }
    public Exception? Exception { get; init; }

    /// <summary>已翻译成中文的提示（可直接显示在界面）。</summary>
    public string? FriendlyMessage { get; init; }
}

/// <summary>联机状态快照，供状态面板直接绑定。</summary>
public sealed record LinkStatus
{
    public LinkState State { get; init; } = LinkState.Idle;
    public bool IsHost { get; init; }
    public string? RoomCode { get; init; }

    /// <summary>房主的虚拟网络 IPv4。</summary>
    public string? VirtualIp { get; init; }

    /// <summary>加入方在虚拟网络中的本地 IP（从 peer 列表推断，仅展示用）。</summary>
    public string? LocalVirtualIp { get; init; }

    /// <summary>当前使用的中继节点。</summary>
    public string? Relay { get; init; }

    /// <summary>中继是否正常工作。</summary>
    public RelayStatus RelayState { get; init; } = RelayStatus.Unknown;

    /// <summary>与房主的延迟（毫秒）。</summary>
    public double Ping { get; init; }

    /// <summary>当前房间在线人数（含房主）。</summary>
    public int OnlineCount { get; init; }

    /// <summary>加入方本地转发端口。</summary>
    public int? LocalPort { get; init; }

    /// <summary>房间内成员昵称。</summary>
    public IReadOnlyList<string> Players { get; init; } = [];

    public string StateText => State switch
    {
        LinkState.Idle => "未联机",
        LinkState.Initializing => "正在准备内核",
        LinkState.Scanning => "正在扫描局域网",
        LinkState.Creating => "正在创建房间",
        LinkState.Joining => "正在加入房间",
        LinkState.Connected => "已连接",
        LinkState.Leaving => "正在离开",
        _ => "发生错误"
    };

    public string RelayText => RelayState switch
    {
        RelayStatus.Direct => "直连（P2P）",
        RelayStatus.Relay => "经中继",
        RelayStatus.Local => "局域网直连",
        RelayStatus.Failed => "中继不可用",
        _ => "未知"
    };

    public string PingText => Ping <= 0 ? "—" : $"{Ping:F0} ms";
}

public enum RelayStatus
{
    Unknown,
    Direct,
    Relay,
    Local,
    Failed
}

/// <summary>
/// 联机会话管理：创建房间（EasyTier 主机）、加入房间（房间码 + 端口转发）、
/// 局域网扫描、成员列表刷新、离开房间。
/// 所有对外错误都经过 <see cref="FriendlyError"/> 翻译成中文提示。
/// </summary>
public sealed class LinkService : IAsyncDisposable
{
    private static LinkService? _instance;
    public static LinkService Instance => _instance ??= new LinkService();

    private readonly EasyTierCore _core = new();
    private LanScanner? _scanner;
    private LanBroadcaster? _broadcaster;
    private TcpForwarder? _forwarder;
    private Timer? _peerTimer;
    private volatile bool _disposed;

    private List<PeerInfo> _lastPeers = [];

    public LinkState State { get; private set; } = LinkState.Idle;
    public bool IsHost { get; private set; }
    public string? CurrentRoomCode { get; private set; }
    public int? JoinerLocalPort { get; private set; }
    public string DisplayName { get; private set; } = "Player";
    public int HostPort { get; private set; } = RoomCode.DefaultPort;

    /// <summary>最近一次的计算状态快照。</summary>
    public LinkStatus Status { get; private set; } = new();

    public event Action<LinkEventArgs>? OnStateChanged;
    public event Action<List<PeerInfo>>? OnPeersUpdated;
    public event Action<LanWorld>? OnWorldFound;
    public event Action<string>? OnLog;
    public event Action<LinkStatus>? OnStatusUpdated;

    private LinkService()
    {
        _core.OnLog += line => OnLog?.Invoke(line);
    }

    #region 内核准备

    /// <summary>启动时调用：同步/更新 EasyTier 内核并检查新版本。</summary>
    public async Task<string?> PrepareAsync(IProgress<(string step, double ratio)>? progress = null,
        CancellationToken ct = default)
    {
        _SetState(LinkState.Initializing, "检查 EasyTier 内核");
        try
        {
            var version = await EasyTierCore.EnsureInstalledAsync(progress, ct).ConfigureAwait(false);
            Logger.Info($"EasyTier 内核就绪: {version}");

            if (LauncherConfig.Current.AutoUpdateEasyTier)
            {
                try
                {
                    var updated = await EasyTierCore.CheckUpdateAsync(false, ct).ConfigureAwait(false);
                    if (updated is not null)
                    {
                        version = updated;
                        Logger.Info($"EasyTier 已自动更新到 {updated}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"EasyTier 自动更新失败: {ex.Message}");
                }
            }

            _SetState(LinkState.Idle, $"EasyTier {version} 已就绪");
            return version;
        }
        catch (Exception ex)
        {
            var friendly = FriendlyError.LogAndTranslate(ex, "EasyTier 初始化失败");
            _SetState(LinkState.Error, friendly, ex);
            return null;
        }
    }

    #endregion

    #region 局域网扫描

    /// <summary>扫描局域网中的开放多人游戏。</summary>
    public async Task<List<LanWorld>> ScanLanAsync(TimeSpan? duration = null, CancellationToken ct = default)
    {
        _scanner?.Dispose();
        _scanner = new LanScanner();
        _scanner.OnWorldFound += w => OnWorldFound?.Invoke(w);

        _SetState(LinkState.Scanning, "正在扫描局域网开放游戏");
        var worlds = await _scanner.ScanAsync(duration ?? TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        _SetState(LinkState.Idle, worlds.Count == 0 ? "未发现局域网世界" : $"发现 {worlds.Count} 个局域网世界");
        return worlds;
    }

    #endregion

    #region 创建 / 加入

    /// <summary>
    /// 创建房间。
    /// </summary>
    /// <param name="displayName">显示昵称。</param>
    /// <param name="mcPort">Minecraft 局域网世界端口（可手动填写）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> CreateRoomAsync(string displayName, int mcPort, CancellationToken ct = default)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();

        // 端口合法性前置检查，避免把底层 SocketException 直接抛给用户
        if (!FriendlyError.IsPortAvailable(mcPort, out var portError))
        {
            _SetState(LinkState.Error, portError ?? "端口不可用");
            return false;
        }

        HostPort = mcPort;

        try
        {
            _SetState(LinkState.Creating, "正在创建房间");

            var version = await EasyTierCore.EnsureInstalledAsync(null, ct).ConfigureAwait(false);
            var code = RoomCode.Generate(mcPort);

            await _core.StartAsync(version, code, true, DisplayName, mcPort, ct).ConfigureAwait(false);

            IsHost = true;
            CurrentRoomCode = code.FullCode;

            _broadcaster = new LanBroadcaster();
            _broadcaster.Start($"LLCL 房间 · {DisplayName}", mcPort);

            RoomHistory.Add(code.FullCode, mcPort, true, DisplayName);
            _StartPeerTimer();
            _SetState(LinkState.Connected, $"房间已创建：{code.FullCode}");
            return true;
        }
        catch (Exception ex)
        {
            var friendly = FriendlyError.LogAndTranslate(ex, "创建房间失败");
            CleanupInternal();
            _SetState(LinkState.Error, friendly, ex);
            return false;
        }
    }

    /// <summary>通过房间码加入房间。端口可从房间码解析，也可手动覆盖。</summary>
    public async Task<bool> JoinRoomAsync(string roomCode, string displayName, int? portOverride = null,
        CancellationToken ct = default)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();

        var parsed = RoomCode.Parse(roomCode, out var error, portOverride);
        if (parsed is null)
        {
            _SetState(LinkState.Error, error ?? "房间码无效");
            return false;
        }

        HostPort = parsed.Port;

        try
        {
            _SetState(LinkState.Joining, "正在加入房间");

            var version = await EasyTierCore.EnsureInstalledAsync(null, ct).ConfigureAwait(false);
            await _core.StartAsync(version, parsed, false, DisplayName, parsed.Port, ct).ConfigureAwait(false);

            IsHost = false;
            CurrentRoomCode = parsed.FullCode;

            _SetState(LinkState.Joining, "等待虚拟网络就绪");
            var ready = await _WaitForNetworkAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (!ready)
            {
                CleanupInternal();
                _SetState(LinkState.Error,
                    "未能连接到房间。请确认房主仍在线、房间码正确，且双方网络可以访问中继服务器。");
                return false;
            }

            var forwardPort = await _core.AddPortForwardAsync(RoomCode.HostIp, parsed.Port, ct)
                .ConfigureAwait(false);
            JoinerLocalPort = forwardPort;

            _forwarder = new TcpForwarder(forwardPort, RoomCode.HostIp, parsed.Port);
            _forwarder.Start();

            RoomHistory.Add(parsed.FullCode, parsed.Port, false, DisplayName);
            _StartPeerTimer();
            _SetState(LinkState.Connected, $"已加入房间：{parsed.FullCode}，本地端口 {forwardPort}");
            return true;
        }
        catch (Exception ex)
        {
            var friendly = FriendlyError.LogAndTranslate(ex, "加入房间失败");
            CleanupInternal();
            _SetState(LinkState.Error, friendly, ex);
            return false;
        }
    }

    private async Task<bool> _WaitForNetworkAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var peers = await _core.GetPeersAsync(ct).ConfigureAwait(false);
            if (peers.Any(p => p.Hostname.StartsWith("H|", StringComparison.Ordinal)))
            {
                _lastPeers = peers;
                OnPeersUpdated?.Invoke(peers);
                return true;
            }

            await Task.Delay(800, ct).ConfigureAwait(false);
        }

        return false;
    }

    #endregion

    #region 状态刷新

    private void _StartPeerTimer()
    {
        _peerTimer = new Timer(async _ =>
        {
            if (State != LinkState.Connected) return;

            var peers = await _core.GetPeersAsync().ConfigureAwait(false);
            _lastPeers = peers;
            OnPeersUpdated?.Invoke(peers);
            _RefreshStatus(peers);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    /// <summary>强制刷新一次状态（界面可手动调用）。</summary>
    public async Task RefreshStatusAsync()
    {
        if (State != LinkState.Connected) return;
        var peers = await _core.GetPeersAsync().ConfigureAwait(false);
        _lastPeers = peers;
        OnPeersUpdated?.Invoke(peers);
        _RefreshStatus(peers);
    }

    private void _RefreshStatus(List<PeerInfo> peers)
    {
        var host = peers.FirstOrDefault(p => p.IsHost);
        var players = peers.Select(p => p.DisplayName).Where(n => n.Length > 0).ToList();

        // 延迟：优先房主节点的 ping
        var ping = 0.0;
        if (host is not null) ping = _ParsePing(host.Ping);
        else if (peers.Count > 0) ping = _ParsePing(peers[0].Ping);

        // 中继状态：根据 cost 字段判断 P2P / relay / local
        var relayState = host is not null
            ? _ClassifyCost(host)
            : RelayStatus.Unknown;

        var localPeer = peers.FirstOrDefault(p => _ClassifyCost(p) == RelayStatus.Local);

        var relayNode = LauncherConfig.Current.EasyTierRelay;

        var status = new LinkStatus
        {
            State = State,
            IsHost = IsHost,
            RoomCode = CurrentRoomCode,
            VirtualIp = IsHost ? RoomCode.HostIp : host?.Ipv4,
            LocalVirtualIp = localPeer?.Ipv4,
            Relay = relayNode,
            RelayState = relayState,
            Ping = ping,
            OnlineCount = players.Count == 0 ? (IsHost ? 1 : 0) : players.Count,
            LocalPort = JoinerLocalPort,
            Players = players
        };

        Status = status;
        try { OnStatusUpdated?.Invoke(status); } catch { }
    }

    private static RelayStatus _ClassifyCost(PeerInfo peer)
    {
        var cost = peer.Cost ?? string.Empty;
        if (cost.Contains("local", StringComparison.OrdinalIgnoreCase)) return RelayStatus.Local;
        if (cost.Contains("relay", StringComparison.OrdinalIgnoreCase)) return RelayStatus.Relay;
        if (cost.Contains("p2p", StringComparison.OrdinalIgnoreCase)) return RelayStatus.Direct;
        return RelayStatus.Unknown;
    }

    private static double _ParsePing(string? ping) =>
        double.TryParse(ping?.Replace("%", string.Empty), out var value) ? value : 0;

    #endregion

    #region 离开 / 清理

    public async Task LeaveAsync()
    {
        _SetState(LinkState.Leaving, "正在离开房间");
        CleanupInternal();
        await Task.Delay(200).ConfigureAwait(false);
        _SetState(LinkState.Idle, "已离开房间");
    }

    private void CleanupInternal()
    {
        _peerTimer?.Dispose();
        _peerTimer = null;
        _broadcaster?.Stop();
        _broadcaster = null;
        _forwarder?.Stop();
        _forwarder = null;
        _core.Stop();
        _lastPeers = [];
        JoinerLocalPort = null;
        CurrentRoomCode = null;
        IsHost = false;
        Status = new LinkStatus { State = LinkState.Idle };
    }

    private void _SetState(LinkState state, string? message = null, Exception? ex = null)
    {
        State = state;
        Logger.Info($"[联机] {state}: {message ?? string.Empty}");

        // 有异常时统一翻译成中文
        var friendly = ex is null ? message : FriendlyError.ToText(ex);
        if (ex is not null && message is not null && friendly is not null && !friendly.Contains(message))
            friendly = message + "\n" + friendly;

        var args = new LinkEventArgs
        {
            State = state,
            Message = message,
            Exception = ex,
            FriendlyMessage = friendly
        };

        Status = Status with { State = state };
        try { OnStateChanged?.Invoke(args); } catch { }
        try { OnStatusUpdated?.Invoke(Status); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupInternal();
        _scanner?.Dispose();
        _core.Dispose();
        await Task.CompletedTask;
    }

    #endregion
}

/// <summary>简单 TCP 端口转发（加入方本地直连使用）。</summary>
public sealed class TcpForwarder : IDisposable
{
    private readonly int _localPort;
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public TcpForwarder(int localPort, string remoteHost, int remotePort)
    {
        _localPort = localPort;
        _remoteHost = remoteHost;
        _remotePort = remotePort;
    }

    public void Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _localPort);
            _listener.Start();
            _ = Task.Run(() => _AcceptLoopAsync(_cts.Token));
            Logger.Info($"本地转发 {_localPort} -> {_remoteHost}:{_remotePort}");
        }
        catch (SocketException ex)
        {
            // 端口占用等异常转成中文，不向上抛
            Logger.Warn($"本地端口转发启动失败：{FriendlyError.ToText(ex)}");
            _listener = null;
        }
    }

    private async Task _AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => _PipeAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.Warn($"转发接受连接失败: {ex.Message}"); }
        }
    }

    private async Task _PipeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var remote = new TcpClient();
            await remote.ConnectAsync(_remoteHost, _remotePort, ct).ConfigureAwait(false);

            using var _ = client;
            client.NoDelay = true;
            remote.NoDelay = true;

            await using var a = client.GetStream();
            await using var b = remote.GetStream();

            var t1 = _CopyAsync(a, b, ct);
            var t2 = _CopyAsync(b, a, ct);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Debug($"转发连接失败: {ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static async Task _CopyAsync(Stream src, Stream dst, CancellationToken ct)
    {
        try { await src.CopyToAsync(dst, ct).ConfigureAwait(false); } catch { }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
