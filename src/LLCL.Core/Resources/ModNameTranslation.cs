using System.IO.Compression;
using System.Text;

namespace LLCL.Core.Resources;

/// <summary>
/// 模组中文名查询。
///
/// <para>
/// 数据文件来自 PCL 内置的 <c>mcmod.buf</c>：gzip 压缩的 protobuf，
/// 每条记录形如 <c>WikiId / 中文名 / CurseForge slug / Modrinth slug</c>，
/// 原始数据整理自 mcmod.cn。在线接口只提供简介、不提供名称，
/// 所以中文名只能靠这份离线数据。
/// </para>
///
/// <para>
/// <b>版权说明</b>：该数据文件随 PCL 一同分发，此处仅为本项目复刻 PCL 体验而沿用。
/// 数据来源为 mcmod.cn 及其贡献者。若权利方认为不妥，请联系我们，会立即移除。
/// </para>
/// </summary>
public static class ModNameTranslation
{
    private const string ResourceName = "LLCL.Core.mcmod.buf";

    /// <summary>slug（两种来源混用）→ 中文名。</summary>
    private static Dictionary<string, string>? _map;

    private static readonly object Gate = new();
    private static bool _attempted;

    /// <summary>
    /// 按 slug 取中文名。未收录或数据不可用时返回 null。
    /// 首次调用会加载整份数据（约 1.4 MB），之后走内存字典。
    /// </summary>
    public static string? TryGetChineseName(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;

        EnsureLoaded();

        var map = _map;
        if (map is null) return null;

        return map.TryGetValue(slug.Trim(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
    }

    /// <summary>加载并解析数据文件；失败只记日志，不影响其它功能。</summary>
    private static void EnsureLoaded()
    {
        if (_attempted) return;

        lock (Gate)
        {
            if (_attempted) return;
            _attempted = true;

            try
            {
                var assembly = typeof(ModNameTranslation).Assembly;
                using var raw = assembly.GetManifestResourceStream(ResourceName);
                if (raw is null)
                {
                    Logger.Warn("未找到内置的模组中文名数据，名称将保持原文");
                    return;
                }

                using var gzip = new GZipStream(raw, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                gzip.CopyTo(buffer);

                var map = ParseProtobuf(buffer.ToArray());
                _map = map;

                Logger.Info($"模组中文名数据已加载：{map.Count} 条");
            }
            catch (Exception ex)
            {
                Logger.Warn($"加载模组中文名数据失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 解析数据。外层是重复的 field 1（每条一个子消息），
    /// 子消息内依次是 WikiId(varint)、中文名(string)、CF slug(string)、MR slug(string)。
    /// 手写解析是为了避免为一份只读数据引入 protobuf 依赖。
    /// </summary>
    private static Dictionary<string, string> ParseProtobuf(byte[] data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < data.Length)
        {
            var tag = ReadVarint(data, ref index);
            if (tag < 0) break;

            var field = (int)(tag >> 3);
            var wire = (int)(tag & 0x7);

            // 只关心 field 1 的 length-delimited（每一条记录）
            if (field != 1 || wire != 2) break;

            var length = ReadVarint(data, ref index);
            if (length < 0 || index + length > data.Length) break;

            ParseEntry(data, index, length, map);
            index += length;
        }

        return map;
    }

    private static void ParseEntry(byte[] data, int offset, int length, Dictionary<string, string> map)
    {
        var end = offset + length;
        var index = offset;

        string? chineseName = null;
        string? curseForgeSlug = null;
        string? modrinthSlug = null;

        while (index < end)
        {
            var tag = ReadVarint(data, ref index);
            if (tag < 0) return;

            var field = (int)(tag >> 3);
            var wire = (int)(tag & 0x7);

            if (wire == 0)
            {
                ReadVarint(data, ref index); // WikiId，这里用不到
                continue;
            }

            if (wire != 2) return;

            var size = ReadVarint(data, ref index);
            if (size < 0 || index + size > end) return;

            var text = size == 0 ? string.Empty : Encoding.UTF8.GetString(data, index, size);
            index += size;

            switch (field)
            {
                case 2: chineseName = text; break;
                case 3: curseForgeSlug = text; break;
                case 4: modrinthSlug = text; break;
            }
        }

        if (string.IsNullOrWhiteSpace(chineseName)) return;

        if (!string.IsNullOrWhiteSpace(curseForgeSlug)) map[curseForgeSlug] = chineseName;
        if (!string.IsNullOrWhiteSpace(modrinthSlug)) map[modrinthSlug] = chineseName;
    }

    /// <summary>读 protobuf varint。越界返回 -1。</summary>
    private static int ReadVarint(byte[] data, ref int index)
    {
        var result = 0;
        var shift = 0;

        while (index < data.Length)
        {
            var b = data[index++];
            result |= (b & 0x7F) << shift;

            if ((b & 0x80) == 0) return result;

            shift += 7;
            if (shift > 28) return -1;
        }

        return -1;
    }
}
