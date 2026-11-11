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

    /// <summary>
    /// 已确认的 10 路频道（2026-11-08「包头」接回：根因为整表序列化字段顺序 data.streamUrl
    /// 位于行内 title 之前，修复见 StreamResolver.ResolveNmtvAsync 行级匹配；
    /// 证据 调研/长尾移植/证据/nmtv_row_order_20261108.txt）。
    /// </summary>
    public static readonly (string Title, string StreamId)[] Channels =
    {
        ("内蒙古卫视", "3621481"), ("内蒙古蒙古语卫视", "2315"), ("新闻综合", "2316"),
        ("经济生活", "2317"), ("少儿频道", "2318"), ("文体娱乐", "2319"),
        ("农牧频道", "2320"), ("内蒙古蒙古语文化频道", "2321"), ("呼和浩特", "2331"),
        ("包头", "2358"),
    };

    public static string PlayUrl(string streamId, string ticket)
        => $"https://livestream-bt.nmtv.cn/nmtv/{streamId}general.m3u8?{ticket}";

    private const string M3u8Pattern = "https?://[^\"\\\\\\s]+\\.m3u8[^\"\\\\\\s]*";

    /// <summary>
    /// 整表内按 title 精确匹配本行的 streamUrl（2026-11-08 修复「包头」取不到流的根因：
    /// 每行 JSON 键序为字母序，行内顺序 = {"aiGene"…data.streamUrl…streamUrls[]…title…}，
    /// 流位于台名之前；旧「title 向右找首个 m3u8」把每台串到下一台、表尾行必失败）。
    /// 主口径：台名之前最近的 m3u8，且两者之间不得跨越下一行起始标记（{"aiGene"）——
    /// 跨了说明该 m3u8 属上一行，本行名前无流。
    /// 兜底1：静态表 title→streamId，定位 "indexId":&lt;sid&gt; 后取其后首个 m3u8（同行内 streamUrl 在其后）；
    /// 兜底2：旧口径 title 向右首个 m3u8（防未来字段序整体反转）。
    /// </summary>
    public static string? LocateStreamUrl(string json, string title)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(title)) return null;
        var token = $"\"title\":\"{title}\"";
        var rx = new System.Text.RegularExpressions.Regex(M3u8Pattern, System.Text.RegularExpressions.RegexOptions.Compiled);
        var t = json.IndexOf(token, StringComparison.Ordinal);
        if (t >= 0)
        {
            var ms = rx.Matches(json);
            for (var i = ms.Count - 1; i >= 0; i--)
            {
                if (ms[i].Index >= t) continue;
                var between = json.Substring(ms[i].Index, t - ms[i].Index);
                if (!between.Contains("{\"aiGene\""))
                    return ms[i].Value.Replace("\\/", "/");
                break; // 最近 m3u8 已跨行界 → 本行名前无流，走兜底
            }
        }
        // 兜底1：静态表 indexId 定位（streamUrl 在同 data 对象内 indexId 之后）
        var sid = Channels.FirstOrDefault(c => c.Title == title).StreamId;
        if (!string.IsNullOrEmpty(sid))
        {
            var idx = json.IndexOf($"\"indexId\":{sid}", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var m = rx.Match(json[idx..]);
                if (m.Success) return m.Value.Replace("\\/", "/");
            }
        }
        // 兜底2：旧口径
        if (t >= 0)
        {
            var m = rx.Match(json[t..]);
            if (m.Success) return m.Value.Replace("\\/", "/");
        }
        return null;
    }

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (title, sid) in Channels)
        {
            list.Add(new Channel
            {
                Name = title,
                Category = title.Contains("呼和浩特") || title.Contains("包头")
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
