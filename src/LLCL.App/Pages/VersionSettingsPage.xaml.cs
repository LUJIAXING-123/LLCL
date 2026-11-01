using System.Diagnostics;
using LLCL.Core;
using LLCL.Core.Minecraft;
using LLCL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Pages;

/// <summary>单个版本的设置与详情页（对标 PCL 的版本设置）。</summary>
public sealed partial class VersionSettingsPage : Page
{
    private readonly string _versionId;
    private VersionSettings _settings = new();

    public VersionSettingsPage(string versionId)
    {
        _versionId = versionId;
        InitializeComponent();
        _settings = VersionSettings.Load(versionId);

        Loaded += (_, _) =>
        {
            TitleText.Text = _settings.DisplayName ?? versionId;
            SubtitleText.Text = _versionId;
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            Show("overview");
        };
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Show(tag);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("download");

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await GameLaunchService.EnsureAndLaunchAsync(_versionId, this);
            if (!result.Success && result.Message is not null)
                await ShowAsync("启动失败", result.NeedsJava ? "未找到可用的 Java 运行时。" : result.Message);
        }
        catch (Exception ex)
        {
            await ShowAsync("启动失败", ex.Message);
        }
    }

    private void Show(string tag)
    {
        var panel = new StackPanel { Spacing = 16 };

        switch (tag)
        {
            case "launch": BuildLaunch(panel); break;
            case "game": BuildGame(panel); break;
            case "mods": BuildMods(panel); break;
            case "other": BuildOther(panel); break;
            default: BuildOverview(panel); break;
        }

        SettingsFrame.Content = new ScrollViewer
        {
            Content = new Grid
            {
                Padding = new Thickness(24, 8, 24, 24),
                MaxWidth = 720,
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

    private void Save() => _settings.Save();

    /// <summary>
    /// 版本隔离的三态选择器：跟随规则 / 强制开启 / 强制关闭。
    /// 与 PCL 的语义一致，并展示当前判定来源。
    /// </summary>
    private ComboBox MakeIsolationSelector()
    {
        var combo = new ComboBox
        {
            Header = "隔离方式",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        combo.Items.Add("跟随规则（自动检测 + 全局方案）");
        combo.Items.Add("强制开启隔离");
        combo.Items.Add("强制关闭隔离");

        combo.SelectedIndex = _settings.IsolationOverride switch
        {
            true => 1,
            false => 2,
            _ => 0
        };

        var decision = VersionIsolation.Resolve(_versionId);
        var status = new TextBlock
        {
            Text = $"当前实际：{(decision.Isolated ? "已隔离" : "共享 .minecraft 根目录")}" +
                   $"（{decision.ReasonText}）\n目录：{VersionIsolation.GetGameDir(_versionId)}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"]
        };

        combo.SelectionChanged += async (_, _) =>
        {
            var wasIsolated = VersionIsolation.IsIsolated(_versionId);

            _settings.IsolationOverride = combo.SelectedIndex switch
            {
                1 => true,
                2 => false,
                _ => null
            };
            Save();

            var nowIsolated = VersionIsolation.IsIsolated(_versionId);

            // 隔离状态真正改变时，迁移已有内容，避免用户的 mods / 存档"消失"
            if (wasIsolated != nowIsolated)
            {
                var moved = VersionIsolation.MigrateContent(_versionId, nowIsolated);
                if (moved > 0)
                {
                    await ShowAsync("已迁移版本数据",
                        $"已将 {moved} 个文件/目录" +
                        (nowIsolated ? "复制到版本隔离目录。" : "复制回 .minecraft 根目录。") +
                        "\n\n原位置的文件仍然保留，确认无误后可手动删除。");
                }
            }

            // 刷新整页，让目录列表与实际路径同步
            Show("game");
        };

        _isolationStatus = status;
        return combo;
    }

    private TextBlock? _isolationStatus;
    private ToggleSwitch MakeToggle(string header, bool value, Action<bool> apply)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = value };
        toggle.Toggled += (_, _) =>
        {
            apply(toggle.IsOn);
            Save();
        };
        return toggle;
    }

    private NumberBox MakeNumber(string header, double value, double min, double max, Action<int> apply)
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

    private TextBox MakeText(string header, string value, Action<string> apply, string? placeholder = null)
    {
        var box = new TextBox { Header = header, Text = value, PlaceholderText = placeholder ?? string.Empty };
        box.TextChanged += (_, _) =>
        {
            apply(box.Text);
            Save();
        };
        return box;
    }

    #region 概览

    private void BuildOverview(StackPanel panel)
    {
        var entry = VersionManager.ScanAll().FirstOrDefault(v => v.Id == _versionId);

        panel.Children.Add(Header("版本信息"));
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(Hint($"版本 ID：{_versionId}"));
        if (entry is not null)
        {
            info.Children.Add(Hint($"加载器：{entry.Loader}" +
                                   (entry.LoaderVersion is null ? string.Empty : $" {entry.LoaderVersion}")));
            info.Children.Add(Hint($"需要的 Java：{entry.JavaMajor?.ToString() ?? "未知"}"));
            info.Children.Add(Hint($"匹配的 Java：{entry.JavaDisplay}"));
            info.Children.Add(Hint($"目录大小：{entry.SizeDisplay}"));
            if (entry.IsBroken) info.Children.Add(Hint($"⚠ 版本损坏：{entry.BrokenReason}"));
        }

        panel.Children.Add(info);

        panel.Children.Add(Header("显示与排序"));
        panel.Children.Add(MakeText("显示名称（留空则使用版本 ID）", _settings.DisplayName ?? string.Empty,
            v => _settings.DisplayName = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), _versionId));
        panel.Children.Add(MakeNumber("排序序号（越大越靠前）", _settings.SortOrder, -1000, 1000,
            v => _settings.SortOrder = v));
        panel.Children.Add(MakeText("备注", _settings.Comment ?? string.Empty,
            v => _settings.Comment = string.IsNullOrWhiteSpace(v) ? null : v));

        var openDir = new Button { Content = "打开版本目录", Margin = new Thickness(0, 8, 0, 0) };
        openDir.Click += (_, _) => OpenPath(VersionManager.GetDirectory(_versionId));
        panel.Children.Add(openDir);
    }

    #endregion

    #region 启动设置

    private void BuildLaunch(StackPanel panel)
    {
        panel.Children.Add(Header("Java"));

        panel.Children.Add(MakeToggle("单独设置此版本的 Java", _settings.OverrideJava ?? false,
            v => { _settings.OverrideJava = v; ApplyJavaOverride(v); }));

        var javaBox = MakeText("Java 路径", _settings.JavaPath ?? string.Empty,
            v => _settings.JavaPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            @"C:\Program Files\Java\jdk-21\bin\javaw.exe");
        javaBox.IsEnabled = _settings.OverrideJava ?? false;
        panel.Children.Add(javaBox);

        var detected = new ComboBox
        {
            Header = "从已检测的 Java 中选择",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(detected);
        _ = LoadJavaOptionsAsync(detected, javaBox);

        panel.Children.Add(Header("内存"));
        panel.Children.Add(MakeToggle("单独设置此版本的内存", _settings.OverrideMemory ?? false,
            v => _settings.OverrideMemory = v));
        panel.Children.Add(MakeNumber("最大内存 (MB)", _settings.MaxMemoryMb, 512, 32768,
            v => _settings.MaxMemoryMb = v));
        panel.Children.Add(MakeNumber("最小内存 (MB)", _settings.MinMemoryMb, 128, 4096,
            v => _settings.MinMemoryMb = v));

        panel.Children.Add(Header("参数"));
        panel.Children.Add(MakeText("额外 JVM 参数（空格分隔）", string.Join(' ', _settings.ExtraJvmArgs),
            v => _settings.ExtraJvmArgs = v.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        panel.Children.Add(MakeText("额外游戏参数（空格分隔）", string.Join(' ', _settings.ExtraGameArgs),
            v => _settings.ExtraGameArgs = v.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    private void ApplyJavaOverride(bool enabled)
    {
        // 重新渲染以更新输入框可用状态
        Show("launch");
    }

    private async Task LoadJavaOptionsAsync(ComboBox combo, TextBox javaBox)
    {
        try
        {
            var javas = await JavaScanner.ScanAsync();
            foreach (var java in javas)
                combo.Items.Add($"{java.Display} — {java.Path}");

            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0 || combo.SelectedIndex >= javas.Count) return;
                var picked = javas[combo.SelectedIndex];
                _settings.JavaPath = picked.Path;
                _settings.OverrideJava = true;
                javaBox.Text = picked.Path;
                Save();
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载 Java 列表失败: {ex.Message}");
        }
    }

    #endregion

    #region 游戏设置

    private void BuildGame(StackPanel panel)
    {
        panel.Children.Add(Header("窗口"));

        panel.Children.Add(MakeToggle("单独设置此版本的窗口", _settings.OverrideWindow ?? false,
            v => _settings.OverrideWindow = v));
        panel.Children.Add(MakeToggle("全屏启动", _settings.Fullscreen, v => _settings.Fullscreen = v));
        panel.Children.Add(MakeNumber("窗口宽度", _settings.Width, 320, 7680, v => _settings.Width = v));
        panel.Children.Add(MakeNumber("窗口高度", _settings.Height, 240, 4320, v => _settings.Height = v));

        panel.Children.Add(Header("版本隔离"));
        panel.Children.Add(Hint(
            "与 PCL 的版本隔离等价：关闭时使用 .minecraft 根目录，各版本共享 mods 与存档；" +
            "开启后该版本使用 versions/<id> 作为独立运行目录。\n" +
            "留空（跟随规则）时：版本目录下已有 mods 或存档会自动开启，否则按全局方案判定。"));
        panel.Children.Add(MakeIsolationSelector());
        if (_isolationStatus is not null) panel.Children.Add(_isolationStatus);

        panel.Children.Add(Header("目录"));
        var dirs = new StackPanel { Spacing = 6 };
        foreach (var (name, sub) in new[]
                 {
                     ("mods", "mods"), ("saves", "saves"), ("resourcepacks", "resourcepacks"),
                     ("shaderpacks", "shaderpacks"), ("config", "config"), ("logs", "logs")
                 })
        {
            var path = Path.Combine(_settings.GetGameDir(_versionId), sub);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var button = new Button { Content = name };
            button.Click += (_, _) => OpenPath(path);
            row.Children.Add(button);
            row.Children.Add(new TextBlock
            {
                Text = Directory.Exists(path) ? $"{Directory.GetFileSystemEntries(path).Length} 项" : "（不存在）",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            dirs.Children.Add(row);
        }

        panel.Children.Add(dirs);
    }

    #endregion

    #region mods 管理

    private void BuildMods(StackPanel panel)
    {
        // 与 PCL 一致：默认共享 .minecraft/mods，开启版本隔离后使用 versions/<id>/mods
        var modsDir = Path.Combine(_settings.GetGameDir(_versionId), "mods");
        Directory.CreateDirectory(modsDir);

        var decision = VersionIsolation.Resolve(_versionId);
        panel.Children.Add(Hint(
            $"当前为{(decision.Isolated ? "版本隔离" : "共享")}模式（{decision.ReasonText}），mod 目录：{modsDir}"));

        panel.Children.Add(Header("已安装的 mod"));
        var files = Directory.GetFiles(modsDir, "*.jar")
            .Concat(Directory.GetFiles(modsDir, "*.jar.disabled"))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n)
            .ToList();

        if (files.Count == 0)
        {
            panel.Children.Add(Hint("此版本还没有安装任何 mod。可在“下载 - 社区资源”中搜索并安装。"));
        }
        else
        {
            var list = new ListView { MaxHeight = 320, SelectionMode = ListViewSelectionMode.Single };
            foreach (var file in files) list.Items.Add(file);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };

            var toggleButton = new Button { Content = "启用 / 禁用" };
            toggleButton.Click += async (_, _) =>
            {
                if (list.SelectedItem is not string selected) return;
                var full = Path.Combine(modsDir, selected);
                try
                {
                    if (full.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        File.Move(full, full[..^".disabled".Length], true);
                    else
                        File.Move(full, full + ".disabled", true);
                    Show("mods");
                }
                catch (Exception ex)
                {
                    await ShowAsync("操作失败", ex.Message);
                }
            };

            var deleteButton = new Button { Content = "删除" };
            deleteButton.Click += async (_, _) =>
            {
                if (list.SelectedItem is not string selected) return;
                if (!await ConfirmAsync("删除 mod", $"确定删除 {selected} 吗？")) return;
                try
                {
                    File.Delete(Path.Combine(modsDir, selected));
                    Show("mods");
                }
                catch (Exception ex)
                {
                    await ShowAsync("删除失败", ex.Message);
                }
            };

            actions.Children.Add(toggleButton);
            actions.Children.Add(deleteButton);

            panel.Children.Add(list);
            panel.Children.Add(actions);
        }

        var openButton = new Button { Content = "打开 mods 目录", Margin = new Thickness(0, 8, 0, 0) };
        openButton.Click += (_, _) => OpenPath(modsDir);
        panel.Children.Add(openButton);
    }

    #endregion

    #region 其他

    private void BuildOther(StackPanel panel)
    {
        panel.Children.Add(Header("重命名"));

        var renameBox = new TextBox
        {
            Header = "新的版本 ID",
            Text = _versionId,
            PlaceholderText = "例如 1.21.1-Fabric"
        };
        panel.Children.Add(renameBox);

        var renameButton = new Button { Content = "重命名版本", Margin = new Thickness(0, 4, 0, 0) };
        renameButton.Click += async (_, _) =>
        {
            var newId = renameBox.Text.Trim();
            if (newId == _versionId) return;

            if (!VersionManager.Rename(_versionId, newId, out var error))
            {
                await ShowAsync("重命名失败", error ?? "未知错误");
                return;
            }

            await ShowAsync("重命名成功", $"版本已重命名为 {newId}");
            MainWindow.Instance?.Navigate("download");
        };
        panel.Children.Add(renameButton);

        panel.Children.Add(Header("危险操作"));

        var repairButton = new Button { Content = "修复 / 补全文件（重新下载原版文件）" };
        repairButton.Click += async (_, _) =>
        {
            try
            {
                await VanillaInstaller.InstallAsync(_versionId, Paths.VersionsDir, null);
                await ShowAsync("修复完成", "已校验并补全版本文件。");
            }
            catch (Exception ex)
            {
                await ShowAsync("修复失败", ex.Message);
            }
        };
        panel.Children.Add(repairButton);

        var deleteButton = new Button { Content = "删除此版本" };
        deleteButton.Click += async (_, _) =>
        {
            if (!await ConfirmAsync("删除版本",
                    $"确定删除版本「{_versionId}」吗？此操作会删除该版本的全部文件（含 mods、存档），且不可撤销。"))
                return;

            if (!VersionManager.Delete(_versionId, out var error))
            {
                await ShowAsync("删除失败", error ?? "未知错误");
                return;
            }

            // 删除成功后回到下载页；版本列表由 VersionsChanged 事件自动刷新
            MainWindow.Instance?.Navigate("download");
        };
        panel.Children.Add(deleteButton);
    }

    #endregion

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开目录失败 {path}: {ex.Message}");
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

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}









