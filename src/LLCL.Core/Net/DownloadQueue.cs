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

            var done = FormatSize(DownloadedBytes);
            var total = TotalBytes > 0 ? FormatSize(TotalBytes) : "未知";
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
        Func<FileDownloader, CancellationToken, Task> work, CancellationToken ct = default)
    {
        var task = new DownloadTask { Title = title, Kind = kind, State = DownloadState.Idle };

        lock (_lock)
        {
            _tasks.Add(task);
        }

        _Notify(task);

        // 串行化：同一时间只跑一个队列任务，避免占满带宽
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
                task.State = downloader.IsPaused ? DownloadState.Paused : DownloadState.Running;

                _Notify(task);
            };

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
            task.Cts?.Dispose();
            task.Cts = null;
            _runnerGate.Release();

            // 终态必须广播一次，否则界面会停留在“下载中”
            _Notify(task);
        }
    }

    /// <summary>统一的通知入口：同时触发状态与进度事件，保证界面不会漏更新。</summary>
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
