using System;

namespace TaoLive.BLL;

/// <summary>
/// 缓冲水位口径（v1.0.10 修复⑨B+⑩）：默认贴直播边缘零延迟感；
/// 水位下降/网络抖动时主动落后一个安全水位吸收抖动（按设备内存自适应：
/// 内存足最高约 60 秒，低配自动缩到 30–45 秒），全程内存环形缓冲不落磁盘；
/// 网络恢复平稳后平滑追回直播边缘。水位是看门狗自救的缓冲池：先吃池子再动手。
/// </summary>
public enum BufferAction { HoldEdge, BuildUp, CatchUp }

public static class BufferPolicy
{
    public const long MB = 1024L * 1024;

    /// <summary>安全水位上限（秒）：内存充裕 60；低配 45；极低配 30。</summary>
    public static int MaxWatermarkSeconds(long totalMemBytes)
    {
        if (totalMemBytes <= 0) return 30;           // 未知内存按低配保守
        if (totalMemBytes >= 3L * 1024 * MB) return 60;
        if (totalMemBytes >= 2L * 1024 * MB) return 45;
        return 30;
    }

    /// <summary>
    /// 喂一次水位（秒）与网络平稳状态：
    /// - 水位充足且平稳 → 追回直播边缘（延迟归零）；
    /// - 水位低或抖动 → 攒水位（提前介入，不等 6 秒卡死）；
    /// - 水位贴边且平稳 → 保持边缘（默认零延迟感）。
    /// </summary>
    public static BufferAction Observe(double bufferedSeconds, bool networkSteady, long totalMemBytes)
    {
        int max = MaxWatermarkSeconds(totalMemBytes);
        double low = max / 3.0;               // 低水位线：上限的 1/3（60→20s，30→10s）
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
    /// 真机验证口径：首帧与深度以真机数据定，不达预期下一批加内存预取层（已有先例，不落盘）。
    /// </summary>
    public const int FillWindowMs = 30_000;

    /// <summary>默认档：起播即 7 秒（vc53 画面平稳优先令 00:36），真机验证「静默重接保帧」后可切 SilentRefetch。</summary>
    public static Mode Default => Mode.StartFull;

    /// <summary>起播缓存口径：StartFull=库存档兜底（不低 7 秒）；SilentRefetch=按阶梯低缓存先开画（首帧后静默补满窗口）。</summary>
    public static int StartCachingMs(Mode mode, int ladderMs) =>
        mode == Mode.StartFull ? Math.Max(TargetMs, ladderMs) : ladderMs;

    /// <summary>补库/正常起播取满窗口缓存（毫秒）：取满窗口目标与 7 秒下限、重连阶梯取大（vc53 00:52 拍板）。</summary>
    public static int FillWindowCachingMs(int ladderMs) =>
        Math.Max(TargetMs, Math.Max(ladderMs, FillWindowMs));
}
