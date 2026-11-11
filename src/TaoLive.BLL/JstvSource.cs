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
/// 广西网络台 gxtv（2026-11-08 电视频道口径接口恢复：POST memberApi/channel/channelList）。
/// 页面实锤：tv.gxtv.cn/static/web/js/channel/channeliveList.js 调用
/// POST https://api2019.gxtv.cn/memberApi/channel/channelList（pageNo=1&pageSize=1000&liveMethod=0，
/// Referer https://tv.gxtv.cn/），rows[].name / state / showChannel / encodeM3u8。
/// 实测口径：encodeM3u8 为在用直播清单（liangtv.cn CDN，明文 HLS，无需签名/防盗链）；
/// decodeM3u8 旧域 mobilelive.gxtv.cn 已 404 停流，不接。
/// 取流器播放时实时取清单按 name 精确匹配（60 秒缓存），源变更自刷新。
/// 本地 7 路（城市台）+ CETV-2/4（专项，gxtv 官方重流）；广西卫视/CETV-1 央视频已覆盖不重号。
/// 证据：调研/长尾移植/广西电视频道口径-已打通.md + 证据/gx_tv_channel_list_20261108.json。
/// 需真机复测。
/// </summary>
public class GxtvSource : IChannelSource
{
    public string SourceId => "gxtv";

    /// <summary>电视频道口径接口（POST 表单 pageNo=1&pageSize=1000&liveMethod=0）。</summary>
    public const string ChannelListApi = "https://api2019.gxtv.cn/memberApi/channel/channelList";
    public const string ChannelListForm = "pageNo=1&pageSize=1000&liveMethod=0";
    public const string Referer = "https://tv.gxtv.cn/";
    public const string UserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    /// <summary>2026-11-08 实测在流的本地频道（title 精确匹配 channelList.rows[].name）。</summary>
    public static readonly (string Name, ChannelCategory Cat)[] Channels =
    {
        ("综艺旅游频道", ChannelCategory.City),
        ("都市频道", ChannelCategory.City),
        ("影视频道", ChannelCategory.City),
        ("新闻频道", ChannelCategory.City),
        ("国际频道", ChannelCategory.City),
        ("乐思购频道", ChannelCategory.City),
        ("移动数字电视频道", ChannelCategory.City),
        ("中国教育电视台CETV-2频道", ChannelCategory.Special),
        ("中国教育电视台CETV-4频道", ChannelCategory.Special),
    };

    /// <summary>
    /// 接口清单按 name 定位 encodeM3u8：data.rows[] 逐行（括号计数切行，行内深嵌套），
    /// 同 name 多行取首个 encodeM3u8 非空的行（实测国际频道双行同 hash）。
    /// 不取 decodeM3u8（旧域 mobilelive 已 404 停流）。
    /// </summary>
    public static string? LocateStreamUrl(string json, string name)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return null;
        var rowsStart = json.IndexOf("\"rows\":[", StringComparison.Ordinal);
        if (rowsStart < 0) return null;
        var i = json.IndexOf('{', rowsStart);
        while (i >= 0)
        {
            var end = JsonBlockEnd(json, i);
            if (end < 0) break;
            var row = json.Substring(i, end - i + 1);
            if (row.Contains($"\"name\":\"{name}\""))
            {
                var u = System.Text.RegularExpressions.Regex.Match(
                    row, "\"encodeM3u8\":\"(https?://[^\"]+)\"");
                if (u.Success && u.Groups[1].Value != "")
                    return u.Groups[1].Value.Replace("\\/", "/");
            }
            i = json.IndexOf('{', end + 1);
        }
        return null;
    }

    /// <summary>忽略字符串内花括号与转义的配对花括号定位（无 JSON 依赖的轻量解析）。</summary>
    private static int JsonBlockEnd(string s, int open)
    {
        var depth = 0; var inStr = false;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (c == '\\') i++;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (name, cat) in Channels)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = cat,
                SourceId = SourceId,
                StreamUrl = ChannelListApi, // 播放端经取流器取清单按 name 匹配 encodeM3u8
                Referer = Referer,
                UserAgent = UserAgent,
                QualityTiers = new List<string> { "auto" },
                Verdict = "可直接接（2026-11-08 电视频道口径实测：清单直吐明文 HLS，分片可解码；需真机复测）",
                EpgKey = $"gx{name}",
            });
        }
        return list;
    }
}
