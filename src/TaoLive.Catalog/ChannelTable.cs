using TaoLive.Model;

namespace TaoLive.Catalog;

/// <summary>
/// 内置台单（2.0 W4）：一期摸底成果逐字移植——各源实现类的表数据原样搬运，
/// 分类口径与编号顺序（央视→CGTN→专项→省台→城市台）跟一期 ChannelCatalog.Rebuild 一致。
/// 每台一路源端点：API 取票口（Kind=Web，播前经 TicketResolver 换真实流）或直连 HLS。
/// 编号即列表顺序（电视端列表序号从 1 起），随台单版本固化，用户记住编号永久直达。
/// </summary>
public static class ChannelTable
{
    /// <summary>分类展示顺序（编号分配顺序，与一期目录一致）。</summary>
    public static readonly string[] CategoryOrder = { "央视", "CGTN", "专项", "省台", "城市台" };

    public sealed record YsChannel(string Id, string Name, string Cnlid, string Livepid, string Defn, string Cat);

    /// <summary>央视频官方取票源 63 路全表（摸底实测 2026-11-07：API+清单+分片，fhd=1080p）。</summary>
    public static readonly YsChannel[] Yangshipin =
    {
        new("c_cctv1", "CCTV1综合", "2024078201", "600001859", "fhd", "央视"),
        new("c_cctv2", "CCTV2财经", "2024075401", "600001800", "fhd", "央视"),
        new("c_cctv3", "CCTV3综艺", "2024068501", "600001801", "fhd", "央视"),
        new("c_cctv4", "CCTV4中文国际", "2029797101", "600001814", "fhd", "央视"),
        new("c_cctv5", "CCTV5体育", "2024078401", "600001818", "fhd", "央视"),
        new("c_cctv5p", "CCTV5+体育赛事", "2024078001", "600001817", "fhd", "央视"),
        new("c_cctv6", "CCTV6电影", "2013693901", "600108442", "fhd", "央视"),
        new("c_cctv7", "CCTV7国防军事", "2024072001", "600004092", "fhd", "央视"),
        new("c_cctv8", "CCTV8电视剧", "2029793001", "600001803", "fhd", "央视"),
        new("c_cctv9", "CCTV9纪录", "2024078601", "600004078", "fhd", "央视"),
        new("c_cctv10", "CCTV10科教", "2024078701", "600001805", "fhd", "央视"),
        new("c_cctv11", "CCTV11戏曲", "2027248701", "600001806", "fhd", "央视"),
        new("c_cctv12", "CCTV12社会与法", "2027248801", "600001807", "fhd", "央视"),
        new("c_cctv13", "CCTV13新闻", "2029797201", "600001811", "fhd", "央视"),
        new("c_cctv14", "CCTV14少儿", "2027248901", "600001809", "fhd", "央视"),
        new("c_cctv15", "CCTV15音乐", "2027249001", "600001815", "fhd", "央视"),
        new("c_cctv16", "CCTV16奥林匹克", "2027249101", "600098637", "fhd", "央视"),
        new("c_cctv164k", "CCTV16 4K", "2027249301", "600099502", "fhd", "央视"),
        new("c_cctv17", "CCTV17农业农村", "2027249401", "600001810", "fhd", "央视"),
        new("c_cctv4k", "CCTV4K超高清", "2029810301", "600002264", "fhd", "央视"),
        new("c_cctv8k", "CCTV8K超高清", "2026774101", "600156816", "fhd", "央视"),
        new("c_cgtn", "CGTN", "2024181701", "600014550", "fhd", "CGTN"),
        new("c_cgtnfy", "CGTN法语", "2024181801", "600084704", "fhd", "CGTN"),
        new("c_cgtney", "CGTN俄语", "2024181901", "600084758", "fhd", "CGTN"),
        new("c_cgtnalby", "CGTN阿拉伯语", "2024182001", "600084782", "fhd", "CGTN"),
        new("c_cgtnxby", "CGTN西班牙语", "2024182101", "600084744", "fhd", "CGTN"),
        new("c_cgtnwyjl", "CGTN纪录", "2024182301", "600084781", "fhd", "CGTN"),
        new("c_cctvfyjc", "CCTV风云剧场", "2025637103", "600099658", "shd", "专项"),
        new("c_cctvdyjc", "CCTV第一剧场", "2026874203", "600099655", "shd", "专项"),
        new("c_cctvhjjc", "CCTV怀旧剧场", "2026874303", "600099620", "shd", "专项"),
        new("c_bjws", "北京卫视", "2024052703", "600002309", "fhd", "省台"),
        new("c_jsws", "江苏卫视", "2024171103", "600002521", "fhd", "省台"),
        new("c_dfws", "东方卫视", "2024054503", "600002483", "fhd", "省台"),
        new("c_zjws", "浙江卫视", "2024054703", "600002520", "fhd", "省台"),
        new("c_hnws", "湖南卫视", "2024054803", "600002475", "fhd", "省台"),
        new("c_hbws", "湖北卫视", "2024171203", "600002508", "fhd", "省台"),
        new("c_gdws", "广东卫视", "2024060903", "600002485", "fhd", "省台"),
        new("c_gxws", "广西卫视", "2024060703", "600002509", "fhd", "省台"),
        new("c_hljws", "黑龙江卫视", "2029797003", "600002498", "fhd", "省台"),
        new("c_hnws2", "海南卫视", "2024055603", "600002506", "fhd", "省台"),
        new("c_cqws", "重庆卫视", "2024061103", "600002531", "fhd", "省台"),
        new("c_szws", "深圳卫视", "2024061303", "600002481", "fhd", "省台"),
        new("c_scws", "四川卫视", "2024061403", "600002516", "fhd", "省台"),
        new("c_henanws", "河南卫视", "2029797303", "600002525", "fhd", "省台"),
        new("c_fjdnhz", "东南卫视", "2024061503", "600002484", "fhd", "省台"),
        new("c_gzhws", "贵州卫视", "2024061603", "600002490", "fhd", "省台"),
        new("c_jxws", "江西卫视", "2024061703", "600002503", "fhd", "省台"),
        new("c_lnws", "辽宁卫视", "2024171303", "600002505", "fhd", "省台"),
        new("c_ahws", "安徽卫视", "2024171403", "600002532", "fhd", "省台"),
        new("c_hbws2", "河北卫视", "2024171503", "600002493", "fhd", "省台"),
        new("c_sdws", "山东卫视", "2029787903", "600002513", "fhd", "省台"),
        new("c_tjws", "天津卫视", "2019927003", "600152137", "fhd", "省台"),
        new("c_jlws", "吉林卫视", "2025561503", "600190405", "fhd", "省台"),
        new("c_shanxiws", "陕西卫视", "2029795103", "600190400", "fhd", "省台"),
        new("c_nxws", "宁夏卫视", "2025608503", "600190737", "fhd", "省台"),
        new("c_nmgws", "内蒙古卫视", "2025561203", "600190401", "fhd", "省台"),
        new("c_ynws", "云南卫视", "2025561303", "600190402", "fhd", "省台"),
        new("c_shanxiws2", "山西卫视", "2025560803", "600190407", "fhd", "省台"),
        new("c_qhws", "青海卫视", "2025559103", "600190406", "fhd", "省台"),
        new("c_xzws", "西藏卫视", "2025558003", "600190403", "fhd", "省台"),
        new("c_cetv1", "CETV1", "2022823801", "600171827", "fhd", "专项"),
        new("c_gxpd", "国学频道", "2029360403", "600213139", "fhd", "专项"),
        new("c_xjws", "新疆卫视", "2019927403", "600152138", "fhd", "省台"),
    };

