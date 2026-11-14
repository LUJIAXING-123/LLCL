using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLCL.Core.Minecraft;

/// <summary>
/// 版本级设置。保存在 versions/&lt;id&gt;/llcl-version.json，与 PCL 的“版本设置”对标。
/// </summary>
public sealed class VersionSettings
{
    /// <summary>序号越大越靠前（用于列表排序）。</summary>
    public int SortOrder { get; set; }

    /// <summary>自定义显示名（空则用版本 ID）。</summary>
    public string? DisplayName { get; set; }

    // ---------- Java ----------
    /// <summary>null = 跟随全局设置；true = 强制使用本版本设置。</summary>
    public bool? OverrideJava { get; set; }
    public string? JavaPath { get; set; }

    // ---------- 内存 ----------
    public bool? OverrideMemory { get; set; }
    public int MaxMemoryMb { get; set; } = 2048;
    public int MinMemoryMb { get; set; } = 512;

    // ---------- 窗口 ----------
    public bool? OverrideWindow { get; set; }
    public bool Fullscreen { get; set; }
    public int Width { get; set; } = 854;
    public int Height { get; set; } = 480;

    // ---------- 参数 ----------
    public string[] ExtraJvmArgs { get; set; } = [];
    public string[] ExtraGameArgs { get; set; } = [];

    // ---------- 服务器（对应 PCL 的 CardServer） ----------
    /// <summary>
    /// 启动后自动加入的服务器地址。支持 host 或 host:port。
    /// </summary>
    public string? ServerEnter { get; set; }

    /// <summary>是否仅允许登录到指定验证服务器（对应 PCL 的“限制登录”）。</summary>
    public bool? ServerLoginRequire { get; set; }

    /// <summary>登录服务器（authlib-injector 的 Yggdrasil 地址）。</summary>
    public string? ServerAuthServer { get; set; }

    /// <summary>注册服务器地址，仅用于展示给玩家的跳转链接。</summary>
    public string? ServerAuthRegister { get; set; }

    /// <summary>登录服务器显示名称。</summary>
    public string? ServerAuthName { get; set; }

    // ---------- 高级（对应 PCL 的 CardAdvance） ----------
    /// <summary>渲染器：空 = 跟随全局；可选 default / lwjgl3 / lwjgl2 等。</summary>
    public string? AdvanceRenderer { get; set; }

    /// <summary>启动后等待游戏窗口出现再返回，便于统计启动耗时。</summary>
    public bool AdvanceRunWait { get; set; }

    /// <summary>启动前自动更新 mod（PCL 的行为，默认关闭）。</summary>
    public bool AdvanceDisableModUpdate { get; set; }

    /// <summary>禁用 Java 版本检查。</summary>
    public bool AdvanceDisableJavaCheck { get; set; }

    /// <summary>使用旧版 assets 索引，兼容部分老旧整合包。</summary>
    public bool AdvanceUseAssetsV2 { get; set; }

    /// <summary>使用系统代理启动游戏。</summary>
    public bool AdvanceUseProxy { get; set; }

    /// <summary>禁用 JavaLaunchWrapper，缓解部分 Forge 版本的冲突。</summary>
    public bool AdvanceDisableJLW { get; set; }

    /// <summary>禁用新版 Forge 的 Log4j 补丁（仅调试用）。</summary>
    public bool AdvanceDisableLog4jPatch { get; set; }

    /// <summary>使用调试用 log4j2 配置。</summary>
    public bool AdvanceUseDebugLog4j { get; set; }

    /// <summary>禁用 LWJGL 的 unsafe agent。</summary>
    public bool AdvanceDisableLwjglUnsafeAgent { get; set; }

    // ---------- 其他 ----------
    /// <summary>版本图标（PNG 文件名，位于版本目录）。</summary>
    public string? Icon { get; set; }

    /// <summary>自定义备注。</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// 版本隔离的手动指定，与 PCL 的三态语义一致：
    /// <c>null</c> = 跟随判定规则（自动检测 + 全局方案）；
    /// <c>true</c> = 强制开启；<c>false</c> = 强制关闭。
    /// </summary>
    public bool? IsolationOverride { get; set; }

    /// <summary>
    /// 兼容旧配置：早期版本使用非空的 <see cref="IsolateVersion"/>。
    /// 读取时若发现旧字段已设置，则迁移为 <see cref="IsolationOverride"/>。
    /// </summary>
    public bool? IsolateVersion
    {
        get => IsolationOverride;
        set => IsolationOverride = value;
    }

    /// <summary>该版本是否启用隔离（按完整规则判定）。</summary>
    public IsolationDecision GetIsolation(string versionId) => VersionIsolation.Resolve(versionId);

    /// <summary>该版本实际的游戏运行目录。</summary>
    public string GetGameDir(string versionId) => VersionIsolation.GetGameDir(versionId);

    [JsonIgnore] public string FilePath { get; set; } = string.Empty;

    /// <summary>读取版本设置，不存在时返回默认值。</summary>
    public static VersionSettings Load(string versionId)
    {
        var path = PathOf(versionId);
        var settings = new VersionSettings { FilePath = path };
        if (!File.Exists(path)) return settings;

        try
        {
            var loaded = JsonSerializer.Deserialize<VersionSettings>(File.ReadAllText(path),
                LauncherConfig.JsonOptions);
            if (loaded is not null)
            {
                loaded.FilePath = path;
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取版本设置失败 {versionId}: {ex.Message}");
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            var path = PathOf(VersionId ?? throw new InvalidOperationException("缺少版本 ID"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, LauncherConfig.JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "保存版本设置失败");
        }
    }

    /// <summary>该设置所属的版本 ID（由文件名推导）。</summary>
    [JsonIgnore]
    public string? VersionId
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath)) return null;
            return Path.GetFileName(Path.GetDirectoryName(FilePath));
        }
    }

    public static string PathOf(string versionId)
        => Path.Combine(Paths.VersionsDir, versionId, "llcl-version.json");

    /// <summary>删除该版本的设置文件。</summary>
    public static void Delete(string versionId)
    {
        try
        {
            var path = PathOf(versionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"删除版本设置失败 {versionId}: {ex.Message}");
        }
    }
}

/// <summary>版本列表中的一项（含元信息，供 UI 展示）。</summary>
public sealed class VersionEntry
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Loader { get; init; } = "vanilla";
    public string? LoaderVersion { get; init; }
    public int? JavaMajor { get; init; }
    public string? JavaPath { get; init; }
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsBroken { get; init; }
    public string? BrokenReason { get; init; }
    public int SortOrder { get; init; }

    public string JavaDisplay => JavaPath is null ? "未匹配 Java" : $"Java {JavaMajor}";
    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:F1} MB",
        _ => $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}
