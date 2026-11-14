using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LLCL.Core.Net;

namespace LLCL.Core.Minecraft;

/// <summary>整合包格式。</summary>
public enum ModpackFormat
{
    /// <summary>无法识别，按普通压缩包处理。</summary>
    Unknown,
    /// <summary>CurseForge：根目录 manifest.json，含 minecraft 与 modLoaders。</summary>
    CurseForge,
    /// <summary>Modrinth：根目录 modrinth.index.json。</summary>
    Modrinth,
    /// <summary>HMCL：modpack.json。</summary>
    Hmcl,
    /// <summary>MCBBS：mcbbs.packmeta。</summary>
    Mcbbs,
    /// <summary>MMC：mmc-pack.json。</summary>
    Mmc
}

/// <summary>整合包安装时解析出的元信息。</summary>
public sealed class ModpackInfo
{
    public ModpackFormat Format { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Summary { get; init; }

    /// <summary>需要的原版版本。</summary>
    public string MinecraftVersion { get; init; } = string.Empty;

    /// <summary>需要的加载器，为空表示纯原版。</summary>
    public LoaderInstaller.Kind? Loader { get; init; }
    public string? LoaderVersion { get; init; }

    /// <summary>压缩包内的基础目录（可能为空，也可能形如 <c>包名/</c>）。</summary>
    public string BaseFolder { get; init; } = string.Empty;

    /// <summary>待下载的文件清单。</summary>
    public List<ModpackFile> Files { get; init; } = [];
}

/// <summary>整合包中需要单独下载的文件。</summary>
public sealed class ModpackFile
{
    public string RelativePath { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? Sha1 { get; init; }
    public long Size { get; init; }

    /// <summary>客户端是否必须安装。optional 的文件默认跳过。</summary>
    public bool Required { get; init; } = true;
}

/// <summary>
/// 整合包安装。逻辑对齐 PCL-CE 的 ModModpack：
/// 识别格式 → 解压并铺开 overrides → 下载补全文件 → 安装对应游戏与加载器。
/// </summary>
public static class ModpackInstaller
{
    /// <summary>
    /// 只读取整合包元信息，不落盘。用于安装前预览与重名检查。
    /// </summary>
    public static ModpackInfo? Inspect(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return Detect(archive);
    }

    /// <summary>
    /// 安装整合包到指定实例名。返回实际使用的实例名。
    /// </summary>
    public static async Task<string> InstallAsync(string archivePath, string instanceId, string versionsRoot,
        IProgress<(string step, double ratio)>? progress = null, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var info = Detect(archive)
                   ?? throw new InvalidOperationException("无法识别的整合包格式，请确认这是 Minecraft 整合包。");

        var instanceDir = Path.Combine(versionsRoot, instanceId);
        Directory.CreateDirectory(instanceDir);

        // ---------- 1. 铺开 overrides ----------
        progress?.Report(("正在解压整合包", 0.05));
        var extracted = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项

            var relative = entry.FullName;

            // 去掉压缩包可能自带的一层根目录
            if (info.BaseFolder.Length > 0 && relative.StartsWith(info.BaseFolder, StringComparison.Ordinal))
                relative = relative[info.BaseFolder.Length..];

            // 只处理 overrides / client-overrides 下的内容
            var overridesPrefix = new[] { "overrides/", "client-overrides/" };
            var matched = overridesPrefix.FirstOrDefault(p =>
                relative.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (matched is null) continue;

            relative = relative[matched.Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;

            var target = SafeCombine(instanceDir, relative);
            if (target is null) continue; // 越界路径直接丢弃

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                entry.ExtractToFile(target, true);
                extracted++;
            }
            catch (Exception ex)
            {
                Logger.Warn($"解压 {entry.FullName} 失败: {ex.Message}");
            }
        }

        Logger.Info($"[整合包] 已解压 {extracted} 个覆写文件到 {instanceDir}");

        // ---------- 2. 下载缺失文件 ----------
        if (info.Files.Count > 0)
        {
            progress?.Report(($"正在下载 {info.Files.Count} 个文件", 0.35));
            await DownloadFilesAsync(info.Files, instanceDir, progress, ct).ConfigureAwait(false);
        }

        // ---------- 3. 安装游戏与加载器 ----------
        progress?.Report(("正在安装游戏本体", 0.55));
        await VanillaInstaller.InstallAsync(info.MinecraftVersion, versionsRoot, null, ct)
            .ConfigureAwait(false);

        if (info.Loader is { } kind && !string.IsNullOrWhiteSpace(info.LoaderVersion))
        {
            progress?.Report(($"正在安装 {kind} {info.LoaderVersion}", 0.75));
            await LoaderInstaller.InstallAsync(kind, info.MinecraftVersion, info.LoaderVersion,
                null, ct).ConfigureAwait(false);
        }

        // ---------- 4. 记录整合包信息 ----------
        var settings = VersionSettings.Load(instanceId);
        settings.DisplayName = info.Name;
        settings.ModpackSource = info.Format.ToString();
        settings.ModpackVersion = info.Version;
        settings.Comment = info.Summary;
        settings.Save();

        progress?.Report(("安装完成", 1.0));
        return instanceId;
    }

