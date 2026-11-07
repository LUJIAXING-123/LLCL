using System.Net;
using System.Net.Http.Headers;

namespace LLCL.Core.Net;

/// <summary>
/// HTTP 客户端工厂。统一超时、UA、重试与代理设置。
/// </summary>
public static class Http
{
    public const string UserAgent =
        "LLCL/3.0 (LL Craft Launcher; +https://github.com/LLCL)";

    private static HttpClient? _client;
    private static string? _proxyUrl;
    private static readonly object _lock = new();

    public static HttpClient Client
    {
        get
        {
            lock (_lock)
            {
                if (_client is null)
                {
                    var handler = new SocketsHttpHandler
                    {
                        AllowAutoRedirect = true,
                        AutomaticDecompression = DecompressionMethods.All,
                        // 连接超时不宜过长：不可达主机应尽快失败并回退备用源，
                        // 否则会在慢源上长时间等待，表现为下载卡住
                        ConnectTimeout = TimeSpan.FromSeconds(8),
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                        MaxConnectionsPerServer = 128
                    };

                    _client = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(180)
                    };
                    _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                    _client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
                    if (_proxyUrl is not null)
                        _client.DefaultRequestHeaders.Accept.Clear();
                }

                return _client;
            }
        }
    }

    /// <summary>设置下载代理，null 表示直连。</summary>
    public static void SetProxy(string? url)
    {
        lock (_lock)
        {
            if (_proxyUrl == url) return;
            _proxyUrl = url;
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>带重试的 GET，返回字符串。失败抛出最后一次异常。</summary>
    public static async Task<string> GetStringAsync(string url, int retry = 3, CancellationToken ct = default)
        => await GetStringAsync(url, retry, null, ct).ConfigureAwait(false);

    /// <summary>
    /// 带重试与单次超时的 GET。用于元数据查询这类「快速失败」场景，
    /// 避免一个不可达的来源把整体拖到全局 180 秒超时。
    /// </summary>
    public static async Task<string> GetStringAsync(string url, int retry,
        TimeSpan? perRequestTimeout, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var i = 0; i < Math.Max(1, retry); i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (perRequestTimeout is { } timeout) cts.CancelAfter(timeout);

            try
            {
                using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException && !ct.IsCancellationRequested
                                        && perRequestTimeout is not null)
            {
                // 单次超时：直接记录为失败，让调用方切换下一个来源，不再重试浪费时间
                last = new TimeoutException($"请求超时（>{perRequestTimeout.Value.TotalSeconds:F0}s）: {url}", ex);
                Logger.Warn($"HTTP 超时 {url}");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"HTTP 请求失败({i + 1}/{retry}) {url}: {ex.Message}");
                await Task.Delay(600 * (i + 1), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("HTTP 请求失败");
    }

    /// <summary>带重试的 GET，返回字节数组。</summary>
    public static async Task<byte[]> GetBytesAsync(string url, int retry = 3, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var i = 0; i < Math.Max(1, retry); i++)
        {
            try
            {
                using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(600 * (i + 1), ct);
            }
        }

        throw last ?? new InvalidOperationException("HTTP 请求失败");
    }
}
