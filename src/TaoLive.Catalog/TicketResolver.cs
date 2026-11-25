using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Catalog;

/// <summary>
/// 取流器（2.0 W4）：一期 StreamResolver 各源分支逐字移植，按端点主机分发——
/// API 取票口（Kind=Web）播前换真实流；直连端点原样放行。
/// 正常 5 分钟换票缓存；央视频走 YspTicketService（403 指数退避账，口径同一期修法 B）。
/// 失败返回 null，由会话层备源自救承接（会话层契约不变）。
/// </summary>
public sealed class TicketResolver
{
    /// <summary>播放统一 UA（App 全局同口径，一期实锤）。</summary>
    public const string ClientUserAgent = "Mozilla/5.0 (Linux; Android 12; TCL 50V8G) AppleWebKit/537.36 TaoLive/2.0";

    private static readonly Regex M3u8Regex = new(
        "https?://[^\"\\\\\\s]+\\.m3u8[^\"\\\\\\s]*", RegexOptions.Compiled);

    private readonly YspTicketService _ysp = new();
    // 按上游主机粒度单飞（vc73 审查清零 P2 修）：同上游仍串行（防重复解析/限流），
    // 不同上游互不排队——旧全局闸让 5 秒超时的慢请求阻塞所有端点取票（换台首慢病根之一）。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, (string Url, long FetchedAt)> _cache = new();
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(5);

    /// <summary>可注入 HttpClient（测试换假实现）；缺省静态实例。#74 切台提速（2026-11-15）：10→5 秒，死源取票快判快走。</summary>
    public static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// 取票台账出口（埋点补充 2026-11-15）：壳层接 CrashNet 落运行台账；未接线为空操作。
    /// 控量口径：缓存命中不记（换台快慢归因的排除依据——无 [取票] 日志即命中缓存），
    /// 只记缓存未命中的成功/失败与耗时；退避/整表聚合等细节由调用方与 [预热] 侧补位。
    /// </summary>
    public static Action<string>? Reporter { get; set; }

    /// <summary>
    /// 端点换流：Web 取票口经各源分支解析；直连端点原样返回。
    /// channelKey 用频道 Id（缓存键），channelName 用于整表行匹配（nmtv/gxtv）。
    /// </summary>
    public async Task<string?> ResolveAsync(SourceEndpoint endpoint, string channelKey, string channelName)
    {
        if (!endpoint.IsValid) return null;
        var uri = new Uri(endpoint.Url, UriKind.Absolute);
        // 5 分钟缓存命中：直接复用（快速切台零等待）
        var key = $"{channelKey}|{endpoint.Url}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (TryGetCache(key, now, out var cached)) return cached;

        var sw = System.Diagnostics.Stopwatch.StartNew(); // 取票耗时账（墙钟，仅诊断展示）
        var gate = _hostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (TryGetCache(key, now, out cached)) return cached;

            string? url = uri.Host switch
            {
                "bkliveinfo.ysp.cctv.cn" => await ResolveYspAsync(endpoint.Url).ConfigureAwait(false),
                "zlive-das.cztv.com" => await ResolveCztvAsync(endpoint.Url).ConfigureAwait(false),
                "api.gzstv.com" => await ResolveByRegexAsync(endpoint.Url, "https://www.gzstv.com/").ConfigureAwait(false),
                "kapi.kankanews.com" => await ResolveKankanAsync(endpoint.Url).ConfigureAwait(false),
                "api-bt.nmtv.cn" => await ResolveNmtvAsync(channelName).ConfigureAwait(false),
                "api2019.gxtv.cn" => await ResolveGxtvAsync(channelName).ConfigureAwait(false),
                "lived.jstv.com" => ResolveJstv(endpoint.Url),
                _ => endpoint.Url, // 直连：原样
            };
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var resolved)
                || (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
            {
                // 取票失败打点（埋点补充 2026-11-15）：此前取票失败完全静默，「换台慢/起不来」查无第一手证据
                Reporter?.Invoke($"[取票] {uri.Host} 失败（耗时 {sw.ElapsedMilliseconds}ms，返回空或非法）");
                return null;
            }
            PutCache(key, url, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            // 文案口径修正（真机反馈批#20）：直连端点原样放行，无换流动作、0ms 是常态——
            // 「成功（耗时 0ms，缓存未命中）」读作「假票/账目异常」，改口径与网络换流端点区分开
            if (ReferenceEquals(url, endpoint.Url))
                Reporter?.Invoke($"[取票] {uri.Host} 直连放行（免换流，不入耗时账）");
            else
                Reporter?.Invoke($"[取票] {uri.Host} 成功（耗时 {sw.ElapsedMilliseconds}ms，缓存未命中）");
            return url;
        }
        finally { gate.Release(); }
    }

