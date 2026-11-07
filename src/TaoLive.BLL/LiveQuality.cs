using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

/// <summary>AI 画质实时档（直播版口径）：关 / 低档（细节锐化）/ 高档（锐化+画面微调）。</summary>
public enum AiLiveTier
{
    Off = 0,
    Low = 1,
    High = 2,
}

/// <summary>
/// AI 画质实时档（直播专用， TaoPlay 同源同设置项，播放链路实时生效）：
/// - 直播流无法走 TaoPlay 离线超分（文件增强写回），实时档用播放器内建滤镜：
///   低档=细节锐化（sharpen），高档=细节锐化+对比/饱和微调（sharpen,adjust）；
/// - 智能档（smart，默认）：基准按 auto 口径静态分档，运行中由 SmartGovernor 按掉帧/卡顿实时升降
///   （v1.0.8 第17项，用户拍板：流畅优先，有余量再升，封顶基准档）；
/// - auto 口径（保守优先，绝不带不动硬开）：<2GB 内存直接关；
///   2~3GB 低配开低档；≥3GB 开高档；
/// - 用户显式 off=强制关；显式 low 尊重选择；显式 high 在 <2GB 仍降为低档（防卡死）。
/// 档位只在本文件变化，纯函数可测。
/// </summary>
public static class LiveQuality
{
    public static AiLiveTier Pick(string setting, bool abi32, long totalMemBytes)
    {
        const long MB = 1024L * 1024;
        bool memAtLeast(long mb) => totalMemBytes >= mb * MB;
        switch ((setting ?? "auto").Trim().ToLowerInvariant())
        {
            case "off":
                return AiLiveTier.Off;
            case "low":
                return AiLiveTier.Low;
            case "high":
                return memAtLeast(2000) ? AiLiveTier.High : AiLiveTier.Low; // <2GB 带不动高档，降级不硬开
            case "smart": // 智能档基准=auto 口径（运行中由 SmartGovernor 动态升降）
            default: // auto 及未知值
                if (totalMemBytes <= 0) return AiLiveTier.Off;
                if (memAtLeast(3000)) return AiLiveTier.High;
                if (memAtLeast(2000)) return AiLiveTier.Low;
                return AiLiveTier.Off;
        }
    }

    /// <summary>档位 → 播放器画质滤镜参数（键值对；Off 返回空，纯原画直出）。</summary>
    public static IReadOnlyList<(string Key, string Value)> OptionsFor(AiLiveTier tier) => tier switch
    {
        AiLiveTier.Low => new List<(string Key, string Value)>
        {
            ("video-filter", "sharpen"),
            ("sharpen-amplitude", "0.8"),
        },
        AiLiveTier.High => new List<(string Key, string Value)>
        {
            ("video-filter", "sharpen,adjust"),
            ("sharpen-amplitude", "0.8"),
            ("contrast", "1.08"),
            ("saturation", "1.12"),
            ("gamma", "1.02"),
        },
        _ => new List<(string Key, string Value)>(),
    };
}
