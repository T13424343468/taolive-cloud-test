using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 江苏荔枝网 jstv（2026-11-07 摸底第二轮：明文 HLS 非 DRM，沙箱全链路实测通，4 路 200+ 分片）。
/// 取流域 https://lived.jstv.com/{chan}pro.m3u8?txSecret=..&txTime=..（腾讯云防盗链）：
/// - key = "7cSrbhisZe2tF3N3ZGPN" 经 x() 函数变换：slice(6,-3) 后 atob；
/// - txSecret = md5(key' + 频道名 + hex(now+180s))，txTime = hex(now+180s)。
/// 证据：调研/省市源证据/（js_jswspro.* 等），需真机复测。
/// </summary>
public class JstvSource : IChannelSource
{
    public string SourceId => "jstv";

    // 江苏卫视已由央视频 63 路覆盖（c_jsws），此处只接本地特色路，不重复编号。
    public static readonly (string Chan, string Name)[] Channels =
    {
        ("jscs", "江苏城市"), ("jsxw", "江苏公共新闻"), ("jszy", "江苏综艺"),
    };

    /// <summary>x() 变换后的实际密钥：originKey.slice(6,-3) 再 Base64 解码（8 字节，不足补齐）。</summary>
    public static string DecodedKey()
    {
        var sliced = OriginKey[6..^3];
        var padded = sliced.PadRight(sliced.Length + (4 - sliced.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    public const string OriginKey = "7cSrbhisZe2tF3N3ZGPN";

    /// <summary>腾讯云防盗链签名：md5(key' + chan + txTime)，txTime=hex(now+180s)。</summary>
    public static (string TxSecret, string TxTime) Sign(string decodedKey, string chan, long epoch)
    {
        var txTime = epoch.ToString("x8");
        using var md5 = System.Security.Cryptography.MD5.Create();
        var raw = $"{decodedKey}{chan}{txTime}";
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return (Convert.ToHexString(h).ToLowerInvariant(), txTime);
    }

    public static string PlayUrl(string chan, long epoch)
    {
        var (secret, txTime) = Sign(DecodedKey(), chan, epoch);
        return $"https://lived.jstv.com/{chan}pro.m3u8?txSecret={secret}&txTime={txTime}";
    }

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (chan, name) in Channels)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = ChannelCategory.Provincial,
                SourceId = SourceId,
                StreamUrl = PlayUrl(chan, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Referer = "https://live.jstv.com/",
                QualityTiers = new List<string> { "auto" },
                Verdict = "可直接接（2026-11-07 实测：明文 HLS，4 路 200+ 分片；需真机复测）",
                EpgKey = $"js_{chan}",
            });
        }
        return list;
    }
}

/// <summary>
/// 西安网络台 xatv（2026-11-07 摸底第二轮：明文 HLS，仅需 Referer，5 路清单+分片全 200）。
/// stream.xiancity.cn/live/{1,2,4,5,7}/index.m3u8；
/// 页面映射：直播间→7、都市→2、戏剧影视→4、丝路→5；xatv4 文化影视流 404 不接（页面映射的 4 = 戏剧影视可用，文化影视流 id 另测 404）。
/// 证据：调研/省市源证据/（xa_*），需真机复测。
/// </summary>
public class XatvSource : IChannelSource
{
    public string SourceId => "xatv";

    public static readonly (int StreamId, string Name)[] Channels =
    {
        (7, "西安直播间"), (2, "西安都市频道"), (4, "西安戏剧影视"), (5, "西安丝路频道"),
    };

    public static string PlayUrl(int streamId) => $"https://stream.xiancity.cn/live/{streamId}/index.m3u8";
    public const string Referer = "https://v.xiancity.cn/";

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (sid, name) in Channels)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = ChannelCategory.City,
                SourceId = SourceId,
                StreamUrl = PlayUrl(sid),
                Referer = Referer,
                QualityTiers = new List<string> { "auto" },
                Verdict = "可直接接（2026-11-07 实测：明文 HLS，分片全 200；需真机复测）",
                EpgKey = $"xa{sid}",
            });
        }
        return list;
    }
}

/// <summary>
/// 广西网络台 gxtv（2026-11-07 摸底第二轮：api2019.gxtv.cn 公开接口直吐 videoLiveUrl，105 条多为活动直播）。
/// 电视频道同族接口口径待真机确认，先接接口通路段。
/// 证据：调研/省市源证据/（gx_*），需真机复测。
/// </summary>
public class GxtvSource : IChannelSource
{
    public string SourceId => "gxtv";

    public static string ApiUrl(string apiId) => $"https://api2019.gxtv.cn/{apiId}";
    public const string Referer = "https://www.gxtv.cn/";

    public List<Channel> FetchChannels()
    {
        // 电视频道同族接口口径待真机确认：先挂取票端点，播放端取 videoLiveUrl 后直连。
        return new List<Channel>
        {
            new()
            {
                // 广西卫视已由央视频覆盖（c_gxws），此路为网络台补充源，归城市台组。
                Name = "广西网络台", Category = ChannelCategory.City, SourceId = SourceId,
                StreamUrl = ApiUrl("api/open/broadcast/live"),
                Referer = Referer, QualityTiers = new List<string> { "auto" },
                Verdict = "需适配（公开接口直吐 videoLiveUrl；电视频道口径待真机确认）",
                EpgKey = "gx_main",
            },
        };
    }
}
