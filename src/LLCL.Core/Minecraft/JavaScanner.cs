using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LLCL.Core.Minecraft;

public sealed record JavaRuntime(string Path, int MajorVersion, string Vendor, string Arch, string FullVersion)
{
    public string Display => $"Java {MajorVersion} ({Vendor} {Arch})";
}

/// <summary>
/// Java 运行时扫描：注册表、常见安装目录、PATH，并解析版本号。
/// </summary>
public static class JavaScanner
{
    private static readonly Regex VersionRegex = new(@"(?:version|openjdk)\s*""?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);

    private static List<JavaRuntime>? _cached;
    private static Task<List<JavaRuntime>>? _scanTask;
    private static readonly object _scanLock = new();

    /// <summary>
    /// 异步扫描（带缓存与去重）。UI 线程调用此方法不会阻塞。
    /// </summary>
    public static Task<List<JavaRuntime>> ScanAsync(bool forceRefresh = false)
    {
        lock (_scanLock)
        {
            if (!forceRefresh && _cached is not null) return Task.FromResult(_cached);
            if (!forceRefresh && _scanTask is not null) return _scanTask;

            _scanTask = Task.Run(() => _ScanCore(forceRefresh));
            return _scanTask;
        }
    }

    /// <summary>同步扫描。不要在 UI 线程上使用（会阻塞数秒），请改用 <see cref="ScanAsync"/>。</summary>
    public static IReadOnlyList<JavaRuntime> Scan(bool forceRefresh = false)
        => _cached is not null && !forceRefresh ? _cached : _ScanCore(forceRefresh);

    private static List<JavaRuntime> _ScanCore(bool forceRefresh)
    {
        if (!forceRefresh && _cached is not null) return _cached;

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in _EnumerateCandidates())
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var full = Path.GetFullPath(candidate);
                if (seen.Add(full)) candidates.Add(full);
            }
            catch
            {
                // 忽略单个候选的异常
            }
        }

        // 并行调用 java -version，避免逐个等待造成数秒卡顿
        var parsed = candidates
            .AsParallel()
            .WithDegreeOfParallelism(Math.Clamp(candidates.Count, 1, 8))
            .Select(Parse)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        // 去重分两层：
        // 1) 同一 bin 目录下的 java.exe 与 javaw.exe 属于同一套 JDK
        // 2) latest\jdk-26 与 jdk-26.0.2 可能是同为 26.0.2 的两份独立副本，
        //    对启动游戏而言等价，只保留更易识别的一个
        var result = parsed
            .GroupBy(r => _RealBinDir(r.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(ScoreEntry).First())
            .GroupBy(r => $"{r.MajorVersion}|{r.FullVersion}|{r.Arch}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(ScoreEntry).First())
            .ToList();

        result.Sort((a, b) => b.MajorVersion.CompareTo(a.MajorVersion));
        _cached = result;
        return result;
    }

    /// <summary>
    /// 解析符号链接后所在 bin 目录的真实路径，用于识别重复的 Java 安装。
    /// 解析失败时退回原始目录。
    /// </summary>
    private static string _RealBinDir(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir is null) return exePath;

            var info = new DirectoryInfo(dir);

            // Windows 的 junction / symlink 需要逐级解析才能拿到最终目标
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            while (target is not null)
            {
                var parent = target.ResolveLinkTarget(returnFinalTarget: true);
                if (parent is null) break;
                target = parent;
            }

            return (target?.FullName ?? info.FullName).ToLowerInvariant();
        }
        catch
        {
            return exePath.ToLowerInvariant();
        }
    }

    /// <summary>
    /// 同一 JDK 的多个入口之间的优先级：越靠前越优先展示。
    /// 优先 javaw.exe，其次避开 latest 软链接目录。
    /// </summary>
    private static int ScoreEntry(JavaRuntime r)
    {
        var score = 0;

        if (!r.Path.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase)) score += 10;
        if (r.Path.Contains(@"\latest\", StringComparison.OrdinalIgnoreCase)) score += 5;

        return score;
    }
    private static IEnumerable<string> _EnumerateCandidates()
    {
        // 1. 用户指定
        var manual = LauncherConfig.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) yield return manual;

        // 2. PATH 中的 java（javaw 与 java 都接受）
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exe in _JavaExesIn(dir.Trim()))
                yield return exe;
        }

        // 3. 注册表：绝大多数 JDK 安装器只写注册表，
        //    仅靠固定目录会漏掉 Oracle / Zulu / Corretto / 部分 Temurin 安装。
        foreach (var exe in _FromRegistry())
            yield return exe;

        // 4. 常见安装目录（递归查找）
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new[]
        {
            Path.Combine(pf, "Java"),
            Path.Combine(pf, "Eclipse Adoptium"),
            Path.Combine(pf, "Eclipse Foundation"),
            Path.Combine(pf, "Microsoft"),
            Path.Combine(pf, "Zulu"),
            Path.Combine(pf, "BellSoft"),
            Path.Combine(pf, "Amazon Corretto"),
            Path.Combine(pf, "Semeru"),
            Path.Combine(pf, "RedHat"),
            Path.Combine(pf, "JetBrains"),
            Path.Combine(pf86, "Java"),
            Path.Combine(pf86, "Zulu"),
            // 用户级安装（Scoop、包管理器、IDE 自带）
            Path.Combine(localApp, "Programs"),
            Path.Combine(localApp, "Eclipse Adoptium"),
            Path.Combine(localApp, "Microsoft"),
            Path.Combine(localApp, "Programs", "Eclipse Adoptium"),
            // 启动器自带的运行时
            Path.Combine(Paths.DataRoot, "java"),
            Path.Combine(Paths.RuntimeDir, "java"),
            Paths.RuntimeDir
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            // 限制深度，避免在巨大的目录树下长时间扫描
            foreach (var exe in _EnumerateJavaExes(root, maxDepth: 4))
                yield return exe;
        }
    }

    /// <summary>列出某目录下的 javaw.exe / java.exe（不含递归）。</summary>
    private static IEnumerable<string> _JavaExesIn(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) yield break;

        string[] names = ["javaw.exe", "java.exe"];
        foreach (var name in names)
        {
            var full = Path.Combine(dir, name);
            bool exists;
            try { exists = File.Exists(full); } catch { exists = false; }
            if (exists) yield return full;
        }
    }

    /// <summary>
    /// 递归查找含 java 可执行文件的目录，限制深度以防扫描过久。
    /// 同一个 bin 目录下 javaw 与 java 只取一个，避免重复解析。
    /// </summary>
    private static IEnumerable<string> _EnumerateJavaExes(string root, int maxDepth)
    {
        var dirs = new List<string> { root };
        var current = new List<string> { root };
        var depth = 0;

        while (depth < maxDepth && current.Count > 0)
        {
            var next = new List<string>();

            foreach (var dir in current)
            {
                foreach (var exe in _JavaExesIn(dir))
                    yield return exe;

                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(dir); }
                catch { continue; }

                foreach (var child in children) next.Add(child);
            }

            current = next;
            depth++;
        }
    }

    /// <summary>
    /// 从注册表读取 Java 安装路径。
    /// Oracle / Zulu / Corretto / Semeru 等安装器会把路径写入 JavaSoft 键，
    /// 这是固定目录扫描最容易漏掉的一批。
    /// </summary>
    private static IEnumerable<string> _FromRegistry()
    {
        var results = new List<string>();

        if (!OperatingSystem.IsWindows()) return results;

        string[] roots =
        [
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit",
            @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment"
        ];

        foreach (var hiveName in new[] { "HKLM", "HKCU" })
        {
            foreach (var root in roots)
            {
                try
                {
                    using var hive = hiveName == "HKLM"
                        ? Microsoft.Win32.Registry.LocalMachine
                        : Microsoft.Win32.Registry.CurrentUser;

                    using var key = hive.OpenSubKey(root);
                    if (key is null) continue;

                    // 子键是版本号，每个版本下通常有 JavaHome / Home
                    foreach (var versionName in key.GetSubKeyNames())
                    {
                        using var versionKey = key.OpenSubKey(versionName);
                        if (versionKey is null) continue;

                        var home = versionKey.GetValue("JavaHome") as string
                                   ?? versionKey.GetValue("Home") as string;

                        if (string.IsNullOrWhiteSpace(home)) continue;

                        var binDir = Path.Combine(home.Trim().TrimEnd('\\'), "bin");
                        foreach (var exe in _JavaExesIn(binDir))
                            results.Add(exe);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"读取 Java 注册表失败 {hiveName}\\{root}: {ex.Message}");
                }
            }
        }

        return results;
    }

    /// <summary>执行 java -version 解析版本信息。</summary>
    public static JavaRuntime? Parse(string javaExe)
    {
        // javaw.exe 是 GUI 版本，某些发行版下它不把输出写到管道，
        // 因此失败时回退到同目录的 java.exe 再试一次。
        var runtime = _ParseWith(javaExe);
        if (runtime is not null) return runtime;

        var fileName = Path.GetFileName(javaExe);
        if (fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var console = Path.Combine(Path.GetDirectoryName(javaExe) ?? "", "java.exe");
            if (File.Exists(console))
            {
                var fallback = _ParseWith(console);
                if (fallback is not null) return fallback with { Path = javaExe };
            }
        }

        return null;
    }

    private static JavaRuntime? _ParseWith(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            var major = _ParseMajor(output);
            if (major <= 0) return null;

            var vendor = "Unknown";
            if (output.Contains("Temurin", StringComparison.OrdinalIgnoreCase)) vendor = "Temurin";
            else if (output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) vendor = "Microsoft";
            else if (output.Contains("Zulu", StringComparison.OrdinalIgnoreCase)) vendor = "Zulu";
            else if (output.Contains("Corretto", StringComparison.OrdinalIgnoreCase)) vendor = "Corretto";
            else if (output.Contains("Oracle", StringComparison.OrdinalIgnoreCase)) vendor = "Oracle";
            else if (output.Contains("BellSoft", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("Liberica", StringComparison.OrdinalIgnoreCase)) vendor = "Liberica";
            else if (output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("AdoptOpenJDK", StringComparison.OrdinalIgnoreCase)) vendor = "Temurin";
            // Oracle 新版不再打印品牌名，只输出 "Java(TM) SE Runtime Environment"，
            // 因此用该特征识别，避免全部归类为 Unknown
            else if (output.Contains("Java(TM)", StringComparison.OrdinalIgnoreCase)) vendor = "Oracle";
            else if (output.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)) vendor = "OpenJDK";
            else if (output.Contains("Semeru", StringComparison.OrdinalIgnoreCase)) vendor = "Semeru";

            var arch = output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";
            var match = VersionRegex.Match(output);
            var full = match.Success ? match.Groups[1].Value : major.ToString();

            return new JavaRuntime(javaExe, major, vendor, arch, full);
        }
        catch (Exception ex)
        {
            Logger.Warn($"解析 Java 失败 {javaExe}: {ex.Message}");
            return null;
        }
    }

    private static int _ParseMajor(string output)
    {
        var match = Regex.Match(output, @"version\s*""(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var value = int.Parse(match.Groups[1].Value);
            // 1.8 形式
            if (value == 1)
            {
                var legacy = Regex.Match(output, @"version\s*""1\.(\d+)", RegexOptions.IgnoreCase);
                if (legacy.Success) return int.Parse(legacy.Groups[1].Value);
            }

            return value;
        }

        var alt = Regex.Match(output, @"openjdk\s+(\d+)", RegexOptions.IgnoreCase);
        return alt.Success ? int.Parse(alt.Groups[1].Value) : 0;
    }

    /// <summary>根据版本所需 Java 主版本选择最合适的运行时。</summary>
    public static JavaRuntime? SelectForVersion(int requiredMajor)
    {
        var all = Scan();
        if (all.Count == 0) return null;

        // 优先精确匹配，其次选择 >= 要求的最低版本（避免过新导致不兼容）
        var exact = all.FirstOrDefault(j => j.MajorVersion == requiredMajor);
        if (exact is not null) return exact;

        var suitable = all.Where(j => j.MajorVersion >= requiredMajor)
            .OrderBy(j => j.MajorVersion)
            .FirstOrDefault();
        if (suitable is not null) return suitable;

        // 兜底：返回最高版本
        return all[0];
    }
}


