using System.Text.Json;
using System.Text.Json.Nodes;
using LLCL.Core.Minecraft;

namespace LLCL.Core.Resources;

public enum ResourceType
{
    Mod,
    Modpack,
    ResourcePack,
    Shader,
    World,
    Datapack
}

public enum ResourceSource
{
    Modrinth,
    CurseForge
}

/// <summary>搜索结果中的一项。</summary>
public sealed class ResourceHit
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public long Downloads { get; init; }
    public string? IconUrl { get; init; }
    public ResourceSource Source { get; init; }
    public ResourceType Type { get; init; }
    public string? LatestVersion { get; init; }
    public string? Categories { get; init; }
}

/// <summary>可下载的具体文件。</summary>
public sealed class ResourceFile
{
    public string Url { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    public string VersionName { get; init; } = string.Empty;
    public string? GameVersions { get; init; }
    public string? Loaders { get; init; }
}

/// <summary>
/// 社区资源下载：Modrinth 与 CurseForge（经 mcimirror 镜像）。
/// </summary>
public static class ResourceService
{
    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string ModrinthMirror = "https://mod.mcimirror.top/modrinth/v2";
    private const string CurseForgeMirror = "https://mod.mcimirror.top/curseforge/v1";

    public static string TypeToProjectType(ResourceType type) => type switch
    {
        ResourceType.Mod => "mod",
        ResourceType.Modpack => "modpack",
        ResourceType.ResourcePack => "resourcepack",
        ResourceType.Shader => "shader",
        ResourceType.World => "world",
        _ => "datapack"
    };

    /// <summary>CurseForge classId 映射。</summary>
    private static int TypeToClassId(ResourceType type) => type switch
    {
        ResourceType.Mod => 6,
        ResourceType.Modpack => 4471,
        ResourceType.ResourcePack => 12,
        ResourceType.Shader => 6552,
        ResourceType.World => 17,
        _ => 6
    };

    // ---------------- Modrinth ----------------

    /// <summary>搜索 Modrinth 资源。</summary>
    public static async Task<List<ResourceHit>> SearchModrinthAsync(string query, ResourceType type,
        string? gameVersion = null, string? loader = null, int limit = 20, int offset = 0,
        CancellationToken ct = default)
    {
        var facets = new List<string> { $"[[\"project_type:{TypeToProjectType(type)}\"]" };
        if (!string.IsNullOrEmpty(gameVersion)) facets[0] += $",[\"versions:{gameVersion}\"]";
        if (!string.IsNullOrEmpty(loader) && type is ResourceType.Mod or ResourceType.Modpack)
            facets[0] += $",[\"categories:{loader}\"]";
        facets[0] += "]";

        var url = $"{ModrinthApi}/search?query={Uri.EscapeDataString(query)}" +
                  $"&facets={Uri.EscapeDataString(facets[0])}&limit={limit}&offset={offset}" +
                  "&index=relevance";

        var json = await GetJsonWithFallbackAsync(url, ModrinthMirror + url[ModrinthApi.Length..], ct)
            .ConfigureAwait(false);
        if (json is null) return [];

        var hits = new List<ResourceHit>();
        if (json["hits"] is not JsonArray arr) return hits;

        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            hits.Add(new ResourceHit
            {
                Id = obj["project_id"]?.GetValue<string>() ?? string.Empty,
                Name = obj["title"]?.GetValue<string>() ?? string.Empty,
                Description = obj["description"]?.GetValue<string>() ?? string.Empty,
                Author = obj["author"]?.GetValue<string>() ?? string.Empty,
                Downloads = obj["downloads"]?.GetValue<long>() ?? 0,
                IconUrl = obj["icon_url"]?.GetValue<string>(),
                Source = ResourceSource.Modrinth,
                Type = type,
                LatestVersion = obj["versions"] is JsonArray vs && vs.Count > 0
                    ? vs[^1]?.GetValue<string>()
                    : null,
                Categories = obj["categories"] is JsonArray cats
                    ? string.Join(", ", cats.Select(c => c?.GetValue<string>() ?? string.Empty))
                    : null
            });
        }

