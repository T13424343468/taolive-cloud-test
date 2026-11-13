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
    // v28 主线④网络面·连接复用：静态单例 + PooledConnectionLifetime 5 分钟兜底刷新
    // （长会话下 DNS 变更/服务端切 IP 不至于挂在失效连接上；连接复用本身默认开，无开关）。
    private static readonly SocketsHttpHandler HttpHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(6),
    };
    private static readonly SocketsHttpHandler ProbeHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(4),
    };
    private static readonly HttpClient Http = new(HttpHandler) { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>A类第11项·超时分级：探活轻探收紧到 4 秒（换票/清单维持 10 秒），快失败快转向。</summary>
    private static readonly HttpClient ProbeHttp = new(ProbeHandler) { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>v28 主线④网络面·DNS 择优（DoH 兜底回落系统 DNS；IP 失败降权不拉黑）。取流链路统一走它。</summary>
    public static readonly StreamDnsPicker Dns = new();

    /// <summary>把 URL 的主机名经 DNS 择优换成「http://IP:port」直连形式（路径/查询原样保留）。
    /// 主机名已是 IP 字面量或解析失败时原样返回（兜底走系统解析，绝不因优化挂掉）。</summary>
    public static async Task<string> PickEndpointAsync(string url, StreamDnsPicker? picker = null)
    {
        var dnsUse = picker ?? Dns; // 测试注入点：默认取全局单例
        try
        {
            // AB 对照·DNS择优短路（vc30ab）：v28/v29 真机白屏+取流失败头号嫌疑即本模块
            // （DoH 连不上时每次解析吃满约 3 秒，挤压取流窗口）。本包改为只走系统 DNS：
            // 直接原样返回 URL，完全绕过 DoH/IP 择路；其余逻辑零改动。
            return url;
#pragma warning disable CS0162 // 保留原实现便于对照恢复
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
                return url;
            var ips = await dnsUse.ResolveAsync(uri.Host);
            if (ips.Length == 0) return url;
            var builder = new UriBuilder(uri) { Host = ips[0].ToString() };
            if (!uri.IsDefaultPort) builder.Port = uri.Port;
            return builder.Uri.ToString();
#pragma warning restore CS0162
        }
        catch { return url; } // DNS 全挂：原 URL 走系统解析兜底
    }
    private static readonly Regex M3u8Regex = new(
        "https?://[^\"\\\\\\s]+\\.m3u8[^\"\\\\\\s]*", RegexOptions.Compiled);

    private readonly YspTicketService _ysp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SourceFailover _failover = new(); // 自愈1：多候选源备援状态

    /// <summary>备援候选解析（自愈1）：主源失败后由 UI/预检注入（用户自有 m3u 按台名匹配等）。</summary>
    public Func<Channel, Task<string?>>? UserFallback { get; set; }

    /// <summary>
    /// vc45 隐患①修复：热备候选不再只走 UserFallback（内置备援表此前完全不覆盖）。
    /// 口径=与 ResolveWithFailoverAsync 同一注册表序：主 URL 恰为注册表某路时取「下一路」（环绕），
    /// 否则取注册表首条非主源路；注册表无可用路才退 UserFallback。备援 URL 恒定非票据型，重复调用返回同值。
    /// </summary>
    public async Task<string?> ResolveStandbyCandidateAsync(Channel ch, string mainUrl)
    {
        var fbs = FallbackSourceRegistry.GetAll(ch.Name);
        int mainIdx = -1;
        for (int i = 0; i < fbs.Count; i++)
            if (string.Equals(fbs[i].Url, mainUrl, StringComparison.Ordinal)) { mainIdx = i; break; }
        if (mainIdx >= 0 && fbs.Count > 1)
            return fbs[(mainIdx + 1) % fbs.Count].Url; // 主路本身是备援路 → 热备取下一路（环绕）
        for (int i = 0; i < fbs.Count; i++)
            if (!string.Equals(fbs[i].Url, mainUrl, StringComparison.Ordinal)) return fbs[i].Url;
        return UserFallback != null ? await UserFallback(ch) : null;
    }

    /// <summary>自愈1：多候选源备援状态（预检与取流共用，恢复自动回主源）。</summary>
    public SourceFailover Failover => _failover;

    /// <summary>
    /// vc45 隐患②：备援切换前拉流验证钩子（url/referer/ua → 是否可用）。空 = 默认清单级轻探
    /// （ProbeHotStandbyAsync，segmentLevel=false）；测试可注入桩探针保证确定性（沙箱不碰外网）。
    /// </summary>
    public Func<string, string?, string?, Task<bool>>? BackupProbeOverride { get; set; }

    // ---- vc45 ②选源智能化：动态源库消费 + 救急搜源触发 ------------------

    /// <summary>vc45②：动态源库（SourceScout 搜源验活收录的源）。备选序=内置备援表之后、用户源之前；
    /// 探死直接摘库（死源被新源直接顶替口径，不占候选位等复活）。空 = 既有口径不变。</summary>
    public DynamicSourceStore? DynamicSources { get; set; }

    /// <summary>vc45②：搜源管线（救急级触发消费）。频道全候选耗尽立即搜源，验活才入库；
    /// 常规级低频探索由宿主按空闲节奏驱动同一管线。空 = 不搜源（既有口径）。</summary>
    public SourceScout? Scout { get; set; }

    /// <summary>vc45②：候选发现器（公开免费渠道发现候选，IO 注入）。救急搜源与常规探索共用。</summary>
    public Func<string, Task<List<ScoutCandidate>>>? DiscoverCandidates { get; set; }

    private readonly ConcurrentDictionary<string, byte> _scoutInflight = new(); // vc45②：同台搜源并发护栏（救急连发不叠加）

    /// <summary>vc45②：候选验活（搜源管线 validate 注入用）——清单级轻验，口径与备援切换前验证一致。</summary>
    public Task<bool> ValidateCandidateAsync(string url, string? referer, string? ua) => ProbeBackupAsync(url, referer, ua);

    private async Task<bool> ProbeBackupAsync(string url, string? referer, string? ua)
    {
        if (BackupProbeOverride != null) return await BackupProbeOverride(url, referer, ua);
        return (await ProbeHotStandbyAsync(url, referer, ua, segmentLevel: false)).Ok;
    }

    /// <summary>
    /// vc45 隐患④：备援成功回灌+回主稳定确认。备援连续成功达 RecoverToPrimaryAfter 轮时，
    /// 回主前先探主源一次（重取主源票+清单级轻验）——探不活留备援，确认期继续累计，防回死源二次断流。
    /// </summary>
    private async Task FailoverOnSuccessWithRecoveryProbeAsync(Channel ch, string key)
    {
        await _failover.OnSuccessAsync(key, async () =>
        {
            var primary = await ResolvePlayUrlAsync(ch);
            if (primary == null) return false;
            string? pRef = null, pUa = null;
            foreach (var (k, v) in primary.Options)
            {
                if (k == "http-referrer") pRef = v;
                else if (k == "http-user-agent") pUa = v;
            }
            return await ProbeBackupAsync(primary.Url, pRef, pUa);
        });
    }

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

    private static TimeSpan TableTtl => TimeSpan.FromSeconds(AdvancedTuning.ListCacheSec); // 调参档接线：清单缓存读 AdvancedTuning

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
    // 调参档接线（老板 2026-11-12 令·旋钮全量进设置页）：清单缓存时长读 AdvancedTuning
    private static TimeSpan ShortCache => TimeSpan.FromSeconds(AdvancedTuning.ListCacheSec);

    public async Task<ResolvedStream?> ResolvePlayUrlAsync(Channel ch, bool force = false)
    {
        string? rawUrl = ch.SourceId switch
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
        if (string.IsNullOrEmpty(rawUrl)) return null;
        string url = rawUrl; // 非空收窄（CS8600 零警口径）

        var opts = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", ch.Referer));
        // 源档案接线：防盗头 UA 覆盖读档案（kankan 签名绑 UA，出厂档案 UaOverride 记账）
        var ua = SourceProfileStore.Shared.ProfileOf(ch.SourceId).UaOverride;
        if (string.IsNullOrEmpty(ua)) ua = ch.UserAgent;
        if (!string.IsNullOrEmpty(ua)) opts.Add(("http-user-agent", ua));
        return new ResolvedStream { Channel = ch, Url = url, Options = opts };
    }

    /// <summary>
    /// 自愈1：带备援的取流。主源失败 → 自动切用户自有源等备援候选（静默）；
    /// 备援连续成功 SourceFailover.RecoverToPrimaryAfter 轮自动回主源；全候选失败返回 null（UI 明确提示）。
    /// 治顿批①：主源失败后先查备援源注册表（15 卫视+4 央视公共备援），再退用户自有源；
    /// 备援 URL 恒定（非票据型），按 N5 口径不做换票假成功判定。
    /// vc45 隐患②：备援（内置表/用户源）切换前先清单级轻验拉流，探死候选按序让位，不硬切死路。
    /// </summary>
    public async Task<ResolvedStream?> ResolveWithFailoverAsync(Channel ch, bool force = false)
    {
        var key = ch.EpgKey;
        // vc43⑥ 生效性修复：候选从「主源+1 备援」扩为「主源+注册表全量按序+用户源」——
        // 同台多家族收录（央视六台 74.91/tsfile/applive、湖北/广东 bfgd/gmxw 双端）逐级切换，收录即可达；
        // 注册表耗尽再退用户自有源，全候选失败返回 null（UI 明确提示）。
        var fbs = FallbackSourceRegistry.GetAll(ch.Name);
        var dyn = DynamicSources?.Get(ch.Name).ToArray() ?? Array.Empty<DynamicSourceEntry>(); // vc45②：快照——探死摘库不动本轮遍历
        var candidateCount = 1 + fbs.Count + dyn.Length + (UserFallback != null ? 1 : 0);
        if (_failover.CurrentIndex(key) == 0)
        {
            var primary = await ResolvePlayUrlAsync(ch, force);
            if (primary != null) return primary;
            _failover.NextCandidateOnFailure(key, candidateCount); // 主源失败→按序进备援（候选数按实际）
        }
        // 治顿批①：内置备援表优先（登记台免网络二次解析，直连即用）；耗尽退 UserFallback
        // vc45 隐患②：备援切换前先轻验拉流（清单级）——探死的候选按序让位进下一个，不硬切死路；
        // 探活才记切换成功（OnSuccess）。探针可注入（测试确定性），默认走 ProbeHotStandbyAsync 清单级。
        var idx = _failover.CurrentIndex(key);
        while (idx >= 1 && idx <= fbs.Count)
        {
            var fb = fbs[idx - 1];
            var fbUa = string.IsNullOrEmpty(fb.UserAgent) ? ch.UserAgent : fb.UserAgent;
            if (!await ProbeBackupAsync(fb.Url, fb.Referer, fbUa))
            {
                var prev = idx;
                _failover.NextCandidateOnFailure(key, candidateCount); // 备援探死 → 下一路
                idx = _failover.CurrentIndex(key);
                if (idx == prev) break; // 已钳在末位不再前进：候选耗尽（末路探死即终局，防无限重探）
                continue;
            }
            await FailoverOnSuccessWithRecoveryProbeAsync(ch, key); // vc45 隐患④：达标回主前先探主源
            var fbOpts = new List<(string Key, string Value)>();
            if (!string.IsNullOrEmpty(fb.Referer)) fbOpts.Add(("http-referrer", fb.Referer));
            if (!string.IsNullOrEmpty(fbUa)) fbOpts.Add(("http-user-agent", fbUa));
            // 接线位 A（老板 2026-11-12 批准）：备援 URL 落预热池——下轮主源失败可零网络直起；
            // 保鲜期封顶 10 分钟：池命中会挡住主源重取，长保鲜期=回主源被拖死，短保鲜期最晚 10 分钟自愈
            WarmupDisk?.Put($"stream:{key}", fb.Url,
                Math.Min(10 * 60_000, WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new ResolvedStream { Channel = ch, Url = fb.Url, Options = fbOpts };
        }
        // vc45②：动态源库消费——搜源验活收录的源，序在内置备援表之后、用户源之前；
        // 先验后切（隐患②同口径），探死直接摘库（死源被新源直接顶替，不占候选位等复活）
        while (idx >= fbs.Count + 1 && idx <= fbs.Count + dyn.Length)
        {
            var de = dyn[idx - fbs.Count - 1];
            var deUa = string.IsNullOrEmpty(de.UserAgent) ? ch.UserAgent : de.UserAgent;
            if (!await ProbeBackupAsync(de.Url, de.Referer, deUa))
            {
                DynamicSources?.Remove(ch.Name, de.Url);
                var prevD = idx;
                _failover.NextCandidateOnFailure(key, candidateCount);
                idx = _failover.CurrentIndex(key);
                if (idx == prevD) break; // 已钳在末位：候选耗尽（救急搜源在出口触发）
                continue;
            }
            await FailoverOnSuccessWithRecoveryProbeAsync(ch, key);
            var deOpts = new List<(string Key, string Value)>();
            if (!string.IsNullOrEmpty(de.Referer)) deOpts.Add(("http-referrer", de.Referer));
            if (!string.IsNullOrEmpty(deUa)) deOpts.Add(("http-user-agent", deUa));
            WarmupDisk?.Put($"stream:{key}", de.Url,
                Math.Min(10 * 60_000, WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new ResolvedStream { Channel = ch, Url = de.Url, Options = deOpts };
        }
        if (UserFallback == null) { await ScoutEmergencyAsync(ch, key); return null; }
        try
        {
            var alt = await UserFallback(ch);
            // vc45 隐患②：用户备援同样先验后切——探死按失败钳在末位候选返回 null，不硬切死路
            if (string.IsNullOrEmpty(alt) || !await ProbeBackupAsync(alt, ch.Referer, ch.UserAgent))
            {
                _failover.NextCandidateOnFailure(key, candidateCount); // 备援也失败（钳在末位候选）
                await ScoutEmergencyAsync(ch, key); // vc45②：全候选耗尽 → 救急搜源
                return null;
            }
            await FailoverOnSuccessWithRecoveryProbeAsync(ch, key); // vc45 隐患④：达标回主前先探主源
            var opts = new List<(string Key, string Value)>();
            if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", ch.Referer));
            if (!string.IsNullOrEmpty(ch.UserAgent)) opts.Add(("http-user-agent", ch.UserAgent));
            // 接线位 A：用户备援同样落池（同封顶口径）
            WarmupDisk?.Put($"stream:{key}", alt,
                Math.Min(10 * 60_000, WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new ResolvedStream { Channel = ch, Url = alt, Options = opts };
        }
        catch { await ScoutEmergencyAsync(ch, key); return null; } // vc45②：用户备援异常同口径触发救急
    }

    /// <summary>
    /// vc45②：救急级搜源——全候选（主+内置备援+动态+用户）耗尽立即触发，fire-and-forget 不拖播放；
    /// 同台并发护栏：搜源进行中不重复触发。验活与切换预算约束在管线内收口（没验活的源绝不入库）。
    /// </summary>
    private async Task ScoutEmergencyAsync(Channel channel, string channelKey)
    {
        if (Scout == null || DiscoverCandidates == null) return;
        if (!_scoutInflight.TryAdd(channelKey, 0)) return;
        var discover = DiscoverCandidates;
        var scout = Scout;
        _ = Task.Run(async () =>
        {
            try
            {
                var known = FallbackSourceRegistry.Has(channel.Name) || (DynamicSources?.Has(channel.Name) ?? false);
                var resolverRef = this;
                await scout.RunAsync(
                    channel.Name,
                    () => discover(channel.Name),
                    async c => await resolverRef.ProbeBackupAsync(c.Url, c.Referer,
                        string.IsNullOrEmpty(c.UserAgent) ? channel.UserAgent : c.UserAgent),
                    existingBestScore: 0, // 救急=全候选判死，任何验活源均可入库（existingBestScore≤0=顶替口径）
                    channelKnown: known);
            }
            catch { /* 搜源失败不拖播放；过程留档由管线 Log 收口 */ }
            finally { _scoutInflight.TryRemove(channelKey, out _); }
        });
        await Task.CompletedTask;
    }

    /// <summary>上海取票/拉流统一 UA（JWT 绑 ua_hash，两侧必须一致）。</summary>
    public const string KankanFetchUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    // ---- 通用：短效缓存包装（60 秒；预连接/快速切台命中即起播） ------------------

    /// <summary>A 类第6项·预热落盘注入（长效源起播零探测；票据短效源不落盘）。</summary>
    public WarmupStore? WarmupDisk { get; set; }

    /// <summary>票据短效源（落了必过期，误导起播）：央视频 5 分钟票、gzstv 腾讯云分钟级票，不落盘。
    /// jstv 不在此列——network.md N3 修复后其保鲜期由 WarmupPolicy 专属节拍（150s）收口，短于签名期 180s，
    /// 落盘命中不再必过期（落盘只加速口径恢复成立）。
    /// 源档案接线（上善若水纪律）：读 SourceProfileStore 档案，出厂名册做默认回落——机制读档案不读代码。</summary>
    private static bool IsShortTicketSource(string sourceId) =>
        SourceProfileStore.Shared.KindOf(sourceId) == SourceKind.TicketShort;

    /// <summary>
    /// network.md N5·票据/签名型源判定（URL 是否随换票轮换）：票据型源换票「假成功」（返回同 URL）
    /// 需按失败计数升级备援；恒定 URL 源（xatv 直连、nmtv/gxtv 整表定位等）换票成功返回原 URL 属正常，
    /// 不做假成功判定——此前 LiveVideoHost.RefreshStreamAsync 对恒定源每次重连都按换票失败计，
    /// attempt≥2 即误转备援空转。UI 层判定点（LiveVideoHost.RefreshStreamAsync）改走本口径（BLL 单一事实源）。
    /// kankan 流 URL 是否轮换未核实，按保守口径出厂档案 Rotating=false（宁可少计数，不误触发备援）。
    /// 源档案接线（上善若水纪律）：读档案 Rotating 标记；新源两次取样自动定档，机制回报更新。
    /// </summary>
    public static bool IsRotatingTicketSource(string sourceId) =>
        SourceProfileStore.Shared.IsRotating(sourceId);

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
            SourceProfileStore.Shared.ObserveSample(ch.SourceId, url, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // 源档案：取样回报定档
            WarmupDisk?.Put($"stream:{key}", url,
                SourceProfileStore.Shared.ProfileOf(ch.SourceId).RefreshIntervalMs > 0
                    ? SourceProfileStore.Shared.ProfileOf(ch.SourceId).RefreshIntervalMs
                    : WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs,
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
            // network.md N2：改用分级取文本——只有服务器明确拒绝（403/429）才入指数退避账；
            // 网络级失败（超时/断网/异常/5xx）按 Unreachable 只给 30 秒短冷却，
            // 绝不回灌拉黑（v23 修法 B 公开口径；此前 body is null 一律 MarkCooldown，
            // 断网几分钟即把央视频台白冻至 2 小时，全源 SourceBackoffLedger 同口径对照）。
            var (body, rejected) = await HttpGetStringClassifiedAsync(ticketUrl, null, YspTicketService.ClientUserAgent);
            if (body is null)
            {
                if (rejected) _ysp.MarkCooldown(ch.EpgKey, DateTime.UtcNow); // 1.0.12 修法 B：明确拒绝才指数退避（2/4/8 分钟）
                else _ysp.MarkUnreachable(ch.EpgKey, DateTime.UtcNow); // N2：网络级失败短冷却 30s，不进指数账
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
        req.Headers.TryAddWithoutValidation("M-Uuid", DeviceIdentity.GetInstallUuid()); // network.md N6：安装级随机 uuid（此前恒定 taolive-tv-01 全安装共享风控配额）
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
        url = await PickEndpointAsync(url); // v28 主线④：探测请求经 DNS 择优（https 证书按原域名 SNI 校验不变，HttpClient 连接复用兜底）
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
    /// vc45 隐患⑤：假 200 判死口径（vc43 74.91 伪 200 教训落码）——响应体非空却无任何 m3u8 标记
    /// （#EXTM3U/#EXTINF/#EXT-X-）即死（如 74.91 伪 200 返回「没有配置任何服务器」文案）。
    /// 体空不做判定（空清单另有分片级校验兜底）。
    /// </summary>
    public static bool LooksLikeManifest(string? body) =>
        !string.IsNullOrEmpty(body)
        && (body.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase)
            || body.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase)
            || body.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase));

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
        // vc45 隐患⑤：假 200 判死——200+非 m3u8 文案=死（74.91 伪 200 教训），不计服务器明确拒绝
        if (!string.IsNullOrEmpty(manifest) && !LooksLikeManifest(manifest)) return (false, false);
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
        url = await PickEndpointAsync(url); // v28 主线④：换票/清单请求经 DNS 择优（同上兜底口径）
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
