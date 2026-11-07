using System.Text.RegularExpressions;

namespace LLCL.Core.Minecraft;

/// <summary>崩溃分析结果：给用户看的说明与建议。</summary>
public sealed record CrashReport(
    string Title,
    string Summary,
    string? Suggestion,
    string? DetailsPath,
    int ExitCode);

/// <summary>
/// 读取 Minecraft 的崩溃报告与日志，翻译成用户能看懂的说明。
/// <para>
/// 覆盖最常见的几类崩溃：内存不足、Java 版本不符、模组冲突、
/// 显卡驱动、缺少依赖库、Mod 加载失败。
/// </para>
/// </summary>
public static class CrashAnalyzer
{
    /// <summary>
    /// 分析一次异常退出。
    /// </summary>
    /// <param name="versionId">版本 ID。</param>
    /// <param name="gameDir">游戏目录（隔离模式下与版本目录不同）。</param>
    /// <param name="exitCode">进程退出码。</param>
    /// <param name="waitForFile">是否等待崩溃文件落盘（Java 退出后写文件略有延迟）。</param>
    public static async Task<CrashReport> AnalyzeAsync(string versionId, string gameDir,
        int exitCode, bool waitForFile = true)
    {
        // Java 进程退出后，崩溃报告通常还要 0.5-2 秒才写完
        if (waitForFile) await Task.Delay(900).ConfigureAwait(false);

        var crashPath = FindLatestCrashReport(versionId, gameDir);
        var content = crashPath is not null ? ReadTextSafe(crashPath) : null;
        var latestLog = ReadLatestLog(gameDir);

        // 有崩溃报告时优先用它，否则退回 latest.log 的末尾
        var text = content ?? latestLog;

        if (text is null)
        {
            return new CrashReport(
                "游戏异常退出",
                $"进程在未生成崩溃报告的情况下退出（退出码 {exitCode}）。",
                ExitCodeHint(exitCode),
                null,
                exitCode);
        }

        return Classify(text, exitCode, crashPath);
    }

    /// <summary>判断这次退出是否属于崩溃（0 为正常退出）。</summary>
    public static bool IsCrash(int exitCode) => exitCode != 0;

    private static CrashReport Classify(string text, int exitCode, string? path)
    {
        // ---- 内存不足 ----
        if (Contain(text, "java.lang.OutOfMemoryError", "out of memory", "GC overhead limit exceeded"))
        {
            return new CrashReport(
                "内存不足",
                "游戏在运行中耗尽了分配的内存。",
                "请在设置 → 启动中调大最大内存（建议 4096 MB 以上），" +
                "或减少同时加载的模组、关闭高画质资源包。",
                path, exitCode);
        }

        // ---- Java 版本不符 ----
        if (Contain(text, "UnsupportedClassVersionError", "has been compiled by a more recent version"))
        {
            return new CrashReport(
                "Java 版本过低",
                "游戏要求的 Java 版本高于当前使用的版本。",
                "请在设置 → 启动中把 Java 换成与游戏版本匹配的版本" +
                "（例如 1.17 以上需要 Java 17，1.20.5 以上需要 Java 21）。",
                path, exitCode);
        }

        // ---- 模组冲突 / 加载失败 ----
        if (Contain(text, "ModResolutionException", "Incompatible mod set",
                "net.fabricmc.loader.impl.FormattedException", "Missing or unsupported mandatory dependencies"))
        {
            return new CrashReport(
                "模组冲突",
                "模组之间存在依赖缺失或版本冲突，游戏在启动阶段就中止了。",
                "请查看崩溃报告里列出的模组名，移除冲突或补齐缺失的依赖模组；" +
                "也可先把最近新增的模组移除后再试。",
                path, exitCode);
        }

        // ---- 显卡 / OpenGL ----
        if (Contain(text, "OpenGL", "GLFW", "Pixel format not accelerated",
                "Failed to create window", "GL_INVALID"))
        {
            return new CrashReport(
                "显卡驱动问题",
                "游戏无法创建 OpenGL 上下文，通常是显卡驱动过旧或不被支持。",
                "请更新显卡驱动；若为双显卡笔记本，请确保游戏使用独立显卡运行，" +
                "并在显卡控制面板中为 Java 指定高性能模式。",
                path, exitCode);
        }

        // ---- 缺少 DLL / 本地库 ----
        if (Contain(text, "UnsatisfiedLinkError", "no lwjgl", "no XXX in java.library.path"))
        {
            return new CrashReport(
                "缺少本地库",
                "游戏所需的本地库（natives）缺失或与当前系统架构不匹配。",
                "请删除该版本后重新安装，确保下载完整；" +
                "同时确认使用的是 64 位 Java。",
                path, exitCode);
        }

        // ---- 资源缺失 ----
        if (Contain(text, "NoSuchFileException", "java.io.FileNotFoundException") &&
            Contain(text, "assets", "resources"))
        {
            return new CrashReport(
                "游戏文件不完整",
                "资源文件或版本文件缺失，游戏在加载阶段失败。",
                "请在下载页重新安装该版本以补全文件。",
                path, exitCode);
        }

        // ---- 未分类：仍然给出崩溃摘要 ----
        var exceptionLine = ExtractExcceptionLine(text);

        return new CrashReport(
            "游戏崩溃",
            exceptionLine is null
                ? $"游戏异常退出（退出码 {exitCode}）。"
                : $"游戏因 {exceptionLine} 崩溃。",
            path is null
                ? "未找到崩溃报告文件，可在游戏目录的 logs 下查看 latest.log。"
                : "完整崩溃报告已保存，可点击下方按钮查看详情。",
            path, exitCode);
    }

