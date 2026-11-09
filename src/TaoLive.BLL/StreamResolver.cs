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
    // v1.0.16 方案 F/G/H（冻结稿）：共享客户端装配一次——
    // H 连接复用参数（池寿命/空闲超时/并发上限/连接超时，取流应答极小，池化命中即省整个握手）；
    // G ConnectCallback 指定实测最优 IP（DoH 优选 + 延迟记账，TLS 仍按原域名；任何异常回落系统解析）；
    // F DoH 走 dns.alidns.com，失败静默回落系统 DNS，绝不阻塞取流。
    private static readonly HttpClient Http = BuildHttpClient();

    private static HttpClient BuildHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(60),      // H：池寿命（长连换票地址会过期，60s 翻新）
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),   // H：空闲回收
            MaxConnectionsPerServer = 16,                             // H：并发上限（防刷单域）
            ConnectTimeout = TimeSpan.FromSeconds(5),                 // H：连接超时（独立于总超时）
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        try { new DnsRouteHandler().Bind(handler); }                  // G：IP 择路（装配失败回落系统解析）
        catch (Exception ex) { BllLog.Warn("StreamResolver", $"IP 择路处理器装配失败——回落系统解析", ex); }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static readonly Regex M3u8Regex = new(
        "https?://[^\"\\\\\\s]+\\.m3u8[^\"\\\\\\s]*", RegexOptions.Compiled);

    private readonly YspTicketService _ysp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SourceFailover _failover = new(); // 自愈1：多候选源备援状态
    // v1.0.20 A 源级死判：403/取票失败整源拉黑观察期（约 10 分钟，半开恢复）——死源快认输，不空转
    public SourceBlackoutLedger Blackout { get; } = new();
    // v1.0.20 G1 请求纪律：同一取票口两次请求最小间隔，压到频控线以下
    private readonly HostPacer _pacer = new();

    /// <summary>G1/G3 摸底观测：最近一次央视频取票口（bkliveinfo）的定性记录（403 响应原文/DNS 结论，交付前回灌）。</summary>
    public string LastYspDiagnosis { get; set; } = "";

    /// <summary>备援候选解析（自愈1）：主源失败后由 UI/预检注入（用户自有 m3u 按台名匹配等）。</summary>
    public Func<Channel, Task<string?>>? UserFallback { get; set; }

    /// <summary>自愈1：多候选源备援状态（预检与取流共用，恢复自动回主源）。</summary>
    public SourceFailover Failover => _failover;

    /// <summary>时钟守卫（自愈4）：票据窗口用修正后的时间计算，容忍设备时间偏差。</summary>
    public ClockGuard Clock { get; } = new();

    /// <summary>自愈8：崩溃后/内存告急时清空取流缓存（可疑状态重置，静默）。v1.0.20：拉黑账本随自清理一并清空。</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _ysp.ClearAll();
        Blackout.ClearAll();
        StreamRouteLedger.ClearAll();
        _pacer.ClearAll();
    }

    private sealed class CachedUrl { public string Url = ""; public long FetchedAt; }
    private readonly ConcurrentDictionary<string, CachedUrl> _cache = new();
    private static readonly TimeSpan ShortCache = TimeSpan.FromSeconds(60);

    /// <summary>
    /// fix23（老板思路）：失败链路立即作废——单频道链路缓存清除，任何路径（列表/切台/重连）
    /// 下次都重新解析拿新鲜链路；检测成功自动加回由预检循环负责。作废不影响其他频道。
    /// </summary>
    public void Invalidate(Channel ch)
    {
        _cache.Remove(CacheKey(ch), out _);
        if (ch.SourceId == "yangshipin") _ysp.InvalidateChannel(ch.EpgKey);
    }

    public async Task<ResolvedStream?> ResolvePlayUrlAsync(Channel ch, bool force = false)
    {
        string? url = ch.SourceId switch // 各分支解析器契约返回可空，如实标注；102 行 IsNullOrEmpty 守卫收窄后使用
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
        if (string.IsNullOrEmpty(url) || !StreamUrlGuard.IsValid(url)) return null; // fix29 恶意加固：取流地址必须为合法绝对 http(s) 地址

        var opts = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", SanitizeOptionValue(ch.Referer)));
        var ua = ch.SourceId == "kankan" ? KankanFetchUserAgent : ch.UserAgent;
        if (!string.IsNullOrEmpty(ua)) opts.Add(("http-user-agent", SanitizeOptionValue(ua)));
        return new ResolvedStream { Channel = ch, Url = url, Options = opts };
    }

    /// <summary>
    /// v1.0.20 ⑧选项注入面收口（纯函数）：Referer/UA 一律剥离 CR/LF——
    /// 用户自有源可携带换行，原样进 VLC 选项拼接即注入面；剥离对合法值零影响（内置源均无换行）。
    /// </summary>
    public static string SanitizeOptionValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        if (!value.Contains('\r') && !value.Contains('\n')) return value;
        return value.Replace("\r", " ").Replace("\n", " ").Trim();
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
            // fix29 恶意加固：备援地址同样必须为合法绝对 http(s) 地址，无效按备援失败走下一候选
            if (string.IsNullOrEmpty(alt) || !StreamUrlGuard.IsValid(alt))
            {
                _failover.NextCandidateOnFailure(key, 2); // 备援也失败
                return null;
            }
            _failover.OnSuccess(key);
            var opts = new List<(string Key, string Value)>();
            if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", SanitizeOptionValue(ch.Referer)));
            if (!string.IsNullOrEmpty(ch.UserAgent)) opts.Add(("http-user-agent", SanitizeOptionValue(ch.UserAgent)));
            return new ResolvedStream { Channel = ch, Url = alt, Options = opts };
        }
        catch (Exception ex) { BllLog.Warn("StreamResolver", $"备援取流异常（频道 {ch.EpgKey}）——按备援失败处理", ex); return null; }
    }

    /// <summary>上海取票/拉流统一 UA（JWT 绑 ua_hash，两侧必须一致）。</summary>
    public const string KankanFetchUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    /// <summary>v1.0.20 A/D：央视频取票口主机（拉黑账本与 G1 节拍按此口记账）。</summary>
    public const string YspTicketHost = "bkliveinfo.ysp.cctv.cn";

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

    /// <summary>
    /// fix31 ②（冻结稿 v2）：该台央视频票据是否在 403 冷却期内——只读既有冷却标记（YspTicketService），
    /// 不新增第二套冷却账本。UI 据此跳过多档发现请求：票据侧正被服务器拒绝，同源 CDN 的发现请求
    /// 只会再吃一次拒绝，白跑还拖慢起播；冷却期满本查询自然回到 false，发现请求照常恢复。
    /// </summary>
    public bool YspInCooldown(Channel ch) => _ysp.InCooldown(ch.EpgKey, DateTime.UtcNow);

    // ---- 央视频：cKey 取票（iretcode=0 → playurl；5 分钟缓存 + 403 冷却） ---------

    private async Task<string?> ResolveYspAsync(Channel ch, bool force = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!force && _ysp.TryGet(ch.EpgKey, now, out var cached)) return cached;

        // v1.0.20 A 源级死判：整源观察期内直接跳过，不重试不热切（死源快认输，绝不撞到撞满为止）
        if (Blackout.Check("yangshipin") == BlackoutState.BlackedOut) return null;
        // v1.0.20 G1 请求纪律：取票口节拍未到不发文（同一口两次请求最小间隔，压到频控线以下）
        if (!_pacer.TryAcquire(YspTicketHost)) return null;

        await _gate.WaitAsync();
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!force && _ysp.TryGet(ch.EpgKey, now, out cached)) return cached;
            if (_ysp.InCooldown(ch.EpgKey, DateTime.UtcNow)) return null;
            if (Blackout.Check("yangshipin") == BlackoutState.BlackedOut) return null;

            var (cnlid, livepid, defn) = ParseYspParams(ch.StreamUrl);
            if (cnlid is null || livepid is null || defn is null) return null;

            var ticketUrl = _ysp.BuildTicketUrl(cnlid, livepid, defn, now);
            // fix28（老板拍板「连接优先」）：网络级失败（没联网/断流）不进 30 秒冷却——
            // 冷却只防服务器明确拒绝后的重取风暴，网没通就锁 30 秒会把开机首连锁死
            var (body, transportError) = await HttpGetStringDetailedAsync(ticketUrl, null, YspTicketService.ClientUserAgent);
            if (transportError)
            {
                // v1.0.20 D 取票口失败快断：不持旧票撞流（无有效票据，持旧票必 403）+ 取票失败并入 A 账本
                _ysp.InvalidateChannel(ch.EpgKey);
                Blackout.OnFailure("yangshipin", ch.EpgKey, SourceFailureKind.Transport);
                BllLog.Warn("StreamResolver", $"取票口网络级失败（频道 {ch.EpgKey}，{YspTicketHost}）——旧票作废不撞流，失败并入源级观察账");
                return null;
            }
            if (body is null)
            {
                // 服务器明确拒绝（403/429）：既走既有 30 秒冷却，也回灌 A 源级死判（单台 2 次/跨台 3 台整源拉黑）
                _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow.AddSeconds(YspTicketService.Cooldown.TotalSeconds));
                Blackout.OnFailure("yangshipin", ch.EpgKey, SourceFailureKind.Rejected);
                return null;
            }
            var (iretcode, playurl) = ParseYspResponse(body);
            if (iretcode == 0 && !string.IsNullOrEmpty(playurl))
            {
                _ysp.MarkFetched(ch.EpgKey, now, playurl);
                Blackout.OnSuccess("yangshipin"); // v1.0.20 A：成功清账全开
                StreamRouteLedger.NoteStreamSuccess(YspTicketHost); // v1.0.20 E：成功清换路账
                return playurl;
            }
            _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow.AddSeconds(YspTicketService.Cooldown.TotalSeconds));
            Blackout.OnFailure("yangshipin", ch.EpgKey, SourceFailureKind.Rejected);
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
            body = await ReadCappedAsync(resp.Content) ?? "";
        }
        catch (Exception ex) { BllLog.Warn("StreamResolver", $"上海取流请求异常（频道 {ch.Name}）——返回无流", ex); return null; }
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
                nbody = await ReadCappedAsync(resp.Content) ?? "";
            }
            catch (Exception ex) { BllLog.Warn("StreamResolver", $"内蒙古取流请求异常（频道 {ch.Name}）——返回无流", ex); return null; }
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
        catch (Exception ex) { BllLog.Warn("StreamResolver", $"内蒙古整表解密/匹配失败（频道 {ch.Name}）——返回无流", ex); return null; }
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
            json = await ReadCappedAsync(resp.Content) ?? "";
        }
        catch (Exception ex) { BllLog.Warn("StreamResolver", $"广西取流请求异常（频道 {ch.Name}）——返回无流", ex); return null; }
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
        => (await ProbeReachableDetailedAsync(url, referer, ua).ConfigureAwait(false)).Ok;

    /// <summary>
    /// 轻探细分版（v1.0.16 方案 B）：除可达结论外区分「被拒」（403/429，交回现行冷却）——
    /// 竞速候选被拒不再空转重试；其余网络级失败照旧只判不可达。
    /// </summary>
    public async Task<(bool Ok, bool Rejected)> ProbeReachableDetailedAsync(string url, string? referer, string? ua)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2047);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        try
        {
            using var resp = await Http.SendAsync(req);
            int code = (int)resp.StatusCode;
            return (code < 400, code == 403 || code == 429);
        }
        catch (Exception ex) { BllLog.Warn("StreamResolver", $"探活请求异常 url={BllLog.MaskUrl(url)}——判不可达", ex); return (false, false); }
    }

    /// <summary>
    /// 取播放清单文本（v1.0.13 预取到底）：给预热循环顺路抓清单入缓存用——
    /// 换台时变体发现/起流直接读缓存，不再现场等网络。失败返回 null，不抛异常。
    /// </summary>
    public static async Task<string?> FetchPlaylistTextAsync(string url, string? referer, string? ua)
        => await HttpGetStringAsync(url, referer, ua).ConfigureAwait(false);

    private static async Task<string?> HttpGetStringAsync(string url, string? referer, string? ua)
        => (await HttpGetStringDetailedAsync(url, referer, ua)).Body;

    /// <summary>
    /// fix28（老板拍板「连接优先」）：区分两种失败——网络级异常（没联网/断流/超时）与服务器明确拒绝。
    /// 前者不进冷却（冷却只防服务器拒绝后的重取风暴），首连快速短重试才走得通。
    /// v1.0.20 E：网络级失败另走换路账本（同域 2 次触发 RefreshRoute 换 IP/线路）；成功清账。
    /// 403 类服务器明确拒绝不经换路账本（换路救不了源侧拒绝，由 A 源级死判记账）。
    /// </summary>
    private static async Task<(string? Body, bool TransportError)> HttpGetStringDetailedAsync(string url, string? referer, string? ua)
    {
        string host;
        try { host = new Uri(url).Host; } catch { host = ""; }
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        try
        {
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (null, false); // 服务器明确拒绝：照旧进冷却，不进换路账本
            var capped = await ReadCappedAsync(resp.Content);
            if (capped is null) { StreamRouteLedger.NoteStreamFailure(host); return (null, true); } // 超封顶：按网络级失败处理（不进冷却，进换路账）
            StreamRouteLedger.NoteStreamSuccess(host); // v1.0.20 E：成功清账
            return (capped, false);
        }
        catch (Exception ex)
        {
            BllLog.Warn("StreamResolver", $"HTTP GET 异常 url={BllLog.MaskUrl(url)}——网络级失败（不进冷却，进换路账）", ex);
            StreamRouteLedger.NoteStreamFailure(host); // v1.0.20 E：网络级失败记账，达阈值自动换路
            return (null, true);
        }
    }

    /// <summary>fix29 恶意加固：应答体读入上限（2MB）——取流应答只是票据/清单，正常远小于此。</summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>fix29 恶意加固：限量读应答体（公开供清单发现等内聚路径复用）——64KB 循环读，累计超 2MB 判恶意（返回 null 丢弃）；同时给读取套连接超时（防慢滴流拖死取流）。正常票据/清单几十 KB 级，绝不误伤。</summary>
    public static async Task<string?> ReadCappedAsync(HttpContent content)
    {
        using var cts = new CancellationTokenSource(Http.Timeout);
        await using var stream = await content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[64 * 1024];
        using var ms = new System.IO.MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxBodyBytes) return null; // 超封顶：判恶意应答，整包丢弃
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string RandomToken()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var r = new byte[10];
        Random.Shared.NextBytes(r);
        return new string(r.Select(b => chars[b % chars.Length]).ToArray());
    }
}

/// <summary>
/// fix29 恶意加固：取流地址白名单校验——只放行合法绝对 http(s) 地址。
/// 恶意直播源可投放 ftp/file 相对路径等非法「流地址」，轻则播放器报错卡死，重则内网路径探测；
/// 在取流总闸口统一收口，任何来源（内置源/用户源/备援）都不例外。
/// </summary>
public static class StreamUrlGuard
{
    public static bool IsValid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
