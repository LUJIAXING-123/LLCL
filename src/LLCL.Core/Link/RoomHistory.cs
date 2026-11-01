using System.Text.Json;

namespace LLCL.Core.Link;

/// <summary>一条历史房间记录。</summary>
public sealed class RoomHistoryEntry
{
    public string Code { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool IsHost { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime LastUsed { get; set; }
    public int UseCount { get; set; } = 1;

    /// <summary>用于展示的简述。</summary>
    public string Summary
        => $"{(IsHost ? "创建" : "加入")} · {DisplayName}" +
           (Port > 0 ? $" · 端口 {Port}" : string.Empty);

    public string TimeDisplay => LastUsed.ToString("MM-dd HH:mm");
}

/// <summary>最近房间历史（持久化到 LLCL 数据目录）。</summary>
public static class RoomHistory
{
    private const int MaxEntries = 15;

    private static readonly string FilePath = Path.Combine(Paths.DataRoot, "room-history.json");
    private static List<RoomHistoryEntry>? _entries;

    /// <summary>按最近使用时间倒序的条目。</summary>
    public static IReadOnlyList<RoomHistoryEntry> Entries
    {
        get
        {
            EnsureLoaded();
            return _entries!
                .OrderByDescending(e => e.LastUsed)
                .ToList();
        }
    }

    public static event Action? Changed;

    /// <summary>记录一次房间使用（同一房间码会合并并更新计数）。</summary>
    public static void Add(string code, int port, bool isHost, string displayName)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        EnsureLoaded();

        var existing = _entries!.FirstOrDefault(e =>
            string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase) &&
            e.IsHost == isHost);

        if (existing is not null)
        {
            existing.LastUsed = DateTime.Now;
            existing.UseCount++;
            existing.Port = port > 0 ? port : existing.Port;
            existing.DisplayName = displayName;
        }
        else
        {
            _entries!.Add(new RoomHistoryEntry
            {
                Code = code,
                Port = port,
                IsHost = isHost,
                DisplayName = displayName,
                LastUsed = DateTime.Now
            });
        }

        // 截断到上限
        var overflow = _entries!.Count - MaxEntries;
        if (overflow > 0)
        {
            var oldest = _entries!.OrderBy(e => e.LastUsed).Take(overflow).ToList();
            foreach (var entry in oldest) _entries.Remove(entry);
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>删除单条记录。</summary>
    public static void Remove(RoomHistoryEntry entry)
    {
        EnsureLoaded();
        _entries!.RemoveAll(e => string.Equals(e.Code, entry.Code, StringComparison.OrdinalIgnoreCase)
                                 && e.IsHost == entry.IsHost);
        Save();
        Changed?.Invoke();
    }

    /// <summary>清空历史。</summary>
    public static void Clear()
    {
        EnsureLoaded();
        _entries!.Clear();
        Save();
        Changed?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (_entries is not null) return;

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _entries = JsonSerializer.Deserialize<List<RoomHistoryEntry>>(json, LauncherConfig.JsonOptions)
                           ?? [];
            }
            else
            {
                _entries = [];
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取房间历史失败: {ex.Message}");
            _entries = [];
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_entries, LauncherConfig.JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warn($"保存房间历史失败: {ex.Message}");
        }
    }
}
