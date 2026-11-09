using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

// ============ 道映 v1.0.20 第一层·止血（2026-11-08 22:59 老板拍板「现在开工」）============
// A【源级死判】：同源连续 403/取票失败达阈值（单台 2 次或跨台 3 台）→ 整源拉黑进观察期（约 10 分钟），
//   期间跳过该源全部候选，不重试不热切；到期半开探测恢复，探成即清账，探败续期。
// B【终局出口】：AutoHopGuard 一圈封顶——自动跳台绕满一圈（无任何成功）即停，不再兜圈。
// G1【请求纪律】：HostPacer 同一取票口两次请求最小间隔，把请求节奏压到频控线以下。
// 纯逻辑可测，全部只依赖标准能力；接线在 StreamResolver / UI 层。

/// <summary>失败性质：403 类服务器明确拒绝 / 取票口网络级失败（SSL/断网/超时）。</summary>
public enum SourceFailureKind
{
    Rejected = 0,     // 服务器明确拒绝（403/429）
    Transport = 1,    // 网络级失败（SSL 连接失败/DNS/超时/断网）
}

/// <summary>源级观察期账本状态。</summary>
public enum BlackoutState
{
    Open = 0,       // 正常放行
    BlackedOut = 1, // 观察期内：跳过该源全部候选，不重试不热切
    HalfOpen = 2,   // 观察期已满：放行一次半开探测，成则清账、败则续期
}

/// <summary>
/// A 源级死判账本（一个取流器一个实例，跨频道共享同一本账）：
/// - 单频道连续失败达 PerChannelLimit 次 → 整源拉黑；
/// - 观察窗内跨 CrossChannelLimit 个不同频道都失败 → 整源拉黑（全家桶整体拒绝早识别）；
/// - 拉黑持续 ObservationPeriod（约 10 分钟）后转半开：放行一次探测，成功清账、失败续期；
/// - 成功回灌（任一请求成功）→ 清账全开。
/// </summary>
public sealed class SourceBlackoutLedger
{
    private sealed class ChannelFails
    {
        public int Streak;                 // 同频道连续失败
        public HashSet<string> Channels = new(StringComparer.Ordinal); // 观察窗内失败过的频道（跨台计数）
        public long WindowStartMs;         // 跨台计数窗口起点（毫秒）
    }

    private sealed class SourceState
    {
        public ChannelFails Fails = new();
        public BlackoutState State = BlackoutState.Open;
        public long UntilMs;               // 拉黑到期时刻（毫秒）
        public bool HalfOpenProbeUsed;     // 半开窗口内探测是否已用
    }

    /// <summary>同频道连续失败多少次触发整源拉黑（v2 细则：单台 2 次）。</summary>
    public const int PerChannelLimit = 2;

    /// <summary>观察窗内跨多少个不同频道都失败触发整源拉黑（v2 细则：跨台 3 台）。</summary>
    public const int CrossChannelLimit = 3;

    /// <summary>跨台计数窗口（毫秒）：超过窗口时间的旧失败不再累计。</summary>
    public const long CrossChannelWindowMs = 10 * 60_000;

