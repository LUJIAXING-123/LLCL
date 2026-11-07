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

    /// <summary>微软账号（可选）。</summary>
    public string? MicrosoftUserName { get; set; }
    public string? MicrosoftUuid { get; set; }
    public string? MicrosoftAccessToken { get; set; }
    public string? MicrosoftRefreshToken { get; set; }

    /// <summary>当前选中的账号类型："offline" / "microsoft"。</summary>
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
    /// <summary>下载线程数。过高会被镜像站限流，反而导致大量失败。</summary>
    public int DownloadThreads { get; set; } = 16;
    public int DownloadRetry { get; set; } = 3;
    public int DownloadSpeedLimitKb { get; set; } = 0; // 0 = 不限速

    // ---------- 联机 ----------
    public string LobbyUserName { get; set; } = string.Empty;
    public string EasyTierRelay { get; set; } = "tcp://public.easytier.top:11010";
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
    /// 配置迁移：修正历史版本遗留的过激默认值。
    /// 早期默认 64 线程会触发镜像站限流，导致大量下载失败。
    /// </summary>
    private void Migrate()
    {
        if (DownloadThreads > 32)
        {
            Logger.Warn($"下载线程数 {DownloadThreads} 过高，已调整为 16 以避免镜像站限流");
            DownloadThreads = 16;
            Save();
        }
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





