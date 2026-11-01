namespace LLCL.Core.Net;

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

    /// <summary>
    /// 将官方 URL 映射为目标下载源 URL。官方源原样返回。
    /// </summary>
    public static string MapUrl(string officialUrl)
    {
        var source = LauncherConfig.Current.Source;
        if (source == DownloadSource.Official) return _Normalize(officialUrl);

        var baseUrl = source == DownloadSource.Custom
            ? LauncherConfig.Current.CustomSourceUrl.TrimEnd('/')
            : "https://bmclapi2.bangbang93.com";

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

        // Forge / NeoForge / Fabric 等元数据走各自 JS 中配置的镜像，不在此处处理
        return url;
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
