using System;

namespace TaoLive.BLL;

/// <summary>
/// 频道列表翻页（v1.0.8，用户 2026-11-07 拍板：左右键管列表）：
/// - 左键（dir=+1）向下翻一屏：当前可见最后一项之后的第一个条目移到可视区顶部；
///   剩余不足一屏直接到底；右键（dir=-1）同逻辑向上，不足一屏直接到顶。
/// 纯函数，可测。
/// </summary>
public static class ListPaging
{
    public static int NextFirstVisible(int itemCount, int firstVisible, int visibleCount, int dir)
    {
        if (itemCount <= 0) return 0;
        var vis = Math.Max(1, visibleCount);
        if (dir > 0)
        {
            int bottom = Math.Max(0, itemCount - vis); // 到底：最后 vis 条充满可视区
            return Math.Min(firstVisible + vis, bottom);
        }
        return Math.Max(0, firstVisible - vis); // 到顶
    }
}

/// <summary>
/// 设置页档位循环（v1.0.8 第17项，用户拍板三档）：
/// 智能（默认）→ 低 → 高 → 智能；旧版本遗留 auto 值映射回智能档。
/// </summary>
public static class SettingsCycler
{
    /// <summary>
    /// 画质档循环（v1.0.10 用户拍板：统一三档「自动/低/高」全中文）：auto→low→high→auto 闭环；
    /// 旧版「smart（智能）」值按自动档口径循环（smart→low），界面不再出现「智能」。
    /// </summary>
    public static string NextAiTier(string current)
    {
        var cur = (current ?? "auto").Trim().ToLowerInvariant();
        return cur switch
        {
            "low" => "high",
            "high" => "auto",
            _ => "low", // auto / 旧版 smart / 未知值 → 低（循环下一档）
        };
    }
}
