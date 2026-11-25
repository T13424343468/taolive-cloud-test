namespace TaoLive.Model;

/// <summary>频道分类：六大来源组，顺序即列表分组顺序（收藏组动态置顶，不在此列）。引擎搬回：vc53 Model.Channel 逐字移植（vc53 口径为准）。</summary>
public enum ChannelCategory
{
    Cctv = 0,      // 央视系 1~24
    Cgtn = 1,      // CGTN 25~30
    Special = 2,   // 专项（CETV/中国天气）31~36
    Provincial = 3,// 省台 37 起
    City = 4,      // 城市台
    UserSource = 5 // 自有源（接在最后顺次编号）
}

/// <summary>频道：核心数据单元。编号一经发布不再变更。引擎搬回：vc53 原型（Number/SourceId/EpgKey/Unavailable 等字段齐备，供预检/账本/目录共用）。</summary>
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

    // ---- 2.0 原有代码兼容面（引擎搬回适配）：record 语义映射到 vc53 原型字段 ----
    public string Id { get => SourceId; set => SourceId = value; }          // 2.0 Id ≙ vc53 SourceId
    public System.Collections.Generic.IReadOnlyList<SourceEndpoint> Sources => SourceEndpoints; // 2.0 Sources ≙ vc53 StreamUrl 多档（SourceEndpoints 见下）
    public System.Collections.Generic.List<SourceEndpoint> SourceEndpoints { get; set; } = new();
    public bool IsValid => !string.IsNullOrWhiteSpace(SourceId) && !string.IsNullOrWhiteSpace(Name) && SourceEndpoints.Count > 0;

    /// <summary>2.0 原有代码按字符串分类过滤（CategoryOrder = 央视/CGTN/专项/省台/城市台）。引擎搬回适配：枚举→字符串兼容面。</summary>
    public string CategoryName => Category switch
    {
        ChannelCategory.Cctv => "央视",
        ChannelCategory.Cgtn => "CGTN",
        ChannelCategory.Special => "专项",
        ChannelCategory.Provincial => "省台",
        ChannelCategory.City => "城市台",
        _ => "自有源",
    }; // // ENGINE-WIRE: 新分类接入时同步 ChannelTable.CategoryOrder

    // ---- 2.0 原有调用点兼容构造器（引擎搬回适配）：ChannelTable 等按 (Id,Name,Category,Sources) 建 Channel ----
    public Channel() { }
    public Channel(string id, string name, string category, System.Collections.Generic.IReadOnlyList<SourceEndpoint> sources)
    {
        SourceId = id;
        Name = name;
        Category = category switch
        {
            "央视" => ChannelCategory.Cctv,
            "CGTN" => ChannelCategory.Cgtn,
            "专项" => ChannelCategory.Special,
            "省台" => ChannelCategory.Provincial,
            "城市台" => ChannelCategory.City,
            "自有源" => ChannelCategory.UserSource,
            _ => ChannelCategory.UserSource,
        }; // // ENGINE-WIRE: 字符串分类仅内置表使用；新分类接入时在此登记
        SourceEndpoints = new System.Collections.Generic.List<SourceEndpoint>(sources);
    }
}

public class CategoryGroup
{
    public ChannelCategory Category { get; set; }
    public string Title { get; set; } = "";
    public List<Channel> Channels { get; set; } = new();
}
