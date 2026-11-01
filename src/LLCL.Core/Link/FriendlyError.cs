using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LLCL.Core.Link;

/// <summary>
/// 把底层原始异常翻译成用户能看懂的中文提示。
/// 目标是：界面上永远不出现 "SocketException" 之类原生英文报错。
/// </summary>
public static class FriendlyError
{
    /// <summary>用户可读的错误提示。</summary>
    public sealed record Message(string Text, string? Suggestion = null, ErrorKind Kind = ErrorKind.Unknown);

    public enum ErrorKind
    {
        Unknown,
        PortInUse,
        NetworkUnreachable,
        Timeout,
        PermissionDenied,
        RoomNotFound,
        InvalidCode,
        EasyTierMissing,
        RelayUnavailable,
        MirrorUnavailable
    }

    /// <summary>把任意异常转换为中文提示。</summary>
    public static Message Translate(Exception? ex)
    {
        if (ex is null) return new Message("发生了未知错误，请稍后重试。");

        // 展开 AggregateException
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            return Translate(agg.InnerExceptions[0]);

        // 先看链上的 SocketException
        var socket = _Find<SocketException>(ex);
        if (socket is not null)
            return _FromSocket(socket);

        if (ex is TimeoutException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            return new Message(
                "网络请求超时了。",
                "请检查网络连接是否正常，或稍后重试；若使用代理/加速器，可尝试关闭后再试。",
                ErrorKind.Timeout);
        }

        if (ex is UnauthorizedAccessException)
        {
            return new Message(
                "没有足够的权限执行该操作。",
                "请尝试以管理员身份运行，或检查文件夹是否被其他程序占用。",
                ErrorKind.PermissionDenied);
        }

        if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return new Message(
                "找不到需要的文件或目录。",
                ex.Message,
                ErrorKind.EasyTierMissing);
        }

        // 安装器退出码 / 哈希校验等
        if (ex is InvalidDataException)
        {
            return new Message(
                "文件内容校验失败，可能已损坏。",
                "请重新下载该文件；若多次失败，可在设置中更换下载源后重试。",
                ErrorKind.Unknown);
        }

        if (ex is IOException io)
        {
            if (_IsPortRelated(io.Message))
                return _PortInUse();

            return new Message(
                "文件读写失败。",
                "请确认目标文件未被其他程序占用，并检查磁盘剩余空间。",
                ErrorKind.Unknown);
        }

        // 代码里主动抛出的、已经中文化的消息，原样展示
        if (_LooksChinese(ex.Message))
            return new Message(ex.Message);

        return new Message(
            "操作未能完成。",
            "可查看日志了解更多细节（设置 - 启动器 - 打开日志目录）。",
            ErrorKind.Unknown);
    }

    /// <summary>把异常转换成一句话（含建议），用于 MessageBox / InfoBar。</summary>
    public static string ToText(Exception? ex)
    {
        var message = Translate(ex);

        // 没有找到更有价值的上下文时，避免重复展示
        if (message.Suggestion is null) return message.Text;
        return $"{message.Text}\n{message.Suggestion}";
    }

    /// <summary>聊特定端口是否可用。</summary>
    public static bool IsPortAvailable(int port, out string? error)
    {
        error = null;

        if (port is <= 0 or > 65535)
        {
            error = "端口号必须在 1 到 65535 之间。";
            return false;
        }

        if (port is < 1024)
        {
            error ??= null; // 低于 1024 的端口可能被系统占用，允许但提示
        }

        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex)
        {
            error = ex.SocketErrorCode switch
            {
                global::System.Net.Sockets.SocketError.AddressAlreadyInUse =>
                    $"端口 {port} 已被其他程序占用，请换一个端口（例如 {SuggestPort()}）。",
                global::System.Net.Sockets.SocketError.AccessDenied =>
                    $"端口 {port} 需要管理员权限才能使用，建议改用 1024 以上的端口。",
                _ => $"端口 {port} 不可用（{ex.SocketErrorCode}），请换一个端口。"
            };
            return false;
        }
        catch (Exception ex)
        {
            error = $"无法检测端口 {port}：{ex.Message}";
            return false;
        }
    }

    /// <summary>找一个空闲的本地端口。</summary>
    public static int SuggestPort(int start = 25565)
    {
        for (var offset = 0; offset < 200; offset++)
        {
            var candidate = start + offset;
            if (candidate > 65535) break;
            if (IsPortAvailable(candidate, out _)) return candidate;
        }

        return 0;
    }

    private static T? _Find<T>(Exception? ex) where T : Exception
    {
        var current = ex;
        while (current is not null)
        {
            if (current is T match) return match;
            current = current.InnerException;
        }

        return null;
    }

    private static Message _FromSocket(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            global::System.Net.Sockets.SocketError.AddressAlreadyInUse => _PortInUse(),
            global::System.Net.Sockets.SocketError.AddressNotAvailable => new Message(
                "指定的地址不可用。",
                "虚拟网卡可能没有成功建立，请先确保 EasyTier 内核正常工作后再试。",
                ErrorKind.NetworkUnreachable),
            global::System.Net.Sockets.SocketError.AccessDenied => new Message(
                "端口被系统拒绝访问。",
                "请改用 1024 以上的端口，或以管理员身份运行。",
                ErrorKind.PermissionDenied),
            global::System.Net.Sockets.SocketError.TimedOut => new Message(
                "网络连接超时。",
                "对方可能已离线，或防火墙拦截了连接。请确认双方都允许 LLCL 通过防火墙。",
                ErrorKind.Timeout),
            global::System.Net.Sockets.SocketError.NetworkUnreachable or global::System.Net.Sockets.SocketError.HostUnreachable => new Message(
                "无法连接到对方网络。",
                "可能是中继节点不可达。请在设置中更换中继服务器，或检查本机网络。",
                ErrorKind.RelayUnavailable),
            global::System.Net.Sockets.SocketError.ConnectionRefused => new Message(
                "对方尚未准备好接收连接。",
                "房主可能还没有在 Minecraft 中开放世界，或游戏已经关闭。请让房主确认后重试。",
                ErrorKind.RoomNotFound),
            global::System.Net.Sockets.SocketError.ConnectionReset => new Message(
                "连接被对方中断。",
                "房主可能关闭了游戏或离开了房间，请重新加入。",
                ErrorKind.RoomNotFound),
            global::System.Net.Sockets.SocketError.HostDown => new Message(
                "对方主机关闭或已离线。", null, ErrorKind.RoomNotFound),
            _ => new Message(
                $"网络操作失败（错误码 {ex.SocketErrorCode}）。",
                "请检查网络连接，或稍后重试。",
                ErrorKind.Unknown)
        };
    }

    private static Message _PortInUse() => new(
        "端口被占用了。",
        $"另一个程序正在使用该端口。请换一个端口（例如 {SuggestPort()}），或关闭占用该端口的程序后再试。",
        ErrorKind.PortInUse);

    private static bool _IsPortRelated(string message)
        => message.Contains("port", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("端口", StringComparison.Ordinal);

    private static bool _LooksChinese(string text)
        => Regex.IsMatch(text, @"[\u4e00-\u9fa5]");

    /// <summary>把任意异常写日志并返回中文提示，供 UI 直接调用。</summary>
    public static string LogAndTranslate(Exception? ex, string context)
    {
        if (ex is not null) Logger.Error(ex, context);
        return ToText(ex);
    }
}