    // ---- 缓存 -------------------------------------------------------------------

    private bool TryGetCache(string key, long nowEpoch, out string? url)
    {
        lock (_cacheGate)
        {
            url = null;
            return _cache.TryGetValue(key, out var t)
                && nowEpoch - t.FetchedAt < (long)RenewInterval.TotalSeconds
                && !string.IsNullOrEmpty(t.Url)
                && (url = t.Url) != null;
        }
    }

    private void PutCache(string key, string url, long nowEpoch)
    {
        lock (_cacheGate) { _cache[key] = (url, nowEpoch); }
    }

    /// <summary>
    /// vc69 坏票不自焚：单频道单端点票据作废（会话层报 HTTP 状态错时经壳层接线调用，
    /// 原地换新票重试）。只清票据缓存，退避账不动（防取票风暴，fix23 同口径）。
    /// </summary>
    public void Invalidate(string channelKey, string endpointUrl)
    {
        lock (_cacheGate) { _cache.Remove($"{channelKey}|{endpointUrl}"); }
    }

    // ---- 央视频：cKey 取票（iretcode=0 → playurl；缓存+403 冷却走 YspTicketService） ----

    private async Task<string?> ResolveYspAsync(string entryUrl)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (cnlid, livepid, defn) = ParseYspParams(entryUrl);
        if (cnlid is null || livepid is null || defn is null) return null;

