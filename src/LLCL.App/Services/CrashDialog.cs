using LLCL.Core;
using LLCL.Core.Minecraft;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Services;

/// <summary>
/// 崩溃提示弹窗：展示原因与解决办法，并可打开完整崩溃报告。
/// 可从任意线程调用，内部自动切到 UI 线程。
/// </summary>
public static class CrashDialog
{
    /// <summary>在 UI 线程弹出崩溃报告（可从后台线程调用）。</summary>
    public static Task ShowAsync(CrashReport report)
    {
        var window = MainWindow.Instance;
        if (window is null) return Task.CompletedTask;

        var queue = window.DispatcherQueue;
        if (queue is null) return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>();

        queue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                await _ShowCoreAsync(report);
            }
            catch (Exception ex)
            {
                Logger.Warn($"显示崩溃报告失败: {ex.Message}");
            }
            finally
            {
                tcs.TrySetResult(true);
            }
        });

        return tcs.Task;
    }

    private static async Task _ShowCoreAsync(CrashReport report)
    {
        var window = MainWindow.Instance;
        if (window?.Content?.XamlRoot is null) return;

        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = report.Summary,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });

        if (report.Suggestion is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "解决办法",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });

            panel.Children.Add(new TextBlock
            {
                Text = report.Suggestion,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = Application.Current.Resources["SystemFillColorCautionBrush"]
                             as Microsoft.UI.Xaml.Media.Brush
            });
        }

        var dialog = new ContentDialog
        {
            Title = report.Title,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "知道了",
            XamlRoot = window.Content.XamlRoot
        };

        // 有崩溃报告文件时提供查看入口
        if (report.DetailsPath is not null && File.Exists(report.DetailsPath))
        {
            var path = report.DetailsPath;

            dialog.SecondaryButtonText = "查看崩溃报告";
            dialog.SecondaryButtonClick += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn($"打开崩溃报告失败: {ex.Message}");
                }
            };
        }

        await dialog.ShowAsync();
    }
}

