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
