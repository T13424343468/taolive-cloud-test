using System.Collections.Generic;

namespace TaoLive.Model;

/// <summary>频道分类：六大来源组，顺序即列表分组顺序（收藏组动态置顶，不在此列）。</summary>
public enum ChannelCategory
{
    Cctv = 0,      // 央视系 1~24
    Cgtn = 1,      // CGTN 25~30
    Special = 2,   // 专项（CETV/中国天气）31~36
    Provincial = 3,// 省台 37 起
    City = 4,      // 城市台
    UserSource = 5 // 自有源（接在最后顺次编号）
}

/// <summary>频道：道映的核心数据单元。编号一经发布不再变更。</summary>
public class Channel
{
    public int Number { get; set; }             // 频道号（数字键直达用）
    public string Name { get; set; } = "";      // 台名（如 CCTV-1 综合）
    public ChannelCategory Category { get; set; }
    public string SourceId { get; set; } = "";  // 归属适配器 ID
    public string EntryUrl { get; set; } = "";  // 官方入口页
    public string StreamUrl { get; set; } = ""; // 解析出的播放地址（可多档）
    public string Referer { get; set; } = "";   // 需适配源补头
    public string UserAgent { get; set; } = "";
    public List<string> QualityTiers { get; set; } = new(); // 清晰度档位
    public string Verdict { get; set; } = "";   // 直接接/需适配/接不进（含原因）
    public bool Unavailable { get; set; }       // 接不进的台标灰说明，不挂待定
    public string EpgKey { get; set; } = "";    // EPG 通道键（央视为 cctv 频道标识）
}

public class CategoryGroup
{
    public ChannelCategory Category { get; set; }
    public string Title { get; set; } = "";
    public List<Channel> Channels { get; set; } = new();
}
