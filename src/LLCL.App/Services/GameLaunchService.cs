using LLCL.Core;
using LLCL.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Services;

/// <summary>按版本启动游戏（无实例概念）。</summary>
public static class GameLaunchService
{
    public sealed record LaunchResult(bool Success, bool NeedsJava, string? Message);

    public static async Task<LaunchResult> EnsureAndLaunchAsync(string versionId, FrameworkElement host)
    {
        var config = LauncherConfig.Current;
        // 与 PCL 一致：默认使用 .minecraft 根目录，版本设置可开启隔离
        var vsSettings = VersionSettings.Load(versionId);
        var gameDir = vsSettings.GetGameDir(versionId);

        // 1. 版本文件缺失时先补全
        var versionJson = Path.Combine(Paths.VersionsDir, versionId, versionId + ".json");
        if (!File.Exists(versionJson))
        {
            var confirm = new ContentDialog
            {
                Title = "版本缺失",
                Content = $"版本 {versionId} 尚未安装，是否现在下载？",
                PrimaryButtonText = "下载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = host.XamlRoot
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return new LaunchResult(false, false, "用户取消");

            await VanillaInstaller.InstallAsync(versionId, Paths.VersionsDir, null);
        }

        // 2. 选择 Java（后台扫描，不阻塞 UI）
        var javaPath = await ResolveJavaAsync(versionId);
        if (javaPath is null)
            return new LaunchResult(false, true, "未找到 Java 运行时");

        // 3. 账号
        var account = config.AccountType == "microsoft" && !string.IsNullOrWhiteSpace(config.MicrosoftUserName)
            ? new LaunchAccount(config.MicrosoftUserName!, config.MicrosoftUuid ?? Guid.NewGuid().ToString(),
                config.MicrosoftAccessToken ?? "0", "microsoft")
            : LaunchAccount.Offline(config.OfflineUserName);

        // 版本级设置优先于全局设置
        var vs = VersionSettings.Load(versionId);
        var maxMemory = vs.OverrideMemory == true
            ? vs.MaxMemoryMb
            : config.MaxMemoryMb > 0 ? config.MaxMemoryMb : SuggestMemoryMb();
        var minMemory = vs.OverrideMemory == true ? vs.MinMemoryMb : config.MinMemoryMb;

        var options = new GameLauncher.LaunchOptions
        {
            Account = account,
            JavaPath = javaPath,
            MaxMemoryMb = maxMemory,
            MinMemoryMb = minMemory,
            ExtraJvmArgs = vs.ExtraJvmArgs.Length > 0 ? vs.ExtraJvmArgs : config.ExtraJvmArgs,
            ExtraGameArgs = vs.ExtraGameArgs.Length > 0 ? vs.ExtraGameArgs : config.ExtraGameArgs,
            Fullscreen = vs.OverrideWindow == true ? vs.Fullscreen : config.Fullscreen,
            Width = vs.OverrideWindow == true ? vs.Width : config.WindowWidth,
            Height = vs.OverrideWindow == true ? vs.Height : config.WindowHeight
        };

        try
        {
            var process = await GameLauncher.LaunchAsync(versionId, gameDir, options);
            ProcessTracker.Track(versionId, process);
            return new LaunchResult(true, false, null);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"启动版本 {versionId} 失败");
            return new LaunchResult(false, false, ex.Message);
        }
    }

    /// <summary>解析要使用的 Java：版本设置优先，其次全局设置，最后按版本要求自动匹配。</summary>
    public static async Task<string?> ResolveJavaAsync(string versionId)
    {
        var config = LauncherConfig.Current;
        var settings = VersionSettings.Load(versionId);

        // 版本单独指定的 Java
        if (settings.OverrideJava == true && !string.IsNullOrWhiteSpace(settings.JavaPath) &&
            File.Exists(settings.JavaPath))
            return settings.JavaPath;

        if (!config.AutoSelectJava && !string.IsNullOrWhiteSpace(config.JavaPath) &&
            File.Exists(config.JavaPath))
            return config.JavaPath;

        var runtimes = await JavaScanner.ScanAsync();

        int requiredMajor;
        try
        {
            requiredMajor = VersionLoader.Load(versionId).JavaVersion?.MajorVersion ?? 8;
        }
        catch
        {
            requiredMajor = 8;
        }

        var exact = runtimes.FirstOrDefault(j => j.MajorVersion == requiredMajor);
        if (exact is not null) return exact.Path;

        var newer = runtimes.Where(j => j.MajorVersion >= requiredMajor)
            .OrderBy(j => j.MajorVersion)
            .FirstOrDefault();
        if (newer is not null) return newer.Path;

        return runtimes.FirstOrDefault()?.Path;
    }

    private static int SuggestMemoryMb()
    {
        try
        {
            var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            var suggested = (int)(totalMb / 4 / 512 * 512);
            return Math.Clamp(suggested, 1024, 8192);
        }
        catch
        {
            return 2048;
        }
    }
}

/// <summary>跟踪运行中的游戏进程。</summary>
public static class ProcessTracker
{
    private static readonly HashSet<string> _running = [];
    private static readonly object _lock = new();

    public static bool IsRunning(string versionId)
    {
        lock (_lock) return _running.Contains(versionId);
    }

    public static void Track(string versionId, System.Diagnostics.Process process)
    {
        lock (_lock) _running.Add(versionId);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_lock) _running.Remove(versionId);
            Logger.Info($"游戏进程已退出: {versionId} (ExitCode {process.ExitCode})");
        };
    }
}
