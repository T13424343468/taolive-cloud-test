using System;

namespace TaoLive.BLL;

/// <summary>
/// AI 画质自动档实时调节器（A 类第10项·带宽/负载感知会话临时档）：
/// - 基准档 = 设备静态分档（LiveQuality.Pick 的 auto 口径），自动档效果不越过基准档；
/// - 掉帧率超阈值或播放停滞 → 立即降一档（High→Low→Off），保证流畅优先（水位偏低提前降档防卡）；
/// - 降档是<b>会话内临时档</b>：不写持久化设置（老板拍板），显示走 A⑤口径（信息行显实际档）；
/// - 平稳 10 分钟（StableRestoreMs）后自动回位一档，逐档回升封顶基准档；
/// 纯逻辑可测；调用方（UI 巡检定时器）每 5 秒喂一次实测统计，档位变化时重起当前台应用滤镜。
/// </summary>
public class SmartGovernor
{
    private readonly AiLiveTier _baseTier;
    private AiLiveTier _effective;
    private long _downgradedAtMs = long.MinValue; // 最近一次降档时刻（TickCount64 基准）

    /// <summary>降档后需平稳多久才自动回位（老板拍板：平稳 10 分钟自动回位）。</summary>
    public const long StableRestoreMs = 10 * 60 * 1000;

    /// <summary>掉帧率超过此值判为卡顿（丢弃帧 / (显示帧+丢弃帧)）。</summary>
    public const double DropRateThreshold = 0.02;

    public SmartGovernor(AiLiveTier baseTier)
    {
        _baseTier = baseTier;
        _effective = baseTier;
    }

    public AiLiveTier BaseTier => _baseTier;
    public AiLiveTier Effective => _effective;
    public bool IsDowngraded => _effective < _baseTier; // A⑩口径：会话内是否处于临时档

    /// <summary>喂一次实测：droppedRate=掉帧率（0~1），stalled=播放时钟停滞，nowMs=TickCount64。返回档位是否变化。</summary>
    public bool Tick(double droppedRate, bool stalled, long nowMs)
    {
        var before = _effective;
        if (stalled || droppedRate > DropRateThreshold)
        {
            if (_effective > AiLiveTier.Off)
            {
                _effective--; // 流畅优先：立即降档（会话内临时档）
                _downgradedAtMs = nowMs;
            }
            return _effective != before;
        }
        // 平稳恢复：距最近一次降档满 10 分钟，逐档回升（封顶基准档）
        if (_effective < _baseTier && nowMs - _downgradedAtMs >= StableRestoreMs)
        {
            _effective++;
            _downgradedAtMs = nowMs; // 再降或再升都重记时刻
            return true;
        }
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
