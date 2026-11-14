using System.Text.Json.Nodes;
using LLCL.Core.Net;

namespace LLCL.Core.Account;

/// <summary>
/// 按账号 UUID 获取 Minecraft 皮肤贴图，供首页头像使用。
/// <para>
/// 参考 PCL-CE 的做法：先用 UUID 向 sessionserver 查询皮肤元数据，
/// 从中取出 SKIN 贴图地址，再下载 64×32 的皮肤图。
/// 至于「只显示头部」这件事交给 UI 层用裁剪+位移完成，
/// 这样不必在 Core 里引入图像处理依赖。
/// </para>
/// </summary>
public static class MinecraftSkin
{
    private const string MojangProfileUrl = "https://sessionserver.mojang.com/session/minecraft/profile/";

    /// <summary>皮肤字节缓存，避免每次切回首页都重新下载。</summary>
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取得指定 UUID 的皮肤贴图 PNG。
    /// 第三方皮肤站需要传入其 Yggdrasil 根地址。失败返回 null。
    /// </summary>
    public static async Task<byte[]?> GetSkinAsync(string uuid, string? yggdrasilBase = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;

        var key = string.IsNullOrWhiteSpace(yggdrasilBase) ? uuid : $"{yggdrasilBase}|{uuid}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var skinUrl = await GetSkinUrlAsync(uuid, yggdrasilBase, ct).ConfigureAwait(false);
            if (skinUrl is null)
            {
                Cache[key] = null;
                return null;
            }

            var bytes = await Http.GetBytesAsync(skinUrl, 2, ct).ConfigureAwait(false);
            Cache[key] = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            Logger.Warn($"获取皮肤失败 {uuid}: {ex.Message}");
            Cache[key] = null;
            return null;
        }
    }

    /// <summary>清空缓存，切换皮肤后可调用。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// 按 UUID 挑选默认皮肤是 Steve 还是 Alex，规则与 PCL-CE 一致：
    /// 取 UUID 中第 8、16、24、32 个字符的十六进制值异或，
    /// 结果为奇数用 Alex，偶数用 Steve；UUID 长度不为 32 时一律用 Steve。
    /// <para>
    /// 离线账号没有真实皮肤，用这个规则可保证同一用户名始终对应同一个形象。
    /// </para>
    /// </summary>
    public static bool IsAlex(string? uuid)
    {
        if (string.IsNullOrEmpty(uuid) || uuid.Length != 32) return false;

        var sum = 0;
        foreach (var index in new[] { 7, 15, 23, 31 })
        {
            var c = uuid[index];
            var value = c is >= '0' and <= '9' ? c - '0'
                : c is >= 'a' and <= 'f' ? c - 'a' + 10
                : c is >= 'A' and <= 'F' ? c - 'A' + 10
                : 0;

            sum ^= value;
        }

        return sum % 2 != 0;
    }

    private static async Task<string?> GetSkinUrlAsync(string uuid, string? yggdrasilBase,
        CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(yggdrasilBase)
            ? MojangProfileUrl + uuid
            : $"{yggdrasilBase.TrimEnd('/')}/sessionserver/session/minecraft/profile/{uuid}";

        var json = await Http.GetStringAsync(url, 2, ct).ConfigureAwait(false);
        if (JsonNode.Parse(json) is not JsonObject obj) return null;

        if (obj["properties"] is not JsonArray props) return null;

        foreach (var prop in props)
        {
            if (prop?["name"]?.GetValue<string>() != "textures") continue;

            var value = prop["value"]?.GetValue<string>();
            if (string.IsNullOrEmpty(value)) continue;

            // value 是 Base64 编码的 JSON，里面才有贴图地址
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
            if (JsonNode.Parse(decoded) is not JsonObject tex) return null;

            return tex["textures"]?["SKIN"]?["url"]?.GetValue<string>();
        }

        return null;
    }
}
