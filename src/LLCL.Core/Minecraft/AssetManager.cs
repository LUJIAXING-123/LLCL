using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLCL.Core.Minecraft;

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class AssetIndexFile
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
    [JsonPropertyName("map_to_resources")] public bool MapToResources { get; set; }
    [JsonPropertyName("virtual")] public bool Virtual { get; set; }
}

/// <summary>
/// 资源（assets）管理：读取索引、创建虚拟资源目录（旧版本需要）。
/// </summary>
public static class AssetManager
{
    private static readonly ConcurrentDictionary<string, AssetIndexFile> _cache = new();

    public static IEnumerable<KeyValuePair<string, AssetObject>> ReadIndex(string indexPath)
    {
        AssetIndexFile? index;
        try
        {
            index = _cache.GetOrAdd(indexPath, p =>
                JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(p), LauncherConfig.JsonOptions)
                ?? new AssetIndexFile());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"读取资源索引失败 {indexPath}");
            index = new AssetIndexFile();
        }

        return index.Objects;
    }

    public static string GetObjectPath(string hash)
        => Path.Combine(Paths.AssetsDir, "objects", hash[..2], hash);

    /// <summary>
    /// 旧版本（≤1.6）需要把资源映射到 resources 目录，现代版本使用 --assetsDir 直接访问。
    /// </summary>
    public static void CreateVirtualAssets(string indexId, string versionDir, CancellationToken ct = default)
    {
        var indexPath = Path.Combine(Paths.AssetsDir, "indexes", indexId + ".json");
        if (!File.Exists(indexPath)) return;

        var index = _cache.GetOrAdd(indexPath, p =>
            JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(p), LauncherConfig.JsonOptions)
            ?? new AssetIndexFile());

        var isLegacy = index.MapToResources || index.Virtual;
        if (!isLegacy) return;

        var targetRoot = index.MapToResources
            ? Path.Combine(versionDir, "resources")
            : Path.Combine(Paths.AssetsDir, "virtual", "legacy");

        Directory.CreateDirectory(targetRoot);

        foreach (var (name, obj) in index.Objects)
        {
            ct.ThrowIfCancellationRequested();
            var source = GetObjectPath(obj.Hash);
            if (!File.Exists(source)) continue;

            var target = Path.Combine(targetRoot, name.Replace('/', '\\'));
            if (File.Exists(target) && new FileInfo(target).Length == obj.Size) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Copy(source, target, true); } catch (Exception ex) { Logger.Warn($"复制资源失败 {name}: {ex.Message}"); }
        }
    }
}
