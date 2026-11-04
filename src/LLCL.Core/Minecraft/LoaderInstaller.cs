using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLCL.Core.Minecraft;

/// <summary>加载器版本条目。</summary>
public sealed class LoaderVersionInfo
{
    public string Version { get; init; } = string.Empty;
    public bool Stable { get; init; }
    public string? McVersion { get; init; }
}

/// <summary>
/// 模组加载器安装：Fabric / Quilt（meta API 直接生成版本 JSON）、
/// Forge / NeoForge（官方 installer 的 --installClient 无头模式）。
/// </summary>
public static class LoaderInstaller
{
    public enum Kind { Fabric, Quilt, Forge, NeoForge }

    /// <summary>单个来源的查询超时。不可达的源要尽快失败，避免拖慢界面。</summary>
    private static readonly TimeSpan LoaderQueryTimeout = TimeSpan.FromSeconds(8);

    // ---------------- 元数据查询 ----------------

    /// <summary>查询可用加载器版本。</summary>
    public static async Task<List<LoaderVersionInfo>> GetVersionsAsync(Kind kind, string mcVersion,
        CancellationToken ct = default)
    {
        return kind switch
        {
            Kind.Fabric => await _GetFabricVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            Kind.Quilt => await _GetQuiltVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            Kind.Forge => await _GetForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            Kind.NeoForge => await _GetNeoForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            _ => []
        };
    }

    private static async Task<List<LoaderVersionInfo>> _GetFabricVersionsAsync(string mc, CancellationToken ct)
    {
        // 镜像优先：官方源在国内经常完全不可达，放在后面只会白白等待超时
        var urls = new[]
        {
            $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mc}",
            $"https://meta.fabricmc.net/v2/versions/loader/{mc}"
        };

