using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLCL.Core.Minecraft;

/// <summary>
/// 版本清单服务：拉取、缓存并离线回退。
/// </summary>
public sealed class VersionManifestService
{
    private static VersionManifest? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheInterval = TimeSpan.FromHours(2);

    public static async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached is not null && DateTime.Now - _cachedAt < CacheInterval)
            return _cached;

        var cacheFile = Path.Combine(Paths.CacheDir, "version_manifest.json");

        Exception? last = null;
        // 依次尝试：配置的源 → 官方源 → 镜像
        var candidates = new List<string>();
        var configured = Net.DownloadMirror.ManifestUrl(LauncherConfig.Current.Source);
        candidates.Add(configured);
        if (!candidates.Contains("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"))
            candidates.Add("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
        if (!candidates.Contains("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"))
            candidates.Add("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");

        foreach (var url in candidates)
        {
            try
            {
                var json = await Net.Http.GetStringAsync(url, 2, ct).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<VersionManifest>(json, LauncherConfig.JsonOptions);
                if (manifest?.Versions.Count > 0)
                {
                    _cached = manifest;
                    _cachedAt = DateTime.Now;
                    try
                    {
                        Directory.CreateDirectory(Paths.CacheDir);
                        File.WriteAllText(cacheFile, json);
                    }
                    catch { }
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"获取版本清单失败 {url}: {ex.Message}");
            }
        }

        // 回退到本地缓存
        if (File.Exists(cacheFile))
        {
            try
            {
                var json = File.ReadAllText(cacheFile);
                var manifest = JsonSerializer.Deserialize<VersionManifest>(json, LauncherConfig.JsonOptions);
                if (manifest?.Versions.Count > 0)
                {
                    Logger.Warn("网络不可用，使用本地缓存的版本清单");
                    _cached = manifest;
                    _cachedAt = DateTime.Now;
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "读取本地版本清单失败");
            }
        }

        throw new IOException("无法获取版本清单，请检查网络", last);
    }

    /// <summary>按版本类型筛选（release / snapshot / old_alpha / old_beta）。</summary>
    public static List<VersionSummary> FilterByType(VersionManifest manifest, params string[] types)
        => manifest.Versions.Where(v => types.Length == 0 || types.Contains(v.Type)).ToList();
}
