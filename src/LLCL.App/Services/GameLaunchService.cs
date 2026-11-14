using LLCL.Core;
using LLCL.Core.Account;
using LLCL.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Services;

/// <summary>按版本启动游戏（无实例概念）。</summary>
public static class GameLaunchService
{
    /// <summary>启动过程中的阶段，用于界面显示进度文案。</summary>
    public sealed record LaunchStage(string Message, double Ratio);

    /// <summary>启动结果。</summary>
    public sealed record LaunchResult(
        bool Success,
        bool NeedsJava,
        string? Message,
        CrashReport? Crash = null,
        bool WindowAppeared = false);

    /// <summary>
    /// 完整启动流程：
    /// 登录校验 → 补全文件 → 选择 Java → 启动 → 等待窗口 → 崩溃检测。
    /// </summary>
    public static async Task<LaunchResult> EnsureAndLaunchAsync(string versionId, FrameworkElement host,
        Action<LaunchStage>? onStage = null)
    {
        var config = LauncherConfig.Current;
        var vsSettings = VersionSettings.Load(versionId);
        var gameDir = vsSettings.GetGameDir(versionId);

        // ---------- 1. 登录 ----------
        Logger.Info("[启动] 1/7 验证账号");
        onStage?.Invoke(new LaunchStage("正在验证账号…", 0.08));

        AccountEntry account;
        try
        {
            var selected = AccountManager.Selected;

            if (selected is null)
            {
                // 没有保存的账号时用离线账号，避免阻断启动
                account = AccountManager.CreateOffline(config.OfflineUserName);
                Logger.Info($"使用离线账号启动：{account.Name}");
            }
            else
            {
                onStage?.Invoke(new LaunchStage($"正在登录 {selected.Name}…", 0.12));
                account = await AccountManager.EnsureValidAsync(selected).ConfigureAwait(true);
            }
        }
        catch (AuthException ex)
        {
            return new LaunchResult(false, false, ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "账号验证失败");
            return new LaunchResult(false, false, "账号验证失败：" + ex.Message);
        }

        // ---------- 2. 补全文件 ----------
        Logger.Info("[启动] 2/7 检查游戏文件");
        onStage?.Invoke(new LaunchStage("正在检查游戏文件…", 0.18));

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

            onStage?.Invoke(new LaunchStage("正在下载游戏文件…", 0.24));
            await VanillaInstaller.InstallAsync(versionId, Paths.VersionsDir, null);
        }

        // ---------- 3. 选择 Java ----------
        Logger.Info("[启动] 3/7 查找 Java");
        onStage?.Invoke(new LaunchStage("正在查找 Java 运行时…", 0.32));

        var javaPath = await ResolveJavaAsync(versionId);
        if (javaPath is null)
            return new LaunchResult(false, true, "未找到 Java 运行时");

        // ---------- 4. 组装参数 ----------
        Logger.Info("[启动] 4/7 准备启动命令");
        onStage?.Invoke(new LaunchStage("正在准备启动命令…", 0.40));

        // 第三方账号需要 authlib-injector：必须在后台异步准备，
        // 否则同步下载会冻结界面，表现为点启动后卡死。
        string? injectorPath = null;
        if (account.Kind == AccountKind.ThirdParty && !string.IsNullOrWhiteSpace(account.ServerUrl))
        {
            onStage?.Invoke(new LaunchStage("正在准备第三方登录组件…", 0.36));
            injectorPath = await AuthlibInjector.EnsureAsync(account.ServerUrl);
        }

        var vs = VersionSettings.Load(versionId);

        // 版本自带的登录服务器优先于账号本身的设置：
        // 整合包会把发行方指定的验证服务器写进版本设置，
        // 玩家双击整合包后应当直接走该服务器，而不是沿用自己账号的。
        var authServer = !string.IsNullOrWhiteSpace(vs.ServerAuthServer)
            ? vs.ServerAuthServer
            : account.Kind == AccountKind.ThirdParty ? account.ServerUrl : null;

        if (!string.IsNullOrWhiteSpace(vs.ServerAuthServer) &&
            account.Kind != AccountKind.ThirdParty)
        {
            onStage?.Invoke(new LaunchStage("正在准备登录服务器组件…", 0.36));
            injectorPath = await AuthlibInjector.EnsureAsync(vs.ServerAuthServer);
        }

        var maxMemory = vs.OverrideMemory == true
            ? vs.MaxMemoryMb
            : config.MaxMemoryMb > 0 ? config.MaxMemoryMb : SuggestMemoryMb();
        var minMemory = vs.OverrideMemory == true ? vs.MinMemoryMb : config.MinMemoryMb;

