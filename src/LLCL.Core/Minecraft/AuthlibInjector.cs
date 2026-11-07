using System.IO.Compression;

namespace LLCL.Core.Minecraft;

/// <summary>
/// authlib-injector 的下载与定位。
/// <para>
/// 第三方皮肤站（LittleSkin 等）依赖这个 Java 代理在运行时改写验证逻辑。
/// 首次使用某个版本时自动下载并缓存，之后直接复用本地文件。
/// </para>
/// </summary>
public static class AuthlibInjector
{
    /// <summary>
    /// 官方制品索引。返回 JSON，含最新版本的下载地址与校验值。
    /// 该接口实测可用（约 500ms），比 GitHub Releases 稳定。
    /// </summary>
    private const string ArtifactIndexUrl =
        "https://authlib-injector.yushi.moe/artifact/latest.json";

    /// <summary>GitHub 官方发布页（索引失效时回退）。</summary>
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";

    /// <summary>缓存的 jar 文件名。</summary>
    private const string JarFileName = "authlib-injector.jar";

    /// <summary>缓存目录。</summary>
    private static string CacheDir => Path.Combine(Paths.DataRoot, "Tools", "authlib-injector");

    private static string CachedJar => Path.Combine(CacheDir, JarFileName);

    /// <summary>
    /// 确保 authlib-injector 可用，返回 jar 的绝对路径；失败返回 null。
    /// 该方法为同步阻塞，仅应在后台线程调用（游戏启动前）。
    /// </summary>
    public static string? Ensure(string? authServerUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authServerUrl)) return null;

        // 已有缓存则直接复用
        if (File.Exists(CachedJar) && new FileInfo(CachedJar).Length > 100_000)
        {
            Logger.Debug("复用缓存的 authlib-injector");
            return CachedJar;
        }

        try
        {
            Directory.CreateDirectory(CacheDir);

            var downloaded = _DownloadAsync(ct).GetAwaiter().GetResult();
            if (downloaded is null) return null;

            Logger.Info($"authlib-injector 已就绪：{downloaded}");
            return downloaded;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "下载 authlib-injector 失败");
            return null;
        }
    }

    /// <summary>
    /// 异步准备 authlib-injector。必须用这个版本，
    /// 同步版本在 UI 线程调用会造成界面卡死（点启动无响应）。
    /// </summary>
    public static async Task<string?> EnsureAsync(string? authServerUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authServerUrl)) return null;

        if (File.Exists(CachedJar) && new FileInfo(CachedJar).Length > 100_000)
            return CachedJar;

        try
        {
            Directory.CreateDirectory(CacheDir);

            var downloaded = await _DownloadAsync(ct).ConfigureAwait(false);
            if (downloaded is not null)
                Logger.Info($"authlib-injector 已就绪：{downloaded}");

            return downloaded;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "下载 authlib-injector 失败");
            return null;
        }
    }
    /// <summary>清理缓存，下次启动会重新下载。</summary>
    public static void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
            Logger.Info("已清理 authlib-injector 缓存");
        }
        catch (Exception ex)
        {
            Logger.Warn($"清理缓存失败: {ex.Message}");
        }
    }

    private static async Task<string?> _DownloadAsync(CancellationToken ct)
    {
        // 优先官方制品索引：返回 JSON，内含 jar 的实际下载地址
        try
        {
            var json = await Net.Http.GetStringAsync(ArtifactIndexUrl, 1,
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var downloadUrl = node?["download_url"]?.GetValue<string>();
            var expectedSha256 = node?["checksums"]?["sha256"]?.GetValue<string>();
            var version = node?["version"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(downloadUrl))
            {
                var bytes = await Net.Http.GetBytesAsync(downloadUrl, ct: ct).ConfigureAwait(false);

                // 校验完整性：索引提供了 sha256，不匹配则视为下载失败
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var actual = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

                    if (!actual.Equals(expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        Logger.Warn($"authlib-injector 校验失败：期望 {expectedSha256[..16]}…，实际 {actual[..16]}…");
                        return null;
                    }
                }

                if (bytes.Length > 100_000 && IsJar(bytes))
                {
                    await File.WriteAllBytesAsync(CachedJar, bytes, ct).ConfigureAwait(false);
                    Logger.Info($"已下载 authlib-injector {version}");
                    return CachedJar;
                }

                Logger.Warn($"官方索引提供的文件不是有效 jar（{bytes.Length} 字节）");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"从官方索引下载 authlib-injector 失败: {ex.Message}");
        }

        // 回退 GitHub
        try
        {
            var json = await Net.Http.GetStringAsync(ReleaseApiUrl, 1,
                TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var assets = node?["assets"] as System.Text.Json.Nodes.JsonArray;

            if (assets is not null)
            {
                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>() ?? string.Empty;
                    if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                    var url = asset?["browser_download_url"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    var bytes = await Net.Http.GetBytesAsync(url, ct: ct).ConfigureAwait(false);
                    if (bytes.Length > 100_000 && IsJar(bytes))
                    {
                        await File.WriteAllBytesAsync(CachedJar, bytes, ct).ConfigureAwait(false);
                        return CachedJar;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"GitHub 下载 authlib-injector 失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>简单校验：zip 头为 PK\x03\x04，jar 本质是 zip。</summary>
    private static bool IsJar(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        return bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
    }
}


