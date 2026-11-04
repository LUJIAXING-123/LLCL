using System.Collections.ObjectModel;
using System.Diagnostics;
using LLCL.Core;
using LLCL.Core.Minecraft;
using LLCL.Core.Net;
using LLCL.Core.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Pages;

/// <summary>
/// 下载中心：左侧统一分类导航（原版 / 加载器 / 模组 / 整合包 / 资源包 / 光影 / 任务），
/// 右侧为对应内容，底部为操作区。
/// </summary>
public sealed partial class DownloadPage : Page
{
    private readonly ObservableCollection<VersionItem> _versions = new();
    private readonly ObservableCollection<ResourceCard> _resources = new();
    private readonly ObservableCollection<TaskCard> _tasks = new();

    private VersionManifest? _manifest;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loaded;

    private string _section = "vanilla";
    private string? _selectedVersion;
    private ResourceHit? _selectedResource;
    private List<ResourceFile> _resourceFiles = [];

    public DownloadPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _versions;
        ResourceList.ItemsSource = _resources;
        TaskList.ItemsSource = _tasks;

        ResourceSourceCombo.Items.Add("Modrinth");
        ResourceSourceCombo.Items.Add("CurseForge");

        DownloadQueue.Changed += OnQueueChanged;
        VersionManager.VersionsChanged += OnVersionsChanged;
        DownloadQueue.ProgressUpdated += OnQueueProgress;

