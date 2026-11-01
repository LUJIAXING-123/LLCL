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
                        ConnectTimeout = TimeSpan.FromSeconds(20),
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
    {
        Exception? last = null;
        for (var i = 0; i < Math.Max(1, retry); i++)
        {
            try
            {
                using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"HTTP 请求失败({i + 1}/{retry}) {url}: {ex.Message}");
                await Task.Delay(600 * (i + 1), ct);
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
