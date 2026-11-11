using System.Collections.Generic;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 地方台候选源（v22 第 16 项，2026-11-10 复验 18/18 全活，台账 verify_tool/city_channel_candidates.json）：
/// 明文 HLS 直连，无签名/无防盗链，取流走 StreamResolver 默认直连分支（_ => ch.StreamUrl）。
/// 分类口径（老板 20:41 拍板）：13 路城市/县级台归 City，「地方台」标签直接点亮；
/// 甘肃经济/福建经济为省属经济频道归 Provincial（卫视口径）；浙江系 3 路已在 CztvSource，不重号。
/// EPG 无官方映射（CntvEpgClient 覆盖央视频系），EpgKey 留空即节目单静默隐藏（没数据不硬造）。
/// 需真机复测。
/// </summary>
public class CityCandidatesSource : IChannelSource
{
    public string SourceId => "citycdn";

    /// <summary>(台名, 直连地址, 分类)。</summary>
    public static readonly (string Name, string Url, ChannelCategory Cat)[] Channels =
    {
        ("七台河新闻综合", "http://live.qthnews.org.cn:1935/live/live1/800k/tzwj_video.m3u8", ChannelCategory.City),
        ("万荣综合", "http://60.222.246.220:19433/hls1.m3u8", ChannelCategory.City),
        // 地址与台账逐字一致（复验实锤口径，含既有查询串，不自作聪明删改）
        ("上虞新闻综合", "http://l.cztvcloud.com/channels/lantian/SXshangyu1/720p.m3u8?zzhed", ChannelCategory.City),
        ("东丰综合", "http://stream3.jlntv.cn:80/aac_dfgb/playlist.m3u8", ChannelCategory.City),
        ("东莞新闻综合", "https://stream.sun0769.com/dgrtv1/mp4tv1_800/index.m3u8", ChannelCategory.City),
        ("乐至综合", "http://175.155.106.72:89/live1/live1.m3u8?zsicd", ChannelCategory.City),
        ("伊犁经济法制", "http://110.153.180.106:55555/out_4/index.m3u8", ChannelCategory.City),
        ("清河经济综艺", "https://jwcdnqx.hebyun.com.cn/live/qinghe1/1500k/tzwj_video.m3u8", ChannelCategory.City),
        ("烟台经济科技", "http://live.yantaitv.cn/live/27f84144e95a4652ae9e5c211b2a6b55/405f3e33ba384aa0a16014d0becd1261-1.m3u8", ChannelCategory.City),
        ("苏州社会经济", "https://live-auth.51kandianshi.com/szgd/csztv2.m3u8", ChannelCategory.City),
        ("东阳影视生活", "http://l.cztvcloud.com/channels/lantian/SXdongyang1/720p.m3u8", ChannelCategory.City),
        ("南宁影视娱乐", "http://hls.nntv.cn/nnlive/YSYL_244.m3u8", ChannelCategory.City),
        ("哈尔滨影视", "http://stream.hrbtv.net/yspd/sd/live.m3u8?zheild", ChannelCategory.City),
        ("甘肃经济", "https://satellitepull.cnr.cn/live/wxgshhzs/playlist.m3u8", ChannelCategory.Provincial),
        ("福建经济", "http://satellitepull.cnr.cn/live/wx32fjdnjjgb/playlist.m3u8", ChannelCategory.Provincial),
    };

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>(Channels.Length);
        foreach (var (name, url, cat) in Channels)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = cat,
                SourceId = SourceId,
                StreamUrl = url,
                QualityTiers = new List<string> { "auto" },
                Verdict = "可直接接（2026-11-10 复验 18/18 全活：清单+分片；需真机复测）",
                EpgKey = "",
            });
        }
        return list;
    }
}