        var ticketUrl = _ysp.BuildTicketUrl(cnlid, livepid, defn, now);
        string? body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ticketUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", ClientUserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            body = await ReadCappedAsync(resp).ConfigureAwait(false);
        }
        catch { return null; } // 网络级失败：不进冷却（fix28 连接优先口径），返回 null 走会话自救
        if (body is null)
        {
            // 服务器明确拒绝（403/429）：指数退避账（2/4/8 分钟 ×2 步进封顶 2 小时）
            _ysp.MarkCooldown(entryUrl, DateTime.UtcNow);
            return null;
        }
        var (iretcode, playurl) = ParseYspResponse(body);
        if (iretcode == 0 && !string.IsNullOrEmpty(playurl))
        {
            _ysp.MarkFetched(entryUrl, now, playurl);
            return playurl;
        }
        _ysp.MarkCooldown(entryUrl, DateTime.UtcNow);
        return null;
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

    private async Task<string?> ResolveCztvAsync(string api)
    {
        var body = await HttpGetStringAsync(api, "https://www.cztv.com/").ConfigureAwait(false);
        if (body is null) return null;
        var urls = M3u8Regex.Matches(body).Select(m => m.Value.Replace("\\/", "/")).Distinct().ToList();
        if (urls.Count == 0) return null;
        var pick = urls.FirstOrDefault(u => u.Contains("1080")) ?? urls[0];
        return CztvSign.SignPlayUrl(pick, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RandomToken());
    }

    // ---- 贵州 gzstv / 通用清单页：Referer 防盗链取票 → 首个 m3u8 --------------------

    private async Task<string?> ResolveByRegexAsync(string api, string referer)
    {
        var body = await HttpGetStringAsync(api, referer).ConfigureAwait(false);
        if (body is null) return null;
        var m = M3u8Regex.Match(body);
        return m.Success ? m.Value.Replace("\\/", "/") : null;
    }

    // ---- 上海 kankan：MD5 排序签名（照 sh_v2 实测实现；取流与取票同 UA） -----------

    private async Task<string?> ResolveKankanAsync(string api)
    {
        var channelId = Regex.Match(api, "channel_id=([^&]+)").Groups[1].Value;
        if (string.IsNullOrEmpty(channelId)) return null;

        var nonce = RandomToken();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var pars = new SortedDictionary<string, string>
        {
            ["platform"] = "mobile", ["version"] = "2.46.0", ["nonce"] = nonce,
            ["timestamp"] = ts, ["Api-Version"] = "v1", ["channelId"] = channelId,
        };
        var sign = KankanSign.Sign(pars, long.Parse(ts), nonce);
        var qs = string.Join("&", pars.Select(kv => $"{kv.Key}={kv.Value}"));

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://kapi.kankanews.com/content/pc/tv/channel/detail?{qs}");
        req.Headers.TryAddWithoutValidation("sign", sign);
        req.Headers.TryAddWithoutValidation("User-Agent", ClientUserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://live.kankanews.com/");
        req.Headers.TryAddWithoutValidation("M-Uuid", "taolive-tv-01");
        string body;
        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            body = await ReadCappedAsync(resp).ConfigureAwait(false) ?? "";
        }
        catch { return null; }
        var m = M3u8Regex.Match(body.Replace("\\/", "/"));
        return m.Success ? m.Value : null;
    }

    // ---- 内蒙古 nmtv：整表 XXTEA 解密 → 按 title 行级匹配 --------------------------
    // 行级匹配口径（2026-11-08「包头」根因修复）：行内 data.streamUrl 位于 title 之前，
    // 间隙法主口径 + indexId 兜底 + 旧口径兜底，与一期 NmtvSource.LocateStreamUrl 一致。

    private async Task<string?> ResolveNmtvAsync(string channelName)
    {
        var json = await FetchTableCachedAsync("nmtv", FetchNmtvTableRawAsync).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;
        return NmtvRow.LocateStreamUrl(json, channelName);
    }

    private async Task<string?> FetchNmtvTableRawAsync()
    {
        string body;
        using (var req = new HttpRequestMessage(HttpMethod.Get, ChannelTable.NmtvApi))
        {
            req.Headers.TryAddWithoutValidation("Client-Type", "web");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.nmtv.cn/");
            req.Headers.TryAddWithoutValidation("User-Agent", ClientUserAgent);
            try
            {
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                body = await ReadCappedAsync(resp).ConfigureAwait(false) ?? "";
            }
            catch { return null; }
        }
        try
        {
            var raw = Convert.FromBase64String(body.Trim().Trim('"'));
            return Encoding.UTF8.GetString(NmtvXxtea.Decrypt(raw));
        }
        catch { return null; }
    }

    // ---- 广西 gxtv：整表抓取 → 按 name 行内 encodeM3u8 匹配（括号计数切行）---------

    private async Task<string?> ResolveGxtvAsync(string channelName)
    {
        var json = await FetchTableCachedAsync("gxtv", FetchGxtvTableRawAsync).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;
        return GxtvRow.LocateStreamUrl(json, channelName);
    }

    private async Task<string?> FetchGxtvTableRawAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ChannelTable.GxtvApi);
        req.Headers.TryAddWithoutValidation("Referer", "https://tv.gxtv.cn/");
        req.Headers.TryAddWithoutValidation("User-Agent", GxtvRow.UserAgent);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pageNo"] = "1", ["pageSize"] = "1000", ["liveMethod"] = "0",
        });
        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return await ReadCappedAsync(resp).ConfigureAwait(false);
        }
        catch { return null; }
    }

    // 整表源级聚合（v25 修法六 1a 口径）：TTL 内同源只发 1 次。
    // vc74 审查清零 P3③：并发 miss 单飞——TTL 过期瞬间多个并发调用此前各自发网（双发多耗）；
    // 时间戳改落网后取（旧版取前，实际新鲜窗被拉网时长吃掉，整表提前过期）。
    private readonly Dictionary<string, (string Json, long FetchedAt)> _tableCache = new();
    private readonly Dictionary<string, Task<string?>> _tableInflight = new();

    /// <summary>整表清单留手时长（nmtv/gxtv 频道→流清单表 TTL）：接通「清单缓存」旋钮
    /// （老板 2026-11-17 拍板，假旋钮接通第 3 位）。出厂 60 秒=原常量口径，改档即时生效；
    /// 上限 10 分钟即蓝图 v2.5「清单缓存上限 10min」钳制（Apply 内统一钳）。</summary>
    internal static TimeSpan TableTtl => TimeSpan.FromSeconds(AdvancedTuning.ListCacheSec);

    internal async Task<string?> FetchTableCachedAsync(string sourceKey, Func<Task<string?>> fetch)
    {
        Task<string?> flight;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_cacheGate)
        {
            if (_tableCache.TryGetValue(sourceKey, out var t)
                && now - t.FetchedAt < (long)TableTtl.TotalSeconds) return t.Json;
            if (!_tableInflight.TryGetValue(sourceKey, out flight!))
            {
                flight = fetch(); // 单飞：锁内登记即共享，并发首查共用同一次网络
                _tableInflight[sourceKey] = flight;
            }
        }
        try
        {
            var json = await flight.ConfigureAwait(false);
            lock (_cacheGate)
            {
                if (!string.IsNullOrEmpty(json))
                    _tableCache[sourceKey] = (json, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            return json;
        }
        finally
        {
            lock (_cacheGate) _tableInflight.Remove(sourceKey); // 首查人收账：飞完必摘牌（含失败），防毒任务滞留
        }
    }

    // ---- 江苏 jstv：txSecret 短效 180s，播放时重签 --------------------------------

    private static string? ResolveJstv(string endpoint)
    {
        var chan = new Uri(endpoint).AbsolutePath.TrimStart('/').Replace("pro.m3u8", "");
        return string.IsNullOrEmpty(chan)
            ? null
            : JstvSign.PlayUrl(chan, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 180); // txTime=hex(now+180s)
    }

    // ---- 公共 -----------------------------------------------------------------------

    private static async Task<string?> HttpGetStringAsync(string url, string? referer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("User-Agent", ClientUserAgent);
        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return await ReadCappedAsync(resp).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode) return null;
        var buf = new byte[1 << 20]; // 1MB 上限：清单/取票响应远小于此
        int n = 0, read;
        using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        while (n < buf.Length && (read = await s.ReadAsync(buf.AsMemory(n)).ConfigureAwait(false)) > 0) n += read;
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static string RandomToken()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var r = new byte[10];
        Random.Shared.NextBytes(r);
        return new string(r.Select(b => chars[b % chars.Length]).ToArray());
    }
}