        // 自动进入服务器：游戏的 --server / --port 参数，
        // 版本设置里填了地址就优先使用，忽略全局的自动进入配置。
        var enterServer = vs.ServerEnter;
        var enterPort = (int?)null;
        if (!string.IsNullOrWhiteSpace(enterServer))
        {
            var colon = enterServer.LastIndexOf(':');
            if (colon > 0 && int.TryParse(enterServer[(colon + 1)..], out var parsedPort))
            {
                enterPort = parsedPort;
                enterServer = enterServer[..colon];
            }
        }

        var options = new GameLauncher.LaunchOptions
        {
            Account = ToLaunchAccount(account),
            JavaPath = javaPath,
            MaxMemoryMb = maxMemory,
            MinMemoryMb = minMemory,
            ExtraJvmArgs = vs.ExtraJvmArgs.Length > 0 ? vs.ExtraJvmArgs : config.ExtraJvmArgs,
            ExtraGameArgs = vs.ExtraGameArgs.Length > 0 ? vs.ExtraGameArgs : config.ExtraGameArgs,
            Fullscreen = vs.OverrideWindow == true ? vs.Fullscreen : config.Fullscreen,
            Width = vs.OverrideWindow == true ? vs.Width : config.WindowWidth,
            Height = vs.OverrideWindow == true ? vs.Height : config.WindowHeight,
            // 第三方账号或版本指定的登录服务器都需要注入 authlib-injector
            AuthServerUrl = authServer,
            AuthlibInjectorPath = injectorPath,
            ServerHost = enterServer,
            ServerPort = enterPort
        };

        // ---------- 5. 启动 ----------
        System.Diagnostics.Process process;

        try
        {
            Logger.Info("[启动] 5/7 启动进程");
            onStage?.Invoke(new LaunchStage("正在启动游戏…", 0.48));
            process = await GameLauncher.LaunchAsync(versionId, gameDir, options);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"启动版本 {versionId} 失败");
            return new LaunchResult(false, false, ex.Message);
        }

        ProcessTracker.Track(versionId, process);

        // ---------- 6. 等待游戏窗口 ----------
        Logger.Info("[启动] 6/7 等待游戏窗口");
        onStage?.Invoke(new LaunchStage("正在等待游戏窗口…", 0.62));

        var handle = await WindowWatcher.WaitForGameWindowAsync(process,
            TimeSpan.FromSeconds(90));

        if (handle == IntPtr.Zero)
        {
            // 窗口没出现就退出：说明启动阶段就崩溃了
            if (process.HasExited)
            {
                var report = await CrashAnalyzer.AnalyzeAsync(versionId, gameDir,
                    process.ExitCode);
                return new LaunchResult(false, false, report.Summary, report, false);
            }

            // 进程还在但没有窗口：可能卡在加载，不判失败，交给用户观察
            Logger.Warn($"游戏窗口未在超时时间内出现，进程仍在运行（PID {process.Id}）");
            return new LaunchResult(true, false, "游戏进程已启动，但窗口尚未出现", null, false);
        }

        var title = WindowWatcher.GetTitle(handle);
        Logger.Info($"游戏窗口已出现：{title}");
        onStage?.Invoke(new LaunchStage("游戏已启动", 1.0));

        // ---------- 7. 后台监听退出，崩溃时提示 ----------
        _WatchForCrash(versionId, gameDir, process);

        return new LaunchResult(true, false, null, null, true);
    }

    /// <summary>
    /// 游戏启动后监听退出事件，若属于崩溃则弹窗说明。
    /// 这个监听在后台线程，通过 UI 线程弹窗。
    /// </summary>
    private static void _WatchForCrash(string versionId, string gameDir,
        System.Diagnostics.Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += async (_, _) =>
        {
            var exitCode = process.ExitCode;
            Logger.Info($"游戏进程已退出: {versionId} (ExitCode {exitCode})");

            if (!CrashAnalyzer.IsCrash(exitCode)) return;

            try
            {
                var report = await CrashAnalyzer.AnalyzeAsync(versionId, gameDir, exitCode)
                    .ConfigureAwait(false);

                await CrashDialog.ShowAsync(report).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"崩溃提示失败: {ex.Message}");
            }
        };
    }

    /// <summary>把账号条目转成启动器使用的账号模型。</summary>
    private static LaunchAccount ToLaunchAccount(AccountEntry account)
    {
        return account.Kind switch
        {
            AccountKind.Offline => LaunchAccount.Offline(account.Name),
            _ => new LaunchAccount(account.Name, account.Uuid, account.AccessToken,
                account.Kind == AccountKind.Microsoft ? "microsoft" : "thirdparty")
        };
    }

    /// <summary>解析要使用的 Java：版本设置优先，其次全局设置，最后按版本要求自动匹配。</summary>
    public static async Task<string?> ResolveJavaAsync(string versionId)
    {
        var config = LauncherConfig.Current;
        var settings = VersionSettings.Load(versionId);

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
        };
    }
}

