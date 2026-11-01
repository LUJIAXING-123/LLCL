using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLCL.Core.Minecraft;

/// <summary>
/// 版本加载与继承合并（原版 → Forge/Fabric 等）。
/// </summary>
public static class VersionLoader
{
    /// <summary>读取指定版本的版本 JSON，并递归合并父版本。</summary>
    public static VersionJson Load(string versionId, string? versionsRoot = null)
    {
        versionsRoot ??= Paths.VersionsDir;
        return _LoadInternal(versionId, versionsRoot, []);
    }

    private static VersionJson _LoadInternal(string versionId, string versionsRoot, List<string> chain)
    {
        if (chain.Contains(versionId))
            throw new InvalidOperationException($"版本继承出现环: {string.Join(" -> ", chain)} -> {versionId}");

        chain.Add(versionId);

        var jsonPath = Path.Combine(versionsRoot, versionId, versionId + ".json");
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"找不到版本文件: {jsonPath}");

        var raw = File.ReadAllText(jsonPath);
        var version = Parse(raw);
        version.RawJson = raw;

        if (!string.IsNullOrEmpty(version.InheritsFrom))
        {
            var parent = _LoadInternal(version.InheritsFrom, versionsRoot, chain);
            version = Merge(parent, version);
        }

        return version;
    }

    public static VersionJson Parse(string json)
    {
        // 使用原始 JsonNode 保留结构，便于后续保存时合并
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("版本 JSON 为空");

        var version = JsonSerializer.Deserialize<VersionJson>(node.ToJsonString(), LauncherConfig.JsonOptions)
                      ?? new VersionJson();

        // 单独解析 arguments 中的对象数组（object[] 反序列化时是 Dictionary）
        version.Arguments = _ParseArguments(node["arguments"]);
        return version;
    }

    private static Arguments? _ParseArguments(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var args = new Arguments();
        args.Game = _ToEntries(obj["game"]);
        args.Jvm = _ToEntries(obj["jvm"]);
        return args;
    }

    private static List<object> _ToEntries(JsonNode? node)
    {
        if (node is not JsonArray arr) return [];
        var list = new List<object>();
        foreach (var item in arr)
        {
            if (item is null) continue;
            if (item is JsonValue && item.GetValueKind() == JsonValueKind.String)
                list.Add(item.GetValue<string>());
            else if (item is JsonObject o)
                list.Add(JsonSerializer.Deserialize<Dictionary<string, object?>>(o.ToJsonString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Dictionary<string, object?>());
        }

        return list;
    }

    /// <summary>
    /// 合并父版本与子版本：子版本覆盖标量字段，库、参数追加。
    /// </summary>
    public static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        var result = new VersionJson
        {
            Id = child.Id,
            Type = child.Type,
            MainClass = string.IsNullOrEmpty(child.MainClass) ? parent.MainClass : child.MainClass,
            InheritsFrom = parent.Id,
            Assets = string.IsNullOrEmpty(child.Assets) || child.Assets == "legacy" ? parent.Assets : child.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = child.Downloads ?? parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            Time = child.Time ?? parent.Time,
            Libraries = [.. parent.Libraries, .. child.Libraries],
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Logging = child.Logging ?? parent.Logging,
            ComplianceLevel = Math.Max(parent.ComplianceLevel, child.ComplianceLevel),
            Loader = child.Loader,
            RawJson = child.RawJson
        };

        // 参数合并：父在前，子在后（Minecraft 客户端自身会去掉重复项）
        var game = new List<object>();
        var jvm = new List<object>();
        if (parent.Arguments is not null)
        {
            game.AddRange(parent.Arguments.Game);
            jvm.AddRange(parent.Arguments.Jvm);
        }

        if (child.Arguments is not null)
        {
            game.AddRange(child.Arguments.Game);
            jvm.AddRange(child.Arguments.Jvm);
        }

        result.Arguments = new Arguments { Game = game, Jvm = jvm };
        return result;
    }

    /// <summary>保存版本 JSON（用于安装加载器后写入）。</summary>
    public static void Save(VersionJson version, string? versionsRoot = null)
    {
        versionsRoot ??= Paths.VersionsDir;
        var dir = Path.Combine(versionsRoot, version.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(version, LauncherConfig.JsonOptions);
        File.WriteAllText(Path.Combine(dir, version.Id + ".json"), json);
    }

    /// <summary>列出已安装的全部版本 ID。</summary>
    public static List<string> GetInstalledVersions(string? versionsRoot = null)
    {
        versionsRoot ??= Paths.VersionsDir;
        if (!Directory.Exists(versionsRoot)) return [];

        var result = new List<string>();
        foreach (var dir in Directory.GetDirectories(versionsRoot))
        {
            var id = Path.GetFileName(dir);
            if (File.Exists(Path.Combine(dir, id + ".json"))) result.Add(id);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}