/// <summary>浙江 cztv CDN auth_key 签名（一期 CztvSource 逐字移植）。</summary>
public static class CztvSign
{
    public static readonly Dictionary<string, string> CdnKeys = new()
    {
        ["zwebl01.cztv.com"] = "CHWr9VybUeBZE1VB", ["zwebl02.cztv.com"] = "CHWr9VybUeBZE1VB",
        ["zwebl03.cztv.com"] = "CHWr9VybUeBZE1VB", ["zwebl04.cztv.com"] = "CHWr9VybUeBZE1VB",
        ["zwebl05.cztv.com"] = "CHWr9VybUeBZE1VB", ["zwebl06.cztv.com"] = "CHWr9VybUeBZE1VB",
        ["zwebl07.cztv.com"] = "CHWr9VybUeBZE1VB", ["zhfivel01.cztv.com"] = "9T08yiAoqM4eeCwV",
        ["zhfivel02.cztv.com"] = "9T08yiAoqM4eeCwV", ["zhfivel03.cztv.com"] = "9T08yiAoqM4eeCwV",
        ["zhfivel04.cztv.com"] = "9T08yiAoqM4eeCwV", ["zhfivel05.cztv.com"] = "9T08yiAoqM4eeCwV",
        ["zhfivel06.cztv.com"] = "9T08yiAoqM4eeCwV",
    };

    /// <summary>auth_key 签名（摸底实测算法：md5(path-ts-rand-0-key)）。</summary>
    public static string BuildAuthKey(string path, long ts, string rand, string key)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var raw = $"{path}-{ts}-{rand}-0-{key}";
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return $"{ts}-{rand}-0-{Convert.ToHexString(h).ToLowerInvariant()}";
    }

    public static string SignPlayUrl(string url, long ts, string rand)
    {
        var uri = new Uri(url);
        if (!CdnKeys.TryGetValue(uri.Host, out var key)) return url;
        return $"{url}?auth_key={BuildAuthKey(uri.AbsolutePath, ts, rand, key)}";
    }
}

/// <summary>上海 kankan 请求签名（一期 KankanSource 逐字移植）。</summary>
public static class KankanSign
{
    public const string Secret = "28c8edde3d61a0411511d3b1866f0636";

