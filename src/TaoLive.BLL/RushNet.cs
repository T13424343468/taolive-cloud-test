using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.BLL;

// ============ 道映 v1.0.16 抢流与网络优化（v3.1 冻结稿，用户 2026-11-08 拍板「开工」）============
// A 健康账评分 / B 换台探测竞速 / C 备用域名择优 / D 并行抢首包 / E 预热记账 / F DNS 优选 / G IP 测速择路。
// 兜底铁律：任何新机制全败/作废/被拒一律退回现行链路（最近验证选源 → 取流重试 2s×3），绝不把台连死。

/// <summary>
/// 健康账评分（冻结稿 A）：评分 = 延迟分×0.4 + 流畅分×0.3 + 连续成功分×0.3，
/// 连胜/连败记忆按「距上次使用」做指数衰减（每过 1 小时权重减半），不清零——
/// 偶发一次失败不至于把长期好源一票否决。判死仍走连败 3 次淘汰（ChannelPrecheck 原口径不动）。
/// </summary>
public static class SourceScorer
{
    public const double LatencyWeight = 0.4;
    public const double StallWeight = 0.3;
    public const double StreakWeight = 0.3;

    /// <summary>延迟分线性区间：≤400ms 记满分，≥3000ms 记零分，中间线性。</summary>
    public const int GoodLatencyMs = 400;
    public const int BadLatencyMs = 3000;

    /// <summary>连续成功记满分的台阶（防无限拉差距）。</summary>
    public const int StreakFull = 5;

    /// <summary>指数衰减：每过 1 小时，连续成功分权重减半（ln2 ≈ 0.693）。</summary>
    public const double HalfLifeHours = 1.0;
    private const double Ln2 = 0.6931471805599453;

    /// <summary>延迟分：0=差 1=好；FirstDelayMs==0 视为未知记中性 0.5。</summary>
    public static double LatencyScore(int firstDelayMs)
    {
        if (firstDelayMs <= 0) return 0.5;
        if (firstDelayMs <= GoodLatencyMs) return 1.0;
        if (firstDelayMs >= BadLatencyMs) return 0.0;
        return 1.0 - (double)(firstDelayMs - GoodLatencyMs) / (BadLatencyMs - GoodLatencyMs);
    }

    /// <summary>连续成功分：0–5 台阶线性；失败连胜已清零即 0 分。</summary>
    public static double StreakScore(int successStreak) =>
        Math.Clamp(successStreak, 0, StreakFull) / (double)StreakFull;

    /// <summary>
    /// 综合评分（0–1）。entries 无记录返回 null（调用方决定是否参选）；
    /// 衰减按「距最近一次被使用/核验」的时长对连续成功分做指数折半。
    /// </summary>
    public static double? Score(SourceHealthEntry? e, long nowMs)
    {
        if (e is null) return null;
        double latency = LatencyScore(e.FirstDelayMs);
        double stall = 1.0 - Math.Clamp(e.StallRate, 0.0, 1.0);
        double streak = StreakScore(e.SuccessStreak);
        // 衰减基准：优先取最后使用时间，没用过取核验时间（都为 0 视为刚发生，不衰减）
        long baseMs = e.LastUsedMs > 0 ? e.LastUsedMs : e.CheckedAtMs;
        double elapsedHours = baseMs > 0 ? Math.Max(0, nowMs - baseMs) / 3600000.0 : 0;
        double decay = Math.Exp(-Ln2 * elapsedHours / HalfLifeHours);
        return latency * LatencyWeight + stall * StallWeight + streak * StreakWeight * decay;
    }
}

/// <summary>
/// 换台探测竞速（冻结稿 B，默认开）：换台时对评分前 2~3 路并行轻探（Range 0–2047 控制层短连接，
/// 不算播放连接），单路超时 1.2s、总窗口 1.5s、同源间隔 ≥30s 防判异常流量；
/// 被拒（403/429）交回现行冷却；竞速任务挂播放代数，换台即作废；全败退回现行选源链路。
/// </summary>
public sealed class RacePreconnector
{
    /// <summary>单路探测超时（毫秒）。</summary>
    public const int PerProbeTimeoutMs = 1200;

    /// <summary>竞速总窗口（毫秒）：到点即收口，绝不拖慢换台。</summary>
    public const int TotalWindowMs = 1500;

    /// <summary>并行参选路数上限（评分前 2~3 路）。</summary>
    public const int MaxParallel = 3;

    /// <summary>同源两次探测最小间隔（毫秒）：防被服务器判异常流量。</summary>
    public const int MinPerSourceGapMs = 30_000;

    private readonly ConcurrentDictionary<string, long> _lastProbeMs = new();
    private readonly Func<long> _clock;

