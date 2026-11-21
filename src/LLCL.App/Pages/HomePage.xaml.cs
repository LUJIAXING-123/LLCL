using LLCL.Core;
using LLCL.Core.Account;
using LLCL.Core.Link;
using LLCL.Core.Minecraft;
using LLCL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

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
    /// 头像不再使用阴影。
    /// ThemeShadow 的模糊半径由元素与光源距离决定，没有精确控制手段，
    /// 无论怎么调都只能得到「散开一圈」或「完全不可见」两种结果，
    /// 因此放弃投影，保持干净的像素风外观。
    /// </summary>
    private void ApplyAvatarShadow()
    {
        // 预留方法体：后续若要换用其它方式（如自绘投影层）可在此接入。
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

        if (entries.Count > 0)
        {
            var index = versions.IndexOf(config.LastInstance ?? string.Empty);

            // 上次使用的版本可能已被删除：回退到第一项并同步配置
            if (index < 0)
            {
                index = 0;
                config.LastInstance = versions[0];
                config.Save();
            }

            _activeVersionId = versions[index];
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

            _activeVersionId = null;
            CurrentVersionText.Text = "尚未安装任何版本，请先到「版本列表」新建安装";
        }

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

    /// <summary>当前要启动的版本。版本选择已移到版本列表页，这里只缓存结果。</summary>
    private string? _activeVersionId;

    private static string DisplayOf(string id)
    {
        var settings = VersionSettings.Load(id);
        return settings.DisplayName ?? id;
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        var version = _activeVersionId;
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

    // ---------- 启动进度条（组合层动画） ----------
    private Microsoft.UI.Composition.Visual? _progressVisual;
    private double _progressValue;
    private bool _progressMarquee;

    /// <summary>取填充条的组合层 Visual，并把它锚在左侧，缩放才会从左往右长。</summary>
    private Microsoft.UI.Composition.Visual? ProgressVisual()
    {
        if (_progressVisual is not null) return _progressVisual;

        try
        {
            _progressVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(LaunchProgressFill);
            _progressVisual.CenterPoint = System.Numerics.Vector3.Zero;
        }
        catch (Exception ex)
        {
            Logger.Warn($"初始化进度条动画失败: {ex.Message}");
            return null;
        }

        return _progressVisual;
    }

    /// <summary>
    /// 把进度推进到指定比例。
    /// <para>
    /// 用组合层关键帧动画驱动 Scale，动画在合成器线程执行，
    /// 不参与 UI 线程布局，因此既平滑又不会拖慢窗口。
    /// （ProgressBar 的 Value 补间属于依赖动画，正好相反。）
    /// </para>
    /// </summary>
    private void SetProgressRatio(double ratio)
    {
        var visual = ProgressVisual();
        if (visual is null) return;

        StopMarquee(visual);

        var compositor = visual.Compositor;

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(
            1f,
            new System.Numerics.Vector3((float)ratio, 1f, 1f),
            compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0f, 0f),
                new System.Numerics.Vector2(0.2f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(280);

        visual.StartAnimation("Scale", animation);
        _progressValue = ratio;
        _progressMarquee = false;
    }

    /// <summary>
    /// 不确定态：一条 30% 宽的条从左滑到右，循环播放。
    /// 用于「等待游戏窗口」这种没有可量化进度的阶段。
    /// </summary>
    private void StartMarquee()
    {
        if (_progressMarquee) return;

        var visual = ProgressVisual();
        if (visual is null) return;

        var width = (float)Math.Max(1, LaunchProgressFill.ActualWidth);
        if (width <= 1) width = (float)Math.Max(1, LaunchProgress.ActualWidth);
        if (width <= 1) width = 400f;

        var compositor = visual.Compositor;

        // 只显示 30% 宽，再横向平移，形成走马灯效果
        visual.StopAnimation("Scale");
        visual.Scale = new System.Numerics.Vector3(0.3f, 1f, 1f);

        var move = compositor.CreateScalarKeyFrameAnimation();
        move.InsertKeyFrame(0f, 0f);
        move.InsertKeyFrame(1f, width * 0.7f);
        move.Duration = TimeSpan.FromSeconds(1.1);
        move.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;

        visual.StartAnimation("Offset.X", move);
        _progressMarquee = true;
    }

    private static void StopMarquee(Microsoft.UI.Composition.Visual visual)
    {
        visual.StopAnimation("Offset.X");
        visual.Offset = System.Numerics.Vector3.Zero;
    }

    /// <summary>启动阶段回调，更新提示文字与进度。</summary>
    private void OnLaunchStage(GameLaunchService.LaunchStage stage)
    {
        Dispatch(() =>
        {
            LaunchStageText.Text = stage.Message;

            var target = Math.Clamp(stage.Ratio, 0, 1);

            if (target > _progressValue + 0.001)
            {
                SetProgressRatio(target);
            }
            else
            {
                // 进度不再增长说明这一步没有可量化的进度
                // （最典型的是「等待游戏窗口」要等十几秒），
                // 切成走马灯，让用户看到还在动而不是停在某个位置。
                StartMarquee();
            }
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

            _progressValue = 0;
            _progressMarquee = false;

            var visual = ProgressVisual();
            if (visual is not null)
            {
                visual.StopAnimation("Scale");
                visual.Scale = new System.Numerics.Vector3(0f, 1f, 1f);
                StopMarquee(visual);
            }

            FadeIn(LaunchProgress);

            // 布局完成后再开走动效，否则拿不到填充条的真实宽度
            _ = DispatcherQueue.TryEnqueue(StartMarquee);
        }
        else
        {
            LaunchButton.Content = "启动";
            LaunchStageText.Text = string.Empty;

            _progressValue = 0;
            _progressMarquee = false;

            var visual = ProgressVisual();
            if (visual is not null)
            {
                visual.StopAnimation("Scale");
                visual.Scale = new System.Numerics.Vector3(0f, 1f, 1f);
                StopMarquee(visual);
            }
        }
    }

    /// <summary>让刚出现的进度条淡入，避免直接闪现。</summary>
    private static void FadeIn(UIElement element)
    {
        element.Opacity = 0;

        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
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
    private void GoVersions_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("versions");
    private void GoLink_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("link");

    private async void ManageVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = _activeVersionId;
        if (version is null)
        {
            await ShowAsync("还没有版本", "请先在下载页安装一个 Minecraft 版本。");
            return;
        }

        MainWindow.Instance?.ShowVersionSettings(version);
    }
}