    /// <summary>整源拉黑观察期（v2 细则：约 10 分钟；半开恢复依据公开资料频控自伤推断）。</summary>
    public static readonly TimeSpan ObservationPeriod = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.Ordinal);

    /// <summary>时钟注入（毫秒；真机 Environment.TickCount64 / 测试可控）。</summary>
    private readonly Func<long> _nowMs;

    public SourceBlackoutLedger(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>失败回灌：该源该频道一次失败（403 或取票口网络级失败）。返回记账后的整源状态。</summary>
    public BlackoutState OnFailure(string sourceId, string channelKey, SourceFailureKind kind)
    {
        if (string.IsNullOrEmpty(sourceId)) return BlackoutState.Open;
        long now = _nowMs();
        lock (_lock)
        {
            var st = _sources.TryGetValue(sourceId, out var s) ? s : (_sources[sourceId] = new SourceState());
            if (st.State == BlackoutState.BlackedOut && now < st.UntilMs) return BlackoutState.BlackedOut; // 观察期内失败不再累计
            if (st.State == BlackoutState.HalfOpen)
            {
                // 半开探测失败：续期一轮，转回拉黑态
                st.State = BlackoutState.BlackedOut;
                st.UntilMs = now + (long)ObservationPeriod.TotalMilliseconds;
                st.HalfOpenProbeUsed = false;
                return BlackoutState.BlackedOut;
            }
            if (st.Fails.Channels.Count == 0 || now - st.Fails.WindowStartMs > CrossChannelWindowMs)
            {
                st.Fails.WindowStartMs = now;
                st.Fails.Channels.Clear(); // 窗口过期：旧失败不再累计，跨台计数重新起算
            }
            if (st.Fails.Channels.Add(channelKey) == false) st.Fails.Streak++; // 同频道重复失败累加连败
            else st.Fails.Streak = 1;
            if (st.Fails.Streak >= PerChannelLimit || st.Fails.Channels.Count >= CrossChannelLimit)
            {
                st.State = BlackoutState.BlackedOut;
                st.UntilMs = now + (long)ObservationPeriod.TotalMilliseconds;
                st.HalfOpenProbeUsed = false;
                st.Fails = new ChannelFails();
            }
            return st.State;
        }
    }

    /// <summary>成功回灌：清账全开（换路成功/半开探成/票据续期成功都走这里）。</summary>
    public void OnSuccess(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        lock (_lock) { _sources.Remove(sourceId); } // 清账全开
    }

    /// <summary>当前该源是否放行：观察期内拒绝；期满转半开放行一次探测；Open/HalfOpen 放行。</summary>
    public BlackoutState Check(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return BlackoutState.Open;
        long now = _nowMs();
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var st)) return BlackoutState.Open;
            if (st.State == BlackoutState.Open) return BlackoutState.Open;
            if (st.State == BlackoutState.BlackedOut)
            {
                if (now < st.UntilMs) return BlackoutState.BlackedOut;
                st.State = BlackoutState.HalfOpen;   // 观察期到点：转半开
                st.HalfOpenProbeUsed = false;
                return BlackoutState.HalfOpen;
            }
            // HalfOpen：第一次查询占用探测名额（名额内放行，之后拉黑直到续期到期）
            if (!st.HalfOpenProbeUsed)
            {
                st.HalfOpenProbeUsed = true;
                return BlackoutState.HalfOpen;
            }
            return BlackoutState.BlackedOut; // 半开探测名额已用：继续等续期到期
        }
    }

    /// <summary>观察期内（BlackedOut 且未到期）= 拒绝一切取流/热切。</summary>
    public bool IsBlackedOut(string sourceId)
    {
        long now = _nowMs();
        lock (_lock)
        {
            return _sources.TryGetValue(sourceId, out var st)
                && st.State == BlackoutState.BlackedOut && now < st.UntilMs;
        }
    }

    /// <summary>清空账本（重启内核/诊断用，与自愈 ClearCache 同口径）。</summary>
    public void ClearAll() { lock (_lock) { _sources.Clear(); } }
}

/// <summary>
/// B 终局出口·自动跳台一圈封顶（纯逻辑）：
/// 自动跳台每次调用 RecordHop 记一跳；距上次成功累计跳满 circleSize 跳（绕满一圈无一成功）→ 判终局，
/// 停止自动跳台，UI 亮「信号源暂时不可用」如实提示；任何一次成功（首帧/用户手动选台）即清账放行。
/// </summary>
public sealed class AutoHopGuard
{
    /// <summary>封顶圈数（v2 细则：1 圈，不再兜圈）。</summary>
    public const int MaxCircles = 1;

    private int _hopsSinceSuccess;
    private readonly object _lock = new();

    /// <summary>记一跳。返回 true=放行本次跳台；false=已绕满一圈，停跳（终局出口）。</summary>
    public bool RecordHop(int circleSize)
    {
        int limit = Math.Max(1, circleSize) * MaxCircles;
        lock (_lock)
        {
            _hopsSinceSuccess++;
            return _hopsSinceSuccess <= limit;
        }
    }

    /// <summary>成功回灌：清账放行（首帧成功 / 用户手动选台）。</summary>
    public void OnSuccess()
    {
        lock (_lock) { _hopsSinceSuccess = 0; }
    }

    /// <summary>自上次成功以来累计跳数（诊断/断言用）。</summary>
    public int HopsSinceSuccess { get { lock (_lock) { return _hopsSinceSuccess; } } }
}

/// <summary>
/// G1 请求纪律·同口节拍（纯逻辑）：同一取票口两次请求最小间隔，把请求节奏压到频控线以下。
/// 与 403 冷却（30 秒）、整源拉黑观察期（10 分钟）三层并存：本闸只管「不让请求太密」。
/// </summary>
public sealed class HostPacer
{
    /// <summary>同一主机两次请求最小间隔（保守值：5 秒；频控线摸底后如有实据再定稿，已定稿参数不动）。</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastMs = new();
    private readonly Func<long> _nowMs;

    public HostPacer(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>尝试占用一次配额：距上次请求不足最小间隔返回 false（调用方跳过本次，不发请求）。</summary>
    public bool TryAcquire(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        long now = _nowMs();
        long last = _lastMs.TryGetValue(host, out var t) ? t : long.MinValue / 2;
        if (now - last < (long)MinGap.TotalMilliseconds) return false;
        _lastMs[host] = now;
        return true;
    }

    /// <summary>清空节拍账（重启内核/诊断用）。</summary>
    public void ClearAll() => _lastMs.Clear();
}
