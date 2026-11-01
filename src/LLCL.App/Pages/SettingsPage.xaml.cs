using LLCL.Core;
using LLCL.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Pages;

/// <summary>设置页（无主题修改项）。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            Show("account");
        };
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Show(tag);
    }

    private void Show(string tag)
    {
        var config = LauncherConfig.Current;
        var panel = new StackPanel { Spacing = 16 };

        switch (tag)
        {
            case "account": BuildAccount(panel, config); break;
            case "launch": BuildLaunch(panel, config); break;
            case "download": BuildDownload(panel, config); break;
            case "link": BuildLink(panel, config); break;
            default: BuildLauncher(panel, config); break;
        }

        SettingsFrame.Content = new ScrollViewer
        {
            Content = new Grid
            {
                Padding = new Thickness(24, 8, 24, 24),
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children = { panel }
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        TextWrapping = TextWrapping.Wrap
    };

    private static void Save() => LauncherConfig.Current.Save();

    private static ToggleSwitch CreateToggle(string header, bool value, Action<bool> apply)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = value };
        toggle.Toggled += (_, _) =>
        {
            apply(toggle.IsOn);
            Save();
        };
        return toggle;
    }

    private static NumberBox CreateNumber(string header, double value, double min, double max,
        Action<int> apply)
    {
        var box = new NumberBox
        {
            Header = header,
            Value = value,
            Minimum = min,
            Maximum = max,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        box.ValueChanged += (_, args) =>
        {
            if (double.IsNaN(args.NewValue)) return;
            apply((int)args.NewValue);
            Save();
        };
        return box;
    }

    private static TextBox CreateText(string header, string value, Action<string> apply,
        string? placeholder = null, bool multiline = false)
    {
        var box = new TextBox
        {
            Header = header,
            Text = value,
            PlaceholderText = placeholder ?? string.Empty,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (multiline) box.Height = 90;
        box.TextChanged += (_, _) =>
        {
            apply(box.Text);
            Save();
        };
        return box;
    }

    #region 账号

    private void BuildAccount(StackPanel panel, LauncherConfig config)
    {
        panel.Children.Add(Header("账号"));
        panel.Children.Add(Hint("LLCL 默认使用离线账号启动，不需要正版账号验证。"));

        panel.Children.Add(CreateText("离线用户名", config.OfflineUserName,
            v => config.OfflineUserName = string.IsNullOrWhiteSpace(v) ? "Player" : v.Trim()));

        panel.Children.Add(CreateToggle("使用离线账号", config.AccountType != "microsoft",
            v => config.AccountType = v ? "offline" : "microsoft"));

        panel.Children.Add(CreateText("微软账号名（可选）", config.MicrosoftUserName ?? string.Empty,
            v => config.MicrosoftUserName = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            "选择微软账号模式时使用"));

        panel.Children.Add(CreateText("微软账号 UUID（可选）", config.MicrosoftUuid ?? string.Empty,
            v => config.MicrosoftUuid = string.IsNullOrWhiteSpace(v) ? null : v.Trim()));
    }

    #endregion

    #region 启动

    private void BuildLaunch(StackPanel panel, LauncherConfig config)
    {
        panel.Children.Add(Header("启动"));

        panel.Children.Add(CreateNumber("最大内存 (MB，0 = 自动)", config.MaxMemoryMb, 0, 32768,
            v => config.MaxMemoryMb = v));
        panel.Children.Add(CreateNumber("最小内存 (MB)", config.MinMemoryMb, 128, 4096,
            v => config.MinMemoryMb = v));

        panel.Children.Add(CreateToggle("自动选择 Java", config.AutoSelectJava,
            v => config.AutoSelectJava = v));

        panel.Children.Add(CreateText("Java 路径（自动选择关闭时使用）", config.JavaPath ?? string.Empty,
            v => config.JavaPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            @"C:\Program Files\Java\jdk-21\bin\javaw.exe"));

        panel.Children.Add(CreateToggle("全屏启动", config.Fullscreen, v => config.Fullscreen = v));

        panel.Children.Add(CreateText("额外 JVM 参数（空格分隔）", string.Join(' ', config.ExtraJvmArgs),
            v => config.ExtraJvmArgs = v.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

        panel.Children.Add(CreateText("额外游戏参数（空格分隔）", string.Join(' ', config.ExtraGameArgs),
            v => config.ExtraGameArgs = v.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

        panel.Children.Add(CreateToggle("启动前自动补全缺失文件", config.AutoRepairFiles,
            v => config.AutoRepairFiles = v));

        // Java 检测放到后台线程，绝不阻塞 UI
        panel.Children.Add(Header("检测到的 Java"));
        var javaList = new ListView { MaxHeight = 180, SelectionMode = ListViewSelectionMode.None };
        var loading = Hint("正在后台检测 Java…");
        panel.Children.Add(javaList);
        panel.Children.Add(loading);

        _ = LoadJavaAsync(javaList, loading);
    }

    private static async Task LoadJavaAsync(ListView list, TextBlock loading)
    {
        try
        {
            var javas = await JavaScanner.ScanAsync(true);
            list.Items.Clear();
            foreach (var java in javas)
                list.Items.Add($"{java.Display} — {java.Path}");

            loading.Text = javas.Count == 0
                ? "未检测到 Java，请安装 Java 或手动指定路径。"
                : $"共检测到 {javas.Count} 个 Java 运行时";
        }
        catch (Exception ex)
        {
            loading.Text = "检测 Java 失败：" + ex.Message;
        }
    }

    #endregion

    #region 下载

    private void BuildDownload(StackPanel panel, LauncherConfig config)
    {
        panel.Children.Add(Header("下载"));

        var combo = new ComboBox { Header = "下载源", HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Items.Add("自动（官方优先，失败时回退镜像）");
        combo.Items.Add("官方源");
        combo.Items.Add("BMCLAPI 镜像");
        combo.Items.Add("自定义镜像");
        combo.SelectedIndex = Math.Clamp((int)config.Source, 0, 3);
        combo.SelectionChanged += (_, _) =>
        {
            config.Source = (DownloadSource)Math.Max(0, combo.SelectedIndex);
            Save();
        };
        panel.Children.Add(combo);

        panel.Children.Add(CreateText("自定义镜像地址", config.CustomSourceUrl,
            v => config.CustomSourceUrl = v.Trim(), "https://bmclapi2.bangbang93.com"));

        panel.Children.Add(CreateNumber("下载线程数", config.DownloadThreads, 1, 256,
            v => config.DownloadThreads = v));
        panel.Children.Add(CreateNumber("重试次数", config.DownloadRetry, 1, 10,
            v => config.DownloadRetry = v));
        panel.Children.Add(CreateNumber("限速 (KB/s，0 = 不限速)", config.DownloadSpeedLimitKb, 0, 102400,
            v => config.DownloadSpeedLimitKb = v));
    }

    #endregion

    #region 联机

    private void BuildLink(StackPanel panel, LauncherConfig config)
    {
        panel.Children.Add(Header("联机"));

        panel.Children.Add(CreateText("联机显示名", config.LobbyUserName,
            v => config.LobbyUserName = v.Trim(), "留空则使用离线用户名"));

        panel.Children.Add(CreateText("中继服务器", config.EasyTierRelay,
            v => config.EasyTierRelay = v.Trim()));

        panel.Children.Add(CreateText("额外中继节点（分号分隔）", config.ExtraRelayNodes,
            v => config.ExtraRelayNodes = v,
            "tcp://public2.easytier.cn:54321;tcp://public.easytier.top:11010", true));

        panel.Children.Add(CreateToggle("强制走中继（关闭 P2P）", config.ForceRelay,
            v => config.ForceRelay = v));
        panel.Children.Add(CreateToggle("启用 IPv6", config.EnableIPv6, v => config.EnableIPv6 = v));
        panel.Children.Add(CreateToggle("自动检查并更新 EasyTier 内核", config.AutoUpdateEasyTier,
            v => config.AutoUpdateEasyTier = v));

        var versions = LLCL.Core.Link.EasyTierCore.GetInstalledVersions();
        var info = Hint(versions.Count == 0
            ? "尚未安装 EasyTier 内核，首次联机时自动下载。"
            : "已安装内核：" + string.Join("、", versions));

        var updateButton = new Button { Content = "立即检查更新", Margin = new Thickness(0, 8, 0, 0) };
        updateButton.Click += async (_, _) =>
        {
            updateButton.IsEnabled = false;
            info.Text = "正在检查更新...";
            try
            {
                var latest = await LLCL.Core.Link.EasyTierCore.CheckUpdateAsync(true);
                info.Text = latest is null
                    ? "已是最新版本：" + string.Join("、", LLCL.Core.Link.EasyTierCore.GetInstalledVersions())
                    : "已更新到 " + latest;
            }
            catch (Exception ex)
            {
                info.Text = "更新失败：" + ex.Message;
            }
            finally
            {
                updateButton.IsEnabled = true;
            }
        };

        panel.Children.Add(info);
        panel.Children.Add(updateButton);
    }

    #endregion

    #region 启动器

    private void BuildLauncher(StackPanel panel, LauncherConfig config)
    {
        panel.Children.Add(Header("启动器"));

        panel.Children.Add(Hint(Paths.IsPortable
            ? $"游戏目录与 PCL 相同（{Paths.MinecraftFolderName}），启动器配置、日志、缓存等存放在 {Paths.DataFolderName} 文件夹。"
            : $"启动器目录不可写，配置改存至 {Paths.DataRoot}，游戏目录为 {Paths.MinecraftDir}。"));

        var mcDirBox = new TextBox
        {
            Header = $"游戏目录（留空则使用 exe 同级 {Paths.MinecraftFolderName}）",
            Text = config.CustomMinecraftDir ?? Paths.MinecraftDir,
            PlaceholderText = Path.Combine(Paths.LauncherDir, Paths.MinecraftFolderName)
        };
        mcDirBox.TextChanged += (_, _) =>
        {
            config.CustomMinecraftDir = string.IsNullOrWhiteSpace(mcDirBox.Text) ? null : mcDirBox.Text.Trim();
            if (config.CustomMinecraftDir is not null) Paths.SetMinecraftDir(config.CustomMinecraftDir);
            Save();
        };
        panel.Children.Add(mcDirBox);
        panel.Children.Add(new TextBox
        {
            Header = $"{Paths.DataFolderName} 文件夹（配置 / 日志 / 缓存 / 工具）",
            Text = Paths.DataRoot,
            IsReadOnly = true
        });

        panel.Children.Add(CreateToggle("窗口使用云母（Mica）材质（重启生效）", config.UseMica,
            v => config.UseMica = v));

        var openLogs = new Button { Content = "打开日志目录", Margin = new Thickness(0, 8, 0, 0) };
        openLogs.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Paths.LogDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"打开日志目录失败: {ex.Message}");
            }
        };
        panel.Children.Add(openLogs);
    }

    #endregion
}


