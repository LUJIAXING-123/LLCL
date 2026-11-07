namespace LLCL.Core.Net;

/// <summary>下载文件类型，用于按类型选择下载源。</summary>
public enum FileKind
{
    /// <summary>游戏与库文件（版本 JSON、客户端 jar、libraries）。</summary>
    Game,
    /// <summary>资源文件（assets objects）。</summary>
    Asset,
    /// <summary>模组加载器相关文件。</summary>
    Loader
}

/// <summary>
/// 下载源管理：官方源与镜像源（BMCLAPI / 自定义）之间的 URL 映射。
/// 镜像站对下载地址有固定映射规则，见 <see cref="MapUrl"/>。
/// </summary>
public static class DownloadMirror
{
    private const string OfficialManifest = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private const string OfficialManifestV1 = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
    private const string PistonMeta = "https://piston-meta.mojang.com";
    private const string PistonData = "https://piston-data.mojang.com";
    private const string Resources = "https://resources.download.minecraft.net";
    private const string Libraries = "https://libraries.minecraft.net";
    private const string LauncherMeta = "https://launcher.mojang.com";

    /// <summary>获取版本清单 URL。</summary>
    public static string ManifestUrl(DownloadSource source, bool v2 = true)
    {
        var official = v2 ? OfficialManifest : OfficialManifestV1;
        return source switch
        {
            DownloadSource.BmclApi => "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            DownloadSource.Custom => LauncherConfig.Current.CustomSourceUrl.TrimEnd('/') + "/mc/game/version_manifest_v2.json",
            _ => official
        };
    }

    /// <summary>判断 URL 属于哪一类文件。</summary>
    public static FileKind Classify(string url)
    {
        if (url.Contains("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/assets/", StringComparison.OrdinalIgnoreCase))
            return FileKind.Asset;

        if (url.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("quilt", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase))
            return FileKind.Loader;

        return FileKind.Game;
    }

    /// <summary>
    /// 按文件类型获取该 URL 的候选源列表（已按优先级排序）。
    /// <para>
    /// 每个源的地址由 <paramref name="officialUrl"/> 的路径转换而来：
    /// assets 源只需替换 host，其余源按各自的映射规则处理。
    /// </para>
    /// </summary>
    public static List<string> GetCandidates(string officialUrl)
    {
        var kind = Classify(officialUrl);
        var config = LauncherConfig.Current.EffectiveSources;

        var options = kind switch
        {
            FileKind.Asset => config.Assets,
            FileKind.Loader => config.Loader,
            _ => config.Game
        };

        var list = options
            .Where(o => o.Enabled)
            .OrderBy(o => o.Priority)
            .Select(o => BuildFromBase(o.BaseUrl, officialUrl, kind))
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 全部关闭时兜底用官方地址
        if (list.Count == 0) list.Add(officialUrl);
        return list;
    }

    /// <summary>用指定基地址拼出该文件的下载 URL。</summary>
    private static string BuildFromBase(string baseUrl, string officialUrl, FileKind kind)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

        // 官方源标记：直接返回官方地址
        if (baseUrl is "https://official" or "official") return _Normalize(officialUrl);

        var b = baseUrl.TrimEnd('/');
        var url = _Normalize(officialUrl);

