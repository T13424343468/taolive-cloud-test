using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.Foundation;

/// <summary>清单分片条目：URL（绝对地址）+ 时长（秒，EXTINF 解析；解析不出按 0）。</summary>
public readonly struct HlsSegmentInfo
{
    public HlsSegmentInfo(string url, double durationSec)
    {
        Url = url;
        DurationSec = durationSec;
    }

    public string Url { get; }
    public double DurationSec { get; }
}

/// <summary>
/// HLS 清单解析器（vc94 并发拉流批，纯逻辑可测）：
/// - TryParse：媒体清单 EXTINF+分片 URL（相对地址按 baseUri 归一），供预取器排片；
/// - TryParseVariantBandwidths：主清单 BANDWIDTH 列表，供低档起播取最低档（vc94 第 1 刀）。
/// 只做纯解析，零 IO、零内核依赖。
/// </summary>
public static class HlsPlaylistInspector
{
    /// <summary>解析媒体清单分片表。主清单（无 EXTINF）返回 false；baseUri 非法时分片须自带绝对地址。</summary>
    public static bool TryParse(string? playlistText, string? baseUri, out List<HlsSegmentInfo> segments)
    {
        segments = new List<HlsSegmentInfo>();
        if (string.IsNullOrWhiteSpace(playlistText)) return false;

        Uri? baseU = null;
        if (!string.IsNullOrWhiteSpace(baseUri)
            && Uri.TryCreate(baseUri, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            baseU = u;
        }

        double pendingDurationSec = -1;
        bool isMaster = false;
        foreach (var raw in playlistText!.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                isMaster = true; // 主清单：URI 行是变体清单地址，不进分片表
                continue;
            }
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                var body = colon >= 0 ? line[(colon + 1)..] : line;
                var comma = body.IndexOf(',');
                var num = comma >= 0 ? body[..comma] : body;
                _ = double.TryParse(num.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out pendingDurationSec);
                continue;
            }
            if (line.StartsWith("#")) continue; // 其余标签行跳过
            if (isMaster) continue;
            if (pendingDurationSec < 0) continue; // 无 EXTINF 先行的裸行不按分片收（防注释残行误入）

            var url = line;
            if (baseU is { } bu && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(bu, line, out var resolved))
            {
                url = resolved.ToString();
            }
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                segments.Add(new HlsSegmentInfo(url, pendingDurationSec));
            }
            pendingDurationSec = -1;
        }
        return segments.Count > 0;
    }

    /// <summary>解析主清单变体带宽表（BANDWIDTH，bps）。媒体清单/无带宽标注返回 false——低档起播只认显式多档，单档流不降档（防黑屏）。</summary>
    public static bool TryParseVariantBandwidths(string? playlistText, out List<long> bandwidths)
    {
        bandwidths = new List<long>();
        if (string.IsNullOrWhiteSpace(playlistText)) return false;
        foreach (var raw in playlistText!.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
            const string key = "BANDWIDTH=";
            var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var digits = new string(line[(idx + key.Length)..].TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && long.TryParse(digits, out var bw) && bw > 0)
            {
                bandwidths.Add(bw);
            }
        }
        return bandwidths.Count > 0;
    }
}

/// <summary>
/// 低档起播升档判据（vc94 第 1 刀，纯常量+纯函数）：
/// 升档条件=水位攒到 <see cref="RampUpWaterMs"/>（15 秒）或低档持有满 <see cref="MaxHoldMs"/>（20 秒）——
/// 水位优先（链路好早点升画质），持时兜底（慢链路不永久锁低档）。
/// </summary>
public static class StartupGear
{
    /// <summary>升档水位线（毫秒）：缓冲读数达到即升档。</summary>
    public const int RampUpWaterMs = 15_000;

    /// <summary>低档最长持有（毫秒）：到点强制升档，慢链路不永久锁低档。</summary>
    public const int MaxHoldMs = 20_000;

    public static bool ShouldLift(long bufferedMs, long heldMs) =>
        bufferedMs >= RampUpWaterMs || heldMs >= MaxHoldMs;
}