    /// <summary>clock：毫秒时钟（真机 Environment.TickCount64 / 测试可控）。</summary>
    public RacePreconnector(Func<long>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>同源（SourceId:EpgKey）上次探测时间（无记录 long.MinValue/2）。</summary>
    public long LastProbeAt(string sourceKey) =>
        _lastProbeMs.TryGetValue(sourceKey, out var t) ? t : long.MinValue / 2;

    /// <summary>
    /// 纯逻辑择路：同频道候选按评分降序取前 N（2~3 路，N=可用数与 MaxParallel 取小）；
    /// 已在 30s 间隔冷却内的候选跳过（防异常流量）；全被跳过/无候选返回空表（退回现行链路）。
    /// </summary>
    public List<Channel> Pick(IEnumerable<Channel> candidates, Func<Channel, double?> score, long nowMs)
    {
        var ranked = candidates
            .Select(c => (Ch: c, S: score(c) ?? 0.0))
            .OrderByDescending(x => x.S)
            .Select(x => x.Ch)
            .Where(c => nowMs - LastProbeAt($"{c.SourceId}:{c.EpgKey}") >= MinPerSourceGapMs)
            .Take(MaxParallel)
            .ToList();
        return ranked;
    }

    /// <summary>
    /// 竞速执行：并行轻探各候选，首个可达者胜出。
    /// probe 返回（可达, 被拒）；被拒（403/429）经 onRejected 交回现行冷却后不再参选；
    /// 总窗口到点收口返回 null（全败/未决），调用方退回现行选源链路。
    /// validate：胜出前最后一次核对播放代数，代数已换（换台）返回 null（作废）。
    /// </summary>
    public async Task<Channel?> RunAsync(
        IReadOnlyList<Channel> candidates,
        Func<Channel, Task<(bool Ok, bool Rejected)>> probe,
        Func<Channel, bool> validate,
        Action<Channel>? onRejected = null,
        CancellationToken ct = default)
    {
        if (candidates is null || candidates.Count == 0) return null;
        var tasks = candidates.Select(async c =>
        {
            string key = $"{c.SourceId}:{c.EpgKey}";
            _lastProbeMs[key] = _clock();
            try
            {
                var (ok, rejected) = await probe(c).WaitAsync(TimeSpan.FromMilliseconds(PerProbeTimeoutMs)).ConfigureAwait(false);
                return (Channel: c, Ok: ok, Rejected: rejected);
            }
            catch (TimeoutException) { return (Channel: c, Ok: false, Rejected: false); }
            catch (OperationCanceledException) { return (Channel: c, Ok: false, Rejected: false); }
            catch (Exception) { return (Channel: c, Ok: false, Rejected: false); } // 探测异常按不可达，不拖垮竞速
        }).ToList();

        // 总窗口兜底：到点即收口，绝不拖慢换台；全部先完也提前收
        var allDone = Task.WhenAll(tasks);
        await Task.WhenAny(allDone, Task.Delay(TotalWindowMs)).ConfigureAwait(false);

        // 收口：只取已完成任务的结论（未完成按不可达弃权）；按参选顺序=评分顺序，天然择优
        Channel? winner = null;
        foreach (var t in tasks)
        {
            if (!t.IsCompleted) continue;
            var r = await t.ConfigureAwait(false);
            if (r.Rejected) { onRejected?.Invoke(r.Channel); continue; }
            if (r.Ok && winner is null) winner = r.Channel;
        }
        if (winner is null) return null;   // 全败/未决：退回现行链路
        if (!validate(winner)) return null; // 换台作废：竞速结果不入账
        return winner;
    }
}

/// <summary>
/// 并行抢首包判定（冻结稿 D，默认关）：陪跑第二实例 3 秒内首帧到齐即抢跑提交；
/// 3 秒到点或主画面先亮即断陪跑；陪跑被拒退避回竞速（本台本轮不再抢包）。
/// 纯逻辑判定，第二实例 Prepare/Commit/Abort 复用 LiveVideoHost 既有口径。
/// </summary>
public static class FirstPacketPolicy
{
    /// <summary>陪跑容忍窗口（毫秒）：到点即断，绝不白耗带宽。</summary>
    public const int CompanionWindowMs = 3000;

    public enum Decision { KeepWaiting, CompanionWins, CutCompanion }

    /// <summary>
    /// 判定：companionFrameShown=陪跑首帧已到；mainFrameShown=主画面已亮；
    /// elapsedMs=陪跑已耗时。陪跑先亮且未超窗 → CompanionWins（提交切换）；
    /// 超窗或主画面已亮 → CutCompanion（陪跑即断）；窗口内都未亮 → KeepWaiting。
    /// </summary>
    public static Decision Decide(bool companionFrameShown, bool mainFrameShown, long elapsedMs)
    {
        if (companionFrameShown && elapsedMs <= CompanionWindowMs && !mainFrameShown)
            return Decision.CompanionWins;
        if (mainFrameShown || elapsedMs >= CompanionWindowMs)
            return Decision.CutCompanion;
        return Decision.KeepWaiting;
    }

