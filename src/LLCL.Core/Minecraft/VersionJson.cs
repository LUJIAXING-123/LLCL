using System.Text.Json.Serialization;

namespace LLCL.Core.Minecraft;

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<VersionSummary> Versions { get; set; } = [];

    public sealed class LatestVersions
    {
        [JsonPropertyName("release")] public string Release { get; set; } = string.Empty;
        [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = string.Empty;
    }
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "release";
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("time")] public string Time { get; set; } = string.Empty;
    [JsonPropertyName("releaseTime")] public string ReleaseTime { get; set; } = string.Empty;
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("complianceLevel")] public int ComplianceLevel { get; set; }
}

/// <summary>
/// 版本 JSON（version.json）。继承自 otherVersion 的字段由 <see cref="VersionJson.Merge"/> 处理。
/// </symbolic>
/// </summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "release";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = string.Empty;
    [JsonPropertyName("minimumLauncherVersion")] public int MinimumLauncherVersion { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("assets")] public string Assets { get; set; } = "legacy";
    [JsonPropertyName("assetIndex")] public AssetIndex? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersion? JavaVersion { get; set; }
    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("libraries")] public List<Library> Libraries { get; set; } = [];
    [JsonPropertyName("arguments")] public Arguments? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("logging")] public LoggingConfig? Logging { get; set; }
    [JsonPropertyName("complianceLevel")] public int ComplianceLevel { get; set; }

    /// <summary>父版本 ID（由继承合并过程设置）。</summary>
    [JsonIgnore] public string? ParentId { get; set; }

    /// <summary>原始 JSON 文本（用于保存时保留未知字段）。</summary>
    [JsonIgnore] public string? RawJson { get; set; }

    /// <summary>该版本的加载器类型（原版/Forge/Fabric 等）。</summary>
    [JsonIgnore] public string Loader { get; set; } = "vanilla";
}

public sealed class AssetIndex
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public Artifact? Client { get; set; }
    [JsonPropertyName("server")] public Artifact? Server { get; set; }
    [JsonPropertyName("client_mappings")] public Artifact? ClientMappings { get; set; }
    [JsonPropertyName("server_mappings")] public Artifact? ServerMappings { get; set; }
}

public sealed class Artifact
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}

public sealed class JavaVersion
{
    [JsonPropertyName("component")] public string Component { get; set; } = string.Empty;
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class Library
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("rules")] public List<Rule>? Rules { get; set; }
    [JsonPropertyName("extract")] public ExtractRule? Extract { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>解析为 Maven 坐标。</summary>
    public MavenCoordinate? Coordinate
    {
        get
        {
            try { return MavenCoordinate.Parse(Name); }
            catch { return null; }
        }
    }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public Artifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, Artifact>? Classifiers { get; set; }
}

public sealed class ExtractRule
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

public sealed class Rule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

public sealed class Arguments
{
    [JsonPropertyName("game")] public List<object> Game { get; set; } = [];
    [JsonPropertyName("jvm")] public List<object> Jvm { get; set; } = [];
}

public sealed class LoggingConfig
{
    [JsonPropertyName("client")] public LoggingFile? Client { get; set; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("argument")] public string Argument { get; set; } = string.Empty;
    [JsonPropertyName("file")] public Artifact File { get; set; } = new();
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
}
