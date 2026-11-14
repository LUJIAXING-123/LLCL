using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLCL.Core.Minecraft;

/// <summary>导出时需要包含的内容。</summary>
public sealed class ModpackExportOptions
{
    /// <summary>整合包名称。留空时用版本显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>整合包版本号。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>是否包含 mods。</summary>
    public bool IncludeMods { get; set; } = true;

    /// <summary>是否包含资源包。</summary>
    public bool IncludeResourcePacks { get; set; } = true;

    /// <summary>是否包含光影。</summary>
    public bool IncludeShaderPacks { get; set; } = true;

    /// <summary>是否包含存档。</summary>
    public bool IncludeSaves { get; set; }

    /// <summary>是否包含 config 等配置文件。</summary>
    public bool IncludeConfig { get; set; } = true;

    /// <summary>
    /// 是否把文件原样打进压缩包（而不是只写下载地址）。
    /// 开启后产物体积大，但对方无网络也能装。
    /// </summary>
    public bool BundleFiles { get; set; }
}

/// <summary>导出结果。</summary>
public sealed class ModpackExportResult
{
    public string Path { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long Size { get; init; }
}

/// <summary>
/// 整合包导出。生成 Modrinth 格式（modrinth.index.json + overrides），
/// 结构与 PCL-CE 的 PageInstanceExport 产出一致。
/// </summary>
public static class ModpackExporter
{
    /// <summary>
    /// 把指定版本导出为整合包。
    /// </summary>
    public static async Task<ModpackExportResult> ExportAsync(string versionId, string outputPath,
        ModpackExportOptions options, IProgress<(string step, double ratio)>? progress = null,
        CancellationToken ct = default)
    {
        var version = VersionLoader.Load(versionId);
        var instanceDir = Path.Combine(Paths.VersionsDir, versionId);

        // 原版版本必须取原始 json 的 inheritsFrom：
        // VersionLoader.Load 返回的是合并后的结果，其中 inheritsFrom 已被丢弃，
        // 直接用它会把整串版本 ID（如 26.3-Fabric-0.20.1）写进 dependencies，
        // 对方按这个值去找原版会失败。
        var minecraftVersion = ReadVanillaVersion(versionId)
                               ?? (string.IsNullOrWhiteSpace(version.InheritsFrom)
                                   ? version.Id
                                   : version.InheritsFrom);

        var (loaderName, loaderVersion) = DetectLoaderForExport(version);

        progress?.Report(("正在收集文件", 0.05));

        // 按选项收集要导出的相对路径
        var folders = new List<string>();
        if (options.IncludeMods) folders.Add("mods");
        if (options.IncludeResourcePacks) folders.Add("resourcepacks");
        if (options.IncludeShaderPacks) folders.Add("shaderpacks");
        if (options.IncludeSaves) folders.Add("saves");
        if (options.IncludeConfig) folders.Add("config");

        var files = new List<(string FullPath, string Relative)>();

        foreach (var folder in folders)
        {
            var dir = Path.Combine(instanceDir, folder);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(instanceDir, file).Replace('\\', '/');
                files.Add((file, relative));
            }
        }

        Logger.Info($"[导出] 版本 {versionId} 收集到 {files.Count} 个文件");

        progress?.Report(($"正在计算校验值（{files.Count} 个文件）", 0.15));

        // 生成 modrinth.index.json 的 files 数组
        var fileArray = new JsonArray();
        var bundled = new List<(string FullPath, string Relative)>();

        var index = 0;
        foreach (var (fullPath, relative) in files)
        {
            ct.ThrowIfCancellationRequested();

            index++;
            if (files.Count > 0)
                progress?.Report(($"正在计算校验值 {index}/{files.Count}", 0.15 + 0.5 * index / files.Count));

            var info = new FileInfo(fullPath);

            if (options.BundleFiles)
            {
                // 原样打包：文件放进 overrides，不需要下载地址
                bundled.Add((fullPath, relative));
            }
            else
            {
                // 只记录校验值，要求对方自己下载。
                // 没有来源地址时无法还原，仍放进 overrides 兜底。
                var sha1 = await HashAsync(fullPath, SHA1.Create, ct).ConfigureAwait(false);
                var sha512 = await HashAsync(fullPath, SHA512.Create, ct).ConfigureAwait(false);

                fileArray.Add(new JsonObject
                {
                    ["path"] = relative,
                    ["hashes"] = new JsonObject
                    {
                        ["sha1"] = sha1,
                        ["sha512"] = sha512
                    },
                    ["downloads"] = new JsonArray(),
                    ["fileSize"] = info.Length
                });

                bundled.Add((fullPath, relative));
            }
        }

        // 组装 index
        var dependencies = new JsonObject { ["minecraft"] = minecraftVersion };

        if (!string.IsNullOrWhiteSpace(loaderVersion))
        {
            switch (loaderName.ToLowerInvariant())
            {
                case "forge":
                    dependencies["forge"] = loaderVersion;
                    break;
                case "neoforge":
                    dependencies["neoforge"] = loaderVersion;
                    break;
                case "fabric":
                    dependencies["fabric-loader"] = loaderVersion;
                    break;
                case "quilt":
                    dependencies["quilt-loader"] = loaderVersion;
                    break;
            }
        }