    /// <summary>陪跑被拒（403/429）：退避回竞速——本轮抢包作废，交回 B 竞速口径。</summary>
    public const string RejectBackoffReason = "陪跑被拒，退回竞速";
}

/// <summary>
/// DNS 优选（冻结稿 F，默认开）：公共 DoH（阿里 dns.alidns.com）解析 + TTL 缓存，
/// 失败静默回落系统 DNS（返回空表，调用方走原生解析，绝不阻塞取流）。
/// </summary>
public static class DnsPick
{
    /// <summary>DoH 端点（阿里公共 DNS，免费公共基础设施）：{0}=域名。</summary>
    public const string DohEndpoint = "https://dns.alidns.com/resolve?name={0}&type=A";

    /// <summary>解析结果缓存时长上限（毫秒）：实际取应答 TTL 与此较小值。</summary>
    public const long CacheCapMs = 5 * 60_000;

    private sealed class CachedIps { public string[] Ips = Array.Empty<string>(); public long ExpiresAtMs; }
    private static readonly ConcurrentDictionary<string, CachedIps> _cache = new();

    /// <summary>解析 JSON 应答（DoH RFC8484 简化口径）：取 Answer 数组中 type=1(A) 的 data 字段，去重。坏数据返回空表。</summary>
    public static string[] ParseAnswer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return answers.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object
                            && a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 1
                            && a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                .Select(a => a.GetProperty("data").GetString() ?? "")
                .Where(ip => !string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                .Distinct()
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>(); // 坏应答静默回落系统 DNS
        }
    }

    /// <summary>应答 TTL（Answer 首条，缺省 300s）。</summary>
    public static long AnswerTtlMs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 300_000;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in answers.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("TTL", out var ttl) && ttl.ValueKind == JsonValueKind.Number)
                        return Math.Clamp(ttl.GetInt64() * 1000, 1000, CacheCapMs);
            }
        }
        catch (Exception) { }
        return 300_000;
    }

    /// <summary>解析该域可用 IP（TTL 缓存内直接返回；否则经 fetch 查 DoH；任何失败返回空表静默回落系统 DNS）。</summary>
    public static async Task<string[]> ResolveAsync(string host, Func<string, Task<string?>> fetch, long nowMs)
    {
        if (string.IsNullOrEmpty(host)) return Array.Empty<string>();
        if (_cache.TryGetValue(host, out var hit) && nowMs < hit.ExpiresAtMs)
            return hit.Ips;
        string? body = null;
        try { body = await fetch(host).ConfigureAwait(false); }
        catch (Exception) { body = null; } // 静默：DoH 失败回落系统 DNS
        var ips = ParseAnswer(body);
        if (ips.Length > 0)
            _cache[host] = new CachedIps { Ips = ips, ExpiresAtMs = nowMs + Math.Min(AnswerTtlMs(body), CacheCapMs) };
        return ips; // 空=回落系统 DNS
    }

    /// <summary>清缓存（测试与故障兜底）。</summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// v1.0.20 方案 E·换路（重连换路只治真网络级断流）：清该域 DoH 解析缓存 + 该域已知 IP 延迟记账，
    /// 下次重连强制重新解析换 IP/线路；403 类服务器拒绝不经此路（分开记账，见 SourceBlackoutLedger）。
    /// </summary>
    public static void RefreshRoute(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        if (_cache.TryRemove(host, out var cached))
        {
            foreach (var ip in cached.Ips) _ipLatencyMs.TryRemove(ip, out _); // 该域已知 IP 延迟记账一并清零
        }
    }

    // ---- IP 测速择路（冻结稿 G）：按实测连接延迟记账择优 ----

    private static readonly ConcurrentDictionary<string, long> _ipLatencyMs = new(); // ip → 最近一次连接耗时

    /// <summary>记录一次 IP 连接实测延迟。</summary>
    public static void RecordIpLatency(string ip, long ms) => _ipLatencyMs[ip] = Math.Max(0, ms);

    /// <summary>记账延迟查询（无记录 long.MaxValue=未测）。</summary>
    public static long IpLatency(string ip) =>
        _ipLatencyMs.TryGetValue(ip, out var ms) ? ms : long.MaxValue;

    /// <summary>纯逻辑择路：候选 IP 按实测延迟升序；全部未测保持应答顺序（DoH 已按权重排好）。</summary>
    public static string[] OrderIps(string[] ips) =>
        ips.OrderBy(IpLatency).ToArray();
}

