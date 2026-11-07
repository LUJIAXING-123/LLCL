using System.Text.Json.Nodes;

namespace LLCL.Core.Account;

/// <summary>
/// 第三方账号登录：Yggdrasil 协议（LittleSkin、Blessing Skin 等）。
/// <para>
/// 这类皮肤站的验证服务兼容 authlib-injector，
/// 登录拿到 accessToken 后，启动时再通过 -javaagent 注入 authlib-injector，
/// 游戏就会用该站的验证与皮肤。
/// </para>
/// </summary>
public static class ThirdPartyAuth
{
    /// <summary>LittleSkin 的验证服务地址。</summary>
    public const string LittleSkinUrl = "https://littleskin.cn/api/yggdrasil";

    /// <summary>常见第三方验证服务预设。</summary>
    public static readonly (string Name, string Url)[] Presets =
    [
        ("LittleSkin", LittleSkinUrl),
        ("Blessing Skin（自建）", "")
    ];

    /// <summary>
    /// 密码登录。返回账号条目，其中 ServerUrl 用于后续注入 authlib-injector。
    /// </summary>
    public static async Task<AccountEntry> LoginAsync(string serverUrl, string email, string password,
        AuthProgress? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new AuthException("请填写验证服务器地址");

        var baseUrl = NormalizeServerUrl(serverUrl);

        progress?.Invoke("正在连接验证服务器…");

        // 先探测该站是否兼容 authlib-injector
        await _ProbeAsync(baseUrl, ct).ConfigureAwait(false);

        progress?.Invoke("正在验证账号密码…");

        var payload = new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = email.Trim(),
            ["password"] = password,
            ["requestUser"] = true
        };

        var resp = await _PostAsync(baseUrl + "/authserver/authenticate", payload, ct)
            .ConfigureAwait(false);

        if (resp is null)
            throw new AuthException("账号或密码错误");

        var accessToken = resp["accessToken"]?.GetValue<string>()
                          ?? throw new AuthException("验证服务器未返回访问令牌");

        var clientToken = resp["clientToken"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");

        var profile = resp["selectedProfile"];
        string name, uuid;

        if (profile is not null)
        {
            name = profile["name"]?.GetValue<string>() ?? string.Empty;
            uuid = profile["id"]?.GetValue<string>() ?? string.Empty;
        }
        else
        {
            // 未选择角色时从 availableProfiles 里取第一个
            var available = resp["availableProfiles"];
            if (available is not JsonArray profiles || profiles.Count == 0)
                throw new AuthException("该账号下没有已创建的角色，请先在皮肤站创建角色");

            name = profiles[0]?["name"]?.GetValue<string>() ?? string.Empty;
            uuid = profiles[0]?["id"]?.GetValue<string>() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uuid))
            throw new AuthException("验证服务器返回的角色信息不完整");

        return new AccountEntry
        {
            Kind = AccountKind.ThirdParty,
            Name = name,
            Uuid = uuid,
            AccessToken = accessToken,
            ClientToken = clientToken,
            ServerUrl = baseUrl,
            ExpiresAt = null // 第三方令牌通常长期有效，失效时由启动阶段报错
        };
    }

    /// <summary>用已有令牌刷新，失败返回 null。</summary>
    public static async Task<AccountEntry?> RefreshAsync(AccountEntry account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.ServerUrl) ||
            string.IsNullOrWhiteSpace(account.AccessToken) ||
            string.IsNullOrWhiteSpace(account.ClientToken))
            return null;

        try
        {
            var payload = new JsonObject
            {
                ["accessToken"] = account.AccessToken,
                ["clientToken"] = account.ClientToken,
                ["requestUser"] = true
            };

            var resp = await _PostAsync(account.ServerUrl + "/authserver/refresh", payload, ct)
                .ConfigureAwait(false);

            if (resp is null) return null;

            var token = resp["accessToken"]?.GetValue<string>() ?? account.AccessToken;
            var updated = account;
            updated.AccessToken = token;
            return updated;
        }
        catch (Exception ex)
        {
            Logger.Warn($"第三方账号刷新失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读取该账号在验证服务器上可用的角色列表。
    /// <para>
    /// Yggdrasil 标准的 authenticate 响应在 requestUser 为真时会带 availableProfiles，
    /// 因此优先用登录响应里的数据；此处作为未选择角色时的兜底。
    /// </para>
    /// </summary>
    public static Task<List<(string Name, string Uuid)>> FetchProfilesAsync(string serverUrl,
        string accessToken, CancellationToken ct = default)
    {
        // 该协议没有独立的角色列表接口，角色信息只能来自 authenticate/refresh 响应。
        // 调用方应从响应体的 availableProfiles 字段读取，这里返回空表示无额外数据。
        return Task.FromResult<List<(string Name, string Uuid)>>([]);
    }

    /// <summary>
    /// 探测验证服务器：Yggdrasil 的 /authserver 根路径应返回 API 元信息。
    /// </summary>
    private static async Task _ProbeAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            var text = await Net.Http.GetStringAsync(
                baseUrl.Replace("/authserver", "/api/yggdrasil", StringComparison.OrdinalIgnoreCase),
                1, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            var node = JsonNode.Parse(text);
            var skinDomains = node?["skinDomains"];
            if (skinDomains is not null)
                Logger.Info($"验证服务器 {baseUrl} 可用，皮肤域名 {skinDomains.ToJsonString()}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"验证服务器探测失败（继续尝试登录）: {ex.Message}");
        }
    }

    /// <summary>规范化服务器地址：补全到 /authserver 结尾。</summary>
    public static string NormalizeServerUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');

        // LittleSkin 常见写法：填 https://littleskin.cn 或 .../api/yggdrasil
        if (u.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
            return u.Replace("/api/yggdrasil", "/authserver", StringComparison.OrdinalIgnoreCase);

        if (u.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase)) return u;

        return u + "/authserver";
    }

    private static async Task<JsonNode?> _PostAsync(string url, JsonObject payload, CancellationToken ct)
    {
        try
        {
            using var resp = await Net.Http.Client.PostAsync(url,
                new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"验证服务器返回 {resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                return null;
            }

            return JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            Logger.Warn($"请求验证服务器失败 {url}: {ex.Message}");
            throw new AuthException("无法连接验证服务器，请检查地址是否正确", ex);
        }
    }
}