/// <summary>预取请求（FetchRequested 事件载荷）：Android 壳层据此起后台下载。</summary>
public readonly struct FetchRequest
{
    public FetchRequest(string url, int generation)
    {
        Url = url;
        Generation = generation;
    }

    public string Url { get; }
    public int Generation { get; }
}

/// <summary>
/// 并发预取器（vc96 试探版，老板 2026-11-26 拍板「试探性并发/延迟性」，纯逻辑可测）：
/// - <b>默认单路起步</b>：切台起播只开内核在播那 1 路，试探档位 0=不预开任何并发；
/// - <b>不够才加、逐路试探</b>：水位采样（<see cref="ProbeSampleIntervalMs"/> 间隔）发现水位不涨
///   （下载追不上播放）且距上次升级满 <see cref="EscalateCooldownMs"/> 观察期，才升一档多发一路；
///   封顶 <see cref="MaxExtraConnections"/>=2（总 3 含在播那路）；
/// - <b>被拒即缩</b>：429/403/被踢 → 档位归零退回单路，源脾气入 <see cref="SourceTemperStore"/>
///   （持久化，重启不清零——下次调度直接按账定打法；好源记好账，下次允许直接试探）；
/// - <b>水满收兵</b>：水位 ≥watermark 停发新请求，在途不撤（水满自然收敛）；
/// - 清单换代（generation++）作废在途与旧缓存，不串台；
/// - 缓存字节硬顶 <see cref="CacheByteCap"/>，顶满不再收新片（内存红线）。
/// 线程安全：loader 线程（Open/Read）与后台下载任务并发进出，单锁管账；事件在锁外发（防回调死锁）。
/// </summary>
public sealed class ParallelPrefetcher
{
    /// <summary>
    /// 试探并发总开关（vc96，默认开）：试探语义本身防封（默认单路、逐路升级、被拒即缩）；
    /// 置 false 退回纯单路（工厂 null 分支，链路不含预取壳），装配点在内核 BuildPlayer。
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>额外连接封顶（不含内核在播那路）：总 3 个，试探版上限。</summary>
    public const int MaxExtraConnections = 2;

    /// <summary>缓存字节硬顶：24MB（老板拍板口径），顶满不再收新片。</summary>
    public const long CacheByteCap = 24 * 1024 * 1024;

    /// <summary>水位采样间隔：两次采样至少隔 5 秒，趋势才作数（防瞬时抖动误升档）。</summary>
    public const long ProbeSampleIntervalMs = 5_000;

    /// <summary>逐路升级观察期：一档平安跑满 20 秒才准升下一档（延迟性，防连环加路触雷）。</summary>
    public const long EscalateCooldownMs = 20_000;

    private readonly long _watermarkMs;
    private readonly Func<long>? _waterProbeMs;
    private readonly SourceTemperStore? _temper; // 持久化源脾气（null=不记账，测试用）
    private readonly Func<long> _monotonicMs;
    private readonly object _gate = new();

    private List<HlsSegmentInfo> _segments = new();
    private int _generation;
    private int _consumedIndex = -1;
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private long _cacheBytes;

    // vc96 P1：非拒绝型失败退避账（per-URL 失败计数 + 指数退避）——否则失败片下轮 Pump 立即重发，热循环烧线
    private readonly Dictionary<string, (int Count, long UntilMs)> _failBackoff = new(StringComparer.Ordinal);

    // vc96 P3：已消费分片日志（防清单滚动后重放已播——consumed URL 滚出清单时按日志定位续点）
    private readonly List<string> _consumedLog = new();

    // 试探账：档位 + 水位趋势采样 + 升级观察期
    private int _probeLevel; // 0=单路（不预开并发）..MaxExtraConnections
    private long _lastWaterMs = -1;
    private long _lastSampleAtMs = long.MinValue;
    private long _lastEscalateAtMs = long.MinValue;

    /// <summary>预取请求出口：参数=URL+代次。壳层起后台下载，成败回报 ReportFetched/ReportFailed。</summary>
    public event EventHandler<FetchRequest>? FetchRequested;

    public ParallelPrefetcher(long watermarkMs, Func<long>? waterProbeMs = null,
        SourceTemperStore? temper = null, Func<long>? monotonicMs = null)
    {
        _watermarkMs = watermarkMs;
        _waterProbeMs = waterProbeMs;
        _temper = temper;
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
    }

