using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// 切台清屏与亮牌口径（v1.0.10 修复⑦）：撤旧播放器到新流首帧之间，GPU 残留旧缓冲可能被
/// 当作画面（彩色竖条花屏）。口径：换台即铺干净底色遮住旧内容；新流首帧到齐才亮；
/// 同台反复花屏（解码规格变更类）改走备用载体重接。
/// </summary>
public static class SwitchRevealPolicy
{
    /// <summary>换台瞬间应立即铺底色（撤旧画面的同时），用户不见残留。</summary>
    public static bool ShouldVeilOnSwitch(bool frameEverShown) => true;

    /// <summary>新流首帧到齐才撤底色亮画面（与「正在连接」亮牌同步撤，不再单独早亮）。</summary>
    public static bool ShouldReveal(bool newStreamFirstFrame) => newStreamFirstFrame;

    /// <summary>换台后短期内反复花屏（计数达阈值）：判定解码规格变更路线不可靠，走备用载体重接。</summary>
    public const int CorruptionLimit = 2;

    public static bool ShouldFallbackCarrier(int corruptionCount) => corruptionCount >= CorruptionLimit;

    // ---- vc53 任务9·切台不闪黑（老板 2026-11-14 12:21 批次合并令） ----
    // 原口径（v1.0.10 修复⑦+雷二）：换台瞬间立即铺底色——解析期整段黑场/纯底，用户见「闪黑」。
    // 新口径：解析期留旧画面继续播（旧流还活着），新流就绪换流前一刻才清场，首帧到撤底色；
    // 冷启动（无旧画面可留）与换流瞬间维持立即清场——GPU 残留防护（修复⑦）覆盖面不缩。

    /// <summary>解析期是否留旧画面：出过画面且新流首帧未到 → 旧流继续显示，零黑场。</summary>
    public static bool ShouldHoldLastFrame(bool oldFrameAvailable, bool newFirstFrame) =>
        oldFrameAvailable && !newFirstFrame;

    /// <summary>换台瞬间是否立即铺底色：仅冷启动（无旧画面）。有旧画面一律留到换流前。</summary>
    public static bool ShouldVeilImmediately(bool oldFrameAvailable) => !oldFrameAvailable;

    /// <summary>新播放器接管前是否清场：一律是——旧播放器释放瞬间 GPU 残留旧缓冲可能外露（修复⑦口径不回退）。</summary>
    public static bool ShouldVeilBeforeSwap(bool oldFrameAvailable) => true;
}

/// <summary>
/// 换台提速·先出画面（v1.0.10 修复⑥）：同台多档可用时先连最快出画面的档位（带宽优先，
/// 低带宽先亮），画面亮后后台无感升到更高档；升级失败留在当前档，绝不为升档搞挂画面。
/// </summary>
public static class FastStartPlanner
{
    /// <summary>变体按带宽升序=出画面最快次序（同带宽取矮的先出）；空表按原样。</summary>
    public static IReadOnlyList<(int Height, long Bandwidth)> StartOrder(
        IReadOnlyList<(int Height, long Bandwidth)> variants)
    {
        if (variants.Count <= 1) return variants;
        return variants.OrderBy(v => v.Bandwidth).ThenBy(v => v.Height).ToList();
    }

    /// <summary>首档（先出画面档）。</summary>
    public static (int Height, long Bandwidth)? First(IReadOnlyList<(int Height, long Bandwidth)> variants)
    {
        if (variants.Count == 0) return null;
        return StartOrder(variants)[0];
    }

    /// <summary>画面亮后的无感升级目标：最高档；已是最高档返回 null（不折腾）。</summary>
    public static (int Height, long Bandwidth)? UpgradeTarget(
        IReadOnlyList<(int Height, long Bandwidth)> variants, int currentHeight)
    {
        if (variants.Count <= 1) return null;
        var top = variants.OrderByDescending(v => v.Height).First();
        if (top.Height > currentHeight) return top;
        return null;
    }

    /// <summary>升级失败或网络撑不住：留在当前档（不回退重试到崩）。</summary>
    public static (int Height, long Bandwidth) OnUpgradeFailed(
        IReadOnlyList<(int Height, long Bandwidth)> variants, int currentHeight) =>
        variants.FirstOrDefault(v => v.Height == currentHeight);
}
