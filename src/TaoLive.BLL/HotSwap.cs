using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

// ============ 道映 v1.0.10【六】【九】A 双实例无感切换（老板 13:46 拍板：跳过项全部完成）============
// 【六】换台提速·先亮后升 + 【九】A 自动档无感热切，共用同一套「新流亮了再换」机制：
// - 档位/变体变化绝不重起当前台（Play(_current) 一律退役）；
// - 后台第二实例连新流，新流首帧到齐才切换画面——全程不黑屏、不闪、不卡；
// - 升级失败或网络撑不住就留在当前档，绝不为升档搞挂画面；
// - 反复升降档设最小间隔防横跳（防闪卡），换台作废未决切换；
// - 纯逻辑状态机全部可测；UI 宿主（LiveVideoHost）按此驱动第二实例。

/// <summary>切换方式：Stay=无变化；HotSwitch=可热应用（滤镜实时生效的渲染器，画面不断）；SwapOnReady=双实例新流亮了再换。</summary>
public enum SwapMode
{
    Stay = 0,
    HotSwitch = 1,
    SwapOnReady = 2,
}

/// <summary>
/// 档位/变体切换决策（纯函数）：
/// - 同档同参 → Stay（不折腾）；
/// - 渲染器支持热应用（canHotApply=true，如未来滤镜免重启生效）→ HotSwitch 优先；
/// - 其余一律 SwapOnReady（双实例「新流亮了再换」）——当前 LibVLC 滤镜随媒体参数生效、需新 Media，
///   故现状自动档/手动档/升档全部走双实例，绝不重起当前台。
/// </summary>
public static class HotSwitchPolicy
{
    public static SwapMode Decide(bool sameTarget, bool canHotApply)
    {
        if (sameTarget) return SwapMode.Stay;
        return canHotApply ? SwapMode.HotSwitch : SwapMode.SwapOnReady;
    }
}

/// <summary>
/// 双实例切换会话（一次「新流亮了再换」的完整生命周期）：
/// Idle →(Begin) Preparing →(新流首帧) Ready →(CommitSwap) Committed；
/// Preparing →(失败/超时) Aborted（留在当前档，画面无任何扰动）；
/// 换台 → Cancel（未决切换作废，旧台新台互不串扰）。
/// </summary>
public sealed class HotSwapSession
{
    public enum SwapState { Idle, Preparing, Ready, Committed, Aborted }

    public SwapState State { get; private set; } = SwapState.Preparing; // 构造即受理：Begin=创建会话
    public int PlayGen { get; }          // 所属播放代数（换台作废守卫）
    public int TargetHeight { get; }     // 目标变体分辨率高（滤镜切换无变体时为 0）
    public string Reason { get; }        // 触发原因（⑥先亮后升 / ⑨A自动档 / 手动切档），落日志用
    public long BeginMs { get; }         // 开始时刻（毫秒时钟），超时判定用

    public HotSwapSession(int playGen, int targetHeight, string reason, long beginMs)
    {
        PlayGen = playGen; TargetHeight = targetHeight; Reason = reason ?? ""; BeginMs = beginMs;
    }

    /// <summary>第二实例已连上新流（首帧到齐）→ Ready。只有 Preparing 态响应；重复通知忽略（提交只此一次）。</summary>
    public bool MarkReady()
    {
        if (State != SwapState.Preparing) return false;
        State = SwapState.Ready;
        return true;
    }

    /// <summary>第二实例连不上/超时 → Aborted，留在当前档。</summary>
    public bool MarkAborted()
    {
        if (State != SwapState.Preparing && State != SwapState.Ready) return false;
        State = SwapState.Aborted;
        return true;
    }

    /// <summary>画面提交（第二实例顶上成主画面）→ Committed。仅 Ready 态可提交，绝不出黑屏半程。</summary>
    public bool MarkCommitted()
    {
        if (State != SwapState.Ready) return false;
        State = SwapState.Committed;
        return true;
    }

    /// <summary>换台作废未决切换。</summary>
    public void Cancel() { if (State == SwapState.Preparing || State == SwapState.Ready) State = SwapState.Aborted; }

    /// <summary>Preparing 态超过 PrepareTimeoutMs 仍未亮 → 判升级失败（调用方据此 Abort，绝不让用户干等黑屏）。</summary>
    public bool IsExpired(long nowMs, long prepareTimeoutMs) =>
        State == SwapState.Preparing && nowMs - BeginMs >= prepareTimeoutMs;
}

