using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LLCL.Core.Link;

/// <summary>
/// 本机局域网广播器：把指定端口以「局域网世界」的名义播给本机的 Minecraft 客户端。
/// <para>
/// 用途：加入房间后，游戏实际连的是本地的转发端口。
/// 但联机走 EasyTier 的端口转发模式（--no-tun），没有虚拟网卡，
/// Minecraft 的「多人游戏」只扫描真实网卡上的多播广播，因此看不到房间。
/// </para>
/// <para>
/// 这里的做法是直接向 <c>127.0.0.1:4445</c> 发送游戏自身的局域网发现报文，
/// 客户端收到后会在多人游戏列表中列出该端口，无需虚拟网卡，也不需要管理员权限。
/// </para>
/// </summary>
public sealed class LocalBroadcaster : IDisposable
{
    /// <summary>Minecraft 客户端监听局域网广播的端口。</summary>
    private const int DiscoveryPort = 4445;

    /// <summary>广播间隔，与游戏自身的刷新频率接近。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private string _motd = "LLCL 房间";
    private int _port;
    private bool _disposed;

    public void Start(string motd, int port)
    {
        if (_disposed || _cts is not null) return;

        _motd = string.IsNullOrWhiteSpace(motd) ? "LLCL 房间" : motd;
        _port = port;

        if (port is <= 0 or > 65535)
        {
            Logger.Warn($"本地广播端口无效: {port}");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _LoopAsync(_cts.Token));

        Logger.Info($"本地局域网广播已启动: {_motd} ({port})");
    }

    /// <summary>
    /// 更新广播内容。网络状态变化后（如人数变动）调用，无需重启。
    /// </summary>
    public void Update(string motd)
    {
        if (!string.IsNullOrWhiteSpace(motd)) _motd = motd;
    }

    private async Task _LoopAsync(CancellationToken ct)
    {
        try
        {
            // DualMode 让同一个 socket 同时支持 v4/v6 回环
            _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true
            };

            // 目标固定为本机回环，端点是游戏自己监听的公告端口
            var target = new IPEndPoint(IPAddress.Loopback, DiscoveryPort);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 报文格式与 Minecraft 自身的局域网公告一致：
                    // [MOTD]标题[/MOTD][AD]端口[/AD]
                    var payload = Encoding.UTF8.GetBytes($"[MOTD]{_motd}[/MOTD][AD]{_port}[/AD]");
                    await _socket.SendToAsync(
                        new ArraySegment<byte>(payload), SocketFlags.None, target, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"本地广播发送失败: {ex.Message}");
                }

                try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"本地广播任务终止: {ex.Message}");
        }
        finally
        {
            try { _socket?.Dispose(); } catch { }
            _socket = null;
        }
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
