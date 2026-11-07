using System.Security.Cryptography;
using System.Text;

namespace LLCL.Core.Minecraft;

/// <summary>离线账号：UUID 由名字离线生成（与原版一致）。</summary>
public sealed class OfflineAccount
{
    public string Name { get; init; } = "Player";
    public string Uuid { get; init; } = string.Empty;
    public string AccessToken { get; init; } = "0";

    public static OfflineAccount Create(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + clean));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var uuid = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        return new OfflineAccount { Name = clean, Uuid = uuid, AccessToken = "0" };
    }

    public static string UuidToUndashed(string uuid) => uuid.Replace("-", string.Empty);
}

/// <summary>启动账号信息（统一模型）。</summary>
public sealed record LaunchAccount(string Name, string Uuid, string AccessToken, string Type)
{
    public bool IsMicrosoft => Type == "microsoft";
    public string UuidUndashed => Uuid.Replace("-", string.Empty);

    public static LaunchAccount Offline(string name)
    {
        var a = OfflineAccount.Create(name);
        return new LaunchAccount(a.Name, a.Uuid, a.AccessToken, "offline");
    }
}

/// <summary>
/// 直接按版本启动。没有“实例”抽象，版本目录即游戏运行目录。
/// </summary>
public sealed class GameLauncher
{
    public sealed class LaunchOptions
    {
        public LaunchAccount Account { get; set; } = LaunchAccount.Offline("Player");
        public string JavaPath { get; set; } = string.Empty;
        public int MaxMemoryMb { get; set; }
        public int MinMemoryMb { get; set; } = 512;
        public string[] ExtraJvmArgs { get; set; } = [];
        public string[] ExtraGameArgs { get; set; } = [];
        public bool Fullscreen { get; set; }
        public int Width { get; set; } = 854;
        public int Height { get; set; } = 480;
        public string? ServerHost { get; set; }
        public int? ServerPort { get; set; }
    }

