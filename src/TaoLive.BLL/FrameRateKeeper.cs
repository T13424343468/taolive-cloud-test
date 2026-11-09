using System;

namespace TaoLive.BLL;

/// <summary>
/// 帧率稳档观察（v1.0.13 fix25，老板拍板「帧率不止要拉得起来，还要稳得住」）：
/// - 升档成功后开观察窗（60 秒）：期间实测帧率明显低于流原始帧率，或播放停滞 → 判「稳不住」，自动退回原档；
/// - 观察窗平静走完 → 转正，不再干预；
/// - 升档门槛加严：实测帧率达不到原始帧率的 85%（或播放停滞）本轮不升，避免来回拉锯。
/// 纯逻辑可测；UI 巡检 5 秒节拍喂实测。
/// </summary>
public class FrameRateKeeper
{
    /// <summary>升档后观察窗时长（毫秒）：60 秒内帧率撑不住就退回。</summary>
    public const long SoakMs = 60_000;

    /// <summary>实测帧率低于原始帧率的此比例判「撑不住」（85%）。</summary>
    public const double FpsHoldRatio = 0.85;

    private AiLiveTier? _fromTier;
    private long _commitMs;
    private bool _settled = true;

    /// <summary>观察窗进行中（升档已提交且未走完/未被判定）。</summary>
    public bool Watching => _fromTier is { } && !_settled;

    /// <summary>退回目标档：升档前的档位。</summary>
    public AiLiveTier FromTier => _fromTier!.Value;

    /// <summary>升档提交：开观察窗。</summary>
    public void OnUpgradeCommitted(AiLiveTier fromTier, long nowMs)
    {
        _fromTier = fromTier;
        _commitMs = nowMs;
        _settled = false;
    }

    /// <summary>
    /// 观察期喂一次实测。返回 true=判「稳不住」，调用方应退回 FromTier（判定即收口，只回退一次）；
    /// 观察窗平静走完 → 转正返回 false 且观察关闭。
    /// </summary>
    public bool Report(long nowMs, double? origFps, double? curFps, bool stalled)
    {
        if (!Watching) return false;
        if (nowMs - _commitMs >= SoakMs) { _settled = true; return false; } // 平稳走完观察窗
        bool fpsDrop = origFps is > 0 && curFps is > 0 && curFps.Value < origFps.Value * FpsHoldRatio;
        if (stalled || fpsDrop)
        {
            _settled = true; // 判定即收口：退回动作交给调用方，观察不再重复判
            return true;
        }
        return false;
    }

    /// <summary>换台重置观察窗（新台新算）。</summary>
    public void Reset() { _fromTier = null; _settled = true; }

    /// <summary>升档门槛（纯函数）：播放停滞，或实测帧率撑不住原始帧率 → 本轮不升；帧率未知不拦（交平稳周期把关）。</summary>
    public static bool UpgradeAllowed(double? origFps, double? curFps, bool stalled)
    {
        if (stalled) return false;
        if (origFps is > 0 && curFps is > 0 && curFps.Value < origFps.Value * FpsHoldRatio) return false;
        return true;
    }
}
