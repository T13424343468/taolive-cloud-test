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

        bool fpsSame = !noFps && origFps.HasValue && (curFps is null || Math.Abs(curFps.Value - origFps.Value) < 0.5); // noFps=false 蕴含 origFps 有值，显式补判空收窄
        bool resSame = noRes || (curW <= 0 || curH <= 0) || (origW == curW && origH == curH);
        var parts = new System.Collections.Generic.List<string>(2);
        if (!noFps)
            parts.Add(fpsSame
                ? $"原帧率 {origFps:0}fps＝现帧率"
                : $"原帧率 {origFps:0}fps · 现帧率 {curFps:0}fps");
        if (!noRes)
            parts.Add(resSame
                ? $"原分辨率 {origW}×{origH}"
                : $"原分辨率 {origW}×{origH} · 现 {curW}×{curH}"); // v1.0.20 ⑩：显式标「原」，缩放补「·现」
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
        return string.IsNullOrEmpty(tierName) ? core : $"{core}  AI画质：{tierName}";
    }

    /// <summary>
    /// v1.0.12 列表页信息行改造（老板拍板）：动态信息拆为三行独占，画质最上、依次分辨率、帧率；
    /// 行高锁死、提前占位（「检测中」/「--」），只换字不动结构，与原标题各不干扰。
    /// </summary>
    public static string QualityLine(string? tierName) =>
        string.IsNullOrEmpty(tierName) ? $"画质 {StatsLinePolicy.UnavailableText}" : $"画质 {tierName}";

    /// <summary>
    /// v1.0.20 ⑪画质档如实（纯函数）：显示实际生效档位——SmartGovernor 降级与 <2GB 强制降档如实透出，
    /// 显示值=生效值，不吃安慰剂（如「画质 高·本机降为低档」/「画质 自动·低档生效」）。
    /// memoryText 非 null/非空时同行右侧追加（如「｜ 内存 1.2/1.9GB」），缺失省略不挡播放。
    /// </summary>
    public static string QualityLineEx(string? settingName, string? effectiveTierName, string? memoryText)
    {
        var setting = string.IsNullOrWhiteSpace(settingName) ? "未知" : settingName.Trim();
        var effective = string.IsNullOrWhiteSpace(effectiveTierName) ? "未知" : effectiveTierName.Trim();
        string text;
        if (string.Equals(setting, effective, StringComparison.Ordinal))
        {
            text = $"画质 {setting}"; // 生效=设置：如实单名
        }
        else
        {
            text = setting switch
            {
                "自动" => effective == "关" ? "画质 自动·增强未生效" : $"画质 自动·{effective}档生效",
                _ => effective == "关" ? $"画质 {setting}·本机降为关闭" : $"画质 {setting}·本机降为{effective}档",
            };
        }
        if (string.IsNullOrWhiteSpace(memoryText)) return text;
        return $"{text}｜ {memoryText.Trim()}";
    }

    /// <summary>内存段（v1.0.20 ⑪纯函数）：占用=总内存−可用；未知/异常（≤0）返回空串省略，不挡播放。</summary>
    public static string MemorySegment(long totalMemBytes, long availMemBytes)
    {
        if (totalMemBytes <= 0) return ""; // 内存未知：整段省略
        double total = totalMemBytes / 1024.0 / 1024 / 1024;
        double used = Math.Max(0, total - Math.Max(0, availMemBytes) / 1024.0 / 1024 / 1024);
        return $"内存 {used:0.#}/{total:0.#}GB";
    }

    public static string ResLine(int origW, int origH, int curW, int curH, bool statsExhausted)
    {
        bool noRes = origW <= 0 || origH <= 0;
        if (noRes) return statsExhausted ? $"分辨率 {StatsLinePolicy.UnavailableText}" : "分辨率 检测中";
        bool curValid = curW > 0 && curH > 0;
        if (curValid && (origW != curW || origH != curH))
            return $"原分辨率 {origW}×{origH} · 现 {curW}×{curH}"; // v1.0.20 ⑩：显式标「原」，缩放补「·现」
        return $"原分辨率 {origW}×{origH}"; // 原=现：显式标「原」（老板 22:26 反馈「原分辨率也没有显示」）
    }

    public static string FpsLine(double? origFps, double? curFps, bool statsExhausted)
    {
        bool noFps = origFps is null || origFps <= 0;
        if (noFps) return statsExhausted ? $"帧率 {StatsLinePolicy.UnavailableText}" : "帧率 检测中";
        bool same = curFps is null || (origFps.HasValue && Math.Abs(curFps.Value - origFps.Value) < 0.5); // 前行已排除无帧率，显式补判空收窄
        if (same) return $"帧率 {origFps:0}fps";
        return $"帧率 {curFps:0}fps（原{origFps:0}fps）";
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