        switch (kind)
        {
            case FileKind.Asset:
                // assets 源基地址已包含 /assets，只需追加 /xx/hash
                var assetPath = _ExtractAssetPath(url);
                return assetPath is null ? string.Empty : $"{b}/{assetPath}";

            default:
                // 其余类型复用完整的映射规则
                return _MapWithBase(url, b);
        }
    }

    /// <summary>从资源 URL 中取出 xx/hash 形式的两段路径。</summary>
    private static string? _ExtractAssetPath(string url)
    {
        try
        {
            var seg = new Uri(url).AbsolutePath.TrimStart('/').Split('/');
            if (seg.Length >= 2) return $"{seg[^2]}/{seg[^1]}";
            return null;
        }
        catch { return null; }
    }
    /// <summary>
    /// 将官方 URL 映射为目标下载源 URL。官方源原样返回。
    /// </summary>
    public static string MapUrl(string officialUrl)
    {
        // 优先使用分类型配置中的首个启用源
        var candidates = GetCandidates(officialUrl);
        if (candidates.Count > 0) return candidates[0];

        return _Normalize(officialUrl);
    }

    /// <summary>按旧版全局设置把 URL 映射到镜像（保留以兼容自定义源）。</summary>
    private static string _MapWithBase(string officialUrl, string baseUrl)
    {
        var url = _Normalize(officialUrl);

        // 版本 JSON 与资源索引：piston-meta / launchermeta → 镜像 /version 与 /assets
        if (url.Contains("/mc/game/version_manifest", StringComparison.Ordinal))
            return baseUrl + "/mc/game/version_manifest_v2.json";

        if (url.Contains("piston-meta.mojang.com", StringComparison.Ordinal))
            return url.Replace(PistonMeta, baseUrl + "/assets", StringComparison.Ordinal)
                .Replace(LauncherMeta + "/mc/game/version_manifest", baseUrl + "/mc/game", StringComparison.Ordinal);

        // 游戏与资源文件
        if (url.Contains(PistonData, StringComparison.Ordinal))
            return url.Replace(PistonData, baseUrl + "/assets", StringComparison.Ordinal);

        if (url.StartsWith(Resources, StringComparison.Ordinal))
            return url.Replace(Resources, baseUrl + "/assets", StringComparison.Ordinal);

        // 库文件
        if (url.Contains(Libraries, StringComparison.Ordinal))
            return url.Replace(Libraries, baseUrl + "/maven", StringComparison.Ordinal);

        // Fabric / Quilt 等第三方 maven：走 BMCLAPI 的 maven 代理
        // 官方 maven.fabricmc.net 在国内速度极差（常见几 KB/s），必须替换
        foreach (var thirdParty in ThirdPartyMavens)
        {
            if (!url.Contains(thirdParty, StringComparison.OrdinalIgnoreCase)) continue;

            var path = _ExtractMavenPath(url);
            if (path is not null) return $"{baseUrl}/maven/{path}";
        }

        return url;
    }

    /// <summary>需要镜像代理的第三方 maven 域名。</summary>
    private static readonly string[] ThirdPartyMavens =
    [
        "maven.fabricmc.net",
        "maven.quiltmc.org",
        "libraries.minecraft.net"
    ];

    /// <summary>从 maven URL 中截取 group/artifact/version/xxx.jar 这段路径。</summary>
    private static string? _ExtractMavenPath(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.TrimStart('/').Split('/');

            // 找到第一个以 group 域名开头的片段（如 org、net、com）
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i] is "org" or "net" or "com" or "io" or "dev") 
                    return string.Join('/', segments[i..]);
            }

            return segments.Length > 0 ? string.Join('/', segments) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>版本 JSON 的下载地址（用于补全缺少 download.url 的情况）。</summary>
    public static string VersionJsonUrl(string version, DownloadSource source)
    {
        var baseUrl = source switch
        {
            DownloadSource.BmclApi => "https://bmclapi2.bangbang93.com",
            DownloadSource.Custom => LauncherConfig.Current.CustomSourceUrl.TrimEnd('/'),
            _ => null
        };

        return baseUrl is null
            ? $"https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"
            : $"{baseUrl}/version/{version}/{version}.json";
    }

    private static string _Normalize(string url)
    {
        // Mojang 旧版资源域名统一升级为 https
        return url.Replace("http://resources.download.minecraft.net", Resources, StringComparison.OrdinalIgnoreCase)
            .Replace("http://piston-data.mojang.com", PistonData, StringComparison.OrdinalIgnoreCase)
            .Replace("http://piston-meta.mojang.com", PistonMeta, StringComparison.OrdinalIgnoreCase)
            .Replace("http://launchermeta.mojang.com", "https://launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase);
    }
}



