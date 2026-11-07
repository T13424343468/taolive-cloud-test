using System.Collections.Generic;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 央视频官方取票源（主干方案，2026-11-07 摸底回填）：
/// bkliveinfo.ysp.cctv.cn 免登录匿名接口，未加密 HLS（H.264 TS，无 EXT-X-KEY），
/// 境外出口全链路实测 63/63 通过（API+清单+分片，fhd=1920x1080）。
/// 工程要点（国家级源摸底.md）：
/// - cKey 为 TEA 票据（公开算法，参考实现调研/ysp/ckey.js），客户端侧计算；
/// - playurl 短效：约 5 分钟级换票 + 403 冷却 30 秒 + 不做整表扫描；
/// - 清晰度档位写死，档位报错不降级；4K/8K 在 H.264 申报下回落 1080p。
/// </summary>
public class YangshipinSource : IChannelSource
{
    public string SourceId => "yangshipin";

    public record YsChannel(string Id, string Name, string Cnlid, string Livepid, string Defn, ChannelCategory Cat);

    // 63 路全表（摸底实测，编号在 ChannelCatalog.Rebuild 时按分类顺序重新分配）
    public static readonly YsChannel[] Table =
    {
        new("c_cctv1", "CCTV1综合", "2024078201", "600001859", "fhd", ChannelCategory.Cctv),
        new("c_cctv2", "CCTV2财经", "2024075401", "600001800", "fhd", ChannelCategory.Cctv),
        new("c_cctv3", "CCTV3综艺", "2024068501", "600001801", "fhd", ChannelCategory.Cctv),
        new("c_cctv4", "CCTV4中文国际", "2029797101", "600001814", "fhd", ChannelCategory.Cctv),
        new("c_cctv5", "CCTV5体育", "2024078401", "600001818", "fhd", ChannelCategory.Cctv),
        new("c_cctv5p", "CCTV5+体育赛事", "2024078001", "600001817", "fhd", ChannelCategory.Cctv),
        new("c_cctv6", "CCTV6电影", "2013693901", "600108442", "fhd", ChannelCategory.Cctv),
        new("c_cctv7", "CCTV7国防军事", "2024072001", "600004092", "fhd", ChannelCategory.Cctv),
        new("c_cctv8", "CCTV8电视剧", "2029793001", "600001803", "fhd", ChannelCategory.Cctv),
        new("c_cctv9", "CCTV9纪录", "2024078601", "600004078", "fhd", ChannelCategory.Cctv),
        new("c_cctv10", "CCTV10科教", "2024078701", "600001805", "fhd", ChannelCategory.Cctv),
        new("c_cctv11", "CCTV11戏曲", "2027248701", "600001806", "fhd", ChannelCategory.Cctv),
        new("c_cctv12", "CCTV12社会与法", "2027248801", "600001807", "fhd", ChannelCategory.Cctv),
        new("c_cctv13", "CCTV13新闻", "2029797201", "600001811", "fhd", ChannelCategory.Cctv),
        new("c_cctv14", "CCTV14少儿", "2027248901", "600001809", "fhd", ChannelCategory.Cctv),
        new("c_cctv15", "CCTV15音乐", "2027249001", "600001815", "fhd", ChannelCategory.Cctv),
        new("c_cctv16", "CCTV16奥林匹克", "2027249101", "600098637", "fhd", ChannelCategory.Cctv),
        new("c_cctv164k", "CCTV16 4K", "2027249301", "600099502", "fhd", ChannelCategory.Cctv),
        new("c_cctv17", "CCTV17农业农村", "2027249401", "600001810", "fhd", ChannelCategory.Cctv),
        new("c_cctv4k", "CCTV4K超高清", "2029810301", "600002264", "fhd", ChannelCategory.Cctv),
        new("c_cctv8k", "CCTV8K超高清", "2026774101", "600156816", "fhd", ChannelCategory.Cctv),
        new("c_cgtn", "CGTN", "2024181701", "600014550", "fhd", ChannelCategory.Cgtn),
        new("c_cgtnfy", "CGTN法语", "2024181801", "600084704", "fhd", ChannelCategory.Cgtn),
        new("c_cgtney", "CGTN俄语", "2024181901", "600084758", "fhd", ChannelCategory.Cgtn),
        new("c_cgtnalby", "CGTN阿拉伯语", "2024182001", "600084782", "fhd", ChannelCategory.Cgtn),
        new("c_cgtnxby", "CGTN西班牙语", "2024182101", "600084744", "fhd", ChannelCategory.Cgtn),
        new("c_cgtnwyjl", "CGTN纪录", "2024182301", "600084781", "fhd", ChannelCategory.Cgtn),
        new("c_cctvfyjc", "CCTV风云剧场", "2025637103", "600099658", "shd", ChannelCategory.Special),
        new("c_cctvdyjc", "CCTV第一剧场", "2026874203", "600099655", "shd", ChannelCategory.Special),
        new("c_cctvhjjc", "CCTV怀旧剧场", "2026874303", "600099620", "shd", ChannelCategory.Special),
        new("c_bjws", "北京卫视", "2024052703", "600002309", "fhd", ChannelCategory.Provincial),
        new("c_jsws", "江苏卫视", "2024171103", "600002521", "fhd", ChannelCategory.Provincial),
        new("c_dfws", "东方卫视", "2024054503", "600002483", "fhd", ChannelCategory.Provincial),
        new("c_zjws", "浙江卫视", "2024054703", "600002520", "fhd", ChannelCategory.Provincial),
        new("c_hnws", "湖南卫视", "2024054803", "600002475", "fhd", ChannelCategory.Provincial),
        new("c_hbws", "湖北卫视", "2024171203", "600002508", "fhd", ChannelCategory.Provincial),
        new("c_gdws", "广东卫视", "2024060903", "600002485", "fhd", ChannelCategory.Provincial),
        new("c_gxws", "广西卫视", "2024060703", "600002509", "fhd", ChannelCategory.Provincial),
        new("c_hljws", "黑龙江卫视", "2029797003", "600002498", "fhd", ChannelCategory.Provincial),
        new("c_hnws2", "海南卫视", "2024055603", "600002506", "fhd", ChannelCategory.Provincial),
        new("c_cqws", "重庆卫视", "2024061103", "600002531", "fhd", ChannelCategory.Provincial),
        new("c_szws", "深圳卫视", "2024061303", "600002481", "fhd", ChannelCategory.Provincial),
        new("c_scws", "四川卫视", "2024061403", "600002516", "fhd", ChannelCategory.Provincial),
        new("c_henanws", "河南卫视", "2029797303", "600002525", "fhd", ChannelCategory.Provincial),
        new("c_fjdnhz", "东南卫视", "2024061503", "600002484", "fhd", ChannelCategory.Provincial),
        new("c_gzhws", "贵州卫视", "2024061603", "600002490", "fhd", ChannelCategory.Provincial),
        new("c_jxws", "江西卫视", "2024061703", "600002503", "fhd", ChannelCategory.Provincial),
        new("c_lnws", "辽宁卫视", "2024171303", "600002505", "fhd", ChannelCategory.Provincial),
        new("c_ahws", "安徽卫视", "2024171403", "600002532", "fhd", ChannelCategory.Provincial),
        new("c_hbws2", "河北卫视", "2024171503", "600002493", "fhd", ChannelCategory.Provincial),
        new("c_sdws", "山东卫视", "2029787903", "600002513", "fhd", ChannelCategory.Provincial),
        new("c_tjws", "天津卫视", "2019927003", "600152137", "fhd", ChannelCategory.Provincial),
        new("c_jlws", "吉林卫视", "2025561503", "600190405", "fhd", ChannelCategory.Provincial),
        new("c_shanxiws", "陕西卫视", "2029795103", "600190400", "fhd", ChannelCategory.Provincial),
        new("c_nxws", "宁夏卫视", "2025608503", "600190737", "fhd", ChannelCategory.Provincial),
        new("c_nmgws", "内蒙古卫视", "2025561203", "600190401", "fhd", ChannelCategory.Provincial),
        new("c_ynws", "云南卫视", "2025561303", "600190402", "fhd", ChannelCategory.Provincial),
        new("c_shanxiws2", "山西卫视", "2025560803", "600190407", "fhd", ChannelCategory.Provincial),
        new("c_qhws", "青海卫视", "2025559103", "600190406", "fhd", ChannelCategory.Provincial),
        new("c_xzws", "西藏卫视", "2025558003", "600190403", "fhd", ChannelCategory.Provincial),
        new("c_cetv1", "CETV1", "2022823801", "600171827", "fhd", ChannelCategory.Special),
        new("c_gxpd", "国学频道", "2029360403", "600213139", "fhd", ChannelCategory.Special),
        new("c_xjws", "新疆卫视", "2019927403", "600152138", "fhd", ChannelCategory.Provincial),
    };

    public List<Channel> FetchChannels()
    {
        var list = new List<Channel>(Table.Length);
        foreach (var t in Table)
        {
            // 低配设备软解带不动 4K/8K，超高清台不进节目单（DeviceProfile.HideUhd，UI 启动时按设备写入）
            if (Model.DeviceProfile.HideUhd &&
                (t.Name.Contains("4K", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("8K", StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(new Channel
            {
                Name = t.Name,
                Category = t.Cat,
                SourceId = SourceId,
                StreamUrl = $"https://bkliveinfo.ysp.cctv.cn/?cnlid={t.Cnlid}&livepid={t.Livepid}&defn={t.Defn}",
                Referer = "https://www.yangshipin.cn/",
                QualityTiers = new List<string> { t.Defn },
                Verdict = "可直接接（2026-11-07 全链路实测：API+清单+分片）",
                EpgKey = t.Id,
            });
        }
        return list;
    }
}
