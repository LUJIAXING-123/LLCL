using System.Collections.Concurrent;

namespace LLCL.Core.Net;

/// <summary>下载任务类型，用于界面分组展示。</summary>
public enum DownloadTaskKind
{
    Vanilla,
    Loader,
    Resource,
    Other
}

/// <summary>一个下载队列任务。</summary>
public sealed class DownloadTask
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DownloadTaskKind Kind { get; set; } = DownloadTaskKind.Other;
    public DownloadState State { get; internal set; } = DownloadState.Idle;
    public double Ratio { get; internal set; }
    public long DownloadedBytes { get; internal set; }
    public long TotalBytes { get; internal set; }
    public long SpeedBytesPerSecond { get; internal set; }
    public string? CurrentFile { get; internal set; }
    public string? ErrorMessage { get; internal set; }
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>
    /// 该任务安装到的游戏版本 ID。
    /// 取消时据此清理整个版本目录，避免留下半成品。
    /// </summary>
    public string? TargetVersion { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal FileDownloader? Downloader { get; set; }
    internal Task? Runner { get; set; }

    /// <summary>进度文本，如 "12.3 MB / 45.6 MB · 1.2 MB/s"。</summary>
    public string ProgressText
    {
        get
        {
            if (State == DownloadState.Paused) return "已暂停";
            if (State == DownloadState.Completed) return "已完成";
            if (State == DownloadState.Failed) return ErrorMessage ?? "下载失败";
            if (State == DownloadState.Canceled) return "已取消";
            if (State == DownloadState.Idle) return "等待中";

            var done = FormatSize(DownloadedBytes);

            // 总大小未知时不要显示“未知”，只展示已下载量与速度
            if (TotalBytes <= 0)
            {
                var speedOnly = FileDownloader.FormatSpeed(SpeedBytesPerSecond);
                return speedOnly == "—" ? $"已下载 {done}" : $"已下载 {done} · {speedOnly}";
            }

            var total = FormatSize(TotalBytes);
            var speed = FileDownloader.FormatSpeed(SpeedBytesPerSecond);
            return $"{done} / {total} · {speed}";
        }
    }

    public string StateText => State switch
    {
        DownloadState.Idle => "等待中",
        DownloadState.Running => "下载中",
        DownloadState.Paused => "已暂停",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => "失败",
        DownloadState.Canceled => "已取消",
        _ => "未知"
    };

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        var mb = kb / 1024;
        if (mb < 1024) return $"{mb:F1} MB";
        return $"{mb / 1024:F2} GB";
    }
}

/// <summary>
/// 全局下载队列：串行执行任务，支持暂停 / 继续 / 取消与状态广播。
/// </summary>
public static class DownloadQueue
{
    private static readonly List<DownloadTask> _tasks = [];
    private static readonly SemaphoreSlim _runnerGate = new(1, 1);
    private static readonly object _lock = new();

    /// <summary>任务列表变化（新增 / 移除 / 状态更新）。</summary>
    public static event Action? Changed;

    /// <summary>最近一次进度更新。</summary>
    public static event Action<DownloadTask>? ProgressUpdated;

    public static IReadOnlyList<DownloadTask> Tasks
    {
        get
        {
            lock (_lock) return _tasks.ToList();
        }
    }

    public static bool HasActiveTask
    {
        get
        {
            lock (_lock)
                return _tasks.Any(t => t.State is DownloadState.Running or DownloadState.Paused
                                       or DownloadState.Idle);
        }
    }

