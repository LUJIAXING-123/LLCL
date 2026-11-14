using LLCL.Core;
using LLCL.Core.Account;
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

        // 版本新增 / 删除 / 重命名后自动刷新，避免界面显示过期数据
        VersionManager.VersionsChanged += OnVersionsChanged;

        // 账号切换或新增后刷新显示
        AccountManager.Changed += OnAccountsChanged;
    }

    /// <summary>账号新增、切换或删除后刷新首页显示。</summary>
    private void OnAccountsChanged()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void OnVersionsChanged()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(Refresh);
    }
    private void Refresh()
    {
        var config = LauncherConfig.Current;

        // 账号显示以账号管理器的当前选中项为准，
        // 没有选中账号时退回离线用户名
        var account = AccountManager.Selected;
        var displayName = account?.Name ?? config.OfflineUserName;

        GreetingText.Text = $"你好，{displayName}";
        SubGreetingText.Text = $"{DateTime.Now:yyyy年M月d日} · {LauncherInfo.ProductShortName} {LauncherInfo.Version}";

        AccountButton.Content = account switch
        {
            null => $"离线 · {config.OfflineUserName}",
            { Kind: AccountKind.Microsoft } => $"微软 · {account.Name}",
            { Kind: AccountKind.ThirdParty } => $"第三方 · {account.Name}",
            _ => $"离线 · {account.Name}"
        };

        _ = LoadAvatarAsync(account, displayName);

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

            // 上次使用的版本可能已被删除：回退到第一项并同步配置，
            // 避免下拉框显示与实际选择不一致（列表"错乱"的观感多源于此）
            if (index < 0)
            {
                index = 0;
                config.LastInstance = versions[0];
                config.Save();
            }

            VersionComboBox.SelectedIndex = index;
            CurrentVersionText.Text = $"将启动 {entries[index].DisplayName}";
        }
        else
        {
            // 版本全部不存在时清空记忆，避免残留无效的 LastInstance
            if (!string.IsNullOrEmpty(config.LastInstance))
            {
                config.LastInstance = null;
                config.Save();
            }

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

    /// <summary>
    /// 加载并合成账号头像。
    /// 皮肤来自 sessionserver，按 PCL-CE 的坐标裁出脸层与帽子层再叠放。
    /// 离线账号或取不到皮肤时显示首字母占位。
    /// </summary>
    private async Task LoadAvatarAsync(AccountEntry? account, string displayName)
    {
        try
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Uuid))
            {
                ShowAvatarFallback(displayName);
                return;
            }

            // 第三方皮肤站使用账号登录时记录的 Yggdrasil 地址
            var yggdrasil = account.Kind == AccountKind.ThirdParty ? account.ServerUrl : null;

            var skin = await MinecraftSkin.GetSkinAsync(account.Uuid, yggdrasil);
            if (skin is null)
            {
                ShowAvatarFallback(displayName);
                return;
            }

            var head = await SkinHeadRenderer.RenderAsync(skin);
            if (head is null)
            {
                ShowAvatarFallback(displayName);
                return;
            }

            Dispatch(() =>
            {
                AvatarImage.Source = head;
                AvatarImage.Visibility = Visibility.Visible;
                AvatarFallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载头像失败: {ex.Message}");
            ShowAvatarFallback(displayName);
        }
    }

    private void ShowAvatarFallback(string displayName)
    {
        var initial = string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : displayName[..1].ToUpperInvariant();

        Dispatch(() =>
        {
            AvatarInitial.Text = initial;
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarFallback.Visibility = Visibility.Visible;
        });
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

        if (ProcessTracker.IsRunning(version))
        {
            await ShowAsync("游戏正在运行", $"版本 {version} 已经在运行中。");
            return;
        }

        SetLaunchBusy(true);

        try
        {
            var result = await GameLaunchService.EnsureAndLaunchAsync(version, this, OnLaunchStage);

            if (result.NeedsJava)
            {
                await ShowAsync("未找到 Java",
                    "启动游戏需要 Java 运行时。请在“设置 - 启动”中指定 Java 路径，或安装 Java 后重试。");
            }
            else if (!result.Success)
            {
                // 崩溃时会由 CrashDialog 单独提示，这里只处理非崩溃的失败
                if (result.Crash is null)
                    await ShowAsync("启动失败", result.Message ?? "未知错误");
                else
                    await CrashDialog.ShowAsync(result.Crash);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启动游戏失败");
            await ShowAsync("启动失败", ex.Message);
        }
        finally
        {
            SetLaunchBusy(false);
            Refresh();
        }
    }

    /// <summary>启动阶段回调，更新按钮下的提示文字。</summary>
    private void OnLaunchStage(GameLaunchService.LaunchStage stage)
    {
        Dispatch(() =>
        {
            LaunchStageText.Text = stage.Message;
        });
    }

    /// <summary>进入/退出启动忙碌状态：按钮转圈，进度条常驻直到窗口出现。</summary>
    private void SetLaunchBusy(bool busy)
    {
        LaunchButton.IsEnabled = !busy;
        LaunchProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LaunchStageText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy)
        {
            LaunchButton.Content = "启动中…";
            LaunchStageText.Text = "正在准备…";
        }
        else
        {
            LaunchButton.Content = "启动";
            LaunchStageText.Text = string.Empty;
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

    /// <summary>切回 UI 线程执行（启动回调可能来自后台线程）。</summary>
    private void Dispatch(Action action)
    {
        var queue = DispatcherQueue;
        if (queue is null) { action(); return; }
        queue.TryEnqueue(() => action());
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