        return await _QueryFirstAsync(urls, url => _ParseFabricAsync(url, mc, ct), ct)
            .ConfigureAwait(false);
    }

    private static async Task<List<LoaderVersionInfo>> _ParseFabricAsync(string url, string mc,
        CancellationToken ct)
    {
        var json = await Net.Http.GetStringAsync(url, 1, LoaderQueryTimeout, ct).ConfigureAwait(false);
        if (JsonNode.Parse(json) is not JsonArray arr) return [];

        var list = new List<LoaderVersionInfo>();
        foreach (var item in arr)
        {
            var loader = item?["loader"]?["version"]?.GetValue<string>();
            if (loader is null) continue;
            var stable = item?["loader"]?["stable"]?.GetValue<bool>() ?? false;
            list.Add(new LoaderVersionInfo { Version = loader, Stable = stable, McVersion = mc });
        }

        // 稳定版优先排前面
        list.Sort((a, b) =>
        {
            if (a.Stable != b.Stable) return b.Stable.CompareTo(a.Stable);
            return string.Compare(b.Version, a.Version, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }

    /// <summary>
    /// 依次尝试多个来源，返回第一个成功且非空的结果。
    /// 每个来源单独带超时，避免单个不可达源拖住整体。
    /// </summary>
    private static async Task<List<LoaderVersionInfo>> _QueryFirstAsync(
        IEnumerable<string> urls, Func<string, Task<List<LoaderVersionInfo>>> fetch,
        CancellationToken ct)
    {
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await fetch(url).ConfigureAwait(false);
                if (result.Count > 0)
                {
                    Logger.Info($"加载器版本查询成功: {url}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"加载器版本查询失败 {url}: {ex.Message}");
            }
        }

        return [];
    }

    private static async Task<List<LoaderVersionInfo>> _GetQuiltVersionsAsync(string mc, CancellationToken ct)
    {
        // Quilt 没有国内镜像，只能直连；给较短超时避免长时间卡住
        try
        {
            // Quilt 无国内镜像，官方源可达性一般；给较短超时避免拖慢界面
            var json = await Net.Http.GetStringAsync(
                $"https://meta.quiltmc.org/v3/versions/loader/{mc}", 1,
                TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonArray arr) return [];

            var list = new List<LoaderVersionInfo>();
            foreach (var item in arr)
            {
                var version = item?["loader"]?["version"]?.GetValue<string>() ?? item?["version"]?.GetValue<string>();
                if (version is null) continue;
                list.Add(new LoaderVersionInfo { Version = version, Stable = true, McVersion = mc });
            }

            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"获取 Quilt 版本失败: {ex.Message}");
            return [];
        }
    }

    private static async Task<List<LoaderVersionInfo>> _GetForgeVersionsAsync(string mc, CancellationToken ct)
    {
        // 镜像优先；官方 maven 在国内基本不可达
        var urls = new[]
        {
            "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml",
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"
        };

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var xml = await Net.Http.GetStringAsync(url, 1, LoaderQueryTimeout, ct).ConfigureAwait(false);
                var versions = System.Text.RegularExpressions.Regex
                    .Matches(xml, "<version>([^<]+)</version>")
                    .Select(m => m.Groups[1].Value)
                    .Where(v => v.StartsWith(mc + "-", StringComparison.Ordinal))
                    .Select(v => v[(mc.Length + 1)..])
                    .Distinct()
                    .ToList();

                if (versions.Count == 0) continue;

                Logger.Info($"Forge 版本查询成功: {url}");
                return versions.Select(v => new LoaderVersionInfo
                {
                    Version = v,
                    Stable = true,
                    McVersion = mc
                }).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 Forge 版本失败 {url}: {ex.Message}");
            }
        }

        return [];
    }

    private sealed class NeoForgeEntry
    {
        public string Version { get; set; } = string.Empty;
    }

    private static async Task<List<LoaderVersionInfo>> _GetNeoForgeVersionsAsync(string mc, CancellationToken ct)
    {
        var urls = new[]
        {
            "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge",
            "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge"
        };

        // NeoForge 版本号与 MC 版本的映射规则：
        //   MC 1.21.1 → NeoForge 21.1.x   （去掉 "1." 前缀，保留 21.1）
        //   MC 1.20.4 → NeoForge 20.4.x
        //   MC 1.20.2 → NeoForge 20.2.x
        // 注意不是简单地取前两段：1.21.1 的前两段是 "1.21"，而 NeoForge 用的是 "21.1"。
        var prefix = BuildNeoForgePrefix(mc);

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await Net.Http.GetStringAsync(url, 1, LoaderQueryTimeout, ct).ConfigureAwait(false);
                List<string> versions;

                var node = JsonNode.Parse(json);
                if (node is JsonObject obj)
                {
                    // 两种返回格式：
                    //   { "versions": ["21.1.248", ...] }                    —— 官方 maven versions API
                    //   { "files": [ { "name": "21.1.248", ... }, ... ] }    —— BMCLAPI 目录列表 API
                    if (obj["versions"] is JsonArray arr)
                        versions = arr.Select(v => v?.GetValue<string>() ?? string.Empty)
                            .Where(v => v.Length > 0).ToList();
                    else if (obj["files"] is JsonArray files)
                        versions = files.Select(f => f?["name"]?.GetValue<string>() ?? string.Empty)
                            .Where(v => v.Length > 0).ToList();
                    else
                        continue;
                }
                else if (node is JsonArray plain)
                {
                    versions = plain.Select(f => f?["name"]?.GetValue<string>() ?? string.Empty)
                        .Where(v => v.Length > 0).ToList();
                }
                else
                {
                    continue;
                }

                if (prefix is not null)
                    versions = versions.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).ToList();

                versions = versions.Where(v => !v.Contains('-')).Distinct().ToList();

                if (versions.Count == 0)
                {
                    Logger.Warn($"NeoForge 源 {url} 解析得到 0 个版本（prefix={prefix}）");
                    continue;
                }

                Logger.Info($"NeoForge 版本查询成功: {url}，{versions.Count} 个");
                return versions.Select(v => new LoaderVersionInfo
                {
                    Version = v,
                    Stable = true,
                    McVersion = mc
                }).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 NeoForge 版本失败 {url}: {ex.Message}");
            }
        }

        return [];
    }

    // ---------------- 安装 ----------------

    /// <summary>
    /// 安装加载器。需要基础原版版本已安装。返回新版本 ID。
    /// </summary>
    public static async Task<string> InstallAsync(Kind kind, string mcVersion, string loaderVersion,
        IProgress<(string step, double ratio)>? progress = null, CancellationToken ct = default)
    {
        return kind switch
        {
            Kind.Fabric => await _InstallFabricAsync(mcVersion, loaderVersion, "fabric", progress, ct)
                .ConfigureAwait(false),
            Kind.Quilt => await _InstallFabricAsync(mcVersion, loaderVersion, "quilt", progress, ct)
                .ConfigureAwait(false),
            Kind.Forge => await _InstallForgeAsync(mcVersion, loaderVersion, false, progress, ct)
                .ConfigureAwait(false),
            Kind.NeoForge => await _InstallForgeAsync(mcVersion, loaderVersion, true, progress, ct)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>Fabric / Quilt：meta API 直接返回完整的 profile JSON。</summary>
    private static async Task<string> _InstallFabricAsync(string mcVersion, string loaderVersion,
        string loaderName, IProgress<(string step, double ratio)>? progress, CancellationToken ct)
    {
        progress?.Report(("获取加载器配置", 0.1));

        var urls = loaderName == "fabric"
            ? new[]
            {
                $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json"
            }
            : new[]
            {
                $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{loaderVersion}/profile/json"
            };

        string? profileJson = null;
        Exception? last = null;

        foreach (var url in urls)
        {
            try
            {
                profileJson = await Net.Http.GetStringAsync(url, 2, ct).ConfigureAwait(false);
                if (profileJson.Length > 0) break;
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"获取 {loaderName} profile 失败 {url}: {ex.Message}");
            }
        }

        if (profileJson is null)
            throw new IOException($"无法获取 {loaderName} 配置", last);

        var version = ParseProfile(profileJson, mcVersion, loaderName, loaderVersion);
        var versionId = $"{mcVersion}-{_LoaderLabel(loaderName)}-{loaderVersion}";

        progress?.Report(($"写入版本 {versionId}", 0.4));

        SaveVersion(version, versionId);

        // 下载该 profile 新增的库（Fabric API 等基础库）
        progress?.Report(("下载加载器库", 0.5));
        await _DownloadLibrariesAsync(version, p => progress?.Report(("下载加载器库", 0.5 + p * 0.45)), ct)
            .ConfigureAwait(false);

        progress?.Report(("安装完成", 1.0));
        return versionId;
    }

    /// <summary>解析 profile JSON，并改写为继承原版的版本。</summary>
    private static VersionJson ParseProfile(string json, string mcVersion, string loaderName,
        string loaderVersion)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidDataException("profile JSON 为空");
        if (node is not JsonObject obj) throw new InvalidDataException("profile JSON 格式错误");

        // 关键：让 profile 继承原版版本，从而复用原版的 jar、assets、参数
        obj["inheritsFrom"] = mcVersion;
        obj["id"] = $"{mcVersion}-{_LoaderLabel(loaderName)}-{loaderVersion}";
        obj["type"] = "release";

        // Fabric 的 profile 有时用 "libraries" 里带 url 的条目，统一成标准格式
        if (obj["libraries"] is JsonArray libs)
        {
            foreach (var lib in libs)
            {
                if (lib is not JsonObject libObj) continue;
                if (libObj["downloads"] is null && libObj["url"] is JsonValue urlValue)
                {
                    // 保留 url，由下载器解析
                    var url = urlValue.GetValue<string>();
                    var name = libObj["name"]?.GetValue<string>();
                    if (name is null) continue;
                    var coord = MavenCoordinate.Parse(name);
                    var artifact = new JsonObject
                    {
                        ["path"] = coord.ToPath(),
                        ["url"] = $"{url.TrimEnd('/')}/{coord.ToPath()}",
                        ["size"] = 0
                    };
                    libObj["downloads"] = new JsonObject { ["artifact"] = artifact };
                }
            }
        }

        return VersionLoader.Parse(obj.ToJsonString());
    }

    private static async Task _DownloadLibrariesAsync(VersionJson version, Action<double>? onProgress,
        CancellationToken ct)
    {
        var items = new List<Net.DownloadItem>();

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;

            var artifact = lib.Downloads?.Artifact;
            if (artifact is null || string.IsNullOrEmpty(artifact.Url)) continue;

            items.Add(new Net.DownloadItem
            {
                Url = artifact.Url,
                TargetPath = Path.Combine(Paths.LibrariesDir, artifact.Path ?? lib.Coordinate!.ToPath()),
                Sha1 = artifact.Sha1,
                Size = artifact.Size
            });
        }

        if (items.Count == 0) return;

        var downloader = new Net.FileDownloader();
        downloader.Progress += (_, e) =>
        {
            if (e.TotalBytes > 0) onProgress?.Invoke((double)e.DownloadedBytes / e.TotalBytes);
        };

        await downloader.DownloadAllAsync(items, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forge / NeoForge：下载官方 installer，以 --installClient 无头模式执行。
    /// 安装器会自行把版本 JSON 写入 versions 目录。
    /// </summary>
    private static async Task<string> _InstallForgeAsync(string mcVersion, string loaderVersion,
        bool neo, IProgress<(string step, double ratio)>? progress, CancellationToken ct)
    {
        var label = neo ? "NeoForge" : "Forge";
        var fullVersion = neo ? loaderVersion : $"{mcVersion}-{loaderVersion}";

        progress?.Report(($"下载 {label} 安装器", 0.05));

        var installerUrl = neo
            ? $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar"
            : $"https://maven.minecraftforge.net/net/minecraftforge/forge/{fullVersion}/forge-{fullVersion}-installer.jar";

        var installers = neo
            ? new[]
            {
                $"https://bmclapi2.bangbang93.com/neoforge/version/{loaderVersion}/download/installer",
                $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar"
            }
            : new[]
            {
                $"https://bmclapi2.bangbang93.com/forge/download?mcversion={mcVersion}&version={loaderVersion}&category=installer&format=jar",
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{fullVersion}/forge-{fullVersion}-installer.jar"
            };

        var installerPath = Path.Combine(Paths.CacheDir, $"{label.ToLowerInvariant()}-{fullVersion}-installer.jar");
        Directory.CreateDirectory(Paths.CacheDir);

        Exception? last = null;
        var downloaded = false;

        foreach (var url in installers)
        {
            try
            {
                await _DownloadFileAsync(url, installerPath,
                    p => progress?.Report(($"下载 {label} 安装器", 0.05 + p * 0.55)), ct).ConfigureAwait(false);
                downloaded = true;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"{label} 安装器下载失败 {url}: {ex.Message}");
            }
        }

        if (!downloaded)
            throw new IOException($"{label} 安装器下载失败，请检查网络或切换下载源", last);

        progress?.Report(($"运行 {label} 安装器", 0.65));

        // 选择 Java：Forge/NeoForge 安装器需要与目标 MC 匹配的 Java
        var requiredJava = 8;
        try
        {
            requiredJava = VersionLoader.Load(mcVersion).JavaVersion?.MajorVersion ?? 8;
        }
        catch
        {
            // 原版未安装时使用默认值
        }

        var javaPath = JavaScanner.SelectForVersion(requiredJava)?.Path;
        if (javaPath is null)
            throw new InvalidOperationException($"{label} 安装需要 Java {requiredJava}，但未找到对应运行时");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = $"-jar \"{installerPath}\" --installClient \"{Paths.MinecraftDir}\"",
            WorkingDirectory = Paths.MinecraftDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Logger.Info($"运行安装器: {javaPath} -jar {installerPath} --installClient {Paths.MinecraftDir}");

        using var process = System.Diagnostics.Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动安装器进程");

        var output = new System.Text.StringBuilder();
        var readTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                output.AppendLine(line);
                Logger.Info("[安装器] " + line);
            }
        }, ct);

        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                output.AppendLine(line);
                Logger.Warn("[安装器] " + line);
            }
        }, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(readTask, errTask).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new IOException($"{label} 安装器退出码 {process.ExitCode}：\n{output}");

        TryDelete(installerPath);

        progress?.Report(("查找安装结果", 0.95));

        // 安装器写入的版本目录：Forge 为 <mc>-forge-<ver>，NeoForge 为 neoforge-<ver>
        var expected = neo ? $"neoforge-{loaderVersion}" : $"{fullVersion}";
        var candidates = Directory.Exists(Paths.VersionsDir)
            ? Directory.GetDirectories(Paths.VersionsDir).Select(Path.GetFileName).ToList()
            : [];

        var match = candidates.FirstOrDefault(c =>
                        c is not null &&
                        (c.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                         c.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)))
                    ?? throw new IOException($"{label} 安装完成但未找到版本目录（期望 {expected}）");

        progress?.Report(("安装完成", 1.0));
        return match;
    }

    private static async Task _DownloadFileAsync(string url, string target, Action<double>? onProgress,
        CancellationToken ct)
    {
        using var resp = await Net.Http.Client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(target);

        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (total > 0) onProgress?.Invoke((double)written / total);
        }

        if (total > 0 && written != total)
            throw new IOException($"下载不完整 ({written}/{total})");
    }

    private static string _LoaderLabel(string loaderName)
        => loaderName switch
        {
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            _ => loaderName
        };

    /// <summary>保存版本 JSON（同时修正 id 字段）。</summary>
    public static void SaveVersion(VersionJson version, string versionId)
    {
        var dir = Path.Combine(Paths.VersionsDir, versionId);
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(version, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        File.WriteAllText(Path.Combine(dir, versionId + ".json"), json);
        Logger.Info($"已写入版本: {versionId}");
    }

    /// <summary>
    /// 把 MC 版本映射为 NeoForge 版本前缀。
    /// 规则：去掉 "1." 前缀后，取前两段。
    ///   1.21.1  → 21.1.
    ///   1.20.4  → 20.4.
    ///   1.19.2  → 19.2.
    /// </summary>
    private static string? BuildNeoForgePrefix(string mcVersion)
    {
        // 先去掉 "1." 这样的主版本前缀（旧式版本号）
        var normalized = mcVersion.StartsWith("1.", StringComparison.Ordinal)
            ? mcVersion[2..]
            : mcVersion;

        var parts = normalized.Split('.');
        if (parts.Length < 2) return null;

        return $"{parts[0]}.{parts[1]}.";
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}






