namespace LLCL.Core.Account;

/// <summary>账号类型。</summary>
public enum AccountKind
{
    /// <summary>离线账号，UUID 由名字推导。</summary>
    Offline = 0,
    /// <summary>微软正版账号，OAuth 授权后获取 XBL / Minecraft 令牌。</summary>
    Microsoft = 1,
    /// <summary>第三方皮肤站（LittleSkin 等），走 authlib-injector / Yggdrasil 协议。</summary>
    ThirdParty = 2
}

/// <summary>
/// 一个可用的账号条目。持久化到配置文件，启动时按需刷新令牌。
/// </summary>
public sealed class AccountEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AccountKind Kind { get; set; } = AccountKind.Offline;

    /// <summary>显示名称（游戏内名字）。</summary>
    public string Name { get; set; } = "Player";

    /// <summary>无横线的 UUID。</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>访问令牌。离线账号固定为 "0"。</summary>
    public string AccessToken { get; set; } = "0";

    /// <summary>微软账号的刷新令牌，用于长期免登录。</summary>
    public string? RefreshToken { get; set; }

    /// <summary>第三方账号的验证服务器地址（authlib-injector 服务端）。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>第三方账号的客户端令牌，用于刷新。</summary>
    public string? ClientToken { get; set; }

    /// <summary>令牌过期时间，离线账号为 null。</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>是否已过有效期（预留 5 分钟余量）。</summary>
    public bool IsExpired =>
        ExpiresAt is not null && DateTime.Now >= ExpiresAt.Value.AddMinutes(-5);

    public string DisplayText => Kind switch
    {
        AccountKind.Microsoft => $"{Name}（微软）",
        AccountKind.ThirdParty => $"{Name}（{ThirdPartyHost()}）",
        _ => $"{Name}（离线）"
    };

    private string ThirdPartyHost()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl)) return "第三方";
        try { return new Uri(ServerUrl).Host; }
        catch { return "第三方"; }
    }
}

/// <summary>登录结果。</summary>
public sealed record AuthResult(bool Success, string? Error = null, AccountEntry? Account = null);

/// <summary>登录过程中的提示回调，用于界面显示"正在验证…"等文案。</summary>
public delegate void AuthProgress(string message);

/// <summary>账号服务出错时的异常，携带面向用户的说明。</summary>
public sealed class AuthException : Exception
{
    public AuthException(string message, Exception? inner = null) : base(message, inner) { }
}
