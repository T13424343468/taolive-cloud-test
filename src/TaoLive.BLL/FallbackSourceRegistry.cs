using System.Collections.Generic;
using System.Linq;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 备援源注册表（治顿批①，老板 2026-11-12 拍板落码）：
/// 把 2026-11-12 预研实测的 15 路卫视备用源 + CCTV 公共备援搬进 1.0.x 线，
/// 经 StreamResolver.UserFallback 注入既有备援管线（主源失败→备援重连→稳定后自动回主源，
/// 消费点 ResolveWithFailoverAsync 与 HotStandbyKeeper，交互时序零新增设计）。
/// 频道键按 1.0.x 频道目录对齐：央视频频道 EpgKey=c_xxx（YangshipinSource.Table.Id），
/// 同名台跨源归并走 ChannelPrecheck.NormalizeName，映射表按台名归一匹配（不依赖源序号）。
/// 活性口径：2026-11-12 16:30 沙箱实测 15/15 活（清单+分片 200），详《治顿最小集预研-备援活性与接线面.md》。
/// 免费直连，不碰加密保护红线；需 Referer 的源在本表带全，注入点原样透传。
/// </summary>
public static class FallbackSourceRegistry
{
    /// <summary>一路备援源：台名（1.0.x 目录口径）+ 直连 URL + 可选 Referer/UA。</summary>
    public record FallbackEntry(string Name, string Url, string Referer = "", string UserAgent = "");

    // ---- 15 路卫视摸排批（v4 树 BuiltinRegistry 2026-11-10 收录，11-12 复测全活） ----

    private static readonly FallbackEntry[] Satellites =
    {
        new("湖南卫视", "https://mgtv-hlsal-ldvt.aiyouchannel.com/otv/hlsal/hnws/hnws.m3u8"),
        new("黑龙江卫视", "https://idclive.hljtv.com:4430/hljws/hljws.m3u8"),
        new("内蒙古卫视", "https://play1-qk.nmtv.cn/live/nmgws/nmgws.m3u8"),
        new("新疆卫视", "http://218.84.12.186:8001/hls/1/index.m3u8"),
        new("深圳卫视", "http://gmxw.7766.org:808/45/index.m3u8"),
        new("安徽卫视", "http://gmxw.7766.org:808/40/index.m3u8"),
        new("东南卫视", "http://live.zohi.tv/zb.m3u8"),
        new("辽宁卫视", "http://61.136.172.236:9901/live/lnws.m3u8"),
        new("江西卫视", "http://112.27.5.218:9901/live/jxws.m3u8"),
        new("山西卫视", "http://119.39.9.8:9901/live/shanxiws.m3u8"),
        new("西藏卫视", "http://112.27.5.218:9901/live/xzws.m3u8"),
        new("宁夏卫视", "http://120.76.248.139/live/nxws.m3u8"),
        new("CGTN西班牙语", "https://espanol-liveali.cgtn.com/live/espanol.m3u8"),
        new("钱江都市", "https://ali-m-l.cztv.com/lantian/002/index.m3u8"),
        new("教科影视", "https://ali-m-l.cztv.com/lantian/004/index.m3u8"),
    };

    // ---- CCTV 侧公共备援（v4 已编入的聚合/单源，老板下午卡顿恰在 CCTV1/2/6） ----

    private static readonly FallbackEntry[] Cctv =
    {
        new("CCTV1综合", "http://74.91.26.218:82/cctv1/index.m3u8"),
        // 17:18 支援摸排收录（老板 17:30 令）：全链实测 清单200→首分片206 实数据；与 CCTV1 同源族单点，
        // 评分侧按单点降权；纯 IP 字面量不走系统解析，对解析僵死场景天然免疫（与⑤网络自愈互为逃生线）
        new("CCTV2财经", "http://74.91.26.218:82/live/cctv2hd.m3u8"),
        new("CCTV6电影", "http://74.91.26.218:82/live/cctv6hd.m3u8"),
        // 19:2x 支援摸排收录（老板 19:28 令「全部干完」）：CCTV3/5/8 同 74.91 家族全链实测活
        // （清单→相对分片按重定向后最终基址 5.5MB 实 TS）；cctv5plushd 已死（「没有配置任何服务器」）不收。
        // 同源族单点特性不变，评分侧按单点降权；央视备援 6→9 路
        new("CCTV3综艺", "http://74.91.26.218:82/live/cctv3hd.m3u8"),
        new("CCTV5体育", "http://74.91.26.218:82/live/cctv5hd.m3u8"),
        new("CCTV8电视剧", "http://74.91.26.218:82/live/cctv8hd.m3u8"),
        new("CCTV4中文国际", "https://dash2.antik.sk/cctv4/index.m3u8"),
        new("CCTV12社会与法", "http://124.165.251.82:85/cctv12/index.m3u8"),
        new("CCTV13新闻", "https://ali-m-l.cztv.com/lantian/channel21/index.m3u8"),
    };

