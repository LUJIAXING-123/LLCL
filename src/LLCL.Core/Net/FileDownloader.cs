using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LLCL.Core.Net;

/// <summary>一个待下载文件。</summary>
public sealed class DownloadItem
{
    /// <summary>官方 URL，实际请求会经 <see cref="DownloadMirror.MapUrl"/> 映射到当前下载源。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>目标绝对路径。</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>期望的 SHA1（小写），空表示不校验。</summary>
    public string? Sha1 { get; set; }

    /// <summary>期望大小，0 表示未知。</summary>
    public long Size { get; set; }

    /// <summary>备用 URL 列表（镜像 / 官方交叉回退）。</summary>
    public List<string> FallbackUrls { get; } = [];

    public override string ToString() => $"{TargetPath} <- {Url}";
}

public sealed class DownloadProgressEventArgs : EventArgs
{
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public long TotalFiles { get; init; }
    public long CompletedFiles { get; init; }
    public long FailedFiles { get; init; }
    public long SpeedBytesPerSecond { get; init; }
    public string? CurrentFile { get; init; }

    /// <summary>0..1 的比例。</summary>
    public double Ratio => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1)
        : TotalFiles > 0 ? Math.Clamp((double)CompletedFiles / TotalFiles, 0, 1) : 0;
}

/// <summary>下载执行状态。</summary>
public enum DownloadState
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// 多线程文件下载器。
/// <list type="bullet">
///   <item>SHA1 完整性校验：校验失败自动重试，并回退到备用源</item>
///   <item>断点续传：利用 HTTP Range 从 .part 文件继续</item>
///   <item>可暂停 / 继续（<see cref="Pause"/> / <see cref="Resume"/>）</item>
///   <item>限速</item>
/// </list>
/// </summary>
public sealed class FileDownloader
{
    private long _downloaded;
    private long _totalBytes;
    private long _completedFiles;
    private long _failedFiles;
    private long _totalFiles;
    private long _lastSpeedTick;
    private long _lastSpeedBytes;
    private volatile int _speedLimitKb;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _resumeGate = new(true);
    private volatile bool _paused;
    private CancellationTokenSource? _innerCts;

    /// <summary>当前下载器的实时进度快照。供队列在内部下载器不广播事件时主动采样。</summary>
    public sealed record DownloadSnapshot(
        double Ratio, long DownloadedBytes, long TotalBytes, long SpeedBytesPerSecond,
        long CompletedFiles, long TotalFiles, long FailedFiles, string? CurrentFile);

    /// <summary>生成当前进度快照；尚未开始下载时返回 null。</summary>
    public DownloadSnapshot? Snapshot
    {
        get
        {
            if (State is DownloadState.Idle) return null;

            var downloaded = Interlocked.Read(ref _downloaded);
            var total = Interlocked.Read(ref _totalBytes);
            var ratio = total > 0
                ? Math.Clamp((double)downloaded / total, 0, 1)
                : _totalFiles > 0
                    ? Math.Clamp((double)Interlocked.Read(ref _completedFiles) / _totalFiles, 0, 1)
                    : 0;

            // 实时计算速度：部分流程（安装器内部）不会频繁触发 Progress 事件，
            // 因此不能依赖 _RaiseProgress 里更新的 _lastSpeedBytes
            var now = Environment.TickCount64;
            var elapsedMs = now - _lastSpeedTick;
            long speed = 0;
            if (elapsedMs >= 500)
            {
                speed = (downloaded - Interlocked.Read(ref _lastSpeedBytes)) * 1000 / Math.Max(1, elapsedMs);
                _lastSpeedTick = now;
                _lastSpeedBytes = downloaded;
            }
            else
            {
                speed = Interlocked.Read(ref _lastSpeedBytes) > 0
                    ? (downloaded - Interlocked.Read(ref _lastSpeedBytes)) * 1000 / Math.Max(1, elapsedMs)
                    : 0;
            }

            return new DownloadSnapshot(
                ratio,
                downloaded,
                total,
                speed,
                Interlocked.Read(ref _completedFiles),
                Interlocked.Read(ref _totalFiles),
                Interlocked.Read(ref _failedFiles),
                null);
        }
    }