    /// <summary>
    /// 把一个下载操作入队并等待完成。
    /// </summary>
    /// <param name="title">任务标题，显示在界面上。</param>
    /// <param name="kind">任务类型。</param>
    /// <param name="work">实际下载逻辑，收到一个 <see cref="FileDownloader"/> 用于暂停控制。</param>
    /// <param name="ct">外部取消令牌。</param>
    public static async Task RunAsync(string title, DownloadTaskKind kind,
        Func<FileDownloader, CancellationToken, Task> work, CancellationToken ct = default,
        string? targetVersion = null)
    {
        var task = new DownloadTask
        {
            Title = title,
            Kind = kind,
            State = DownloadState.Idle,
            TargetVersion = targetVersion
        };

        lock (_lock)
        {
            _tasks.Add(task);
        }

        _Notify(task);

        // 串行化：同一时间只跑一个队列任务，避免占满带宽
        var syncing = true;

        await _runnerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var downloader = new FileDownloader();
            task.Downloader = downloader;

            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            task.Cts = linked;
            task.State = DownloadState.Running;
            _Notify(task);

            downloader.Progress += (_, e) =>
            {
                task.Ratio = e.Ratio;
                task.DownloadedBytes = e.DownloadedBytes;
                task.TotalBytes = e.TotalBytes;
                task.SpeedBytesPerSecond = e.SpeedBytesPerSecond;
                task.CurrentFile = e.CurrentFile;

                // 以下载器实际状态为准，避免 UI 与真实状态不一致
                var wasPaused = task.State == DownloadState.Paused;
                task.State = downloader.IsPaused ? DownloadState.Paused : DownloadState.Running;

                // 状态发生切换时才需要结构刷新，纯进度更新只发进度事件
                if (wasPaused != (task.State == DownloadState.Paused)) _Notify(task);
                else _NotifyProgress(task);
            };

            // 周期性同步：某些安装流程（如 VanillaInstaller / LoaderInstaller）
            // 内部自行创建 FileDownloader，不会把进度事件传到队列，
            // 因此这里主动读取下载器的实时统计，避免任务一直显示“未知”。
            var syncTask = Task.Run(async () =>
            {
                while (syncing && !linked.IsCancellationRequested)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    if (!syncing) break;

                    // 优先采样队列自己的下载器；若内部流程新建了下载器则采样 Active
                    var activeDownloader = FileDownloader.Active ?? downloader;
                    var snapshot = activeDownloader.Snapshot;
                    if (snapshot is null) continue;

                    task.Ratio = snapshot.Ratio;
                    task.DownloadedBytes = snapshot.DownloadedBytes;
                    task.TotalBytes = snapshot.TotalBytes;
                    task.SpeedBytesPerSecond = snapshot.SpeedBytesPerSecond;
                    task.CurrentFile = snapshot.CurrentFile;

                    if (downloader.IsPaused) task.State = DownloadState.Paused;
                    else if (task.State == DownloadState.Paused) task.State = DownloadState.Running;

                    _NotifyProgress(task);
                }
            }, linked.Token);

            task.Runner = work(downloader, linked.Token);
            await task.Runner.ConfigureAwait(false);

