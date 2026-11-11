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
/// - 央视频票据 5 分钟窗口 + 403 指数退避账 2/4/8 分钟（1.0.12 修法 B，YspTicketService）；其余源缓存 60 秒。
/// </summary>
public class StreamResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>A类第11项·超时分级：探活轻探收紧到 4 秒（换票/清单维持 10 秒），快失败快转向。</summary>
    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(4) };
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
        _tableCache.Clear();
    }

    /// <summary>
    /// v25 修法二 2c/2d：网络变更（换 IP）/时钟跳变时清退避账——旧账是旧 IP/旧墙钟记的，新环境不可信；
    /// 票据缓存保留验证，失败自然重新入账。UI 在网络变更/TimeChanged 钩子处调用。
    /// </summary>
    public void ClearCooldowns() => _ysp.ClearCooldowns();

    /// <summary>
    /// v25 修法二 1：票账只读查询口（账本仍是 YspTicketService 单一事实来源，UI 不建第二本）——
    /// 探测前先查：退避剩余>0 顺延不发起（不入源账、不记失败，退避期弹回不算坏）。
    /// </summary>
    public long TicketRemainingCooldownMs(Channel ch) => _ysp.RemainingCooldownMs(ch.EpgKey, DateTime.UtcNow);

    // ---- v25 修法六 1a：整表源真聚合（nmtv/gxtv） --------------------------------
    // 现状实锤（分析轮 2 漏洞 1）：ResolveNmtvAsync/ResolveGxtvAsync 每台真发整表请求，
    // 60 秒缓存按「SourceId:EpgKey」键存互不复用。v25 改：整表 JSON 按源级缓存（下表 TTL 内同源只发 1 次），
    // 同源其余台直接在缓存表内行级匹配——同源到期只探一次整表，结论自然广播全部到期台。

    private sealed class TableSnapshot { public string Body = ""; public long FetchedAtMs; }
    private readonly ConcurrentDictionary<string, TableSnapshot> _tableCache = new();
    private readonly SemaphoreSlim _tableGate = new(1, 1); // 同源整表抓取串行（并发到期只发一次）

    private static readonly TimeSpan TableTtl = TimeSpan.FromSeconds(60);

    /// <summary>整表源按源级聚合抓表：TTL 内同源只发 1 次请求，返回缓存的整表 JSON。</summary>
    private async Task<string?> FetchSourceTableAsync(string sourceId, Func<Task<string?>> fetchRaw)
    {
        var nowMs = System.Environment.TickCount64;
        if (_tableCache.TryGetValue(sourceId, out var snap) && nowMs - snap.FetchedAtMs < TableTtl.TotalMilliseconds)
            return snap.Body;
        await _tableGate.WaitAsync();
        try
        {
            nowMs = System.Environment.TickCount64;
            if (_tableCache.TryGetValue(sourceId, out snap) && nowMs - snap.FetchedAtMs < TableTtl.TotalMilliseconds)
                return snap.Body; // 双检：并发到期同源只发一次（整表聚合断言）
            var body = await fetchRaw();
            if (!string.IsNullOrEmpty(body))
                _tableCache[sourceId] = new TableSnapshot { Body = body, FetchedAtMs = nowMs };
            return body;
        }
        finally { _tableGate.Release(); }
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

    /// <summary>A 类第6项·预热落盘注入（长效源起播零探测；票据短效源不落盘）。</summary>
    public WarmupStore? WarmupDisk { get; set; }

    /// <summary>票据短效源（落了必过期，误导起播）：央视频 5 分钟票、gzstv 腾讯云分钟级票，不落盘。</summary>
    private static bool IsShortTicketSource(string sourceId) => sourceId is "yangshipin" or "gzstv";

    private async Task<string?> ResolveCachedAsync(Channel ch, Func<Channel, Task<string?>> fetch, bool force = false)
    {
        var key = CacheKey(ch);
        if (!force && _cache.TryGetValue(key, out var hit) &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hit.FetchedAt < (long)ShortCache.TotalSeconds)
            return hit.Url;
        // A 类第6项·落盘读回：长效源保鲜期内直接用落盘 URL（起播第一帧加速，零网络探测）；
        // 校验失败由重连现场重取——落盘只加速，绝不把旧清单当唯一路径
        if (!force && WarmupDisk != null && !IsShortTicketSource(ch.SourceId))
        {
            var disk = WarmupDisk.Get($"stream:{key}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (disk != null) return disk;
        }
        var url = await fetch(ch);
        if (!string.IsNullOrEmpty(url))
        {
            _cache[key] = new CachedUrl { Url = url, FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            WarmupDisk?.Put($"stream:{key}", url,
                WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
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
                _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow); // 1.0.12 修法 B：指数退避账（2/4/8 分钟）替换 30 秒固定冷却
                return null;
            }
            var (iretcode, playurl) = ParseYspResponse(body);
            if (iretcode == 0 && !string.IsNullOrEmpty(playurl))
            {
                _ysp.MarkFetched(ch.EpgKey, now, playurl);
                return playurl;
            }
            _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow); // 1.0.12 修法 B：连败退避加深（×2 步进封顶 2 小时）
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
    // v25 修法六 1a：整表抓取经 FetchSourceTableAsync 源级聚合（TTL 内同源只发 1 次），行级匹配本地完成

    private async Task<string?> ResolveNmtvAsync(Channel ch)
    {
        var json = await FetchSourceTableAsync("nmtv", FetchNmtvTableRawAsync);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return NmtvSource.LocateStreamUrl(json, ch.Name);
        }
        catch { return null; }
    }

    private async Task<string?> FetchNmtvTableRawAsync()
    {
        string body;
        using (var req = new HttpRequestMessage(HttpMethod.Get, NmtvSource.ApiBase))
        {
            req.Headers.TryAddWithoutValidation("Client-Type", NmtvSource.ClientType);
            req.Headers.TryAddWithoutValidation("Referer", NmtvSource.Referer);
            req.Headers.TryAddWithoutValidation("User-Agent", YspTicketService.ClientUserAgent);
            try
            {
                using var resp = await Http.SendAsync(req);
                body = await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }
        try
        {
            var raw = System.Convert.FromBase64String(body.Trim().Trim('"'));
            return System.Text.Encoding.UTF8.GetString(NmtvXxtea.Decrypt(raw));
        }
        catch { return null; }
    }

    // ---- 广西 gxtv：电视频道口径清单直吐（encodeM3u8），60 秒缓存 ----------------
    // v25 修法六 1a：同 nmtv，整表抓取源级聚合

    private async Task<string?> ResolveGxtvAsync(Channel ch)
    {
        var json = await FetchSourceTableAsync("gxtv", FetchGxtvTableRawAsync);
        if (string.IsNullOrEmpty(json)) return null;
        return GxtvSource.LocateStreamUrl(json, ch.Name);
    }

    private async Task<string?> FetchGxtvTableRawAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, GxtvSource.ChannelListApi);
        req.Headers.TryAddWithoutValidation("Referer", GxtvSource.Referer);
        req.Headers.TryAddWithoutValidation("User-Agent", GxtvSource.UserAgent);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pageNo"] = "1", ["pageSize"] = "1000", ["liveMethod"] = "0",
        });
        try
        {
            using var resp = await Http.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
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
    /// v23 修法 B·探活分级：拉黑账只认服务器明确拒绝（403/429），
    /// 网络级失败（超时/异常/断网/其他状态码）一律 Unreachable，不回灌整源冻结——
    /// 切后台回前台的网络冷启动不再把好源拉黑十分钟。
    /// </summary>
    public enum ProbeVerdict
    {
        Reachable = 0,    // 探通（2xx/3xx）
        Rejected = 1,     // 服务器明确拒绝（403/429），唯一可入拉黑账的失败
        Unreachable = 2,  // 网络级失败（超时/异常/断网/非 403/429 状态码）
    }

    /// <summary>
    /// 直连流轻探分级版（v23 修法 B）：同 ProbeReachableAsync 的探法（Range 0–2047 几 KB 级），
    /// 但把「探不通」的原因分开——403/429=Rejected，其余=Unreachable。
    /// </summary>
    public async Task<ProbeVerdict> ProbeUrlVerdictAsync(string url, string? referer, string? ua)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2047);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        try
        {
            using var resp = await ProbeHttp.SendAsync(req);
            int code = (int)resp.StatusCode;
            if (code < 400) return ProbeVerdict.Reachable;
            return code == 403 || code == 429 ? ProbeVerdict.Rejected : ProbeVerdict.Unreachable;
        }
        catch { return ProbeVerdict.Unreachable; }
    }

    /// <summary>直连流轻探（v1.0.8 预检握手）：只取极小头数据（Range 0–2047，几 KB 级）验证可达性，
    /// 不建立视频传输。2xx/3xx 视为可达；4xx/5xx/超时/异常视为不可达。</summary>
    public async Task<bool> ProbeReachableAsync(string url, string? referer, string? ua)
        => await ProbeUrlVerdictAsync(url, referer, ua) == ProbeVerdict.Reachable;

    /// <summary>
    /// v25 修法七：热备校验流轻探。segmentLevel=true 时「清单 + 单分片头」（拉清单后取首个分片，
    /// Range 0–2047 几 KB 级，不建视频传输不解码）；false 时仅清单级（央视频同源备路降档，同 IP 不建双会话）。
    /// 请求画像与播放链路完全同口径（referer/ua 由调用方透传播放统一头，修法七 1d）。
    /// 返回 (可达, 是否服务器明确拒绝)——拒绝供共用退避账入账（修法七 1b）。
    /// </summary>
    public async Task<(bool Ok, bool Rejected)> ProbeHotStandbyAsync(string url, string? referer, string? ua, bool segmentLevel)
    {
        string? manifest;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
            if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return (false, (int)resp.StatusCode is 403 or 429);
            manifest = await resp.Content.ReadAsStringAsync();
        }
        catch { return (false, false); } // 网络级失败：不入账（修法七 1b）
        if (!segmentLevel || string.IsNullOrEmpty(manifest)) return (true, false);
        // 清单 → 首个分片（相对路径转绝对）→ 几 KB 级分片头校验
        var segMatch = Regex.Match(manifest, "^(?!#)[^\\r\\n]+\\.(?:ts|m4s|mp4)[^\\r\\n]*", RegexOptions.Multiline);
        if (!segMatch.Success) return (true, false); // 无分片行（主清单/嵌套）：清单级即通过
        var segUrl = segMatch.Value.Trim();
        if (!segUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var baseUri = new Uri(url);
            segUrl = new Uri(baseUri, segUrl).ToString();
        }
        var verdict = await ProbeUrlVerdictAsync(segUrl, referer, ua);
        return verdict switch
        {
            ProbeVerdict.Reachable => (true, false),
            ProbeVerdict.Rejected => (false, true),
            _ => (false, false),
        };
    }

    /// <summary>
    /// v23 修法 B·取文本分级：换票/清单请求把「服务器明确拒绝（403/429）」与网络级失败分开回报，
    /// 供调用方决定是否入拉黑账（网络级失败绝不回灌）。
    /// </summary>
    public static async Task<(string? Body, bool Rejected)> HttpGetStringClassifiedAsync(string url, string? referer, string? ua)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        try
        {
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return (null, (int)resp.StatusCode is 403 or 429);
            return (await resp.Content.ReadAsStringAsync(), false);
        }
        catch { return (null, false); }
    }

    private static async Task<string?> HttpGetStringAsync(string url, string? referer, string? ua)
    {
        var (body, _) = await HttpGetStringClassifiedAsync(url, referer, ua);
        return body;
    }

    private static string RandomToken()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var r = new byte[10];
        Random.Shared.NextBytes(r);
        return new string(r.Select(b => chars[b % chars.Length]).ToArray());
    }
}
