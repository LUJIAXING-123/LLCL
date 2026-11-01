using LLCL.Core;
using LLCL.Core.Link;
using LLCL.Core.Minecraft;
using Microsoft.UI.Xaml;

namespace LLCL.Services;

/// <summary>启动期后台任务：内核检查、EasyTier 更新、日志转发。</summary>
public static class AppServices
{
    private static bool _warmed;

    public static async Task WarmupAsync(Window window)
    {
        if (_warmed) return;
        _warmed = true;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            Logger.OnLog += line => { try { MainWindow.Instance?.SetStatus(line.Length > 120 ? line[..120] : line); } catch { } };

            // 预热：后台扫描 Java，避免设置页首次打开时卡顿
            _ = Task.Run(() => JavaScanner.ScanAsync());
        });

        // EasyTier 内核检查与自动更新（不阻塞界面）
        _ = Task.Run(async () =>
        {
            try
            {
                var version = await LinkService.Instance.PrepareAsync().ConfigureAwait(false);
                if (version is not null)
                    Logger.Info($"联机内核已就绪: EasyTier {version}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "联机内核初始化失败");
            }
        });
    }
}


