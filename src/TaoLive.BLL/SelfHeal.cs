using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

// ============ 道映 v1.0.8 第19项·全面自愈化（用户 2026-11-07 拍板：十项全要）============
// 本文件承载自愈化 1/4/5/6/9/10 项的纯逻辑核心，全部只依赖标准能力、能力不足自动降级，
// 供逻辑测试专项断言；UI 层负责定时巡检、事件接线与用户可见提示（静默自愈 / 明确提示）。

/// <summary>
/// 自愈1·源路径失效：多候选源备援（主源 + 备源/用户自有源）。
/// - 主源探测/播放失败 → 自动切下一候选（预检与取流共用同一状态，切换对用户静默）；
/// - 备援候选连续成功 RecoverToPrimaryAfter 轮 → 恢复自动回主源（同样静默）；
/// - 候选全部失效 → 返回明确失败（UI 明确提示，不静默坏掉）。
/// </summary>
public class SourceFailover
{
    private sealed class CandidateState { public int Index; public int RecoverStreak; }

    private readonly ConcurrentDictionary<string, CandidateState> _states = new();

    /// <summary>备援候选连续成功多少轮后回主源。</summary>
    public const int RecoverToPrimaryAfter = 2;

    /// <summary>主源失败：切到下一候选；候选数≤1 时停在主源（无备援可用）。</summary>
    public int NextCandidateOnFailure(string channelKey, int candidateCount)
    {
        var st = _states.GetOrAdd(channelKey, _ => new CandidateState());
        if (candidateCount <= 1) { st.Index = 0; st.RecoverStreak = 0; return 0; }
        st.Index = Math.Min(st.Index + 1, candidateCount - 1);
        st.RecoverStreak = 0;
        return st.Index;
    }

    /// <summary>当前候选成功：主源清零计数；备援候选累计连续成功，达标即静默回主源。</summary>
    public void OnSuccess(string channelKey) => OnSuccessAsync(channelKey).GetAwaiter().GetResult();

    /// <summary>
    /// 当前候选成功（异步版，vc45 隐患④·回主源稳定确认期）：主源清零计数；备援候选累计连续成功，
    /// 达标且主源探活确认（探针由调用方注入）才回主源——主源未确认恢复则留在备援，
    /// 确认期继续累计（下轮达标再探），防「回死源二次断流」。探针为空按原口径达标即回主（纯逻辑测试兼容）。
    /// </summary>
    public async Task<bool> OnSuccessAsync(string channelKey, Func<Task<bool>>? primaryRecoveryProbe = null)
    {
        var st = _states.GetOrAdd(channelKey, _ => new CandidateState());
        if (st.Index == 0) { st.RecoverStreak = 0; return true; }
        st.RecoverStreak++;
        if (st.RecoverStreak >= RecoverToPrimaryAfter)
        {
            if (primaryRecoveryProbe != null && !await primaryRecoveryProbe())
                return false; // 主源未确认恢复：留备援，确认期不清零，下轮达标再探
            st.Index = 0;
            st.RecoverStreak = 0;
        }
        return true;
    }

    /// <summary>该台当前使用的候选序号（0=主源，1+=备援）。</summary>
    public int CurrentIndex(string channelKey) =>
        _states.GetOrAdd(channelKey, _ => new CandidateState()).Index;
}

/// <summary>
/// 自愈4·设备时间异常：本地时间与响应头对齐（UI 侧取 HTTP Date 头），
/// 偏差超容差标记时间异常（UI 明确提示修正系统时间）；票据计算用修正后的时间，容忍偏差不再误判过期。
/// </summary>
public class ClockGuard
{
    /// <summary>时间容差（秒）：5 分钟内不视为异常。</summary>
    public const int ToleranceSeconds = 300;

    /// <summary>服务端时间 − 本地时间（秒）。容差内记 0（不做无谓修正）。</summary>
    public long ServerOffsetSeconds { get; private set; }

    /// <summary>时间异常标志（UI 据此明确提示「请校准电视系统时间」）。</summary>
    public bool TimeSuspicious { get; private set; }

    /// <summary>对齐一次服务端时间。serverEpochSeconds 传 null（无响应头）不动状态。</summary>
    public void ObserveServerDate(long? serverEpochSeconds, long localNowSeconds)
    {
        if (serverEpochSeconds is null) return;
        long off = serverEpochSeconds.Value - localNowSeconds;
        if (Math.Abs(off) <= ToleranceSeconds)
        {
            ServerOffsetSeconds = 0;
            TimeSuspicious = false;
            return;
        }
        ServerOffsetSeconds = off;
        TimeSuspicious = true;
    }

    /// <summary>票据计算用的时间：本地时间 + 服务端偏移（异常时容忍偏差，不把好票据误判过期）。</summary>
    public long Now(long localNowSeconds) => localNowSeconds + ServerOffsetSeconds;
}

