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

        // 来源选择：默认全部，合并两个源一起展示
        ResourceSourceCombo.Items.Add("全部");
        ResourceSourceCombo.Items.Add("Modrinth");
        ResourceSourceCombo.Items.Add("CurseForge");
        ResourceSourceCombo.SelectedIndex = 0;
        ResourceSourceCombo.SelectionChanged += (_, _) =>
        {
            if (_loaded) _ = SearchResourcesAsync();
        };

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
        try
        {
            _manifest = await VersionManifestService.GetManifestAsync(true);
            FilterVersions();
            PopulateMcVersions();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "获取版本清单失败");
            SelectedText.Text = "版本清单获取失败：" + ex.Message;
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

        PopulateDetailGameVersions();

        // 游戏版本就绪后，若当前就在加载器页，立即查询并自动选中加载器版本
        if (_section == "loader") _ = LoadLoaderVersionsAsync();

        // 后台预热四种加载器版本，切过去时无需等待网络
        var defaultMc = McVersionCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(defaultMc)) PrefetchLoaders(defaultMc);

        // 后台预热社区资源首页，切到模组/整合包等分类时立即出结果
        PrefetchResources();
    }

    /// <summary>
    /// 填充二级页面的游戏版本下拉框。
    /// 数据来自版本清单，只列出正式版，避免列表过长。
    /// </summary>
    private void PopulateDetailGameVersions()
    {
        if (_manifest is null) return;

        DetailGameVersionCombo.Items.Clear();
        DetailGameVersionCombo.Items.Add("全部版本");

        foreach (var version in _manifest.Versions.Where(v => v.Type == "release").Take(120))
            DetailGameVersionCombo.Items.Add(version.Id);

        DetailGameVersionCombo.SelectedIndex = 0;
    }

    private void ResourceVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 版本筛选已移到二级页面，这里保留空实现以免事件签名失配
    }

    /// <summary>二级页面的游戏版本筛选。</summary>
    private void DetailGameVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PanelResourceDetail.Visibility == Visibility.Visible) _ = LoadDetailFilesAsync();
    }

    /// <summary>
    /// 预热社区资源的分类首页。
    /// <para>
    /// 资源搜索要同时请求 Modrinth 与 CurseForge，首次进入往往要等好几秒。
    /// 这里在后台提前取好四类结果的首屏并缓存，用户切过去时直接命中缓存。
    /// 缓存带过期时间，避免显示过时数据。
    /// </para>
    /// </summary>
    private void PrefetchResources()
    {
        var types = new[]
        {
            ResourceType.Mod, ResourceType.Modpack,
            ResourceType.ResourcePack, ResourceType.Shader
        };

        foreach (var type in types)
        {
            if (_resourceCache.TryGetValue(type, out var cached) &&
                DateTime.Now < cached.Expire) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    var hits = await ResourceService.SearchModrinthAsync(string.Empty, type,
                        limit: ResourcePageSize, offset: 0);
                    var cf = await ResourceService.SearchCurseForgeAsync(string.Empty, type,
                        limit: ResourcePageSize, offset: 0);

                    // 必须与 FetchResourceBatchAsync 使用同一套合并算法，
                    // 否则缓存命中时排序规则会与实时查询不一致。
                    var merged = MergeByScore(hits, cf, type, null);

                    _resourceCache[type] = new ResourceCacheEntry
                    {
                        Hits = merged,
                        Expire = DateTime.Now.AddMinutes(10)
                    };

                    Logger.Info($"预热资源 {type}: {merged.Count} 条");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"预热资源 {type} 失败: {ex.Message}");
                }
            });
        }
    }

    /// <summary>资源首页缓存条目。</summary>
    private sealed class ResourceCacheEntry
    {
        public List<ResourceHit> Hits { get; init; } = [];
        public DateTime Expire { get; init; }
    }

    private readonly Dictionary<ResourceType, ResourceCacheEntry> _resourceCache = new();

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
        // 切换到别的分类时销毁二级页面，避免下次进入残留上一次的资源
        if (PanelResourceDetail.Visibility == Visibility.Visible) CloseResourceDetail();

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
            PanelResource.Visibility = Visibility.Visible;
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

    /// <summary>
    /// 删除已无作用的原版版本目录。
    /// <para>
    /// 删除前必须确认加载器版本确实自包含：
    /// 存在同名 jar、且版本 JSON 中不含 inheritsFrom。
    /// 任一条件不满足就保留原版，避免误删导致加载器版本无法启动。
    /// </para>
    /// </summary>
    private static void DeleteVanillaIfRedundant(string mcDir, string mcVersion, string loaderVersionId)
    {
        try
        {
            var loaderDir = Path.Combine(Paths.VersionsDir, loaderVersionId);
            var loaderJar = Path.Combine(loaderDir, loaderVersionId + ".jar");
            var loaderJson = Path.Combine(loaderDir, loaderVersionId + ".json");

            if (!File.Exists(loaderJar))
            {
                Logger.Warn($"加载器版本缺少 jar，保留原版 {mcVersion}");
                return;
            }

            if (!File.Exists(loaderJson))
            {
                Logger.Warn($"加载器版本缺少 JSON，保留原版 {mcVersion}");
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(loaderJson));
            if (doc.RootElement.TryGetProperty("inheritsFrom", out _))
            {
                Logger.Warn($"加载器版本仍依赖 inheritsFrom，保留原版 {mcVersion}");
                return;
            }

            if (!Directory.Exists(mcDir)) return;

            Directory.Delete(mcDir, true);
            Logger.Info($"已删除冗余原版版本 {mcVersion}（{loaderVersionId} 已自包含）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"删除原版版本 {mcVersion} 失败: {ex.Message}");
        }
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
                    // 原版未安装时先临时装一份，供加载器读取 jar 与 libraries；
                    // 加载器版本自包含后，这份原版即成为冗余，安装成功后清理。
                    var mcDir = Path.Combine(Paths.VersionsDir, mc);
                    var installedMc = Directory.Exists(mcDir);
                    var tempVanilla = false;

                    if (!installedMc)
                    {
                        ProgressText.Text = "正在下载原版游戏…";
                        await VanillaInstaller.InstallAsync(mc, Paths.VersionsDir, progress, token);
                        tempVanilla = true;
                    }

                    newId = await LoaderInstaller.InstallAsync(CurrentLoaderKind(), mc, loaderVersion,
                        progress, token);

                    // 仅当原版是本次临时安装、且加载器版本确认已自包含时才删除
                    if (tempVanilla)
                    {
                        ProgressText.Text = "清理临时原版…";
                        DeleteVanillaIfRedundant(mcDir, mc, newId);
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
        if (e.ClickedItem is ResourceCard card) OpenResourceDetail(card);
    }

    #region 资源二级页面

    /// <summary>二级页面当前打开的资源。</summary>
    private ResourceCard? _detailCard;

    /// <summary>二级页面拿到的全部可用文件。</summary>
    private List<ResourceFile> _detailFiles = [];

    /// <summary>用户在二级页面选中的具体版本。</summary>
    private ResourceFile? _detailFile;

    /// <summary>加载器选项。第一项表示不限制。</summary>
    private static readonly string[] LoaderOptions = ["不限", "fabric", "forge", "neoforge", "quilt"];

    private void OpenResourceDetail(ResourceCard card)
    {
        _detailCard = card;
        _detailFiles = [];
        _detailFile = null;

        DetailTitle.Text = card.Name;
        DetailSummary.Text = card.Summary;
        DetailVersionList.ItemsSource = null;
        DetailDependencyList.ItemsSource = null;
        DetailDependencySection.Visibility = Visibility.Collapsed;
        DetailSelectionText.Text = string.Empty;
        DetailStatus.Text = "正在读取可用版本…";

        // 只有模组与整合包需要区分加载器
        var needsLoader = _section is "mod" or "modpack";
        DetailLoaderCombo.Visibility = needsLoader ? Visibility.Visible : Visibility.Collapsed;
        if (needsLoader)
        {
            DetailLoaderCombo.ItemsSource = LoaderOptions;
            DetailLoaderCombo.SelectedIndex = 0;
        }

        // 游戏版本筛选：每次打开都重建并归位到「全部版本」
        PopulateDetailGameVersions();

        PanelResource.Visibility = Visibility.Collapsed;
        PanelResourceDetail.Visibility = Visibility.Visible;
        InstallButton.Visibility = Visibility.Collapsed;
        DetailButton.Visibility = Visibility.Collapsed;

        _ = LoadDetailFilesAsync();
    }

    /// <summary>
    /// 关闭详情页并清空其状态。
    /// 离开该界面时调用，避免下一次打开残留上次的资源与勾选。
    /// </summary>
    private void CloseResourceDetail()
    {
        PanelResourceDetail.Visibility = Visibility.Collapsed;

        _detailCard = null;
        _detailFiles = [];
        _detailFile = null;

        DetailVersionList.ItemsSource = null;
        DetailDependencyList.ItemsSource = null;
        DetailDependencySection.Visibility = Visibility.Collapsed;
        DetailSelectionText.Text = string.Empty;
        DetailStatus.Text = string.Empty;
        DetailProgress.Visibility = Visibility.Collapsed;
        DetailDownloadButton.IsEnabled = true;
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e)
    {
        CloseResourceDetail();
        PanelResource.Visibility = Visibility.Visible;
        InstallButton.Visibility = _section == "tasks" ? Visibility.Collapsed : Visibility.Visible;
        DetailButton.Visibility = _section is "vanilla" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DetailLoader_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PanelResourceDetail.Visibility == Visibility.Visible) _ = LoadDetailFilesAsync();
    }

    private async Task LoadDetailFilesAsync()
    {
        if (_detailCard is null) return;

        _detailFile = null;
        DetailSelectionText.Text = string.Empty;
        DetailDependencySection.Visibility = Visibility.Collapsed;
        DetailDependencyList.ItemsSource = null;
        DetailProgress.Visibility = Visibility.Visible;

        try
        {
            var loader = DetailLoaderCombo.SelectedItem as string;
            if (loader is "不限") loader = null;

            // 二级页面的游戏版本筛选
            var gameVersion = DetailGameVersionCombo.SelectedItem as string;
            if (gameVersion is "全部版本") gameVersion = null;

            var isCurseForge = _detailCard.Source == "CurseForge";

            _detailFiles = isCurseForge
                ? await ResourceService.GetCurseForgeFilesAsync(_detailCard.Id, gameVersion, loader)
                : await ResourceService.GetModrinthFilesAsync(_detailCard.Id, gameVersion, loader);

            // 按游戏版本分组：同一版本下的所有资源版本收进同一个折叠项
            var groups = new List<ResourceVersionGroup>();

            foreach (var group in _detailFiles
                         .GroupBy(f => FirstGameVersion(f.GameVersions))
                         .OrderByDescending(g => g.Key, GameVersionComparer.Instance))
            {
                var items = group.Select(f => new ResourceFileItem
                {
                    Name = string.IsNullOrEmpty(f.VersionName) ? f.FileName : f.VersionName,
                    Meta = string.Join(" · ", new[]
                    {
                        LLCL.Core.Net.DownloadTask.FormatSize(f.Size),
                        f.Loaders ?? string.Empty
                    }.Where(s => !string.IsNullOrEmpty(s))),
                    File = f
                }).ToList();

                groups.Add(new ResourceVersionGroup
                {
                    GameVersion = group.Key,
                    CountText = $"{items.Count} 个版本",
                    Items = items
                });
            }

            DetailVersionList.ItemsSource = groups;

            DetailStatus.Text = _detailFiles.Count == 0
                ? "该加载器下没有可用版本"
                : $"共 {groups.Count} 个游戏版本 · {_detailFiles.Count} 个文件";
        }
        catch (Exception ex)
        {
            DetailStatus.Text = "读取版本失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
            Logger.Error(ex, "读取资源版本失败");
        }
        finally
        {
            DetailProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>取文件声明的第一个游戏版本，用于分组。</summary>
    private static string FirstGameVersion(string? gameVersions)
    {
        if (string.IsNullOrWhiteSpace(gameVersions)) return "未标注版本";
        return gameVersions.Split(',', StringSplitOptions.TrimEntries)[0];
    }

    /// <summary>按版本号语义比较，1.21.4 排在 1.21 之前。</summary>
    private sealed class GameVersionComparer : IComparer<string>
    {
        public static readonly GameVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parse(x);
            var b = Parse(y);
            if (a is null || b is null) return string.CompareOrdinal(x, y);

            for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                var va = i < a.Count ? a[i] : 0;
                var vb = i < b.Count ? b[i] : 0;
                if (va != vb) return va.CompareTo(vb);
            }

            return 0;
        }

        private static List<int>? Parse(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            var numbers = new List<int>();
            foreach (var part in version.Split('.', '-'))
            {
                if (!int.TryParse(part, out var value)) break;
                numbers.Add(value);
            }

            return numbers.Count == 0 ? null : numbers;
        }
    }

    /// <summary>点击某个具体版本即为选中，随后可直接下载。</summary>
    private async void DetailFile_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ResourceFileItem item) return;

        _detailFile = item.File;
        DetailSelectionText.Text = $"已选择：{item.Name}";

        DetailDependencyList.ItemsSource = null;
        DetailDependencySection.Visibility = Visibility.Collapsed;

        if (item.File.Dependencies.Count == 0) return;

        var isCurseForge = _detailCard?.Source == "CurseForge";
        var deps = new List<DependencyItem>();

        foreach (var dep in item.File.Dependencies.Take(8))
        {
            var name = await ResourceService.GetProjectNameAsync(
                isCurseForge ? ResourceSource.CurseForge : ResourceSource.Modrinth, dep.ProjectId);

            deps.Add(new DependencyItem
            {
                ProjectId = dep.ProjectId,
                Label = $"{(dep.IsRequired ? "必需" : "可选")} · {name ?? dep.ProjectId}",
                IsRequired = dep.IsRequired,
                IsChecked = dep.IsRequired
            });
        }

        if (deps.Count == 0) return;

        DetailDependencyList.ItemsSource = deps;
        DetailDependencySection.Visibility = Visibility.Visible;
    }

    private async void DetailDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCard is null)
        {
            DetailStatus.Text = "请先返回列表重新选择资源";
            return;
        }

        if (_detailFile is null)
        {
            DetailStatus.Text = "请先展开游戏版本并点选一个具体版本";
            return;
        }

        DetailDownloadButton.IsEnabled = false;
        DetailProgress.Visibility = Visibility.Visible;

        try
        {
            // 与列表页下载一致：弹出「安装到哪个版本」让用户选择
            var versionId = await PickTargetVersionAsync();
            if (versionId is null) return;

            var type = CurrentResourceType();

            // 一并下载用户勾选的前置
            var checkedDeps = (DetailDependencyList.ItemsSource as IEnumerable<DependencyItem> ?? [])
                .Where(d => d.IsChecked)
                .ToList();

            var gameVersion = FirstGameVersion(_detailFile.GameVersions);
            if (gameVersion is "未标注版本") gameVersion = null;
            var loader = DetailLoaderCombo.SelectedItem as string;
            if (loader is "不限") loader = null;
            var isCurseForge = _detailCard.Source == "CurseForge";

            var mainFile = _detailFile;
            var progress = MakeProgress();

            // 必须走下载队列，否则不会出现在「下载任务」列表里，
            // 也无法暂停、取消或看到进度。
            await DownloadQueue.RunAsync(
                $"下载 {mainFile.FileName}", DownloadTaskKind.Resource,
                async (_, token) =>
                {
                    await ResourceService.DownloadToVersionAsync(mainFile, versionId, type, progress, token);

                    foreach (var dep in checkedDeps)
                    {
                        var files = isCurseForge
                            ? await ResourceService.GetCurseForgeFilesAsync(dep.ProjectId, gameVersion, loader, token)
                            : await ResourceService.GetModrinthFilesAsync(dep.ProjectId, gameVersion, loader, token);

                        var pick = files.FirstOrDefault();
                        if (pick is not null)
                            await ResourceService.DownloadToVersionAsync(pick, versionId, type, progress, token);
                    }
                },
                default,
                versionId);

            DetailStatus.Text = checkedDeps.Count > 0
                ? $"已下载到版本「{versionId}」，含 {checkedDeps.Count} 个前置"
                : $"已下载到版本「{versionId}」";
        }
        catch (Exception ex)
        {
            DetailStatus.Text = "下载失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
            Logger.Error(ex, "下载资源失败");
        }
        finally
        {
            DetailDownloadButton.IsEnabled = true;
            DetailProgress.Visibility = Visibility.Collapsed;
        }
    }

    #endregion

    /// <summary>当前资源搜索的页码（从 1 开始）。</summary>
    private int _resourcePage = 1;

    /// <summary>上一次搜索使用的关键词与条件，供翻页时复用。</summary>
    private string _resourceQuery = string.Empty;
    private string? _resourceGameVersion;
    private ResourceType _resourceType;

    /// <summary>每页条数。两个源合并后单页最多为此值的两倍。</summary>
    private const int ResourcePageSize = 20;

    /// <summary>是否正在加载，仅用于进度条显示，不再用来丢弃新请求。</summary>
    private bool _loadingMore;

    /// <summary>记录本次搜索中哪个源没有返回结果，显示在状态文字里。</summary>
    private string? _sourceNote;

    /// <summary>
    /// 搜索请求序号。每次发起新搜索都会自增，
    /// 迟到的旧响应据此丢弃，避免切换分类后列表仍停留在上一次的结果。
    /// </summary>
    private int _searchSeq;

    private async Task SearchResourcesAsync()
    {
        if (_busy || !_loaded) return;
        if (ResourceSearchBox is null || ResourceList is null) return;

        var query = ResourceSearchBox.Text?.Trim() ?? string.Empty;

        // 列表页不做版本筛选，统一在二级页面里挑具体的游戏版本
        string? gameVersion = null;

        _resourceQuery = query;
        _resourceGameVersion = gameVersion;
        _resourceType = CurrentResourceType();

        await LoadResourcePageAsync(1, ++_searchSeq);
    }

    /// <summary>
    /// 加载指定页并替换列表内容。分页模式而非追加上限，
    /// 因为两个源的搜索接口单次最多返回固定条数，
    /// 用页码翻页比「加载更多」更直观。
    /// </summary>
    private async Task LoadResourcePageAsync(int page, int seq = 0)
    {
        if (seq == 0) seq = ++_searchSeq;

        _loadingMore = true;
        SelectedText.Text = $"正在加载第 {page} 页…";
        Dispatch(() => LoadMoreProgress.Visibility = Visibility.Visible);

        try
        {
            // 首页且无关键词时优先用预热缓存，避免重复请求
            List<ResourceHit>? cached = null;
            if (page == 1 &&
                string.IsNullOrWhiteSpace(_resourceQuery) &&
                (_resourceGameVersion is null) &&
                (ResourceSourceCombo?.SelectedIndex ?? 0) == 0 &&
                _resourceCache.TryGetValue(_resourceType, out var entry) &&
                DateTime.Now < entry.Expire)
            {
                cached = entry.Hits;
            }

            var batch = cached ?? await FetchResourceBatchAsync((page - 1) * ResourcePageSize, default);

            // 期间用户又切了分类或翻了页，本次结果作废
            if (seq != _searchSeq) return;

            _resourcePage = page;
            _resources.Clear();
            foreach (var hit in batch) _resources.Add(ToCard(hit));

            if (batch.Count == 0 && page > 1)
            {
                // 翻过头了，退回上一页
                _resourcePage = page - 1;
                SelectedText.Text = $"已经是最后一页（共 {page - 1} 页）";
            }
            else
            {
                SelectedText.Text = batch.Count == 0
                    ? "没有找到结果，试试其他关键词或切换类型"
                    : $"第 {page} 页 · 共 {_resources.Count} 个结果";
                if (!string.IsNullOrEmpty(_sourceNote)) SelectedText.Text += " · " + _sourceNote;
            }

            UpdatePageButtons();
        }
        catch (Exception ex)
        {
            if (seq != _searchSeq) return;
            SelectedText.Text = "搜索失败：" + LLCL.Core.Link.FriendlyError.ToText(ex);
            Logger.Error(ex, "搜索社区资源失败");
        }
        finally
        {
            if (seq == _searchSeq)
            {
                _loadingMore = false;
                Dispatch(() => LoadMoreProgress.Visibility = Visibility.Collapsed);
            }
        }
    }

    /// <summary>根据当前页与结果数量更新翻页按钮的可用状态。</summary>
    private void UpdatePageButtons()
    {
        if (PageText is null) return;

        PageText.Text = _resourcePage.ToString();

        var hasPrev = _resourcePage > 1;
        if (PrevPageButton is not null) PrevPageButton.IsEnabled = hasPrev;
        if (FirstPageButton is not null) FirstPageButton.IsEnabled = hasPrev;

        // 下一页：本页有结果就当作还有更多（服务端总数未知，按实际返回判断）
        var hasNext = _resources.Count > 0;
        if (NextPageButton is not null) NextPageButton.IsEnabled = hasNext;
    }

    private void FirstPage_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcePage > 1) _ = LoadResourcePageAsync(1);
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcePage > 1) _ = LoadResourcePageAsync(_resourcePage - 1);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadResourcePageAsync(_resourcePage + 1);
    }

    /// <summary>
    /// 取一批结果。根据顶栏的来源选择决定查询方式：
    /// 全部 = 两个源并行合并，Modrinth / CurseForge = 只查对应源。
    /// </summary>
    private async Task<List<ResourceHit>> FetchResourceBatchAsync(int offset, CancellationToken ct)
    {
        var query = _resourceQuery;
        var gameVersion = _resourceGameVersion;
        var type = _resourceType;

        // 0=全部 1=Modrinth 2=CurseForge
        var sourceIndex = ResourceSourceCombo?.SelectedIndex ?? 0;

        var modrinth = new List<ResourceHit>();
        var curseforge = new List<ResourceHit>();
        var modrinthFailed = false;
        var curseforgeFailed = false;

        if (sourceIndex is 0 or 1)
        {
            try
            {
                modrinth = await ResourceService.SearchModrinthAsync(query, type, gameVersion,
                    limit: ResourcePageSize, offset: offset, ct: ct);
            }
            catch (Exception ex)
            {
                modrinthFailed = true;
                Logger.Warn($"Modrinth 搜索失败: {ex.Message}");
            }
        }

        if (sourceIndex is 0 or 2)
        {
            try
            {
                curseforge = await ResourceService.SearchCurseForgeAsync(query, type, gameVersion,
                    limit: ResourcePageSize, offset: offset, ct: ct);
            }
            catch (Exception ex)
            {
                curseforgeFailed = true;
                Logger.Warn($"CurseForge 搜索失败: {ex.Message}");
            }
        }

        // 加权评分后统一排序，见 MergeByScore 的说明
        var merged = MergeByScore(modrinth, curseforge, type, query);

        if (sourceIndex == 0)
        {
            _sourceNote = (modrinthFailed, curseforgeFailed) switch
            {
                (true, true) => "两个源都请求失败",
                (true, false) => "Modrinth 请求失败，当前仅显示 CurseForge",
                (false, true) => "CurseForge 请求失败，当前仅显示 Modrinth",
                _ => modrinth.Count == 0
                    ? "Modrinth 没有返回结果"
                    : curseforge.Count == 0
                        ? "CurseForge 没有返回结果"
                        : null
            };
        }
        else
        {
            _sourceNote = null;
        }

        return merged;
    }

    /// <summary>
    /// 合并两个源的结果并统一排序，逻辑照搬 PCL-CE 的 CompProjectsGet。
    /// <para>
    /// PCL-CE 分成两条路径，两者差异很大：
    /// </para>
    /// <para>
    /// <b>浏览（无关键词）</b>：直接用「下载量 × 源权重」比较，不取对数。
    /// 权重是按资源类型定的常数（Mod/整合包 Modrinth 记 7、CurseForge 记 1），
    /// 但只要不放进对数里，它就只是等比缩放，项目之间的相对次序依然由下载量决定。
    /// </para>
    /// <para>
    /// <b>搜索（有关键词）</b>：先算 log10 压缩量级，再叠加文本相似度。
    /// 这一步取对数是因为搜索场景下不同源的匹配度差异可能极大，
    /// 需要让相似度有足够权重。
    /// </para>
    /// <para>
    /// 之前的错误就是把搜索分支的对数公式用到了浏览分支：
    /// log(a×7) = log(a) + log(7)，权重退化成给整个源加一个常数分，
    /// 源与源之间被整体拉开，于是前 20 条全部来自同一个源。
    /// </para>
    /// </summary>
    private static List<ResourceHit> MergeByScore(List<ResourceHit> modrinth,
        List<ResourceHit> curseforge, ResourceType type, string? query)
    {
        // 去重：同一项目在两个源都上架时只留 Modrinth 那条
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<ResourceHit>(modrinth.Count + curseforge.Count);

        foreach (var hit in modrinth.Concat(curseforge))
        {
            if (seen.Add(Normalize(hit.Name))) all.Add(hit);
        }

        if (all.Count == 0) return all;

        // 源权重，与 PCL-CE 的 getDownloadCountMult 一致
        static double Weight(ResourceSource source, ResourceType type) => type switch
        {
            ResourceType.Mod or ResourceType.Modpack => source == ResourceSource.CurseForge ? 1 : 7,
            ResourceType.ResourcePack or ResourceType.Shader => source == ResourceSource.CurseForge ? 1 : 5,
            _ => 1
        };

        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var needle = query?.Trim() ?? string.Empty;

        var scored = new List<(ResourceHit Hit, double Score)>(all.Count);

        foreach (var hit in all)
        {
            var weighted = Math.Max(hit.Downloads, 1) * Weight(hit.Source, type);
            double score;

            if (hasQuery)
            {
                // 搜索：压缩量级后叠加匹配度
                score = Math.Log10(weighted) / 9 + MatchScore(hit, needle);
            }
            else
            {
                // 浏览：直接按加权下载量比较，不做对数变换
                score = weighted;
            }

            scored.Add((hit, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => x.Hit)
            .ToList();
    }

    /// <summary>
    /// 规范化项目名称用于去重。
    /// 两个源对同一模组的命名常有差异，例如
    /// 「Cloth Config API」与「Cloth Config API (Fabric/Forge/NeoForge)」，
    /// 因此去掉括号内容、空格与大小写差异后再比较。
    /// </summary>
    private static string Normalize(string name)
    {
        var text = name;

        // 去掉括号及其内容
        var open = text.IndexOfAny(['(', '（']);
        if (open > 0) text = text[..open];

        return new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// 计算简单的文本匹配分，范围 0~1。
    /// 名称完全匹配得 1 分，名称包含关键词得 0.7，仅描述包含得 0.2。
    /// </summary>
    private static double MatchScore(ResourceHit hit, string query)
    {
        if (hit.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1.0;

        if (hit.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 0.7;

        if (hit.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) return 0.2;

        return 0;
    }

    /// <summary>把搜索结果转换成列表项。</summary>
    private static ResourceCard ToCard(ResourceHit hit) => new()
    {
        Id = hit.Id,
        Name = hit.Name,
        Summary = hit.Description,
        Source = hit.Source == ResourceSource.Modrinth ? "Modrinth" : "CurseForge",
        Meta = $"下载 {FormatDownloads(hit.Downloads)}" +
               (string.IsNullOrEmpty(hit.Author) ? string.Empty : $" · {hit.Author}")
    };

    private static string FormatDownloads(long count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
        if (count >= 1_000) return $"{count / 1_000.0:F1}K";
        return count.ToString();
    }

    private async Task EnsureResourceFilesAsync()
    {
        if (_selectedResource is null || _resourceFiles.Count > 0) return;

        // 列表页下载不限定游戏版本，交由服务端返回默认排序的文件
        _resourceFiles = _selectedResource.Source == ResourceSource.Modrinth
            ? await ResourceService.GetModrinthFilesAsync(_selectedResource.Id, null, null)
            : await ResourceService.GetCurseForgeFilesAsync(_selectedResource.Id, null);
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

    /// <summary>
    /// 弹出「安装到哪个版本」选择框，返回用户选中的已安装版本。
    /// 与原版下载流程保持一致，避免依赖左侧列表的选中项
    /// （那个列表属于「原版游戏」面板，在模组分类下是空的）。
    /// </summary>
    private async Task<string?> PickTargetVersionAsync()
    {
        var installed = VersionLoader.GetInstalledVersions();
        if (installed.Count == 0)
        {
            await ShowAsync("没有可用版本", "请先在「原版游戏」中安装一个版本。");
            return null;
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return combo.SelectedItem as string;
    }

    private async Task InstallResourceAsync()
    {
        if (_selectedResource is null)
        {
            await ShowAsync("未选择资源", "请先在列表中搜索并选择一个资源。");
            return;
        }

        var targetVersion = await PickTargetVersionAsync();
        if (targetVersion is null) return;

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

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var task = DownloadQueue.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;

        // 已结束的任务只需从列表移除；进行中的任务由下载器自行清理 .part 临时文件。
        // 两种情况都只影响这一条记录，不会动到同版本里的其它资源。
        var wasRunning = task.State is DownloadState.Running or DownloadState.Idle
            or DownloadState.Paused;

        DownloadQueue.CancelAndRemove(id);
        RefreshTasks();

        ProgressText.Text = wasRunning
            ? $"已取消「{task.Title}」"
            : $"已从列表移除「{task.Title}」";
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 取消当前所有未完成的下载，只清理各自的临时文件，不做版本级删除。
        _cts?.Cancel();
        DownloadQueue.Cancel();
        ProgressText.Text = "已取消下载";
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




















