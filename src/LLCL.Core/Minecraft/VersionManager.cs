using System.Text.Json;

namespace LLCL.Core.Minecraft;

/// <summary>
/// 版本管理：扫描、元信息收集、重命名、删除、迁移。
/// </summary>
public static class VersionManager
{
    /// <summary>扫描全部已安装版本，返回带详细元信息的列表。</summary>
    public static List<VersionEntry> ScanAll()
    {
        var result = new List<VersionEntry>();
        if (!Directory.Exists(Paths.VersionsDir)) return result;

        foreach (var dir in Directory.GetDirectories(Paths.VersionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            var settings = VersionSettings.Load(id);

            if (!File.Exists(jsonPath))
            {
                result.Add(new VersionEntry
                {
                    Id = id,
                    DisplayName = settings.DisplayName ?? id,
                    IsBroken = true,
                    BrokenReason = "缺少版本 JSON",
                    SortOrder = settings.SortOrder
                });
                continue;
            }

            try
            {
                var version = VersionLoader.Load(id);
                var loader = DetectLoader(version);
                var javaRequired = version.JavaVersion?.MajorVersion;

                result.Add(new VersionEntry
                {
                    Id = id,
                    DisplayName = settings.DisplayName ?? id,
                    Loader = loader.Name,
                    LoaderVersion = loader.Version,
                    JavaMajor = javaRequired,
                    JavaPath = FindJavaFor(javaRequired),
                    SizeBytes = ComputeSize(dir),
                    CreatedAt = File.GetCreationTime(jsonPath),
                    SortOrder = settings.SortOrder
                });
            }
            catch (Exception ex)
            {
                result.Add(new VersionEntry
                {
                    Id = id,
                    DisplayName = settings.DisplayName ?? id,
                    IsBroken = true,
                    BrokenReason = ex.Message,
                    SortOrder = settings.SortOrder
                });
            }
        }

        result.Sort((a, b) =>
        {
            if (a.SortOrder != b.SortOrder) return b.SortOrder.CompareTo(a.SortOrder);
            return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    /// <summary>从主类与库推断加载器类型。</summary>
    public static (string Name, string? Version) DetectLoader(VersionJson version)
    {
        var main = version.MainClass ?? string.Empty;

        if (main.Contains("neoforged", StringComparison.OrdinalIgnoreCase) ||
            version.Libraries.Any(l => l.Name.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase)))
        {
            var ver = version.Libraries
                .Select(l => l.Coordinate)
                .FirstOrDefault(c => c is not null &&
                                     c.Group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                                     c.Artifact is "neoforge" or "forge")
                ?.Version;
            return ("neoforge", ver);
        }

        if (main.Contains("fml", StringComparison.OrdinalIgnoreCase) ||
            version.Libraries.Any(l => l.Name.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase)))
        {
            var ver = version.Libraries
                .Select(l => l.Coordinate)
                .FirstOrDefault(c => c is not null &&
                                     c.Group.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                                     c.Artifact == "forge")
                ?.Version;
            return ("forge", ver);
        }

        if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase) ||
            version.Libraries.Any(l => l.Name.Contains("org.quiltmc", StringComparison.OrdinalIgnoreCase)))
            return ("quilt", null);

        if (main.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
            version.Libraries.Any(l => l.Name.Contains("net.fabricmc", StringComparison.OrdinalIgnoreCase)))
            return ("fabric", null);

        if (main.Contains("liteloader", StringComparison.OrdinalIgnoreCase)) return ("liteloader", null);
        if (main.Contains("optifine", StringComparison.OrdinalIgnoreCase)) return ("optifine", null);

        return ("vanilla", null);
    }