/// <summary>
/// 自愈5·网络环境变化：断网监测与恢复。连接失败连续达阈值判「断网」（静默等待），
/// 任一次成功即恢复在线（UI 收到恢复沿 → 自动重连当前台 + 预检全量重测，静默自愈）。
/// </summary>
public class NetGuard
{
    private int _failStreak;

    /// <summary>连续失败多少次判为断网（预检/取票各自回灌，避免单次抖动误判）。</summary>
    public const int OfflineAfterFails = 4;

    public bool Offline { get; private set; }

    /// <summary>失败回灌。返回当前是否断网态。</summary>
    public bool OnFailure()
    {
        _failStreak++;
        if (!Offline && _failStreak >= OfflineAfterFails) Offline = true;
        return Offline;
    }

    /// <summary>成功回灌。返回恢复沿（true = 刚从断网恢复，调用方触发重连与预检重测）。</summary>
    public bool OnSuccess()
    {
        bool wasOffline = Offline;
        Offline = false;
        _failStreak = 0;
        return wasOffline;
    }
}

/// <summary>自愈6·低配长时运行退化的处置动作。</summary>
public enum MemoryAction
{
    Normal = 0,
    ReleaseCache = 1,    // 释放取流缓存（静默）
    RestartKernel = 2,   // 软重启播放内核（不整机重启，静默；播放内核异常累计达阈值才动用）
}

/// <summary>
/// 自愈6·播放器长时运行退化：看门狗兜底之外加内存监测——
/// 可用内存占比低 → 释放缓存；可用内存极低且播放内核连续异常 → 软重启播放内核（重建设播环境，不动系统）。
/// </summary>
public class MemoryWatcher
{
    /// <summary>可用内存占比低于此值释放缓存。</summary>
    public const double ReleaseThreshold = 0.15;

    /// <summary>可用内存占比低于此值且内核连续异常 → 软重启播放内核。</summary>
    public const double RestartThreshold = 0.06;

    /// <summary>播放内核连续异常（重连耗尽等）多少次后允许动用软重启。</summary>
    public const int KernelFailLimit = 3;

    private int _kernelFailStreak;

    public void OnKernelFailure() => _kernelFailStreak++;
    public void OnKernelHealthy() => _kernelFailStreak = 0;

    public MemoryAction Observe(double availRatio, bool kernelAlive)
    {
        if (availRatio < RestartThreshold && !kernelAlive && _kernelFailStreak >= KernelFailLimit)
        {
            _kernelFailStreak = 0; // 动用一次后重新累计，防连环重启
            return MemoryAction.RestartKernel;
        }
        if (availRatio < ReleaseThreshold) return MemoryAction.ReleaseCache;
        return MemoryAction.Normal;
    }
}

/// <summary>
/// 自愈9·低配资源自适应：缓存条目数/预检频率按可用内存动态调整。三档：充裕 / 紧张 / 极低（内存未知按充裕兜底）。
/// </summary>
public static class ResourceProfile
{
    public static (int PrecheckIntervalMs, int RoundGapMs, int MaxCacheEntries) Adapt(long availBytes)
    {
        const long MB = 1024L * 1024;
        if (availBytes <= 0) return (600, 20_000, 96);      // 内存未知：按充裕兜底
        if (availBytes < 400 * MB) return (1200, 40_000, 32);  // 极低：降频减存
        if (availBytes < 800 * MB) return (900, 30_000, 64);   // 紧张：小降频
        return (600, 20_000, 96);                              // 充裕：全速
    }
}

/// <summary>
/// 自愈10·遥控未知键值：常见别名映射 + 未知键记录（不硬编码机型）。
/// 已知键值归一到标准 Keycode 语义（UI 据此分派）；未记录过的键值由 UI 写入记录表，
/// 后续可经远程热更新（第19项第3条）扩充映射表——映射表本身数据驱动，不写死任何机型。
/// </summary>
public static class KeyAlias
{
    /// <summary>常见别名映射：厂商遥控常见键值 → 标准 Keycode 值。</summary>
    public static readonly IReadOnlyDictionary<int, int> Known = new Dictionary<int, int>
    {
        { 23, 23 },   // DPAD_CENTER（标准确认）
        { 66, 66 },   // ENTER（标准确认）
        { 82, 82 },   // MENU（标准菜单）
        { 165, 165 }, // INFO（信息键）
        { 177, 177 }, // SETTINGS（标准设置键）
        { 4, 4 },     // BACK（标准返回）
        { 111, 4 },   // 部分遥控 ESC 码 → 返回（常见别名）
        { 19, 19 }, { 20, 20 }, { 21, 21 }, { 22, 22 }, // DPAD 上下左右
        { 4077, 177 },// TCL 等机型设置键实测值（真机已验证）→ 标准设置键语义
    };

    /// <summary>归一：已知键返回标准 Keycode 值；未知键返回 null（UI 记录后按未接管处理）。</summary>
    public static int? ToCanonical(int raw) => Known.TryGetValue(raw, out var canonical) ? canonical : null;
}