            task.State = DownloadState.Completed;
            task.Ratio = 1;
            task.SpeedBytesPerSecond = 0;
        }
        catch (OperationCanceledException)
        {
            task.State = DownloadState.Canceled;
            task.SpeedBytesPerSecond = 0;
            throw;
        }
        catch (Exception ex)
        {
            task.State = DownloadState.Failed;
            task.ErrorMessage = Link.FriendlyError.ToText(ex);
            task.SpeedBytesPerSecond = 0;
            throw;
        }
        finally
        {
            syncing = false;
            task.Cts?.Dispose();
            task.Cts = null;
            _runnerGate.Release();

            // 终态必须广播一次，否则界面会停留在“下载中”
            _Notify(task);
        }
    }

    /// <summary>
    /// 进度更新通知：只触发 <see cref="ProgressUpdated"/>，不触发 <see cref="Changed"/>。
    /// 进度每 500ms 采样一次，若同时触发 Changed 会让界面反复重建列表而闪烁。
    /// </summary>
    private static void _NotifyProgress(DownloadTask task)
    {
        try { ProgressUpdated?.Invoke(task); } catch (Exception ex) { Logger.Warn($"下载进度通知失败: {ex.Message}"); }
    }

    /// <summary>
    /// 结构性变化通知（新增 / 移除 / 状态切换）：同时触发两个事件。
    /// </summary>
    private static void _Notify(DownloadTask task)
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Logger.Warn($"下载队列通知失败: {ex.Message}"); }
        try { ProgressUpdated?.Invoke(task); } catch (Exception ex) { Logger.Warn($"下载进度通知失败: {ex.Message}"); }
    }

    /// <summary>暂停指定任务（或全部正在运行的任务）。</summary>
    public static void Pause(string? taskId = null)
    {
        List<DownloadTask> affected = [];

        lock (_lock)
        {
            foreach (var task in _tasks)
            {
                if (taskId is not null && task.Id != taskId) continue;
                if (task.State != DownloadState.Running) continue;

                task.Downloader?.Pause();
                task.State = DownloadState.Paused;
                affected.Add(task);
            }
        }

        foreach (var task in affected) _Notify(task);
        if (affected.Count == 0) Changed?.Invoke();
    }

    /// <summary>继续指定任务（或全部已暂停的任务）。</summary>
    public static void Resume(string? taskId = null)
    {
        List<DownloadTask> affected = [];

        lock (_lock)
        {
            foreach (var task in _tasks)
            {
                if (taskId is not null && task.Id != taskId) continue;
                if (task.State != DownloadState.Paused) continue;

                task.Downloader?.Resume();
                task.State = DownloadState.Running;
                affected.Add(task);
            }
        }

        foreach (var task in affected) _Notify(task);
        if (affected.Count == 0) Changed?.Invoke();
    }

    /// <summary>取消指定任务（或全部未完成任务）。</summary>
    public static void Cancel(string? taskId = null)
    {
        List<DownloadTask> affected = [];

        lock (_lock)
        {
            foreach (var task in _tasks)
            {
                if (taskId is not null && task.Id != taskId) continue;
                if (task.State is DownloadState.Completed or DownloadState.Failed
                    or DownloadState.Canceled) continue;

                task.Downloader?.Cancel();
                try { task.Cts?.Cancel(); } catch { }
                affected.Add(task);
            }
        }

        foreach (var task in affected) _Notify(task);
        if (affected.Count == 0) Changed?.Invoke();
    }

    /// <summary>
    /// 取消任务并彻底清理：删除该版本目录下的全部文件（含已完成部分），
    /// 让下一次安装从干净状态开始。
    /// </summary>
    public static void CancelAndPurge(string taskId)
    {
        DownloadTask? target;
        var affected = new List<DownloadTask>();

        lock (_lock)
        {
            target = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (target is null) return;

            target.Downloader?.Cancel();
            try { target.Cts?.Cancel(); } catch { }
            affected.Add(target);

            // 同版本的其它任务一并取消（例如先下原版再装加载器）
            var version = target.TargetVersion;
            if (!string.IsNullOrEmpty(version))
            {
                foreach (var other in _tasks)
                {
                    if (other.Id == taskId) continue;
                    if (!string.Equals(other.TargetVersion, version, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (other.State is DownloadState.Completed or DownloadState.Failed
                        or DownloadState.Canceled) continue;

                    other.Downloader?.Cancel();
                    try { other.Cts?.Cancel(); } catch { }
                    affected.Add(other);
                }
            }
        }

        foreach (var task in affected) _Notify(task);

        if (!string.IsNullOrEmpty(target.TargetVersion))
            PurgeVersionDirectory(target.TargetVersion);
    }

    /// <summary>彻底删除某个版本的目录与相关缓存文件。</summary>
    private static void PurgeVersionDirectory(string versionId)
    {
        // 安全校验：versionId 为空时 Path.Combine 会得到 versions 根目录，
        // 直接删除会清掉所有版本，必须拦截。
        if (string.IsNullOrWhiteSpace(versionId) ||
            versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            versionId.Contains(Path.DirectorySeparatorChar) ||
            versionId.Contains(Path.AltDirectorySeparatorChar))
        {
            Logger.Warn($"拒绝清理版本目录：非法版本 ID「{versionId}」");
            return;
        }

        try
        {
            var versionDir = Path.Combine(Paths.VersionsDir, versionId);
            var fullDir = Path.GetFullPath(versionDir);
            var fullRoot = Path.GetFullPath(Paths.VersionsDir);

            // 目标必须在 versions 目录之内，且不能是 versions 本身
            if (!fullDir.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullDir, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn($"拒绝清理版本目录：路径越界 {fullDir}");
                return;
            }

            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, true);
                Logger.Info($"已删除版本目录: {versionDir}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"删除版本目录失败 {versionId}: {ex.Message}");
        }
    }

    /// <summary>移除已完成 / 失败 / 取消的任务记录。</summary>
    public static void PruneFinished()
    {
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.State is DownloadState.Completed or DownloadState.Failed
                or DownloadState.Canceled);
        }

        Changed?.Invoke();
    }

    /// <summary>移除全部任务记录（不影响进行中的下载）。</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _tasks.Clear();
        }

        Changed?.Invoke();
    }
}





