using System;

namespace TaoLive.Session;

/// <summary>
/// 分片级码率自适应（六项优化④，vc85 蓝本 v2.5 全量落码）：链路健康裕度驱动内核选轨视频码率上限，
/// fhd/hd 档内降片，一细一粗与第 6 条「整路降档」互补——整路降档管「这条线撑不撑得住」，
/// 档内降片管「这条线还撑得住但带宽变薄」。
/// 升降迟滞闸（防抖令 #76 同思想）：降片裕度&lt;0 即判但降档动作最小间隔 10 秒；
/// 升片裕度 ≥0.6 须站满 30 秒持续窗才回一档，宁稳勿抖。
/// 纯逻辑类（测试可直探）；调用方（管理器偷拉泵）每拍喂裕度快照，换档才下发内核。
/// </summary>
public sealed class BitrateGovernor
{
    /// <summary>码率档位（升序）：fhd/hd/sd 出厂标称量级（信号源统一口径：直播流 1~4 Mbps 上下）。</summary>
    public const long TierSd = 1_500_000;
    public const long TierHd = 3_000_000;
    public const long TierFhd = 6_000_000;

    /// <summary>降片判据：裕度 &lt; 0（链路负账，<see cref="LinkHealthLine"/> 合成口径）才降，无账不降（margin=0 不动）。</summary>
    public const double DownMargin = 0.0;

    /// <summary>升片判据：裕度 ≥ 0.6（链路健康富余）才开始积累升档持续窗。</summary>
    public const double UpMargin = 0.6;

    /// <summary>换档最小间隔（毫秒）：降片动作间隔下限，迟滞防抖（单拍裕度抖动不换档）。</summary>
    public const int MinSwitchIntervalMs = 10_000;

    /// <summary>升片持续窗（毫秒）：裕度站满此窗才回一档，与 #76 黏性口径同思想。</summary>
    public const int UpgradeSustainMs = 30_000;

    private static readonly long[] Tiers = { TierSd, TierHd, TierFhd };

    private int _tierIndex;              // 当前档位（起播满档=不钳）
    private long _lastSwitchAtMs;        // 最近换档时刻（单调钟，long.MinValue=从未换档）
    private long _goodSinceMs;           // 裕度达标起点（long.MinValue=未达标）

    public BitrateGovernor()
    {
        _tierIndex = Tiers.Length - 1;
        _lastSwitchAtMs = long.MinValue;
        _goodSinceMs = long.MinValue;
    }

    /// <summary>当前码率上限（null=不钳，内核按估速自由选档）。</summary>
    public long? CurrentCeiling => _tierIndex >= Tiers.Length - 1 ? null : Tiers[_tierIndex];

    /// <summary>每拍喂健康裕度快照（[-1,1]），返回当前应生效的码率上限；换档由调用方比对下发内核。</summary>
    public long? Update(double margin, long nowMs)
    {
        if (margin < DownMargin)
        {
            _goodSinceMs = long.MinValue; // 链路负账：升档持续窗清零
            if (_tierIndex > 0
                && (_lastSwitchAtMs == long.MinValue || nowMs - _lastSwitchAtMs >= MinSwitchIntervalMs))
            {
                _tierIndex--;
                _lastSwitchAtMs = nowMs;
            }
        }
        else if (margin >= UpMargin)
        {
            if (_goodSinceMs == long.MinValue) _goodSinceMs = nowMs;
            if (_tierIndex < Tiers.Length - 1
                && nowMs - _goodSinceMs >= UpgradeSustainMs
                && (_lastSwitchAtMs == long.MinValue || nowMs - _lastSwitchAtMs >= MinSwitchIntervalMs))
            {
                _tierIndex++;
                _lastSwitchAtMs = nowMs;
                _goodSinceMs = nowMs; // 升档后重新积累持续窗，一档一档回
            }
        }
        else
        {
            _goodSinceMs = long.MinValue; // 裕度平庸：既不降也不积升档窗
        }
        return CurrentCeiling;
    }

    /// <summary>复位（换台/接管口径）：解除钳制回满档。</summary>
    public void Reset()
    {
        _tierIndex = Tiers.Length - 1;
        _lastSwitchAtMs = long.MinValue;
        _goodSinceMs = long.MinValue;
    }
}
