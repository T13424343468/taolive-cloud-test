using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

/// <summary>
/// 内置台标资源映射（B 包第 12 项，v4 同源 63 枚 PNG，零网络依赖真机必现）。
/// key = 台名去空格后精确匹配——不用前缀匹配，杜绝「CCTV5+ 误配 CCTV5」一类传染；
/// 映射外的台返回 null，界面降级为首字占位（与 v4 ChannelLogo 口径一致）。
/// </summary>
public static class ChannelLogoMap
{
    /// <summary>台名（去空格）→ Android drawable 资源名（logo_ 前缀，SDK 自动从文件名生成）。</summary>
    private static readonly Dictionary<string, string> Map = new()
    {
        ["CCTV1综合"] = "logo_cctv1", ["CCTV2财经"] = "logo_cctv2", ["CCTV3综艺"] = "logo_cctv3",
        ["CCTV4中文国际"] = "logo_cctv4", ["CCTV5体育"] = "logo_cctv5", ["CCTV5+体育赛事"] = "logo_cctv5plus",
        ["CCTV6电影"] = "logo_cctv6", ["CCTV7国防军事"] = "logo_cctv7", ["CCTV8电视剧"] = "logo_cctv8",
        ["CCTV9纪录"] = "logo_cctv9", ["CCTV10科教"] = "logo_cctv10", ["CCTV11戏曲"] = "logo_cctv11",
        ["CCTV12社会与法"] = "logo_cctv12", ["CCTV13新闻"] = "logo_cctv13", ["CCTV14少儿"] = "logo_cctv14",
        ["CCTV15音乐"] = "logo_cctv15", ["CCTV16奥林匹克"] = "logo_cctv16", ["CCTV164K"] = "logo_cctv164k",
        ["CCTV17农业农村"] = "logo_cctv17", ["CCTV4K超高清"] = "logo_cctv4k", ["CCTV8K超高清"] = "logo_cctv8k",
        ["CGTN"] = "logo_cgtn", ["CGTN法语"] = "logo_cgtn_fayu", ["CGTN俄语"] = "logo_cgtn_eyu",
        ["CGTN纪录"] = "logo_cgtn_jilu",
        ["CCTV风云剧场"] = "logo_fengyun_juchang", ["CCTV第一剧场"] = "logo_diyi_juchang",
        ["CCTV怀旧剧场"] = "logo_huaijiu_juchang",
        ["北京卫视"] = "logo_beijing_ws", ["江苏卫视"] = "logo_jiangsu_ws", ["东方卫视"] = "logo_dongfang_ws",
        ["浙江卫视"] = "logo_zhejiang_ws", ["湖南卫视"] = "logo_hunan_ws", ["湖北卫视"] = "logo_hubei_ws",
        ["广东卫视"] = "logo_guangdong_ws", ["广西卫视"] = "logo_guangxi_ws", ["黑龙江卫视"] = "logo_heilongjiang_ws",
        ["海南卫视"] = "logo_hainan_ws", ["重庆卫视"] = "logo_chongqing_ws", ["深圳卫视"] = "logo_shenzhen_ws",
        ["四川卫视"] = "logo_sichuan_ws", ["河南卫视"] = "logo_henan_ws", ["东南卫视"] = "logo_dongnan_ws",
        ["贵州卫视"] = "logo_guizhou_ws", ["江西卫视"] = "logo_jiangxi_ws", ["辽宁卫视"] = "logo_liaoning_ws",
        ["安徽卫视"] = "logo_anhui_ws", ["河北卫视"] = "logo_hebei_ws", ["山东卫视"] = "logo_shandong_ws",
        ["天津卫视"] = "logo_tianjin_ws", ["吉林卫视"] = "logo_jilin_ws", ["陕西卫视"] = "logo_shaanxi_ws",
        ["宁夏卫视"] = "logo_ningxia_ws", ["内蒙古卫视"] = "logo_neimenggu_ws", ["云南卫视"] = "logo_yunnan_ws",
        ["山西卫视"] = "logo_shanxi_ws", ["青海卫视"] = "logo_qinghai_ws", ["西藏卫视"] = "logo_xizang_ws",
        ["新疆卫视"] = "logo_xinjiang_ws",
        ["CETV1"] = "logo_cetv1", ["钱江都市"] = "logo_qianjiang_dushi",
        ["经济生活"] = "logo_jingji_shenghuo", ["教科影视"] = "logo_jiaoke_yingshi",
    };

    /// <summary>台名（内部已做去空格）→ 资源名；无内置台标返回 null。</summary>
    public static string? ResourceName(string channelName)
    {
        var key = channelName?.Replace(" ", "") ?? "";
        return Map.TryGetValue(key, out var res) ? res : null;
    }

    /// <summary>映射表条数（测试断言：与搬运的 63 枚 PNG 一一对应）。</summary>
    public static int Count => Map.Count;
}
