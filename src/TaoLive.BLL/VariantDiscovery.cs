using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace TaoLive.BLL;

// ============ 道映 v1.0.10【六】多档变体发现（老板 13:46 拍板：机制照建，数据到位即生效）============
// 源即使当前无多档变体数据（Height/Bandwidth），发现机制也照建：
// - 解析出的流若是 master playlist（#EXT-X-STREAM-INF 多码率变体），交 FastStartPlanner
//   「最快档先亮、亮后无感升到最高档」；单档流/无变体/拉取失败 → 空表，按单档如实播放；
// - 变体 URI 统一按基础流地址补全为绝对地址（master 里的相对路径直接可用）；
// - 抓取失败只落日志（BllLog），绝不影响起播——变体发现是纯增益路径，不挡播放；
// - 抓取器可注入：测试桩/后续接缓存复用都走同一注入点，不硬编码网络行为。

/// <summary>多档变体发现器（纯逻辑 + 可注入抓取，不硬编码网络）。</summary>
public static class VariantDiscovery
{
    /// <summary>抓取器注入点：给 URL 返回 master playlist 文本；null 时走内置 HttpClient。</summary>
    public static Func<string, Task<string?>>? Fetcher { get; set; }

    /// <summary>
    /// 带请求头的抓取器注入点：给 URL 与请求头返回 master playlist 文本；设置时优先于 Fetcher。
    /// v1.0.11 fix21（真机日志）：央视频系 CDN 对无 Referer/UA 的清单请求回 403，
    /// 发现请求须与主播放同一套请求头（频道 Referer + UA），否则多档永远发现不了。
    /// </summary>
    public static Func<string, IReadOnlyDictionary<string, string>, Task<string?>>? HeaderedFetcher { get; set; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) }; // 变体发现从快：慢源不拖起播

    /// <summary>
    /// 对解析出的流地址尝试发现多档变体：
    /// - master playlist → 返回多档变体表（URI 已补全为绝对地址，按带宽升序）；
    /// - 单档/媒体 playlist/拉取失败/非 m3u8 → 空表（调用方按单档如实播放，不硬造档位）。
    /// </summary>
    public static async Task<IReadOnlyList<QualityVariant>> DiscoverAsync(
        string url, string? referer = null, string? userAgent = null,
        IEnumerable<(string Key, string Value)>? extraHeaders = null)
    {
        if (string.IsNullOrEmpty(url)) return Array.Empty<QualityVariant>();
        try
        {
            var headers = BuildHeaders(referer, userAgent, extraHeaders);
            string? body;
            // v1.0.13 缓存分层（老板拍板）：内存缓存（90 秒）→ 档位目录落盘（24 小时）→ 现场抓取，
            // 逐层回落、逐层回填——换台零网络等待，重启电视也不用重抓；播放清单永不缓存（善变）
            if (PlaylistPrefetch.TryGet(url, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var cached))
            {
                body = cached;
            }
            else if (VariantTableStore.Shared is { } disk && disk.TryGet(url, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var fromDisk))
            {
                body = fromDisk;
                PlaylistPrefetch.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // 回填内存层，本轮换台零等待
            }
            else if (HeaderedFetcher is { } hfetch) { body = await hfetch(url, headers).ConfigureAwait(false); if (!string.IsNullOrEmpty(body)) { PlaylistPrefetch.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); VariantTableStore.Shared?.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); } }
            else if (Fetcher is { } fetch) { body = await fetch(url).ConfigureAwait(false); if (!string.IsNullOrEmpty(body)) { PlaylistPrefetch.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); VariantTableStore.Shared?.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); } }
            else { body = await HttpGetStringAsync(url, headers).ConfigureAwait(false); if (!string.IsNullOrEmpty(body)) { PlaylistPrefetch.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); VariantTableStore.Shared?.Put(url, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); } }
            if (string.IsNullOrEmpty(body)) return Array.Empty<QualityVariant>();
            if (!body.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<QualityVariant>(); // 媒体 playlist（单档流）：如实单档
            var variants = MasterPlaylistParser.Parse(body);
            if (variants.Count <= 1) return Array.Empty<QualityVariant>();
            return variants
                .Select(v => new QualityVariant
                {
                    Uri = ResolveUri(url, v.Uri),
                    BandwidthBps = v.BandwidthBps,
                    HeightPx = v.HeightPx,
                    Label = v.Label,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            // 变体发现失败绝不挡起播：落日志后按单档播（BllLog 通道未接时格式化即弃，同样无副作用）
            // v1.0.13 fix27（真机日志）：同源失败限流——同一地址 5 分钟内只落一次，不再刷「崩溃日志」面板
            var key = BllLog.MaskUrl(url);
            if (_failLogCool.TryGetValue(key, out var until) && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < until)
                return Array.Empty<QualityVariant>();
            _failLogCool[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + FailLogCooldownMs;
            BllLog.Warn("VariantDiscovery", $"多档变体发现失败，按单档播放：url={key}", ex);
            return Array.Empty<QualityVariant>();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _failLogCool = new();
    private const long FailLogCooldownMs = 300_000; // 同源失败日志冷却 5 分钟

    /// <summary>
    /// 按需组装发现请求头：只放非空值，空频道不带头；
    /// v1.0.13 fix27（真机日志）：追加主播放同源头（频道解析出的完整选项，含令牌/画质档键值），
    /// 同键以后到者覆盖——发现请求与主播放逐字一致，央视频系 CDN 不再凭头缺口 403/417。
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildHeaders(string? referer, string? userAgent,
        IEnumerable<(string Key, string Value)>? extraHeaders)
    {
        var dict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(referer)) dict["Referer"] = referer;
        if (!string.IsNullOrEmpty(userAgent)) dict["User-Agent"] = userAgent;
        if (extraHeaders is { } extras)
        {
            foreach (var (key, value) in extras)
            {
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value)) dict[key] = value;
            }
        }
        return dict;
    }

    private static async Task<string?> HttpGetStringAsync(string url, IReadOnlyDictionary<string, string> headers)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in headers) req.Headers.TryAddWithoutValidation(key, value);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        // fix29 恶意加固：发现请求同样限量读应答体（2MB 封顶，超限整包丢弃）
        return await StreamResolver.ReadCappedAsync(resp.Content);
    }

    /// <summary>相对变体 URI 按基础流地址补全为绝对地址（已绝对则原样）。</summary>
    public static string ResolveUri(string baseUrl, string uri)
    {
        if (string.IsNullOrEmpty(uri)) return uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var abs) &&
            (abs.Scheme == "http" || abs.Scheme == "https")) return abs.ToString();
        try { return new Uri(new Uri(baseUrl), uri).ToString(); }
        catch (Exception ex) { BllLog.Warn("VariantDiscovery", $"相对变体地址补全失败 url={BllLog.MaskUrl(baseUrl)}——按原样返回", ex); return uri; }
    }
}
