using System;

using System.Collections.Generic;

using System.Linq;

using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>

/// 频道列表五标签（方案书收口批②照 vc53 DrawerSupport 口径搬运；1.0.12 UI② 定稿名册）：

/// 全部/收藏/央视/卫视/地方台——收藏插第二位，其余不变；左右键循环切标签。

/// 过滤口径照 vc53：CGTN/专项并入「央视」（同为国家级）；「地方台」只放城市台/自有源，不兜杂烩。

/// </summary>

public static class ListTabs

{

    public enum Tab { All, Favorite, Cctv, Satellite, Local }

    public static string Label(Tab t) => t switch

    {

        Tab.All => "全部", Tab.Cctv => "央视", Tab.Satellite => "卫视",

        Tab.Local => "地方台", Tab.Favorite => "收藏",

        _ => "",

    };

    /// <summary>标签循环（左右键切标签）：顺序 全部→收藏→央视→卫视→地方台→全部（枚举序即行序）。</summary>

    public static Tab NextTab(Tab t, bool rightward)

    {

        var all = (Tab[])Enum.GetValues(typeof(Tab));

        int i = Array.IndexOf(all, t);

        i = (i + (rightward ? 1 : all.Length - 1)) % all.Length;

        return all[i];

    }

    /// <summary>按标签过滤频道（收藏标签=收藏频道顺次铺开；其余照 vc53 分类口径）。</summary>

    public static IReadOnlyList<Channel> Filter(Tab tab, IReadOnlyList<Channel> channels, Func<Channel, bool> isFavorite)
        => Filter(tab, channels, isFavorite, null);

    /// <summary>带收藏顺序的过滤：收藏页签按 favoriteOrder（收藏先后）顺次铺开，未入册的按频道表序（vc53 排序口径）。</summary>
    public static IReadOnlyList<Channel> Filter(Tab tab, IReadOnlyList<Channel> channels, Func<Channel, bool> isFavorite, IReadOnlyList<int>? favoriteOrder)

    {

        IEnumerable<Channel> q = tab switch

        {

            Tab.Favorite => OrderFavorites(channels, isFavorite, favoriteOrder),

            // vc53 v22 第 7 项（老板拍板）：CGTN 并回「央视」；专项（风云/第一/怀旧剧场、CETV、国学）随拍板并入央视口径

            Tab.Cctv => channels.Where(c => c.Category is ChannelCategory.Cctv or ChannelCategory.Cgtn or ChannelCategory.Special),

            Tab.Satellite => channels.Where(c => c.Category == ChannelCategory.Provincial),

            // vc53 v22 第 7 项：「地方台」只放真城市台（City/自有源），不再兜「非央视非卫视」杂烩

            Tab.Local => channels.Where(c => c.Category is ChannelCategory.City or ChannelCategory.UserSource),

            _ => channels,

        };

        return q.ToList();

    }

    /// <summary>收藏顺序铺开：order 在前的在前；order 外的按频道表序缀后（vc53 TopGroup 口径 + 未入册兜底）。</summary>
    private static List<Channel> OrderFavorites(IReadOnlyList<Channel> channels, Func<Channel, bool> isFavorite, IReadOnlyList<int>? favoriteOrder)
    {
        var result = new List<Channel>();
        var used = new HashSet<int>();
        if (favoriteOrder != null)
        {
            foreach (var n in favoriteOrder)
            {
                var ch = channels.FirstOrDefault(c => c.Number == n);
                if (ch != null && isFavorite(ch) && used.Add(ch.Number)) result.Add(ch);
            }
        }
        foreach (var ch in channels)
            if (isFavorite(ch) && used.Add(ch.Number)) result.Add(ch);
        return result;
    }

}
