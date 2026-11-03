using System.IO.Compression;

namespace LLCL.Core.Minecraft;

/// <summary>
/// 原版游戏安装：版本 JSON、客户端 jar、libraries、assets 与 natives。
/// </summary>
public static class VanillaInstaller
{
    public sealed class InstallResult
    {
        public int TotalFiles { get; set; }
        public int DownloadedFiles { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// 安装（或补全）指定原版版本。已存在的正确文件会跳过。
    /// </summary>
    public static async Task<InstallResult> InstallAsync(string versionId, string versionsRoot,
        IProgress<(string step, double ratio)>? progress = null, CancellationToken ct = default)
    {
        var result = new InstallResult();

        Directory.CreateDirectory(versionsRoot);

        // 1. 取版本 JSON（若本地不存在则从清单 / 镜像获取）
        var jsonPath = Path.Combine(versionsRoot, versionId, versionId + ".json");
        if (!File.Exists(jsonPath))
        {
            progress?.Report(("获取版本信息", 0.05));
            await _DownloadVersionJsonAsync(versionId, jsonPath, ct).ConfigureAwait(false);
        }

        var version = VersionLoader.Load(versionId, versionsRoot);

        // 2. 客户端 jar
        // 注意：加载器版本（Forge/Fabric 等）通过 inheritsFrom 继承原版的 Downloads.Client，
        // 此时原版 jar 必须落在「继承链根版本」的目录里，而不是加载器版本目录，
        // 否则同一个 jar 会在两个版本目录各下一份。
        var items = new List<Net.DownloadItem>();

        progress?.Report(("解析文件列表", 0.15));
        await _CollectFilesAsync(version, versionsRoot, items, ct).ConfigureAwait(false);

        result.TotalFiles = items.Count;
        result.TotalBytes = items.Sum(i => i.Size);

        progress?.Report(($"下载 {items.Count} 个文件", 0.2));

        var downloader = new Net.FileDownloader();
        downloader.Progress += (_, e) =>
        {
            var ratio = e.TotalBytes > 0 ? (double)e.DownloadedBytes / e.TotalBytes : 0.0;
            progress?.Report(($"下载 {e.CompletedFiles}/{e.TotalFiles} 个文件", 0.2 + ratio * 0.75));
        };

        await downloader.DownloadAllAsync(items, ct).ConfigureAwait(false);

        result.DownloadedFiles = items.Count;

        // 3. 解压 natives
        progress?.Report(("解压本地库", 0.97));
        await ExtractNativesAsync(version, versionsRoot, ct).ConfigureAwait(false);

        progress?.Report(("安装完成", 1.0));
        return result;
    }

    private static async Task _DownloadVersionJsonAsync(string versionId, string jsonPath, CancellationToken ct)
    {
        var manifest = await VersionManifestService.GetManifestAsync(false, ct).ConfigureAwait(false);
        var summary = manifest.Versions.FirstOrDefault(v => v.Id == versionId);
        if (summary is not null)
        {
            try
            {
                var json = await Net.Http.GetStringAsync(summary.Url, 2, ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
                File.WriteAllText(jsonPath, json);
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取版本 JSON 失败，尝试镜像: {ex.Message}");
            }
        }

        // 镜像兜底
        foreach (var url in new[]
                 {
                     $"https://bmclapi2.bangbang93.com/version/{versionId}/{versionId}.json",
                     $"https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"
                 })
        {
            try
            {
                if (url.EndsWith("version_manifest_v2.json", StringComparison.Ordinal)) continue;
                var json = await Net.Http.GetStringAsync(url, 1, ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
                File.WriteAllText(jsonPath, json);
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"镜像获取版本 JSON 失败 {url}: {ex.Message}");
            }
        }

        throw new FileNotFoundException($"无法获取版本 {versionId} 的 JSON");
    }

    /// <summary>
    /// 解析继承链的根版本 ID（原版版本）。
    /// 加载器版本（Forge/Fabric 等）通过 inheritsFrom 指向原版，
    /// 根版本才是 jar 与 natives 的归属目录。
    /// </summary>
    private static string ResolveRootVersionId(VersionJson version)
    {
        var current = version;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Id };

        while (!string.IsNullOrEmpty(current.InheritsFrom))
        {
            if (!seen.Add(current.InheritsFrom)) break;

            try
            {
                current = VersionLoader.Load(current.InheritsFrom);
            }
            catch (Exception ex)
            {
                Logger.Warn($"解析继承链失败，停在 {current.Id}: {ex.Message}");
                break;
            }
        }

        return current.Id;
    }
    /// <summary>收集需要下载的全部文件（客户端 jar、库、资源、资源索引、日志配置）。</summary>
    public static async Task _CollectFilesAsync(VersionJson version, string versionsRoot,
        List<Net.DownloadItem> items, CancellationToken ct)
    {
        var add = (Net.DownloadItem item) =>
        {
            if (!items.Any(i => i.TargetPath.Equals(item.TargetPath, StringComparison.OrdinalIgnoreCase)))
                items.Add(item);
        };

        // 客户端 jar
        // 加载器版本的 Downloads.Client 继承自原版，jar 应写入继承链根版本目录，
        // 与 GameLauncher 的 classpath 解析保持一致，避免重复落盘。
        var clientArtifact = version.Downloads?.Client;
        if (clientArtifact is not null && !string.IsNullOrEmpty(clientArtifact.Url))
        {
            var rootId = ResolveRootVersionId(version);
            var target = Path.Combine(versionsRoot, rootId, rootId + ".jar");
            add(new Net.DownloadItem
            {
                Url = clientArtifact.Url,
                TargetPath = target,
                Sha1 = clientArtifact.Sha1,
                Size = clientArtifact.Size
            });
        }

        // 库文件
        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;

            var coordinate = lib.Coordinate;
            if (coordinate is null) continue;

            // natives：解压到版本目录，下载的是 classifier jar
            var nativeClassifier = lib.Natives?.GetValueOrDefault("windows");
            if (!string.IsNullOrEmpty(nativeClassifier))
            {
                nativeClassifier = nativeClassifier.Replace("${arch}", _NativeArch, StringComparison.Ordinal);
                var nativeArtifact = lib.Downloads?.Classifiers?.GetValueOrDefault(nativeClassifier);
                if (nativeArtifact is not null)
                {
                    add(new Net.DownloadItem
                    {
                        Url = nativeArtifact.Url,
                        TargetPath = Path.Combine(Paths.LibrariesDir, coordinate.ToPath().Replace(
                            $"{coordinate.Version}/", $"{coordinate.Version}/", StringComparison.Ordinal)),
                        Sha1 = nativeArtifact.Sha1,
                        Size = nativeArtifact.Size
                    });
                }

                continue;
            }

            var artifact = lib.Downloads?.Artifact;
            if (artifact is not null)
            {
                add(new Net.DownloadItem
                {
                    Url = artifact.Url,
                    TargetPath = Path.Combine(Paths.LibrariesDir, artifact.Path ?? coordinate.ToPath()),
                    Sha1 = artifact.Sha1,
                    Size = artifact.Size
                });
            }
            else
            {
                // 部分库（如某些 Forge 库）只给 Maven 坐标，没有 downloads 字段
                var path = coordinate.ToPath();
                add(new Net.DownloadItem
                {
                    Url = Net.DownloadMirror.MapUrl($"{lib.Url?.TrimEnd('/') ?? "https://libraries.minecraft.net"}/{path}"),
                    TargetPath = Path.Combine(Paths.LibrariesDir, path)
                });
            }
        }

        // 资源索引与资源对象
        if (version.AssetIndex is not null)
        {
            var indexId = version.AssetIndex.Id;
            var indexPath = Path.Combine(Paths.AssetsDir, "indexes", indexId + ".json");
            add(new Net.DownloadItem
            {
                Url = version.AssetIndex.Url,
                TargetPath = indexPath,
                Sha1 = version.AssetIndex.Sha1,
                Size = version.AssetIndex.Size
            });

            // 资源对象需索引文件就绪后才可枚举；此处若索引不存在，先下载后由上层补跑
            if (File.Exists(indexPath))
            {
                var objects = AssetManager.ReadIndex(indexPath);
                foreach (var (name, obj) in objects)
                {
                    var hash = obj.Hash;
                    var prefix = hash[..2];
                    add(new Net.DownloadItem
                    {
                        Url = $"https://resources.download.minecraft.net/{prefix}/{hash}",
                        TargetPath = Path.Combine(Paths.AssetsDir, "objects", prefix, hash),
                        Sha1 = hash,
                        Size = obj.Size
                    });
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        // 日志配置（log4j 等）
        if (version.Logging?.Client is not null)
        {
            var logFile = version.Logging.Client.File;
            add(new Net.DownloadItem
            {
                Url = logFile.Url,
                TargetPath = Path.Combine(Paths.AssetsDir, "log_configs", Path.GetFileName(new Uri(logFile.Url).LocalPath)),
                Sha1 = logFile.Sha1,
                Size = logFile.Size
            });
        }
    }

    private static readonly string _NativeArch =
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            is System.Runtime.InteropServices.Architecture.X86 ? "32" : "64";

    /// <summary>将 natives 包解压到版本目录。</summary>
    public static async Task ExtractNativesAsync(VersionJson version, string versionsRoot, CancellationToken ct)
    {
        // 与 jar 同理：natives 归属继承链根版本，避免加载器版本重复解压一份
        var rootId = ResolveRootVersionId(version);
        var nativeDir = Path.Combine(versionsRoot, rootId, "natives");
        Directory.CreateDirectory(nativeDir);

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;
            var classifier = lib.Natives?.GetValueOrDefault("windows");
            if (classifier is null) continue;

            classifier = classifier.Replace("${arch}", _NativeArch, StringComparison.Ordinal);
            var artifact = lib.Downloads?.Classifiers?.GetValueOrDefault(classifier);
            if (artifact?.Path is null) continue;

            var jarPath = Path.Combine(Paths.LibrariesDir, artifact.Path);
            if (!File.Exists(jarPath)) continue;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('/', '\\');

                    // 按 extract.exclude 跳过
                    if (lib.Extract?.Exclude is not null &&
                        lib.Extract.Exclude.Any(ex => name.EndsWith(ex, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var target = Path.Combine(nativeDir, Path.GetFileName(name));
                    if (File.Exists(target) && new FileInfo(target).Length == entry.Length) continue;

                    entry.ExtractToFile(target, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"解压 natives 失败 {jarPath}: {ex.Message}");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}




