using LLCL.Core;
using LLCL.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LLCL.Pages;

/// <summary>
/// 版本列表页。与 PCL 一致：版本以独立界面集中管理，
/// 不再挤在首页用一个下拉框选择。
/// </summary>
public sealed partial class VersionsPage : Page
{
    /// <summary>列表项。绑定字段必须是属性，XAML 才能取值。</summary>
    public sealed class VersionItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;

        private string _subtitle = string.Empty;

        /// <summary>副标题。目录大小在后台算完后再补进来，所以需要可通知。</summary>
        public string Subtitle
        {
            get => _subtitle;
            set
            {
                if (_subtitle == value) return;
                _subtitle = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Subtitle)));
            }
        }

        public string LoaderText { get; init; } = string.Empty;
        public bool IsBroken { get; init; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private List<VersionItem> _all = [];

    public VersionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        VersionManager.VersionsChanged += OnVersionsChanged;
        Unloaded += (_, _) => VersionManager.VersionsChanged -= OnVersionsChanged;
    }

    private void OnVersionsChanged()
    {
        // 事件可能来自后台线程
        _ = DispatcherQueue.TryEnqueue(Reload);
    }

    private void Reload()
    {
        // 先不带大小扫一遍，保证列表立刻显示出来。
        // 递归统计目录大小很慢，放在这里会把切页卡住。
        var entries = VersionManager.ScanAll()
            .OrderByDescending(v => v.SortOrder)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _all = entries
            .Select(v => new VersionItem
            {
                Id = v.Id,
                DisplayName = v.DisplayName,
                Subtitle = v.IsBroken
                    ? $"⚠ {v.BrokenReason}"
                    : $"{v.Loader} · {v.JavaDisplay}",
                LoaderText = v.IsBroken ? "损坏" : DescribeLoader(v),
                IsBroken = v.IsBroken
            })
            .ToList();

        ApplyFilter();

        SummaryText.Text = _all.Count == 0
            ? "暂无已安装版本"
            : $"共 {_all.Count} 个版本";

        FillSizesInBackground();
    }

    /// <summary>
    /// 在后台线程统计各版本目录大小，算完再逐项补进副标题。
    /// 这样列表能立刻出现，不会因为统计磁盘占用而白屏。
    /// </summary>
    private void FillSizesInBackground()
    {
        var items = _all.Where(i => !i.IsBroken).ToList();
        if (items.Count == 0) return;

        var versionsRoot = Paths.VersionsDir;

        _ = Task.Run(() =>
        {
            foreach (var item in items)
            {
                long size;
                try
                {
                    size = VersionManager.ComputeSize(Path.Combine(versionsRoot, item.Id));
                }
                catch
                {
                    continue;
                }

                var text = size switch
                {
                    < 1024 => $"{size} B",
                    < 1024 * 1024 => $"{size / 1024.0:F1} KB",
                    < 1024L * 1024 * 1024 => $"{size / 1024.0 / 1024:F1} MB",
                    _ => $"{size / 1024.0 / 1024 / 1024:F2} GB"
                };

                var subtitle = $"{item.LoaderText} · {text}";

                _ = DispatcherQueue.TryEnqueue(() => item.Subtitle = subtitle);
            }
        });
    }

    private static string DescribeLoader(VersionEntry v)
    {
        var name = v.Loader?.ToLowerInvariant() switch
        {
            "vanilla" or null or "" => "原版",
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            "liteloader" => "LiteLoader",
            "optifine" => "OptiFine",
            _ => v.Loader
        };

        return string.IsNullOrWhiteSpace(v.LoaderVersion) ? name : $"{name} {v.LoaderVersion}";
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;

        var items = string.IsNullOrWhiteSpace(keyword)
            ? _all
            : _all.Where(v =>
                v.DisplayName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        VersionList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "还没有安装任何版本，点右上角「新建安装」开始。"
            : "没有匹配的版本。";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>
    /// 点条目 = 选中为启动目标并回到首页；
    /// 想改设置要点右侧的「设置」按钮，避免误点进设置页。
    /// </summary>
    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not VersionItem item) return;

        // 损坏的版本不能启动，直接送去设置页修
        if (item.IsBroken)
        {
            MainWindow.Instance?.ShowVersionSettings(item.Id);
            return;
        }

        LauncherConfig.Current.LastInstance = item.Id;
        LauncherConfig.Current.Save();

        MainWindow.Instance?.Navigate("home");
    }

    private void ItemSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        MainWindow.Instance?.ShowVersionSettings(id);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("home");

    private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("download");
}
