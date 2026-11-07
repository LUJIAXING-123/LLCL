using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLCL.Core;

/// <summary>
/// 版本隔离的全局默认方案，语义对齐 PCL。
/// </summary>
public enum VersionIsolationMode
{
    /// <summary>关闭隔离，所有版本共用 .minecraft 根目录。</summary>
    Off = 0,

    /// <summary>仅隔离可安装 Mod 的版本（Forge / NeoForge / Fabric / Quilt 等）。</summary>
    ModdableOnly = 1,

    /// <summary>仅隔离非正式版（快照、远古版本）。</summary>
    NonReleaseOnly = 2,

    /// <summary>隔离非正式版与可安装 Mod 的版本。</summary>
    ModdableOrNonRelease = 3,

    /// <summary>隔离所有版本（默认）。</summary>
    All = 4
}


public enum DownloadSource
{
    /// <summary>自动选择（默认官方源，失败时自动回退镜像）。</summary>
    Auto = 0,
    Official = 1,
    BmclApi = 2,
    Custom = 3
}

/// <summary>
/// 单个下载源的配置：主机地址 + 是否参与该类型文件的下载。
/// </summary>
public sealed class SourceOption
{
    /// <summary>显示名称，用于设置界面。</summary>
    public string Name { get; set; } = "";

    /// <summary>主机基地址，例如 https://bmclapi2.bangbang93.com 。</summary>
    public string BaseUrl { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>优先级，数字越小越先尝试。</summary>
    public int Priority { get; set; }
}

/// <summary>
/// 按文件类型划分的下载源配置。
/// <para>
/// 不同类型的最优源并不相同：assets 在官方 CDN 上更快，
/// 而版本 JSON 与加载器元数据在 BMCLAPI 上更快。
/// 因此允许用户分别配置，并支持多个源交叉使用以提高并发。
/// </para>
/// </summary>
public sealed class DownloadSources
{
    /// <summary>原版资源文件（assets objects）的源列表。</summary>
    public List<SourceOption> Assets { get; set; } = [];

    /// <summary>版本 JSON、客户端 jar、libraries 的源列表。</summary>
    public List<SourceOption> Game { get; set; } = [];

    /// <summary>加载器（Fabric/Forge/NeoForge/Quilt）的源列表。</summary>
    public List<SourceOption> Loader { get; set; } = [];

    /// <summary>是否启用多源交叉下载（按源并发分配，提升整体吞吐）。</summary>
    public bool CrossSource { get; set; } = true;

    /// <summary>创建内置默认配置。</summary>
    public static DownloadSources CreateDefault() => new()
    {
        Assets =
        [
            new SourceOption { Name = "官方 CDN", BaseUrl = "https://resources.download.minecraft.net", Priority = 0 },
            new SourceOption { Name = "BMCLAPI", BaseUrl = "https://bmclapi2.bangbang93.com/assets", Priority = 1 },
            new SourceOption { Name = "MCMirror", BaseUrl = "https://mirror.minguncle.cn/assets", Enabled = false, Priority = 2 }
        ],
        Game =
        [
            new SourceOption { Name = "BMCLAPI", BaseUrl = "https://bmclapi2.bangbang93.com", Priority = 0 },
            new SourceOption { Name = "官方源", BaseUrl = "https://official", Priority = 1 }
        ],
        Loader =
        [
            new SourceOption { Name = "BMCLAPI", BaseUrl = "https://bmclapi2.bangbang93.com", Priority = 0 },
            new SourceOption { Name = "官方源", BaseUrl = "https://official", Priority = 1 }
        ]
    };
}
public enum JavaSelection
{
    /// <summary>自动选择最合适的 Java。</summary>
    Auto = 0,
    /// <summary>用户指定路径。</summary>
    Manual = 1
}

/// <summary>
/// 启动器配置。序列化为 JSON，保存在 <see cref="Paths.ConfigFile"/>。
/// </summary>
public sealed class LauncherConfig
{
    // ---------- 账号 ----------
    /// <summary>离线登录用户名。</summary>
    public string OfflineUserName { get; set; } = "Player";

    /// <summary>
    /// 当前选中账号的 ID，对应 accounts.json 中的条目。
    /// 为空时回退到离线账号。
    /// </summary>
    public string? SelectedAccountId { get; set; }

    /// <summary>微软账号（旧字段，保留以兼容早期配置）。</summary>
    public string? MicrosoftUserName { get; set; }
    public string? MicrosoftUuid { get; set; }
    public string? MicrosoftAccessToken { get; set; }
    public string? MicrosoftRefreshToken { get; set; }

    /// <summary>当前选中的账号类型："offline" / "microsoft"（兼容字段）。</summary>
    public string AccountType { get; set; } = "offline";

    // ---------- 启动 ----------
    public int MaxMemoryMb { get; set; } = 0;      // 0 = 自动
    public int MinMemoryMb { get; set; } = 512;
    public string? JavaPath { get; set; }
    public bool AutoSelectJava { get; set; } = true;
    public string[] ExtraJvmArgs { get; set; } = [];
    public string[] ExtraGameArgs { get; set; } = [];
    public bool Fullscreen { get; set; }
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    /// <summary>实例在启动时是否自动更新已损坏的原版文件。</summary>
    public bool AutoRepairFiles { get; set; } = true;