    private static async Task DownloadFilesAsync(List<ModpackFile> files, string instanceDir,
        IProgress<(string step, double ratio)>? progress, CancellationToken ct)
    {
        var downloader = new FileDownloader();
        var items = new List<DownloadItem>();
        var total = files.Count;
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var target = SafeCombine(instanceDir, file.RelativePath);
            if (target is null)
            {
                Logger.Warn($"[整合包] 跳过越界路径: {file.RelativePath}");
                continue;
            }

            // 已存在且大小一致则跳过
            if (File.Exists(target) && file.Size > 0 && new FileInfo(target).Length == file.Size)
            {
                done++;
                continue;
            }

            items.Add(new DownloadItem { Url = file.Url, TargetPath = target, Sha1 = file.Sha1, Size = file.Size });
        }

        if (items.Count == 0) return;

        await downloader.DownloadAllAsync(items, ct).ConfigureAwait(false);

        progress?.Report(($"已下载 {total} 个文件", 0.5));
    }

    /// <summary>
    /// 把相对路径拼到根目录下，并校验结果仍在根目录内，
    /// 防止压缩包里的 ../ 造成路径穿越。
    /// </summary>
    private static string? SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);

        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    // ---------------------------------------------------------------- 格式识别

    private static ModpackInfo? Detect(ZipArchive archive)
    {
        // 先在根目录找，再在一级子目录找（很多整合包会多套一层文件夹）
        foreach (var (prefix, entry) in EnumerateRootCandidates(archive))
        {
            if (entry is null) continue;

            var name = entry.Name;

            if (name == "modrinth.index.json") return ParseModrinth(archive, prefix, entry);
            if (name == "manifest.json") return ParseCurseForge(archive, prefix, entry);
            if (name == "modpack.json") return ParseHmcl(archive, prefix, entry);
            if (name == "mcbbs.packmeta") return ParseMcbbs(archive, prefix, entry);
        }

        return null;
    }

