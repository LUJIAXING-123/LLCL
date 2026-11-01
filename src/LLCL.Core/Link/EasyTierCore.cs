using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLCL.Core.Link;

/// <summary>
/// EasyTier 内核管理：版本检查、自动下载更新、进程启动/停止、状态查询。
/// </summary>
public sealed class EasyTierCore : IDisposable
{
    public const string RepoOwner = "EasyTier";
    public const string RepoName = "EasyTier";

    private Process? _process;
    private int _rpcPort;
    private bool _disposed;

    public string? Version { get; private set; }
    public string InstallDir => Path.Combine(Paths.EasyTierDir, Version ?? "unknown");
    public int RpcPort => _rpcPort;
    public bool IsRunning => _process is { HasExited: false };
    public event Action<string>? OnLog;

    public static string ArchName => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "i686",
        _ => "x86_64"
    };

    #region 下载 / 更新

    private sealed class GithubRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<GithubAsset> Assets { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;
    }

    private sealed class GithubAsset
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string Url { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>国内可用的 GitHub 加速前缀。</summary>
    public const string ProxyPrefix = "https://v6.gh-proxy.org/";

    /// <summary>查询 GitHub 最新版本号（不含 v 前缀）。优先走加速前缀，失败回退直连。</summary>
    public static async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var api = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        foreach (var url in new[] { ProxyPrefix + api, api })
        {
            try
            {
                var json = await Net.Http.GetStringAsync(url, 2, ct).ConfigureAwait(false);
                var release = JsonSerializer.Deserialize<GithubRelease>(json, LauncherConfig.JsonOptions);
                if (release?.TagName is { Length: > 0 })
                    return release.TagName.TrimStart('v', 'V');
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 EasyTier 版本失败 {url}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// 确保内核可用：已安装则直接返回，否则下载并解压最新版本。
    /// </summary>
    public static async Task<string> EnsureInstalledAsync(IProgress<(string, double)>? progress = null,
        CancellationToken ct = default)
    {
        var installed = GetInstalledVersions();
        var configured = LauncherConfig.Current.EasyTierVersion;

        if (!string.IsNullOrEmpty(configured) && installed.Contains(configured))
            return configured;

        if (installed.Count > 0)
            return installed.OrderByDescending(v => v).First();

        // 无本地内核：下载最新版
        var latest = await GetLatestVersionAsync(ct).ConfigureAwait(false) ?? "2.6.4";
        progress?.Report(("下载 EasyTier 内核", 0.05));
        await InstallVersionAsync(latest, progress, ct).ConfigureAwait(false);
        return latest;
    }

    public static List<string> GetInstalledVersions()
    {
        var result = new List<string>();
        if (!Directory.Exists(Paths.EasyTierDir)) return result;

        foreach (var dir in Directory.GetDirectories(Paths.EasyTierDir))
        {
            if (File.Exists(Path.Combine(dir, "easytier-core.exe")) &&
                File.Exists(Path.Combine(dir, "easytier-cli.exe")))
                result.Add(Path.GetFileName(dir));
        }

        return result;
    }

    /// <summary>下载并解压指定版本的 EasyTier。</summary>
    public static async Task InstallVersionAsync(string version, IProgress<(string, double)>? progress = null,
        CancellationToken ct = default)
    {
        var tag = $"v{version.TrimStart('v', 'V')}";
        var zipName = $"easytier-windows-{ArchName}-{tag}.zip";
        var githubUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/{zipName}";

        progress?.Report(($"下载 {zipName}", 0.1));

        var targetDir = Path.Combine(Paths.EasyTierDir, version.TrimStart('v', 'V'));
        Directory.CreateDirectory(targetDir);

        var tempZip = Path.Combine(Paths.CacheDir, zipName);
        Directory.CreateDirectory(Paths.CacheDir);

        // 优先使用加速前缀，失败时回退其他镜像与 GitHub 直连
        var urls = new[]
        {
            ProxyPrefix + githubUrl,
            $"https://gh-proxy.com/{githubUrl}",
            $"https://ghfast.top/{githubUrl}",
            githubUrl
        };

        Exception? last = null;
        var downloaded = false;
        foreach (var candidate in urls)
        {
            try
            {
                await _DownloadFileAsync(candidate, tempZip, p => progress?.Report(($"下载 {zipName}", 0.1 + p * 0.7)),
                    ct).ConfigureAwait(false);
                downloaded = true;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"EasyTier 下载失败 {candidate}: {ex.Message}");
            }
        }

        if (!downloaded)
            throw new IOException("EasyTier 内核下载失败，请检查网络", last);

        progress?.Report(("解压内核", 0.85));

        using (var zip = ZipFile.OpenRead(tempZip))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                // 只解压需要的文件
                var name = Path.GetFileName(entry.FullName);
                if (name.Length == 0) continue;
                entry.ExtractToFile(Path.Combine(targetDir, name), true);
            }
        }

        try { File.Delete(tempZip); } catch { }

        // 记录当前版本
        LauncherConfig.Current.EasyTierVersion = version.TrimStart('v', 'V');
        LauncherConfig.Current.Save();

        progress?.Report(("内核就绪", 1.0));
        Logger.Info($"EasyTier {version} 安装完成: {targetDir}");
    }

    /// <summary>检查更新：若远端版本比本地新，自动升级。</summary>
    public static async Task<string?> CheckUpdateAsync(bool force = false, CancellationToken ct = default)
    {
        if (!LauncherConfig.Current.AutoUpdateEasyTier && !force) return null;

        var latest = await GetLatestVersionAsync(ct).ConfigureAwait(false);
        if (latest is null) return null;

        var installed = GetInstalledVersions();
        if (installed.Contains(latest)) return null;

        Logger.Info($"发现 EasyTier 新版本 {latest}，开始自动更新");
        await InstallVersionAsync(latest, null, ct).ConfigureAwait(false);
        return latest;
    }

    private static async Task _DownloadFileAsync(string url, string target,
        Action<double>? onProgress, CancellationToken ct)
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
    }

    #endregion

    #region 进程控制

    private static int _NewPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = listener.LocalEndpoint is System.Net.IPEndPoint ep ? ep.Port : 0;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 启动 EasyTier 内核，加入/创建虚拟网络。
    /// </summary>
    public async Task StartAsync(string? version, RoomCode code, bool isHost, string displayName,
        int mcPort, CancellationToken ct = default)
    {
        Version = version ?? await EnsureInstalledAsync(null, ct).ConfigureAwait(false);
        var dir = InstallDir;

        var coreExe = Path.Combine(dir, "easytier-core.exe");
        var cliExe = Path.Combine(dir, "easytier-cli.exe");
        if (!File.Exists(coreExe))
            throw new FileNotFoundException($"EasyTier 内核缺失: {coreExe}");

        _rpcPort = _NewPort();

        var args = new List<string>
        {
            "--no-tun",
            "--multi-thread",
            "--enable-kcp-proxy",
            "--enable-quic-proxy",
            "--private-mode", "true",
            "--encryption-algorithm", "aes-gcm",
            "--compression", "zstd",
            "--network-name", code.NetworkName,
            "--network-secret", code.NetworkSecret,
            "--hostname", (isHost ? "H|" : "J|") + displayName,
            "--rpc-portal", $"127.0.0.1:{_rpcPort}",
            "--machine-id", GetMachineId()
        };

        if (!LauncherConfig.Current.EnableIPv6) args.Add("--disable-ipv6");
        if (!LauncherConfig.Current.AutoPunchSymmetricNat) args.Add("--disable-sym-hole-punching");
        if (LauncherConfig.Current.ForceRelay)
        {
            args.Add("--disable-p2p");
        }

        // 中继节点
        foreach (var node in _RelayNodes())
        {
            args.Add("--p");
            args.Add(node);
        }

        if (isHost)
        {
            args.Add("--i");
            args.Add(RoomCode.HostIp);
            args.Add("--tcp-whitelist");
            args.Add(mcPort.ToString());
            args.Add("--udp-whitelist");
            args.Add(mcPort.ToString());
        }
        else
        {
            args.Add("--tcp-whitelist");
            args.Add("0");
            args.Add("--udp-whitelist");
            args.Add("0");
        }

        Logger.Info("启动 EasyTier: " + string.Join(" ", args));

        var psi = new ProcessStartInfo
        {
            FileName = coreExe,
            WorkingDirectory = dir,
            Arguments = string.Join(" ", args.Select(_Quote)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLog?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnLog?.Invoke(e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static IEnumerable<string> _RelayNodes()
    {
        var nodes = new List<string>();
        var primary = LauncherConfig.Current.EasyTierRelay;
        if (!string.IsNullOrWhiteSpace(primary)) nodes.Add(primary);

        foreach (var node in LauncherConfig.Current.ExtraRelayNodes.Split([';', '\n', ','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (node.Contains("://", StringComparison.Ordinal)) nodes.Add(node);
        }

        // 默认公共节点（主节点不可用时仍有中继）
        nodes.Add("tcp://public2.easytier.cn:54321");

        return nodes.Distinct();
    }

    private static string _Quote(string arg)
        => arg.Any(c => c is ' ' or '"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    private static string GetMachineId()
    {
        try
        {
            var file = Path.Combine(Paths.DataRoot, "machine.id");
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (existing.Length > 0) return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Paths.DataRoot);
            File.WriteAllText(file, id);
            return id;
        }
        catch
        {
            return "llcl-default-id";
        }
    }

    /// <summary>查询在线成员。返回解析后的 peer 列表。</summary>
    public async Task<List<PeerInfo>> GetPeersAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return [];

        var cliExe = Path.Combine(InstallDir, "easytier-cli.exe");
        if (!File.Exists(cliExe)) return [];

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliExe,
                WorkingDirectory = InstallDir,
                Arguments = $"--rpc-portal 127.0.0.1:{_rpcPort} -o json peer",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var cli = Process.Start(psi);
            if (cli is null) return [];

            var output = await cli.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var err = await cli.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await cli.WaitForExitAsync(ct).ConfigureAwait(false);

            var json = output.Trim();
            if (json.Length == 0) json = err.Trim();
            if (json.Length == 0) return [];

            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start < 0 || end <= start) return [];

            var peers = JsonSerializer.Deserialize<List<PeerInfo>>(json[start..(end + 1)],
                LauncherConfig.JsonOptions) ?? [];
            return peers;
        }
        catch (Exception ex)
        {
            Logger.Warn($"获取 EasyTier 成员列表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>添加端口转发（加入方把远端 MC 端口映射到本地端口）。</summary>
    public async Task<int> AddPortForwardAsync(string targetIp, int targetPort, CancellationToken ct = default)
    {
        var localPort = _NewPort();
        var cliExe = Path.Combine(InstallDir, "easytier-cli.exe");
        if (!File.Exists(cliExe)) return localPort;

        foreach (var protocol in new[] { "tcp", "udp" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cliExe,
                    WorkingDirectory = InstallDir,
                    Arguments = $"--rpc-portal 127.0.0.1:{_rpcPort} port-forward add {protocol} 127.0.0.1:{localPort} {targetIp}:{targetPort}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var cli = Process.Start(psi);
                if (cli is not null) await cli.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"端口转发失败 ({protocol}): {ex.Message}");
            }
        }

        return localPort;
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"关闭 EasyTier 失败: {ex.Message}");
        }
        finally
        {
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    #endregion
}

/// <summary>EasyTier 节点信息。</summary>
public sealed class PeerInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("ipv4")]
    public string Ipv4 { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("cost")]
    public string Cost { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("ping")]
    public string Ping { get; set; } = "-";
    [System.Text.Json.Serialization.JsonPropertyName("loss")]
    public string Loss { get; set; } = "-";
    [System.Text.Json.Serialization.JsonPropertyName("nat_type")]
    public string? NatType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHost => Hostname.StartsWith("H|", StringComparison.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName
    {
        get
        {
            var parts = Hostname.Split('|');
            return parts.Length >= 2 ? parts[1] : Hostname;
        }
    }
}


