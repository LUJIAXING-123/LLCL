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
            _client = new UdpClient();
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.MulticastTimeToLive, 4);
            _client.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"创建局域网广播失败: {ex.Message}");
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
            var payload = Encoding.UTF8.GetBytes($"[MOTD]@|{_motd}|@[/MOTD][AD]{_worldPort}[/AD]");
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
