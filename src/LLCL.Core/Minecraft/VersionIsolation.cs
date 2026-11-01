namespace LLCL.Core.Minecraft;

/// <summary>隔离判定结果，附带判定来源以便在界面上解释。</summary>
public enum IsolationReason
{
    /// <summary>用户手动开启。</summary>
    ManualOn,
    /// <summary>用户手动关闭。</summary>
    ManualOff,
    /// <summary>版本目录下已有 mods 或 saves，自动开启。</summary>
    AutoDetected,
    /// <summary>按全局默认策略决定。</n</summary>
    GlobalRule
}

/// <overloads>隔离方案。</overloads>
public sealed record IsolationDecision(bool Isolated, IsolationReason Reason)
{
    /// <summary>判定来源的中文描述，直接用于界面提示。</summary>
    public string ReasonText => Reason switch
    {
        IsolationReason.ManualOn => "已在版本设置中手动开启",
        IsolationReason.ManualOff => "已在版本设置中手动关闭",
        IsolationReason.AutoDetected => "检测到版本目录下已有 mods 或存档，自动开启",
        _ => "按全局默认策略"
    };
}

/// <summary>
/// 版本隔离判定，行为对齐 PCL：
/// <list type="number">
///   <item>用户在版本设置中手动指定时，以手动为准</item>
///   <item>否则若版本目录下已有 mods（含文件）或 saves（含存档），自动开启隔离</item>
///   <item>否则按全局默认方案判定</item>
/// </list>
/// </summary>
public static class VersionIsolation
{
    /// <summary>决定某个版本是否启用隔离。</summary>
    public static IsolationDecision Resolve(string versionId)
    {
        var settings = VersionSettings.Load(versionId);

        // 1. 手动指定优先
        if (settings.IsolationOverride is { } manual)
        {
            return new IsolationDecision(manual,
                manual ? IsolationReason.ManualOn : IsolationReason.ManualOff);
        }

        // 2. 版本目录下已有 mods / saves 时自动开启
        var versionDir = Path.Combine(Paths.VersionsDir, versionId);
        if (HasExistingContent(versionDir))
            return new IsolationDecision(true, IsolationReason.AutoDetected);

        // 3. 全局默认策略
        return new IsolationDecision(
            EvaluateGlobalRule(versionId, LauncherConfig.Current.VersionIsolationMode),
            IsolationReason.GlobalRule);
    }

    /// <summary>版本目录下是否已有 mods 文件或存档目录。</summary>
    private static bool HasExistingContent(string versionDir)
    {
        try
        {
            var modsDir = Path.Combine(versionDir, "mods");
            if (Directory.Exists(modsDir) &&
                Directory.EnumerateFiles(modsDir, "*.jar*").Any())
                return true;

            var savesDir = Path.Combine(versionDir, "saves");
            if (Directory.Exists(savesDir) &&
                Directory.EnumerateDirectories(savesDir).Any())
                return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"检测版本隔离内容失败 {versionDir}: {ex.Message}");
        }

        return false;
    }

    /// <summary>按全局方案判定是否需要隔离。</summary>
    public static bool EvaluateGlobalRule(string versionId, VersionIsolationMode mode)
    {
        return mode switch
        {
            VersionIsolationMode.Off => false,
            VersionIsolationMode.ModdableOnly => CanInstallMods(versionId),
            VersionIsolationMode.NonReleaseOnly => !IsReleaseVersion(versionId),
            VersionIsolationMode.ModdableOrNonRelease =>
                CanInstallMods(versionId) || !IsReleaseVersion(versionId),
            // 默认：隔离所有版本
            _ => true
        };
    }

    /// <summary>该版本是否为正式版（快照 / 远古版本不算）。</summary>
    public static bool IsReleaseVersion(string versionId)
    {
        try
        {
            var version = VersionLoader.Load(versionId);
            var type = version.Type ?? "release";
            return type is "release";
        }
        catch
        {
            // 读取失败时保守视为正式版，避免误开启隔离
            return true;
        }
    }

    /// <summary>
    /// 该版本是否可安装 Mod。
    /// 判定依据：加载器为 Forge / NeoForge / Fabric / Quilt，或已带 mods 目录。
    /// </summary>
    public static bool CanInstallMods(string versionId)
    {
        try
        {
            var version = VersionLoader.Load(versionId);
            var loader = VersionManager.DetectLoader(version).Name;
            if (loader is "forge" or "neoforge" or "fabric" or "quilt" or "liteloader")
                return true;

            // 原版也可能装了 mods（少数情况），有 mods 目录也算
            return HasExistingContent(Path.Combine(Paths.VersionsDir, versionId));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取该版本最终应使用的游戏目录。</summary>
    public static string GetGameDir(string versionId)
        => Resolve(versionId).Isolated
            ? Path.Combine(Paths.VersionsDir, versionId)
            : Paths.MinecraftDir;

    /// <summary>
    /// 把 .minecraft 根目录下的共用数据迁移到版本隔离目录（或反向迁移回共享）。
    /// 与 PCL 开启/关闭版本隔离时的行为一致，避免用户"文件凭空消失"。
    /// </summary>
    /// <param name="versionId">版本 ID。</param>
    /// <param name="toIsolated">true = 迁往隔离目录；false = 迁回共享根目录。</param>
    /// <returns>迁移的条目数。</returns>
    public static int MigrateContent(string versionId, bool toIsolated)
    {
        var sharedRoot = Paths.MinecraftDir;
        var isolatedRoot = Path.Combine(Paths.VersionsDir, versionId);
        var source = toIsolated ? sharedRoot : isolatedRoot;
        var target = toIsolated ? isolatedRoot : sharedRoot;

        if (!Directory.Exists(source)) return 0;

        // 仅迁移这些与版本强相关的子目录
        var subDirs = new[] { "mods", "saves", "resourcepacks", "shaderpacks", "config", "logs" };
        var moved = 0;

        foreach (var sub in subDirs)
        {
            var src = Path.Combine(source, sub);
            if (!Directory.Exists(src)) continue;

            var dst = Path.Combine(target, sub);
            Directory.CreateDirectory(dst);

            moved += CopyMissing(src, dst);
        }

        if (moved > 0)
            Logger.Info($"版本隔离迁移：{versionId} 共 {moved} 项 " +
                        $"（{source} → {target}）");

        return moved;
    }

    /// <summary>
    /// 复制缺失的文件（已存在则不覆盖），返回复制的条目数。
    /// 采用复制而非移动：迁移失败时原始数据仍保留，避免数据丢失。
    /// </summary>
    private static int CopyMissing(string sourceDir, string targetDir)
    {
        var count = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var dest = Path.Combine(targetDir, relative);

                if (File.Exists(dest)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, false);
                count++;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"迁移目录失败 {sourceDir} → {targetDir}: {ex.Message}");
        }

        return count;
    }
    /// <summary>该版本是否隔离（便捷方法）。</summary>
    public static bool IsIsolated(string versionId) => Resolve(versionId).Isolated;
}

