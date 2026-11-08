using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>取流结果：换票后的可播地址 + VLC 透传头（Referer/UA）。</summary>
public class ResolvedStream
{
    public Channel Channel { get; set; } = new();
    public string Url { get; set; } = "";
    public List<(string Key, string Value)> Options { get; set; } = new();
}

/// <summary>
/// 取流器（播放链路真闸口）：
/// - 取票型源（央视频/浙江/贵州/上海/内蒙古）在此现场换票成可播 m3u8，入口 URL 不直接给播放器；
/// - 直连型（西安）与自签型（江苏，播放时重签 180s 防盗链）在此统一收口；
/// - 换票结果进短效缓存：切台/预连接命中缓存即起播（预连接缓存窗口见各源标注）；
/// - 央视频票据 5 分钟窗口 + 403 冷却 30 秒（YspTicketService）；其余源缓存 60 秒。
/// </summary>
public class StreamResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex M3u8Regex = new(
        "https?://[^\"\\\\\\s]+\\.m3u8[^\"\\\\\\s]*", RegexOptions.Compiled);

    private readonly YspTicketService _ysp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SourceFailover _failover = new(); // 自愈1：多候选源备援状态

    /// <summary>备援候选解析（自愈1）：主源失败后由 UI/预检注入（用户自有 m3u 按台名匹配等）。</summary>
    public Func<Channel, Task<string?>>? UserFallback { get; set; }

    /// <summary>自愈1：多候选源备援状态（预检与取流共用，恢复自动回主源）。</summary>
    public SourceFailover Failover => _failover;

    /// <summary>时钟守卫（自愈4）：票据窗口用修正后的时间计算，容忍设备时间偏差。</summary>
    public ClockGuard Clock { get; } = new();

    /// <summary>自愈8：崩溃后/内存告急时清空取流缓存（可疑状态重置，静默）。</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _ysp.ClearAll();
    }

    private sealed class CachedUrl { public string Url = ""; public long FetchedAt; }
    private readonly ConcurrentDictionary<string, CachedUrl> _cache = new();
    private static readonly TimeSpan ShortCache = TimeSpan.FromSeconds(60);

    public async Task<ResolvedStream?> ResolvePlayUrlAsync(Channel ch, bool force = false)
    {
        string url = ch.SourceId switch
        {
            "yangshipin" => await ResolveYspAsync(ch, force),
            "cztv" => await ResolveCachedAsync(ch, ResolveCztvAsync, force),
            "gzstv" => await ResolveCachedAsync(ch, ResolveGzstvAsync, force),
            "kankan" => await ResolveCachedAsync(ch, ResolveKankanAsync, force),
            "nmtv" => await ResolveCachedAsync(ch, ResolveNmtvAsync, force),
            "gxtv" => await ResolveCachedAsync(ch, ResolveGxtvAsync, force),
            "jstv" => await ResolveCachedAsync(ch, ch2 => Task.FromResult(ResolveJstv(ch2)), force),
            _ => ch.StreamUrl,
        };
        if (string.IsNullOrEmpty(url)) return null;

        var opts = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", ch.Referer));
        var ua = ch.SourceId == "kankan" ? KankanFetchUserAgent : ch.UserAgent;
        if (!string.IsNullOrEmpty(ua)) opts.Add(("http-user-agent", ua));
        return new ResolvedStream { Channel = ch, Url = url, Options = opts };
    }

    /// <summary>
    /// 自愈1：带备援的取流。主源失败 → 自动切用户自有源等备援候选（静默）；
    /// 备援连续成功 SourceFailover.RecoverToPrimaryAfter 轮自动回主源；全候选失败返回 null（UI 明确提示）。
    /// </summary>
    public async Task<ResolvedStream?> ResolveWithFailoverAsync(Channel ch, bool force = false)
    {
        var key = ch.EpgKey;
        if (_failover.CurrentIndex(key) == 0)
        {
            var primary = await ResolvePlayUrlAsync(ch, force);
            if (primary != null) return primary;
            _failover.NextCandidateOnFailure(key, 2); // 主源 + 用户源
        }
        if (UserFallback == null) return null;
        try
        {
            var alt = await UserFallback(ch);
            if (string.IsNullOrEmpty(alt))
            {
                _failover.NextCandidateOnFailure(key, 2); // 备援也失败
                return null;
            }
            _failover.OnSuccess(key);
            var opts = new List<(string Key, string Value)>();
            if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", ch.Referer));
            if (!string.IsNullOrEmpty(ch.UserAgent)) opts.Add(("http-user-agent", ch.UserAgent));
            return new ResolvedStream { Channel = ch, Url = alt, Options = opts };
        }
        catch { return null; }
    }

    /// <summary>上海取票/拉流统一 UA（JWT 绑 ua_hash，两侧必须一致）。</summary>
    public const string KankanFetchUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    // ---- 通用：短效缓存包装（60 秒；预连接/快速切台命中即起播） ------------------

    private async Task<string?> ResolveCachedAsync(Channel ch, Func<Channel, Task<string?>> fetch, bool force = false)
    {
        var key = CacheKey(ch);
        if (!force && _cache.TryGetValue(key, out var hit) &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hit.FetchedAt < (long)ShortCache.TotalSeconds)
            return hit.Url;
        var url = await fetch(ch);
        if (!string.IsNullOrEmpty(url))
            _cache[key] = new CachedUrl { Url = url, FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        return url;
    }

    private static string CacheKey(Channel ch) => $"{ch.SourceId}:{ch.EpgKey}";

    // ---- 央视频：cKey 取票（iretcode=0 → playurl；5 分钟缓存 + 403 冷却） ---------

    private async Task<string?> ResolveYspAsync(Channel ch, bool force = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!force && _ysp.TryGet(ch.EpgKey, now, out var cached)) return cached;

        await _gate.WaitAsync();
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!force && _ysp.TryGet(ch.EpgKey, now, out cached)) return cached;
            if (_ysp.InCooldown(ch.EpgKey, DateTime.UtcNow)) return null;

            var (cnlid, livepid, defn) = ParseYspParams(ch.StreamUrl);
            if (cnlid is null || livepid is null || defn is null) return null;

            var ticketUrl = _ysp.BuildTicketUrl(cnlid, livepid, defn, now);
            var body = await HttpGetStringAsync(ticketUrl, null, YspTicketService.ClientUserAgent);
            if (body is null)
            {
                _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow.AddSeconds(YspTicketService.Cooldown.TotalSeconds));
                return null;
            }
            var (iretcode, playurl) = ParseYspResponse(body);
            if (iretcode == 0 && !string.IsNullOrEmpty(playurl))
            {
                _ysp.MarkFetched(ch.EpgKey, now, playurl);
                return playurl;
            }
            _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow.AddSeconds(YspTicketService.Cooldown.TotalSeconds));
            return null;
        }
        finally { _gate.Release(); }
    }

    private static (string? Cnlid, string? Livepid, string? Defn) ParseYspParams(string entryUrl)
    {
        string? Get(string key)
        {
            var m = Regex.Match(entryUrl, $"[?&]{key}=([^&]+)");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }
        return (Get("cnlid"), Get("livepid"), Get("defn"));
    }

    private static (int Iretcode, string? PlayUrl) ParseYspResponse(string body)
    {
        var code = Regex.Match(body, "\"iretcode\"\\s*:\\s*(\\d+)");
        var url = Regex.Match(body, "\"playurl\"\\s*:\\s*\"([^\"]+)\"");
        if (!code.Success) return (-1, null);
        var raw = url.Success ? url.Groups[1].Value.Replace("\\/", "/") : null;
        return (int.Parse(code.Groups[1].Value), raw);
    }

    // ---- 浙江 cztv：playInfo JSON → 取 1080P 流 → auth_key 签名 -------------------

    private async Task<string?> ResolveCztvAsync(Channel ch)
    {
        var body = await HttpGetStringAsync(ch.StreamUrl, ch.Referer, YspTicketService.ClientUserAgent);
        if (body is null) return null;
        var urls = M3u8Regex.Matches(body).Select(m => m.Value.Replace("\\/", "/")).Distinct().ToList();
        if (urls.Count == 0) return null;
        var pick = urls.FirstOrDefault(u => u.Contains("1080")) ?? urls[0];
        return CztvSource.SignPlayUrl(pick, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RandomToken());
    }

    // ---- 贵州 gzstv：取票 API（Referer 防盗链）→ txSecret/txTime m3u8 -------------

    private async Task<string?> ResolveGzstvAsync(Channel ch)
    {
        var body = await HttpGetStringAsync(ch.StreamUrl, ch.Referer, YspTicketService.ClientUserAgent);
        if (body is null) return null;
        var m = M3u8Regex.Match(body);
        return m.Success ? m.Value.Replace("\\/", "/") : null;
    }

    // ---- 上海 kankan：MD5 排序签名（照 sh_v2 实测实现；取流与取票同 UA） -----------

    private async Task<string?> ResolveKankanAsync(Channel ch)
    {
        var channelId = Regex.Match(ch.StreamUrl, "channel_id=([^&]+)").Groups[1].Value;
        if (string.IsNullOrEmpty(channelId)) return null;

        var nonce = RandomToken();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var pars = new SortedDictionary<string, string>
        {
            ["platform"] = "mobile", ["version"] = "2.46.0", ["nonce"] = nonce,
            ["timestamp"] = ts, ["Api-Version"] = "v1", ["channelId"] = channelId,
        };
        var qs = string.Join("&", pars.Select(kv => $"{kv.Key}={kv.Value}"));
        var sign = KankanSource.Sign(
            pars.ToDictionary(kv => kv.Key, kv => kv.Value),
            long.Parse(ts), nonce);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://kapi.kankanews.com/content/pc/tv/channel/detail?{qs}");
        req.Headers.TryAddWithoutValidation("sign", sign);
        req.Headers.TryAddWithoutValidation("User-Agent", KankanFetchUserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://live.kankanews.com/");
        req.Headers.TryAddWithoutValidation("M-Uuid", "taolive-tv-01");
        string body;
        try
        {
            using var resp = await Http.SendAsync(req);
            body = await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
        var m = M3u8Regex.Match(body.Replace("\\/", "/"));
        return m.Success ? m.Value : null;
    }

    // ---- 内蒙古 nmtv：整表 XXTEA 解密 → 按 title 匹配 streamUrl --------------------

    private async Task<string?> ResolveNmtvAsync(Channel ch)
    {
        string body;
        using (var req = new HttpRequestMessage(HttpMethod.Get, NmtvSource.ApiBase))
        {
            req.Headers.TryAddWithoutValidation("Client-Type", NmtvSource.ClientType);
            req.Headers.TryAddWithoutValidation("Referer", NmtvSource.Referer);
            req.Headers.TryAddWithoutValidation("User-Agent", YspTicketService.ClientUserAgent);
            string nbody;
            try
            {
                using var resp = await Http.SendAsync(req);
                nbody = await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
            body = nbody;
        }
        try
        {
            var raw = System.Convert.FromBase64String(body.Trim().Trim('"'));
            var json = System.Text.Encoding.UTF8.GetString(NmtvXxtea.Decrypt(raw));
            // 行级匹配（2026-11-08 修复「包头」取不到流：整表行内 data.streamUrl 位于 title 之前，
            // 旧「title 向右找首个 m3u8」全行顺位偏差；NmtvSource.LocateStreamUrl 间隙法主口径+兜底）
            return NmtvSource.LocateStreamUrl(json, ch.Name);
        }
        catch { return null; }
    }

    // ---- 广西 gxtv：电视频道口径清单直吐（encodeM3u8），60 秒缓存 ----------------

    private async Task<string?> ResolveGxtvAsync(Channel ch)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, GxtvSource.ChannelListApi);
        req.Headers.TryAddWithoutValidation("Referer", GxtvSource.Referer);
        req.Headers.TryAddWithoutValidation("User-Agent", GxtvSource.UserAgent);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pageNo"] = "1", ["pageSize"] = "1000", ["liveMethod"] = "0",
        });
        string? json;
        try
        {
            using var resp = await Http.SendAsync(req);
            json = await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
        return GxtvSource.LocateStreamUrl(json ?? "", ch.Name);
    }

    // ---- 江苏 jstv：txSecret 短效 180s，播放时重签 --------------------------------

    private static string? ResolveJstv(Channel ch)
    {
        var chan = new Uri(ch.StreamUrl).AbsolutePath.TrimStart('/').Replace("pro.m3u8", "");
        return string.IsNullOrEmpty(chan)
            ? null
            : JstvSource.PlayUrl(chan, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 180); // txTime=hex(now+180s)
    }

    // ---- 公共 -----------------------------------------------------------------------

    /// <summary>
    /// 直连流轻探（v1.0.8 预检握手）：只取极小头数据（Range 0–2047，几 KB 级）验证可达性，
    /// 不建立视频传输。2xx/3xx 视为可达；4xx/5xx/超时/异常视为不可达。
    /// </summary>
    public async Task<bool> ProbeReachableAsync(string url, string? referer, string? ua)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2047);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        try
        {
            using var resp = await Http.SendAsync(req);
            return (int)resp.StatusCode < 400;
        }
        catch { return false; }
    }

    private static async Task<string?> HttpGetStringAsync(string url, string? referer, string? ua)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        try
        {
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }

    private static string RandomToken()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var r = new byte[10];
        Random.Shared.NextBytes(r);
        return new string(r.Select(b => chars[b % chars.Length]).ToArray());
    }
}
