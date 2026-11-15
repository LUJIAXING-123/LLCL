using System.Security.Cryptography;
using System.Text;

namespace LLCL.Core.Link;

/// <summary>
/// 房间码：一串短码即可加入房间，同时携带网络名、密钥与游戏端口。
/// <para>
/// 格式为 5 组 5 字符 + 1 组校验位，例如 <c>ABCDE-FGHIJ-KLMNO-PQRST-UVWXY-Z1</c>。
/// 字符集使用 32 进制并剔除易混淆的 0/O/1/I，便于口头报码与手工输入。
/// </para>
/// <para>数据段 25 字符 = 125 bit，分配如下：</para>
/// <list type="bullet">
///   <item>前 12 字符（60 bit）→ 网络名随机部分</item>
///   <item>中间 8 字符（40 bit，取 32 bit）→ 网络密钥</item>
///   <item>最后 5 字符（25 bit，取 16 bit）→ 游戏端口</item>
///   <item>剩余位保留给未来扩展</item>
/// </list>
/// </summary>
public sealed record RoomCode(string FullCode, string NetworkName, string NetworkSecret, int Port)
{
    /// <summary>主机在虚拟网络中的固定 IPv4。</summary>
    public const string HostIp = "10.114.51.41";

    /// <summary>
    /// 为加入方派生一个稳定的虚拟 IPv4。
    /// <para>
    /// 无 TUN 模式下，端口转发是「本节点主动连向虚拟网内的目标」，
    /// 数据面必须知道本节点自己的虚拟地址，否则握手会被对端重置。
    /// 若完全不给 <c>--ipv4</c>，EasyTier 会把这个节点当成纯转发节点，
    /// 日志里表现为 port-forward 规则添加成功、本地端口也在监听，
    /// 但真正连接时立刻断开。
    /// </para>
    /// <para>
    /// 地址按 machine.id 派生，同一台机器每次启动都得到同一个地址，
    /// 重启后房间内成员不会因为地址变化而掉线。
    /// 取值避开房主的 .41，范围 42–254。
    /// </para>
    /// </summary>
    public static string JoinerIp(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) machineId = "llcl";