    public static string Sign(SortedDictionary<string, string> pars, long ts, string nonce)
    {
        var merged = new SortedDictionary<string, string> { ["timestamp"] = ts.ToString(), ["nonce"] = nonce };
        foreach (var kv in pars) merged[kv.Key] = kv.Value;
        var qs = string.Join("&", merged.Select(kv => $"{kv.Key}={kv.Value}"));
        using var md5 = System.Security.Cryptography.MD5.Create();
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(qs + Secret));
        return Convert.ToHexString(h).ToLowerInvariant();
    }
}

/// <summary>江苏 jstv 腾讯云防盗链签名（一期 JstvSource 逐字移植）。</summary>
public static class JstvSign
{
    public const string OriginKey = "7cSrbhisZe2tF3N3ZGPN";

    /// <summary>x() 变换后的实际密钥：originKey.slice(6,-3) 再 Base64 解码（8 字节，不足补齐）。</summary>
    public static string DecodedKey()
    {
        var sliced = OriginKey[6..^3];
        var padded = sliced.PadRight(sliced.Length + (4 - sliced.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    public static (string TxSecret, string TxTime) Sign(string decodedKey, string chan, long epoch)
    {
        var txTime = epoch.ToString("x8");
        using var md5 = System.Security.Cryptography.MD5.Create();
        var raw = $"{decodedKey}{chan}{txTime}";
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return (Convert.ToHexString(h).ToLowerInvariant(), txTime);
    }

    public static string PlayUrl(string chan, long epoch)
    {
        var (secret, txTime) = Sign(DecodedKey(), chan, epoch);
        return $"https://lived.jstv.com/{chan}pro.m3u8?txSecret={secret}&txTime={txTime}";
    }
}

/// <summary>内蒙古整表行级匹配（一期 NmtvSource.LocateStreamUrl 逐字移植，静态表查兜底不用）。</summary>
public static class NmtvRow
{
    private const string M3u8Pattern = "https?://[^\"\\\\\\s]+\\.m3u8[^\"\\\\\\s]*";

    public static string? LocateStreamUrl(string json, string title)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(title)) return null;
        var token = $"\"title\":\"{title}\"";
        var rx = new Regex(M3u8Pattern, RegexOptions.Compiled);
        var t = json.IndexOf(token, StringComparison.Ordinal);
        if (t >= 0)
        {
            var ms = rx.Matches(json);
            for (var i = ms.Count - 1; i >= 0; i--)
            {
                if (ms[i].Index >= t) continue;
                var between = json.Substring(ms[i].Index, t - ms[i].Index);
                if (!between.Contains("{\"aiGene\""))
                    return ms[i].Value.Replace("\\/", "/");
                break; // 最近 m3u8 已跨行界 → 本行名前无流，走兜底
            }
        }
        // 兜底2：旧口径（一期静态 indexId 表在 2.0 台单里由 ChannelTable 供给，此处仅旧口径）
        if (t >= 0)
        {
            var m = rx.Match(json[t..]);
            if (m.Success) return m.Value.Replace("\\/", "/");
        }
        return null;
    }
}

/// <summary>广西整表行内 encodeM3u8 匹配（一期 GxtvSource 逐字移植）。</summary>
public static class GxtvRow
{
    public const string UserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    public static string? LocateStreamUrl(string json, string name)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return null;
        var rowsStart = json.IndexOf("\"rows\":[", StringComparison.Ordinal);
        if (rowsStart < 0) return null;
        var i = json.IndexOf('{', rowsStart);
        while (i >= 0)
        {
            var end = JsonBlockEnd(json, i);
            if (end < 0) break;
            var row = json.Substring(i, end - i + 1);
            if (row.Contains($"\"name\":\"{name}\""))
            {
                var u = Regex.Match(row, "\"encodeM3u8\":\"(https?://[^\"]+)\"");
                if (u.Success && u.Groups[1].Value != "")
                    return u.Groups[1].Value.Replace("\\/", "/");
            }
            i = json.IndexOf('{', end + 1);
        }
        return null;
    }

    private static int JsonBlockEnd(string s, int open)
    {
        var depth = 0; var inStr = false;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (c == '\\') i++;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
