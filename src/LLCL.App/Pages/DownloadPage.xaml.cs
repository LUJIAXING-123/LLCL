using System.Collections.ObjectModel;
using System.Diagnostics;
using LLCL.Core;
using LLCL.Core.Minecraft;
using LLCL.Core.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LLCL.Pages;

/// <summary>下载中心：原版版本、模组加载器、社区资源。</summary>
public sealed partial class DownloadPage : Page
{
    private readonly ObservableCollection<VersionItem> _versions = new();
    private readonly ObservableCollection<ResourceCard> _resources = new();
    private VersionManifest? _manifest;
    private CancellationTokenSource? _cts;
    private bool _busy;

    private string _kind = "vanilla";
    private string? _selectedVersion;
    private ResourceHit? _selectedResource;
    private List<ResourceFile> _resourceFiles = [];

    public DownloadPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _versions;
        ResourceList.ItemsSource = _resources;

        ResourceSourceCombo.Items.Add("Modrinth");
        ResourceSourceCombo.Items.Add("CurseForge");

        // 注意：XAML 中的 Checked/SelectionChanged 可能在 InitializeComponent 期间
        // 就已触发（此时部分控件尚未赋值），因此初始加载统一放到 Loaded 之后。
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = LoadAsync();
        };
    }

    private bool _loaded;

    #region 初始化

    private async Task LoadAsync()
    {
        SourceText.Text = LauncherConfig.Current.Source switch
        {
            DownloadSource.BmclApi => "下载源：BMCLAPI 镜像",
            DownloadSource.Custom => $"下载源：{LauncherConfig.Current.CustomSourceUrl}",
            DownloadSource.Official => "下载源：官方",
            _ => "下载源：自动（官方优先，失败回退镜像）"
        };

        try
        {
            _manifest = await VersionManifestService.GetManifestAsync(true);
            FilterVersions();
            PopulateMcVersions();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "获取版本清单失败");
            SourceText.Text = "版本清单获取失败：" + ex.Message;
        }
    }

    private void PopulateMcVersions()
    {
        if (_manifest is null) return;

        var current = McVersionCombo.SelectedItem as string;
        McVersionCombo.Items.Clear();
        foreach (var version in _manifest.Versions.Where(v => v.Type == "release").Take(120))
            McVersionCombo.Items.Add(version.Id);

        if (current is not null && McVersionCombo.Items.Contains(current))
            McVersionCombo.SelectedItem = current;
        else if (McVersionCombo.Items.Count > 0)
            McVersionCombo.SelectedIndex = 0;

        ResourceVersionCombo.Items.Clear();
        ResourceVersionCombo.Items.Add("全部版本");
        foreach (var version in _manifest.Versions.Where(v => v.Type == "release").Take(60))
            ResourceVersionCombo.Items.Add(version.Id);
        ResourceVersionCombo.SelectedIndex = 0;
    }

    #endregion

    #region 分类切换

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        _kind = clicked.Tag as string ?? "vanilla";
        TabVanilla.IsChecked = _kind == "vanilla";
        TabLoader.IsChecked = _kind == "loader";
        TabResource.IsChecked = _kind == "resource";

        PanelVanilla.Visibility = _kind == "vanilla" ? Visibility.Visible : Visibility.Collapsed;
        PanelLoader.Visibility = _kind == "loader" ? Visibility.Visible : Visibility.Collapsed;
        PanelResource.Visibility = _kind == "resource" ? Visibility.Visible : Visibility.Collapsed;

        SelectedText.Text = _kind switch
        {
            "loader" => "选择一个游戏版本与加载器版本",
            "resource" => "搜索并选择一个资源",
            _ => "选择一个游戏版本"
        };

        InstallButton.IsEnabled = false;
        DetailButton.IsEnabled = false;
        _selectedVersion = null;
        _selectedResource = null;

        if (!_loaded) return;
        if (_kind == "loader") _ = LoadLoaderVersionsAsync();
        if (_kind == "resource") _ = SearchResourcesAsync();
    }

    #endregion

    #region 原版

    private void FilterVersions()
    {
        if (_manifest is null) return;

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var types = new List<string>();
        if (RadioRelease.IsChecked == true) types.Add("release");
        else if (RadioSnapshot.IsChecked == true) types.Add("snapshot");
        else if (RadioOld.IsChecked == true) types.AddRange(["old_alpha", "old_beta"]);

        _versions.Clear();
        foreach (var version in _manifest.Versions)
        {
            if (types.Count > 0 && !types.Contains(version.Type)) continue;
            if (query.Length > 0 && !version.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            _versions.Add(new VersionItem
            {
                Id = version.Id,
                TypeDisplay = version.Type switch
                {
                    "release" => "正式版",
                    "snapshot" => "快照",
                    "old_alpha" => "远古 Alpha",
                    "old_beta" => "远古 Beta",
                    _ => version.Type
                },
                ReleaseDate = version.ReleaseTime.Length >= 10 ? version.ReleaseTime[..10] : version.ReleaseTime,
                Url = version.Url
            });
        }
    }

    private void Type_Changed(object sender, RoutedEventArgs e) => FilterVersions();

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) FilterVersions();
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionItem item) return;
        _selectedVersion = item.Id;
        SelectedText.Text = $"将安装 {item.Id}";
        InstallButton.IsEnabled = !_busy;
        DetailButton.IsEnabled = Directory.Exists(Path.Combine(Paths.VersionsDir, item.Id));
    }

    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VersionItem item) VersionList.SelectedItem = item;
    }

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVersion is null) return;
        MainWindow.Instance?.ShowVersionSettings(_selectedVersion);
    }

    #endregion

    #region 加载器

    private async Task LoadLoaderVersionsAsync()
    {
        var mc = McVersionCombo.SelectedItem as string;
        if (mc is null) return;

        LoaderVersionCombo.Items.Clear();
        LoaderSummary.Text = "正在查询加载器版本…";
        InstallLoaderButton.IsEnabled = false;

        try
        {
            var versions = await LoaderInstaller.GetVersionsAsync(CurrentLoaderKind(), mc);
            foreach (var v in versions.Take(80)) LoaderVersionCombo.Items.Add(v.Version);

            if (LoaderVersionCombo.Items.Count > 0) LoaderVersionCombo.SelectedIndex = 0;

            LoaderSummary.Text = versions.Count == 0
                ? $"未找到适用于 {mc} 的 {CurrentLoaderName()} 版本"
                : $"{CurrentLoaderName()} · {mc} · 共 {versions.Count} 个可用版本";
            InstallLoaderButton.IsEnabled = versions.Count > 0;
            UpdateLoaderNote(mc);
        }
        catch (Exception ex)
        {
            LoaderSummary.Text = "查询失败：" + ex.Message;
            Logger.Error(ex, "查询加载器版本失败");
        }
    }

    private void UpdateLoaderNote(string mc)
    {
        var vanillaInstalled = Directory.Exists(Path.Combine(Paths.VersionsDir, mc));
        LoaderNote.Text = vanillaInstalled
            ? $"将基于已安装的 {mc} 安装加载器，安装完成后会生成一个新的版本。"
            : $"尚未安装原版 {mc}：安装时会先自动下载原版，再安装加载器。";
    }

    private LoaderInstaller.Kind CurrentLoaderKind() =>
        LoaderForge.IsChecked == true ? LoaderInstaller.Kind.Forge
        : LoaderNeoForge.IsChecked == true ? LoaderInstaller.Kind.NeoForge
        : LoaderQuilt.IsChecked == true ? LoaderInstaller.Kind.Quilt
        : LoaderInstaller.Kind.Fabric;

    private string CurrentLoaderName() => CurrentLoaderKind() switch
    {
        LoaderInstaller.Kind.Forge => "Forge",
        LoaderInstaller.Kind.NeoForge => "NeoForge",
        LoaderInstaller.Kind.Quilt => "Quilt",
        _ => "Fabric"
    };

    private void Loader_Changed(object sender, RoutedEventArgs e) => _ = LoadLoaderVersionsAsync();

    private void McVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_kind == "loader") _ = LoadLoaderVersionsAsync();
    }

    private async void InstallLoader_Click(object sender, RoutedEventArgs e)
    {
        var mc = McVersionCombo.SelectedItem as string;
        var loaderVersion = LoaderVersionCombo.SelectedItem as string;
        if (mc is null || loaderVersion is null || _busy) return;

        _busy = true;
        InstallLoaderButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<(string step, double ratio)>(p =>
        {
            DownloadProgress.Value = Math.Clamp(p.ratio, 0, 1);
            ProgressText.Text = p.step;
        });

        try
        {
            // 确保原版基础版本存在
            if (!Directory.Exists(Path.Combine(Paths.VersionsDir, mc)))
            {
                ProgressText.Text = "正在下载原版游戏…";
                await VanillaInstaller.InstallAsync(mc, Paths.VersionsDir, progress, _cts.Token);
            }

            var newId = await LoaderInstaller.InstallAsync(CurrentLoaderKind(), mc, loaderVersion,
                progress, _cts.Token);

            ProgressText.Text = $"✓ {newId} 安装完成";
            SelectedText.Text = $"已安装 {newId}";
            _selectedVersion = newId;
            DetailButton.IsEnabled = true;
            InstallButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"安装加载器失败 {mc} {loaderVersion}");
            ProgressText.Text = "安装失败：" + ex.Message;
            await ShowAsync("安装失败", ex.Message);
        }
        finally
        {
            _busy = false;
            InstallLoaderButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    #endregion

    #region 社区资源

    private ResourceType CurrentResourceType() =>
        ResModpack.IsChecked == true ? ResourceType.Modpack
        : ResResourcePack.IsChecked == true ? ResourceType.ResourcePack
        : ResShader.IsChecked == true ? ResourceType.Shader
        : ResourceType.Mod;

    private void ResType_Changed(object sender, RoutedEventArgs e) => _ = SearchResourcesAsync();

    private void ResourceSearch_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => _ = SearchResourcesAsync();

    private void ResourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResourceList.SelectedItem is not ResourceCard card) return;

        _resourceFiles = [];

        _selectedResource = new ResourceHit
        {
            Id = card.Id,
            Name = card.Name,
            Source = card.Source == "Modrinth" ? ResourceSource.Modrinth : ResourceSource.CurseForge,
            Type = CurrentResourceType()
        };

        SelectedText.Text = $"已选择：{card.Name}";
        InstallButton.IsEnabled = !_busy;
        DetailButton.IsEnabled = false;
    }

    private void ResourceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResourceCard card) ResourceList.SelectedItem = card;
    }

    private async Task SearchResourcesAsync()
    {
        if (_busy) return;

        // 控件可能在 XAML 完全载入前被早触发的回调访问，这里做防御
        if (ResourceSearchBox is null || ResourceList is null ||
            ResourceVersionCombo is null || ResourceSourceCombo is null)
            return;

        var query = ResourceSearchBox.Text?.Trim() ?? string.Empty;

        var gameVersion = ResourceVersionCombo.SelectedItem as string;
        if (gameVersion is "全部版本") gameVersion = null;

        var useCf = ResourceSourceCombo.SelectedIndex == 1;
        var type = CurrentResourceType();

        SelectedText.Text = "正在搜索…";
        _resources.Clear();

        try
        {
            var hits = useCf
                ? await ResourceService.SearchCurseForgeAsync(query, type, gameVersion)
                : await ResourceService.SearchModrinthAsync(query, type, gameVersion);

            if (hits.Count == 0 && !useCf)
            {
                // Modrinth 失败时自动回退 CurseForge
                hits = await ResourceService.SearchCurseForgeAsync(query, type, gameVersion);
            }

            foreach (var hit in hits)
            {
                _resources.Add(new ResourceCard
                {
                    Id = hit.Id,
                    Name = hit.Name,
                    Summary = hit.Description,
                    Source = hit.Source == ResourceSource.Modrinth ? "Modrinth" : "CurseForge",
                    Meta = $"下载 {FormatDownloads(hit.Downloads)}" +
                           (string.IsNullOrEmpty(hit.Author) ? string.Empty : $" · {hit.Author}") +
                           (string.IsNullOrEmpty(hit.Categories) ? string.Empty : $" · {hit.Categories}")
                });
            }

            SelectedText.Text = hits.Count == 0
                ? "没有找到结果，试试其他关键词或切换来源"
                : $"找到 {hits.Count} 个结果";
        }
        catch (Exception ex)
        {
            SelectedText.Text = "搜索失败：" + ex.Message;
            Logger.Error(ex, "搜索社区资源失败");
        }
    }

    private static string FormatDownloads(long count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
        if (count >= 1_000) return $"{count / 1_000.0:F1}K";
        return count.ToString();
    }

    #endregion

    #region 安装 / 下载

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_kind == "resource")
        {
            await InstallResourceAsync();
            return;
        }

        if (_selectedVersion is null) return;
        await InstallVanillaAsync();
    }

    private async Task InstallVanillaAsync()
    {
        _busy = true;
        InstallButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<(string step, double ratio)>(p =>
        {
            DownloadProgress.Value = Math.Clamp(p.ratio, 0, 1);
            ProgressText.Text = p.step;
        });

        try
        {
            await VanillaInstaller.InstallAsync(_selectedVersion!, Paths.VersionsDir, progress, _cts.Token);

            LauncherConfig.Current.LastInstance = _selectedVersion;
            LauncherConfig.Current.Save();

            ProgressText.Text = $"✓ {_selectedVersion} 安装完成";
            DetailButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消下载";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"安装 {_selectedVersion} 失败");
            ProgressText.Text = "安装失败：" + ex.Message;
            await ShowAsync("安装失败", ex.Message + "\n\n提示：可在下载设置中切换下载源后重试。");
        }
        finally
        {
            _busy = false;
            InstallButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task InstallResourceAsync()
    {
        if (_selectedResource is null)
        {
            await ShowAsync("未选择资源", "请先在列表中搜索并选择一个资源。");
            return;
        }

        // 选择目标版本
        var installed = VersionLoader.GetInstalledVersions();
        if (installed.Count == 0)
        {
            await ShowAsync("没有可用版本", "请先在“原版游戏”中安装一个版本。");
            return;
        }

        var combo = new ComboBox { ItemsSource = installed, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog
        {
            Title = "安装到哪个版本？",
            Content = combo,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var targetVersion = combo.SelectedItem as string;
        if (targetVersion is null) return;

        _busy = true;
        InstallButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;

        var progress = new Progress<(string step, double ratio)>(p =>
        {
            DownloadProgress.Value = Math.Clamp(p.ratio, 0, 1);
            ProgressText.Text = p.step;
        });

        try
        {
            await EnsureResourceFilesAsync();

            if (_resourceFiles.Count == 0)
            {
                ProgressText.Text = "没有找到可下载的文件";
                return;
            }

            var file = _resourceFiles[0];
            var type = _selectedResource.Type;
            await ResourceService.DownloadToVersionAsync(file, targetVersion, type, progress);

            ProgressText.Text = $"✓ 已下载 {file.FileName} 到 {targetVersion}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "下载社区资源失败");
            ProgressText.Text = "下载失败：" + ex.Message;
            await ShowAsync("下载失败", ex.Message);
        }
        finally
        {
            _busy = false;
            InstallButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task EnsureResourceFilesAsync()
    {
        if (_selectedResource is null || _resourceFiles.Count > 0) return;

        var gameVersion = ResourceVersionCombo.SelectedItem as string;
        if (gameVersion is "全部版本") gameVersion = null;

        _resourceFiles = _selectedResource.Source == ResourceSource.Modrinth
            ? await ResourceService.GetModrinthFilesAsync(_selectedResource.Id, gameVersion, null)
            : await ResourceService.GetCurseForgeFilesAsync(_selectedResource.Id, gameVersion);
    }

    #endregion

    #region 其他

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SourceSettings_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("settings");

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Paths.VersionsDir);
            Process.Start(new ProcessStartInfo { FileName = Paths.MinecraftDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开目录失败: {ex.Message}");
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

    #endregion
}




