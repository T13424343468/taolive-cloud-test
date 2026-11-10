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
/// - 自动档（auto，默认，v1.0.10 统一口径）：基准按内存静态分档，运行中由 SmartGovernor
///   按掉帧/卡顿实时升降（原「智能」档能力并入自动档，用户拍板：流畅优先，有余量再升，封顶基准档）；
/// - auto 口径（保守优先，绝不带不动硬开）：<2GB 内存直接关；
///   2~3GB 低配开低档；≥3GB 开高档；
/// - 用户显式 off=强制关；显式 low 尊重选择；显式 high 在 <2GB 仍降为低档（防卡死）；
/// - 旧版 smart 值按 auto 口径解析（v1.0.10 统一命名，界面只显「自动/低/高」）。
/// 档位只在本文件变化，纯函数可测。
/// </summary>
public static class LiveQuality
{
    public static AiLiveTier Pick(string setting, bool abi32, long totalMemBytes)
    {
        const long MB = 1024L * 1024;
        bool memAtLeast(long mb) => totalMemBytes >= mb * MB;
        // A类第8项：32 位机软解负担封顶——高档一律压到低档（Off 尊重用户、auto 内存地板保留，
        // 封顶=天花板下沉，不是地板抬高：auto <2GB 仍为关）
        AiLiveTier tier = (setting ?? "auto").Trim().ToLowerInvariant() switch
        {
            "off" => AiLiveTier.Off,
            "low" => AiLiveTier.Low,
            "high" => memAtLeast(2000) ? AiLiveTier.High : AiLiveTier.Low, // <2GB 带不动高档，降级不硬开
            _ => // auto / 旧版 smart / 未知值
                totalMemBytes <= 0 ? AiLiveTier.Off
                : memAtLeast(3000) ? AiLiveTier.High
                : memAtLeast(2000) ? AiLiveTier.Low
                : AiLiveTier.Off,
        };
        return abi32 && tier == AiLiveTier.High ? AiLiveTier.Low : tier;
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

    /// <summary>
    /// 档位设置值 → 界面显示名（v1.0.10 用户拍板：统一三档全中文「自动/低/高」，绝不出「智能」）。
    /// 旧版 smart 值按自动档口径显示（能力已并入自动档）；未知值按自动兜底。
    /// </summary>
    public static string DisplayName(string setting) =>
        (setting ?? "auto").Trim().ToLowerInvariant() switch
        {
            "low" => "低",
            "high" => "高",
            _ => "自动", // auto / 旧版 smart / 未知值
        };

    /// <summary>实际生效档 → 界面显示名（A 类第5项·信息行显实际档口径：显示在跑的，不是选的）。</summary>
    public static string TierName(AiLiveTier tier) => tier switch
    {
        AiLiveTier.Off => "关",
        AiLiveTier.Low => "低",
        _ => "高",
    };
}
