using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace TaoLive.BLL;

/// <summary>
/// 央视网官方节目单（B 包第 14 项）：api.cntv.cn/epg/epginfo，v4 EpgSource 同源移植。
/// 口径（老板拍板）：官方接口无数据的台不出节目单不硬造——映射表外的台/接口失败/无节目，
/// 一律返回 null，界面隐藏节目区。
/// 响应形如 {&lt;c&gt;:{program:[{t=节目名, st/et=epoch 秒}]}}；按开播时间排序取当前+后续。
/// </summary>
public class CntvEpgClient
{
    /// <summary>节目条目：标题 + 起止（本地时区 Unix 秒转 DateTime）。</summary>
    public record Entry(string Title, DateTime Start, DateTime End);

    /// <summary>本台 EPG 键（央视频 c_cctv1 形态）→ 央视网 EPG 台号；映射外 = 官方无数据。</summary>
    private static readonly Dictionary<string, string> CntvIds = new()
    {
        // 央视（cctv1..cctv17 同名直映；5+ 与 4K/8K 特殊写法）
        ["c_cctv1"] = "cctv1", ["c_cctv2"] = "cctv2", ["c_cctv3"] = "cctv3", ["c_cctv4"] = "cctv4",
        ["c_cctv5"] = "cctv5", ["c_cctv5p"] = "cctv5plus", ["c_cctv6"] = "cctv6", ["c_cctv7"] = "cctv7",
        ["c_cctv8"] = "cctv8", ["c_cctv9"] = "cctv9", ["c_cctv10"] = "cctv10", ["c_cctv11"] = "cctv11",
        ["c_cctv12"] = "cctv12", ["c_cctv13"] = "cctv13", ["c_cctv14"] = "cctv14", ["c_cctv15"] = "cctv15",
        ["c_cctv16"] = "cctv16", ["c_cctv17"] = "cctv17", ["c_cctv4k"] = "cctv4k", ["c_cctv8k"] = "cctv8k",
        // 省级卫视（拼音台号）
        ["c_jsws"] = "jiangsu", ["c_dfws"] = "dongfang", ["c_zjws"] = "zhejiang", ["c_hnws"] = "hunan",
        ["c_hbws"] = "hubei", ["c_gdws"] = "guangdong", ["c_gxws"] = "guangxi", ["c_hljws"] = "heilongjiang",
        ["c_cqws"] = "chongqing", ["c_szws"] = "shenzhen", ["c_scws"] = "sichuan", ["c_henanws"] = "henan",
        ["c_gzhws"] = "guizhou", ["c_jxws"] = "jiangxi", ["c_lnws"] = "liaoning", ["c_ahws"] = "anhui",
        ["c_hbws2"] = "hebei", ["c_sdws"] = "shandong", ["c_tjws"] = "tianjin", ["c_jlws"] = "jilin",
        ["c_nxws"] = "ningxia", ["c_nmgws"] = "neimenggu", ["c_ynws"] = "yunnan", ["c_qhws"] = "qinghai",
        ["c_xzws"] = "xizang", ["c_xjws"] = "xinjiang",
        // 北京/海南/东南（福建）/陕西/山西/CGTN/剧场/省市链：官方接口无数据，不硬造
    };

    private const string UrlFmt = "https://api.cntv.cn/epg/epginfo?serviceId=tvcctv&c={0}&d={1}";

    /// <summary>映射表条数（测试断言与 v4 同源）。</summary>
    public static int MappedCount => CntvIds.Count;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>拉取指定 EPG 键今日节目单。返回 (当前节目, 后续节目)；映射外/失败/无数据返回 null。
    /// vc53 红灯B：接受取消令——UI 侧换台/退出可取消在飞请求（原写法 CTS 纯摆设，token 没传进请求）。</summary>
    public virtual (Entry Now, List<Entry> Next)? Fetch(string epgKey, CancellationToken ct = default)
    {
        if (epgKey is null || !CntvIds.TryGetValue(epgKey, out var c)) return null;
        var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        try
        {
            using var resp = _http.GetAsync(string.Format(UrlFmt, c, day), ct).ConfigureAwait(false).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var body = resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false).GetAwaiter().GetResult();
            return ParseWithKey(body, c, DateTime.Now);
        }
        catch (OperationCanceledException)
        {
            return null; // 取消属正常收场：不落日志不重试
        }
        catch
        {
            return null; // 失败静默：EPG 缺失不影响播放
        }
    }

    /// <summary>按台号键解析（Parse 的实际实现；JSON 形如 {&lt;c&gt;:{program:[{t,st,et}]}}）。</summary>
    public static (Entry Now, List<Entry> Next)? ParseWithKey(string body, string key, DateTime now)
    {
        try
        {
            using var root = JsonDocument.Parse(body);
            if (!root.RootElement.TryGetProperty(key, out var node)) return null;
            if (!node.TryGetProperty("program", out var program) || program.ValueKind != JsonValueKind.Array) return null;
            var entries = new List<Entry>();
            foreach (var e in program.EnumerateArray())
            {
                if (!e.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("st", out var stEl) || !e.TryGetProperty("et", out var etEl)) continue;
                if (!stEl.TryGetInt64(out var st) || !etEl.TryGetInt64(out var et)) continue;
                if (et <= st) continue;
                entries.Add(new Entry(tEl.GetString()!,
                    DateTimeOffset.FromUnixTimeSeconds(st).LocalDateTime,
                    DateTimeOffset.FromUnixTimeSeconds(et).LocalDateTime));
            }
            entries = entries.OrderBy(p => p.Start).ToList();
            if (entries.Count == 0) return null;
            var nowIdx = entries.FindLastIndex(p => p.Start <= now && now < p.End);
            if (nowIdx < 0)
            {
                // 全天节目未覆盖当前时刻（数据缺口）：取最后一条已开播的作当前，其后无后续
                var last = entries.LastOrDefault(p => p.Start <= now);
                return last is null ? null : (last, new List<Entry>());
            }
            return (entries[nowIdx], entries.Skip(nowIdx + 1).ToList());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>供解析测试用：暴露键名映射（拷贝防外部篡改）。</summary>
    public static IReadOnlyDictionary<string, string> IdMap => CntvIds;
}
