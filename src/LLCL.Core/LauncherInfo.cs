using System.Reflection;

namespace LLCL.Core;

/// <summary>发布阶段。大版本 0 期间只会产出 Dev / Beta。</summary>
public enum ReleaseStage
{
    /// <summary>内部测试，不对外分发。</summary>
    Dev,
    /// <summary>内测版，可分享给测试人员，不发布 GitHub。</summary>
    Beta,
    /// <summary>公测版，发布 GitHub。</summary>
    Preview,
    /// <summary>正式版发布前的最后测试。</summary>
    Rc,
    /// <summary>正式版。</summary>
    Release
}

/// <summary>
/// 启动器版本信息。版本号统一由 <c>build/version.json</code> 驱动，
/// 构建时生成 <see cref="BuildInfo"/> 注入程序。
/// </summary>
public static class LauncherInfo
{
    public const string ProductName = "LL Craft Launcher";
    public const string ProductShortName = "LLCL";
    public const string Company = "LL STUDIO";

    /// <summary>完整版本字符串，如 "0.1.0-dev.1"。</summary>
    public static string Version => BuildInfo.Version;

    /// <summary>语义化主体版本，如 "0.1.0"。</summary>
    public static string BaseVersion
    {
        get
        {
            var dash = Version.IndexOf('-');
            return dash < 0 ? Version : Version[..dash];
        }
    }

    public static ReleaseStage Stage
    {
        get
        {
            var dash = Version.IndexOf('-');
            if (dash < 0) return ReleaseStage.Release;

            var suffix = Version[(dash + 1)..];
            var dot = suffix.IndexOf('.');
            var stageText = dot < 0 ? suffix : suffix[..dot];

            return stageText.ToLowerInvariant() switch
            {
                "dev" => ReleaseStage.Dev,
                "beta" => ReleaseStage.Beta,
                "preview" => ReleaseStage.Preview,
                "rc" => ReleaseStage.Rc,
                _ => ReleaseStage.Release
            };
        }
    }

    /// <summary>阶段的中文显示名。</summary>
    public static string StageDisplay => Stage switch
    {
        ReleaseStage.Dev => "开发版",
        ReleaseStage.Beta => "内测版",
        ReleaseStage.Preview => "公测版",
        ReleaseStage.Rc => "候选版",
        _ => "正式版"
    };

    private static int MajorVersion
    {
        get
        {
            var head = BaseVersion.Split('.');
            return head.Length > 0 && int.TryParse(head[0], out var major) ? major : 0;
        }
    }

    /// <summary>是否允许对外发布（大版本 0 永远为 false）。</summary>
    public static bool IsPublicRelease =>
        MajorVersion != 0 && Stage is ReleaseStage.Preview or ReleaseStage.Rc or ReleaseStage.Release;

    /// <summary>是否发布到 GitHub（大版本 0 永远为 false）。</summary>
    public static bool IsGithubStage => IsPublicRelease;

    /// <summary>是否为仅内部使用的版本。</summary>
    public static bool IsInternalOnly => Stage is ReleaseStage.Dev;

    /// <summary>用于窗口标题与界面的展示文本。</summary>
    public static string DisplayVersion => $"{ProductShortName} {Version}";

    /// <summary>构建时间（UTC 写入，此处转本地时区）。</summary>
    public static DateTime BuildTime => BuildInfo.BuildTimeUtc.ToLocalTime();

    /// <summary>程序集版本，如 "0.1.0.0"。</summary>
    public static Version AssemblyVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0, 0);

    /// <summary>形如 "0.1.0-dev.1（开发版，2026-11-01）"。</summary>
    public static string FullDescription
        => $"{Version}（{StageDisplay}，{BuildTime:yyyy-MM-dd}）";
}
