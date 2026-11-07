using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLCL.Core.Account;

/// <summary>
/// 微软账号登录：OAuth 2.0 设备码流程。
/// <para>
/// 相比授权码流程，设备码不需要在本地起回调端口，也不需要内嵌浏览器，
/// 用户只需在任意设备上打开链接输入验证码即可，兼容性最好。
/// </para>
/// <para>
/// 流程：设备码 → 轮询拿 Microsoft 令牌 → Xbox Live → XSTS → Minecraft 令牌 → 档案。
/// </para>
/// </summary>
public static class MicrosoftAuth
{
    /// <summary>Minecraft 启动器使用的公共客户端 ID（各启动器通用）。</summary>
    private const string ClientId = "00000000402b5328";

    private const string DeviceCodeUrl =
        "https://login.live.com/oauth20_connect.srf";

    private const string TokenUrl =
        "https://login.live.com/oauth20_token.srf";

    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>设备码授权会话。</summary>
    public sealed record DeviceCodeSession(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string VerificationUriComplete,
        int IntervalSeconds,
        DateTime ExpiresAt);

    /// <summary>
    /// 第一步：请求设备码，返回给用户看的验证链接与代码。
    /// </summary>
    public static async Task<DeviceCodeSession> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code"
        };

        using var resp = await Net.Http.Client.PostAsync(DeviceCodeUrl,
            new FormUrlEncodedContent(form), ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"请求登录验证码失败（HTTP {(int)resp.StatusCode}）");

        var node = JsonNode.Parse(body)
            ?? throw new AuthException("登录服务返回了无法解析的数据");

        var deviceCode = node["device_code"]?.GetValue<string>();
        var userCode = node["user_code"]?.GetValue<string>();
        var verifyUri = node["verification_uri"]?.GetValue<string>();
        var expireIn = node["expires_in"]?.GetValue<int>() ?? 900;
        var interval = node["interval"]?.GetValue<int>() ?? 5;

        if (deviceCode is null || userCode is null || verifyUri is null)
            throw new AuthException("登录服务返回的数据不完整");

        return new DeviceCodeSession(
            deviceCode,
            userCode,
            verifyUri,
            $"{verifyUri}?otc={userCode}",
            Math.Max(2, interval),
            DateTime.Now.AddSeconds(expireIn));
    }

    /// <summary>
    /// 第二步：轮询直到用户在浏览器完成授权。
    /// 用户取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async Task<AccountEntry> WaitForAuthorizationAsync(
        DeviceCodeSession session, AuthProgress? progress = null, CancellationToken ct = default)
    {
        while (DateTime.Now < session.ExpiresAt)
        {
            ct.ThrowIfCancellationRequested();

            var token = await _PollTokenAsync(session.DeviceCode, ct).ConfigureAwait(false);

            if (token is not null)
            {
                progress?.Invoke("已获取微软授权，正在验证 Xbox 账号…");
                return await _CompleteLoginAsync(token.AccessToken, token.RefreshToken, progress, ct)
                    .ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(session.IntervalSeconds), ct).ConfigureAwait(false);
        }

        throw new AuthException("验证码已过期，请重新开始登录");
    }

    /// <summary>
    /// 用刷新令牌免登录获取账号（启动时自动续期）。
    /// 失败返回 null，调用方应引导用户重新登录。
    /// </summary>
    public static async Task<AccountEntry?> RefreshAsync(AccountEntry account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            Logger.Warn($"账号 {account.Name} 缺少刷新令牌，无法续期");
            return null;
        }

        try
        {
            var token = await _RefreshTokenAsync(account.RefreshToken, ct).ConfigureAwait(false);
            if (token is null) return null;

            var updated = await _CompleteLoginAsync(token.AccessToken, token.RefreshToken,
                null, ct).ConfigureAwait(false);

            updated.Id = account.Id;
            return updated;
        }
        catch (Exception ex)
        {
            Logger.Warn($"微软账号续期失败: {ex.Message}");
            return null;
        }
    }

    // ---------------- 内部步骤 ----------------

    private sealed record TokenPair(string AccessToken, string? RefreshToken);

    /// <summary>轮询令牌端点。授权未完成返回 null，出错抛出异常。</summary>
    private static async Task<TokenPair?> _PollTokenAsync(string deviceCode, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode
        };

        using var resp = await Net.Http.Client.PostAsync(TokenUrl,
            new FormUrlEncodedContent(form), ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
        {
            var node = JsonNode.Parse(body);
            var access = node?["access_token"]?.GetValue<string>();
            var refresh = node?["refresh_token"]?.GetValue<string>();
            if (access is null) return null;
            return new TokenPair(access, refresh);
        }

        // 常见的错误：authorization_pending（还在等用户操作）、
        // authorization_declined（用户拒绝）、expired_token（过期）
        var error = _ReadError(body);

        if (error == "authorization_pending") return null;
        if (error == "slow_down")
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return null;
        }

        throw new AuthException(error switch
        {
            "authorization_declined" => "已在浏览器中拒绝授权",
            "expired_token" => "验证码已过期，请重新开始登录",
            "bad_verification_code" => "验证码无效，请重新开始登录",
            _ => $"微软登录失败：{error ?? "未知错误"}"
        });
    }

    private static async Task<TokenPair?> _RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        using var resp = await Net.Http.Client.PostAsync(TokenUrl,
            new FormUrlEncodedContent(form), ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(body);
        var access = node?["access_token"]?.GetValue<string>();
        var refresh = node?["refresh_token"]?.GetValue<string>() ?? refreshToken;
        if (access is null) return null;
        return new TokenPair(access, refresh);
    }

    private static string? _ReadError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["error"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>微软令牌 → XBL → XSTS → Minecraft 令牌 → 档案。</summary>
    private static async Task<AccountEntry> _CompleteLoginAsync(string microsoftToken,
        string? refreshToken, AuthProgress? progress, CancellationToken ct)
    {
        progress?.Invoke("正在登录 Xbox Live…");

        // 1. Xbox Live 用户令牌
        var xblPayload = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = microsoftToken
            },
            ["RpsTicket"] = microsoftToken,
            ["TokenType"] = "JWT"
        };

        var xbl = await _PostJsonAsync(XblUrl, xblPayload, ct).ConfigureAwait(false);
        var xblToken = xbl?["Token"]?.GetValue<string>()
                       ?? throw new AuthException("Xbox Live 验证失败（可能账号未开通 Xbox 服务）");

        var uhs = xbl?["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>()
                    ?? throw new AuthException("Xbox Live 返回数据异常");

        progress?.Invoke("正在验证 Xbox 安全令牌…");

        // 2. XSTS 令牌
        var xstsPayload = new JsonObject
        {
            ["Properties"] = new JsonObject { ["SandboxId"] = "RETAIL" },
            ["RpsTicket"] = xblToken,
            ["TokenType"] = "JWT"
        };

        var xsts = await _PostJsonAsync(XstsUrl, xstsPayload, ct).ConfigureAwait(false);
        var xstsToken = xsts?["Token"]?.GetValue<string>();

        if (xstsToken is null)
        {
            var xerr = xsts?["XErr"]?.GetValue<long>();
            throw new AuthException(xerr switch
            {
                2148916233L => "该微软账号没有 Xbox 档案，请先在 Xbox 官网创建档案",
                2148916235L => "该账号所在地区不支持 Xbox Live",
                2148916236L or 2148916237L => "该账号需要先完成成人验证",
                2148916238L => "该账号是未成年账号，需要家长同意后才能使用",
                _ => "Xbox 安全令牌验证失败"
            });
        }

        progress?.Invoke("正在获取 Minecraft 令牌…");

        // 3. Minecraft 访问令牌
        var mcPayload = new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}"
        };

        var mc = await _PostJsonAsync(McLoginUrl, mcPayload, ct).ConfigureAwait(false);
        var mcToken = mc?["access_token"]?.GetValue<string>()
                      ?? throw new AuthException("获取 Minecraft 令牌失败，请确认该账号已购买游戏");

        progress?.Invoke("正在读取游戏档案…");

        // 4. 档案（确认是否拥有游戏）
        var profile = await _GetJsonAsync(McProfileUrl, mcToken, ct).ConfigureAwait(false);

        if (profile is null)
            throw new AuthException("该微软账号没有购买 Minecraft，无法用于启动游戏");

        var name = profile["name"]?.GetValue<string>()
                   ?? throw new AuthException("无法读取游戏档案");
        var uuid = profile["id"]?.GetValue<string>()
                   ?? throw new AuthException("无法读取游戏档案");

        return new AccountEntry
        {
            Kind = AccountKind.Microsoft,
            Name = name,
            Uuid = uuid,
            AccessToken = mcToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.Now.AddHours(24)
        };
    }

    private static async Task<JsonNode?> _PostJsonAsync(string url, JsonObject payload,
        CancellationToken ct)
    {
        using var resp = await Net.Http.Client.PostAsync(url,
            new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            return JsonNode.Parse(body);
        }
        catch
        {
            Logger.Warn($"{url} 返回非 JSON：{body[..Math.Min(80, body.Length)]}");
            return null;
        }
    }

    private static async Task<JsonNode?> _GetJsonAsync(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await Net.Http.Client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try { return JsonNode.Parse(body); } catch { return null; }
    }
}

