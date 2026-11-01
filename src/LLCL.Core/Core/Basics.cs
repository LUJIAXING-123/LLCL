using System.Diagnostics;
using System.Text;

namespace LLCL.Core;

/// <summary>
/// 全局路径管理：
/// <list type="bullet">
///   <item>游戏目录使用用户目录下的 <c>.minecraft</c>（与 PCL 相同，可直接共用同一套游戏文件）</item>
///   <item>启动器自身的配置、日志、缓存、工具保存在 <c>%APPDATA%\LLCL</c>（符合 Windows 软件惯例）</item>
/// </list>
/// 允许用户在设置中自定义游戏目录，也支持便携模式（exe 同目录存在 <c>portable.txt</c> 时把数据放在 exe 同级）。
/// </summary>
public static class Paths
{
    public const string LauncherName = "LLCL";

    /// <summary>游戏目录名，与 PCL 一致。</summary>
    public const string MinecraftFolderName = ".minecraft";

    /// <summary>启动器数据目录名（位于 %APPDATA%）。</summary>
    public const string DataFolderName = LauncherName;

    /// <summary>便携模式标记文件名（放在 exe 同目录即可启用便携模式）。</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>
    /// 启动器所在目录。单文件发布时 <see cref="AppContext.BaseDirectory"/>
    /// 可能指向自解压临时目录，因此优先使用真实 exe 路径。
    /// </summary>
    private static string _launcherDir = _ResolveLauncherDir();

    private static string _ResolveLauncherDir()
    {
        // 1. 正常运行时 ProcessPath 就是用户双击的 exe
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch
        {
            // 忽略
        }

        // 2. 回退到 AppContext（非单文件时为应用目录）
        var baseDir = AppContext.BaseDirectory;
        return string.IsNullOrEmpty(baseDir)
            ? Directory.GetCurrentDirectory()
            : baseDir.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>启动器 exe 所在目录。</summary>
    public static string LauncherDir => _launcherDir;

    /// <summary>启动器数据根目录（配置、日志、缓存、工具）。</summary>
    public static string DataRoot => _dataRoot;

    private static string _dataRoot = _ResolveDataRoot();

    private static string _ResolveDataRoot()
    {
        // 便携模式：exe 同级有 portable.txt
        try
        {
            if (File.Exists(Path.Combine(_launcherDir, PortableMarker)))
                return Path.Combine(_launcherDir, DataFolderName);
        }
        catch
        {
            // 忽略
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, DataFolderName);
    }

    /// <summary>是否为便携模式（数据保存在 exe 同级目录）。</summary>
    public static bool IsPortable
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(_launcherDir, PortableMarker));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 初始化路径。必须在读取配置之前调用一次。
    /// 便携模式或 %APPDATA% 不可用时做相应回退。
    /// </summary>
    public static void Initialize()
    {
        // 若配置目录不可写，回退到 exe 同级（再不行就退到临时目录）
        if (!_CanWrite(_dataRoot))
        {
            var fallback = Path.Combine(_launcherDir, DataFolderName);
            if (_CanWrite(fallback))
            {
                _dataRoot = fallback;
                Logger.Warn($"用户数据目录不可写，改为 {_dataRoot}");
            }
            else
            {
                _dataRoot = Path.Combine(Path.GetTempPath(), DataFolderName);
                Logger.Warn($"数据目录不可写，改为临时目录 {_dataRoot}");
            }
        }
    }

    /// <summary>为兼容旧调用点保留。</summary>
    public static void ResolveWritableRoot() => Initialize();

    private static bool _CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".llcl-write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ConfigFile => Path.Combine(DataRoot, "llcl.json");
    public static string LogDir => Path.Combine(DataRoot, "Logs");
    public static string CacheDir => Path.Combine(DataRoot, "Cache");
    public static string ToolsDir => Path.Combine(DataRoot, "Tools");
    public static string EasyTierDir => Path.Combine(ToolsDir, "EasyTier");
    public static string RuntimeDir => Path.Combine(DataRoot, "Runtime");

    /// <summary>
    /// 游戏根目录（.minecraft），包含 versions / libraries / assets。
    /// 默认使用用户目录下的 .minecraft（与 PCL 一致）；便携模式下使用 exe 同级。
    /// 可由设置在启动时重定向。
    /// </summary>
    public static string MinecraftDir { get; set; } = _ResolveMinecraftDir();

    private static string _ResolveMinecraftDir()
        => IsPortable
            ? Path.Combine(_launcherDir, MinecraftFolderName)
            : Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), MinecraftFolderName));

    public static string VersionsDir => Path.Combine(MinecraftDir, "versions");
    public static string LibrariesDir => Path.Combine(MinecraftDir, "libraries");
    public static string AssetsDir => Path.Combine(MinecraftDir, "assets");

    public static void EnsureAll()
    {
        foreach (var dir in new[] { DataRoot, LogDir, CacheDir, ToolsDir, RuntimeDir, MinecraftDir, VersionsDir, LibrariesDir, AssetsDir })
        {
            try { Directory.CreateDirectory(dir); } catch { /* 忽略，权限问题在后续使用处报告 */ }
        }
    }

    /// <summary>允许在设置中把游戏目录重定向到其他位置（如已存在的另一个 .minecraft）。</summary>
    public static void SetMinecraftDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        MinecraftDir = Path.GetFullPath(dir);
    }
}

/// <summary>
/// 简易日志记录器：写入滚动文件并广播到内存事件（供 UI 日志页面使用）。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly StringBuilder _buffer = new();
    private static string? _currentFile;
    private static DateTime _currentDate = DateTime.MinValue;

    public static event Action<string>? OnLog;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Debug(string message) => Write("DEBUG", message);

    public static void Error(Exception ex, string context)
    {
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}");
        Write("TRACE", ex.StackTrace ?? string.Empty);
    }

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"[{now:HH:mm:ss.fff}] [{level}] {message}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                if (_currentDate.Date != now.Date || _currentFile is null)
                {
                    _currentDate = now;
                    _currentFile = Path.Combine(Paths.LogDir, $"LLCL-{now:yyyyMMdd}.log");
                }

                File.AppendAllText(_currentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不得影响主流程
            }

            if (_buffer.Length > 200_000) _buffer.Clear();
            _buffer.AppendLine(line);
        }

        try { OnLog?.Invoke(line); } catch { }
        System.Diagnostics.Debug.WriteLine(line);
    }

}