        Loaded += (_, _) =>
        {
            // 每次回到本页都恢复真实下载状态，避免界面与队列脱节
            RefreshTasks();

            if (_loaded) return;
            _loaded = true;
            GameNav.SelectedIndex = 0;
            _ = LoadAsync();
        };
    }

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

        // 游戏版本就绪后，若当前就在加载器页，立即查询并自动选中加载器版本
        if (_section == "loader") _ = LoadLoaderVersionsAsync();

        // 后台预热四种加载器版本，切过去时无需等待网络
        var defaultMc = McVersionCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(defaultMc)) PrefetchLoaders(defaultMc);
    }

    #endregion

    /// <summary>按 Tag 切换到指定分类（会同步左侧导航的选中状态）。</summary>
    private void SelectSection(string tag)
    {
        foreach (var list in new[] { GameNav, ResourceNav, OtherNav })
        {
            foreach (var raw in list.Items)
            {
                if (raw is ListViewItem item && item.Tag is string t &&
                    string.Equals(t, tag, StringComparison.Ordinal))
                {
                    list.SelectedItem = item;
                    return;
                }
            }
        }
    }
    #region 分类导航

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list || list.SelectedItem is not ListViewItem item) return;
        if (item.Tag is not string tag) return;

        // 三组列表互斥选中
        foreach (var other in new[] { GameNav, ResourceNav, OtherNav })
            if (!ReferenceEquals(other, list)) other.SelectedItem = null;

        _section = tag;
        ApplySection();
    }

    private void ApplySection()
    {
        PanelVanilla.Visibility = _section == "vanilla" ? Visibility.Visible : Visibility.Collapsed;
        PanelLoader.Visibility = _section == "loader" ? Visibility.Visible : Visibility.Collapsed;
        PanelTasks.Visibility = _section == "tasks" ? Visibility.Visible : Visibility.Collapsed;

        var isResource = _section is "mod" or "modpack" or "resourcepack" or "shader";
        PanelResource.Visibility = isResource ? Visibility.Visible : Visibility.Collapsed;

        if (isResource)
        {
            ResourceTitle.Text = _section switch
            {
                "mod" => "模组",
                "modpack" => "整合包",
                "resourcepack" => "资源包",
                _ => "光影"
            };
            _ = SearchResourcesAsync();
        }

        if (_section == "loader") _ = LoadLoaderVersionsAsync();
        if (_section == "tasks") RefreshTasks();

        InstallButton.Visibility = _section == "tasks" ? Visibility.Collapsed : Visibility.Visible;
        DetailButton.Visibility = _section is "vanilla" ? Visibility.Visible : Visibility.Collapsed;

        if (_section == "tasks")
        {
            SelectedText.Text = "下载任务管理";
            InstallButton.IsEnabled = false;
            DetailButton.IsEnabled = false;
        }
        else
        {
            ResetSelection();
        }
    }

    private void ResetSelection()
    {
        _selectedVersion = null;
        _selectedResource = null;
        DetailButton.IsEnabled = false;

        SelectedText.Text = _section switch
        {
            "loader" => "选择游戏版本与加载器版本后点击下载",
            "mod" => "搜索并选择一个模组",
            "modpack" => "搜索并选择一个整合包",
            "resourcepack" => "搜索并选择一个资源包",
            "shader" => "搜索并选择一个光影",
            _ => "选择一个游戏版本"
        };
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

        // 游戏版本未就绪时不做查询；等 PopulateMcVersions 选好默认项后会再次调用
        if (string.IsNullOrEmpty(mc)) return;

        var kind = CurrentLoaderKind();

        // 已有缓存则立即使用，切换加载器标签时无需重新等网络
        if (_loaderCache.TryGetValue((kind, mc), out var cached))
        {
            ApplyLoaderVersions(cached, mc);
            return;
        }

        LoaderVersionCombo.Items.Clear();
        LoaderSummary.Text = "正在查询加载器版本…";
        InstallLoaderButton.IsEnabled = false;

        try
        {
            var versions = await LoaderInstaller.GetVersionsAsync(kind, mc);
            _loaderCache[(kind, mc)] = versions;
            ApplyLoaderVersions(versions, mc);
        }
        catch (Exception ex)
        {
            LoaderSummary.Text = "查询失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
            Logger.Error(ex, "查询加载器版本失败");
        }
    }

    /// <summary>把查询结果填入下拉框并自动选中最新版本。</summary>
    private void ApplyLoaderVersions(List<LoaderVersionInfo> versions, string mc)
    {
        LoaderVersionCombo.Items.Clear();
        foreach (var v in versions.Take(80)) LoaderVersionCombo.Items.Add(v.Version);

        if (LoaderVersionCombo.Items.Count > 0)
        {
            // 自动选中最新版本，用户无需手动点选即可安装
            LoaderVersionCombo.SelectedIndex = 0;
        }

        LoaderSummary.Text = versions.Count == 0
            ? $"未找到适用于 {mc} 的 {CurrentLoaderName()} 版本"
            : $"{CurrentLoaderName()} · {mc} · 共 {versions.Count} 个可用版本";
        InstallLoaderButton.IsEnabled = versions.Count > 0;
        UpdateLoaderNote(mc);
    }

    /// <summary>
    /// 后台预热四种加载器的版本列表。
    /// 用户切到「模组加载器」页时通常已经有结果，不再干等网络。
    /// </summary>
    private void PrefetchLoaders(string mc)
    {
        foreach (var kind in new[]
                 {
                     LoaderInstaller.Kind.Fabric, LoaderInstaller.Kind.Quilt,
                     LoaderInstaller.Kind.Forge, LoaderInstaller.Kind.NeoForge
                 })
        {
            var key = (kind, mc);
            if (_loaderCache.ContainsKey(key)) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    var versions = await LoaderInstaller.GetVersionsAsync(kind, mc);
                    _loaderCache[key] = versions;
                    Logger.Info($"预热 {kind} {mc}: {versions.Count} 个版本");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"预热 {kind} {mc} 失败: {ex.Message}");
                }
            });
        }
    }

    private readonly Dictionary<(LoaderInstaller.Kind, string), List<LoaderVersionInfo>> _loaderCache = new();

    private void UpdateLoaderNote(string mc)
    {
        var installed = Directory.Exists(Path.Combine(Paths.VersionsDir, mc));
        LoaderNote.Text = installed
            ? $"将基于已安装的 {mc} 安装加载器，完成后会生成一个新版本。"
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
        if (_loaded && _section == "loader") _ = LoadLoaderVersionsAsync();
    }

    private async void InstallLoader_Click(object sender, RoutedEventArgs e)
    {
        var mc = McVersionCombo.SelectedItem as string;
        var loaderVersion = LoaderVersionCombo.SelectedItem as string;
        if (mc is null || loaderVersion is null || _busy) return;

        BeginBusy();
        var progress = MakeProgress();
        var cts = _cts;
        if (cts is null) { EndBusy(); return; }

        try
        {
            string newId = string.Empty;
            await DownloadQueue.RunAsync($"安装 {CurrentLoaderName()} {loaderVersion}", DownloadTaskKind.Loader,
                async (_, token) =>
                {
                    // 原版尚未安装时先临时装一份，供加载器读取 jar 与库；
                    // 加载器版本自包含后会删掉这份临时的原版版本。
                    var installedMc = Directory.Exists(Path.Combine(Paths.VersionsDir, mc));
                    var tempVanilla = false;

                    if (!installedMc)
                    {
                        ProgressText.Text = "正在下载原版游戏…";
                        await VanillaInstaller.InstallAsync(mc, Paths.VersionsDir, progress, token);
                        tempVanilla = true;
                    }

                    newId = await LoaderInstaller.InstallAsync(CurrentLoaderKind(), mc, loaderVersion,
                        progress, token);

                    // 加载器版本已自带 jar 与 natives，临时原版不再需要
                    if (tempVanilla)
                    {
                        try
                        {
                            var mcDir = Path.Combine(Paths.VersionsDir, mc);
                            if (Directory.Exists(mcDir))
                            {
                                Directory.Delete(mcDir, true);
                                Logger.Info($"已删除临时原版版本 {mc}（加载器版本已自包含）");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"删除临时原版版本失败: {ex.Message}");
                        }
                    }
                }, cts.Token, mc);

            ProgressText.Text = $"✓ {newId} 安装完成";
            SelectedText.Text = $"已安装 {newId}";
            _selectedVersion = newId;
            DetailButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消并清理临时文件";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"安装加载器失败 {mc} {loaderVersion}");
            ProgressText.Text = "安装失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
        }
        finally
        {
            EndBusy();
            RefreshTasks();
        }
    }

    #endregion

    #region 社区资源

    private ResourceType CurrentResourceType() => _section switch
    {
        "modpack" => ResourceType.Modpack,
        "resourcepack" => ResourceType.ResourcePack,
        "shader" => ResourceType.Shader,
        _ => ResourceType.Mod
    };

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
    }

    private void ResourceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResourceCard card) ResourceList.SelectedItem = card;
    }

    private async Task SearchResourcesAsync()
    {
        if (_busy || !_loaded) return;
        if (ResourceSearchBox is null || ResourceList is null ||
            ResourceVersionCombo is null || ResourceSourceCombo is null) return;

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
                hits = await ResourceService.SearchCurseForgeAsync(query, type, gameVersion);

            foreach (var hit in hits)
            {
                _resources.Add(new ResourceCard
                {
                    Id = hit.Id,
                    Name = hit.Name,
                    Summary = hit.Description,
                    Source = hit.Source == ResourceSource.Modrinth ? "Modrinth" : "CurseForge",
                    Meta = $"下载 {FormatDownloads(hit.Downloads)}" +
                           (string.IsNullOrEmpty(hit.Author) ? string.Empty : $" · {hit.Author}")
                });
            }

            SelectedText.Text = hits.Count == 0
                ? "没有找到结果，试试其他关键词或切换来源"
                : $"找到 {hits.Count} 个结果";
        }
        catch (Exception ex)
        {
            SelectedText.Text = "搜索失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
            Logger.Error(ex, "搜索社区资源失败");
        }
    }

    private static string FormatDownloads(long count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
        if (count >= 1_000) return $"{count / 1_000.0:F1}K";
        return count.ToString();
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

    #region 安装 / 下载

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_section is "mod" or "modpack" or "resourcepack" or "shader")
            _ = InstallResourceAsync();
        else if (_selectedVersion is not null)
            _ = InstallVanillaAsync();
    }

    private async Task InstallVanillaAsync()
    {
        BeginBusy();
        var progress = MakeProgress();
        var cts = _cts;
        if (cts is null) { EndBusy(); return; }

        try
        {
            await DownloadQueue.RunAsync($"安装 {_selectedVersion}", DownloadTaskKind.Vanilla,
                async (_, token) =>
                {
                    await VanillaInstaller.InstallAsync(_selectedVersion!, Paths.VersionsDir, progress, token);
                }, cts.Token, _selectedVersion);

            LauncherConfig.Current.LastInstance = _selectedVersion;
            LauncherConfig.Current.Save();

            ProgressText.Text = $"✓ {_selectedVersion} 安装完成";
            DetailButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消并清理临时文件";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"安装 {_selectedVersion} 失败");
            ProgressText.Text = "安装失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
        }
        finally
        {
            EndBusy();
            RefreshTasks();
        }
    }

    private async Task InstallResourceAsync()
    {
        if (_selectedResource is null)
        {
            await ShowAsync("未选择资源", "请先在列表中搜索并选择一个资源。");
            return;
        }

        var installed = VersionLoader.GetInstalledVersions();
        if (installed.Count == 0)
        {
            await ShowAsync("没有可用版本", "请先在“原版游戏”中安装一个版本。");
            return;
        }

        var combo = new ComboBox
        {
            ItemsSource = installed,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
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
        if (combo.SelectedItem is not string targetVersion) return;

        BeginBusy();
        var progress = MakeProgress();
        var cts = _cts;
        if (cts is null) { EndBusy(); return; }

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

            await DownloadQueue.RunAsync($"下载 {file.FileName}", DownloadTaskKind.Resource,
                async (_, token) =>
                {
                    await ResourceService.DownloadToVersionAsync(file, targetVersion, type, progress, token);
                }, targetVersion: targetVersion);

            ProgressText.Text = $"✓ 已下载 {file.FileName} 到 {targetVersion}";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消并清理临时文件";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "下载社区资源失败");
            ProgressText.Text = "下载失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
        }
        finally
        {
            EndBusy();
            RefreshTasks();
        }
    }

    #endregion

    #region 下载任务管理

    private void OnVersionsChanged() => Dispatch(RefreshTasks);
    private void OnQueueChanged() => Dispatch(() =>
    {
        RefreshTasks();
        SyncActiveProgress();
    });

    private void OnQueueProgress(DownloadTask task) => Dispatch(() =>
    {
        // 首个任务真正进入队列后再切到「下载任务」页
        if (_pendingSwitchToTasks)
        {
            _pendingSwitchToTasks = false;
            SelectSection("tasks");
        }

        var card = _tasks.FirstOrDefault(t => t.Id == task.Id);

        if (card is null)
        {
            // 新任务：补进列表，否则进度无处可写
            card = new TaskCard
            {
                Id = task.Id,
                Title = task.Title,
                Detail = task.ProgressText,
                State = task.StateText,
                Ratio = task.Ratio
            };
            _tasks.Add(card);
        }
        else
        {
            card.Detail = task.ProgressText;
            card.State = task.StateText;
            card.Ratio = task.Ratio;
        }

        // 底部进度条与按钮状态跟随真实任务
        if (task.State is DownloadState.Running or DownloadState.Paused)
        {
            _busy = true;
            CancelButton.Visibility = Visibility.Visible;
            PauseButton.Visibility = Visibility.Visible;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = Math.Clamp(task.Ratio, 0, 1);
            PauseButton.Content = task.State == DownloadState.Paused ? "继续" : "暂停";
            ProgressText.Text = task.ProgressText;
        }
    });

    private void RefreshTasks()
    {
        // 增量同步，避免每次刷新都 Clear 重建整张列表：
        // 队列每 500ms 广播一次进度，清空重建会让 ListView 反复重绘而闪烁。

        var source = DownloadQueue.Tasks;

        // 移除队列中已不存在的任务
        for (var i = _tasks.Count - 1; i >= 0; i--)
        {
            if (!source.Any(t => t.Id == _tasks[i].Id))
                _tasks.RemoveAt(i);
        }

        // 更新已有任务 / 追加新任务
        foreach (var task in source)
        {
            var card = _tasks.FirstOrDefault(t => t.Id == task.Id);
            if (card is null)
            {
                _tasks.Add(new TaskCard
                {
                    Id = task.Id,
                    Title = task.Title,
                    Detail = task.ProgressText,
                    State = task.StateText,
                    Ratio = task.Ratio
                });
            }
            else
            {
                card.Detail = task.ProgressText;
                card.State = task.StateText;
                card.Ratio = task.Ratio;
            }
        }

        SyncActiveProgress();
    }

    /// <summary>
    /// 把界面底部进度条同步到队列中真实运行的任务。
    /// 这样从其它页面切回来时，进度不会归零或消失。
    /// </summary>
    private void SyncActiveProgress()
    {
        var active = DownloadQueue.Tasks.FirstOrDefault(t =>
            t.State is DownloadState.Running or DownloadState.Paused);

        // 队列里暂时没有运行中的任务，但界面刚发起下载（RunAsync 还没入队）：
        // 此时绝不能调用 EndBusy()，否则会把 BeginBusy 刚创建的 _cts 置空，
        // 导致后续 _cts!.Token 抛出 NullReferenceException。
        if (active is null)
        {
            if (!_busy) return;
            // 仍处于忙碌状态：保持进度条可见，等任务真正入队后再由进度回调接管
            return;
        }

        _busy = true;
        CancelButton.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = Math.Clamp(active.Ratio, 0, 1);

        PauseButton.Content = active.State == DownloadState.Paused ? "继续" : "暂停";
        ProgressText.Text = active.ProgressText;
        InstallButton.IsEnabled = false;
    }

    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        DownloadQueue.Pause();
        RefreshTasks();
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        DownloadQueue.Resume();
        RefreshTasks();
    }

    private void PruneTasks_Click(object sender, RoutedEventArgs e)
    {
        DownloadQueue.PruneFinished();
        RefreshTasks();
    }

    private void PauseTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var task = DownloadQueue.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;

        if (task.State == DownloadState.Paused) DownloadQueue.Resume(id);
        else DownloadQueue.Pause(id);

        RefreshTasks();
    }

    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var task = DownloadQueue.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;

        await ConfirmAndCancelTaskAsync(task);
    }

    #endregion

    #region 通用

    private void BeginBusy()
    {
        _busy = true;
        InstallButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Visible;
        PauseButton.Content = "暂停";
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        _cts = new CancellationTokenSource();

        // 注意：这里不能立刻切到「下载任务」页。
        // SelectSection 会触发 ApplySection → RefreshTasks → SyncActiveProgress，
        // 而此时 RunAsync 尚未入队，早期版本会因此在 EndBusy 中把 _cts 置空。
        // 切换动作改由 OnQueueProgress 在首个任务真正入队后执行。
        _pendingSwitchToTasks = true;
    }

    /// <summary>首个任务入队后自动切到「下载任务」页（只切一次）。</summary>
    private bool _pendingSwitchToTasks;

    private void EndBusy()
    {
        _busy = false;
        InstallButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Collapsed;
        DownloadProgress.Visibility = Visibility.Collapsed;
        _cts?.Dispose();
        _cts = null;
    }

    private IProgress<(string step, double ratio)> MakeProgress() =>
        new Progress<(string step, double ratio)>(p =>
        {
            DownloadProgress.Value = Math.Clamp(p.ratio, 0, 1);
            ProgressText.Text = p.step;
        });

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        var task = DownloadQueue.Tasks.FirstOrDefault(t =>
            t.State is DownloadState.Running or DownloadState.Paused);
        if (task is null) return;

        if (task.State == DownloadState.Paused)
        {
            DownloadQueue.Resume(task.Id);
            PauseButton.Content = "暂停";
            ProgressText.Text = "已继续下载";
        }
        else
        {
            DownloadQueue.Pause(task.Id);
            PauseButton.Content = "继续";
            ProgressText.Text = "已暂停下载，点击“继续”恢复";
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 找出当前正在进行的任务，走「整版本清理」流程
        var running = DownloadQueue.Tasks.FirstOrDefault(t =>
            t.State is DownloadState.Running or DownloadState.Paused);

        if (running is not null)
        {
            await ConfirmAndCancelTaskAsync(running);
            return;
        }

        // 兜底：没有活跃任务时直接取消全部
        _cts?.Cancel();
        DownloadQueue.Cancel();
    }

    /// <summary>弹出确认框并执行「取消 + 整版本清理」。</summary>
    private async Task ConfirmAndCancelTaskAsync(DownloadTask task)
    {
        var versionHint = string.IsNullOrEmpty(task.TargetVersion)
            ? string.Empty
            : $"\n\n会连同版本「{task.TargetVersion}」的整个目录一并删除，" +
              "包括已经下载完成的部分文件，以便下次安装从干净状态开始。";

        var confirm = new ContentDialog
        {
            Title = "取消并清理下载",
            Content = $"将取消「{task.Title}」并清理磁盘上的相关内容，确定继续吗？{versionHint}",
            PrimaryButtonText = "取消并清理",
            CloseButtonText = "只暂停",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            DownloadQueue.Pause(task.Id);
            RefreshTasks();
            ProgressText.Text = "已暂停下载，点击“继续”可续传";
            return;
        }

        DownloadQueue.CancelAndPurge(task.Id);
        DownloadQueue.PruneFinished();
        RefreshTasks();
        ProgressText.Text = string.IsNullOrEmpty(task.TargetVersion)
            ? "已取消并清理下载内容"
            : $"已取消，并删除版本「{task.TargetVersion}」的全部文件";
    }

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

    private void Dispatch(Action action)
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() => action());
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







