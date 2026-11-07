using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 内蒙古·腾格里网 nmtv（2026-11-07 第二轮实测：API 解密+清单+分片全通，10 路电视频道）。
/// API: https://api-bt.nmtv.cn/broadcast/list?type=1（Client-Type: web + Referer nmtv.cn）。
/// 响应 = base64(XXTEA 密文)，key=5b28bae827e651b3；解密后 data[].title / data[].data.streamUrl
/// （腾讯云防盗链短效，切台/重连重取整表）。播放域名 livestream-bt.nmtv.cn 明文 TS。
/// 证据：调研/长尾移植/内蒙古腾格里-已打通.md。需真机复测。
/// </summary>
public class NmtvSource : IChannelSource
{
    public string SourceId => "nmtv";

    public const string ApiBase = "https://api-bt.nmtv.cn/broadcast/list?type=1";
    public const string Referer = "https://www.nmtv.cn/";
    public const string ClientType = "web";

    /// <summary>XXTEA 密钥（前端 bundle 提取，2026-11-07 实测有效）。</summary>
    public const string Key = "5b28bae827e651b3";

    /// <summary>已确认的 9 路频道（2026-11-07 全量真连 9/10 通过；「包头」一路整表匹配不上台名取流失败，移入攻克名单后接回）。</summary>
    public static readonly (string Title, string StreamId)[] Channels =
    {
        ("内蒙古卫视", "3621481"), ("内蒙古蒙古语卫视", "2315"), ("新闻综合", "2316"),
        ("经济生活", "2317"), ("少儿频道", "2318"), ("文体娱乐", "2319"),
        ("农牧频道", "2320"), ("内蒙古蒙古语文化频道", "2321"), ("呼和浩特", "2331"),
    };

    public static string PlayUrl(string streamId, string ticket)
        => $"https://livestream-bt.nmtv.cn/nmtv/{streamId}general.m3u8?{ticket}";

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (title, sid) in Channels)
        {
            list.Add(new Channel
            {
                Name = title,
                Category = title.Contains("呼和浩特")
                    ? ChannelCategory.City : ChannelCategory.Provincial,
                SourceId = SourceId,
                StreamUrl = ApiBase, // 播放端经取流器调 API→XXTEA 解密→取 streamUrl
                Referer = Referer,
                QualityTiers = new List<string> { "auto" },
                Verdict = "可直接接（2026-11-07 实测：XXTEA 解密通，清单+分片 200；需真机复测）",
                EpgKey = $"nmtv{sid}",
            });
        }
        return list;
    }
}
