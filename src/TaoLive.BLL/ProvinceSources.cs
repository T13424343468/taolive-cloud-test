using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 浙江新蓝网 cztv（2026-11-07 摸底实测：直接接）。
/// 频道表 p.cztv.com/api/paas/channel/tv（station_code 起 101）；
/// 多档流 zlive-das.cztv.com/zapp/live/tv/channel/playInfo?channelId=..&platform=WEB；
/// CDN 鉴权 auth_key=ts-rand-0-md5(path-ts-rand-0-key)，key 按域名写死（zwebl01~07=CHWr9VybUeBZE1VB，
/// zhfivel01~06=9T08yiAoqM4eeCwV），清单+分片实测 200。
/// </summary>
public class CztvSource : IChannelSource
{
    public string SourceId => "cztv";

    public static readonly (string Code, string Name)[] Stations =
    {
        ("101", "浙江卫视"), ("102", "钱江都市"), ("103", "经济生活"), ("104", "教科影视"),
    };

    public static readonly Dictionary<string, string> CdnKeys = new()
    {
        ["zwebl01.cztv.com"] = "CHWr9VybUeBZE1VB", ["zwebl02.cztv.com"] = "CHWr9VybUeBZE1VB",
        ["zwebl03.cztv.com"] = "CHWr9VybUeBZE1VB", ["zwebl04.cztv.com"] = "CHWr9VybUeBZE1VB",
        ["zwebl05.cztv.com"] = "CHWr9VybUeBZE1VB", ["zwebl06.cztv.com"] = "CHWr9VybUeBZE1VB",
        ["zwebl07.cztv.com"] = "CHWr9VybUeBZE1VB", ["zhfivel01.cztv.com"] = "9T08yiAoqM4eeCwV",
        ["zhfivel02.cztv.com"] = "9T08yiAoqM4eeCwV", ["zhfivel03.cztv.com"] = "9T08yiAoqM4eeCwV",
        ["zhfivel04.cztv.com"] = "9T08yiAoqM4eeCwV", ["zhfivel05.cztv.com"] = "9T08yiAoqM4eeCwV",
        ["zhfivel06.cztv.com"] = "9T08yiAoqM4eeCwV",
    };

    /// <summary>auth_key 签名（摸底实测算法：md5(path-ts-rand-0-key)）。</summary>
    public static string BuildAuthKey(string path, long ts, string rand, string key)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var raw = $"{path}-{ts}-{rand}-0-{key}";
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return $"{ts}-{rand}-0-{Convert.ToHexString(h).ToLowerInvariant()}";
    }

    public static string SignPlayUrl(string url, long ts, string rand)
    {
        var uri = new Uri(url);
        if (!CdnKeys.TryGetValue(uri.Host, out var key)) return url;
        return $"{url}?auth_key={BuildAuthKey(uri.AbsolutePath, ts, rand, key)}";
    }

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (code, name) in Stations)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = ChannelCategory.Provincial,
                SourceId = SourceId,
                StreamUrl = $"https://zlive-das.cztv.com/zapp/live/tv/channel/playInfo?channelId={code}&platform=WEB",
                Referer = "https://www.cztv.com/",
                QualityTiers = new List<string> { "720P", "1080P" },
                Verdict = "可直接接（2026-11-07 实测：auth_key 算法通，720P 清单+分片 200）",
                EpgKey = $"cztv{code}",
            });
        }
        return list;
    }
}

/// <summary>
/// 贵州贵视网 gzstv（2026-11-07 摸底实测：直接接）。
/// 取票 api.gzstv.com/v1/tv/chNN（须带 Referer www.gzstv.com）→ txSecret/txTime 腾讯云防盗链 m3u8；
/// 清单+分片实测 200；票据短效，切台/重连重取。
/// </summary>
public class GzstvSource : IChannelSource
{
    public string SourceId => "gzstv";

    public static readonly (string Id, string Name)[] Channels =
    {
        ("ch01", "贵州卫视"), ("ch02", "贵州公共"), ("ch03", "影视文艺"),
        ("ch04", "大众生活"), ("ch05", "生态乡村"), ("ch06", "科教健康"), ("ch13", "贵州移动电视"),
    };

    public static string ApiUrl(string channelId) => $"https://api.gzstv.com/v1/tv/{channelId}";
    public const string Referer = "https://www.gzstv.com/";

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (id, name) in Channels)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = ChannelCategory.Provincial,
                SourceId = SourceId,
                StreamUrl = ApiUrl(id),
                Referer = Referer,
                QualityTiers = new List<string> { "lo" },
                Verdict = "可直接接（2026-11-07 实测：API 返回 txSecret/txTime，清单+分片 200）",
                EpgKey = $"gz{id}",
            });
        }
        return list;
    }
}

/// <summary>
/// 上海看看新闻 kankan（需适配，签名链已逆向；流 JWT 绑 UA+出口 IP——取流必须与取票同 UA）。
/// 请求签名：MD5(params 排序 + secret)，secret=28c8edde3d61a0411511d3b1866f0636（参考实现调研/省市源证据/knapi2.py）。
/// </summary>
public class KankanSource : IChannelSource
{
    public string SourceId => "kankan";

    public static readonly (string Id, string Name)[] Channels =
    {
        ("1", "东方卫视"), ("2", "新闻综合"), ("5", "第一财经"), ("10", "五星体育"),
        ("4", "都市频道"), ("9", "哈哈炫动"), ("11", "魔都眼"), ("12", "新纪实"),
    };

    public const string Secret = "28c8edde3d61a0411511d3b1866f0636";

    /// <summary>kapi 请求签名（params 按键排序后 MD5(secret+...)，细节见 knapi2.py；端到端待真机同 UA 复测）。</summary>
    public static string Sign(Dictionary<string, string> pars, long ts, string nonce)
    {
        var merged = new SortedDictionary<string, string>();
        merged["timestamp"] = ts.ToString();
        merged["nonce"] = nonce;
        foreach (var kv in pars) merged[kv.Key] = kv.Value;
        var qs = string.Join("&", merged.Select(kv => $"{kv.Key}={kv.Value}"));
        using var md5 = System.Security.Cryptography.MD5.Create();
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(qs + Secret));
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>();
        foreach (var (id, name) in Channels)
        {
            list.Add(new Channel
            {
                Name = name,
                Category = ChannelCategory.Provincial,
                SourceId = SourceId,
                StreamUrl = $"https://kapi.kankanews.com/content/pc/tv/channel/detail?channel_id={id}",
                Referer = "https://live.kankanews.com/",
                QualityTiers = new List<string> { "auto" },
                Verdict = "需适配（签名链已逆向，流 JWT 绑 UA+IP，播放端须同 UA 拉流；待真机复测收口）",
                EpgKey = $"kn{id}",
            });
        }
        return list;
    }
}
