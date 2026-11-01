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
        var result = candidates
            .AsParallel()
            .WithDegreeOfParallelism(Math.Clamp(candidates.Count, 1, 8))
            .Select(Parse)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        result.Sort((a, b) => b.MajorVersion.CompareTo(a.MajorVersion));
        _cached = result;
        return result;
    }

    private static IEnumerable<string> _EnumerateCandidates()
    {
        // 1. 用户指定
        var manual = LauncherConfig.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) yield return manual;

        // 2. PATH 中的 java
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var javaExe = Path.Combine(dir.Trim(), "javaw.exe");
            if (File.Exists(javaExe)) yield return javaExe;
        }

        // 3. 常见安装目录
        var roots = new[]
        {
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Eclipse Foundation",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files\Zulu",
            @"C:\Program Files\BellSoft",
            @"C:\Program Files\Amazon Corretto",
            @"C:\Program Files (x86)\Java",
            Path.Combine(Paths.DataRoot, "java"),
            Path.Combine(Paths.RuntimeDir, "java")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var exe in Directory.EnumerateFiles(root, "javaw.exe", SearchOption.AllDirectories))
                yield return exe;
        }

        // 4. 启动器目录下的 runtime（由自动下载 Java 功能放置）
        if (Directory.Exists(Paths.RuntimeDir))
        {
            foreach (var exe in Directory.EnumerateFiles(Paths.RuntimeDir, "javaw.exe", SearchOption.AllDirectories))
                yield return exe;
        }
    }

    /// <summary>执行 java -version 解析版本信息。</summary>
    public static JavaRuntime? Parse(string javaExe)
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
