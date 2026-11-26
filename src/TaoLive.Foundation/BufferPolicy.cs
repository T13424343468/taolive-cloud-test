using System;

namespace TaoLive.Foundation;

/// <summary>
/// 缓冲水位口径（v1.0.10 修复⑨B+⑩）：默认贴直播边缘零延迟感；
/// 水位下降/网络抖动时主动落后一个安全水位吸收抖动；
/// 老板令 2026-11-15 #21：内存分档废止，一律按 60 秒上限拉满（内核自身按内存吃满自然封顶，
/// 不再替内核保守降档）；全程内存环形缓冲不落磁盘；
/// 网络恢复平稳后平滑追回直播边缘。水位是看门狗自救的缓冲池：先吃池子再动手。
/// </summary>
public enum BufferAction { HoldEdge, BuildUp, CatchUp }

public static class BufferPolicy
{
    public const long MB = 1024L * 1024;

    /// <summary>安全水位上限（秒）：老板令 2026-11-15 #21 曾固定 60 秒拉满；#67 三档位令改走
    /// AdvancedTuning.PrefetchWindowSec 旋钮（30/45/60 档，默认 60=原固定口径），总纲「旋钮写活不写死」。
    /// （totalMemBytes 参数保留兼容既有调用口径，不再参与分档）。</summary>
    public static int MaxWatermarkSeconds(long totalMemBytes) => AdvancedTuning.PrefetchWindowSec;

    /// <summary>
    /// 喂一次水位（秒）与网络平稳状态：
    /// - 水位充足且平稳 → 追回直播边缘（延迟归零）；
    /// - 水位低或抖动 → 攒水位（提前介入，不等 6 秒卡死）；
    /// - 水位贴边且平稳 → 保持边缘（默认零延迟感）。
    /// </summary>
    public static BufferAction Observe(double bufferedSeconds, bool networkSteady, long totalMemBytes)
    {
        int max = MaxWatermarkSeconds(totalMemBytes);
        double low = max / 3.0;               // 低水位线：上限的 1/3（60→20s）
        if (!networkSteady || bufferedSeconds < low) return BufferAction.BuildUp;
        if (bufferedSeconds < max * 0.9) return BufferAction.CatchUp; // 平稳未满：平滑追回
        return BufferAction.HoldEdge;
    }
}

/// <summary>
/// 库存两档（1.0.12 发现 B·拍板稿，治「慢」不治「断」——网络抖动型卡顿近乎消除）：
/// - StartFull 起播即 7 秒：起播缓存不低于 7 秒，稳；默认档（vc53 画面平稳优先令 00:36：缓冲 7 秒）。
/// - SilentRefetch 静默重接补库存：低缓存先开画（换台口径：先开画后补库存，首帧不慢），
///   首帧后静默重接一次把库存补满窗口。
/// vc53 画面平稳优先令（老板 2026-11-14 00:36/00:52 拍板）：缓冲下限 7 秒（00:36）；
/// 预取=取满窗口（00:52）——参数给足（FillWindowMs），实际缓冲深度由源清单滑动窗口决定
/// （实测：央视 3 段约 18.5 秒、地方台 18~29 秒、大厂 CDN 源 20~30 秒），
/// 延迟 10~30 秒属老板确认取舍；无人为深度上限（参数给到确认延迟带的上限 30 秒档）。
/// 1.0.12 修正②④：重连缓存递增口径并入固定库存档（库存档兜底，不被水位自适应降回去）。
/// </summary>
public static class BufferInventory
{
    public enum Mode { SilentRefetch, StartFull }

    /// <summary>起播缓冲下限（毫秒）：vc53 画面平稳优先令（老板 2026-11-14 00:36 拍板）——缓冲 7 秒。</summary>
    public const int TargetMs = 7000;

    /// <summary>
    /// 取满窗口缓存目标（毫秒）：vc53 画面平稳优先令（老板 00:52 拍板）——预取=取满窗口，
    /// 窗口内能取多少取多少、无人为深度上限；参数给到实测源窗口的上界（大厂 CDN 源 20~30 秒），
    /// 实际深度由源清单滑动窗口与内核实况决定，延迟 10~30 秒属老板确认取舍。
    /// 真机验证口径：首帧与深度以真机数据定，不达预期加内存预取层（记台账，真机定标后处置；已有先例，不落盘）。
    /// </summary>
    public const int FillWindowMs = 30_000;

    /// <summary>默认档：老板 2026-11-25 令激活「先开画后补库存」——切 SilentRefetch（低缓存先开画，首帧后静默补满窗口）。
    /// 原 StartFull（起播即 7 秒，vc53 画面平稳优先令 00:36）为激活前的旧默认档，保留作回退档。</summary>
    public static Mode Default => Mode.SilentRefetch;

    /// <summary>SilentRefetch 档首帧出画门槛（毫秒）=1 秒（vc93 第 6 刀，老板 2026-11-26 拍板接线）：
    /// 先出画后补库存，首帧不慢——低缓存 1 秒即出画，出画后静默补满窗口。
    /// 此前 c7a0aca 只切了 Default 档未接内核门槛（空拨），内核仍写死 7 秒，本常量补上落点。</summary>
    public const int SilentRefetchOpenMs = 1000;

    /// <summary>首帧出画门槛（毫秒）按库存档分档（vc93 第 6 刀接线，老板 2026-11-26 拍板）：
    /// SilentRefetch=1 秒出画（<see cref="SilentRefetchOpenMs"/>）；StartFull=7 秒（<see cref="TargetMs"/>，出画那一刻手里正好 7 秒存货）。
    /// 只管首帧何时出画，不管取哪一帧——首帧永远取库存中能取到的最老画面（老板 2026-11-26 18:50 重申口径）。
    /// 卡后重入门槛不受此影响（仍 PrefetchCurve.StartupThreshold 12 秒）；防误烧闸 _inventoryEverFilled
    /// 判据用 ProactiveInventoryFloor（TargetMs 联动），与出画门槛无关。</summary>
    public static int FirstFrameOpenMs(Mode mode) =>
        mode == Mode.SilentRefetch ? SilentRefetchOpenMs : TargetMs;

    /// <summary>起播缓存口径：StartFull=库存档兜底（不低 7 秒）；SilentRefetch=按阶梯低缓存先开画（首帧后静默补满窗口）。</summary>
    public static int StartCachingMs(Mode mode, int ladderMs) =>
        mode == Mode.StartFull ? Math.Max(TargetMs, ladderMs) : ladderMs;

    /// <summary>补库/正常起播取满窗口缓存（毫秒）：取满窗口目标与 7 秒下限、重连阶梯取大（vc53 00:52 拍板）。</summary>
    public static int FillWindowCachingMs(int ladderMs) =>
        Math.Max(TargetMs, Math.Max(ladderMs, FillWindowMs));
}
