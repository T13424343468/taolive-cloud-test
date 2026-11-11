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

    /// <summary>
    /// v1.0.10 修复⑤：带重试口径与 AI 档位的画面信息行。
    /// - 两项全缺且重试已到限 → 显「--」不留「检测中」；未到限（还在重试）→ 维持「检测中」；
    /// - tierName 非 null 时并显 AI 画质档位（口径同【二】三档全中文），随档位变化刷新。
    /// </summary>
    public static string FormatEx(double? origFps, double? curFps, int origW, int origH, int curW, int curH,
        string? tierName, bool statsExhausted)
    {
        bool noFps = origFps is null || origFps <= 0;
        bool noRes = origW <= 0 || origH <= 0;
        string core;
        if (noFps && noRes)
        {
            core = statsExhausted
                ? $"画面信息 {StatsLinePolicy.UnavailableText}"  // 取不到显「--」
                : "画面信息检测中";                              // 还在重试周期内
        }
        else
        {
            core = Format(
                (noFps && statsExhausted) ? 0 : origFps,
                curFps, origW, origH, curW, curH);
            // 部分缺失到限：缺失项也显「--」不静默
            if (noFps && statsExhausted) core += "  原帧率 --";
        }
        return string.IsNullOrEmpty(tierName) ? core : $"{core}  画质增强：{tierName}";
    }

    // ---- v22 第 9 项：抽屉画面信息改短格式（老板 20:09 令、20:36 加严拍板）----
    // 帧率与分辨率一行、画质独占一行；一律真实画面真实数据，测量缺失显「检测中」、到限显「--」，绝不编数。
    // 两行固定行高提前占位（UI 层一次量足），数值跳变只换文字不换高度。

    /// <summary>帧率段：同值只显「帧率:25fps」，变化显「帧率:25fps&gt;30fps」（原&gt;现，实测值口径）。</summary>
    public static string FpsSegment(double? origFps, double? curFps, bool statsExhausted)
    {
        if (origFps is null || origFps <= 0)
            return statsExhausted ? "帧率:--" : "帧率:检测中";
        bool same = curFps is null || Math.Abs(curFps.Value - origFps.Value) < 0.5;
        return same ? $"帧率:{origFps:0}fps" : $"帧率:{origFps:0}fps>{curFps:0}fps";
    }

    /// <summary>分辨率档位（按宽度向上归档）：≤1280=720P、≤1920=1080P、≤2560=2K、≤4096=4K、其余 8K。</summary>
    public static string ResBucket(int w)
    {
        if (w <= 0) return "";
        if (w <= 1280) return "720P";
        if (w <= 1920) return "1080P";
        if (w <= 2560) return "2K";
        if (w <= 4096) return "4K";
        return "8K";
    }

    /// <summary>分辨率段：同档只显一档（宽高并入档位文案），变化显「分辨率:1080P&gt;2K」。</summary>
    public static string ResSegment(int origW, int origH, int curW, int curH, bool statsExhausted)
    {
        if (origW <= 0 || origH <= 0)
            return statsExhausted ? "分辨率:--" : "分辨率:检测中";
        bool curValid = curW > 0 && curH > 0;
        var orig = ResBucket(origW);
        if (!curValid) return $"分辨率:{orig}";
        var cur = ResBucket(curW);
        return cur == orig ? $"分辨率:{orig}" : $"分辨率:{orig}>{cur}";
    }

    /// <summary>画质段（v22 第 9/17 项口径）：实际档与设置档一致显「生效」，临时降档如实显「未生效」；关档只显「关」。</summary>
    public static string TierSegment(string? tierName, bool effective)
    {
        if (string.IsNullOrEmpty(tierName) || tierName == "关") return "画质:关";
        return $"画质:{tierName}:{(effective ? "生效" : "未生效")}";
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
