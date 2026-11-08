using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LLCL.Core.Link;

/// <summary>
/// Minimal Minecraft Server List Ping (1.7+) over a local port.
/// <para>
/// 仅发送握手与状态请求，读取服务器返回的 JSON 状态。
/// 用途：确认转发端口后面确实是 Minecraft 服务端，
/// 而不是「端口能连通、但根本不是游戏」造成的假阳性。
/// </para>
/// </summary>
public static class MinecraftPing
{
    /// <summary>握手时声明的协议版本；状态查询下服务端不会因版本不符而拒绝。</summary>
    private const int StatusProtocol = 767;

    /// <summary>状态 JSON 的合理上限，防止异常服务端撑爆内存。</summary>
    private const int MaxPacketLength = 1 << 20;

    public static async Task<bool> PingAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(BuildHandshake(port), cts.Token).ConfigureAwait(false);

            // 状态请求：长度 1，包 ID 0x00
            await stream.WriteAsync(new byte[] { 0x01, 0x00 }, cts.Token).ConfigureAwait(false);

            var length = await ReadVarIntAsync(stream, cts.Token).ConfigureAwait(false);
            if (length <= 0 || length > MaxPacketLength) return false;

            var payload = new byte[length];
            await ReadExactlyAsync(stream, payload, cts.Token).ConfigureAwait(false);

            var index = 0;
            if (ReadVarInt(payload, ref index) != 0x00) return false;

            var json = ReadString(payload, ref index);
            return json.Contains("\"version\"", StringComparison.Ordinal)
                || json.Contains("\"description\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildHandshake(int port)
    {
        using var body = new MemoryStream();
        WriteVarInt(body, 0x00);                 // 包 ID：握手
        WriteVarInt(body, StatusProtocol);       // 协议版本
        WriteString(body, "127.0.0.1");          // 服务端地址（仅用于虚拟主机判断）
        body.WriteByte((byte)(port >> 8));
        body.WriteByte((byte)(port & 0xFF));
        WriteVarInt(body, 1);                    // 下一状态：1 = 状态查询

        using var frame = new MemoryStream();
        WriteVarInt(frame, (int)body.Length);
        var bodyBytes = body.ToArray();

        var result = new byte[frame.Length + bodyBytes.Length];
        frame.ToArray().CopyTo(result, 0);
        bodyBytes.CopyTo(result, (int)frame.Length);
        return result;
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (v != 0);
    }

    private static void WriteString(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];

        for (var i = 0; i < 5; i++)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();

            var b = buffer[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }

        throw new InvalidDataException("VarInt 过长");
    }

    private static int ReadVarInt(byte[] data, ref int index)
    {
        var result = 0;
        var shift = 0;

        for (var i = 0; i < 5; i++)
        {
            if (index >= data.Length) throw new EndOfStreamException();
            var b = data[index++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }

        throw new InvalidDataException("VarInt 过长");
    }

    private static string ReadString(byte[] data, ref int index)
    {
        var length = ReadVarInt(data, ref index);
        if (length < 0 || index + length > data.Length) throw new EndOfStreamException();

        var text = Encoding.UTF8.GetString(data, index, length);
        index += length;
        return text;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
