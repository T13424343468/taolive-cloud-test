using System;

namespace TaoLive.Model;

/// <summary>
/// 设备档位（UI 启动时写入，逻辑测试默认不设为低配）：
/// 低配设备（32 位芯片/内存不足 3GB）软解带不动 4K/8K，节目单直接不收超高清台，把黑屏风险拦在门外。
/// </summary>
public static class DeviceProfile
{
    /// <summary>低配设备标志：true 时 4K/8K 台不进节目单。</summary>
    public static bool HideUhd { get; set; }

    /// <summary>AI 画质档位设置（v1.0.10 统一口径：auto=自动默认/low/high/off；旧版 smart 兼容按 auto 解析）。</summary>
    public static string AiEnhanceTierSetting { get; set; } = "auto";
}