    /// <summary>启动指定版本，返回 Java 进程。</summary>
    public static Task<System.Diagnostics.Process> LaunchAsync(string versionId, string gameDir,
        LaunchOptions options, CancellationToken ct = default)
    {
        var version = VersionLoader.Load(versionId);
        Directory.CreateDirectory(gameDir);

        var javaExe = options.JavaPath;
        if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe))
            throw new InvalidOperationException("未找到可用的 Java 运行时，请在设置中指定 Java 路径");

        // 继承链顶层版本决定 jar 与 natives 位置
        var rootId = versionId;
        var cursor = version;
        while (!string.IsNullOrEmpty(cursor.InheritsFrom))
        {
            rootId = cursor.InheritsFrom;
            cursor = VersionLoader.Load(rootId);
        }

        var assetIndexId = version.AssetIndex?.Id ?? version.Assets;
        AssetManager.CreateVirtualAssets(assetIndexId, gameDir, ct);

        var placeholder = BuildPlaceholders(versionId, gameDir, options, rootId, assetIndexId);

        var args = new List<string> { "-XX:+UnlockExperimentalVMOptions" };
        if (options.MaxMemoryMb > 0) args.Add($"-Xmx{options.MaxMemoryMb}m");
        args.Add($"-Xmn{Math.Max(128, options.MinMemoryMb)}m");
        args.Add("-Dfile.encoding=UTF-8");
        args.Add($"-Djava.library.path={Path.Combine(Paths.VersionsDir, rootId, "natives")}");
        args.Add("-Dminecraft.launcher.brand=LLCL");
        args.Add("-Dminecraft.launcher.version=3.0");

        // 版本自带的 jvm 参数通常已经包含 -cp 与 classpath，
        // 此时手动再加一次会导致命令行出现两个 -cp，游戏可能读到错误的那个。
        var hasClasspathInArguments =
            version.Arguments?.Jvm.Any(o => o is string s &&
                (s == "-cp" || s == "--class-path")) ?? false;

        if (!hasClasspathInArguments)
        {
            args.Add("-cp");
            args.Add(placeholder["classpath"]);
        }

        if (version.Arguments is not null)
        {
            foreach (var raw in ArgumentEvaluator.Flatten(version.Arguments.Jvm))
            {
                var value = ArgumentEvaluator.Substitute(raw, placeholder);
                if (value.Contains("${", StringComparison.Ordinal)) continue;
                args.Add(value);
            }
        }

        foreach (var extra in options.ExtraJvmArgs)
            if (!string.IsNullOrWhiteSpace(extra)) args.Add(extra);

        args.Add(version.MainClass);

        if (version.Arguments is not null && version.Arguments.Game.Count > 0)
        {
            foreach (var raw in ArgumentEvaluator.Flatten(version.Arguments.Game))
            {
                var value = ArgumentEvaluator.Substitute(raw, placeholder);
                if (value.Contains("${", StringComparison.Ordinal)) continue;
                args.Add(value);
            }
        }
        else if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            foreach (var part in version.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(ArgumentEvaluator.Substitute(part, placeholder));
        }

        if (options.Fullscreen) args.Add("--fullscreen");
        else
        {
            args.Add("--width");
            args.Add(options.Width.ToString());
            args.Add("--height");
            args.Add(options.Height.ToString());
        }

        if (options.ServerHost is not null && options.ServerPort is not null)
        {
            args.Add("--server");
            args.Add(options.ServerHost);
            args.Add("--port");
            args.Add(options.ServerPort.Value.ToString());
        }

        foreach (var extra in options.ExtraGameArgs)
            if (!string.IsNullOrWhiteSpace(extra)) args.Add(extra);

        Logger.Info($"启动版本 {versionId}，Java: {javaExe}");
        Logger.Info("参数: " + string.Join(" ", args));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = gameDir,
            Arguments = EscapeArguments(args),
            UseShellExecute = false,
            CreateNoWindow = false
        };

        var process = System.Diagnostics.Process.Start(psi)
                      ?? throw new InvalidOperationException("无法启动 Java 进程");

        return Task.FromResult(process);
    }

    /// <summary>
    /// 版本的游戏运行目录。判定逻辑见 <see cref="VersionIsolation" />：
    /// 手动设置优先 → 已有 mods/saves 自动开启 → 全局方案。
    /// </summary>
    public static string GetGameDir(string versionId) => VersionIsolation.GetGameDir(versionId);

    /// <summary>显式指定是否隔离（供需要绕过判定的调用点使用）。</summary>
    public static string GetGameDir(string versionId, bool isolated)
        => isolated
            ? Path.Combine(Paths.VersionsDir, versionId)
            : Paths.MinecraftDir;

    private static Dictionary<string, string> BuildPlaceholders(string versionId, string gameDir,
        LaunchOptions options, string rootId, string assetIndexId)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = options.Account.Name,
            ["auth_uuid"] = options.Account.UuidUndashed,
            ["auth_access_token"] = options.Account.AccessToken,
            ["auth_session"] = $"token:{options.Account.AccessToken}:{options.Account.UuidUndashed}",
            ["user_type"] = options.Account.IsMicrosoft ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["game_directory"] = gameDir,
            ["assets_root"] = Paths.AssetsDir,
            ["assets_index_name"] = assetIndexId,
            ["version_name"] = versionId,
            ["version_type"] = "release",
            ["game_assets"] = Path.Combine(Paths.AssetsDir, "virtual", "legacy"),
            ["natives_directory"] = Path.Combine(Paths.VersionsDir, rootId, "natives"),
            ["launcher_name"] = "LLCL",
            ["launcher_version"] = "3.0",
            ["classpath"] = BuildClassPath(LoadChain(versionId)),
            ["library_directory"] = Paths.LibrariesDir,
            ["classpath_separator"] = Path.DirectorySeparatorChar.ToString(),
            ["primary_jar"] = Path.Combine(Paths.VersionsDir, rootId, rootId + ".jar"),
            ["clientid"] = "LLCL",
            ["auth_xuid"] = "0",
            ["resolution_width"] = options.Width.ToString(),
            ["resolution_height"] = options.Height.ToString()
        };
    }

    private static List<VersionJson> LoadChain(string versionId)
    {
        var chain = new List<VersionJson>();
        var current = VersionLoader.Load(versionId);
        while (true)
        {
            chain.Add(current);
            if (string.IsNullOrEmpty(current.InheritsFrom)) break;
            try
            {
                current = VersionLoader.Load(current.InheritsFrom);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"加载父版本失败: {current.InheritsFrom}");
                break;
            }
        }

        return chain;
    }

    private static string BuildClassPath(List<VersionJson> chain)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootId = chain[^1].Id;

        var jar = Path.Combine(Paths.VersionsDir, rootId, rootId + ".jar");
        if (!File.Exists(jar))
        {
            var alt = Path.Combine(Paths.VersionsDir, rootId, "client.jar");
            if (File.Exists(alt)) jar = alt;
        }

        if (File.Exists(jar)) entries.Add(jar);

        foreach (var version in chain)
        {
            foreach (var lib in version.Libraries)
            {
                if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;
                if (lib.Natives is not null && lib.Natives.Count > 0) continue;

                var path = lib.Downloads?.Artifact?.Path ?? lib.Coordinate?.ToPath();
                if (path is null) continue;

                var full = Path.Combine(Paths.LibrariesDir, path);
                if (!File.Exists(full)) continue;
                if (!seen.Add(Path.GetFileName(full))) continue;
                entries.Add(full);
            }
        }

        return string.Join(Path.PathSeparator, entries);
    }

    private static string EscapeArguments(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (arg.Length == 0)
            {
                sb.Append("\"\"");
                continue;
            }

            if (!arg.Any(c => c is ' ' or '\t' or '"'))
            {
                sb.Append(arg);
                continue;
            }

            sb.Append('"');
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);

                sb.Append(c);
                backslashes = 0;
            }

            sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }

        return sb.ToString();
    }
}