    /// <summary>浙江新蓝网 cztv（2026-11-07 实测：playInfo→auth_key 签名，720P/1080P）。</summary>
    public static readonly (string Code, string Name)[] Cztv =
    {
        ("101", "浙江卫视"), ("102", "钱江都市"), ("103", "经济生活"), ("104", "教科影视"),
    };

    /// <summary>贵州贵视网 gzstv（2026-11-07 实测：API 取票→txSecret/txTime m3u8）。</summary>
    public static readonly (string Id, string Name)[] Gzstv =
    {
        ("ch01", "贵州卫视"), ("ch02", "贵州公共"), ("ch03", "影视文艺"),
        ("ch04", "大众生活"), ("ch05", "生态乡村"), ("ch06", "科教健康"), ("ch13", "贵州移动电视"),
    };

    /// <summary>上海看看新闻 kankan（签名链已逆向；取流须与取票同 UA）。</summary>
    public static readonly (string Id, string Name)[] Kankan =
    {
        ("1", "东方卫视"), ("2", "新闻综合"), ("5", "第一财经"), ("10", "五星体育"),
        ("4", "都市频道"), ("9", "哈哈炫动"), ("11", "魔都眼"), ("12", "新纪实"),
    };

    /// <summary>内蒙古腾格里网 nmtv 10 路（2026-11-07 实测：整表 XXTEA 解密；含城市台 2 路）。</summary>
    public static readonly (string Title, string StreamId)[] Nmtv =
    {
        ("内蒙古卫视", "3621481"), ("内蒙古蒙古语卫视", "2315"), ("新闻综合", "2316"),
        ("经济生活", "2317"), ("少儿频道", "2318"), ("文体娱乐", "2319"),
        ("农牧频道", "2320"), ("内蒙古蒙古语文化频道", "2321"), ("呼和浩特", "2331"),
        ("包头", "2358"),
    };

