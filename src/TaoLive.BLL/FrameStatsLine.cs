using System;

namespace TaoLive.BLL;

/// <summary>
/// 画面信息行（v1.0.9 第④项，用户拍板）：列表旁操作提示全部去掉，该区域改为实时显示
/// 原帧率/现帧率/原分辨率/现分辨率。增强滤镜为逐帧处理、不改变帧数与分辨率，
/// 原/现实测同值时按原值单行如实展示；测量缺失时显示「检测中」，绝不编数字。
/// </summary>
public static class FrameStatsLine
{
    public static string Format(double? origFps, double? curFps, int origW, int origH, int curW, int curH)
    {
        bool noFps = origFps is null || origFps <= 0;
        bool noRes = origW <= 0 || origH <= 0;
        if (noFps && noRes) return "画面信息检测中";

        bool fpsSame = !noFps && (curFps is null || Math.Abs(curFps.Value - origFps.Value) < 0.5);
        bool resSame = noRes || (curW <= 0 || curH <= 0) || (origW == curW && origH == curH);
        var parts = new System.Collections.Generic.List<string>(2);
        if (!noFps)
            parts.Add(fpsSame
                ? $"原帧率 {origFps:0}fps＝现帧率"
                : $"原帧率 {origFps:0}fps · 现帧率 {curFps:0}fps");
        if (!noRes)
            parts.Add(resSame
                ? $"原分辨率 {origW}×{origH}＝现分辨率"
                : $"原分辨率 {origW}×{origH} · 现分辨率 {curW}×{curH}");
        return string.Join("  ", parts);
    }
}

/// <summary>
/// 分类标题统计（v1.0.9 第⑧项，用户拍板「央视(21·可连18)」）：
/// 可连=至少一个来源可达的频道数（与⑬预检口径统一）；
/// 预检未出结果（全部未探）先显总数，后台静默刷新后带可连数。
/// </summary>
public static class ListHeaderStats
{
    public static string Format(string title, int total, int connectable, bool known) =>
        known ? $"{title}({total}·可连{connectable})" : $"{title}({total})";
}
