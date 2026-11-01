using LLCL.Core;
using LLCL.Core.Link;
using LLCL.Core.Minecraft;
using LLCL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Pages;

/// <summary>首页：直接选择版本并启动。</summary>
public sealed partial class HomePage : Page
{
    private bool _suppressSelection;

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var config = LauncherConfig.Current;
        GreetingText.Text = $"你好，{config.DisplayAccountName}";
        SubGreetingText.Text = $"{DateTime.Now:yyyy年M月d日} · LLCL 3.0";
        AccountButton.Content = config.AccountType == "microsoft"
            ? config.MicrosoftUserName ?? "微软账号"
            : $"离线 · {config.OfflineUserName}";

        // 使用版本管理器的顺序与自定义显示名
        var entries = VersionManager.ScanAll().Where(e => !e.IsBroken).ToList();
        var versions = entries.Select(e => e.Id).ToList();

        _suppressSelection = true;
        VersionComboBox.Items.Clear();
        foreach (var entry in entries)
            VersionComboBox.Items.Add($"{entry.DisplayName}   [{entry.Id}]");

        if (entries.Count > 0)
        {
            var index = versions.IndexOf(config.LastInstance ?? string.Empty);
            VersionComboBox.SelectedIndex = index >= 0 ? index : 0;
            CurrentVersionText.Text = $"将启动 {entries[VersionComboBox.SelectedIndex].DisplayName}";
        }
        else
        {
            CurrentVersionText.Text = "尚未安装任何版本，请先前往下载中心";
        }

        _suppressSelection = false;

        var broken = VersionManager.ScanAll().Count(e => e.IsBroken);
        VersionCountText.Text = entries.Count == 0
            ? "尚未安装版本"
            : $"共 {entries.Count} 个版本" + (broken > 0 ? $"（{broken} 个损坏）" : string.Empty);

        LinkStateText.Text = LinkService.Instance.State switch
        {
            LinkState.Connected => $"已连接房间 {LinkService.Instance.CurrentRoomCode}",
            LinkState.Scanning => "正在扫描局域网",
            _ => "未联机"
        };
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("settings");

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        var id = SelectedVersionId();
        if (id is null) return;

        LauncherConfig.Current.LastInstance = id;
        LauncherConfig.Current.Save();
        CurrentVersionText.Text = $"将启动 {DisplayOf(id)}";
    }

    /// <summary>从下拉框当前项取出真实版本 ID。</summary>
    private string? SelectedVersionId()
    {
        if (VersionComboBox.SelectedIndex < 0) return null;
        var ordered = VersionManager.ScanAll().Where(e => !e.IsBroken).ToList();
        if (VersionComboBox.SelectedIndex >= ordered.Count) return null;
        return ordered[VersionComboBox.SelectedIndex].Id;
    }

    private static string DisplayOf(string id)
    {
        var settings = VersionSettings.Load(id);
        return settings.DisplayName ?? id;
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersionId();
        if (version is null)
        {
            await ShowAsync("还没有版本", "请先在下载页安装一个 Minecraft 版本。");
            return;
        }

        LaunchButton.IsEnabled = false;
        LaunchProgress.Visibility = Visibility.Visible;

        try
        {
            var result = await GameLaunchService.EnsureAndLaunchAsync(version, this);
            if (result.NeedsJava)
                await ShowAsync("未找到 Java",
                    "启动游戏需要 Java 运行时。请在“设置 - 启动”中指定 Java 路径，或安装 Java 后重试。");
            else if (!result.Success)
                await ShowAsync("启动失败", result.Message ?? "未知错误");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启动游戏失败");
            await ShowAsync("启动失败", ex.Message);
        }
        finally
        {
            LaunchButton.IsEnabled = true;
            LaunchProgress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private async Task ShowAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("download");
    private void GoLink_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("link");

    private async void ManageVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersionId();
        if (version is null)
        {
            await ShowAsync("还没有版本", "请先在下载页安装一个 Minecraft 版本。");
            return;
        }

        MainWindow.Instance?.ShowVersionSettings(version);
    }
}