    /// <summary>当前试探档位（0=单路；断言与测试用）。</summary>
    public int ProbeLevel { get { lock (_gate) return _probeLevel; } }

    /// <summary>当前代次（清单换代 ++；回报对不上代次即弃）。</summary>
    public int Generation { get { lock (_gate) return _generation; } }

    /// <summary>在途预取连接数（封顶断言用）。</summary>
    public int ActiveFetches { get { lock (_gate) return _inFlight.Count; } }

    /// <summary>缓存字节总量。</summary>
    public long CacheBytes { get { lock (_gate) return _cacheBytes; } }

    /// <summary>水位账（毫秒）：内核读数 + 缓存领先分片时长和。</summary>
    public long EffectiveWaterMs
    {
        get
        {
            // vc96 第 5 轮：外部回调不进锁（本文件 P3 纪律，ReadProbe 同法）——探针先锁外读，再进锁取缓存领先账
            long probe = ReadProbe();
            lock (_gate)
            {
                return probe + CacheAheadMsLocked();
            }
        }
    }

    /// <summary>非拒绝失败退避起步（毫秒）：首次失败歇 2 秒再试。</summary>
    public const long FailBackoffBaseMs = 2_000;

    /// <summary>非拒绝失败退避封顶（毫秒）：连续失败指数退避最深 5 分钟（防坏片热循环烧线）。</summary>
    public const long FailBackoffMaxMs = 5 * 60_000;