    /// <summary>依次尝试根目录与各个一级子目录，返回前缀与入口文件。</summary>
    private static IEnumerable<(string Prefix, ZipArchiveEntry? Entry)> EnumerateRootCandidates(ZipArchive archive)
    {
        var names = new[] { "modrinth.index.json", "manifest.json", "modpack.json", "mcbbs.packmeta" };

        // 根目录
        foreach (var name in names)
            yield return ("", archive.GetEntry(name));

        // 一级子目录
        var folders = archive.Entries
            .Select(e => e.FullName.Split('/'))
            .Where(parts => parts.Length == 2 && parts[1].Length > 0)
            .Select(parts => parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var name in names)
                yield return ($"{folder}/", archive.GetEntry($"{folder}/{name}"));
        }
    }

    private static JsonObject? ReadJson(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 {entry.FullName} 失败: {ex.Message}");
            return null;
        }
    }

    private static ModpackInfo? ParseModrinth(ZipArchive archive, string prefix, ZipArchiveEntry entry)
    {
        var json = ReadJson(entry);
        if (json is null) return null;

        var deps = json["dependencies"] as JsonObject ?? [];

        var minecraft = deps["minecraft"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(minecraft)) return null;

        LoaderInstaller.Kind? kind = null;
        string? loaderVersion = null;

        if (deps["forge"]?.GetValue<string>() is { } forge)
        {
            kind = LoaderInstaller.Kind.Forge;
            loaderVersion = forge;
        }
        else if (deps["neoforge"]?.GetValue<string>() is { } neo)
        {
            kind = LoaderInstaller.Kind.NeoForge;
            loaderVersion = neo;
        }
        else if (deps["fabric-loader"]?.GetValue<string>() is { } fabric)
        {
            kind = LoaderInstaller.Kind.Fabric;
            loaderVersion = fabric;
        }
        else if (deps["quilt-loader"] is not null)
        {
            // Quilt 与 Fabric 的安装方式接近，但依赖名不同，暂不支持
            Logger.Warn("[整合包] 暂不支持 Quilt 加载器的整合包");
        }

        var files = new List<ModpackFile>();

        if (json["files"] is JsonArray fileArray)
        {
            foreach (var node in fileArray)
            {
                if (node is not JsonObject file) continue;

                var path = file["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path)) continue;

                // env.client：optional 默认跳过，unsupported 一律跳过
                var clientEnv = file["env"]?["client"]?.GetValue<string>();
                if (clientEnv is "unsupported") continue;

                var required = clientEnv is not "optional";

                var url = (file["downloads"] as JsonArray)?
                    .Select(d => d?.GetValue<string>())
                    .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
                if (string.IsNullOrWhiteSpace(url)) continue;

                files.Add(new ModpackFile
                {
                    RelativePath = path,
                    Url = url,
                    Sha1 = file["hashes"]?["sha1"]?.GetValue<string>(),
                    Size = file["fileSize"]?.GetValue<long>() ?? 0,
                    Required = required
                });
            }
        }

        return new ModpackInfo
        {
            Format = ModpackFormat.Modrinth,
            Name = json["name"]?.GetValue<string>() ?? "整合包",
            Version = json["versionId"]?.GetValue<string>(),
            Summary = json["summary"]?.GetValue<string>(),
            MinecraftVersion = minecraft,
            Loader = kind,
            LoaderVersion = loaderVersion,
            BaseFolder = prefix,
            Files = files
        };
    }

    private static ModpackInfo? ParseCurseForge(ZipArchive archive, string prefix, ZipArchiveEntry entry)
    {
        var json = ReadJson(entry);
        if (json is null) return null;

        var minecraftObj = json["minecraft"] as JsonObject;
        var minecraft = minecraftObj?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(minecraft)) return null;

        LoaderInstaller.Kind? kind = null;
        string? loaderVersion = null;

        if (minecraftObj?["modLoaders"] is JsonArray loaders)
        {
            // 形如 forge-47.2.0 / fabric-0.15.7；primary 为 true 的那条才是要装的
            var primary = loaders
                .OfType<JsonObject>()
                .FirstOrDefault(l => l["primary"]?.GetValue<bool>() == true)
                ?? loaders.OfType<JsonObject>().FirstOrDefault();

            var id = primary?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                var dash = id.IndexOf('-');
                var loaderName = dash > 0 ? id[..dash] : id;
                var version = dash > 0 ? id[(dash + 1)..] : null;

                kind = loaderName.ToLowerInvariant() switch
                {
                    "forge" => LoaderInstaller.Kind.Forge,
                    "fabric" => LoaderInstaller.Kind.Fabric,
                    "neoforge" => LoaderInstaller.Kind.NeoForge,
                    "quilt" => null,
                    _ => null
                };

                if (kind is not null) loaderVersion = version;
            }
        }

        // CurseForge 只给出项目与文件 ID，需要逐个查询下载地址。
        // 这里先记录下来，交给调用方按需补全。
        var files = new List<ModpackFile>();
        if (json["files"] is JsonArray fileArray)
        {
            foreach (var node in fileArray)
            {
                if (node is not JsonObject file) continue;

                var projectId = file["projectID"]?.GetValue<long>();
                var fileId = file["fileID"]?.GetValue<long>();
                if (projectId is null || fileId is null) continue;

                // 文件名与路径只能靠接口查，这里用 ID 占位
                files.Add(new ModpackFile
                {
                    RelativePath = $"mods/{projectId}-{fileId}.jar",
                    Url = string.Empty, // 需要按 ID 解析
                    Required = file["required"]?.GetValue<bool>() ?? true
                });
            }
        }

        return new ModpackInfo
        {
            Format = ModpackFormat.CurseForge,
            Name = json["name"]?.GetValue<string>() ?? "整合包",
            Version = json["version"]?.GetValue<string>(),
            Author = json["author"]?.GetValue<string>(),
            Summary = json["manifest"]?["description"]?.GetValue<string>(),
            MinecraftVersion = minecraft,
            Loader = kind,
            LoaderVersion = loaderVersion,
            BaseFolder = prefix,
            Files = files
        };
    }

    private static ModpackInfo? ParseHmcl(ZipArchive archive, string prefix, ZipArchiveEntry entry)
    {
        var json = ReadJson(entry);
        if (json is null) return null;

        // HMCL 的 modpack.json 结构不固定，能取到多少算多少
        var minecraft = json["gameVersion"]?.GetValue<string>()
                        ?? json["minecraft"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(minecraft)) return null;

        return new ModpackInfo
        {
            Format = ModpackFormat.Hmcl,
            Name = json["name"]?.GetValue<string>() ?? "整合包",
            Version = json["version"]?.GetValue<string>(),
            Author = json["author"]?.GetValue<string>(),
            MinecraftVersion = minecraft,
            BaseFolder = prefix
        };
    }

    private static ModpackInfo? ParseMcbbs(ZipArchive archive, string prefix, ZipArchiveEntry entry)
    {
        // MCBBS 的 packmeta 是二进制 NBT，这里只做基本识别，
        // 具体版本信息需要额外解析，暂不支持自动安装。
        Logger.Warn("[整合包] MCBBS 格式暂不支持自动安装");
        return null;
    }
}
