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
    public sealed class VersionItem
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public string LoaderText { get; init; } = string.Empty;
        public bool IsBroken { get; init; }
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
        _all = VersionManager.ScanAll()
            .OrderByDescending(v => v.SortOrder)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new VersionItem
            {
                Id = v.Id,
                DisplayName = v.DisplayName,
                Subtitle = v.IsBroken
                    ? $"⚠ {v.BrokenReason}"
                    : $"{v.Loader} · {v.SizeDisplay} · {v.JavaDisplay}",
                LoaderText = v.IsBroken ? "损坏" : DescribeLoader(v),
                IsBroken = v.IsBroken
            })
            .ToList();

        ApplyFilter();

        SummaryText.Text = _all.Count == 0
            ? "暂无已安装版本"
            : $"共 {_all.Count} 个版本";
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

    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VersionItem item)
            MainWindow.Instance?.ShowVersionSettings(item.Id);
    }

    private void ItemSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        MainWindow.Instance?.ShowVersionSettings(id);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("home");

    private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("download");
}
