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
        // B 组修正（老板 2026-11-13 14:07 令「全收」）：hljws/hljws.m3u8 为 404 式样错，改官方 own 流
        // （与 Batch2 重复项合并回此处置顶；探活 11-13 午 LIVE）
        new("黑龙江卫视", "https://idclive.hljtv.com:4430/live/hljws_own.m3u8"),
        new("内蒙古卫视", "https://play1-qk.nmtv.cn/live/nmgws/nmgws.m3u8"),
        new("新疆卫视", "http://218.84.12.186:8001/hls/1/index.m3u8"),
        // B 组修正：gmxw 45 路缺 /hls/ 段为死路，补正为 /hls/45/（与 Batch2 重复项合并回此处置顶）
        new("深圳卫视", "http://gmxw.7766.org:808/hls/45/index.m3u8"),
        // B 组修正：gmxw 40 路缺 /hls/ 段为死路，直接替换为 183.11 纯 IP 端（域名端在 Batch4）
        new("安徽卫视", "http://183.11.239.36:808/hls/40/index.m3u8"),
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
        // B 组降序（老板 14:07 令）：CCTV1 74.91 路 404 三采样稳定死、CCTV5 74.91 路伪 200 错误文案型，
        // 两路挪到 Batch4 末位独立家族之后观察，不再顶前；CCTV12 旧路同理挪 Batch4 观察。
        // 17:18 支援摸排收录（老板 17:30 令）：全链实测 清单200→首分片206 实数据；与 CCTV1 同源族单点，
        // 评分侧按单点降权；纯 IP 字面量不走系统解析，对解析僵死场景天然免疫（与⑤网络自愈互为逃生线）
        new("CCTV2财经", "http://74.91.26.218:82/live/cctv2hd.m3u8"),
        new("CCTV6电影", "http://74.91.26.218:82/live/cctv6hd.m3u8"),
        // 19:2x 支援摸排收录（老板 19:28 令「全部干完」）：CCTV3/5/8 同 74.91 家族全链实测活
        // （清单→相对分片按重定向后最终基址 5.5MB 实 TS）；cctv5plushd 已死（「没有配置任何服务器」）不收。
        // 同源族单点特性不变，评分侧按单点降权；央视备援 6→9 路
        new("CCTV3综艺", "http://74.91.26.218:82/live/cctv3hd.m3u8"),
        new("CCTV8电视剧", "http://74.91.26.218:82/live/cctv8hd.m3u8"),
        new("CCTV4中文国际", "https://dash2.antik.sk/cctv4/index.m3u8"),
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
        // 黑龙江官方 own 流与深圳 gmxw /hls/45 已在 Satellites 修正置顶，此处重复项合并（B 组 14:07 令）
    };

    // ---- 第三批·最新全部收录（老板 2026-11-13 09:2x 令「把最新的全部收录」）----
    // 详《备援第二家族与jstv换源摸排.md》§二/§十，全部 11-13 晨 09:2x 单发探活 LIVE（probe_fallback.py 全链）：
    // ① tsfile-124 国内 IP 六台：CCTV1/2/3/5/6/8 独立兜底（静态 key 非票据轮换，纯 IP 免解析，解析僵死免疫）——
    //    央视六台原全押 74.91 零独立备路，本批解掉该单点；Index 首条生效，新六路留表作独立家族备线（②评分按家族降权）。
    // ② gmxw id93=CCTV5 独立二路（域名直出无票无 302）。
    // ③ applive 票据家族：CCTV1 入口恒定（gslb）302 取票，票随基址每次重取（N5 口径不缓存判死）。
    // ④ gmxw 同族双端标定（§十）：id46=湖北、id80=广东，双端四路全链 LIVE——首条 183.11 纯 IP 免解析，
    //    第二路 gmxw 域名同族备线（双端异构降解析单点；同族计 1 家族，②评分共族降权）；北京无映射，bfgd 观察档维持。
    // ⑤ 江苏卫视 applive B 路（jstv 换源主推，票据随基址）——江苏首个备援。

    private static readonly FallbackEntry[] Batch3 =
    {
        new("CCTV1综合", "http://124.165.251.82:85/tsfile/live/0001_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV2财经", "http://124.165.251.82:85/tsfile/live/0002_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV3综艺", "http://124.165.251.82:85/tsfile/live/0003_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV5体育", "http://124.165.251.82:85/tsfile/live/0005_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV6电影", "http://124.165.251.82:85/tsfile/live/0006_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV8电视剧", "http://124.165.251.82:85/tsfile/live/0008_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV5体育", "http://gmxw.7766.org:808/hls/93/index.m3u8"),
        new("CCTV1综合", "http://63.141.230.178:82/gslb/zbdq5.m3u8?id=cctv1hd"), // applive：入口恒定 302 取票
        new("湖北卫视", "http://183.11.239.36:808/hls/46/index.m3u8"),           // gmxw 同族 id46，纯 IP 免解析
        new("湖北卫视", "http://gmxw.7766.org:808/hls/46/index.m3u8"),           // 同族域名备线
        new("广东卫视", "http://183.11.239.36:808/hls/80/index.m3u8"),           // gmxw 同族 id80，纯 IP 免解析
        new("广东卫视", "http://gmxw.7766.org:808/hls/80/index.m3u8"),           // 同族域名备线
        new("江苏卫视", "http://107.150.60.122/live/jswshd.m3u8"),               // applive 票据家族（jstv 换源 B 主推）
    };

    // ---- 第四批·零活兜底台救活（老板 2026-11-13 14:07 令「全收」）----
    // 探活口径：projects/1377423/tools/probe_fallback.py 全链单发，11-13 午实测全 LIVE（收录清单已验）。
    // 收录原则：首条优先纯 IP 免解析；同台活源在前；183.11/gmxw.7766 双端计 1 家族（②评分共族降权）。
    // 本批后零活台 15→7。

    private static readonly FallbackEntry[] Batch4 =
    {
        // CCTV12社会与法：tsfile 顶前 + gmxw 双端 id30；旧 cctv12 直拼路（11-12 预研收录）降末位观察
        new("CCTV12社会与法", "http://124.165.251.82:85/tsfile/live/0012_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV12社会与法", "http://183.11.239.36:808/hls/30/index.m3u8"),
        new("CCTV12社会与法", "http://gmxw.7766.org:808/hls/30/index.m3u8"),
        // CCTV4/13：tsfile 独立兜底（静态 key 非票据轮换，纯 IP 免解析）
        new("CCTV4中文国际", "http://124.165.251.82:85/tsfile/live/0004_1.m3u8?key=txiptv&playlive=1&authid=0"),
        new("CCTV13新闻", "http://124.165.251.82:85/tsfile/live/0013_1.m3u8?key=txiptv&playlive=1&authid=0"),
        // 山西/内蒙古/新疆：gmxw id59/60/65 双端（183.11 纯 IP 首条 + 域名端备线）
        new("山西卫视", "http://183.11.239.36:808/hls/59/index.m3u8"),
        new("山西卫视", "http://gmxw.7766.org:808/hls/59/index.m3u8"),
        new("内蒙古卫视", "http://183.11.239.36:808/hls/60/index.m3u8"),
        new("内蒙古卫视", "http://gmxw.7766.org:808/hls/60/index.m3u8"),
        new("新疆卫视", "http://183.11.239.36:808/hls/65/index.m3u8"),
        new("新疆卫视", "http://gmxw.7766.org:808/hls/65/index.m3u8"),
        // 西藏：仅 183.11 端 id64 一条（gmxw 域名端 id64 空清单例外，无双端冗余）
        new("西藏卫视", "http://183.11.239.36:808/hls/64/index.m3u8"),
        // 安徽域名端（纯 IP 端在 Satellites 置顶）
        new("安徽卫视", "http://gmxw.7766.org:808/hls/40/index.m3u8"),
        // B 组降序观察档：74.91 CCTV1（404 三采样稳定死）/CCTV5（伪 200 错误文案型）与 CCTV12 旧直拼路
        new("CCTV1综合", "http://74.91.26.218:82/cctv1/index.m3u8"),
        new("CCTV5体育", "http://74.91.26.218:82/live/cctv5hd.m3u8"),
        new("CCTV12社会与法", "http://124.165.251.82:85/cctv12/index.m3u8"),
    };

    /// <summary>全量备援表（既有 42 路 + 第四批 16 路，共 58 路）。</summary>
    public static readonly FallbackEntry[] Table =
        Satellites.Concat(Cctv).Concat(SatellitesBatch2).Concat(Batch3).Concat(Batch4).ToArray();

    /// <summary>台名归一（与 ChannelPrecheck.NormalizeName 同口径）：去空格后精确匹配。</summary>
    private static string Normalize(string name) =>
        (name ?? "").Replace(" ", "").Replace("　", "");

    // vc43⑥ 生效性修复：Index 由「每台取首条」改「每台全量按序」——同台多家族候选（如央视六台的 74.91/tsfile/applive、
    // 湖北/广东的 bfgd/gmxw 双端）逐台按登记序保留，运行时按序取用，收录即可达（老板 11-13 令「有问题就改」）。
    private static readonly Dictionary<string, List<FallbackEntry>> Index =
        Table.GroupBy(e => Normalize(e.Name))
             .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>该频道是否有登记的备援源。</summary>
    public static bool Has(string channelName) => Index.ContainsKey(Normalize(channelName));

    /// <summary>该频道的全部备援候选（按登记序：稳定档在前，后收的独立家族/同族备线依次跟上）。</summary>
    public static IReadOnlyList<FallbackEntry> GetAll(string channelName) =>
        Index.TryGetValue(Normalize(channelName), out var list) ? list : Array.Empty<FallbackEntry>();

    /// <summary>取备援 URL（无登记返回 null）。Referer/UA 逐条返回给注入点透传。</summary>
    public static FallbackEntry? Get(string channelName)
    {
        var all = GetAll(channelName);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>UserFallback 注入适配：备援 URL 恒定（非票据型），按 N5 口径不做换票判定。</summary>
    public static (string? Url, string Referer, string UserAgent) Resolve(Channel ch)
    {
        var e = Get(ch.Name);
        return e is null ? (null, "", "") : (e.Url, e.Referer, e.UserAgent);
    }
}
