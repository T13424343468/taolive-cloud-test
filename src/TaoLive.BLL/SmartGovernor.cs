using System;

namespace TaoLive.BLL;

/// <summary>
/// AI 画质自动档实时调节器（v1.0.8 第17项落地、v1.0.10 统一口径：原「智能」能力并入自动档）：
/// - 基准档 = 设备静态分档（LiveQuality.Pick 的 auto 口径），自动档效果不越过基准档；
/// - 掉帧率超阈值或播放停滞 → 立即降一档（High→Low→Off），保证流畅优先；
/// - 连续 CleanTicksToUpgrade 个巡检周期无掉帧 → 回升一档（封顶基准档）；
/// 纯逻辑可测；调用方（UI 巡检定时器）每 5 秒喂一次实测统计，档位变化时重起当前台应用滤镜。
/// </summary>
public class SmartGovernor
{
    private readonly AiLiveTier _baseTier;
    private AiLiveTier _effective;
    private int _cleanTicks;

    /// <summary>连续无掉帧多少个周期后回升一档（5 秒周期 × 6 = 30 秒平稳才回升）。</summary>
    public const int CleanTicksToUpgrade = 6;

    /// <summary>掉帧率超过此值判为卡顿（丢弃帧 / (显示帧+丢弃帧)）。</summary>
    public const double DropRateThreshold = 0.02;

    public SmartGovernor(AiLiveTier baseTier)
    {
        _baseTier = baseTier;
        _effective = baseTier;
    }

    public AiLiveTier BaseTier => _baseTier;
    public AiLiveTier Effective => _effective;

    /// <summary>喂一次实测：droppedRate=掉帧率（0~1），stalled=播放时钟停滞。返回档位是否变化。</summary>
    public bool Tick(double droppedRate, bool stalled)
    {
        var before = _effective;
        if (stalled || droppedRate > DropRateThreshold)
        {
            _cleanTicks = 0;
            if (_effective > AiLiveTier.Off) _effective--; // 流畅优先：立即降档
            return _effective != before;
        }
        if (_effective < _baseTier)
        {
            _cleanTicks++;
            if (_cleanTicks >= CleanTicksToUpgrade)
            {
                _cleanTicks = 0;
                _effective++; // 有余量回升，封顶基准档
                return true;
            }
            return false;
        }
        _cleanTicks = 0;
        return false;
    }
}

/// <summary>
/// 列表页顶部 AI 效果信息行（v1.0.8 第17项 c，用户拍板）：
/// 原/现帧数与原/现分辨率。增强滤镜为逐帧处理、不改变帧数与分辨率，原/现实测同值时按原值单行如实展示；
/// 未开增强（off）显示原值。测量缺失时显示「检测中」。
/// </summary>
public static class AiEffectHeader
{
    public static string Format(double? fps, int width, int height, string tierLabel)
    {
        if (fps is null || fps <= 0) return "画面信息检测中";
        string main = $"{fps.Value:0}fps" + (width > 0 && height > 0 ? $" · {width}×{height}" : "");
        var tier = (tierLabel ?? "smart").Trim().ToLowerInvariant();
        if (tier is "off" or "smart" && tier == "off") return main + "（原画直出）";
        if (tier == "off") return main + "（原画直出）";
        return main + $"（AI {TierName(tier)} 增强中）";
    }

    private static string TierName(string tier) => tier switch
    {
        "smart" => "自动", // v1.0.10 统一口径：智能档并入自动档，界面只显「自动/低/高」
        "low" => "低",
        "high" => "高",
        _ => tier,
    };
}