    /// <summary>
    /// 版本隔离的全局默认方案，对齐 PCL 的「版本隔离」设置项。
    /// 单个版本可在版本设置中手动覆盖。
    /// </summary>
    public VersionIsolationMode VersionIsolationMode { get; set; } = VersionIsolationMode.All;

    // ---------- 下载 ----------
    public DownloadSource Source { get; set; } = DownloadSource.Auto;
    public string CustomSourceUrl { get; set; } = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// 按文件类型配置的下载源。为 null 时使用内置默认值。
    /// 老配置文件没有此字段，反序列化后为 null，由访问器补默认值。
    /// </summary>
    public DownloadSources? Sources { get; set; }

    /// <summary>取有效的分类型源配置（缺失时回落到默认值）。</summary>
    public DownloadSources EffectiveSources
    {
        get
        {
            Sources ??= DownloadSources.CreateDefault();
            var def = DownloadSources.CreateDefault();
            EnsureList(Sources.Assets, def.Assets);
            EnsureList(Sources.Game, def.Game);
            EnsureList(Sources.Loader, def.Loader);
            return Sources;
        }
    }

    /// <summary>
    /// 保证某类源的列表非空且至少有一个启用项。
    /// 用户若把全部源关掉会导致无法下载，这里兜底启用第一个。
    /// </summary>
    private static void EnsureList(List<SourceOption> list, List<SourceOption> fallback)
    {
        if (list.Count == 0) { list.AddRange(fallback); return; }
        if (!list.Any(s => s.Enabled)) list[0].Enabled = true;
    }
    /// <summary>下载线程数。过高会被镜像站限流，反而导致大量失败。</summary>
    public int DownloadThreads { get; set; } = 16;
    public int DownloadRetry { get; set; } = 3;
    public int DownloadSpeedLimitKb { get; set; } = 0; // 0 = 不限速

    // ---------- 联机 ----------
    public string LobbyUserName { get; set; } = string.Empty;

    /// <summary>
    /// 主中继节点。留空时由动态接口自动选择；
    /// 此处给出一个实测可用的社区节点作为初值。
    /// </summary>
    public string EasyTierRelay { get; set; } = "tcp://easytier.weiai.org.cn:11010";

    public string ExtraRelayNodes { get; set; } = string.Empty;
    public bool ForceRelay { get; set; }
    public bool EnableIPv6 { get; set; } = true;
    public bool AutoPunchSymmetricNat { get; set; } = true;
    public bool AutoUpdateEasyTier { get; set; } = true;
    public string EasyTierVersion { get; set; } = string.Empty; // 空 = 自动（最新已下载）
    /// <summary>EasyTier 网络名 / 密钥前缀，用于区分房间。</summary>
    public string LobbyNetworkPrefix { get; set; } = "LLCLRoom";

    /// <summary>禁用“首次启动必须登录正版账号”，默认离线即可进入主界面。</summary>
    public bool SkipOnlineAuth { get; set; } = true;

    /// <summary>启动时清理无效实例配置。</summary>
    public bool InvalidInstanceCleanup { get; set; } = true;

    // ---------- 界面 ----------
    public bool UseMica { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public string? LastInstance { get; set; }

    /// <summary>自定义游戏目录（空 = exe 同目录下的 Minecraft）。</summary>
    public string? CustomMinecraftDir { get; set; }

    // ---------- 加载 / 保存 ----------

    private static LauncherConfig? _current;

    /// <summary>当前显示用的账号名。</summary>
    public string DisplayAccountName => AccountType == "microsoft"
        ? (MicrosoftUserName ?? "微软账号")
        : OfflineUserName;

    public static LauncherConfig Current
    {
        get
        {
            if (_current is null) Load();
            return _current!;
        }
    }

    public static event Action? OnChanged;

    /// <summary>
    /// 配置迁移：修正历史版本遗留的过激或失效默认值。
    /// </summary>
    private void Migrate()
    {
        var changed = false;

        // 早期默认 64 线程会触发镜像站限流，导致大量下载失败
        if (DownloadThreads > 32)
        {
            Logger.Warn($"下载线程数 {DownloadThreads} 过高，已调整为 16 以避免镜像站限流");
            DownloadThreads = 16;
            changed = true;
        }

        // 官方 EasyTier 中继域名已失效，继续使用会导致联机永远连接超时
        if (IsDeadRelay(EasyTierRelay))
        {
            Logger.Warn($"中继节点 {EasyTierRelay} 已失效，改用可用节点");
            EasyTierRelay = "tcp://easytier.weiai.org.cn:11010";
            changed = true;
        }

        if (changed) Save();
    }

    /// <summary>判断中继地址是否属于已失效的官方域名。</summary>
    private static bool IsDeadRelay(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;

        return url.Contains("public.easytier.top", StringComparison.OrdinalIgnoreCase)
               || url.Contains("public2.easytier.cn", StringComparison.OrdinalIgnoreCase)
               || url.Contains("kkrainbow.top", StringComparison.OrdinalIgnoreCase);
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var json = File.ReadAllText(Paths.ConfigFile);
                _current = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions) ?? new LauncherConfig();
            }
            else
            {
                // 首次运行：立即落盘，便于用户在 LLCL 文件夹里找到配置文件
                _current = new LauncherConfig();
                _current.Save();
            }

            // 修正历史遗留的过激配置
            _current.Migrate();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "读取配置失败，使用默认配置");
            _current = new LauncherConfig();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(Paths.ConfigFile, json);
            OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "保存配置失败");
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}








