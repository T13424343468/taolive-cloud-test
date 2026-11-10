using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// B 包界面辅助纯函数（可测）：五标签过滤 + 抽屉行装配 + 节目单格式化 + 限流提示。
/// Kotlin→C# 移植自 v4 DaoyingUiController.drawerRows / EpgBar / EpgMiniBar 口径。
/// </summary>
public static class DrawerSupport
{
    /// <summary>五标签（B 包定稿：全部/央视/卫视/地方台/收藏）。</summary>
    public enum Tab { All, Cctv, Satellite, Local, Favorite }

    public static string Label(Tab t) => t switch
    {
        Tab.All => "全部", Tab.Cctv => "央视", Tab.Satellite => "卫视",
        Tab.Local => "地方台", Tab.Favorite => "收藏",
        _ => "",
    };

    /// <summary>标签循环（左右键切标签）：顺序 全部→央视→卫视→地方台→收藏→全部。</summary>
    public static Tab NextTab(Tab t, bool rightward)
    {
        var all = (Tab[])Enum.GetValues(typeof(Tab));
        int i = Array.IndexOf(all, t);
        i = (i + (rightward ? 1 : all.Length - 1)) % all.Length;
        return all[i];
    }

    /// <summary>频道行：归属台；分组行：标题。</summary>
    public abstract record DrawerRow
    {
        public sealed record Channel(Model.Channel Data) : DrawerRow;
        public sealed record Group(string Label) : DrawerRow;
        public sealed record AiTier(string Label) : DrawerRow;
    }

    /// <summary>
    /// 抽屉行装配（v4 口径移植）：
    /// - 首行常驻画质增强档位行（确认键循环 关→低→高→关）；
    /// - 收藏标签：收藏排序结果顺次铺开；
    /// - 全部/央视/卫视/地方台：收藏成组置顶（按收藏排序），其余频道按台单顺序铺开；
    /// - 全部标签收口补「无信号」分组（只能看不能切）。
    /// </summary>
    public static List<DrawerRow> BuildRows(
        Tab tab,
        IReadOnlyList<Model.Channel> channels,
        IReadOnlyList<int> favoriteOrder,
        Func<Model.Channel, ChannelHealth> health,
        string aiTierLabel)
    {
        var rows = new List<DrawerRow> { new DrawerRow.AiTier(aiTierLabel) };
        var favOrdered = favoriteOrder
            .Select(n => channels.FirstOrDefault(c => c.Number == n))
            .Where(c => c is not null).Select(c => c!).ToList();

        IEnumerable<Model.Channel> baseList = tab switch
        {
            Tab.All => channels,
            Tab.Cctv => channels.Where(c => c.Category == Model.ChannelCategory.Cctv),
            Tab.Satellite => channels.Where(c => c.Category == Model.ChannelCategory.Provincial),
            Tab.Local => channels.Where(c => c.Category is Model.ChannelCategory.City or Model.ChannelCategory.Special or Model.ChannelCategory.Cgtn or Model.ChannelCategory.UserSource),
            Tab.Favorite => favOrdered,
            _ => channels,
        };
        var baseL = baseList.ToList();

        if (tab == Tab.Favorite)
        {
            foreach (var c in favOrdered) rows.Add(new DrawerRow.Channel(c));
            return rows;
        }

        // 收藏成组置顶（按收藏排序结果）
        var favIdx = favoriteOrder.ToList();
        var favInBase = baseL.Where(c => favIdx.Contains(c.Number))
            .OrderBy(c => favIdx.IndexOf(c.Number)).ToList();
        if (favInBase.Count > 0)
        {
            rows.Add(new DrawerRow.Group("★ 收藏"));
            foreach (var c in favInBase) rows.Add(new DrawerRow.Channel(c));
        }
        foreach (var c in baseL.Where(c => !favIdx.Contains(c.Number)))
            rows.Add(new DrawerRow.Channel(c));

        // 全部标签收口：无信号分组置底
        if (tab == Tab.All)
        {
            var off = baseL.Where(c => health(c) == ChannelHealth.Dead).ToList();
            if (off.Count > 0)
            {
                rows.Add(new DrawerRow.Group("── 无信号分组 ──"));
                foreach (var c in off) rows.Add(new DrawerRow.Channel(c));
            }
        }
        return rows;
    }

    /// <summary>节目单条目（展示口径：HH:mm）。</summary>
    public record EpgLine(string Title, DateTime Start, DateTime End);

    /// <summary>节目条 HH:mm 格式（v4 formatClock 口径）。</summary>
    public static string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>「当前节目 · 还剩 N 分钟」（v4 EpgBar 口径）。</summary>
    public static string NowLabel(EpgLine now, DateTime at) =>
        $"{Clock(now.Start)} – {Clock(now.End)} · 还剩 {Math.Max(0, (int)Math.Round((now.End - at).TotalMinutes))} 分钟";

    /// <summary>IP 限流/风控类失败的用户提示（第 3 项）：给原因给预期，不笼统。</summary>
    public static string RateLimitHint(string channelName) =>
        $"{channelName} 取流被限流，约 1 分钟后自动重试，先看别的台也行";
}