/// <summary>
/// 双实例切换协调器（一个播放宿主一个实例）：
/// - 同一时刻最多一个未决切换：Preparing/Ready 未决时新请求拒绝（叠加切换必然闪卡）；
/// - 最小间隔冷却：MinSwapIntervalMs 内的反复升降档请求不受理（防横跳闪卡）；
/// - 换台即清（OnChannelChanged）：未决会话作废；
/// - 绝不重起当前台：协调器只管「第二实例亮没亮」，当前画面在提交前原样保留。
/// </summary>
public sealed class HotSwapCoordinator
{
    /// <summary>两次切换最小间隔（毫秒）：反复升降档不横跳不闪卡。</summary>
    public const int MinSwapIntervalMs = 3000;

    /// <summary>第二实例起流到首帧的容忍时长（毫秒）：超时判升级失败，留当前档。</summary>
    public const int PrepareTimeoutMs = 8000;

    /// <summary>
    /// fix24（老板拍板）：热切失败冷却基期（毫秒）——第二实例起流失败后不按 5 秒节拍空转重试，
    /// 从 2 分钟起步、连续失败指数退避（2/4/8/16 分钟封顶）；换台清账（新台可能正常）。
    /// </summary>
    public const long FailCooldownBaseMs = 120_000;

    private HotSwapSession? _active;
    private long _lastBeginMs = long.MinValue / 2;
    private int _lastCommittedGen = -1;
    private long _lastFailMs = long.MinValue / 2;
    private int _failStreak;

    /// <summary>当前未决/刚完成的会话（诊断用）。</summary>
    public HotSwapSession? Active => _active;

    /// <summary>是否有未决切换（Preparing/Ready）——宿主自救（静默重取/软重启）期间应让路，不与切换叠加。</summary>
    public bool HasPending => _active is { State: HotSwapSession.SwapState.Preparing or HotSwapSession.SwapState.Ready };

    /// <summary>受理一次切换请求；失败冷却期/最小间隔冷却期内/已有未决切换返回 null（留在当前档，调用方不重试到崩）。</summary>
    public HotSwapSession? Begin(int playGen, int targetHeight, string reason, long nowMs)
    {
        if (HasPending) return null;                                   // 未决切换不叠加
        long failCool = FailCooldownBaseMs << Math.Min(Math.Max(_failStreak - 1, 0), 3); // fix24：首败 2 分钟，连败 2/4/8/16 分钟封顶
        if (nowMs - _lastFailMs < failCool) return null;               // 失败冷却：不 5 秒一循环空耗
        if (nowMs - _lastBeginMs < MinSwapIntervalMs) return null;     // 冷却防横跳
        _lastBeginMs = nowMs;
        _active = new HotSwapSession(playGen, targetHeight, reason, nowMs);
        return _active;
    }

    /// <summary>fix24：热切失败记账（第二实例起流失败/升级超时）——进入退避冷却，连续失败指数加长。</summary>
    public void MarkFailed(long nowMs)
    {
        _lastFailMs = nowMs;
        _failStreak++;
    }

    /// <summary>fix24：失败账清零（成功提交画面 / 换台后新台重计）——退避窗口一并解除。</summary>
    public void ResetFailures() { _lastFailMs = long.MinValue / 2; _failStreak = 0; }

    /// <summary>未决切换超时（新流迟迟不亮）→ 判失败留当前档。</summary>
    public bool ExpireIfDue(long nowMs)
    {
        if (_active is { } s && s.IsExpired(nowMs, PrepareTimeoutMs)) return s.MarkAborted();
        return false;
    }

    /// <summary>换台：未决切换作废（旧会话不再受理提交）；fix24：失败账随台清零（新台重计）。</summary>
    public void OnChannelChanged() { _active?.Cancel(); ResetFailures(); }

    /// <summary>提交记账（UI 宿主完成画面顶替后调用）：仅 Ready 态受理，记录提交代数供「不重复提交」断言。</summary>
    public void OnCommitted(HotSwapSession session, int playGen)
    {
        if (!session.MarkCommitted()) return; // 非 Ready 拒绝记账：半程状态绝不入账
        _lastCommittedGen = playGen;
    }

    /// <summary>同一播放代数是否已提交过（防重复提交断言用）。</summary>
    public bool CommittedIn(int playGen) => _lastCommittedGen == playGen;
}