    /// <summary>
    /// 清单换代：解析成功即换代（generation++），旧缓存与在途账按新清单重整——
    /// 不在新清单里的缓存条目作废，consumed 按已消费 URL 在新清单中的位置续算（找不到按消费日志定位，防重放已播）。
    /// 解析失败原样不动（清单残包不掀桌）。
    /// </summary>
    public bool UpdatePlaylist(string? playlistText, string? baseUri)
    {
        List<HlsSegmentInfo> parsed;
        lock (_gate)
        {
            if (!HlsPlaylistInspector.TryParse(playlistText, baseUri, out var segments)) return false;
            parsed = segments;
            var consumedUrl = _consumedIndex >= 0 && _consumedIndex < _segments.Count
                ? _segments[_consumedIndex].Url
                : null;
            _segments = parsed;
            _generation++;
            // consumed 续算：优先按当前 consumed URL 定位；滚出清单（找不到）时按消费日志取新清单中
            // 已消费过的最深位置（vc96 P3：防清单滚动后从头重放已播分片）
            var idx = consumedUrl is null
                ? -1
                : _segments.FindIndex(s => string.Equals(s.Url, consumedUrl, StringComparison.Ordinal));
            if (idx < 0)
            {
                for (var i = _segments.Count - 1; i >= 0; i--)
                {
                    if (!_consumedLog.Contains(_segments[i].Url)) continue;
                    idx = i;
                    break;
                }
            }
            _consumedIndex = idx;
            // 旧缓存按新清单重整：不在新清单的条目作废（换代作废，不串台）；退避账同步修剪
            var valid = _segments.Select(s => s.Url).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _cache.Keys.Where(u => !valid.Contains(u)).ToList())
            {
                _cacheBytes -= _cache[stale].LongLength;
                _cache.Remove(stale);
            }
            if (_cacheBytes < 0) _cacheBytes = 0;
            foreach (var stale in _failBackoff.Keys.Where(u => !valid.Contains(u)).ToList())
            {
                _failBackoff.Remove(stale);
            }
        }
        Pump();
        return true;
    }

    /// <summary>消费记账：播放吃到某分片（缓存命中或走内层），consumed 前推（幂等，不回退）。</summary>
    public void NotifyConsumed(string url)
    {
        List<FetchRequest> requests;
        var probe = ReadProbe();
        lock (_gate)
        {
            var idx = _segments.FindIndex(s => string.Equals(s.Url, url, StringComparison.Ordinal));
            if (idx > _consumedIndex) _consumedIndex = idx;
            LogConsumedLocked(url);
            requests = CollectRequestsLocked(probe);
        }
        Raise(requests);
    }

    /// <summary>
    /// 取缓存分片：命中即服务（字节移交调用方）并前推 consumed；未命中走内核正常拉流。
    /// </summary>
    public bool TryTake(string url, out byte[]? data)
    {
        List<FetchRequest> requests;
        var probe = ReadProbe();
        lock (_gate)
        {
            if (_cache.TryGetValue(url, out var bytes))
            {
                _cache.Remove(url);
                _cacheBytes -= bytes.LongLength;
                if (_cacheBytes < 0) _cacheBytes = 0;
                var idx = _segments.FindIndex(s => string.Equals(s.Url, url, StringComparison.Ordinal));
                if (idx > _consumedIndex) _consumedIndex = idx;
                LogConsumedLocked(url);
                data = bytes;
                requests = CollectRequestsLocked(probe);
            }
            else
            {
                data = null;
                requests = new List<FetchRequest>();
            }
        }
        Raise(requests);
        return data is not null;
    }

    /// <summary>预取下载成功回报：对上代次且仍在新清单才入缓存（换代作废在途）；顶满字节硬顶即弃。
    /// vc96：试探平安记好账（好源下次调度允许直接试探）；成功即清该 URL 退避账。</summary>
    public void ReportFetched(string url, byte[] data, int generation)
    {
        List<FetchRequest> requests;
        var probe = ReadProbe();
        lock (_gate)
        {
            _inFlight.Remove(url);
            _failBackoff.Remove(url); // 成功清退避：好片不再背失败账
            if (generation != _generation
                || !_segments.Any(s => string.Equals(s.Url, url, StringComparison.Ordinal))
                || _cacheBytes + data.LongLength > CacheByteCap)
            {
                requests = CollectRequestsLocked(probe); // 换代作废/清单外/顶满：不入账
            }
            else
            {
                if (_cache.TryGetValue(url, out var old)) _cacheBytes -= old.LongLength; // 覆盖写先扣旧账（vc96 P3：不虚增）
                _cache[url] = data;
                _cacheBytes += data.LongLength;
                _temper?.MarkGood(SourceTemperStore.HostOf(url)); // 试探平安：好账+1
                requests = CollectRequestsLocked(probe);
            }
        }
        Raise(requests);
    }

    /// <summary>预取下载失败回报：rejected（429/403/被踢）即缩——档位归零退回单路，
    /// 源脾气入持久化账（保守期内该 host 不再试探）；非拒绝失败按 per-URL 指数退避（vc96 P1：
    /// 否则失败片下轮 Pump 立即重发，坏片热循环烧线）。</summary>
    public void ReportFailed(string url, bool rejected, int generation)
    {
        List<FetchRequest> requests;
        var probe = ReadProbe();
        lock (_gate)
        {
            _inFlight.Remove(url);
            if (rejected)
            {
                _probeLevel = 0; // 被拒即缩：全部退回单路
                _lastEscalateAtMs = _monotonicMs(); // 观察期重新起算，防立刻再升
                _temper?.MarkRejected(SourceTemperStore.HostOf(url));
            }
            else
            {
                var now = _monotonicMs();
                var count = _failBackoff.TryGetValue(url, out var b) ? b.Count + 1 : 1;
                var backoff = Math.Min(FailBackoffBaseMs << Math.Min(count - 1, 20), FailBackoffMaxMs);
                _failBackoff[url] = (count, now + backoff);
            }
            requests = CollectRequestsLocked(probe);
        }
        Raise(requests);
    }

    /// <summary>
    /// 泵：按试探档位与水位账补发预取请求——默认单路（档位 0 不发并发），
    /// 水位不涨才逐路升级（观察期满），水满（≥watermark）不发新，host 保守期跳过，
    /// 缓存字节顶满停发。事件在锁外发。
    /// </summary>
    public void Pump()
    {
        List<FetchRequest> requests;
        var probe = ReadProbe();
        lock (_gate)
        {
            requests = CollectRequestsLocked(probe);
        }
        Raise(requests);
    }

    /// <summary>整账清零（Play/Stop/换内核口径）：缓存、在途、consumed、试探档位全清；
    /// 源脾气为持久化账，不随 Reset 清（跨启动调度依据）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _segments = new List<HlsSegmentInfo>();
            _consumedIndex = -1;
            _cache.Clear();
            _cacheBytes = 0;
            _inFlight.Clear();
            _failBackoff.Clear();
            _consumedLog.Clear();
            _probeLevel = 0;
            _lastWaterMs = -1;
            _lastSampleAtMs = long.MinValue;
            _lastEscalateAtMs = long.MinValue;
        }
    }

    /// <summary>host 保守判定（测试与壳层可见；无账=不保守）。</summary>
    public bool IsHostConservative(string host) => _temper?.IsConservative(host) ?? false;

    /// <summary>水位探针锁外读数（vc96 P3：外部回调不进锁，防回调死锁/长尾拖住账锁）。</summary>
    private long ReadProbe()
    {
        try { return _waterProbeMs?.Invoke() ?? 0; }
        catch { return 0; } // 探针异常按无读数，不挡预取
    }

    /// <summary>已消费分片入日志（锁内）：滚动上限 128 条，防长播内存无界。</summary>
    private void LogConsumedLocked(string url)
    {
        _consumedLog.Add(url);
        if (_consumedLog.Count > 128) _consumedLog.RemoveRange(0, _consumedLog.Count - 128);
    }

    /// <summary>锁内收集下一批预取请求：先跑试探账（水位趋势→逐路升级），再按档位补发。
    /// probe 由调用方锁外读入（vc96 P3：外部探针回调不进锁）。</summary>
    private List<FetchRequest> CollectRequestsLocked(long probe)
    {
        var requests = new List<FetchRequest>();
        if (_segments.Count == 0) return requests;

        // ---- 试探账：水位采样与逐路升级（延迟触发） ----
        var now = _monotonicMs();
        var water = probe + CacheAheadMsLocked();
        var firstSample = _lastSampleAtMs == long.MinValue;
        var sampled = firstSample || now - _lastSampleAtMs >= ProbeSampleIntervalMs;
        var falling = sampled && !firstSample && water <= _lastWaterMs; // 不涨（含持平）=单路供不上
        if (sampled)
        {
            _lastWaterMs = water;
            _lastSampleAtMs = now;
        }
        if (falling && water < _watermarkMs
            && _probeLevel < MaxExtraConnections
            && (_lastEscalateAtMs == long.MinValue || now - _lastEscalateAtMs >= EscalateCooldownMs))
        {
            _probeLevel++; // 水位不涨=单路供不上：试探加一路
            _lastEscalateAtMs = now;
        }

        if (water >= _watermarkMs) return requests;            // 水满才撤：不再发新
        if (_cacheBytes >= CacheByteCap) return requests;       // 字节硬顶：停发

        var generation = _generation;
        foreach (var seg in _segments.Skip(Math.Max(0, _consumedIndex + 1)))
        {
            if (_inFlight.Count >= _probeLevel) break;          // 试探档位封顶：0=单路不预开
            if (_cache.ContainsKey(seg.Url) || _inFlight.Contains(seg.Url)) continue;
            if (_failBackoff.TryGetValue(seg.Url, out var fb) && now < fb.UntilMs) continue; // 退避未到期：不再发
            if (_consumedLog.Contains(seg.Url)) continue;       // 已播过的分片不重放
            if (_temper is { } t && t.IsConservative(SourceTemperStore.HostOf(seg.Url))) continue; // 保守期：不试探
            _inFlight.Add(seg.Url);
            requests.Add(new FetchRequest(seg.Url, generation));
        }
        return requests;
    }

    /// <summary>缓存领先量（毫秒）：consumed 之后已缓存分片时长和（水位账的本地部分）。</summary>
    private long CacheAheadMsLocked()
    {
        double sec = _segments
            .Skip(Math.Max(0, _consumedIndex + 1))
            .Where(s => _cache.ContainsKey(s.Url))
            .Sum(s => s.DurationSec);
        return (long)(sec * 1000);
    }

    private void Raise(List<FetchRequest> requests)
    {
        foreach (var r in requests)
        {
            FetchRequested?.Invoke(this, r);
        }
    }
}
