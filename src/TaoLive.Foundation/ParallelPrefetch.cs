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

/// <summary>
/// 源脾气账（vc94 并发拉流批，纯逻辑）：per-host 保守标记，429/403 被拒即记脾气，
/// 冷却 <see cref="DefaultCooldownMs"/> 内该 host 不再并发预取（降回单连接，防封禁升级）。
/// 单调钟注入，快测不真等。
/// </summary>
public sealed class TemperLedger
{
    public const int DefaultCooldownMs = 30 * 60 * 1000;

    private readonly Func<long> _monotonicMs;
    private readonly int _cooldownMs;
    private readonly Dictionary<string, long> _markedAtMs = new(StringComparer.OrdinalIgnoreCase);

    public TemperLedger(Func<long>? monotonicMs = null, int cooldownMs = DefaultCooldownMs)
    {
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
        _cooldownMs = cooldownMs;
    }

    public void MarkConservative(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        _markedAtMs[host] = _monotonicMs();
    }

    /// <summary>该 host 是否仍在冷却期（保守不并发）。</summary>
    public bool IsConservative(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        return _markedAtMs.TryGetValue(host, out var at)
            && _monotonicMs() - at < _cooldownMs;
    }

    public void Reset() => _markedAtMs.Clear();

    /// <summary>URL 取 host（解析失败返回原 URL，账不丢）。</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? u.Host
            : url;
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
/// 并发预取器（vc94 激进版，老板 2026-11-26 拍板，纯逻辑可测）：
/// - 切台起播即开多连接、播放中一直保持、<b>水满才撤</b>：水位账=内核读数（waterProbeMs 注入）+缓存领先量，
///   ≥watermark 即停发新请求，在途不撤（水满自然收敛）；
/// - 连接封顶 <see cref="MaxExtraConnections"/>=2（总 3 含内核在播那路）；
/// - 429/403 被拒 → <see cref="ReportFailed">(rejected:true) 记源脾气，冷却期内该 host 降回单连接；
/// - 清单换代（generation++）作废在途与旧缓存，不串台；
/// - 缓存字节硬顶 <see cref="CacheByteCap"/>，顶满不再收新片（内存红线）。
/// 线程安全：loader 线程（Open/Read）与后台下载任务并发进出，单锁管账；事件在锁外发（防回调死锁）。
/// </summary>
public sealed class ParallelPrefetcher
{
    /// <summary>额外连接封顶（不含内核在播那路）：总 3 个，激进版上限。</summary>
    public const int MaxExtraConnections = 2;

    /// <summary>缓存字节硬顶：24MB（老板拍板口径），顶满不再收新片。</summary>
    public const long CacheByteCap = 24 * 1024 * 1024;

    private readonly long _watermarkMs;
    private readonly Func<long>? _waterProbeMs;
    private readonly TemperLedger _temper;
    private readonly object _gate = new();

    private List<HlsSegmentInfo> _segments = new();
    private int _generation;
    private int _consumedIndex = -1;
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private long _cacheBytes;

    /// <summary>预取请求出口：参数=URL+代次。壳层起后台下载，成败回报 ReportFetched/ReportFailed。</summary>
    public event EventHandler<FetchRequest>? FetchRequested;

