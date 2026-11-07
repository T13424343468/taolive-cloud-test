using System;

namespace TaoLive.BLL;

/// <summary>解码方式设置项（v1.0.8 第18项，用户 23:49 拍板方案A）。</summary>
public enum DecodeMode
{
    Auto = 0,     // 自动：优先硬解，异常自动回退软解（默认）
    ForceHw = 1,  // 强制硬解
    ForceSw = 2,  // 强制软解
}

/// <summary>实际生效的解码路径。</summary>
public enum DecodeActual
{
    Hardware = 0,
    Software = 1,
}

/// <summary>
/// 芯片级优化策略（v1.0.8 第18项方案A）：
/// a) 硬解：只经标准 MediaCodec（VLC 内部调用），auto 优先硬解；已知坏档（自动回退过）直接软解；
///    硬解探活 4 秒零解码帧或 8 秒无首帧 → 自动回退软解并记日志（由 UI 持久化坏档标记）；
/// b) GPU 滤镜：设备 GLES 能力达标走 GPU 着色器路径，不达标或路径失败回退 CPU 滤镜；
/// c) 补帧交电视系统 MEMC，App 不自插帧。
/// 全部按能力探测动态启用，不硬编码机型、不碰厂商私有接口；能力不足自动降级通用路径。
/// </summary>
public static class DecodePolicy
{
    /// <summary>硬解探活：起播 4 秒内零解码帧判硬解异常（快路径）。</summary>
    public const int HwProbeMs = 4000;

    /// <summary>硬解兜底：起播 8 秒仍无首帧必回退软解（慢路径）。</summary>
    public const int HwFallbackMs = 8000;

    /// <summary>硬解失败即记坏档：本安装周期内 auto 直接软解（真机已证实部分芯片硬解黑屏）。</summary>
    public const int HwBadThreshold = 1;

    public static DecodeActual Decide(DecodeMode mode, bool hwSupported, bool hwKnownBad)
    {
        if (mode == DecodeMode.ForceSw) return DecodeActual.Software;
        if (mode == DecodeMode.ForceHw) return hwSupported ? DecodeActual.Hardware : DecodeActual.Software;
        // Auto：优先硬解，坏档记录在案则直接走软解（绝不在已知坏档上反复试错）
        return (hwSupported && !hwKnownBad) ? DecodeActual.Hardware : DecodeActual.Software;
    }

    /// <summary>硬解失败次数达阈值 → 标坏档（UI 持久化，下次启动不再试硬解）。</summary>
    public static bool ShouldMarkBad(int consecutiveHwFailures) => consecutiveHwFailures >= HwBadThreshold;

    /// <summary>设置面板解码方式循环：auto→强制硬解→强制软解→auto。</summary>
    public static DecodeMode NextMode(DecodeMode mode) => mode switch
    {
        DecodeMode.Auto => DecodeMode.ForceHw,
        DecodeMode.ForceHw => DecodeMode.ForceSw,
        _ => DecodeMode.Auto,
    };

    public static string Label(DecodeMode mode) => mode switch
    {
        DecodeMode.Auto => "自动",
        DecodeMode.ForceHw => "强制硬解",
        _ => "强制软解",
    };
}

/// <summary>
/// GPU 滤镜路径决策（18b）：能力探测（GLES 主版本）达标走 GPU 着色器路径，
/// 不达标或 GPU 路径失败（着色器编译/运行异常）回退 CPU 滤镜——功能完整性不受影响。
/// 能力未知（null）按保守 CPU 处理，绝不在未知设备上硬开。
/// </summary>
public static class FilterPathDecider
{
    /// <summary>GLES 2.0 起具备基本着色器能力。</summary>
    public const int GlesMajorForGpu = 2;

    public static string Decide(int? glesMajor, bool gpuPathFailed) =>
        (!gpuPathFailed && glesMajor is >= GlesMajorForGpu) ? "gpu" : "cpu";
}
