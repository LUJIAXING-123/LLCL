using LLCL.Core;
using LLCL.Core.Account;
using LLCL.Core.Link;
using LLCL.Core.Minecraft;
using LLCL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LLCL.Pages;

/// <summary>首页：直接选择版本并启动。</summary>
public sealed partial class HomePage : Page
{
    private bool _suppressSelection;

    public HomePage()
    {
        InitializeComponent();

        // 阴影在图片真正解码完成后才能取到正确的 alpha 遮罩
        AvatarImage.ImageOpened += (_, _) => ApplyAvatarShadow();

        // 布局变化时元素尺寸会变，遮罩需要同步重建
        AvatarShadowHost.SizeChanged += (_, _) => ApplyAvatarShadow();

        Loaded += (_, _) => Refresh();

        // 版本新增 / 删除 / 重命名后自动刷新，避免界面显示过期数据
        VersionManager.VersionsChanged += OnVersionsChanged;

        // 账号切换或新增后刷新显示
        AccountManager.Changed += OnAccountsChanged;
    }

    /// <summary>
    /// 给头像挂上投影。
    /// <para>
    /// Visual 上没有 Shadow 属性（只有 SpriteVisual 才有），
    /// 用 SetElementChildVisual 塞进去又会被同层内容盖住，
    /// 所以改用 XAML 的 ThemeShadow，靠 Shadow 属性加 Z 位移触发。
    /// </para>
    /// <para>
    /// 需要显式声明接收面（卡片），否则阴影没有落点、看起来像没生效。
    /// Z 取 8，ThemeShadow 的模糊半径约为其一半，贴近头像轮廓。
    /// </para>
    /// </summary>
    private void ApplyAvatarShadow()
    {
        if (AvatarShadowHost.Shadow is not ThemeShadow shadow)
        {
            shadow = new ThemeShadow();
            AvatarShadowHost.Shadow = shadow;
        }

        shadow.Receivers.Clear();
        shadow.Receivers.Add(AvatarCard);

        AvatarShadowHost.Translation = new System.Numerics.Vector3(0, 0, 8);
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
    /// <summary>
    /// 加载并合成账号头像。
    /// 在线账号从 sessionserver 取皮肤；离线账号按 PCL-CE 的规则
    /// 用内置的 Steve / Alex 皮肤，保证同一用户名始终对应同一形象。
    /// </summary>
    private async Task LoadAvatarAsync(AccountEntry? account, string displayName)
    {
        try
        {
            // 离线账号：没有真实皮肤，按 UUID 选用内置 Steve / Alex
            if (account is null || account.Kind == AccountKind.Offline)
            {
                var offlineSkin = LoadDefaultSkin(account?.Uuid);
                if (offlineSkin is null)
                {
                    ShowAvatarFallback(displayName);
                    return;
                }

                await ApplyHeadAsync(offlineSkin, displayName);
                return;
            }

            if (string.IsNullOrWhiteSpace(account.Uuid))
            {
                ShowAvatarFallback(displayName);
                return;
            }

            // 第三方皮肤站使用账号登录时记录的 Yggdrasil 地址
            var yggdrasil = account.Kind == AccountKind.ThirdParty ? account.ServerUrl : null;

            var skin = await MinecraftSkin.GetSkinAsync(account.Uuid, yggdrasil);

            // 在线账号取不到皮肤时也回退到内置皮肤，比纯色块更自然
            skin ??= LoadDefaultSkin(account.Uuid);
            if (skin is null)
            {
                ShowAvatarFallback(displayName);
                return;
            }

            await ApplyHeadAsync(skin, displayName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载头像失败: {ex.Message}");
            ShowAvatarFallback(displayName);
        }
    }

    /// <summary>把皮肤合成为头像并显示。</summary>
    private async Task ApplyHeadAsync(byte[] skin, string displayName)
    {
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

            // 图片换新后必须重做遮罩，否则阴影仍按旧内容（或空内容）计算
            ApplyAvatarShadow();
        });
    }

    /// <summary>
    /// 读取内置的 Steve / Alex 皮肤。
    /// 选择规则与 PCL-CE 的 McSkinSex 一致：UUID 特定位异或为奇数则 Alex。
    /// </summary>
    private static byte[]? LoadDefaultSkin(string? uuid)
    {
        var name = MinecraftSkin.IsAlex(uuid) ? "alex" : "steve";

        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"LLCL.Assets.{name}.png");
            if (stream is null)
            {
                Logger.Warn($"内置皮肤缺失: {name}.png");
                return null;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取内置皮肤失败 {name}: {ex.Message}");
            return null;
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






