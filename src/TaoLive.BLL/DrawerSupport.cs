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
    /// <summary>五标签（1.0.12 UI② 定稿：全部/收藏/央视/卫视/地方台——收藏插第二位，其余不变）。</summary>
    public enum Tab { All, Favorite, Cctv, Satellite, Local }

    public static string Label(Tab t) => t switch
    {
        Tab.All => "全部", Tab.Cctv => "央视", Tab.Satellite => "卫视",
        Tab.Local => "地方台", Tab.Favorite => "收藏",
        _ => "",
    };

    /// <summary>标签循环（左右键切标签）：顺序 全部→收藏→央视→卫视→地方台→全部（1.0.12 UI② 改序后自动生效）。</summary>
    public static Tab NextTab(Tab t, bool rightward)
    {
        var all = (Tab[])Enum.GetValues(typeof(Tab));
        int i = Array.IndexOf(all, t);
        i = (i + (rightward ? 1 : all.Length - 1)) % all.Length;
        return all[i];
    }

    /// <summary>
    /// 1.0.12 UI②·打开列表页默认定位（纯函数可测）：当前台已收藏 → 收藏；
    /// 未收藏 → 按分类落央视（含 CGTN/专项，v22 第 7 项口径）/卫视（省级）/地方台（城市/自有源）；
    /// 未收藏且无归属 → 全部。
    /// </summary>
    public static Tab DefaultTab(Model.Channel? current, Func<int, bool> isFavorite)
    {
        if (current is null) return Tab.All;
        if (isFavorite(current.Number)) return Tab.Favorite;
        return current.Category switch
        {
            Model.ChannelCategory.Cctv or Model.ChannelCategory.Cgtn or Model.ChannelCategory.Special => Tab.Cctv,
            Model.ChannelCategory.Provincial => Tab.Satellite,
            Model.ChannelCategory.City or Model.ChannelCategory.UserSource => Tab.Local,
            _ => Tab.All,
        };
    }

    /// <summary>频道行：归属台；分组行：标题；空态行：无频道时的提示文案（不占焦点）。</summary>
    public abstract record DrawerRow
    {
        public sealed record Channel(Model.Channel Data) : DrawerRow;
        public sealed record Group(string Label) : DrawerRow;
        public sealed record Note(string Text) : DrawerRow;
    }

    /// <summary>
    /// 抽屉行装配（v4 口径移植，v22 第 7/8/15 项定稿）：
    /// - 画质增强档位行撤出列表页（设置页保留唯一入口，操作按钮不占频道列表）；
    /// - 收藏标签：收藏排序结果顺次铺开；
    /// - 全部/央视/卫视/地方台：收藏成组置顶（按收藏排序），其余频道按台单顺序铺开；
    /// - 全部标签收口补「无信号」分组（只能看不能切）；
    /// - 标签行装配后无任何频道行 → 补空态行（v22 第 6/15 项：收藏空/分类空如实告知，不白屏）。
    /// </summary>
    public static List<DrawerRow> BuildRows(
        Tab tab,
        IReadOnlyList<Model.Channel> channels,
        IReadOnlyList<int> favoriteOrder,
        Func<Model.Channel, ChannelHealth> health)
    {
        var rows = new List<DrawerRow>();
        var favOrdered = favoriteOrder
            .Select(n => channels.FirstOrDefault(c => c.Number == n))
            .Where(c => c is not null).Select(c => c!).ToList();

        IEnumerable<Model.Channel> baseList = tab switch
        {
            Tab.All => channels,
            // v22 第 7 项（老板 20:04 拍板）：CGTN 并回「央视」（同为国家级），专项（风云/第一/怀旧剧场、CETV、国学）随拍板并入央视口径；
            Tab.Cctv => channels.Where(c => c.Category is Model.ChannelCategory.Cctv or Model.ChannelCategory.Cgtn or Model.ChannelCategory.Special),
            Tab.Satellite => channels.Where(c => c.Category == Model.ChannelCategory.Provincial),
            // v22 第 7 项：「地方台」只放真城市台（City/自有源），不再兜「非央视非卫视」杂烩；空态走第 6/15 项
            Tab.Local => channels.Where(c => c.Category is Model.ChannelCategory.City or Model.ChannelCategory.UserSource),
            Tab.Favorite => favOrdered,
            _ => channels,
        };
        var baseL = baseList.ToList();

        if (tab == Tab.Favorite)
        {
            foreach (var c in favOrdered) rows.Add(new DrawerRow.Channel(c));
            if (favOrdered.Count == 0)
                rows.Add(new DrawerRow.Note("收藏夹是空的，对着频道按确认可收藏")); // v22 第 6/15 项
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

        // v22 第 6/15 项：装配完没有任何频道行（只有可能的分组行也没有）→ 空态提示
        if (!rows.Any(r => r is DrawerRow.Channel))
            rows.Add(new DrawerRow.Note("该分类暂无频道"));
        return rows;
    }

    /// <summary>节目单条目（展示口径：HH:mm）。</summary>
    public record EpgLine(string Title, DateTime Start, DateTime End);

    /// <summary>节目条 HH:mm 格式（v4 formatClock 口径）。</summary>
    public static string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>「当前节目 · 还剩 N 分钟」（v4 EpgBar 口径）。</summary>
    public static string NowLabel(EpgLine now, DateTime at) =>
        $"{Clock(now.Start)} – {Clock(now.End)} · 还剩 {Math.Max(0, (int)Math.Round((now.End - at).TotalMinutes))} 分钟";

    /// <summary>IP 限流/风控类失败的用户提示（第 3 项）：给原因给预期，不笼统。
    /// v22 第 2 项：时长接退避账——剩余 ≤0 显「即将自动重试」，否则按分钟给预期（纯函数可测）。</summary>
    public static string RateLimitHint(string channelName, long remainingMs)
    {
        var min = remainingMs > 0 ? (int)Math.Ceiling(remainingMs / 60000.0) : 0;
        var when = min > 0 ? $"约 {min} 分钟后自动重试" : "即将自动重试";
        return $"{channelName} 取流被限流，{when}，先看别的台也行";
    }
}