        return hits;
    }

    /// <summary>获取 Modrinth 项目的可用文件列表。</summary>
    public static async Task<List<ResourceFile>> GetModrinthFilesAsync(string projectId, string? gameVersion,
        string? loader, CancellationToken ct = default)
    {
        var url = $"{ModrinthApi}/project/{projectId}/version";
        if (!string.IsNullOrEmpty(gameVersion) || !string.IsNullOrEmpty(loader))
        {
            var parts = new List<string>();
            if (gameVersion is not null) parts.Add($"game_versions=%5B%22{Uri.EscapeDataString(gameVersion)}%22%5D");
            if (loader is not null) parts.Add($"loaders=%5B%22{Uri.EscapeDataString(loader)}%22%5D");
            url += "?" + string.Join("&", parts);
        }

        var json = await GetJsonWithFallbackAsync(url, ModrinthMirror + url[ModrinthApi.Length..], ct)
            .ConfigureAwait(false);
        if (json is JsonArray arr)
        {
            var files = new List<ResourceFile>();
            foreach (var version in arr)
            {
                if (version is not JsonObject obj) continue;
                if (obj["files"] is not JsonArray fileArr) continue;

                foreach (var file in fileArr)
                {
                    if (file is not JsonObject fileObj) continue;
                    var primary = fileObj["primary"]?.GetValue<bool>() ?? false;

                    files.Add(new ResourceFile
                    {
                        Url = fileObj["url"]?.GetValue<string>() ?? string.Empty,
                        FileName = fileObj["filename"]?.GetValue<string>() ?? string.Empty,
                        Sha1 = fileObj["hashes"]?["sha1"]?.GetValue<string>(),
                        Size = fileObj["size"]?.GetValue<long>() ?? 0,
                        VersionName = obj["name"]?.GetValue<string>() ?? obj["version_number"]?.GetValue<string>() ?? string.Empty,
                        GameVersions = obj["game_versions"] is JsonArray gv
                            ? string.Join(", ", gv.Select(v => v?.GetValue<string>() ?? string.Empty))
                            : null,
                        Loaders = obj["loaders"] is JsonArray ld
                            ? string.Join(", ", ld.Select(v => v?.GetValue<string>() ?? string.Empty))
                            : null
                    });

                    if (primary) break;
                }
            }

            return files;
        }

        return [];
    }

    // ---------------- CurseForge ----------------

    /// <summary>搜索 CurseForge 资源（经镜像）。</summary>
    public static async Task<List<ResourceHit>> SearchCurseForgeAsync(string query, ResourceType type,
        string? gameVersion = null, int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        var url = $"{CurseForgeMirror}/mods/search?gameId=432&classId={TypeToClassId(type)}" +
                  $"&searchFilter={Uri.EscapeDataString(query)}&pageSize={limit}" +
                  $"&index={offset}&sortField=2&sortOrder=desc";
        if (!string.IsNullOrEmpty(gameVersion))
            url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (json?["data"] is not JsonArray arr) return [];

        var hits = new List<ResourceHit>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            hits.Add(new ResourceHit
            {
                Id = obj["id"]?.GetValue<long>().ToString() ?? string.Empty,
                Name = obj["name"]?.GetValue<string>() ?? string.Empty,
                Description = obj["summary"]?.GetValue<string>() ?? string.Empty,
                Author = obj["authors"] is JsonArray authors && authors.Count > 0
                    ? authors[0]?["name"]?.GetValue<string>() ?? string.Empty
                    : string.Empty,
                Downloads = obj["downloadCount"]?.GetValue<long>() ?? 0,
                IconUrl = obj["logo"]?["thumbnailUrl"]?.GetValue<string>(),
                Source = ResourceSource.CurseForge,
                Type = type,
                LatestVersion = obj["latestFilesIndexes"] is JsonArray idx && idx.Count > 0
                    ? idx[0]?["filename"]?.GetValue<string>()
                    : null
            });
        }