    // ---- 卫视大盘摸排第二批（老板 2026-11-12 23:0x 令「新增的十个源一并收录」）----
    // 摸排详《备援第二家族与jstv换源摸排.md》§七：全部全链 206 实 TS（清单→分片按清单目录拼），
    // 无票据无 302，域名或国内 IP 免解析。新增独立覆盖 5 台（浙江/东方/广东/北京/湖北）；
    // 深圳/黑龙江/东南三台与既有登记同名——按 Index 首条生效，既有稳定档优先，
    // 新路留表作同族备线（②选源智能化落码时按家族/token 评分）。
    // bfgd 六路共享同一入口与 token 串：家族内共 token，token 失效=同族齐挂，评分侧须共 token 降权；
    // bfgd 慢端特性：并发探测分片偶发 seg-fail，单发全过——探活须单发、超时≥12s（防并发限速误判）。

    private static readonly FallbackEntry[] SatellitesBatch2 =
    {
        new("浙江卫视", "http://ali-m-l.cztv.com/channels/lantian/channel001/1080p.m3u8"), // cztv-ali 浙江官方蓝天频道，同域双活
        new("东方卫视", "http://bp-resource-dfl.bestv.cn/148/3/video.m3u8"),               // bestv 百视通
        new("黑龙江卫视", "http://120.76.248.139/live/bfgd/4200000095.m3u8"),              // bfgd（观察档，共 token）
        new("深圳卫视", "http://120.76.248.139/live/bfgd/4200000100.m3u8"),                // bfgd（观察档，共 token）
        new("东南卫视", "http://120.76.248.139/live/bfgd/4200000483.m3u8"),                // bfgd（观察档，共 token）
        new("广东卫视", "http://120.76.248.139/live/bfgd/4200000092.m3u8"),                // bfgd（单发重测过）
        new("北京卫视", "http://120.76.248.139/live/bfgd/4200000083.m3u8"),                // bfgd（单发重测过）
        new("湖北卫视", "http://120.76.248.139/live/bfgd/4200000102.m3u8"),                // bfgd（单发重测过）
        new("黑龙江卫视", "https://idclive.hljtv.com:4430/live/hljws_own.m3u8"),           // hljtv 黑龙江官方 own 流
        new("深圳卫视", "http://gmxw.7766.org:808/hls/45/index.m3u8"),                     // gmxw id45（清单标注口径，分片按目录拼）
    };

    /// <summary>全量备援表（24 路既有 + 10 路卫视摸排第二批，共 34 路）。</summary>
    public static readonly FallbackEntry[] Table =
        Satellites.Concat(Cctv).Concat(SatellitesBatch2).ToArray();

    /// <summary>台名归一（与 ChannelPrecheck.NormalizeName 同口径）：去空格后精确匹配。</summary>
    private static string Normalize(string name) =>
        (name ?? "").Replace(" ", "").Replace("　", "");

    private static readonly Dictionary<string, FallbackEntry> Index =
        Table.GroupBy(e => Normalize(e.Name))
             .ToDictionary(g => g.Key, g => g.First());

    /// <summary>该频道是否有登记的备援源。</summary>
    public static bool Has(string channelName) => Index.ContainsKey(Normalize(channelName));

    /// <summary>取备援 URL（无登记返回 null）。Referer/UA 逐条返回给注入点透传。</summary>
    public static FallbackEntry? Get(string channelName) =>
        Index.TryGetValue(Normalize(channelName), out var e) ? e : null;

    /// <summary>UserFallback 注入适配：备援 URL 恒定（非票据型），按 N5 口径不做换票判定。</summary>
    public static (string? Url, string Referer, string UserAgent) Resolve(Channel ch)
    {
        var e = Get(ch.Name);
        return e is null ? (null, "", "") : (e.Url, e.Referer, e.UserAgent);
    }
}
