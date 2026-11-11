using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TaoLive.BLL;

/// <summary>单源限流节拍配置（预热细案 §三，不可变）。时间毫秒；※标注为真机调优项。</summary>
public readonly struct SourcePace
{
    public SourcePace(long refreshIntervalMs, long minGapMs, bool batchAggregation = false,
        long backoffBaseMs = 120_000, long backoffMaxMs = 7_200_000)
    {
        RefreshIntervalMs = refreshIntervalMs;
        MinGapMs = minGapMs;
        BatchAggregation = batchAggregation;
        BackoffBaseMs = backoffBaseMs;
        BackoffMaxMs = backoffMaxMs;
    }

    /// <summary>保鲜期：距上次确认成功超过该值才需要再探。</summary>
    public long RefreshIntervalMs { get; }
    /// <summary>两次请求最小间隔（让路前台连接）。</summary>
    public long MinGapMs { get; }
    /// <summary>整表 API 一发全台的源（nmtv/gxtv）：一次请求覆盖全源，节拍按源算不按台算。</summary>
    public bool BatchAggregation { get; }
    /// <summary>403 退避指数基值（细案：2 分钟起步）。</summary>
    public long BackoffBaseMs { get; }
    /// <summary>403 退避封顶（细案：2 小时）。</summary>
    public long BackoffMaxMs { get; }
}

/// <summary>
/// 按源限流节拍政策（A 类第6项·预热改造的调度依据，总装口径三「一个账本」）：
/// yangshipin 最敏感最慢；nmtv/gxtv 整表聚合节拍按源；xatv/gxtv 低敏感长保鲜。
/// </summary>
public static class WarmupPolicy
{
    /// <summary>逐源节拍（键=SourceId；未注册源按 conservative 兜底）。</summary>
    public static readonly IReadOnlyDictionary<string, SourcePace> Paces = new Dictionary<string, SourcePace>
    {
        // 央视频：IP 绑定画像最重，节拍最慢、退避最深（细案 §一.1）
        ["yangshipin"] = new SourcePace(refreshIntervalMs: 10 * 60_000, minGapMs: 2_000),
        // 整表 API：一次请求全台（细案 §一.5/§一.6）
        ["nmtv"] = new SourcePace(refreshIntervalMs: 30 * 60_000, minGapMs: 1_000, batchAggregation: true),
        ["gxtv"] = new SourcePace(refreshIntervalMs: 6 * 60 * 60_000, minGapMs: 1_000, batchAggregation: true),
        // 中敏感：票据短效但可整轮缓存
        ["gzstv"] = new SourcePace(refreshIntervalMs: 30 * 60_000, minGapMs: 1_500),
        ["cztv"] = new SourcePace(refreshIntervalMs: 2 * 60 * 60_000, minGapMs: 1_000),
        // 低敏感：URL 恒定，落盘价值最高
        ["xatv"] = new SourcePace(refreshIntervalMs: 6 * 60 * 60_000, minGapMs: 1_000),
    };

    /// <summary>取源节拍；未注册源给保守兜底（10 分钟保鲜 + 2 秒让路）。</summary>
    public static SourcePace PaceOf(string sourceId) =>
        Paces.TryGetValue(sourceId ?? "", out var pace) ? pace
        : new SourcePace(refreshIntervalMs: 10 * 60_000, minGapMs: 2_000);

    /// <summary>是否整表聚合源（A⑬竞速与预热按源不按台调度）。</summary>
    public static bool IsBatchSource(string sourceId) => PaceOf(sourceId).BatchAggregation;
}

/// <summary>
/// 403 源级指数退避账（总装口径三·与 A⑬竞速/A⑥预热共用同一本账）：
/// 任一台 403 → 该源全部排队任务冻结一个退避周期；连续 403 指数加深（×4 步进，2 分钟起步封顶 2 小时）；
/// 出现 200 即全额恢复。纯逻辑可测，时间由调用方注入。
/// </summary>
public class SourceBackoffLedger
{
    // v25 修法七 3a：热备校验流与稳态循环并发入账，Dictionary 非线程安全 → 改 ConcurrentDictionary
    private readonly ConcurrentDictionary<string, (long UntilMs, int Streak)> _frozen = new(StringComparer.Ordinal);

    /// <summary>记录一次 403：该源冻结一个退避周期。nowMs=TickCount64 基准。</summary>
    public void RecordLimited(string sourceId, long nowMs, SourcePace? pace = null)
    {
        var p = pace ?? WarmupPolicy.PaceOf(sourceId);
        _frozen.AddOrUpdate(sourceId,
            _ => (nowMs + BackoffMs(1, p), 1),
            (_, cur) => (nowMs + BackoffMs(cur.Streak + 1, p), cur.Streak + 1));
    }

    private static long BackoffMs(int streak, SourcePace p)
    {
        // 指数：基值 × 4^(streak-1)，封顶 BackoffMaxMs
        long ms = p.BackoffBaseMs;
        for (int i = 1; i < streak; i++)
        {
            ms *= 4;
            if (ms >= p.BackoffMaxMs) { ms = p.BackoffMaxMs; break; }
        }
        return Math.Min(ms, p.BackoffMaxMs);
    }

    /// <summary>记录一次成功（200）：退避全额恢复。</summary>
    public bool Recover(string sourceId) => _frozen.TryRemove(sourceId, out _);

    /// <summary>源是否处于退避期（竞速跳过/预热挂起的唯一判据）。</summary>
    public bool IsFrozen(string sourceId, long nowMs) =>
        _frozen.TryGetValue(sourceId, out var cur) && nowMs < cur.UntilMs;

    /// <summary>剩余冻结毫秒（未冻结为 0）。</summary>
    public long RemainingMs(string sourceId, long nowMs) =>
        _frozen.TryGetValue(sourceId, out var cur) && nowMs < cur.UntilMs ? cur.UntilMs - nowMs : 0;

    /// <summary>清账（换台/测试重置）。</summary>
    public void Clear() => _frozen.Clear();
}