    /// <summary>江苏荔枝网 jstv 本地特色 3 路（江苏卫视由央视频覆盖不重号；txSecret 短效 180s 播时重签）。</summary>
    public static readonly (string Chan, string Name)[] Jstv =
    {
        ("jscs", "江苏城市"), ("jsxw", "江苏公共新闻"), ("jszy", "江苏综艺"),
    };

    /// <summary>西安网络台 xatv 4 路（2026-11-07 实测：明文 HLS，仅需 Referer；xatv4 文化影视流 404 不接）。</summary>
    public static readonly (int StreamId, string Name)[] Xatv =
    {
        (7, "西安直播间"), (2, "西安都市频道"), (4, "西安戏剧影视"), (5, "西安丝路频道"),
    };

    /// <summary>广西网络台 gxtv 9 路（2026-11-08 实测：电视频道口径 channelList；城市台 7 路+CETV 专项 2 路）。</summary>
    public static readonly (string Name, string Cat)[] Gxtv =
    {
        ("综艺旅游频道", "城市台"), ("都市频道", "城市台"), ("影视频道", "城市台"),
        ("新闻频道", "城市台"), ("国际频道", "城市台"), ("乐思购频道", "城市台"),
        ("移动数字电视频道", "城市台"), ("中国教育电视台CETV-2频道", "专项"),
        ("中国教育电视台CETV-4频道", "专项"),
    };

    /// <summary>地方台候选直连源 15 路（2026-11-10 复验 18/18 全活；13 城市台+2 省属经济频道）。</summary>
    public static readonly (string Name, string Url, string Cat)[] CityDirect =
    {
        ("七台河新闻综合", "http://live.qthnews.org.cn:1935/live/live1/800k/tzwj_video.m3u8", "城市台"),
        ("万荣综合", "http://60.222.246.220:19433/hls1.m3u8", "城市台"),
        ("上虞新闻综合", "http://l.cztvcloud.com/channels/lantian/SXshangyu1/720p.m3u8?zzhed", "城市台"),
        ("东丰综合", "http://stream3.jlntv.cn:80/aac_dfgb/playlist.m3u8", "城市台"),
        ("东莞新闻综合", "https://stream.sun0769.com/dgrtv1/mp4tv1_800/index.m3u8", "城市台"),
        ("乐至综合", "http://175.155.106.72:89/live1/live1.m3u8?zsicd", "城市台"),
        ("伊犁经济法制", "http://110.153.180.106:55555/out_4/index.m3u8", "城市台"),
        ("清河经济综艺", "https://jwcdnqx.hebyun.com.cn/live/qinghe1/1500k/tzwj_video.m3u8", "城市台"),
        ("烟台经济科技", "http://live.yantaitv.cn/live/27f84144e95a4652ae9e5c211b2a6b55/405f3e33ba384aa0a16014d0becd1261-1.m3u8", "城市台"),
        ("苏州社会经济", "https://live-auth.51kandianshi.com/szgd/csztv2.m3u8", "城市台"),
        ("东阳影视生活", "http://l.cztvcloud.com/channels/lantian/SXdongyang1/720p.m3u8", "城市台"),
        ("南宁影视娱乐", "http://hls.nntv.cn/nnlive/YSYL_244.m3u8", "城市台"),
        ("哈尔滨影视", "http://stream.hrbtv.net/yspd/sd/live.m3u8?zheild", "城市台"),
        ("甘肃经济", "https://satellitepull.cnr.cn/live/wxgshhzs/playlist.m3u8", "省台"),
        ("福建经济", "http://satellitepull.cnr.cn/live/wx32fjdnjjgb/playlist.m3u8", "省台"),
    };

