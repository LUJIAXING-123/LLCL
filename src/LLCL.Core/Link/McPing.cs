using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LLCL.Core.Link;

/// <summary>Minecraft 服务器 Ping 结果。</summary>
public sealed record PingResult(string Description, string Version, int? Online, int? Max, string? IconBase64, long LatencyMs);

/// <summary>
/// Minecraft Server List Ping（现代协议 1.7+，含旧版回退）。
/// </summary>
public static class McPing
{
    public static async Task<PingResult?> PingAsync(string host, int port, TimeSpan timeout,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            client.NoDelay = true;

            var stream = client.GetStream();
            stream.ReadTimeout = (int)timeout.TotalMilliseconds;

            var start = Environment.TickCount64;

            // Handshake
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);                                   // packet id
            WriteVarInt(handshake, 765);                                    // protocol version (1.20.4)
            WriteString(handshake, host);
            WriteVarInt(handshake, port);
            WriteVarInt(handshake, 1);                                      // next state: status
            await _SendPacketAsync(stream, handshake.ToArray(), cts.Token).ConfigureAwait(false);

            // Status request
            await _SendPacketAsync(stream, [0x00], cts.Token).ConfigureAwait(false);

            // 读取响应
            var jsonText = await _ReadStringPacketAsync(stream, cts.Token).ConfigureAwait(false);
            if (jsonText is null) return null;

            var latency = Environment.TickCount64 - start;
            var node = JsonNode.Parse(jsonText);
            if (node is not JsonObject obj) return null;

            var description = _FlattenDescription(obj["description"]);
            var versionName = obj["version"]?["name"]?.GetValue<string>() ?? string.Empty;
            var online = obj["players"]?["online"]?.GetValue<int>();
            var max = obj["players"]?["max"]?.GetValue<int>();
            var icon = obj["favicon"]?.GetValue<string>();

            return new PingResult(description, versionName, online, max, icon, latency);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Logger.Debug($"Ping {host}:{port} 失败: {ex.Message}");
            return null;
        }
    }

    private static string _FlattenDescription(JsonNode? node)
    {
        if (node is null) return string.Empty;
        if (node is JsonValue)
        {
            try { return node.GetValue<string>(); } catch { return string.Empty; }
        }

        if (node is JsonObject obj)
        {
            var sb = new StringBuilder();
            if (obj["text"] is JsonValue tv)
            {
                try { sb.Append(tv.GetValue<string>()); } catch { }
            }

            if (obj["extra"] is JsonArray extra)
                foreach (var item in extra)
                    sb.Append(_FlattenDescription(item));

            return sb.ToString().Trim();
        }

        if (node is JsonArray arr)
            return string.Concat(arr.Select(_FlattenDescription));

        return string.Empty;
    }

    private static async Task _SendPacketAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        WriteVarInt(buffer, payload.Length);
        buffer.Write(payload);
        stream.Write(buffer.ToArray());
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> _ReadStringPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await _ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length <= 0 || length > 8 * 1024 * 1024) return null;

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read <= 0) return null;
            offset += read;
        }

        using var ms = new MemoryStream(buffer);
        var packetId = await _ReadVarIntAsync(ms, ct).ConfigureAwait(false);
        if (packetId != 0x00) return null;

        var jsonLength = await _ReadVarIntAsync(ms, ct).ConfigureAwait(false);
        if (jsonLength <= 0 || jsonLength > ms.Length) return null;

        var json = new byte[jsonLength];
        var jsonRead = 0;
        while (jsonRead < jsonLength)
        {
            var got = await ms.ReadAsync(json.AsMemory(jsonRead, jsonLength - jsonRead), ct).ConfigureAwait(false);
            if (got <= 0) return null;
            jsonRead += got;
        }

        return Encoding.UTF8.GetString(json);
    }

    private static async Task<int> _ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var value = 0;
        var position = 0;
        while (true)
        {
            var b = new byte[1];
            var read = await stream.ReadAsync(b, ct).ConfigureAwait(false);
            if (read <= 0) return -1;

            value |= (b[0] & 0x7F) << position;
            if ((b[0] & 0x80) == 0) break;

            position += 7;
            if (position >= 32) return -1;
        }

        return value;
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var buffer = new byte[5];
        var index = 0;
        do
        {
            var b = value & 0x7F;
            value >>= 7;
            if (value != 0) b |= 0x80;
            buffer[index++] = (byte)b;
        } while (value != 0);

        stream.Write(buffer, 0, index);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }
}

