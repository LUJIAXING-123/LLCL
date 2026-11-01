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

    /// <summary>可接受的最短输入长度（允许省略校验位）。</summary>
    public const int MinInputLength = PayloadLength;

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
        var secretBits = _RandomBits(32);

        var payload = string.Concat(
            _Encode(nameBits, NameChars),
            _Encode(secretBits << 9, SecretChars),   // 40 bit 空间，取高位对齐
            _Encode((ulong)port << 9, PortChars));   // 25 bit 空间

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
        foreach (var raw in input.Trim().ToUpperInvariant())
        {
            // 允许用户输入易混淆字符，自动纠正
            var ch = raw switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                _ => raw
            };

            if (ch is '-' or ' ' or '_' or '\t') continue;

            // 纠正后的 0/1 不在字符集内，提示用户
            if (ch is '0' or '1')
            {
                error = $"房间码中不能出现字符「{(raw is 'O' ? 'O' : raw is 'I' or 'L' ? 'I' : raw)}」，请检查是否输入了正确的内容。";
                return null;
            }

            if (Chars.IndexOf(ch) < 0)
            {
                error = $"房间码包含无效字符「{raw}」。房间码只使用大写字母和数字 2-9。";
                return null;
            }

            cleaned.Append(ch);
        }

        if (cleaned.Length < MinInputLength)
        {
            error = $"房间码长度不足（当前 {cleaned.Length} 位，至少需要 {MinInputLength} 位），可能少输入了一段。";
            return null;
        }

        if (cleaned.Length > FullInputLength)
        {
            error = $"房间码过长（当前 {cleaned.Length} 位，最多 {FullInputLength} 位），可能多输入了内容。";
            return null;
        }

        var payload = cleaned.ToString(0, PayloadLength);

        // 若用户提供了校验位，做一次完整性校验
        if (cleaned.Length == FullInputLength)
        {
            var expected = _Checksum(payload);
            var actual = cleaned.ToString(PayloadLength, ChecksumChars);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                error = "房间码校验失败，可能存在个别字符输错，请核对后重试。";
                return null;
            }
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
