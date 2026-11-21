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

    /// <summary>
    /// 是否处于「不确定进度」状态。
    /// 运行安装器这类阶段没有可量化的比例，界面应显示滚动条而不是固定百分比。
    /// </summary>
    public bool IsIndeterminate { get; internal set; }

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

            // 不确定进度阶段（例如运行安装器）直接显示阶段提示，
            // 这时没有字节数可报，显示「已下载 0 B」反而像卡住了。
            if (IsIndeterminate && !string.IsNullOrWhiteSpace(CurrentFile))
                return CurrentFile!;

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
    private static readonly SemaphoreSlim _runnerGate = new(3, 3);
    private static readonly object _lock = new();

    /// <summary>
    /// 队列是否处于暂停状态。
    /// <para>
    /// 安装整合包、补全版本文件这些流程会在内部自行创建 FileDownloader，
    /// 队列拿不到那些实例，只暂停自己持有的下载器等于什么也没停住。
    /// 所以暂停做成队列级：置位后所有下载器在传输循环里都会等待。
    /// 用简单的 volatile 布尔而不是事件对象，
    /// 避免多任务并行时 Set/Reset 互相覆盖导致状态错乱。
    /// </para>
    /// </summary>
    internal static volatile bool IsQueuePaused;

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
    public static async Task<DownloadState> RunAsync(string title, DownloadTaskKind kind,
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

        // 允许并行的任务数。此前是 1（完全串行），
        // 用户同时装整合包又要下模组时，后一个会一直排队到前一个结束，
        // 看起来像「多个任务不能并行下载」。限制到 3 个既能并行，
        // 又不至于把带宽摊薄到每个都很慢。
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

                    // 这里不要再改 task.State。
                    // 之前按下载器状态回写 Running/Paused，会把 Pause() 刚设好的
                    // 暂停状态在 500ms 内又翻回运行，界面上看起来就像「暂停没反应」。
                    // 状态现在只由 Pause/Resume 与任务终态负责。

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
            // 用户主动取消不是错误，不要抛给调用方。
            // 之前这里 throw 之后，调用方按「失败」处理，
            // 界面上就会在正常取消后弹出一条「下载资源失败: A task was canceled.」。
            task.State = DownloadState.Canceled;
            task.ErrorMessage = null;
            task.SpeedBytesPerSecond = 0;
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

            // 只有确实没有别的任务处于暂停时才放开全局闸门。
            // 允许多任务并行后，这里如果无条件 Set，
            // 任何一个任务结束都会把其他正在暂停的任务一起放开。
            lock (_lock)
            {
                IsQueuePaused = _tasks.Any(t => t.State == DownloadState.Paused);
            }

            _runnerGate.Release();

            // 终态必须广播一次，否则界面会停留在“下载中”
            _Notify(task);
        }

        // 把终态返回给调用方。用户取消是正常结束、不抛异常，
        // 调用方需要据此判断该显示「已完成」还是「已取消」。
        return task.State;
    }

    /// <summary>
    /// 把正在运行的任务切换成「不确定进度」并显示给定提示；
    /// 传 null 表示恢复按实际比例显示。
    /// <para>
    /// 用于运行安装器这类没有可量化进度的阶段：进度条一直在动，
    /// 但比例长时间不变，界面看起来像卡住。
    /// </para>
    /// </summary>
    public static void SetIndeterminate(string? stageText)
    {
        List<DownloadTask> affected = [];

        lock (_lock)
        {
            foreach (var task in _tasks)
            {
                if (task.State != DownloadState.Running) continue;

                task.IsIndeterminate = stageText is not null;
                if (stageText is not null) task.CurrentFile = stageText;
                affected.Add(task);
            }
        }

        foreach (var task in affected) _Notify(task);
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

                // 让所有下载器（含内部自行创建的那些）一起停下来
                IsQueuePaused = true;

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

        // 只要还有任务处于暂停，队列就保持暂停状态
        lock (_lock) IsQueuePaused = _tasks.Any(t => t.State == DownloadState.Paused);

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
    /// 取消单个任务并把它的记录从列表移除。
    /// <para>
    /// 只影响这一个任务：下载器在取消时已经清掉自己的 .part 临时文件，
    /// 不会动到同版本里其它已经下好的资源（例如模组、资源包）。
    /// </para>
    /// </summary>
    public static void CancelAndRemove(string taskId)
    {
        lock (_lock)
        {
            var target = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (target is null) return;

            target.Downloader?.Cancel();
            try { target.Cts?.Cancel(); } catch { }
            _tasks.Remove(target);
        }

        Changed?.Invoke();
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







