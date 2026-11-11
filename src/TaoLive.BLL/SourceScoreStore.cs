using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TaoLive.BLL;

// ============ v28 主线③·源健康度评分（2026-11-11 17:41 老板拍板：可靠 40% + 速度 30% + 稳定 30%，七天半衰）============
// 从快从精：不引入新文件格式以外的花活；落盘口径照抄 SourceHealthStore（原子替换、3 秒节流、启动灌回）。
// 定位同健康账：只加速选源、不保证正确性——账空/账坏一律退化到既有顺序，绝不倒退。

/// <summary>评分记录：键 = SourceId:EpgKey，时间 = Unix 毫秒（跨启动可比）。</summary>
public class SourceScoreRecord
{
    public string K { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string EpgKey { get; set; } = "";
    public int OkCount { get; set; }        // 成功次数
    public int FailCount { get; set; }      // 失败次数（403/退避/探通失败）
    public double AvgMs { get; set; }       // 平均耗时（建连/首帧喂入，指数移动）
    public int TimeoutCount { get; set; }   // 超时/断流次数（稳定度负项）
    public long LastMs { get; set; }        // 最近一次回写时间（半衰基准）
}

/// <summary>
/// 源健康度账本（BLL 纯逻辑，文件路径由 UI 传入应用私有目录）：
/// - 打分 = 可靠 40%（成功占比）+ 速度 30%（耗时归一化，越快越高）+ 稳定 30%（无超时占比）；
/// - 每条结论带时间权重：七天半衰（越旧权重越低，0.5^(age/7d)）；
/// - 账空/无该源账 = 无分，BestAliveSource 退化既有顺序（可注入时间，纯逻辑可测）。
/// </summary>
public class SourceScoreStore
{
    /// <summary>半衰期：7 天（毫秒）。0.5^(age/HalfLifeMs)。</summary>
    public const long HalfLifeMs = 7 * 24 * 60 * 60 * 1000L;

    /// <summary>速度分满分对应的耗时上限：2 秒内视为满分梯度基准（>2s 线性衰减到 10s 为 0）。</summary>
    public const double FastMs = 2000, SlowMs = 10000;

    private const int FlushDebounceMs = 3000;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ConcurrentDictionary<string, SourceScoreRecord> _entries = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;
    private System.Threading.Timer? _flushTimer;
    private bool _dirty;

    /// <summary>nowMs 注入：测试用；默认 Unix 毫秒。</summary>
    public SourceScoreStore(string dir, Func<long>? nowMs = null)
    {
        _path = Path.Combine(dir ?? ".", "source_score.json");
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>喂入一次结果（UI 首帧成功/取流失败/预检回写共用；ok=true 时 ms>0 计速度账）。</summary>
    public void Report(string sourceId, string epgKey, bool ok, long ms = 0, bool timeout = false)
    {
        var key = $"{sourceId}:{epgKey}";
        var rec = _entries.GetOrAdd(key, _ => new SourceScoreRecord { K = key, SourceId = sourceId, EpgKey = epgKey });
        lock (rec) // 单条目锁：并发回写不互踩
        {
            if (ok) rec.OkCount++; else rec.FailCount++;
            if (ok && ms > 0)
                rec.AvgMs = rec.AvgMs <= 0 ? ms : rec.AvgMs * 0.7 + ms * 0.3; // EMA 平滑，7/3 折
            if (timeout) rec.TimeoutCount++;
            rec.LastMs = _nowMs();
        }
        MarkDirty();
    }

    /// <summary>打分：0~100，账空返回 null（=无分退化）。时间权重 = 0.5^(age/7d)。</summary>
    public double? ScoreOf(string sourceId, string epgKey)
    {
        var key = $"{sourceId}:{epgKey}";
        if (!_entries.TryGetValue(key, out var rec)) return null;
        double age = Math.Max(0, _nowMs() - rec.LastMs);
        double w = Math.Pow(0.5, age / HalfLifeMs); // 七天半衰
        int total = rec.OkCount + rec.FailCount;
        if (total == 0) return null;
        double reliable = (double)rec.OkCount / total;                       // 可靠 40%
        double speed = rec.AvgMs <= 0 ? 0.5
            : Math.Clamp(1.0 - (rec.AvgMs - FastMs) / (SlowMs - FastMs), 0, 1); // 速度 30%
        double stable = 1.0 - (total == 0 ? 0 : (double)rec.TimeoutCount / Math.Max(1, rec.OkCount + rec.TimeoutCount)); // 稳定 30%
        return (reliable * 0.4 + speed * 0.3 + stable * 0.3) * 100.0 * w;
    }

    /// <summary>该台是否有账（BestAliveSource 退化判据）。</summary>
    public bool HasAccount(string sourceId, string epgKey) => _entries.ContainsKey($"{sourceId}:{epgKey}");

    /// <summary>启动载入：全量灌入（记录本身带时间权重，无需按期清理；账坏当无账处理）。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var records = JsonSerializer.Deserialize<SourceScoreRecord[]>(json ?? "[]") ?? Array.Empty<SourceScoreRecord>();
            foreach (var r in records)
            {
                if (r is null || string.IsNullOrEmpty(r.K) || string.IsNullOrEmpty(r.SourceId)) continue;
                _entries.TryAdd(r.K, r);
            }
        }
        catch
        {
            // 账本损坏不致命：当无账处理，喂入很快重建
        }
    }

    /// <summary>脏标记：3 秒节流后台落盘（写绝不阻塞播放线程）。</summary>
    public void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_flushTimer != null) return;
            _flushTimer = new System.Threading.Timer(_ =>
            {
                FlushNow();
                lock (_gate) _flushTimer?.Dispose();
                lock (_gate) _flushTimer = null;
            }, null, FlushDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>立即落盘：临时文件写入后原子替换（退出/暂停时调用）。</summary>
    public void FlushNow()
    {
        SourceScoreRecord[] snapshot;
        lock (_gate)
        {
            _dirty = false;
            snapshot = _entries.Values.ToArray();
        }
        try
        {
            var json = JsonSerializer.Serialize(snapshot);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true); // 原子替换一步到位（照抄 SourceHealthStore v28 修法）
        }
        catch
        {
            // 落盘失败静默：下轮脏标记再试，绝不影响播放
        }
    }
}