    public ParallelPrefetcher(long watermarkMs, Func<long>? waterProbeMs = null, TemperLedger? temper = null)
    {
        _watermarkMs = watermarkMs;
        _waterProbeMs = waterProbeMs;
        _temper = temper ?? new TemperLedger();
    }

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
            lock (_gate)
            {
                long probe = 0;
                try { probe = _waterProbeMs?.Invoke() ?? 0; }
                catch { probe = 0; } // 探针异常按无读数，不挡预取
                return probe + CacheAheadMsLocked();
            }
        }
    }

    /// <summary>
    /// 清单换代：解析成功即换代（generation++），旧缓存与在途账按新清单重整——
    /// 不在新清单里的缓存条目作废，consumed 按已消费 URL 在新清单中的位置续算（找不到归 -1）。
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
            _consumedIndex = consumedUrl is null
                ? -1
                : _segments.FindIndex(s => string.Equals(s.Url, consumedUrl, StringComparison.Ordinal));
            // 旧缓存按新清单重整：不在新清单的条目作废（换代作废，不串台）
            var valid = _segments.Select(s => s.Url).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _cache.Keys.Where(u => !valid.Contains(u)).ToList())
            {
                _cacheBytes -= _cache[stale].LongLength;
                _cache.Remove(stale);
            }
            if (_cacheBytes < 0) _cacheBytes = 0;
        }
        Pump();
        return true;
    }

    /// <summary>消费记账：播放吃到某分片（缓存命中或走内层），consumed 前推（幂等，不回退）。</summary>
    public void NotifyConsumed(string url)
    {
        List<FetchRequest> requests;
        lock (_gate)
        {
            var idx = _segments.FindIndex(s => string.Equals(s.Url, url, StringComparison.Ordinal));
            if (idx > _consumedIndex) _consumedIndex = idx;
            requests = CollectRequestsLocked();
        }
        Raise(requests);
    }

    /// <summary>
    /// 取缓存分片：命中即服务（字节移交调用方）并前推 consumed；未命中走内核正常拉流。
    /// </summary>
    public bool TryTake(string url, out byte[]? data)
    {
        List<FetchRequest> requests;
        lock (_gate)
        {
            if (_cache.TryGetValue(url, out var bytes))
            {
                _cache.Remove(url);
                _cacheBytes -= bytes.LongLength;
                if (_cacheBytes < 0) _cacheBytes = 0;
                var idx = _segments.FindIndex(s => string.Equals(s.Url, url, StringComparison.Ordinal));
                if (idx > _consumedIndex) _consumedIndex = idx;
                data = bytes;
                requests = CollectRequestsLocked();
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

    /// <summary>预取下载成功回报：对上代次且仍在新清单才入缓存（换代作废在途）；顶满字节硬顶即弃。</summary>
    public void ReportFetched(string url, byte[] data, int generation)
    {
        List<FetchRequest> requests;
        lock (_gate)
        {
            _inFlight.Remove(url);
            if (generation != _generation
                || !_segments.Any(s => string.Equals(s.Url, url, StringComparison.Ordinal))
                || _cacheBytes + data.LongLength > CacheByteCap)
            {
                requests = CollectRequestsLocked(); // 换代作废/清单外/顶满：不入账
            }
            else
            {
                _cache[url] = data;
                _cacheBytes += data.LongLength;
                requests = CollectRequestsLocked();
            }
        }
        Raise(requests);
    }

    /// <summary>预取下载失败回报：rejected（429/403 被拒）记源脾气，冷却期内该 host 不再并发。</summary>
    public void ReportFailed(string url, bool rejected, int generation)
    {
        List<FetchRequest> requests;
        lock (_gate)
        {
            _inFlight.Remove(url);
            if (rejected) _temper.MarkConservative(TemperLedger.HostOf(url));
            requests = CollectRequestsLocked();
        }
        Raise(requests);
    }

    /// <summary>
    /// 泵：按水位账与封顶补发预取请求——水满才撤（≥watermark 不发新），封顶 2 路，host 保守期跳过，
    /// 缓存字节顶满停发。事件在锁外发。
    /// </summary>
    public void Pump()
    {
        List<FetchRequest> requests;
        lock (_gate)
        {
            requests = CollectRequestsLocked();
        }
        Raise(requests);
    }

    /// <summary>整账清零（Play/Stop/换内核口径）：缓存、在途、consumed、脾气全清。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _segments = new List<HlsSegmentInfo>();
            _consumedIndex = -1;
            _cache.Clear();
            _cacheBytes = 0;
            _inFlight.Clear();
            _temper.Reset();
        }
    }

    /// <summary>host 保守判定（测试与壳层可见）。</summary>
    public bool IsHostConservative(string host) => _temper.IsConservative(host);

    /// <summary>锁内收集下一批预取请求：补齐到封顶数为止。</summary>
    private List<FetchRequest> CollectRequestsLocked()
    {
        var requests = new List<FetchRequest>();
        if (_segments.Count == 0) return requests;
        if (EffectiveWaterMs >= _watermarkMs) return requests; // 水满才撤：不再发新
        if (_cacheBytes >= CacheByteCap) return requests;       // 字节硬顶：停发

        var generation = _generation;
        foreach (var seg in _segments.Skip(Math.Max(0, _consumedIndex + 1)))
        {
            if (_inFlight.Count >= MaxExtraConnections) break;
            if (_cache.ContainsKey(seg.Url) || _inFlight.Contains(seg.Url)) continue;
            if (_temper.IsConservative(TemperLedger.HostOf(seg.Url))) continue; // 源脾气：降回单连接
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
