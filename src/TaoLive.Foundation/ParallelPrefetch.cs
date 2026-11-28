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
/// 灌水试探引擎（vc140 水位大脑合并，老板 2026-11-28 拍板「大手术一次到位」）：
/// vc94 试探纪律原样移植（纯逻辑可测），账本唯一——磁盘水位由灌水器喂，本类只管档位与退避：
/// - <b>默认单路起步</b>：档位 0=灌水器只开自己那 1 路，不预开任何并发；
/// - <b>不够才加、逐路试探</b>：水位采样（<see cref="ProbeSampleIntervalMs"/> 间隔）发现水位不涨
///   （灌水追不上播放）且距上次升级满 <see cref="EscalateCooldownMs"/> 观察期，才升一档多发一路；
///   封顶 <see cref="MaxExtraConnections"/>=2（总 3 含灌水基本盘那路）；
/// - <b>被拒即缩</b>：429/403/被踢 → 档位归零退回单路，源脾气入 <see cref="SourceTemperStore"/>
///   （持久化，重启不清零；好源记好账，下次允许直接试探）；
/// - <b>水满收兵</b>：水位 ≥watermark 停发新路（档位保留，回落自然收敛）；
/// - <b>失败退避</b>：非拒绝失败按 per-URL 指数退避（2 秒起步封顶 5 分钟），坏片不热循环烧线。
/// 线程安全：单锁管账；回调在锁外发（防回调死锁）。
/// </summary>
public sealed class FillThrottle
{
    /// <summary>额外连接封顶（不含灌水基本盘那路）：总 3 个，试探版上限。</summary>
    public const int MaxExtraConnections = 2;

    /// <summary>水位采样间隔：两次采样至少隔 5 秒，趋势才作数（防瞬时抖动误升档）。</summary>
    public const long ProbeSampleIntervalMs = 5_000;

    /// <summary>逐路升级观察期：一档平安跑满 20 秒才准升下一档（延迟性，防连环加路触雷）。</summary>
    public const long EscalateCooldownMs = 20_000;

    /// <summary>非拒绝失败退避起步（毫秒）：首次失败歇 2 秒再试。</summary>
    public const long FailBackoffBaseMs = 2_000;

    /// <summary>非拒绝失败退避封顶（毫秒）：连续失败指数退避最深 5 分钟（防坏片热循环烧线）。</summary>
    public const long FailBackoffMaxMs = 5 * 60_000;

    private readonly long _watermarkMs;
    private readonly SourceTemperStore? _temper; // 持久化源脾气（null=不记账，测试用）
    private readonly Func<long> _monotonicMs;
    private readonly object _gate = new();

    private readonly Dictionary<string, (int Count, long UntilMs)> _failBackoff = new(StringComparer.Ordinal);

    // 试探账：档位 + 水位趋势采样 + 升级观察期
    private int _probeLevel; // 0=单路..MaxExtraConnections
    private long _lastWaterMs = -1;
    private long _lastSampleAtMs = long.MinValue;
    private long _lastEscalateAtMs = long.MinValue;

    public FillThrottle(long watermarkMs, SourceTemperStore? temper = null, Func<long>? monotonicMs = null)
    {
        _watermarkMs = watermarkMs;
        _temper = temper;
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
    }

    /// <summary>当前试探档位（0=单路；断言与测试用）。</summary>
    public int ProbeLevel { get { lock (_gate) return _probeLevel; } }

    /// <summary>host 保守判定（灌水器选路用；无账=不保守）。</summary>
    public bool IsHostConservative(string host) => _temper?.IsConservative(host) ?? false;

    /// <summary>该分片是否在失败退避期内（退避未到期不重发）。</summary>
    public bool IsBackedOff(string url)
    {
        lock (_gate)
        {
            return _failBackoff.TryGetValue(url, out var b) && _monotonicMs() < b.UntilMs;
        }
    }

    /// <summary>水位采样（灌水器每拍喂磁盘领先量）：趋势判定 + 逐路升级（水满不升）。</summary>
    public void OnSample(long waterMs)
    {
        lock (_gate)
        {
            var now = _monotonicMs();
            var firstSample = _lastSampleAtMs == long.MinValue;
            var sampled = firstSample || now - _lastSampleAtMs >= ProbeSampleIntervalMs;
            var falling = sampled && !firstSample && waterMs <= _lastWaterMs; // 不涨（含持平）=单路供不上
            if (sampled)
            {
                _lastWaterMs = waterMs;
                _lastSampleAtMs = now;
            }
            if (falling && waterMs < _watermarkMs
                && _probeLevel < MaxExtraConnections
                && (_lastEscalateAtMs == long.MinValue || now - _lastEscalateAtMs >= EscalateCooldownMs))
            {
                _probeLevel++; // 水位不涨=单路供不上：试探加一路
                _lastEscalateAtMs = now;
            }
        }
    }

    /// <summary>被拒回报（429/403/被踢）：档位归零退回单路，观察期重新起算，源脾气入持久化账。</summary>
    public void OnRejected(string url)
    {
        lock (_gate)
        {
            _probeLevel = 0;
            _lastEscalateAtMs = _monotonicMs();
        }
        _temper?.MarkRejected(SourceTemperStore.HostOf(url));
    }

    /// <summary>成功回报：好源记好账（下次允许直接试探），清该 URL 退避账。</summary>
    public void OnGood(string url)
    {
        lock (_gate)
        {
            _failBackoff.Remove(url);
        }
        _temper?.MarkGood(SourceTemperStore.HostOf(url));
    }

    /// <summary>非拒绝失败回报：per-URL 指数退避（2 秒起步封顶 5 分钟）。</summary>
    public void OnFailed(string url)
    {
        lock (_gate)
        {
            var now = _monotonicMs();
            var count = _failBackoff.TryGetValue(url, out var b) ? b.Count + 1 : 1;
            var backoff = Math.Min(FailBackoffBaseMs << Math.Min(count - 1, 20), FailBackoffMaxMs);
            _failBackoff[url] = (count, now + backoff);
        }
    }

    /// <summary>退避账重整（同台滚动清单口径）：滚出清单的退避条目作废（防长播字典无界）；
    /// 清单内条目原样保留（退避未到期不重发的纪律不因滚动刷新而破）。</summary>
    public void PruneBackoff(IEnumerable<string> validUrls)
    {
        lock (_gate)
        {
            var valid = new HashSet<string>(validUrls, StringComparer.Ordinal);
            var stale = new List<string>();
            foreach (var url in _failBackoff.Keys)
            {
                if (!valid.Contains(url)) stale.Add(url);
            }
            foreach (var url in stale) _failBackoff.Remove(url);
        }
    }

    /// <summary>整账清零（换台口径）：档位、采样、退避全清；源脾气为持久化账不清（跨启动调度依据）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _probeLevel = 0;
            _lastWaterMs = -1;
            _lastSampleAtMs = long.MinValue;
            _lastEscalateAtMs = long.MinValue;
            _failBackoff.Clear();
        }
    }
}
