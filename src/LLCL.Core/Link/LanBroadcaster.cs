using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LLCL.Core.Link;

/// <summary>
/// 本地局域网广播器：创建房间后向局域网声明世界，使同一局域网内其他玩家可见。
/// 格式：<c>[MOTD]描述[/MOTD][AD]端口[/AD]</c>。
/// </summary>
public sealed class LanBroadcaster : IDisposable
{
    private static readonly IPAddress Destination = IPAddress.Parse("224.0.2.60");
    private const int Port = 4445;

    private UdpClient? _client;
    private Timer? _timer;
    private int _worldPort;
    private string _motd = "LLCL 联机房间";
    private bool _disposed;

    public void Start(string motd, int worldPort)
    {
        _motd = motd;
        _worldPort = worldPort;

        try
        {
            // 先绑定到随机端口，否则部分 SocketOption 在未绑定状态下会报
            // 「提供了一个无效的参数」(WSAEINVAL)。
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.EnableBroadcast = true;

            // 组播 TTL 默认是 1，只在本网段内有效。
            // 联机场景下接收方可能经虚拟网卡进来，TTL 太小会让包出不去。
            _client.Client.SetSocketOption(
                SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);

            // 注意：这里不要设置 SocketOptionName.MulticastInterface。
            // 实测（Windows 11、多网卡机器）手动绑定出口网卡后，
            // 发往 224.0.2.60 的包在接收端一个都收不到；
            // 保持不指定、由系统按路由表选择接口才是正确做法。
        }
        catch (Exception ex)
        {
            Logger.Warn($"创建局域网广播失败: {ex.Message}");
            _client = null;
            return;
        }

        _timer = new Timer(_ => Broadcast(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
        Logger.Info($"局域网广播已启动: {_motd} ({worldPort})");
    }

    private void Broadcast()
    {
        if (_client is null) return;
        try
        {
            // 标准局域网广播格式：[MOTD]名称[/MOTD][AD]端口[/AD]。
            // 不要在名称外再套 @|...|@ 之类的标记：游戏会把它们
            // 原样显示在多人游戏列表里，看起来像乱码。
            var payload = Encoding.UTF8.GetBytes($"[MOTD]{_motd}[/MOTD][AD]{_worldPort}[/AD]");
            _client.Send(payload, payload.Length, new IPEndPoint(Destination, Port));
        }
        catch (Exception ex)
        {
            Logger.Debug($"广播失败: {ex.Message}");
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