    /// <summary>
    /// 建台单：分类顺序即编号顺序（一期口径）。每台一路源端点，取票口 Kind=Web、直连 Kind=Cdn/PureIp。
    /// </summary>
    public static List<Channel> Build()
    {
        var list = new List<Channel>(128);
        // 央视频取票口（cnlid+livepid 原样进查询串，播前经 TicketResolver 取真实流）
        foreach (var t in Yangshipin)
        {
            list.Add(new Channel(t.Id, t.Name, t.Cat, new[]
            {
                new SourceEndpoint($"https://bkliveinfo.ysp.cctv.cn/?cnlid={t.Cnlid}&livepid={t.Livepid}&defn={t.Defn}",
                    SourceKind.Web, 1),
            }));
        }
        // 浙江新蓝网：playInfo JSON 取流（auth_key 签名在取票层）
        foreach (var (code, name) in Cztv)
        {
            list.Add(new Channel($"cztv{code}", name, "省台", new[]
            {
                new SourceEndpoint($"https://zlive-das.cztv.com/zapp/live/tv/channel/playInfo?channelId={code}&platform=WEB",
                    SourceKind.Web, 1),
            }));
        }
        // 贵州贵视网：API 取票
        foreach (var (id, name) in Gzstv)
        {
            list.Add(new Channel($"gz{id}", name, "省台", new[]
            {
                new SourceEndpoint($"https://api.gzstv.com/v1/tv/{id}", SourceKind.Web, 1),
            }));
        }
        // 上海看看新闻：签名 API 取票（取流同 UA 在取票层）
        foreach (var (id, name) in Kankan)
        {
            list.Add(new Channel($"kn{id}", name, "省台", new[]
            {
                new SourceEndpoint($"https://kapi.kankanews.com/content/pc/tv/channel/detail?channel_id={id}",
                    SourceKind.Web, 1),
            }));
        }
        // 内蒙古腾格里网：整表解密取流
        foreach (var (title, sid) in Nmtv)
        {
            var cat = title.Contains("呼和浩特") || title.Contains("包头") ? "城市台" : "省台";
            list.Add(new Channel($"nmtv{sid}", title, cat, new[]
            {
                new SourceEndpoint(NmtvApi, SourceKind.Web, 1),
            }));
        }
        // 江苏荔枝网：180s 短效签名，基址登记、播时重签
        foreach (var (chan, name) in Jstv)
        {
            list.Add(new Channel($"js_{chan}", name, "省台", new[]
            {
                new SourceEndpoint($"https://lived.jstv.com/{chan}pro.m3u8", SourceKind.Cdn, 1),
            }));
        }
        // 西安网络台：直连+Referer（Referer 在播放层统一注入）
        foreach (var (sid, name) in Xatv)
        {
            list.Add(new Channel($"xa{sid}", name, "城市台", new[]
            {
                new SourceEndpoint($"https://stream.xiancity.cn/live/{sid}/index.m3u8", SourceKind.Cdn, 1),
            }));
        }
        // 广西网络台：整表匹配取流
        foreach (var (name, cat) in Gxtv)
        {
            list.Add(new Channel($"gx_{name}", name, cat, new[]
            {
                new SourceEndpoint(GxtvApi, SourceKind.Web, 1),
            }));
        }
        // 地方台候选直连（地址与台账逐字一致，不自作聪明删改）
        foreach (var (name, url, cat) in CityDirect)
        {
            var kind = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.IPv4
                ? SourceKind.PureIp : SourceKind.Cdn;
            list.Add(new Channel($"cd_{name}", name, cat, new[] { new SourceEndpoint(url, kind, 1) }));
        }
        // 分类归组重排（一期 ChannelCatalog.Rebuild 口径）：编号按分类依序分配，同分类内保持登记序
        // 引擎搬回适配：Channel 换 vc53 原型后经 CategoryName 兼容面过滤
        var grouped = ChannelTable.CategoryOrder
            .Select(cat => list.Where(c => c.CategoryName == cat).ToList())
            .Where(g => g.Count > 0)
            .SelectMany(g => g);
        // 节目单键装配（真机反馈批#7 实锤 2026-11-15）：全表此前 EpgKey 恒空——CntvEpgClient.Fetch("")
        // 查映射必败，底部节目单永久无数据（vc53 有节目单而 2.0 整个没有的根因）。
        // 键=频道 Id（c_cctv1/c_jsws 形态），恰是 CntvIds 映射表键；映射外台（nmtv/gxtv/直连）官方无数据不硬造。
        foreach (var c in grouped) c.EpgKey = c.Id;
        return grouped.ToList();
    }

    public const string NmtvApi = "https://api-bt.nmtv.cn/broadcast/list?type=1";
    public const string GxtvApi = "https://api2019.gxtv.cn/memberApi/channel/channelList";
}