    /// <summary>
    /// 把「当前活动下载器」登记为最近一次创建的实例。
    /// 供安装器这类内部自行创建下载器的流程，让队列能够采样到真实进度。
    /// </summary>
    public static FileDownloader? Active { get; private set; }

    /// <summary>HEAD 探测超时。不可达源要快速跳过，不能拖慢整体下载。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    public event EventHandler<DownloadProgressEventArgs>? Progress;

    public DownloadState State { get; private set; } = DownloadState.Idle;

    /// <summary>当前是否处于暂停状态。</summary>
    public bool IsPaused => _paused;

    public FileDownloader()
    {
        _speedLimitKb = LauncherConfig.Current.DownloadSpeedLimitKb;

        // 登记为当前活动下载器，便于队列采样内部创建的实例
        Active = this;
    }

    /// <summary>暂停下载。已在传输中的分块会先完成再停下。</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (State != DownloadState.Running) return;
            _paused = true;
            State = DownloadState.Paused;
            _resumeGate.Reset();
            Logger.Info("下载已暂停");
        }
    }

    /// <summary>继续下载。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (State != DownloadState.Paused) return;
            _paused = false;
            State = DownloadState.Running;
            _resumeGate.Set();
            Logger.Info("下载已继续");
        }
    }

    /// <summary>取消下载（与暂停不同，取消会放弃未完成文件）。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _paused = false;
            _resumeGate.Set();
            _innerCts?.Cancel();
        }
    }

    /// <summary>
    /// 并发下载一组文件。已存在且哈希一致的文件将被跳过。
    /// </summary>
    public async Task DownloadAllAsync(IReadOnlyList<DownloadItem> items, CancellationToken ct = default)
    {
        _totalFiles = items.Count;
        _completedFiles = 0;
        _failedFiles = 0;
        _totalBytes = items.Sum(i => Math.Max(0, i.Size));
        _downloaded = 0;
        _lastSpeedTick = Environment.TickCount64;
        _lastSpeedBytes = 0;
        _paused = false;
        _resumeGate.Set();
        State = DownloadState.Running;

        // 若有文件大小未知（例如加载器安装器 URL 不提供 size），
        // 先做 HEAD 探测补全大小，避免界面一直显示“未知”。
        // 关键：必须并行探测且带短超时。串行探测在不可达源上会逐个等 20 秒，
        // 十几个库累积就是几分钟，表现为"卡在最后阶段"。
        var unknown = items.Where(i => i.Size <= 0).ToList();
        if (unknown.Count > 0)
        {
            var probeTasks = unknown.Select(async item =>
            {
                try
                {
                    var probe = DownloadMirror.MapUrl(item.Url);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(ProbeTimeout);

                    using var req = new HttpRequestMessage(HttpMethod.Head, probe);
                    using var resp = await Http.Client.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is { } len && len > 0)
                    {
                        item.Size = len;
                        Interlocked.Add(ref _totalBytes, len);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"探测文件大小失败 {item.Url}: {ex.Message}");
                }
            }).ToArray();

            await Task.WhenAll(probeTasks).ConfigureAwait(false);
        }

        _innerCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _innerCts.Token);
        var token = linked.Token;

        try
        {
            var missing = new List<DownloadItem>();
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (await _IsValidAsync(item, token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _completedFiles);
                    Interlocked.Add(ref _downloaded, Math.Max(0, item.Size));
                }
                else
                {
                    missing.Add(item);
                }
            }

            _RaiseProgress(null);

            if (missing.Count == 0)
            {
                State = DownloadState.Completed;
                _RaiseProgress(null);
                return;
            }

            await _DownloadMissingAsync(missing, token).ConfigureAwait(false);

            _RaiseProgress(null);
            State = _failedFiles > 0 ? DownloadState.Failed : DownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            // 取消时彻底清理：删除所有未完成的 .part 临时文件，避免磁盘残留
            CleanupRemaining(items);
            State = DownloadState.Canceled;
            throw;
        }
        finally
        {
            _innerCts?.Dispose();
            _innerCts = null;
        }
    }

    private async Task _DownloadMissingAsync(List<DownloadItem> missing, CancellationToken ct)
    {
        var threads = Math.Clamp(LauncherConfig.Current.DownloadThreads, 1, 256);
        var retry = Math.Max(1, LauncherConfig.Current.DownloadRetry);
        var queue = new ConcurrentQueue<DownloadItem>(missing);
        var errors = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, Math.Min(threads, missing.Count)).Select(_ => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                // 暂停时等待
                await _WaitIfPausedAsync(ct).ConfigureAwait(false);

                Exception? lastError = null;
                for (var attempt = 0; attempt < retry; attempt++)
                {
                    try
                    {
                        await _DownloadOneAsync(item, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _completedFiles);
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Logger.Warn($"下载失败({attempt + 1}/{retry}) {item.Url}: {ex.Message}");
                        // 哈希失败时删掉损坏文件，保证下次是干净重试
                        if (ex is InvalidDataException) _TryDelete(item.TargetPath);
                        await Task.Delay(600 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                }

                if (lastError is not null)
                {
                    errors.Add(new Exception($"下载失败: {Path.GetFileName(item.TargetPath)}", lastError));
                    Interlocked.Increment(ref _failedFiles);
                    Interlocked.Increment(ref _completedFiles);
                }

                _RaiseProgress(item.TargetPath);
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (!errors.IsEmpty)
            throw new AggregateException("部分文件下载失败", errors);
    }

    private async Task _WaitIfPausedAsync(CancellationToken ct)
    {
        if (!_paused) return;
        while (!_resumeGate.IsSet && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>校验已存在的文件是否符合预期（大小 + SHA1）。</summary>
    public async Task<bool> IsValidAsync(DownloadItem item, CancellationToken ct = default)
        => await _IsValidAsync(item, ct).ConfigureAwait(false);

    private async Task<bool> _IsValidAsync(DownloadItem item, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(item.TargetPath)) return false;

            var info = new FileInfo(item.TargetPath);
            if (item.Size > 0 && info.Length != item.Size) return false;
            if (string.IsNullOrEmpty(item.Sha1)) return true;

            var actual = await _HashFileAsync(item.TargetPath, ct).ConfigureAwait(false);
            return string.Equals(actual, item.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn($"校验文件失败 {item.TargetPath}: {ex.Message}");
            return false;
        }
    }

    private static async Task<string> _HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task _DownloadOneAsync(DownloadItem item, CancellationToken ct)
    {
        var urls = new List<string>();

        var primary = DownloadMirror.MapUrl(item.Url);
        if (!urls.Contains(primary)) urls.Add(primary);

        foreach (var fallback in item.FallbackUrls)
        {
            var mapped = DownloadMirror.MapUrl(fallback);
            if (!urls.Contains(mapped)) urls.Add(mapped);
        }

        // 最后一层：镜像失效时仍可直连原始 URL
        var original = item.Url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
        if (!urls.Contains(original)) urls.Add(original);

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await _DownloadFromUrlAsync(url, item, ct).ConfigureAwait(false);

                // 校验：没有 Sha1 时用大小判断（若已知）
                if (await _IsValidAsync(item, ct).ConfigureAwait(false))
                    return;

                last = new InvalidDataException($"SHA1 校验失败: {Path.GetFileName(item.TargetPath)}");
                Logger.Warn($"{last.Message}（来源 {url}），将重试");

                // 内容不正确时必须删掉目标文件与残留的 .part，
                // 否则下一轮续传会以损坏的旧文件为基准继续追加，永远无法通过校验。
                _TryDelete(item.TargetPath);
                _TryDelete(item.TargetPath + ".part");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new IOException($"无法下载 {item.Url}");
    }

    private async Task _DownloadFromUrlAsync(string url, DownloadItem item, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(item.TargetPath);
        if (dir is not null) Directory.CreateDirectory(dir);

        // 断点续传：从 .part 文件继续。
        // 注意：只在已知期望大小时信任 .part，否则残留的残缺文件会导致无限续传。
        var partPath = item.TargetPath + ".part";
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // .part 超过期望大小说明它来自另一次不同的下载，直接丢弃
        if (existing > 0 && item.Size > 0 && existing > item.Size)
        {
            Logger.Warn($"丢弃异常大小的临时文件 {Path.GetFileName(partPath)}（{existing} > {item.Size}）");
            _TryDelete(partPath);
            existing = 0;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var resp = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var remoteLength = resp.Content.Headers.ContentLength ?? -1;

        long startOffset = 0;
        if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0)
        {
            startOffset = existing;
            Logger.Debug($"断点续传 {Path.GetFileName(item.TargetPath)} 从 {existing} 字节继续");
        }
        else if (resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // 服务器不支持 Range：从头开始
            if (existing > 0) _TryDelete(partPath);
            startOffset = 0;
        }
        else
        {
            resp.EnsureSuccessStatusCode();
        }

        await _WriteStreamAsync(resp, partPath, startOffset, remoteLength, ct).ConfigureAwait(false);

        // 原子替换 .part -> 目标
        File.Move(partPath, item.TargetPath, true);
    }

    private async Task _WriteStreamAsync(HttpResponseMessage resp, string partPath, long startOffset,
        long remoteLength, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(partPath,
            startOffset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 64 * 1024, true);

        var buffer = new byte[64 * 1024];
        long written = startOffset;
        var lastTick = Environment.TickCount64;
        long bytesInWindow = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 暂停时保持文件句柄，等恢复后继续
            await _WaitIfPausedAsync(ct).ConfigureAwait(false);

            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            Interlocked.Add(ref _downloaded, read);
            bytesInWindow += read;

            // 限速
            if (_speedLimitKb > 0)
            {
                var elapsed = Environment.TickCount64 - lastTick;
                if (elapsed > 50 && bytesInWindow > _speedLimitKb * 1024L * elapsed / 1000)
                {
                    var expected = bytesInWindow * 1000 / (_speedLimitKb * 1024L);
                    var wait = expected - elapsed;
                    if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
                    lastTick = Environment.TickCount64;
                    bytesInWindow = 0;
                }
            }
            else if (Environment.TickCount64 - lastTick > 100)
            {
                lastTick = Environment.TickCount64;
                bytesInWindow = 0;
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Close();

        if (remoteLength > 0 && written != remoteLength && startOffset == 0)
            throw new IOException($"下载大小不匹配（{written}/{remoteLength} 字节），将重试");

        // 内容为空一律视为失败：高并发下被服务端限流时，
        // 连接会建立但读不到任何数据，此时不能生成 0 字节的目标文件。
        if (written == 0)
            throw new IOException($"下载内容为空: {Path.GetFileName(partPath)}，将重试");

        // 已知期望大小时做一次完整性校验，避免残缺文件被当作成功
        if (startOffset == 0 && remoteLength <= 0 && written > 0)
        {
            // 服务端未提供 Content-Length，交给上层 SHA1 校验兜底
        }
    }

    private void _RaiseProgress(string? currentFile)
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lastSpeedTick;
        long speed = 0;
        if (elapsed >= 500)
        {
            speed = (Interlocked.Read(ref _downloaded) - _lastSpeedBytes) * 1000 / Math.Max(1, elapsed);
            _lastSpeedTick = now;
            _lastSpeedBytes = Interlocked.Read(ref _downloaded);
        }

        try
        {
            Progress?.Invoke(this, new DownloadProgressEventArgs
            {
                TotalBytes = _totalBytes,
                DownloadedBytes = Interlocked.Read(ref _downloaded),
                TotalFiles = _totalFiles,
                CompletedFiles = Interlocked.Read(ref _completedFiles),
                FailedFiles = Interlocked.Read(ref _failedFiles),
                SpeedBytesPerSecond = speed,
                CurrentFile = currentFile
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"进度回调异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 取消后清理：删除该批次所有未完成的临时文件（.part），
    /// 避免用户取消后磁盘上留下半截文件。
    /// </summary>
    private static void CleanupRemaining(IReadOnlyList<DownloadItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                var part = item.TargetPath + ".part";
                if (File.Exists(part)) File.Delete(part);

                // 目标文件若大小不符（半截写入），一并删除，保证下次是干净重下
                if (item.Size > 0 && File.Exists(item.TargetPath) &&
                    new FileInfo(item.TargetPath).Length != item.Size)
                    File.Delete(item.TargetPath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"清理临时文件失败 {item.TargetPath}: {ex.Message}");
            }
        }

        Logger.Info("已清理取消的下载临时文件");
    }

    private static void _TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>把字节数格式化为 "12.3 MB/s" 之类。</summary>
    public static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "—";

        var kb = bytesPerSecond / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB/s";
        return $"{kb / 1024:F1} MB/s";
    }
}