        var displayName = string.IsNullOrWhiteSpace(options.Name)
            ? VersionSettings.Load(versionId).DisplayName ?? versionId
            : options.Name;

        var modrinthIndex = new JsonObject
        {
            ["game"] = "minecraft",
            ["formatVersion"] = 1,
            ["versionId"] = options.Version,
            ["name"] = displayName,
            ["summary"] = VersionSettings.Load(versionId).Comment ?? string.Empty,
            ["files"] = fileArray,
            ["dependencies"] = dependencies
        };

        progress?.Report(("正在打包", 0.7));

        // 写压缩包：modrinth.index.json + overrides/<文件>
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir)) Directory.CreateDirectory(outputDir);

        if (File.Exists(outputPath)) File.Delete(outputPath);

        using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // 1) 索引
            var indexEntry = archive.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            await using (var entryStream = indexEntry.Open())
            await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(modrinthIndex.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                })).ConfigureAwait(false);
            }

            // 2) overrides
            foreach (var (fullPath, relative) in bundled)
            {
                ct.ThrowIfCancellationRequested();

                var entryName = "overrides/" + relative;

                try
                {
                    archive.CreateEntryFromFile(fullPath, entryName, CompressionLevel.Optimal);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[导出] 打包 {relative} 失败: {ex.Message}");
                }
            }
        }

        progress?.Report(("导出完成", 1.0));

        var result = new ModpackExportResult
        {
            Path = outputPath,
            FileCount = bundled.Count,
            Size = new FileInfo(outputPath).Length
        };

        Logger.Info($"[导出] 完成：{outputPath}（{result.FileCount} 个文件，{result.Size / 1024 / 1024} MB）");
        return result;
    }

    private static async Task<string> HashAsync(string path, Func<HashAlgorithm> factory,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var algorithm = factory();

        var hash = await algorithm.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 读取本版本所基于的原版版本号。纯原版返回 null。
    /// <para>
    /// 优先读 <c>llclParent</c>：加载器版本安装时会把 inheritsFrom 去掉改成自包含，
    /// 同时把原版版本写进这个自定义字段。
    /// 旧的版本文件可能只有 inheritsFrom，因此都兼容。
    /// 两者都没有时按版本 ID 的命名规律推断（如 26.3-Fabric-0.20.1 → 26.3）。
    /// </para>
    /// </summary>
    private static string? ReadVanillaVersion(string versionId)
    {
        try
        {
            var path = Path.Combine(Paths.VersionsDir, versionId, versionId + ".json");
            if (!File.Exists(path)) return null;

            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;

            var recorded = json?["llclParent"]?.GetValue<string>()
                           ?? json?["inheritsFrom"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(recorded)) return recorded;

            // 兜底：从 ID 里剥离加载器后缀
            foreach (var marker in new[] { "-Fabric-", "-Forge-", "-NeoForge-", "-Quilt-" })
            {
                var index = versionId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index > 0) return versionId[..index];
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 {versionId} 的原版版本失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 提取加载器名称与版本。
    /// <para>
    /// VersionManager.DetectLoader 只用于界面展示，对 Fabric 一律返回空版本，
    /// 而导出必须写入确切版本号，否则对方装不出同样的环境。
    /// 这里直接从合并后的 libraries 里取 fabric-loader / forge / neoforge 的坐标。
    /// </para>
    /// </summary>
    private static (string Name, string? Version) DetectLoaderForExport(VersionJson version)
    {
        var main = version.MainClass ?? string.Empty;

        foreach (var lib in version.Libraries)
        {
            var coord = lib.Coordinate;
            if (coord is null) continue;

            var group = coord.Group.ToLowerInvariant();
            var artifact = coord.Artifact?.ToLowerInvariant();

            // Fabric：net.fabricmc:fabric-loader:<ver>
            if (group == "net.fabricmc" && artifact == "fabric-loader")
                return ("fabric", coord.Version);

            // Quilt：org.quiltmc:quilt-loader:<ver>
            if (group == "org.quiltmc" && artifact == "quilt-loader")
                return ("quilt", coord.Version);

            // NeoForge：net.neoforged:neoforge:<ver>
            if (group == "net.neoforged" && artifact == "neoforge")
                return ("neoforge", coord.Version);

            // Forge：net.minecraftforge:forge:<mc>-<ver>，版本里带游戏版本前缀
            if (group == "net.minecraftforge" && artifact == "forge")
            {
                var raw = coord.Version ?? string.Empty;
                var dash = raw.IndexOf('-');
                var bare = dash > 0 ? raw[(dash + 1)..] : raw;
                return ("forge", bare);
            }
        }

        // 没有加载器库时，靠 mainClass 兜底判断
        if (main.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return ("fabric", null);
        if (main.Contains("neoforged", StringComparison.OrdinalIgnoreCase)) return ("neoforge", null);
        if (main.Contains("fml", StringComparison.OrdinalIgnoreCase)) return ("forge", null);
        if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return ("quilt", null);

        return ("vanilla", null);
    }
}