        return hits;
    }

    /// <summary>获取 CurseForge 项目的文件列表。</summary>
    public static async Task<List<ResourceFile>> GetCurseForgeFilesAsync(string projectId, string? gameVersion,
        CancellationToken ct = default)
    {
        var url = $"{CurseForgeMirror}/mods/{projectId}/files?pageSize=50";
        if (!string.IsNullOrEmpty(gameVersion))
            url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (json?["data"] is not JsonArray arr) return [];

        var files = new List<ResourceFile>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var downloadUrl = obj["downloadUrl"]?.GetValue<string>();
            if (string.IsNullOrEmpty(downloadUrl)) continue;

            files.Add(new ResourceFile
            {
                Url = downloadUrl,
                FileName = obj["fileName"]?.GetValue<string>() ?? string.Empty,
                Sha1 = obj["hashes"] is JsonArray hashes
                    ? hashes.FirstOrDefault(h => h?["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>()
                    : null,
                Size = obj["fileLength"]?.GetValue<long>() ?? 0,
                VersionName = obj["displayName"]?.GetValue<string>() ?? string.Empty,
                GameVersions = obj["gameVersions"] is JsonArray gv
                    ? string.Join(", ", gv.Select(v => v?.GetValue<string>() ?? string.Empty))
                    : null
            });
        }

        return files;
    }

    // ---------------- 下载到实例 ----------------

    /// <summary>把资源文件下载到对应目录（mods/resourcepacks/shaderpacks/saves）。</summary>
    public static async Task<string> DownloadToVersionAsync(ResourceFile file, string versionId,
        ResourceType type, IProgress<(string step, double ratio)>? progress = null,
        CancellationToken ct = default)
    {
        // 与 PCL 一致：默认放入 .minecraft 根目录的对应子目录，开启版本隔离后放入 versions/<id>
        var settings = VersionSettings.Load(versionId);
        var baseDir = settings.GetGameDir(versionId);
        var subDir = type switch
        {
            ResourceType.Mod => "mods",
            ResourceType.ResourcePack => "resourcepacks",
            ResourceType.Shader => "shaderpacks",
            ResourceType.World => "saves",
            _ => "mods"
        };

        var targetDir = Path.Combine(baseDir, subDir);
        Directory.CreateDirectory(targetDir);

        var target = Path.Combine(targetDir, file.FileName);
        if (File.Exists(target) && file.Size > 0 && new FileInfo(target).Length == file.Size)
        {
            progress?.Report(("已存在，跳过", 1.0));
            return target;
        }

        progress?.Report(($"下载 {file.FileName}", 0.1));

        var downloader = new Net.FileDownloader();
        downloader.Progress += (_, e) =>
        {
            if (e.TotalBytes > 0)
                progress?.Report(($"下载 {file.FileName}", 0.1 + 0.9 * e.DownloadedBytes / e.TotalBytes));
        };

        await downloader.DownloadAllAsync(
        [
            new Net.DownloadItem
            {
                Url = file.Url,
                TargetPath = target,
                Sha1 = file.Sha1,
                Size = file.Size
            }
        ], ct).ConfigureAwait(false);

        progress?.Report(("下载完成", 1.0));
        return target;
    }

    // ---------------- 内部 ----------------

    private static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            var text = await Net.Http.GetStringAsync(url, 3, ct).ConfigureAwait(false);
            return JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            Logger.Warn($"请求失败 {url}: {ex.Message}");
            return null;
        }
    }

    private static async Task<JsonNode?> GetJsonWithFallbackAsync(string primary, string fallback,
        CancellationToken ct)
    {
        var json = await GetJsonAsync(primary, ct).ConfigureAwait(false);
        if (json is not null) return json;
        Logger.Info($"主源不可用，回退镜像: {fallback}");
        return await GetJsonAsync(fallback, ct).ConfigureAwait(false);
    }
}

