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
