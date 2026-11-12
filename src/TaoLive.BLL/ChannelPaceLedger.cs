using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// v25 修法六·按台自适应节拍（纯逻辑可测，时间由调用方注入 TickCount64 基准毫秒）：
/// - 台级表现账：按「EpgKey+SourceId」一格账（老板 12:32 拍板：一台多源各记各的，与探测登记/健康表同粒度；
///   播放实测记账同样带源标记）——备援源抽风不拖累主源表现；
/// - 每格滑动窗口最近 10 次结论（探测结果 + 播放实测共用记账：播放成功计正、失败计负）；
/// - 计负口径（分析轮 2 漏洞 2）：仅服务器明确拒绝（Rejected/403/429）与播放流级失败计负；
///   网络级失败（超时/断网）不计负，防用户侧网络差把全部台推成脆档；
/// - 三档：稳（窗口≥3 且成功率≥90% → ×1.5）/ 正常（默认 ×1.0）/ 脆（窗口≥3 且成功率&lt;60% → ×0.7）；
///   窗口不足 3 条不评档按正常（分析轮 3 漏洞 1，防 1 次失败即判脆）；整表源不做台级三档（节拍按源）；
/// - 档位系数钳制 [0.5×, 2×]×保鲜期；时段系数：晚高峰 19:00–23:00 ×0.7（质量波动加密），
///   深夜 01:00–07:00 ×2（降功耗降风控）；只作用于下次到期登记，不改让路间隔与退避账；
///   yangshipin 时段系数恒 1.0（分析轮 1 漏洞 4：总闸只降不升）；组合顺序=先台级档再时段系数，
///   晚高峰受下限钳制（≥0.5×保鲜期），深夜总上限 4×保鲜期（分析轮 1 漏洞 6）；
/// - 全局让路闸（修法一 1a）：按源维护 nextAllowedAtMs，稳态循环与屏内先探共享同一闸，
///   发起前原子占位——两方同时到期先到先探，不设优先级队列；让路间隔不翻倍。
/// </summary>
public class ChannelPaceLedger
{
    public const int WindowSize = 10;
    public const int MinWindowForTier = 3;

    public const string TierStable = "stable";
    public const string TierNormal = "normal";
    public const string TierBrittle = "brittle";

    private sealed class Cell
    {
        public readonly Queue<bool> Results = new();
    }

    private readonly ConcurrentDictionary<string, Cell> _cells = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _nextAllowedAtMs = new(StringComparer.Ordinal);
    private readonly object _gateLock = new();

    /// <summary>记账一条结论：positive=播放成功/探测 200；negative 仅传服务器拒绝与播放流级失败。</summary>
    public void Record(string epgKey, string sourceId, bool positive)
    {
        var cell = _cells.GetOrAdd($"{sourceId}:{epgKey}", _ => new Cell());
        lock (cell.Results)
        {
            cell.Results.Enqueue(positive);
            while (cell.Results.Count > WindowSize) cell.Results.Dequeue();
        }
    }

    /// <summary>台×源当前档；整表源恒正常档（节拍按源，台级档位无意义，分析轮 2 漏洞 1）。</summary>
    public string TierOf(string epgKey, string sourceId, bool batchSource)
    {
        if (batchSource) return TierNormal;
        if (!_cells.TryGetValue($"{sourceId}:{epgKey}", out var cell)) return TierNormal;
        lock (cell.Results)
        {
            if (cell.Results.Count < MinWindowForTier) return TierNormal; // 窗口起评门槛
            int ok = cell.Results.Count(r => r);
            double rate = (double)ok / cell.Results.Count;
            if (rate >= 0.9) return TierStable;
            if (rate < 0.6) return TierBrittle;
            return TierNormal;
        }
    }

    /// <summary>
    /// 台×源下次登记间隔毫秒（登记到期完整口径）：baseInterval=保鲜期。
    /// 顺序=档位系数（钳 [0.5×,2×]）→ 时段系数（yangshipin 恒 1.0）→ jitter（EpgKey 稳定哈希 [0.8,1.4]，
    /// 同表多次启动分布一致不漂移，分析轮 2 补丁）；最终钳 [0.5×base, 4×base]
    /// （晚高峰下限钳制、深夜总上限 4×保鲜期）。localHour=设备本地小时（0–23；时钟不对系数错位可接受）。
    /// </summary>
    public long RegisterIntervalMs(string epgKey, string sourceId, long baseIntervalMs, int localHour, bool batchSource)
    {
        double f = TierOf(epgKey, sourceId, batchSource) switch
        {
            TierStable => 1.5,
            TierBrittle => 0.7,
            _ => 1.0,
        };
        long tierMs = (long)Math.Clamp(baseIntervalMs * f, baseIntervalMs * 0.5, baseIntervalMs * 2);
        // 源档案接线（上善若水纪律）：IP 绑定源（出厂=央视频）豁免时段加密，判据读档案不再点名
        double tf = SourceProfileStore.Shared.ProfileOf(sourceId).IpBound ? 1.0 : TimeFactor(localHour);
        long ms = (long)(tierMs * tf * StableJitter(epgKey));
        return Math.Clamp(ms, (long)(baseIntervalMs * 0.5), baseIntervalMs * 4);
    }

    /// <summary>EpgKey 稳定哈希散列 jitter ∈ [0.8, 1.4]（同表分布一致，均值 1.1）。</summary>
    public static double StableJitter(string epgKey)
    {
        uint h = 2166136261;
        foreach (var c in epgKey ?? "") { h ^= c; h *= 16777619; }
        return 0.8 + (h % 1000) / 1000.0 * 0.6;
    }

    /// <summary>时段系数：晚高峰 19–23 → 0.7；深夜 01–07 → 2.0；其余 1.0。</summary>
    public static double TimeFactor(int localHour) =>
        localHour is >= 19 and < 23 ? 0.7
        : localHour is >= 1 and < 7 ? 2.0
        : 1.0;

    /// <summary>
    /// 全局让路闸占位（修法一 1a）：nowMs ≥ nextAllowedAtMs 时占位成功并把 nextAllowedAtMs 推进 nowMs+minGapMs；
    /// 未到时刻返回 false（本次不发起，调用方顺延）。稳态循环与屏内先探共享，先到先探不翻倍。
    /// </summary>
    public bool TryAcquireGate(string sourceId, long nowMs, long minGapMs)
    {
        lock (_gateLock)
        {
            if (_nextAllowedAtMs.TryGetValue(sourceId, out var next) && nowMs < next) return false;
            _nextAllowedAtMs[sourceId] = nowMs + minGapMs;
            return true;
        }
    }

    /// <summary>让路闸剩余毫秒（0=空闲），仅供日志与测试。</summary>
    public long GateRemainingMs(string sourceId, long nowMs) =>
        _nextAllowedAtMs.TryGetValue(sourceId, out var next) && nowMs < next ? next - nowMs : 0;

    /// <summary>清空（测试重置）。</summary>
    public void Clear()
    {
        _cells.Clear();
        _nextAllowedAtMs.Clear();
    }

    /// <summary>目录刷新清孤儿（分析轮 55）：仅保留 keep(key) 为 true 的台账；让路闸按源不清理。</summary>
    public void RetainOnly(Func<string, bool> keep)
    {
        foreach (var key in _cells.Keys.ToList())
            if (!keep(key)) _cells.TryRemove(key, out _);
    }
}
