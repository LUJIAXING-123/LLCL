using System.Text.RegularExpressions;

namespace LLCL.Core.Minecraft;

/// <summary>Maven 坐标 group:artifact:version[:classifier][@ext]。</summary>
public sealed class MavenCoordinate
{
    public string Group { get; init; } = string.Empty;
    public string Artifact { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Classifier { get; init; } = string.Empty;
    public string Extension { get; init; } = "jar";

    public static MavenCoordinate Parse(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3) throw new FormatException($"无效的 Maven 坐标: {name}");

        return new MavenCoordinate
        {
            Group = parts[0],
            Artifact = parts[1],
            Version = parts[2],
            Classifier = parts.Length > 3 ? parts[3] : string.Empty,
            Extension = ext
        };
    }

    /// <summary>库文件相对路径，如 org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar。</summary>
    public string ToPath()
    {
        var fileName = Classifier.Length > 0
            ? $"{Artifact}-{Version}-{Classifier}.{Extension}"
            : $"{Artifact}-{Version}.{Extension}";
        return $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{fileName}";
    }

    public string ToUrl(string baseUrl = "https://libraries.minecraft.net")
        => $"{baseUrl.TrimEnd('/')}/{ToPath()}";

    public override string ToString() => $"{Group}:{Artifact}:{Version}" + (Classifier.Length > 0 ? $":{Classifier}" : string.Empty);
}

public static class RuleEvaluator
{
    private const string CurrentOsName = "windows";
    private static readonly string CurrentArch =
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            is System.Runtime.InteropServices.Architecture.X86 ? "x86"
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                is System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>评估规则列表是否允许该条目（默认不允许，当无规则时允许）。</summary>
    public static bool IsAllowed(List<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule)) continue;
            allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(Rule rule)
    {
        if (rule.Os is not null)
        {
            if (!string.IsNullOrEmpty(rule.Os.Name) && rule.Os.Name != CurrentOsName) return false;
            if (!string.IsNullOrEmpty(rule.Os.Arch) && rule.Os.Arch != CurrentArch) return false;
        }

        if (rule.Features is not null)
        {
            foreach (var (key, value) in rule.Features)
            {
                var actual = key switch
                {
                    "has_custom_resolution" => false,
                    "is_demo_user" => false,
                    "has_quick_plays_support" => false,
                    "is_quick_play_singleplayer" => false,
                    "is_quick_play_multiplayer" => false,
                    "is_quick_play_realms" => false,
                    _ => false
                };
                if (actual != value) return false;
            }
        }

        return true;
    }
}

public static class ArgumentEvaluator
{
    private static readonly Regex ParamRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    /// <sumary>将参数条目（字符串或规则对象）展开为字符串列表。</sumary>
    public static List<string> Flatten(List<object>? entries)
    {
        var result = new List<string>();
        if (entries is null) return result;

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case string s:
                    result.Add(s);
                    break;
                case Dictionary<string, object?> dict:
                {
                    var rules = _ParseRules(dict);
                    if (rules is not null && !RuleEvaluator.IsAllowed(rules)) break;

                    var value = dict.TryGetValue("value", out var v) ? v : null;
                    switch (value)
                    {
                        case string single:
                            result.Add(single);
                            break;
                        case List<object?> list:
                            foreach (var item in list)
                                if (item is string str) result.Add(str);
                            break;
                    }

                    break;
                }
            }
        }

        return result;
    }

    private static List<Rule>? _ParseRules(Dictionary<string, object?> dict)
    {
        if (!dict.TryGetValue("rules", out var raw) || raw is not List<object?> list) return null;
        var rules = new List<Rule>();
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> rule) continue;
            var r = new Rule { Action = rule.TryGetValue("action", out var a) && a is string s ? s : "allow" };
            if (rule.TryGetValue("os", out var osRaw) && osRaw is Dictionary<string, object?> os)
            {
                r.Os = new OsRule
                {
                    Name = os.TryGetValue("name", out var n) && n is string ns ? ns : null,
                    Version = os.TryGetValue("version", out var ver) && ver is string vs ? vs : null,
                    Arch = os.TryGetValue("arch", out var arc) && arc is string ac ? ac : null
                };
            }

            if (rule.TryGetValue("features", out var fRaw) && fRaw is Dictionary<string, object?> features)
            {
                r.Features = features.Where(kv => kv.Value is bool).ToDictionary(kv => kv.Key, kv => (bool)kv.Value!);
            }

            rules.Add(r);
        }

        return rules;
    }

    public static string Substitute(string input, IReadOnlyDictionary<string, string> values)
        => ParamRegex.Replace(input, m =>
        {
            var key = m.Groups[1].Value;
            return values.TryGetValue(key, out var value) ? value : m.Value;
        });
}