        // FNV-1a：稳定、无依赖，跨进程一致
        var hash = 2166136261u;
        foreach (var c in machineId)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        // 213 个可用地址（42..254），与房主的 41 错开
        var lastOctet = 42 + (int)(hash % 213);
        return $"10.114.51.{lastOctet}";
    }

    /// <summary>默认的被联机世界端口。</summary>
    public const int DefaultPort = 25565;

    private const string Chars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // 32 字符
    private const int BaseVal = 32;

    private const int NameChars = 12;
    private const int SecretChars = 8;
    private const int PortChars = 5;
    private const int PayloadLength = NameChars + SecretChars + PortChars; // 25
    private const int GroupSize = 5;
    private const int ChecksumChars = 2;

    /// <summary>
    /// 可接受的最短输入长度。
    /// <para>
    /// 与完整长度一致：房间码总会生成校验位，因此不存在“合法的 25 位码”。
    /// 允许省略校验位时，任何一个字符输错都不会被本地发现，
    /// 只能等连接超时后才报“未能连接”，排错体验很差；
    /// 强制校验可以立刻指出输错。
    /// </para>
    /// </summary>
    public const int MinInputLength = PayloadLength + ChecksumChars;

    /// <summary>标准输入长度（含校验位）。</summary>
    public const int FullInputLength = PayloadLength + ChecksumChars;

    /// <summary>合法性判断用：房间码是否包含端口信息。</summary>
    public bool HasPort => Port is > 0 and <= 65535;

    // ---------------- 生成 ----------------

    /// <summary>生成新的房间码，并编码游戏端口。</summary>
    public static RoomCode Generate(int port)
    {
        if (port is <= 0 or > 65535)
            port = DefaultPort;

        var nameBits = _RandomBits(60);

        // 密钥段占 8 个字符 = 40 bit 空间，直接用满 40 bit 随机数。
        // 早先取 32 bit 再左移 9 位对齐，最高位会超出 40 bit 被截断，
        // 实际只剩 31 bit 有效熵，且低位恒为 0，新码的末尾字符永远是同一个。
        var secretBits = _RandomBits(40);

        var payload = string.Concat(
            _Encode(nameBits, NameChars),
            _Encode(secretBits, SecretChars),
            _Encode((ulong)port << 9, PortChars));   // 25 bit 空间，低 9 位留空

        return _Build(payload, port);
    }

    // ---------------- 解析 ----------------

    /// <summary>
    /// 解析用户输入的房间码。失败时返回 null，并通过 <paramref name="error"/> 给出中文原因。
    /// 校验位可省略，省略时不做完整性校验。
    /// </summary>
    public static RoomCode? Parse(string? input, out string? error, int? portOverride = null)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "房间码不能为空，请向房主要一份完整的房间码。";
            return null;
        }

        var cleaned = new StringBuilder(input.Length);

        // 只在使用者输入了「确实不属于字符集的字符」时才报错。
        // 注意字符集本身含 L，因此 L 不能被纠正成 1；
        // 0 与 1 也都不在字符集内，输入后同样应报错而非静默替换。
        foreach (var raw in input.Trim().ToUpperInvariant())
        {
            if (raw is '-' or ' ' or '_' or '\t') continue;

            if (Chars.IndexOf(raw) < 0)
            {
                // 给出更贴近原因的提示：0/O、1/I 是常见的手误
                error = raw switch
                {
                    '0' or 'O' => "房间码不含数字 0 与字母 O，请核对是否看错了字符。",
                    '1' or 'I' => "房间码不含数字 1 与字母 I，请核对是否看错了字符。",
                    _ => $"房间码包含无效字符「{raw}」。房间码只使用大写字母和数字 2-9。"
                };
                return null;
            }

            cleaned.Append(raw);
        }

        if (cleaned.Length < MinInputLength)
        {
            error = cleaned.Length == PayloadLength
                ? $"房间码少了一段校验位（当前 {cleaned.Length} 位，完整应为 {FullInputLength} 位），" +
                  "请向房主索取完整的房间码。"
                : $"房间码长度不足（当前 {cleaned.Length} 位，应为 {FullInputLength} 位），可能少输入了一段。";
            return null;
        }

        if (cleaned.Length > FullInputLength)
        {
            error = $"房间码过长（当前 {cleaned.Length} 位，最多 {FullInputLength} 位），可能多输入了内容。";
            return null;
        }

        var payload = cleaned.ToString(0, PayloadLength);

        // 校验位现在必定存在，任何一位输错都会在这里被拦下，
        // 不必等到连接超时才发现。
        var expected = _Checksum(payload);
        var actual = cleaned.ToString(PayloadLength, ChecksumChars);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            error = "房间码校验失败，可能存在个别字符输错，请核对后重试。";
            return null;
        }

        var encodedPort = (int)(_Decode(payload.AsSpan(NameChars + SecretChars, PortChars)) >> 9);

        var port = portOverride is > 0 and <= 65535
            ? portOverride.Value
            : encodedPort is > 0 and <= 65535 ? encodedPort : DefaultPort;

        return _Build(payload, port);
    }

    /// <summary>把任意输入规范化为标准展示形式（含校验位）。</summary>
    public static string? Normalize(string? input)
    {
        var parsed = Parse(input, out _);
        return parsed?.FullCode;
    }

    /// <summary>是否为合法的完整房间码。</summary>
    public static bool IsValid(string? input) => Parse(input, out _) is not null;

    private static RoomCode _Build(string payload, int port)
        => new(_Format(payload), NetworkNameOf(payload), SecretOf(payload), port);

    private static string NetworkNameOf(string payload)
    {
        var prefix = LauncherConfig.Current.LobbyNetworkPrefix;
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "LLCLRoom";
        return $"{prefix}-{payload[..NameChars]}";
    }

    private static string SecretOf(string payload)
        => payload.Substring(NameChars, SecretChars);

    // ---------------- 编码工具 ----------------

    private static string _Format(string payload)
    {
        var sb = new StringBuilder(FullInputLength + 6);
        for (var i = 0; i < PayloadLength; i += GroupSize)
        {
            if (sb.Length > 0) sb.Append('-');
            sb.Append(payload, i, Math.Min(GroupSize, PayloadLength - i));
        }

        sb.Append('-').Append(_Checksum(payload));
        return sb.ToString();
    }

    private static string _Encode(ulong value, int chars)
    {
        var sb = new StringBuilder(chars);
        for (var i = 0; i < chars; i++)
        {
            sb.Append(Chars[(int)(value % BaseVal)]);
            value /= BaseVal;
        }

        // 上面的循环是低位在前，反转以让高位在前、便于阅读
        var arr = sb.ToString().ToCharArray();
        Array.Reverse(arr);
        return new string(arr);
    }

    private static ulong _Decode(ReadOnlySpan<char> text)
    {
        var value = 0UL;
        foreach (var ch in text)
        {
            var digit = Chars.IndexOf(ch);
            if (digit < 0) digit = 0;
            value = value * BaseVal + (ulong)digit;
        }

        return value;
    }

    private static ulong _RandomBits(int bits)
    {
        var bytes = RandomNumberGenerator.GetBytes((bits + 7) / 8);
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        var mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return value & mask;
    }

    /// <summary>2 字符校验位，用于发现个别字符输错。</summary>
    private static string _Checksum(string payload)
    {
        var sum = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            var digit = Chars.IndexOf(payload[i]);
            if (digit < 0) digit = 0;
            sum = (sum * 31 + digit) & 0xFFFF;
        }

        var check = sum % (BaseVal * BaseVal);
        return string.Concat(Chars[check / BaseVal], Chars[check % BaseVal]);
    }

    public override string ToString() => FullCode;
}
