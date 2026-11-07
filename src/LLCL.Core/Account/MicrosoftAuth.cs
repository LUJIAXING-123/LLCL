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

    private const string Scope = "XboxLive.signin offline_access";

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
        // 注意：RpsTicket 必须是 "d=<令牌>" 形式，直接传裸令牌会被拒绝
        var xblPayload = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + microsoftToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        };

        var xbl = await _PostJsonAsync(XblUrl, xblPayload, ct).ConfigureAwait(false);
        var xblToken = xbl?["Token"]?.GetValue<string>()
                       ?? throw new AuthException("Xbox Live 验证失败（可能账号未开通 Xbox 服务）");

        var uhs = xbl?["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>()
                    ?? throw new AuthException("Xbox Live 返回数据异常");

        progress?.Invoke("正在验证 Xbox 安全令牌…");

        // 2. XSTS 令牌
        // 关键：必须使用 RelyingParty（骤准的 Minecraft 服务地址）而非 SandboxId，
        // 否则拿到的令牌无法用于 Minecraft 登录。
        var xstsPayload = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["UserTokens"] = new JsonArray(xblToken)
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
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

        var (mc, mcStatus) = await _PostJsonWithStatusAsync(McLoginUrl, mcPayload, ct)
            .ConfigureAwait(false);

        var mcToken = mc?["access_token"]?.GetValue<string>();

        if (mcToken is null)
        {
            Logger.Warn($"Minecraft 登录失败 HTTP {mcStatus}: {mc?.ToJsonString() ?? "空响应"}");

            // 401/403 通常意味着该账号没有 Minecraft 授权
            if (mcStatus is 401 or 403)
            {
                throw new AuthException(
                    "该微软账号没有购买 Minecraft。\n" +
                    "请确认登录的账号与购买游戏时使用的是同一个微软账号。");
            }

            throw new AuthException(
                $"获取 Minecraft 令牌失败（HTTP {mcStatus}），请检查网络后重试");
        }

        progress?.Invoke("正在确认游戏所有权…");

        // 4. 档案（确认是否拥有游戏）
        var profileResult = await _GetJsonWithStatusAsync(McProfileUrl, mcToken, ct)
            .ConfigureAwait(false);

        if (profileResult.Node is null)
        {
            // 404 表示该账号没有 Minecraft 档案，即未购买
            if (profileResult.Status == 404)
            {
                throw new AuthException(
                    "该微软账号没有购买 Minecraft。\n" +
                    "请确认登录的账号与购买游戏时使用的是同一个微软账号。");
            }

            if (profileResult.Status == 401)
                throw new AuthException("Minecraft 令牌验证失败，请重新登录");

            throw new AuthException(
                $"读取游戏档案失败（HTTP {profileResult.Status}），请检查网络后重试");
        }

        var profile = profileResult.Node;

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

    /// <summary>带状态码的 POST，便于区分认证失败与网络故障。</summary>
    private static async Task<(JsonNode? Node, int Status)> _PostJsonWithStatusAsync(string url,
        JsonObject payload, CancellationToken ct)
    {
        try
        {
            using var resp = await Net.Http.Client.PostAsync(url,
                new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct).ConfigureAwait(false);

            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (body.Length == 0)
            {
                Logger.Warn($"{url} 返回空响应（HTTP {status}）");
                return (null, status);
            }

            var node = JsonNode.Parse(body);

            if (!resp.IsSuccessStatusCode)
                Logger.Warn($"{url} 返回 HTTP {status}：{body[..Math.Min(250, body.Length)]}");

            return (node, status);
        }
        catch (Exception ex)
        {
            Logger.Warn($"请求 {url} 失败: {ex.Message}");
            return (null, 0);
        }
    }
    private static async Task<JsonNode?> _PostJsonAsync(string url, JsonObject payload,
        CancellationToken ct)
    {
        try
        {
            using var resp = await Net.Http.Client.PostAsync(url,
                new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                ct).ConfigureAwait(false);

            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (body.Length == 0)
            {
                Logger.Warn($"{url} 返回空响应（HTTP {status}）");
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"{url} 返回 HTTP {status}：{body[..Math.Min(250, body.Length)]}");
                return null;
            }

            return JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            Logger.Warn($"请求 {url} 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>带状态码的 GET 请求，用于区分「未购买」和「网络故障」。</summary>
    private static async Task<(JsonNode? Node, int Status)> _GetJsonWithStatusAsync(string url,
        string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await Net.Http.Client.SendAsync(req, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"{url} 返回 HTTP {status}");
                return (null, status);
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (JsonNode.Parse(body), status);
        }
        catch (Exception ex)
        {
            Logger.Warn($"请求 {url} 失败: {ex.Message}");
            return (null, 0);
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