    private static string? FindJavaFor(int? required)
    {
        if (required is null) return null;
        try
        {
            return JavaScanner.SelectForVersion(required.Value)?.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>计算版本目录大小（不含 natives 解压结果时的近似值）。</summary>
    public static long ComputeSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>重命名版本（同时修改目录、JSON 内的 id 与引用它的子版本）。</summary>
    public static bool Rename(string oldId, string newId, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(newId))
            {
                error = "新名称不能为空";
                return false;
            }

            var invalid = Path.GetInvalidFileNameChars();
            if (newId.Any(c => invalid.Contains(c)))
            {
                error = "名称包含非法字符";
                return false;
            }

            var oldDir = Path.Combine(Paths.VersionsDir, oldId);
            var newDir = Path.Combine(Paths.VersionsDir, newId);

            if (!Directory.Exists(oldDir))
            {
                error = "版本不存在";
                return false;
            }

            if (Directory.Exists(newDir))
            {
                error = "同名版本已存在";
                return false;
            }

            Directory.Move(oldDir, newDir);

            // 重命名 JSON 与 jar
            var oldJson = Path.Combine(newDir, oldId + ".json");
            var newJson = Path.Combine(newDir, newId + ".json");
            if (File.Exists(oldJson)) File.Move(oldJson, newJson);

            var oldJar = Path.Combine(newDir, oldId + ".jar");
            var newJar = Path.Combine(newDir, newId + ".jar");
            if (File.Exists(oldJar)) File.Move(oldJar, newJar);

            // 更新 JSON 内的 id
            if (File.Exists(newJson))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(newJson));
                if (node is System.Text.Json.Nodes.JsonObject obj)
                {
                    obj["id"] = newId;
                    File.WriteAllText(newJson, obj.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }));
                }
            }

            // 更新子版本对旧 ID 的继承引用
            foreach (var subDir in Directory.GetDirectories(Paths.VersionsDir))
            {
                var subId = Path.GetFileName(subDir);
                var subJson = Path.Combine(subDir, subId + ".json");
                if (!File.Exists(subJson)) continue;

                try
                {
                    var text = File.ReadAllText(subJson);
                    if (!text.Contains($"\"{oldId}\"", StringComparison.Ordinal)) continue;
                    var node = System.Text.Json.Nodes.JsonNode.Parse(text);
                    if (node is System.Text.Json.Nodes.JsonObject obj &&
                        obj["inheritsFrom"]?.GetValue<string>() == oldId)
                    {
                        obj["inheritsFrom"] = newId;
                        File.WriteAllText(subJson, obj.ToJsonString(new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        }));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"更新子版本继承引用失败 {subId}: {ex.Message}");
                }
            }

            // 迁移版本设置
            var settingsPath = Path.Combine(newDir, "llcl-version.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var settings = VersionSettings.Load(newId);
                    settings.FilePath = VersionSettings.PathOf(newId);
                    settings.Save();
                }
                catch { }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error(ex, $"重命名版本 {oldId} -> {newId} 失败");
            return false;
        }
    }

    /// <summary>删除版本目录（含其中全部文件）。</summary>
    public static bool Delete(string id, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.Combine(Paths.VersionsDir, id);
            if (!Directory.Exists(dir))
            {
                error = "版本不存在";
                return false;
            }

            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error(ex, $"删除版本 {id} 失败");
            return false;
        }
    }

    /// <summary>打开版本所在目录。</summary>
    public static string GetDirectory(string id) => Path.Combine(Paths.VersionsDir, id);

    /// <summary>把未整理的版本（或任意目录）导入到 versions 下。</summary>
    public static bool ImportFromFolder(string sourceDir, string? newId, out string? error)
    {
        error = null;
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                error = "源目录不存在";
                return false;
            }

            // 找到目录内唯一的版本 JSON
            var jsonFiles = Directory.GetFiles(sourceDir, "*.json")
                .Where(f => !Path.GetFileName(f).StartsWith("llcl-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var id = newId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = jsonFiles.Count > 0
                    ? Path.GetFileNameWithoutExtension(jsonFiles[0])
                    : Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar));
            }

            var target = Path.Combine(Paths.VersionsDir, id!);
            if (Directory.Exists(target))
            {
                error = "目标版本已存在";
                return false;
            }

            Directory.CreateDirectory(Paths.VersionsDir);

            // 跨卷移动需要复制
            if (Path.GetPathRoot(sourceDir) == Path.GetPathRoot(Paths.VersionsDir))
                Directory.Move(sourceDir, target);
            else
                CopyDirectory(sourceDir, target);

            // 确保 JSON 名称与目录名一致
            var expectedJson = Path.Combine(target, id + ".json");
            if (!File.Exists(expectedJson) && jsonFiles.Count > 0)
            {
                var original = Path.Combine(target, Path.GetFileName(jsonFiles[0]));
                if (File.Exists(original)) File.Move(original, expectedJson);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error(ex, "导入版本失败");
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