/// <summary>
/// v1.0.20 方案 E·换路账本（纯逻辑）：同域网络级失败（SSL/断流/超时）计数，阈值 2 触发换路
/// （清 DoH 缓存 + 清该域 IP 延迟记账），成功即清账。403 类服务器明确拒绝不进本账本——
/// 换路救不了源侧拒绝，403 走 A 源级死判（SourceBlackoutLedger）分开记账，互不混淆。
/// </summary>
public static class StreamRouteLedger
{
    /// <summary>同域连续网络级失败多少次触发换路（v2 细则：阈值=2）。</summary>
    public const int FailureThreshold = 2;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _fails = new(StringComparer.Ordinal);

    /// <summary>网络级失败回灌：达阈值触发换路（清 DoH 缓存+该域 IP 记账）并清零计数。返回本次是否触发了换路。</summary>
    public static bool NoteStreamFailure(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        int n = _fails.AddOrUpdate(host, 1, (_, v) => v + 1);
        if (n >= FailureThreshold)
        {
            DnsPick.RefreshRoute(host);
            _fails[host] = 0;
            return true;
        }
        return false;
    }

    /// <summary>成功回灌：清账（链路通了，路由不动）。</summary>
    public static void NoteStreamSuccess(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        _fails.TryRemove(host, out _);
    }

    /// <summary>该域当前失败计数（断言/诊断用）。</summary>
    public static int FailCount(string host) => _fails.TryGetValue(host, out var v) ? v : 0;

    /// <summary>清空账本（重启内核/诊断用）。</summary>
    public static void ClearAll() => _fails.Clear();
}

/// <summary>
/// IP 择路连接处理器（冻结稿 G）：SocketsHttpHandler.ConnectCallback 指定实测最优 IP 连接，
/// TLS 仍按原域名校验（SslClientAuthenticationOptions 不改）；任何异常回落系统解析，绝不连死。
/// </summary>
public sealed class DnsRouteHandler
{
    /// <summary>指定 IP 连接超时（毫秒）。</summary>
    public const int ConnectTimeoutMs = 800;

    /// <summary>DoH 域自身不走路由（防递归）。</summary>
    public const string DohHost = "dns.alidns.com";

    private readonly Func<long> _nowMs;
    private readonly Func<string, Task<string?>> _dohFetch;

    private static readonly HttpClient DohHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    public DnsRouteHandler(Func<long>? nowMs = null, Func<string, Task<string?>>? dohFetch = null)
    {
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _dohFetch = dohFetch ?? (async host =>
        {
            try { return await DohHttp.GetStringAsync(string.Format(DnsPick.DohEndpoint, host)).ConfigureAwait(false); }
            catch (Exception) { return null; } // 静默：DoH 失败回落系统 DNS
        });
    }

    /// <summary>为 HttpClient 装配连接回调（handler 由调用方建好传入，便于复用池参数）。</summary>
    public void Bind(SocketsHttpHandler handler)
    {
        handler.ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            if (string.Equals(host, DohHost, StringComparison.OrdinalIgnoreCase))
                return await DefaultConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false); // DoH 不路由
            try
            {
                long start = _nowMs();
                var ips = await DnsPick.ResolveAsync(host, _dohFetch, _nowMs()).ConfigureAwait(false);
                if (ips.Length == 0)
                    return await DefaultConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false); // 静默回落系统 DNS
                foreach (var ip in DnsPick.OrderIps(ips))
                {
                    try
                    {
                        var ep = new DnsEndPoint(ip, ctx.DnsEndPoint.Port);
                        using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                        var stream = await DefaultConnectAsync(ep, linked.Token).ConfigureAwait(false);
                        DnsPick.RecordIpLatency(ip, _nowMs() - start);
                        return stream;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; } // 单 IP 超时换下一个
                    catch (System.IO.IOException) { continue; }
                    catch (SocketException) { continue; }
                }
                return await DefaultConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false); // 全 IP 不通：系统解析兜底
            }
            catch (Exception)
            {
                return await DefaultConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false); // 任何异常：系统解析兜底
            }
        };
    }

    /// <summary>系统解析连接（兜底路径）：与原生 SocketsHttpHandler 行为同口径。</summary>
    private static async ValueTask<Stream> DefaultConnectAsync(DnsEndPoint ep, CancellationToken ct)
    {
        var addrs = await Dns.GetHostAddressesAsync(ep.Host, ct).ConfigureAwait(false);
        foreach (var addr in addrs)
        {
            var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(addr, ep.Port, ct).ConfigureAwait(false);
                return new NetworkStream(s, ownsSocket: true);
            }
            catch (Exception) { s.Dispose(); }
        }
        throw new SocketException((int)SocketError.HostUnreachable); // 系统解析也不通：按原语义抛给上层
    }
}