    /// <summary>从崩溃报告中提取最关键的异常行。</summary>
    private static string? ExtractExcceptionLine(string text)
    {
        // 崩溃报告里 Description 段通常是最有价值的一句话
        var match = Regex.Match(text, @"Description:\s*(.+)");
        if (match.Success) return match.Groups[1].Value.Trim();

        match = Regex.Match(text, @"(java\.lang\.\w+(Exception|Error)[^\r\n]*)");
        if (match.Success) return match.Groups[1].Value.Trim();

        return null;
    }

    private static string? ExitCodeHint(int exitCode)
    {
        return exitCode switch
        {
            -1073741819 => "进程访问冲突（0xC0000005），通常是显卡驱动或本地库问题",
            -1073741571 => "栈溢出，常见于模组递归调用或 JVM 参数不当",
            -1073740940 => "堆损坏，可能与模组或内存分配有关",
            1 => "游戏主动退出并返回错误码，请查看日志",
            _ => null
        };
    }

    /// <summary>找最新的崩溃报告文件。</summary>
    private static string? FindLatestCrashReport(string versionId, string gameDir)
    {
        var candidates = new List<string>();

        // 隔离模式的崩溃报告在版本目录下
        candidates.Add(Path.Combine(Paths.VersionsDir, versionId, "crash-reports"));

        // 非隔离模式在 .minecraft 根目录
        if (!string.Equals(gameDir, Path.Combine(Paths.VersionsDir, versionId),
                StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(gameDir, "crash-reports"));
        }

        string? newest = null;
        DateTime newestTime = DateTime.MinValue;

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "crash-*.txt"))
            {
                try
                {
                    var time = File.GetLastWriteTime(file);
                    if (time > newestTime)
                    {
                        newestTime = time;
                        newest = file;
                    }
                }
                catch { }
            }
        }

        // 只认最近 5 分钟内生成的报告，避免误报历史崩溃
        if (newest is not null && DateTime.Now - newestTime > TimeSpan.FromMinutes(5))
            return null;

        return newest;
    }

    /// <summary>读取 latest.log 的末尾若干行。</summary>
    private static string? ReadLatestLog(string gameDir)
    {
        var path = Path.Combine(gameDir, "logs", "latest.log");
        if (!File.Exists(path)) return null;

        try
        {
            var info = new FileInfo(path);
            if (DateTime.Now - info.LastWriteTime > TimeSpan.FromMinutes(5)) return null;

            var lines = File.ReadAllLines(path);
            var take = Math.Min(lines.Length, 120);
            return string.Join('\n', lines[^take..]);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTextSafe(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private static bool Contain(string text, params string[] keywords)
    {
        foreach (var k in keywords)
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
