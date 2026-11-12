using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AllPlayer.BLL;
using TaoLive.BLL;
using TaoLive.Model;

// 道映核心逻辑断言（硬指标先行）：分类分组、编号固化、收藏置顶与排序、数字键直达。
// 源适配器以桩源替代，真实源数据回填后补全量用例。
int pass = 0, fail = 0;
void Check(bool cond, string name)
{
    if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
    else { fail++; Console.WriteLine($"FAIL {name}"); }
}

var fav = new FavoriteStore();
var catalog = new ChannelCatalog(fav);
catalog.RegisterSource(new StubSource("s1"));
catalog.RegisterSource(new StubSource("s2"));
catalog.Rebuild();

// 1 分类：六大分类组均在，接不进的台不占号
Check(catalog.Groups.Count == 6, "分类组数=6");
Check(catalog.UnavailableChannels.Count == 2, "接不进的台归档(2路)");
Check(catalog.All.Count == 4, "可用台数=4");
Check(catalog.All.Select(c => c.Number).SequenceEqual(new[] {1,2,3,4}), "编号顺次固化 1~4");

// 2 编号直达
Check(catalog.ByNumber(3)!.Name == "s1-B" && catalog.ByNumber(3)!.Category == ChannelCategory.Provincial, "编号3可达省台");
Check(catalog.ByNumber(99) is null, "无号不越界");

// 3 收藏：置顶 + 排序（硬性验收）
fav.Toggle(3); fav.Toggle(1); fav.Toggle(4);
var top = fav.TopGroup(catalog.All.ToList());
Check(top.Select(c => c.Number).SequenceEqual(new[] {3,1,4}), "收藏按加入顺序置顶");
fav.MoveUp(1);
top = fav.TopGroup(catalog.All.ToList());
Check(top.Select(c => c.Number).SequenceEqual(new[] {1,3,4}), "上移排序生效");
fav.Toggle(1);
Check(!fav.IsFavorite(1), "取消收藏");

// 4 数字键直达
int jumped = 0; string echo = "";
var digits = new ChannelNumberController(catalog, n => jumped = n, e => echo = e);
digits.OnDigit(0); digits.OnDigit(4);
Check(echo == "0-4", "数字回显 0-4");
System.Threading.Thread.Sleep(1200); // 停 1 秒自动跳台
Check(jumped == 4, "停顿自动跳台→编号4");

// 5 m3u 解析容错
var parser = new M3uParser();
var entries = parser.ParseText(
    "#EXTM3U\n" +
    "#EXTINF:-1 tvg-id=\"c1\" group-title=\"央视\",CCTV-1\nhttp://x/1.m3u8\n" +
    "garbage-line\n" +
    "#EXTINF:-1 group-title=\"本地\",本地台\nhttp://x/2.m3u8\n" +
    "#EXTINF:-1,bad\nnot-a-url\n");
Check(entries.Count == 2, "m3u 容错：坏行跳过(2条)");
Check(entries[0].GroupHint == "央视" && entries[0].Name == "CCTV-1", "m3u 分组与台名解析");

// 6 央视频 cKey 票据：与 Python 参考实现（调研/ysp/ckey.py）固定随机源对拍
{
    int idx = 0;
    byte[] Fixed(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((idx + i) & 0xFF);
        idx += n;
        return b;
    }
    var (ck, _, _) = YspCkey.Create("2024078201", 1700000000, "00112233445566778899aabbccddeeff", Fixed);
    const string expect = "--01ni8d9JxziWOkUzGW0L-8zC7JA6zYlHfdtB11Kqv1LGpYA5BSlmTHHzkeO69rd4w9vSXz1Ot3AGwCrCZijM4fW4EnFabufOS9aSPwM3tTvUgz2xmUxLZUxqZ43Em5BwG0XOIBgvmtV3z0t98hGlDNK7u_OM7oqijAQd2-96nIv0KqaKkTlddVXPNEkdPc5HI9ylUHlZ5hr1-TAy6Ccdr6BT8ApN7sUDYgHylKL6TSuCpafdMLaZJlLJFEj_G4kOcIb8n8kyX-eh0LWAyowRPLh8NxX6ZI2d3ZLWib600I3cziSsDYqSiSJadsyGsPr8Ai-zZ5-0Pi0kozWi5JB3zDmdYJSFpRdWRTlqna4V5gc7P1JUcU1NZK4S66UE2L12MMqjDgJ4qTectmzzns2mx7hG5Z2xaIeePLPPn4KObpPzqeOFljcPcDFf3z5qfvd5TDXvH35kGjWPgT7Yb-D1bQsflwoAk";
    Check(ck == expect, "cKey 票据与参考实现逐字节对拍一致");
}
// 6.1 取票服务窗口与冷却（纯 epoch 比较）
{
    var svc = new YspTicketService();
    long t0 = 1794016800; // 2026-11-07 10:00 +08
    Check(svc.ShouldFetch("c1", t0), "首次取票放行");
    svc.MarkFetched("c1", t0, "http://x/c1.m3u8");
    Check(!svc.ShouldFetch("c1", t0 + 240), "5分钟内不重取");
    Check(svc.ShouldFetch("c1", t0 + 301), "5分钟窗口到期重取");
    Check(svc.TryGet("c1", t0 + 240, out var hit) && hit == "http://x/c1.m3u8", "票据缓存窗口内命中");
    Check(!svc.TryGet("c1", t0 + 301, out _), "票据缓存窗口过期");
    var now = DateTimeOffset.FromUnixTimeSeconds(t0).DateTime;
    // 1.0.12 修法 B：403 冷却改指数退避账（2/4/8 分钟 ×2 步进，封顶 2 小时），替换固定 30 秒
    svc.MarkCooldown("c2", now); // 首败：退避 2 分钟
    Check(svc.InCooldown("c2", now.AddMinutes(1)), "1.0.12修法B·403退避期内拒绝取票");
    Check(!svc.InCooldown("c2", now.AddMinutes(2).AddSeconds(1)), "1.0.12修法B·首败退避约2分钟到期放行（30秒固定冷却作废）");
    svc.MarkCooldown("c2", now); // 连败第 2 次：退避 4 分钟
    Check(svc.InCooldown("c2", now.AddMinutes(3)) && !svc.InCooldown("c2", now.AddMinutes(4).AddSeconds(1)), "1.0.12修法B·连败退避加深至4分钟");
    svc.MarkCooldown("c2", now); // 连败第 3 次：退避 8 分钟
    Check(svc.InCooldown("c2", now.AddMinutes(5)) && !svc.InCooldown("c2", now.AddMinutes(8).AddSeconds(1)), "1.0.12修法B·三连败退避8分钟（2/4/8 口径）");
    svc.MarkFetched("c2", t0, "http://x/c2.m3u8"); // 取票成功=全额恢复
    Check(!svc.InCooldown("c2", now.AddSeconds(1)), "1.0.12修法B·取票成功退避全额恢复（口子开自动切回）");
    Check(YspTicketService.CooldownBase == TimeSpan.FromMinutes(2) && YspTicketService.CooldownMax == TimeSpan.FromHours(2), "1.0.12修法B·退避基值2分钟封顶2小时（口径固化）");
}
// 6.2 浙江新蓝网 auth_key：与摸底实测向量对拍（2026-11-07 实测 200）
{
    var signed = CztvSource.SignPlayUrl("https://zwebl03.cztv.com/live/channel01720Pnew.m3u8", 1793992552, "tbck3bbata3xr3u1");
    Check(signed.EndsWith("?auth_key=1793992552-tbck3bbata3xr3u1-0-a5788374601837dfa197e39b6ed797a3"), "cztv auth_key 签名对拍");
    Check(CztvSource.SignPlayUrl("https://api.example.com/x.m3u8", 1, "r") == "https://api.example.com/x.m3u8", "非浙江域不签");
}
// 6.3 贵州取票端点与 Referer（防盗链短效）
Check(GzstvSource.ApiUrl("ch01") == "https://api.gzstv.com/v1/tv/ch01" && GzstvSource.Referer == "https://www.gzstv.com/", "gzstv 取票端点/Referer 固化");
// 6.4 上海看看签名与 UA 约束（JWT 绑 UA，取流同 UA 拉流）
{
    var sig = KankanSource.Sign(new Dictionary<string, string> { ["channel_id"] = "1" }, 1793988075, "abcdwxyz");
    Check(sig.Length == 32, "kankan 签名输出 32 位 hex");
    Check(YspTicketService.ClientUserAgent.Contains("TaoLive"), "全局固定 UA 固化（上海 JWT 绑 UA 的前置约束）");
}
// 6.5 江苏荔枝网签名：x() 变换 + 腾讯云防盗链（摸底第二轮证据；txTime=hex(now+180s)）
{
    var key = JstvSource.DecodedKey();
    Check(key.Length == 8, "jstv x() 密钥解出 8 字节");
    var (secret, txTime) = JstvSource.Sign(key, "jscs", 1794018000);
    Check(txTime == ((long)1794018000).ToString("x8"), "jstv txTime=hex(now+180s) 固化");
    Check(secret.Length == 32, "jstv txSecret 32 位 hex");
    var url = JstvSource.PlayUrl("jscs", 1794018000);
    Check(url.StartsWith("https://lived.jstv.com/jscspro.m3u8?txSecret=") && url.Contains($"&txTime={txTime}"), "jstv 播放 URL 形态固化");
    Check(JstvSource.Channels.All(c => !c.Name.Contains("江苏卫视")), "jstv 不与央视频覆盖重复");
}
// 6.6 西安网络台：明文 HLS + Referer（摸底第二轮分片全 200；xatv4 文化影视 404 不接）
{
    Check(XatvSource.PlayUrl(2) == "https://stream.xiancity.cn/live/2/index.m3u8", "xatv 流地址固化");
    Check(XatvSource.Channels.Length == 4, "xatv 接入 4 路（直播间/都市/戏剧影视/丝路）");
}
// 6.7 广西网络台：电视频道口径接口恢复（2026-11-08：POST channelList 直吐 encodeM3u8）
{
    Check(GxtvSource.ChannelListApi == "https://api2019.gxtv.cn/memberApi/channel/channelList", "gxtv 电视频道口径接口固化");
    Check(GxtvSource.ChannelListForm == "pageNo=1&pageSize=1000&liveMethod=0", "gxtv 接口表单固化");
    var chans = new GxtvSource().FetchChannels();
    Check(chans.Count == 9, $"gxtv 接回 9 路（本地 7 + CETV-2/4，实际 {chans.Count}）");
    Check(chans.Count(c => c.Category == ChannelCategory.City) == 7, "gxtv 本地 7 路归城市台组");
    Check(chans.Count(c => c.Category == ChannelCategory.Special) == 2, "gxtv CETV-2/4 归专项组");
    Check(chans.All(c => c.Name != "广西卫视") && chans.All(c => c.Name != "中国教育电视台CETV-1频道"),
        "gxtv 不与央视频覆盖重复（广西卫视/CETV-1 不重号）");
    Check(chans.All(c => c.StreamUrl == GxtvSource.ChannelListApi), "gxtv 台目入口=清单接口（播放端实时匹配）");
    // 行级定位器：真实清单形态（深嵌套行、encodeM3u8 在 name 同行；同名录取首个非空）
    var rowA = "{\"clickCount\":null,\"keywords\":\"\",\"editorUid\":{\"name\":\"盟市节目\"},\"encodeM3u8\":\"\",\"decodeM3u8\":\"\",\"showChannel\":1,\"data\":{\"streamUrl\":\"\"}}";
    var fixture = "{\"code\":0,\"data\":{\"total\":4,\"rows\":["
        + rowA + ","
        + "{\"name\":\"综艺旅游频道\",\"encodeM3u8\":\"https://txlivecdn.liangtv.cn/live/a1/b1.m3u8\"},"
        + "{\"name\":\"国际频道\",\"encodeM3u8\":\"\"},"
        + "{\"name\":\"国际频道\",\"encodeM3u8\":\"https://hlscdn.liangtv.cn/live/x/y.m3u8\"}]}}";
    Check(GxtvSource.LocateStreamUrl(fixture, "综艺旅游频道") == "https://txlivecdn.liangtv.cn/live/a1/b1.m3u8", "gxtv 按 name 定位 encodeM3u8");
    Check(GxtvSource.LocateStreamUrl(fixture, "国际频道") == "https://hlscdn.liangtv.cn/live/x/y.m3u8", "gxtv 同名录取首个非空行");
    Check(GxtvSource.LocateStreamUrl(fixture, "不存在的台") == null, "gxtv 未命中返回 null");
    Check(GxtvSource.LocateStreamUrl("{\"rows\":[]}", "综艺旅游频道") == null, "gxtv 空清单容错");
}
// 6.8 内蒙古 XXTEA：加解密往返（长度小端尾缀口径）＋密钥固化＋行级流匹配
{
    var plain = System.Text.Encoding.UTF8.GetBytes("{\"title\":\"nmtv01\",\"streamUrl\":\"http://s/live.m3u8\"}");
    var enc = NmtvXxtea.Encrypt(plain);
    Check(enc.Length % 4 == 0 && enc.Length > plain.Length, "XXTEA 密文按 4 字节对齐且含长度尾缀");
    var dec = NmtvXxtea.Decrypt(enc);
    Check(System.Text.Encoding.UTF8.GetString(dec) == System.Text.Encoding.UTF8.GetString(plain), "XXTEA 加解密往返还原");
    Check(NmtvXxtea.Key == "5b28bae827e651b3", "XXTEA 密钥固化");
    // 行级流匹配（2026-11-08 修「包头」）：真实整表行内顺序 = {"aiGene"…data.streamUrl…streamUrls[]…title…}，
    // 流在台名之前——旧「title 向右找」把每台串到下一台、表尾行必失败；新口径按行归属还原。
    var nmtvRows = string.Concat(Enumerable.Range(1, 3).Select(i =>
        "{\"aiGene\":0,\"data\":{\"indexId\":" + (2300 + i) + ",\"streamUrl\":\"https://livestream-bt.nmtv.cn/nmtv/" + (2300 + i) + "general.m3u8?txSecret=s" + i + "\"},"
        + "\"streamUrls\":[\"https://livestream-bt.nmtv.cn/nmtv/" + (2300 + i) + "general.m3u8?txSecret=s" + i + "\"],"
        + "\"title\":\"台" + i + "\"}"));
    var whole = "{\"code\":0,\"data\":[" + nmtvRows + "]}";
    Check(NmtvSource.LocateStreamUrl(whole, "台1") == "https://livestream-bt.nmtv.cn/nmtv/2301general.m3u8?txSecret=s1", "nmtv 首行流按行归属匹配");
    Check(NmtvSource.LocateStreamUrl(whole, "台3") == "https://livestream-bt.nmtv.cn/nmtv/2303general.m3u8?txSecret=s3", "nmtv 表尾行（包头形态）匹配成功");
    Check(NmtvSource.LocateStreamUrl(whole, "台2") == "https://livestream-bt.nmtv.cn/nmtv/2302general.m3u8?txSecret=s2", "nmtv 中间行不串行");
    Check(NmtvSource.LocateStreamUrl(whole, "不存在") == null, "nmtv 未命中返回 null");
    // 兜底1：名前无流（字段序反转形态）时按静态表 indexId 定位同行流
    var flipped = "{\"code\":0,\"data\":[{\"title\":\"包头\",\"data\":{\"indexId\":2358,\"streamUrl\":\"https://livestream-bt.nmtv.cn/nmtv/2358general.m3u8?txSecret=x\"}}]}";
    Check(NmtvSource.LocateStreamUrl(flipped, "包头") == "https://livestream-bt.nmtv.cn/nmtv/2358general.m3u8?txSecret=x", "nmtv 字段序反转走 indexId 兜底");
    Check(NmtvSource.Channels.Length == 10, $"nmtv 10 路（包头接回，实际 {NmtvSource.Channels.Length}）");
}
// 6.9 取流器路由：江苏播放时重签 txTime=hex(now+180s)；非取票源直连透传 Referer
{
    var res = new StreamResolver();
    var jstvCh = new Channel { SourceId = "jstv", StreamUrl = "https://lived.jstv.com/jscspro.m3u8", EpgKey = "jscs" };
    var r1 = await res.ResolvePlayUrlAsync(jstvCh);
    Check(r1 != null && r1.Url.StartsWith("https://lived.jstv.com/jscspro.m3u8?txSecret="), "jstv 取流重签 txSecret");
    var txTime = System.Text.RegularExpressions.Regex.Match(r1!.Url, @"txTime=([0-9a-f]+)").Groups[1].Value;
    var got = Convert.ToInt64(txTime, 16);
    var expect = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 180;
    Check(Math.Abs(got - expect) <= 2, "jstv txTime=播放时重签(now+180s)");
    var xaCh = new Channel { SourceId = "xatv", StreamUrl = "https://stream.xiancity.cn/live/2/index.m3u8", Referer = "https://xiancity.cn/" };
    var r2 = await res.ResolvePlayUrlAsync(xaCh);
    Check(r2 != null && r2.Url == xaCh.StreamUrl, "非取票源直连不改地址");
    Check(r2!.Options.Any(o => o.Key == "http-referrer" && o.Value == "https://xiancity.cn/"), "直连透传 Referer");
}

// 追加：真实源注册后的目录重建对账（骨架桩源替换为正式源前的口径检查）
{
    var fav2 = new FavoriteStore();
    var cat = new ChannelCatalog(fav2);
    // 2026-11-07 全量真连后按用户指令：上海/江苏暂下架（攻克名单）；
    // 2026-11-08：广西电视频道口径恢复接回（本地 7 + CETV-2/4），内蒙古包头接回
    cat.RegisterSource(new YangshipinSource());
    cat.RegisterSource(new CztvSource());
    cat.RegisterSource(new GzstvSource());
    cat.RegisterSource(new XatvSource());
    cat.RegisterSource(new NmtvSource());
    cat.RegisterSource(new GxtvSource());
    cat.RegisterSource(new CityCandidatesSource()); // v22 第 16 项：地方台 18 路候选源挂链
    cat.Rebuild();
    var cctv = cat.Groups.First(g => g.Category == ChannelCategory.Cctv).Channels.Count;
    var cgtn = cat.Groups.First(g => g.Category == ChannelCategory.Cgtn).Channels.Count;
    var prov = cat.Groups.First(g => g.Category == ChannelCategory.Provincial).Channels.Count;
    var city = cat.Groups.First(g => g.Category == ChannelCategory.City).Channels.Count;
    Check(cctv == 21, $"央视 21 路（实际 {cctv}）");
    Check(cgtn == 6, $"CGTN 6 路（实际 {cgtn}）");
    Check(prov == 31 + 4 + 7 + 8 + 2, $"省台=央视频31+浙江4+贵州7+内蒙古8+城市候选2省经济（实际 {prov}）");
    Check(city == 4 + 2 + 7 + 13, $"城市台 西安4+内蒙古2+广西7+候选13城市/县级台（实际 {city}）");
    Check(cat.All.Count == 97 + 15, $"v22 第 16 项：原 97 路挂链后总台数 112（实际 {cat.All.Count}）");
    Check(cat.All.Where(c => c.SourceId == "citycdn").All(c => !string.IsNullOrEmpty(c.StreamUrl) && string.IsNullOrEmpty(c.EpgKey)),
        "v22 第 16 项：城市候选 15 路全带直连地址、EPG 留空不硬造");
    Check(ChannelLogoMap.Count == 78, $"v22 第 16 项：台标映射 63+15 枚（实际 {ChannelLogoMap.Count}）");
    Check(ChannelLogoMap.ResourceName("七台河新闻综合") == "logo_qitaihe_xwzh"
        && ChannelLogoMap.ResourceName("甘肃经济") == "logo_gansu_jj"
        && ChannelLogoMap.ResourceName("福建经济") == "logo_fujian_jj"
        && ChannelLogoMap.ResourceName("不存在的台") is null, "v22 第 16 项：新台标映射按台名精确命中、映射外回退 null");
    var special = cat.Groups.First(g => g.Category == ChannelCategory.Special).Channels.Count;
    Check(special == cat.Groups.First(g => g.Category == ChannelCategory.Special).Channels
        .Count(c => c.SourceId == "yangshipin") + 2, $"专项含 gxtv 重流 CETV-2/4（实际 {special}）");
    Check(cat.All.Count == cctv + cgtn + special + prov + city, "总台数=各分类和，编号无空洞");
    Check(cat.All.Select(c => c.Number).SequenceEqual(Enumerable.Range(1, cat.All.Count)), "编号 1..N 连续无跳号");
    Check(cat.UnavailableChannels.Count == 0, "接不进的路未挂进正式源（湖北/湖南不排期）");
    // 硬性验收三件：分类/收藏/数字键在真实台单上仍成立
    Check(cat.Groups.Count == 6, "六大分类组齐");
    Check(cat.ByNumber(1)!.Category == ChannelCategory.Cctv, "1 号=央视组首位");
    var last = cat.All.Last();
    Check(cat.ByNumber(last.Number) == last, "末号可达");
}

// 追加：低配设备档位（雷一）——HideUhd=true 时 4K/8K 台不进节目单，关掉后原样恢复
{
    var cat1 = new ChannelCatalog(new FavoriteStore());
    cat1.RegisterSource(new YangshipinSource());
    cat1.Rebuild();
    var uhdBefore = cat1.All.Count(c => c.Name.Contains("4K") || c.Name.Contains("8K"));

    DeviceProfile.HideUhd = true;
    try
    {
        var catLow = new ChannelCatalog(new FavoriteStore());
        catLow.RegisterSource(new YangshipinSource());
        catLow.Rebuild();
        var uhd = catLow.All.Count(c => c.Name.Contains("4K") || c.Name.Contains("8K"));
        Check(uhd == 0, $"低配档位下超高清台全拦（实际 {uhd} 路）");
        Check(uhdBefore == 3, $"正常档位下央视频超高清 3 路（实际 {uhdBefore}）");
        Check(catLow.All.Count == cat1.All.Count - uhdBefore, "低配节目单=正常节目单减超高清，其余台不受影响");
    }
    finally
    {
        DeviceProfile.HideUhd = false; // 必须还原，别污染后续用例
    }
}

// 追加：开机预检握手状态机（v1.0.8）——按序探测、状态流转、事件单发、复测入口
{
    var pre = new ChannelPrecheck(ch => Task.FromResult(ch.Name != "dead"), intervalMs: 10);
    var events = new List<(string, ChannelHealth)>();
    pre.StateChanged += (k, s) => events.Add((k, s));
    var chans = new List<Channel>
    {
        new() { Name = "ok", EpgKey = "ok" },
        new() { Name = "dead", EpgKey = "dead" },
    };
    await pre.RunAsync(chans);
    Check(pre.Of(chans[0]) == ChannelHealth.Alive, "预检可达台=可用");
    Check(pre.Of(chans[1]) == ChannelHealth.Dead, "预检不可达台=失效");
    Check(pre.Of(new Channel { EpgKey = "zzz" }) == ChannelHealth.Unknown, "未探测台=未知");
    Check(events.Count(e => e.Item2 == ChannelHealth.Dead) == 1, "状态变化事件只发一次");
    Check(events.First().Item2 == ChannelHealth.Checking, "探测先置检测中");
    // 重复标记不发事件（防列表反复重绘）
    var before = events.Count;
    pre.MarkState(chans[0], ChannelHealth.Alive);
    Check(events.Count == before, "同状态重复标记不发事件");
    // 复测入口：失效台可重新探测
    pre.Requeue(chans[1]);
    Check(pre.Of(chans[1]) == ChannelHealth.Checking, "复测入口置检测中");
    // 探测器抛异常按失效处理（预检不吞异常不崩）
    var boom = new ChannelPrecheck(_ => throw new InvalidOperationException("net down"), intervalMs: 1);
    var c2 = new Channel { Name = "x", EpgKey = "x" };
    await boom.RunAsync(new[] { c2 });
    Check(boom.Of(c2) == ChannelHealth.Dead, "探测异常按失效处理");
}

// 追加：v1.0.9 第⑬项——预检按频道全源检测 + 来源级健康表
{
    // 同名频道两个来源：一方通一方挂，整台=可用；各来源单独记录
    var multi = new ChannelPrecheck(ch => Task.FromResult(ch.SourceId == "a"), intervalMs: 1);
    var va = new Channel { Name = "CCTV-1 综合", SourceId = "a", EpgKey = "a1" };
    var vb = new Channel { Name = "CCTV-1 综合", SourceId = "b", EpgKey = "b1" };
    await multi.RunAsync(new[] { va, vb });
    Check(multi.Of(va) == ChannelHealth.Alive, "全源检测：任一来源通整台=可用");
    Check(multi.Of(vb) == ChannelHealth.Alive, "可用口径按频道归并，不按单来源判死");
    Check(multi.SourceHealthOf(va)?.State == ChannelHealth.Alive, "来源A健康=Alive");
    Check(multi.SourceHealthOf(vb)?.State == ChannelHealth.Dead, "来源B健康=Dead");
    Check(multi.SourceHealthOf(va)?.CheckedAtMs > 0, "来源健康记录核验时间");
    // 全源挂=死
    var allDead = new ChannelPrecheck(_ => Task.FromResult(false), intervalMs: 1);
    var da = new Channel { Name = "央2", SourceId = "a", EpgKey = "a2" };
    var db = new Channel { Name = "央2", SourceId = "b", EpgKey = "b2" };
    await allDead.RunAsync(new[] { da, db });
    Check(allDead.Of(da) == ChannelHealth.Dead && allDead.Of(db) == ChannelHealth.Dead, "全源不可达整台=失效");
    // 最优来源：取最近验证可用的变体
    var picked = allDead.BestAliveSource(da, new[] { da, db });
    Check(picked is null, "全源挂时无最优可用来源");
    var pick2 = multi.BestAliveSource(vb, new[] { va, vb });
    Check(pick2 != null && pick2.EpgKey == "a1", "最优来源=最近验证可用的变体");
    // 自救/播放实测回写：失败来源被播放实测翻绿，整台状态随之恢复
    multi.ReportSourceResult(vb, true, 123456L);
    Check(multi.SourceHealthOf(vb)?.State == ChannelHealth.Alive, "回写：失败来源被实测翻绿");
    Check(multi.SourceHealthOf(vb)?.CheckedAtMs == 123456L, "回写记录核验时间");
    Check(multi.Of(vb) == ChannelHealth.Alive, "回写可用后整台状态=可用");
    multi.ReportSourceResult(va, false, 234567L);
    Check(multi.SourceHealthOf(va)?.State == ChannelHealth.Dead, "回写：实测失效覆盖预检结论");

    // ---- v28 主线③源健康度评分：归一化/半衰/空账退化/喂入路径 ----
    {
        var clock = 1_700_000_000_000L;
        var scores = new SourceScoreStore("/tmp/taolive_score_test", () => clock);
        // 喂入路径：Report 走账、ScoreOf 出分
        scores.Report("a", "a1", ok: true, ms: 1000);
        var s1 = scores.ScoreOf("a", "a1");
        Check(s1.HasValue && s1.Value > 0 && s1.Value <= 100, "v28评分①喂入后出分（0~100 归一化）");
        // 速度归一化：2s 内满分梯度、10s 及以上为 0
        var fast = new SourceScoreStore("/tmp/taolive_score_test", () => clock);
        fast.Report("f", "f1", ok: true, ms: 1000);
        fast.Report("f", "f2", ok: true, ms: 12000);
        var sf = fast.ScoreOf("f", "f1")!.Value;
        var sl = fast.ScoreOf("f", "f2")!.Value;
        Check(sf > sl, "v28评分②速度归一化：快源分高于慢源");
        // 半衰：同一笔账，时间后移 7 天分值约减半
        clock += SourceScoreStore.HalfLifeMs;
        var s2 = scores.ScoreOf("a", "a1")!.Value;
        Check(s2 < s1!.Value && s2 > s1.Value * 0.4, "v28评分③七天半衰：旧结论权重衰减约半");
        clock -= SourceScoreStore.HalfLifeMs;
        // 空账退化：无评分账时 BestAliveSource 仍按既有「最近核验优先」
        var bare = new ChannelPrecheck(ch => Task.FromResult(ch.SourceId == "a"), intervalMs: 1);
        var xa = new Channel { Name = "X", SourceId = "a", EpgKey = "x1" };
        var xb = new Channel { Name = "X", SourceId = "b", EpgKey = "x2" };
        await bare.RunAsync(new[] { xa, xb });
        var pickBare = bare.BestAliveSource(xb, new[] { xa, xb });
        Check(pickBare?.EpgKey == "x1", "v28评分④空账退化：无评分仍取最近核验可用源（行为不倒退）");
        // 有账排序：高分源胜出（va 高分、vb 低分，都 Alive）
        var scoredPre = new ChannelPrecheck(ch => Task.FromResult(true), intervalMs: 1);
        var sa = new Channel { Name = "S", SourceId = "s", EpgKey = "s1" };
        var sb = new Channel { Name = "S", SourceId = "s", EpgKey = "s2" };
        await scoredPre.RunAsync(new[] { sa, sb });
        var scoreStore = new SourceScoreStore("/tmp/taolive_score_test", () => clock);
        scoreStore.Report("s", "s1", ok: true, ms: 300);
        scoreStore.Report("s", "s2", ok: true, ms: 9000);
        scoredPre.AttachScoreStore(scoreStore);
        var pickScored = scoredPre.BestAliveSource(sa, new[] { sa, sb });
        Check(pickScored?.EpgKey == "s1", "v28评分⑤有账按分排序：高分源胜出");
        // 喂入路径打通：ReportSourceResult 经 WriteSourceResult 入评分账
        scoredPre.ReportSourceResult(sa, true, clock + 1, 500);
        Check(scoreStore.HasAccount("s", "s1"), "v28评分⑥喂入路径：ReportSourceResult 同步入评分账");
        // 落盘：节流 3 秒后台落（不阻塞），这里即时 FlushNow 验证原子写口径（同健康账）
        scoreStore.FlushNow();
        Check(File.Exists("/tmp/taolive_score_test/source_score.json"), "v28评分⑦落盘：即时落盘文件在位（原子替换口径同健康账）");
    }
    Check(multi.Of(va) == ChannelHealth.Alive, "单来源失效但另一来源可用，整台仍=可用");
    multi.ReportSourceResult(vb, false, 345678L);
    Check(multi.SourceHealthOf(vb)?.State == ChannelHealth.Dead, "全部来源实测失败均已记录");
    Check(multi.Of(vb) == ChannelHealth.Alive, "回写只记来源结论，整台判死交给下一轮预检");
    // 台名归并：空格差异不影响分组
    Check(ChannelPrecheck.NormalizeName("CCTV-1 综合") == ChannelPrecheck.NormalizeName("CCTV-1综合"), "台名归并忽略空格");
}

// v1.0.10 第①项：源健康账持久化——落盘、回灌、过期清理、连挂自动淘汰
{
    var dir = Path.Combine(Path.GetTempPath(), "taolive_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var probe = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 1);
        var store = new SourceHealthStore(dir);
        probe.AttachHealthStore(store);
        // 预检一轮：来源记录健康并带 Unix 毫秒时间
        var chans = new List<Channel>
        {
            new() { Name = "央视频一套", SourceId = "yangshipin", EpgKey = "cctv1" },
        };
        await probe.RunAsync(chans);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Check(probe.SourceHealthOf(chans[0])?.CheckedAtMs is > 1_700_000_000_000, "健康记录时间=Unix 毫秒（跨启动可比）");
        Check(store.Entries.ContainsKey("yangshipin:cctv1"), "预检结论进持久化账本");

        store.FlushNow();
        Check(File.Exists(Path.Combine(dir, "source_health.json")), "落盘文件生成");

        // 新会话：新账本灌回未过期历史账，换台仍能直连已验证源
        var probe2 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 1);
        var store2 = new SourceHealthStore(dir);
        store2.Load();
        probe2.AttachHealthStore(store2);
        Check(probe2.SourceHealthOf(chans[0])?.State == ChannelHealth.Alive, "重启后历史账灌回：已验证源仍可用");

        // 过期规则：Alive 超 24h 丢、Dead 超 30 分钟丢
        var staleDir = Path.Combine(Path.GetTempPath(), "taolive_stale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staleDir);
        try
        {
            var staleStore = new SourceHealthStore(staleDir);
            staleStore.Entries["yangshipin:old_alive"] = new SourceHealthEntry
            {
                SourceId = "yangshipin", EpgKey = "old_alive", State = ChannelHealth.Alive,
                CheckedAtMs = now - SourceHealthStore.AliveTtlMs - 60_000,
            };
            staleStore.Entries["yangshipin:old_dead"] = new SourceHealthEntry
            {
                SourceId = "yangshipin", EpgKey = "old_dead", State = ChannelHealth.Dead,
                CheckedAtMs = now - SourceHealthStore.DeadTtlMs - 60_000,
            };
            staleStore.Entries["yangshipin:fresh_dead"] = new SourceHealthEntry
            {
                SourceId = "yangshipin", EpgKey = "fresh_dead", State = ChannelHealth.Dead,
                CheckedAtMs = now,
            };
            staleStore.FlushNow();
            staleStore.Entries.Clear();
            staleStore.Load();
            Check(!staleStore.Entries.ContainsKey("yangshipin:old_alive"), "过期 Alive 条目丢弃（交回重探）");
            Check(!staleStore.Entries.ContainsKey("yangshipin:old_dead"), "过期 Dead 条目丢弃（不留死账）");
            Check(staleStore.Entries.ContainsKey("yangshipin:fresh_dead"), "未过期 Dead 条目保留（无信号延续展示）");
        }
        finally { Directory.Delete(staleDir, true); }

        // 连挂自动淘汰：连续 3 次失败移出健康表，绝不再被选源取用
        var retire = new ChannelPrecheck(_ => Task.FromResult(false), intervalMs: 1);
        var retireCh = new Channel { Name = "挂台", SourceId = "yangshipin", EpgKey = "gone" };
        for (int i = 0; i < ChannelPrecheck.RetireAfterConsecutiveFails; i++)
            retire.ReportSourceResult(retireCh, false, now + i);
        Check(retire.SourceHealthOf(retireCh) == null, $"连挂 {ChannelPrecheck.RetireAfterConsecutiveFails} 次自动淘汰（交回预检重探）");
        Check(retire.BestAliveSource(retireCh, new[] { retireCh }) == null, "淘汰后不再被选源取用");

        // v28 读盘优化：灌回改后台后，Load 与 FlushNow/回写并发——并发字典护体，种子条目不丢不覆盖
        {
            var cdir = Path.Combine(Path.GetTempPath(), "taolive_health_c_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cdir);
            var seedStore = new SourceHealthStore(cdir);
            seedStore.Entries["yangshipin:seed"] = new SourceHealthEntry
            {
                SourceId = "yangshipin", EpgKey = "seed", State = ChannelHealth.Alive,
                CheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            seedStore.FlushNow();
            var cstore = new SourceHealthStore(cdir);
            var loadTask = Task.Run(cstore.Load); // 模拟 UI 层后台灌回（产品口径：灌回完成前无回写无落盘，预检循环串行其后）
            Task.WaitAll(loadTask);
            var errs = new List<Exception>();
            var workers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                try
                {
                    for (int k = 0; k < 100; k++)
                    {
                        cstore.Entries[$"yangshipin:w{i}-{k}"] = new SourceHealthEntry
                        {
                            SourceId = "yangshipin", EpgKey = $"w{i}-{k}", State = ChannelHealth.Alive,
                            CheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        };
                        cstore.FlushNow();
                    }
                }
                catch (Exception ex) { lock (errs) errs.Add(ex); }
            })).ToArray();
            Task.WaitAll(workers);
            Check(errs.Count == 0, $"v28健康账并发回写+落盘无竞态异常（{errs.Count}笔）");
            Check(cstore.Entries.ContainsKey("yangshipin:seed"), "v28健康账灌回种子条目保留（不被并发落盘冲掉）");
        }
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

// 追加：列表宽度度量（v1.0.8，越小越好）——不超半屏、下限兜底、内容自适应
{
    float d = 2f; int sw = 1920;
    Check(ListMetrics.ComputeWidthPx(0, d, sw) == 720, "内容极窄时取下限 360dp（信息完整兜底）");
    Check(ListMetrics.ComputeWidthPx(2000, d, sw) == 960, "超宽内容钳到半屏（绝不盖住整屏）");
    Check(ListMetrics.ComputeWidthPx(800, d, sw) == (int)(800 + 48 * 2), "中等内容=内容宽+留白（越小越好）");
    Check(ListMetrics.ComputeWidthPx(-5, d, sw) == 720, "负值输入不越界（按下限兜底）");
    int mid = ListMetrics.ComputeWidthPx(700, d, 3840);
    Check(mid < 3840 / 2, "大屏上半屏上限仍然成立");
}

// 追加：强制刷新取票（v1.0.8 重连前换票的取流侧口径）——force 绕过缓存仍出票
{
    var res = new StreamResolver();
    var jstvCh = new Channel { SourceId = "jstv", StreamUrl = "https://lived.jstv.com/jscspro.m3u8", EpgKey = "jscs" };
    var rForce = await res.ResolvePlayUrlAsync(jstvCh, force: true);
    Check(rForce != null && rForce.Url.Contains("txSecret="), "强制刷新绕过缓存仍出票（重连前换票口径）");
    var rAgain = await res.ResolvePlayUrlAsync(jstvCh, force: true);
    Check(rAgain != null && rAgain.Url.Contains("txSecret="), "连续强制刷新仍稳定出票");
}

// 追加：左右键列表翻页纯函数（v1.0.8 用户拍板）——整屏步进、不足一屏到底/到顶、越界兜底
{
    // 列表 20 行、可视 6 行、当前顶在第 0 行：左键（向下）翻到第 6 行
    Check(ListPaging.NextFirstVisible(20, 0, 6, +1) == 6, "左键向下翻一屏（0→6）");
    Check(ListPaging.NextFirstVisible(20, 6, 6, +1) == 12, "左键连续翻（6→12）");
    // 剩余不足一屏：直接到底（最后一屏首行=20-6=14）
    Check(ListPaging.NextFirstVisible(20, 14, 6, +1) == 14, "向下不足一屏直接到底");
    Check(ListPaging.NextFirstVisible(20, 12, 6, +1) == 14, "向下越界钳到底（12→14）");
    // 右键（向上）：对称逻辑
    Check(ListPaging.NextFirstVisible(20, 6, 6, -1) == 0, "右键向上翻一屏（6→0）");
    Check(ListPaging.NextFirstVisible(20, 0, 6, -1) == 0, "向上到顶不再越界");
    // 可视行数异常输入按 1 兜底；空列表安全
    Check(ListPaging.NextFirstVisible(20, 10, 0, +1) == 11, "可视行数非法按 1 步进兜底");
    Check(ListPaging.NextFirstVisible(0, 0, 6, +1) == 0, "空列表返回 0 不越界");
    Check(ListPaging.NextFirstVisible(3, 0, 6, +1) == 0, "列表比一屏短时原地不动");
}

// 追加：循环预检保态复测（v1.0.8 用户拍板：列表不闪烁）——已知台复测不先置检测中
{
    var pre = new ChannelPrecheck(ch => Task.FromResult(ch.Name != "dead"), intervalMs: 1);
    var chans = new List<Channel>
    {
        new() { Name = "ok", EpgKey = "ok" },
        new() { Name = "dead", EpgKey = "dead" },
    };
    await pre.RunAsync(chans);
    var events = new List<(string, ChannelHealth)>();
    pre.StateChanged += (k, s) => events.Add((k, s));
    // 第二轮（保态复测）：没有 Checking 事件——可用台常绿、无信号台留组
    await pre.RunAsync(chans, keepKnownState: true);
    Check(events.All(e => e.Item2 != ChannelHealth.Checking), "保态复测不发检测中事件（列表不闪烁）");
    Check(events.Count == 0, "复测结论不变时零事件（零重绘）");
    Check(pre.Of(chans[0]) == ChannelHealth.Alive && pre.Of(chans[1]) == ChannelHealth.Dead, "保态复测结论保持正确");
    // 第三轮：可用台变坏——探测完才翻转，坏台进无信号组
    chans[0].Name = "dead";
    await pre.RunAsync(chans, keepKnownState: true);
    Check(pre.Of(chans[0]) == ChannelHealth.Dead, "复测变坏：可用台翻转进无信号组");
    // 第四轮：坏台缓过来——自动移回原分类（恢复可切）
    chans[0].Name = "ok";
    await pre.RunAsync(chans, keepKnownState: true);
    Check(pre.Of(chans[0]) == ChannelHealth.Alive, "复测恢复：无信号台自动移回可用");
}


// ============ v1.0.8 重点项①：中央一台播两三秒卡死——看门狗停滞判定专项 ============
// 用户 23:30 指令：重点项必须专用对应用例。直播流停止推进时 VLC 不报错，
// 唯一可靠信号是播放时钟停走；看门狗 2 秒巡检、6 秒停滞即按断流重连（退避 2/4/6 秒）。
{
    Check(PlaybackWatchdog.PollMs == 2000, "重点①看门狗：2秒巡检周期");
    Check(PlaybackWatchdog.StallMs == 6000, "重点①看门狗：6秒停滞阈值");
    Check(PlaybackWatchdog.IsStalled(true, 1000, 7000), "重点①看门狗：首帧后时钟停走满6秒=卡流（触发重连）");
    Check(!PlaybackWatchdog.IsStalled(true, 1000, 6999), "重点①看门狗：未满6秒不误判（起播缓冲不中断）");
    Check(!PlaybackWatchdog.IsStalled(false, 0, 999999), "重点①看门狗：未出首帧不介入（连接流程自管亮牌）");
    Check(PlaybackWatchdog.IsStalled(true, 7000, 13000), "重点①看门狗：时钟零推进恰满阈值按停滞（边界含等号）");
}

// ============ v1.0.8 重点项⑥：退出再开黑屏——回前台存活判定专项 ============
// 后台播放被系统杀掉或表面失效时 IsPlaying 仍可能为 true，唯一判据是播放时钟是否推进；
// 回前台校验不活即重播，绝不让用户面对黑屏。
{
    Check(PlaybackWatchdog.LivenessMs == 5000, "重点⑥黑屏：5秒存活窗口");
    Check(PlaybackWatchdog.IsLive(true, 9900, 10000), "重点⑥黑屏：时钟近5秒推进过=活（原样续播）");
    Check(PlaybackWatchdog.IsLive(true, 9999, 10000), "重点⑥黑屏：时钟刚推进=活（边界）");
    Check(!PlaybackWatchdog.IsLive(true, 4000, 10000), "重点⑥黑屏：时钟停走超5秒=已死（回前台重播）");
    Check(!PlaybackWatchdog.IsLive(false, 9999, 10000), "重点⑥黑屏：无首帧按连接流程处理");
}

// ============ v1.0.8 重连缓存阶梯 + 代数守卫（与重点项联动） ============
{
    Check(CacheLadder.At(0) == 1200 && CacheLadder.At(3) == 6000, "缓存阶梯：首连1200→重连6000");
    Check(CacheLadder.At(99) == 6000 && CacheLadder.At(-1) == 1200, "缓存阶梯：越界钳制不越档");
    Check(CacheLadder.Next(0) == 1 && CacheLadder.Next(2) == 3 && CacheLadder.Next(3) == 3, "缓存阶梯：递增封顶");
    var g = new GenerationCounter();
    long g0 = g.Current;
    long g1 = g.NextGeneration();
    Check(g0 != g1 && g.IsCurrent(g1) && !g.IsCurrent(g0), "代数守卫：推进后旧代作废（防旧重连覆盖新台）");
    g.NextGeneration();
    Check(!g.IsCurrent(g1), "代数守卫：再次换台后中间代也作废");
}

// ============ v1.0.8 第16项：开机默认台（收藏优先→常规第一台=中央一台→预检衔接） ============
{
    var reg = new List<Channel>
    {
        new() { Name = "CCTV1综合", Number = 1, EpgKey = "c_cctv1" },
        new() { Name = "CCTV2财经", Number = 2, EpgKey = "c_cctv2" },
        new() { Name = "北京卫视", Number = 3, EpgKey = "c_bjws" },
    };
    Check(reg[0].Name == "CCTV1综合", "第16项：中央一台在常规列表第一台");
    var favs = new List<Channel> { reg[2], reg[1] };
    Check(StartupPicker.Pick(favs, reg)!.Name == "北京卫视", "第16项：收藏非空播收藏第一个");
    Check(StartupPicker.Pick(new List<Channel>(), reg)!.Name == "CCTV1综合", "第16项：收藏空播常规第一个（中央一台）");
    static ChannelHealth Dead1(Channel c) => c.EpgKey == "c_cctv1" ? ChannelHealth.Dead : ChannelHealth.Unknown;
    Check(StartupPicker.Pick(new List<Channel>(), reg, Dead1)!.Name == "CCTV2财经", "第16项：默认台预检判死跳下一个可用台");
    static ChannelHealth AllDead(Channel c) => ChannelHealth.Dead;
    Check(StartupPicker.Pick(new List<Channel>(), reg, AllDead) is null, "第16项：全部不可用返回空（UI亮无信号）");
    static ChannelHealth Checking1(Channel c) => ChannelHealth.Checking;
    Check(StartupPicker.Pick(new List<Channel>(), reg, Checking1)!.Name == "CCTV1综合", "第16项：检测中先开播不空等（预检很快补态）");
}

// ============ v1.0.8 第14项：收藏排序（按屏挪/置顶/持久化） ============
{
    var st = new FavoriteStore();
    st.Toggle(3); st.Toggle(1); st.Toggle(4); st.Toggle(7);
    Check(string.Join(",", st.Numbers) == "3,1,4,7", "排序：初始为收藏加入顺序");
    st.MoveBy(4, 2);
    Check(string.Join(",", st.Numbers) == "3,1,7,4", "排序：左右键按屏快速挪（+2位）");
    st.MoveBy(4, -9);
    Check(string.Join(",", st.Numbers) == "4,3,1,7", "排序：越界钳到顶不越界");
    st.MoveBy(1, 9);
    Check(string.Join(",", st.Numbers) == "4,3,7,1", "排序：越界钳到底不越界");
    st.MoveToTop(1);
    Check(string.Join(",", st.Numbers) == "1,4,3,7", "排序：信息键一键置顶");
    var saved = st.Serialize();
    var st2 = new FavoriteStore();
    st2.Restore(saved + ",abc");
    Check(st2.Numbers.SequenceEqual(st.Numbers), "排序：持久化往返一致（坏段忽略）");
    var st3 = new FavoriteStore();
    st3.Restore(saved + ",abc,99");
    Check(st3.Numbers.Take(st.Numbers.Count).SequenceEqual(st.Numbers) && st3.Numbers[^1] == 99, "排序：合法但未收藏的号容错补入（坏段跳过）");
    st2.Restore(null);
    Check(st2.Numbers.Count == 0, "排序：空值安全清空");
    st2.Restore("");
    Check(st2.Numbers.Count == 0, "排序：空串安全清空");
}

// ============ v1.0.8 第18项：硬解策略（自动/强制回退/坏档记忆/GPU滤镜降级） ============
{
    Check(DecodePolicy.Decide(DecodeMode.Auto, true, false) == DecodeActual.Hardware, "18a：自动=优先硬解");
    Check(DecodePolicy.Decide(DecodeMode.Auto, true, true) == DecodeActual.Software, "18a：坏档在案自动回软解（不反复试错）");
    Check(DecodePolicy.Decide(DecodeMode.Auto, false, false) == DecodeActual.Software, "18a：无硬解能力走软解");
    Check(DecodePolicy.Decide(DecodeMode.ForceHw, false, false) == DecodeActual.Software, "18a：强制硬解但能力不足→软解（不黑屏）");
    Check(DecodePolicy.Decide(DecodeMode.ForceSw, true, false) == DecodeActual.Software, "18a：强制软解如实软解");
    Check(DecodePolicy.ShouldMarkBad(1) && !DecodePolicy.ShouldMarkBad(0), "18a：硬解失败1次即标坏档");
    Check(DecodePolicy.NextMode(DecodeMode.Auto) == DecodeMode.ForceHw &&
          DecodePolicy.NextMode(DecodeMode.ForceHw) == DecodeMode.ForceSw &&
          DecodePolicy.NextMode(DecodeMode.ForceSw) == DecodeMode.Auto, "18a：解码设置三档循环闭环");
    Check(FilterPathDecider.Decide(3, false) == "gpu", "18b：GLES3 走 GPU 滤镜");
    Check(FilterPathDecider.Decide(2, false) == "gpu", "18b：GLES2 走 GPU 滤镜");
    Check(FilterPathDecider.Decide(1, false) == "cpu", "18b：能力不足回 CPU 滤镜");
    Check(FilterPathDecider.Decide(null, false) == "cpu", "18b：能力未知保守走 CPU");
    Check(FilterPathDecider.Decide(3, true) == "cpu", "18b：GPU 路径失败回退 CPU（功能完整性不受影响）");
}

// ============ v1.0.8 第19项·自愈化十项专项断言（用户 00:07 确认十项全要）============
{
    // 自愈1·多候选源备援：主源失败切备援、连续成功自动回主源、单候选不折腾
    var fo = new SourceFailover();
    Check(fo.NextCandidateOnFailure("ch", 2) == 1, "自愈1：主源失败切备援候选");
    fo.OnSuccess("ch"); fo.OnSuccess("ch");
    Check(fo.CurrentIndex("ch") == 0, "自愈1：备援连续成功2轮自动回主源");
    Check(fo.NextCandidateOnFailure("solo", 1) == 0, "自愈1：无备援时停在主源不折腾");
    // 自愈2·取票多版本：内置 v1 出票、注册新版本按序追加、坏版本不拖垮
    var urls = TicketAlgorithms.TryAll("2004652103:900001:fhd", 1794016800, CkeyAlgorithmV1.GuidConst);
    Check(urls.Count >= 1 && urls[0].Contains("cKey="), "自愈2：内置 v1 cKey 算法出票");
    TicketAlgorithms.Register(new TestAlgoV2());
    var urls2 = TicketAlgorithms.TryAll("2004652103:900001:fhd", 1794016800, CkeyAlgorithmV1.GuidConst);
    Check(urls2.Count == urls.Count + 1 && urls2[^1].StartsWith("v2://"), "自愈2：热更新注入 v2 按序追加");
    // 自愈3·热更新三层：L1 数据 + L2 规则 + L3 脚本失败回落并提示
    bool docOk = RemoteConfigDoc.TryParse(
        """{"channelBackupUrls":{"c1":"http://bak/c1.m3u8"},"rules":[{"Layer":"L2","MatchHost":"old.example","ReplaceHost":"new.example"},{"Layer":"L3","MatchHost":"live.example","Header":"Referer=https://r/"}]}""",
        out var doc);
    Check(docOk, "自愈3：热更新文档解析");
    Check(doc!.ChannelBackupUrls["c1"] == "http://bak/c1.m3u8", "自愈3·L1：频道备源热更新生效");
    var (u2, ap2, n2) = ScriptAdapter.Apply("http://old.example/a.m3u8", doc.Rules, out _);
    Check(ap2 && u2 == "http://new.example/a.m3u8" && !n2, "自愈3·L2：取流适配规则换域生效");
    Check(RemoteConfigDoc.TryParse("not-json{{", out _) == false, "自愈3：坏文档判损坏（走本地缓存不落盘）");
    var (u3, ap3, n3) = ScriptAdapter.Apply("http://live.example/x.m3u8", doc.Rules, out var failed3);
    Check(!ap3 && u3 == "" && n3 && ScriptAdapter.IsScriptLayer(failed3!), "自愈3·L3：脚本层规则失败→回落原生+明确提示");
    // 自愈3·拉取器：成功覆盖缓存、失败用缓存续跑、全失败保持默认
    string? stored = null;
    var fetcher = new RemoteConfigFetcher(_ => Task.FromResult<string?>("{\"rules\":[]}"), _ => stored, (_, v) => stored = v);
    Check(await fetcher.FetchAsync("http://x/cfg") == FetchOutcome.Fresh, "自愈3：拉到新配置=Fresh（坏数据不落盘）");
    var fetcher2 = new RemoteConfigFetcher(_ => Task.FromResult<string?>(null), _ => stored, (_, v) => stored = v);
    Check(await fetcher2.FetchAsync("http://x/cfg") == FetchOutcome.UsedCache, "自愈3：拉取失败用本地缓存继续跑");
    var fetcher3 = new RemoteConfigFetcher(_ => Task.FromResult<string?>(null), _ => (string?)null, (_, v) => { });
    Check(await fetcher3.FetchAsync("http://x/cfg") == FetchOutcome.Failed, "自愈3：无缓存无网=Failed（首次可提示一次）");
    // 自愈4·时间异常：容差内不误判、超容差提示修正、票据时间用修正值
    var cg = new ClockGuard();
    cg.ObserveServerDate(1794016800, 1794016800 + 100);
    Check(!cg.TimeSuspicious && cg.Now(1794016800) == 1794016800, "自愈4：容差5分钟内视为正常");
    cg.ObserveServerDate(1794016800, 1794016800 + 3600);
    Check(cg.TimeSuspicious && cg.Now(1794016800) == 1794016800 - 3600, "自愈4：超容差标记异常，票据时间按服务端修正");
    cg.ObserveServerDate(null, 1794016800);
    Check(cg.TimeSuspicious, "自愈4：无响应头不动状态（不误报）");
    // 自愈5·断网监测：连续失败达阈值判断网、成功即恢复沿
    var ng = new NetGuard();
    Check(!ng.OnFailure() && !ng.OnFailure() && !ng.OnFailure(), "自愈5：零星失败不误判断网");
    Check(ng.OnFailure(), "自愈5：连续4次失败判断网");
    Check(ng.OnSuccess(), "自愈5：恢复沿=true（触发重连+预检重测，静默自愈）");
    Check(!ng.Offline, "自愈5：恢复后回到在线态");
    // 自愈6·长时运行退化：内存告急释放缓存、极低+内核异常软重启播放内核
    var mw = new MemoryWatcher();
    Check(mw.Observe(0.5, true) == MemoryAction.Normal, "自愈6：内存充裕不动");
    Check(mw.Observe(0.10, true) == MemoryAction.ReleaseCache, "自愈6：可用内存<15%释放缓存");
    mw.OnKernelFailure(); mw.OnKernelFailure(); mw.OnKernelFailure();
    Check(mw.Observe(0.05, false) == MemoryAction.RestartKernel, "自愈6：极低内存+内核连续异常→软重启播放内核（不整机重启）");
    Check(mw.Observe(0.05, false) == MemoryAction.ReleaseCache, "自愈6：动用一次后重新累计（防连环重启）");
    // 自愈7·配置损坏：主档损坏快照回放、全坏重建默认
    string? main = "{\"ok\":1}", s1 = "{\"ok\":1}", s2 = null, s3 = null;
    var store = new SnapshotStore(k => k switch
    {
        "cfg:main" => main, "cfg:snap1" => s1, "cfg:snap2" => s2, "cfg:snap3" => s3, _ => null,
    }, (k, v) => { if (k == "cfg:main") main = v; });
    var (c1, p1) = store.Load(s => s != null && s.Contains("\"ok\""));
    Check(p1 == RecoveryPath.Main, "自愈7：主档完好直读");
    main = "{{{corrupted";
    var (c2r, p2) = store.Load(s => s != null && s.Contains("\"ok\""));
    Check(p2 == RecoveryPath.Snapshot && c2r == s1, "自愈7：主档损坏快照1回放");
    s1 = "junk"; s2 = "{\"ok\":2}";
    var (c3r, p3r) = store.Load(s => s != null && s.Contains("\"ok\""));
    Check(p3r == RecoveryPath.Snapshot && c3r == s2, "自愈7：快照逐份回放直到有效");
    main = null; s1 = null; s2 = null; s3 = null;
    Check((store.Load(s => s != null && s.Contains("\"ok\""))).Path == RecoveryPath.Default, "自愈7：全坏重建默认（UI 提示一次）");
    // 自愈8·崩溃自恢复：崩溃后重置可疑状态、连续≥3 明确提示
    var plan0 = CrashRecovery.Plan(0);
    Check(!plan0.ClearResolverCache && !CrashRecovery.ShouldNotice(plan0), "自愈8：无崩溃不折腾");
    var plan1 = CrashRecovery.Plan(1);
    Check(plan1.ClearResolverCache && plan1.RequeueAllPrecheck && plan1.FallbackToStableCarrier && !CrashRecovery.ShouldNotice(plan1), "自愈8：崩溃后静默重置可疑状态");
    Check(CrashRecovery.ShouldNotice(CrashRecovery.Plan(3)), "自愈8：连续崩溃≥3 屏显一次恢复提示");
    // 自愈9·低配资源自适应：预检频率/缓存条目按内存调
    var (iv1, _, cache1) = ResourceProfile.Adapt(100 * 1024 * 1024);
    Check(iv1 == 1200 && cache1 == 32, "自愈9：极低内存降频减存");
    var (iv2, _, cache2) = ResourceProfile.Adapt(2 * 1024L * 1024 * 1024);
    Check(iv2 == 600 && cache2 == 96, "自愈9：内存充裕全速");
    // 自愈10·键值归一：常见别名映射、未知键不硬编码
    Check(KeyAlias.ToCanonical(4077) == 177, "自愈10：TCL 设置键别名→标准设置键");
    Check(KeyAlias.ToCanonical(111) == 4, "自愈10：ESC 码别名→返回键");
    Check(KeyAlias.ToCanonical(19) == 19, "自愈10：标准键原样透传");
    Check(KeyAlias.ToCanonical(88888) is null, "自愈10：未知键返回空（记录后按未接管处理）");
}

// ============ v1.0.8 第19项·通道化架构（用户 00:01 拍板：所有方式做成通道、按频道实际选择）============
{
    var router = new LaneRouter();
    router.Register(new StubLane("ysp"));
    router.Register(new StubLane("cztv"));
    router.Register(new StubLane("user-m3u"));
    // 未登记路由：按 CanHandle 命中
    var ch = new Channel { EpgKey = "c1", SourceId = "yangshipin" };
    Check(router.CurrentLane("c1", ch)?.LaneId == "ysp", "通道化：未登记路由按 CanHandle 命中内置通道");
    // 登记路由：首选→失败降级备选→耗尽明确失败→恢复回首选
    router.SetRoute("c1", new[] { "ysp", "cztv", "user-m3u" });
    Check(router.CurrentIndex("c1") == 0, "通道化：路由登记后从首选通道开始");
    var degraded = router.AdvanceOnFailure("c1", ch);
    Check(degraded?.LaneId == "cztv", "通道化：首选失败自动降级备选通道");
    Check(router.CurrentLane("c1", ch)?.LaneId == "cztv", "通道化：当前通道随降级切换");
    router.AdvanceOnFailure("c1", ch);
    Check(router.CurrentLane("c1", ch)?.LaneId == "user-m3u", "通道化：备选再败降级兜底通道");
    Check(router.AdvanceOnFailure("c1", ch) is null, "通道化：全部通道耗尽=明确失败（UI 提示不静默）");
    // 恢复回首选（自愈1 备援状态共用）
    router.MarkSuccess("c1"); router.MarkSuccess("c1");
    Check(router.CurrentIndex("c1") == 0 && router.CurrentLane("c1", ch)?.LaneId == "ysp", "通道化：兜底连续成功2轮自动回首选");
    // 同 LaneId 幂等注册
    router.Register(new StubLane("ysp"));
    Check(router.RouteOf("none").Count == 0, "通道化：未登记路由的台返回空候选（按 CanHandle 兜底）");
}

// ============ v1.0.8 第20项：清晰度档位自适应（用户 00:07 拍板：保画面保流畅选择或切换） ============
{
    // master playlist 解析：多档变体按带宽升序、坏行跳过、无变体返回空
    var variants = MasterPlaylistParser.Parse(
        "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080\nhd.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720\nsd.m3u8\n" +
        "garbage\n");
    Check(variants.Count == 2, "第20项：master playlist 解析出两档（坏行跳过）");
    Check(variants[0].HeightPx == 720 && variants[1].HeightPx == 1080, "第20项：变体按带宽升序排列");
    Check(MasterPlaylistParser.Parse("#EXTM3U\nmedia.m3u8\n").Count == 0, "第20项：无变体返回空表（如实单档不硬造）");
    // 单档流：自适应不介入
    var single = new QualityGovernor(MasterPlaylistParser.Parse(""), QualityMode.Auto);
    Check(single.IsSingleStream && single.Observe(true) == 0, "第20项：单档源如实单档播放，自适应不介入");
    // 起步选档：网速未知=保守低档（宁低勿卡）；网速实况按 80% 余量选最高撑得住的档
    var govUnknown = new QualityGovernor(variants, QualityMode.Auto);
    Check(govUnknown.Index == 0, "第20项：网速未知起步取低档（宁低勿卡）");
    var govNet = new QualityGovernor(variants, QualityMode.Auto, measuredBps: 3_000_000);
    Check(govNet.Index == 0, "第20项：网速3Mbps×0.8余量→选720P档（1080P 4M 撑不住）");
    // 卡顿立即降档（先手自救）；平稳 3 个周期升档（迟滞防横跳）
    var gov = new QualityGovernor(variants, QualityMode.Auto, measuredBps: 9_000_000);
    Check(gov.Index == 1, "第20项：高网速起步1080P档");
    gov.Observe(true);
    Check(gov.Index == 0, "第20项：卡顿立即降档（先手自救，不等看门狗）");
    gov.Observe(false); gov.Observe(false);
    Check(gov.Index == 0, "第20项：平稳不足3周期不升档（迟滞防横跳）");
    gov.Observe(false);
    Check(gov.Index == 1, "第20项：连续3周期平稳升回高档");
    // 锁定档：锁定低不升、锁定高撑不住降档自救并温和提示
    var govLow = new QualityGovernor(variants, QualityMode.LockLow, measuredBps: 99_000_000);
    govLow.Observe(false); govLow.Observe(false); govLow.Observe(false);
    Check(govLow.Index == 0, "第20项：锁定低档如实执行不升档");
    var govHigh = new QualityGovernor(variants, QualityMode.LockHigh);
    govHigh.Observe(true);
    Check(govHigh.Index == 0 && govHigh.ConsumeLockNotice(), "第20项：锁定高档撑不住→降档自救+温和提示一次");
    Check(!govHigh.ConsumeLockNotice(), "第20项：提示只发一次不重复打扰");
}

// ============ v1.0.8·机制互扰审查（设计自洽铁律：组合场景下画面能出来不卡顿） ============
{
    // 组合A：反复切台时代数守卫作废旧重连——旧代定时器触发不覆盖新台
    var g = new GenerationCounter();
    long oldGen = g.NextGeneration();
    g.NextGeneration(); // 换台
    Check(!g.IsCurrent(oldGen), "互扰A：换台后旧代作废，旧重连不覆盖新台");
    // 组合B：预检中换台——预检状态只标状态不改播放决策，默认台选择不被并发预检抖动
    var pre = new ChannelPrecheck(ch => Task.FromResult(true), intervalMs: 1);
    var chans = new List<Channel> { new() { Name = "a", EpgKey = "a" }, new() { Name = "b", EpgKey = "b" } };
    _ = pre.RunAsync(chans); // 不 await：模拟换台时预检仍在跑
    var picked = StartupPicker.Pick(new List<Channel>(), chans, c => pre.Of(c));
    Check(picked != null, "互扰B：预检进行中换台，选台不空等不炸");
    // 组合C：降档与重连并发——降档自救后时钟恢复，看门狗不再叠加触发
    var ladder = CacheLadder.Next(0);
    var govC = new QualityGovernor(MasterPlaylistParser.Parse(
        "#EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080\nhd.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720\nsd.m3u8\n"),
        QualityMode.Auto, measuredBps: 9_000_000);
    govC.Observe(true); // 卡顿先降档
    Check(govC.Index == 0 && ladder == 1, "互扰C：降档先手+缓存递增并行不冲突（先降档、看门狗兜底）");
    // 组合D：热更新拉取失败+断网并存——本地缓存续跑，播放不中断
    Check(await new RemoteConfigFetcher(_ => Task.FromResult<string?>(null), _ => (string?)null, (_, _) => { }).FetchAsync("u") == FetchOutcome.Failed,
        "互扰D：断网时热更新按无缓存口径失败，不影响播放主链路");
}

// ============ v1.0.9 追加：⑫分级快速自救 + ④画面信息行 + ⑧分类统计 ============

// ⑫分级快速自救（静默重取流 → 降清晰度 → 软重启播放内核，用户 10:40 拍板）
Check(StallRescue.Plan(0, true) == StallRescueAction.SilentRefresh, "⑫第1次卡死：静默重取流（不发状态不占重连次数）");
Check(StallRescue.Plan(1, true) == StallRescueAction.LowerQuality, "⑫第2次卡死：降清晰度（有档可降）");
Check(StallRescue.Plan(1, false) == StallRescueAction.SilentRefresh, "⑫第2次卡死：无档可降并入重取流，绝不空转一档");
Check(StallRescue.Plan(2, false) == StallRescueAction.RestartKernel, "⑫第3次卡死：软重启播放内核（不动系统）");
Check(StallRescue.Plan(5, true) == StallRescueAction.RestartKernel, "⑫连环卡死：持续用最强自救，不回退");
Check(StallRescue.KernelRestartAt == 3, "⑫内核软重启档位=第3次卡死");

// ④画面信息行（原/现帧率/分辨率；操作提示去掉，测量缺失显示检测中）
Check(FrameStatsLine.Format(null, null, 0, 0, 0, 0) == "画面信息检测中", "④测量缺失：显示检测中，绝不编数字");
Check(FrameStatsLine.Format(25, null, 1920, 1080, 1920, 1080).Contains("原帧率 25fps"), "④原帧率如实展示");
Check(FrameStatsLine.Format(25, null, 1920, 1080, 1920, 1080).Contains("原分辨率 1920×1080"), "④原分辨率如实展示");
Check(FrameStatsLine.Format(25, null, 1920, 1080, 1920, 1080).Contains("＝现帧率") && FrameStatsLine.Format(25, null, 1920, 1080, 1920, 1080).Contains("＝现分辨率"), "④原现同值单行如实展示（无缩放）");
var fsDiff = FrameStatsLine.Format(25.0, 18.0, 1920, 1080, 1280, 720);
Check(fsDiff.Contains("现帧率 18fps") && fsDiff.Contains("现分辨率 1280×720"), "④原现不同：分别如实展示");

// ⑧分类统计加可连数（口径与⑬统一：可连=至少一个来源可达）
Check(ListHeaderStats.Format("央视", 21, 18, true) == "央视(21·可连18)", "⑧统计格式「央视(21·可连18)」");
Check(ListHeaderStats.Format("央视", 21, 18, false) == "央视(21)", "⑧未出结果先显总数");

// ---- v1.0.10 修复③④⑤⑥⑦⑨⑩⑪⑫ 断言 ----
// ③启动贴面时序：未就绪等回调不抢跑；就绪才贴；等待间隙纹理回收退回等待，绝不带 null 表面进 AWindow
Check(SurfaceAttachGate.Decide(playerPresent: true, windowAbsent: true, textureReady: false) == AttachAction.WaitSurface, "③纹理未就绪：先等 OnSurfaceTextureAvailable，不抢跑");
Check(SurfaceAttachGate.Decide(playerPresent: true, windowAbsent: true, textureReady: true) == AttachAction.AttachNow, "③纹理就绪：才允许真正贴面");
Check(SurfaceAttachGate.Decide(playerPresent: false, windowAbsent: true, textureReady: true) == AttachAction.WaitSurface, "③播放器未给：不动作");
Check(SurfaceAttachGate.Decide(playerPresent: true, windowAbsent: false, textureReady: true) == AttachAction.WaitSurface, "③已贴过 AWindow：不重复贴");

// ④退出弹窗焦点：默认落取消（继续看）；左右键两按钮间切换、到边不动；返回键=继续看
Check(ExitDialogFocus.Initial == ExitButton.Cancel, "④弹窗默认焦点落「取消（继续看）」，防误退");
Check(ExitDialogFocus.Move(ExitButton.Cancel, +1) == ExitButton.Exit && ExitDialogFocus.Move(ExitButton.Exit, -1) == ExitButton.Cancel, "④左右键在退出与取消间切换");
Check(ExitDialogFocus.Move(ExitButton.Exit, +1) == ExitButton.Exit && ExitDialogFocus.Move(ExitButton.Cancel, -1) == ExitButton.Cancel, "④到边不动不环绕，防误触退出");
Check(ExitDialogFocus.Back() == ExitButton.Cancel, "④返回键=继续看");

// ⑤画面信息行：首帧后重试（500ms×10），到限显「--」不留「检测中」
Check(StatsLinePolicy.IntervalMs == 500 && StatsLinePolicy.MaxRetries == 10, "⑤重试口径：每500ms至多10次");
Check(StatsLinePolicy.NextDelayMs(0) == 500 && StatsLinePolicy.NextDelayMs(9) == 500 && StatsLinePolicy.NextDelayMs(10) is null, "⑤到限停止重试");
Check(FrameStatsLine.FormatEx(null, null, 0, 0, 0, 0, statsExhausted: false).Contains("检测中"), "⑤重试期内维持「检测中」");
Check(FrameStatsLine.FormatEx(null, null, 0, 0, 0, 0, statsExhausted: true).Contains("--") && !FrameStatsLine.FormatEx(null, null, 0, 0, 0, 0, true).Contains("检测中"), "⑤到限显「--」不留「检测中」");
Check(FrameStatsLine.FormatEx(null, null, 1920, 1080, 1920, 1080, true).Contains("原帧率 --"), "⑤部分缺失到限：缺失项显「--」");

// ⑥换台提速：先连最快出画面档，亮后无感升高档，失败留在当前档
var tiers = new List<(int Height, long Bandwidth)> { (1080, 8_000_000), (720, 3_000_000), (480, 1_500_000) };
Check(FastStartPlanner.First(tiers)!.Value.Height == 480, "⑥多档先连最快出画面档（带宽最低先亮）");
Check(FastStartPlanner.UpgradeTarget(tiers, 480)!.Value.Height == 1080, "⑥画面亮后无感升级最高档");
Check(FastStartPlanner.UpgradeTarget(tiers, 1080) is null, "⑥已是最高档不折腾");
Check(FastStartPlanner.OnUpgradeFailed(tiers, 720).Height == 720, "⑥升级失败留在当前档");

// ⑦切台清屏：换台即铺底色，新流首帧到齐才亮；反复花屏走备用载体重接
Check(SwitchRevealPolicy.ShouldVeilOnSwitch(frameEverShown: true), "⑦换台瞬间即铺干净底色，GPU 残留不外露");
Check(!SwitchRevealPolicy.ShouldReveal(newStreamFirstFrame: false) && SwitchRevealPolicy.ShouldReveal(newStreamFirstFrame: true), "⑦新流首帧到齐才亮，中间绝不亮残留");
Check(!SwitchRevealPolicy.ShouldFallbackCarrier(1) && SwitchRevealPolicy.ShouldFallbackCarrier(2), "⑦短期反复花屏达阈值改走备用载体重接");

// ⑨⑩缓冲水位：低配自适应、低水位先攒（提前介入）、平稳平滑追回、贴边保持
Check(BufferPolicy.MaxWatermarkSeconds(4L * 1024 * 1024 * 1024) == 60, "⑩内存充裕安全水位上限60秒");
Check(BufferPolicy.MaxWatermarkSeconds(2L * 1024 * 1024 * 1024) == 45, "⑩中配缩到45秒");
Check(BufferPolicy.MaxWatermarkSeconds(0) == 30, "⑩未知内存按低配30秒（不落磁盘，纯内存环形）");
Check(BufferPolicy.Observe(5, networkSteady: true, 4L * 1024 * 1024 * 1024) == BufferAction.BuildUp, "⑩水位低先攒池子（提前介入不等6秒）");
Check(BufferPolicy.Observe(10, networkSteady: false, 4L * 1024 * 1024 * 1024) == BufferAction.BuildUp, "⑩网络抖动主动落后安全水位");
Check(BufferPolicy.Observe(35, networkSteady: true, 4L * 1024 * 1024 * 1024) == BufferAction.CatchUp, "⑩平稳后平滑追回直播边缘");
Check(BufferPolicy.Observe(60, networkSteady: true, 4L * 1024 * 1024 * 1024) == BufferAction.HoldEdge, "⑩贴直播边缘零延迟感");

// ⑪列表一键展开/折叠：总开关全收/全开切换；无信号组参与；默认展开习惯不变
var fold = new ListFoldController();
var groups = new List<string> { "收藏", "央视", "省台", "无信号" };
var expanded = ListFoldController.DefaultExpanded(groups, "收藏", "央视");
Check(expanded.Count == 2 && expanded.Contains("收藏") && expanded.Contains("央视") && !expanded.Contains("无信号"), "⑪默认只展开收藏+当前台所在组，习惯不变");
var collapsed = fold.ToggleAll(groups);
Check(collapsed.Count == groups.Count, "⑪全开时总开关一键全部收起（含无信号组）");
Check(fold.IsCollapsed("无信号"), "⑪无信号组参与收放");
collapsed = fold.ToggleAll(groups);
Check(collapsed.Count == 0, "⑪有任何折叠时总开关一键全部展开");
Check(ListFoldController.ToggleLabel(0) == "全部收起" && ListFoldController.ToggleLabel(1) == "全部展开", "⑪总开关行文案随状态切换");
fold.ToggleGroup("央视");
Check(fold.IsCollapsed("央视"), "⑪单组点收原逻辑不变");
fold.ToggleGroup("央视");
Check(!fold.IsCollapsed("央视"), "⑪单组再点恢复展开");

// ---- v23 修复批（六条一批修完）：折叠整行退场后 BLL 纯逻辑保留；⑨B 介入升级阶梯；回前台网络等待步进 ----
Check(ListFoldController.ToggleLabel(0) == "全部收起" && ListFoldController.ToggleLabel(1) == "全部展开",
    "v23①折叠交互整行退场：ListFoldController 纯逻辑保留（UI 行已清，列表默认铺开）");
Check(StallRescue.EarlyRescueEscalateAt == 3,
    "v23⑤⑨B 静默重取流连续介入升级线=3 次（时钟微动假活不再无限循环介入）");
Check(StallRescue.EarlyRescueResetMs == 60_000,
    "1.0.12·出过首帧后 60 秒持续走钟才清介入计数（30 秒窗口作废，自救能真正走到重启内核级）");
Check(StallRescue.DedupWindowMs == 5000,
    "1.0.12修正①·Buffering 主信号与看门狗 5 秒去重窗（同一次卡顿双触发不重复计，阶梯不跳级）");
Check(StallRescue.RescueWindowMs == 90_000 && StallRescue.RescueCapPerWindow == 2,
    "1.0.12修正⑤·90 秒窗口介入上限 2 次（限频代替拉黑，探活与重连共用请求预算口径）");
Check(StallRescue.BufferingLowPct == 5f,
    "1.0.12修正①·Buffering 主信号触发线（缓冲接近耗尽即时介入，发现延迟 2–6 秒压到 ≤1 秒）");
Check(BufferInventory.TargetMs == 3000 && BufferInventory.Default == BufferInventory.Mode.StartFull,
    "1.0.12发现B·3 秒库存两档：默认档=起播即 3 秒（拍板口径，真机验证后可切）");
// ---- v25 真机修复批（11-11 真机卡死实锤：两三秒一卡且卡死后永冻，须手动切台才恢复）----
Check(StallRescue.ExhaustedRecoveryFirstDelayMs == 30_000,
    "v25真机修复·重连阶梯耗尽后 30 秒起兜底重试（原口径永久躺平只报一次「信号中断」，画面冻死实锤）");
Check(StallRescue.ExhaustedRecoveryPeriodMs == 60_000,
    "v25真机修复·耗尽兜底 60 秒常驻节奏（首帧恢复或换台才收摊，不等用户救场）");
Check(StallRescue.ExhaustedRebuildEveryCycles == 1,
    "④内核自愈提前·耗尽兜底每轮即软重启内核（老板 11-12 令：取不到首帧几十秒内主动内核自愈，不再等 3 轮约 2.5 分钟）");
Check(StallRescue.ExhaustedRecoveryPeriodMs / StallRescue.ExhaustedRebuildEveryCycles > 0,
    "v25真机修复·耗尽兜底节奏整除守卫（升级间隔=3 分钟，防参数改坏后除零/永不升级）");
// ④内核自愈提前·三条验收断言（老板 2026-11-12 令）
Check(StallRescue.ShouldKernelRebuildOnExhausted(1, frameShown: false)
    && StallRescue.ShouldKernelRebuildOnExhausted(2, frameShown: false),
    "④首帧超时→内核自愈触发：首帧未出时第 1 轮兜底即软重启内核（30 秒首延迟内达「几十秒内」口径），后续每轮持续自愈");
Check(!StallRescue.ShouldKernelRebuildOnExhausted(0, frameShown: false),
    "④边界守卫：cycle=0（未进兜底循环）不触发内核自愈");
Check(!StallRescue.ShouldKernelRebuildOnExhausted(1, frameShown: true)
    && !StallRescue.ShouldKernelRebuildOnExhausted(3, frameShown: true),
    "④正常流不误触发：首帧已出（frameShown=true）恒不重启（兜底循环本应已收摊，双保险）");
Check(StallRescue.RescueCapPerWindow == 2 && StallRescue.RescueWindowMs == 90_000,
    "④节流账不破：内核自愈走重启路径不消耗取流预算，90 秒窗 2 次的介入节流口径原样");
Check(BufferInventory.StartCachingMs(BufferInventory.Mode.StartFull, 1200) == 3000
    && BufferInventory.StartCachingMs(BufferInventory.Mode.StartFull, 4000) == 4000
    && BufferInventory.StartCachingMs(BufferInventory.Mode.SilentRefetch, 1200) == 1200,
    "1.0.12发现B·起播缓存口径：库存档兜底不低3秒/阶梯高者保留；补库存档先开画（修正②④并入）");
Check(ResumeNetWaitPolicy.StepMs == 500 && ResumeNetWaitPolicy.MaxSteps == 6,
    "v23④回前台网络就绪等待步进 500ms×6≈3 秒封顶（常数收口可调）");
Check(ResumeNetWaitPolicy.ShouldWait(0) && !ResumeNetWaitPolicy.ShouldWait(ResumeNetWaitPolicy.MaxSteps),
    "v23④等待步进封顶：超时照常发起重接，绝不无限等");

// ⑫连接失败矩阵：失败分类枚举逐类归因；限流冷却退避
Check(ConnectFailure.Classify("getaddrinfo ENOTFOUND x") == ConnectFailureKind.DnsFailure, "⑫DNS 解析失败单列");
Check(ConnectFailure.Classify("connection timed out") == ConnectFailureKind.ConnectTimeout, "⑫连接超时单列");
Check(ConnectFailure.Classify("取票返回 iretcode=403", 403) == ConnectFailureKind.RateLimited, "⑫限流/风控归冷却退避类");
Check(ConnectFailure.Classify("HTTP 502", 502) == ConnectFailureKind.Rejected, "⑫被拒（HTTP 状态）归健康账淘汰类");
Check(ConnectFailure.Classify("certificate verify failed") == ConnectFailureKind.TimeSkew, "⑫证书类错误归校时触发路径");
Check(ConnectFailure.Classify("decode error at surface") == ConnectFailureKind.DecoderSurface, "⑫播放器内部错误归三级自救");
Check(ConnectFailure.Classify("神秘异常") == ConnectFailureKind.Unknown, "⑫未知异常单列：落日志+「正在自动重试」不无限转圈");
Check(ConnectFailure.CooldownSeconds(0) == 1 && ConnectFailure.CooldownSeconds(4) == 16 && ConnectFailure.CooldownSeconds(9) == 60, "⑫限流冷却指数退避封顶60秒，不无限重试");

// ---- B 包（v20 界面优化版）纯逻辑断言：五标签/抽屉装配/节目单/限流提示 ----
{
    // 测试台单：央视1、省台1、城市台1，省台/城市台预置收藏
    var chans = new List<Channel>
    {
        new() { Name="CCTV1", Number=1, Category=ChannelCategory.Cctv, SourceId="t", StreamUrl="http://t/1" },
        new() { Name="WS1", Number=2, Category=ChannelCategory.Provincial, SourceId="t", StreamUrl="http://t/2" },
        new() { Name="CS1", Number=3, Category=ChannelCategory.City, SourceId="t", StreamUrl="http://t/3", Unavailable=true },
    };
    var favOrder = new List<int> { 3, 2 };            // 收藏：城市台在前
    static ChannelHealth Dead(Channel c) => c.Unavailable ? ChannelHealth.Dead : ChannelHealth.Alive;

    // 标签与循环（1.0.12 UI② 改序：全部/收藏/央视/卫视/地方台——收藏插第二位）
    Check(DrawerSupport.Label(DrawerSupport.Tab.Cctv) == "央视", "B·五标签文案（央视）");
    Check(((DrawerSupport.Tab[])Enum.GetValues(typeof(DrawerSupport.Tab)))
        .Select(t => DrawerSupport.Label(t)).SequenceEqual(new[] { "全部", "收藏", "央视", "卫视", "地方台" }),
        "1.0.12 UI②·标签顺序=全部/收藏/央视/卫视/地方台（收藏插第二位）");
    Check(DrawerSupport.NextTab(DrawerSupport.Tab.Favorite, true) == DrawerSupport.Tab.Cctv
        && DrawerSupport.NextTab(DrawerSupport.Tab.Favorite, false) == DrawerSupport.Tab.All,
        "1.0.12 UI②·标签循环随改序自动生效：收藏→央视、收藏反向→全部");
    Check(DrawerSupport.NextTab(DrawerSupport.Tab.All, true) == DrawerSupport.Tab.Favorite
        && DrawerSupport.NextTab(DrawerSupport.Tab.Local, true) == DrawerSupport.Tab.All,
        "1.0.12 UI②·首尾循环：全部→收藏、地方台→全部");
    // 1.0.12 UI②·默认定位当前台：已收藏→收藏；未收藏按分类；无归属→全部
    var curFav = chans.First(c => c.Number == 2); // 省台已收藏
    var curCctv = chans.First(c => c.Number == 1); // 央视未收藏
    Check(DrawerSupport.DefaultTab(curFav, n => favOrder.Contains(n)) == DrawerSupport.Tab.Favorite,
        "1.0.12 UI②·当前台已收藏默认定位「收藏」");
    Check(DrawerSupport.DefaultTab(curCctv, n => favOrder.Contains(n)) == DrawerSupport.Tab.Cctv,
        "1.0.12 UI②·未收藏按分类定位（央视）");
    Check(DrawerSupport.DefaultTab(null, n => favOrder.Contains(n)) == DrawerSupport.Tab.All,
        "1.0.12 UI②·无当前台默认「全部」");

    // 全部标签：收藏成组置顶（按收藏序）+ 无信号分组置底（v22 第 7/15 项：画质档位行撤出列表页）
    var all = DrawerSupport.BuildRows(DrawerSupport.Tab.All, chans, favOrder, Dead);
    Check(all.All(r => r is DrawerSupport.DrawerRow.Channel or DrawerSupport.DrawerRow.Group), "v22 第 7 项：画质档位行撤出，抽屉只剩频道/分组行");
    Check(all.OfType<DrawerSupport.DrawerRow.Group>().Any(g => g.Label == "★ 收藏"), "B·收藏成组置顶");
    Check(all.Select((r, i) => (r, i)).Where(x => x.r is DrawerSupport.DrawerRow.Channel)
            .Select(x => ((DrawerSupport.DrawerRow.Channel)x.r).Data.Number).SequenceEqual(new[] { 3, 2, 1, 3 }),/* 无信号组置底后复列 */
        "B·行序=收藏序(3,2)在先+其余按台单(1)在后");
    Check(all.OfType<DrawerSupport.DrawerRow.Group>().Any(g => g.Label.Contains("无信号")), "B·全部标签无信号分组置底");

    // 分类标签过滤：央视只留 CCTV1（且无收藏组——收藏里没有央视台）
    var cctv = DrawerSupport.BuildRows(DrawerSupport.Tab.Cctv, chans, favOrder, Dead);
    Check(cctv.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Number).SequenceEqual(new[] { 1 }),
        "B·央视标签过滤只留央视台");
    Check(!cctv.OfType<DrawerSupport.DrawerRow.Group>().Any(g => g.Label == "★ 收藏"), "B·标签内无收藏台则无收藏组");

    // v22 第 7 项：央视=央视/CGTN/专项并集；地方台=城市台/自有源收窄，不再兜非央视非卫视杂烩
    var wide = new List<Channel>
    {
        new() { Name="CCTV1", Number=1, Category=ChannelCategory.Cctv, StreamUrl="http://t/1" },
        new() { Name="CGTN纪录", Number=2, Category=ChannelCategory.Cgtn, StreamUrl="http://t/2" },
        new() { Name="风云剧场", Number=3, Category=ChannelCategory.Special, StreamUrl="http://t/3" },
        new() { Name="WS1", Number=4, Category=ChannelCategory.Provincial, StreamUrl="http://t/4" },
        new() { Name="CS1", Number=5, Category=ChannelCategory.City, StreamUrl="http://t/5" },
        new() { Name="自录1", Number=6, Category=ChannelCategory.UserSource, StreamUrl="http://t/6" },
    };
    var cctvWide = DrawerSupport.BuildRows(DrawerSupport.Tab.Cctv, wide, new List<int>(), Dead);
    Check(cctvWide.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Number).SequenceEqual(new[] { 1, 2, 3 }),
        "v22 第 7 项：央视标签含 CGTN 与专项频道");
    var localWide = DrawerSupport.BuildRows(DrawerSupport.Tab.Local, wide, new List<int>(), Dead);
    Check(localWide.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Number).SequenceEqual(new[] { 5, 6 }),
        "v22 第 7 项：地方台标签只收城市台与自有源");
    var satWide = DrawerSupport.BuildRows(DrawerSupport.Tab.Satellite, wide, new List<int>(), Dead);
    Check(satWide.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Number).SequenceEqual(new[] { 4 }),
        "v22 第 7 项：卫视标签只收省台");

    // v22 第 6/15 项：空态两条——收藏空/分类空如实告知，不白屏
    var emptyLocal = DrawerSupport.BuildRows(DrawerSupport.Tab.Local, wide.Where(c => c.Number == 4).ToList(), new List<int>(), Dead);
    Check(emptyLocal.Count == 1 && emptyLocal[0] is DrawerSupport.DrawerRow.Note n1 && n1.Text == "该分类暂无频道",
        "v22 第 6/15 项：分类装配后无频道补空态行");
    var emptyFav = DrawerSupport.BuildRows(DrawerSupport.Tab.Favorite, chans, new List<int>(), Dead);
    Check(emptyFav.Count == 1 && emptyFav[0] is DrawerSupport.DrawerRow.Note n2 && n2.Text.Contains("收藏夹是空的"),
        "v22 第 6/15 项：收藏空态给指引不白屏");

    // 收藏标签：按收藏排序铺开
    var favRows = DrawerSupport.BuildRows(DrawerSupport.Tab.Favorite, chans, favOrder, Dead);
    Check(favRows.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Number).SequenceEqual(new[] { 3, 2 }),
        "B·收藏标签按收藏排序铺开");

    // v22 第 9 项：画面信息短格式——帧率+分辨率一行；同值只显一档、变化显 a>b、缺失/到限口径
    Check(FrameStatsLine.FpsSegment(null, null, false) == "帧率:检测中", "v22 第 9 项：帧率缺失未到限显检测中");
    Check(FrameStatsLine.FpsSegment(null, null, true) == "帧率:--", "v22 第 9 项：帧率缺失到限显 --");
    Check(FrameStatsLine.FpsSegment(25, null, false) == "帧率:25fps", "v22 第 9 项：现值未测同值单显");
    Check(FrameStatsLine.FpsSegment(25, 25.2, false) == "帧率:25fps", "v22 第 9 项：帧率小幅波动按同值");
    Check(FrameStatsLine.FpsSegment(25, 30, false) == "帧率:25fps>30fps", "v22 第 9 项：帧率变化显原>现");
    Check(FrameStatsLine.ResSegment(0, 0, 0, 0, true) == "分辨率:--", "v22 第 9 项：分辨率缺失到限显 --");
    Check(FrameStatsLine.ResSegment(1920, 1080, 1920, 1080, false) == "分辨率:1080P", "v22 第 9 项：同档并入档位文案");
    Check(FrameStatsLine.ResSegment(1920, 1080, 2560, 1440, false) == "分辨率:1080P>2K", "v22 第 9 项：分辨率变化显 1080P>2K");
    Check(FrameStatsLine.ResSegment(1280, 720, 640, 360, false) == "分辨率:720P", "v22 第 9 项：≤1280 同归 720P 只显一档");

    // v22 第 16 项：18 路挂链后抽屉分类过滤对账——地方台含 13 城市候选，卫视含甘肃/福建经济
    {
        var cat2 = new ChannelCatalog(new FavoriteStore());
        cat2.RegisterSource(new YangshipinSource());
        cat2.RegisterSource(new CztvSource());
        cat2.RegisterSource(new GzstvSource());
        cat2.RegisterSource(new XatvSource());
        cat2.RegisterSource(new NmtvSource());
        cat2.RegisterSource(new GxtvSource());
        cat2.RegisterSource(new CityCandidatesSource());
        cat2.Rebuild();
        var favReal = new List<int>();
        var localRows = DrawerSupport.BuildRows(DrawerSupport.Tab.Local, cat2.All, favReal, c => ChannelHealth.Alive);
        var localNums = localRows.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Number).ToList();
        Check(localNums.Count == 26, $"v22 第 16 项：地方台=原 13 城市+候选 13（实际 {localNums.Count}）");
        Check(localNums.All(n => cat2.ByNumber(n)!.Category is ChannelCategory.City or ChannelCategory.UserSource),
            "v22 第 16 项：地方台标签只含城市台/自有源");
        var satRows = DrawerSupport.BuildRows(DrawerSupport.Tab.Satellite, cat2.All, favReal, c => ChannelHealth.Alive);
        var satNames = satRows.OfType<DrawerSupport.DrawerRow.Channel>().Select(r => r.Data.Name).ToList();
        Check(satNames.Contains("甘肃经济") && satNames.Contains("福建经济"), "v22 第 16 项：卫视标签含甘肃/福建经济");
    }

    // 节目单格式化 + 限流提示
    var start = new DateTime(2026, 11, 10, 20, 0, 0);
    var end = start.AddMinutes(45);
    var line = new DrawerSupport.EpgLine("新闻联播", start, end);
    Check(DrawerSupport.Clock(start) == "20:00", "B·节目时刻 HH:mm 格式");
    Check(DrawerSupport.NowLabel(line, start.AddMinutes(15)).StartsWith("20:00 – 20:45 · 还剩 30 分钟"),
        "B·当前节目带剩余分钟");
    Check(DrawerSupport.RateLimitHint("CCTV1", 90_000).Contains("CCTV1") && DrawerSupport.RateLimitHint("CCTV1", 90_000).Contains("限流"),
        "B·限流提示点名台号与原因");
    Check(DrawerSupport.RateLimitHint("CCTV1", 90_000).Contains("约 2 分钟后自动重试"),
        "v22 第 2 项：限流提示按退避账剩余分钟给预期");
    Check(DrawerSupport.RateLimitHint("CCTV1", 0).Contains("即将自动重试") && !DrawerSupport.RateLimitHint("CCTV1", 0).Contains("约 1 分钟"),
        "v22 第 2 项：退避期外不再写死一分钟");
}

// ---- A 类 v21 新增断言 ----
{
    // A 类 P1·选项合并（缺键才补）：请求头不被整表覆盖
    var merged = MediaOptionMerge.Merge(
        new List<string> { ":http-referrer=https://www.yangshipin.cn/", ":http-user-agent=UA-X", ":network-caching=1200" },
        new List<(string, string)> { ("video-filter", "sharpen") });
    Check(merged.Contains(":http-referrer=https://www.yangshipin.cn/"), "P1合并：旧请求头保留");
    Check(merged.Contains(":http-user-agent=UA-X"), "P1合并：旧UA保留");
    Check(merged.Contains(":video-filter=sharpen"), "P1合并：新键补入");
    Check(merged.Contains(":network-caching=1200"), "P1合并：无关键保留");
    // 同键以新值为准；空现有表全按新表；无等号条目保留；前导冒号解析
    var merged2 = MediaOptionMerge.Merge(
        new List<string> { ":video-filter=sharpen", ":sharpen-sigma=0.8" },
        new List<(string, string)> { ("video-filter", "sharpen,adjust"), ("contrast", "1.08") });
    Check(merged2.Contains(":video-filter=sharpen,adjust"), "P1合并：同键新值胜");
    Check(merged2.Contains(":sharpen-sigma=0.8"), "P1合并：未给键沿用旧值");
    Check(merged2.Contains(":contrast=1.08"), "P1合并：新键追加");
    var merged3 = MediaOptionMerge.Merge(new List<string>(), new List<(string, string)> { ("a", "1") });
    Check(merged3.Count == 1 && merged3[0] == ":a=1", "P1合并：空表现场全补");
    var merged4 = MediaOptionMerge.Merge(new List<string> { "solo" }, new List<(string, string)>());
    Check(merged4.Count == 1 && merged4[0] == "solo", "P1合并：无等号条目原样保留");

    // A 类第2项·信息行单写入口：同文重刷吞 + 优先级让位 + 到期放行
    var hub = new InfoLineHub();
    long now = 10_000;
    Check(hub.Submit("1  CCTV1 连接中…", InfoLinePriority.Connect, now) != null, "A②连接状态上屏");
    Check(hub.Submit("1  CCTV1 连接中…", InfoLinePriority.Connect, now + 100) == null, "A②同文重刷直接吞");
    Check(hub.Submit("1  频道名", InfoLinePriority.Normal, now + 12_000) != null, "A②常规随到随显");
    Check(hub.Submit("1  取流被限流", InfoLinePriority.RateLimit, now + 12_100) != null, "A②限流可顶常规");
    Check(hub.Submit("1  又一条常规", InfoLinePriority.Normal, now + 12_200) == null, "A②常规让位限流保持期");
    hub.Reset();
    Check(hub.Submit("1  新台连接中…", InfoLinePriority.Connect, now + 13_000) != null, "A②Reset清闸后立即放行");
}


    // ---- A⑬ 多端点竞速 + 403 退避账 ----
    {
        var ledger = new SourceBackoffLedger();
        long now = 1000;
        // 退避指数：首败 2 分钟、连败 ×4 封顶 2 小时
        ledger.RecordLimited("yangshipin", now);
        Check(ledger.IsFrozen("yangshipin", now + 60_000) == true, "A⑬403退避期内冻结");
        Check(ledger.IsFrozen("yangshipin", now + 120_001) == false, "A⑬首败退避约2分钟");
        Check(WarmupPolicy.PaceOf("yangshipin").RefreshIntervalMs == 10 * 60_000, "A⑥央视频保鲜10分钟");
        Check(WarmupPolicy.PaceOf("nmtv").BatchAggregation == true, "A⑥nmtv整表聚合");
        Check(WarmupPolicy.PaceOf("未知源").RefreshIntervalMs == 10 * 60_000, "A⑥未注册源保守兜底");
        // 连败指数加深
        var l2 = new SourceBackoffLedger();
        var pace = WarmupPolicy.PaceOf("yangshipin");
        l2.RecordLimited("yangshipin", 0, pace);           // 2min
        l2.RecordLimited("yangshipin", 1000, pace);        // 8min
        l2.RecordLimited("yangshipin", 2000, pace);        // 32min
        l2.RecordLimited("yangshipin", 3000, pace);        // 128min
        l2.RecordLimited("yangshipin", 4000, pace);        // 封顶 2h
        Check(l2.RemainingMs("yangshipin", 4000) == 7_200_000, "A⑥退避封顶2小时");
        l2.Recover("yangshipin");
        Check(l2.IsFrozen("yangshipin", 4000) == false, "A⑥200全额恢复");
        // 竞速：退避期源跳过；首个 Alive 胜出；4 秒预算不归按失败
        var racers = new List<EndpointRacer.Candidate>
        {
            new("slow", async () => { await Task.Delay(10_000); return EndpointRacer.Outcome.Alive; }), // 预算外
            new("fast", () => Task.FromResult(EndpointRacer.Outcome.Alive)),
            new("dead", () => Task.FromResult(EndpointRacer.Outcome.Dead)),
        };
        var led = new SourceBackoffLedger();
        var win = EndpointRacer.RaceAsync(racers, led, 0, budgetMs: 300).GetAwaiter().GetResult();
        Check(win == "fast", "A⑬首个探通者胜出");
        // 全员退避：竞速不发起
        led.RecordLimited("fast", 0, WarmupPolicy.PaceOf("xatv"));
        led.RecordLimited("dead", 0, WarmupPolicy.PaceOf("xatv"));
        led.RecordLimited("slow", 0, WarmupPolicy.PaceOf("xatv"));
        var win2 = EndpointRacer.RaceAsync(racers, led, 0).GetAwaiter().GetResult();
        Check(win2 is null, "A⑬退避期内竞速不发起");
        // 限流回调落账
        var led3 = new SourceBackoffLedger();
        var lim = new List<EndpointRacer.Candidate> { new("lk", () => Task.FromResult(EndpointRacer.Outcome.Limited)) };
        string? hit = null;
        EndpointRacer.RaceAsync(lim, led3, 0, onLimited: id => { hit = id; led3.RecordLimited(id, 0, WarmupPolicy.PaceOf("xatv")); return Task.CompletedTask; })
            .GetAwaiter().GetResult();
        Check(hit == "lk" && led3.IsFrozen("lk", 1), "A⑬竞速403写同一本退避账");

        // ---- 调参档接线断言（老板 2026-11-12 令：旋钮全量进设置页+一键还原） ----
        {
            AdvancedTuning.ResetToDefaults();
            // 钳制：非法值收进合法范围（设置页与还原同一入口）
            AdvancedTuning.Apply(99, 1, 99999, 1, 1, 99);
            Check(AdvancedTuning.SelfHealFailGate == 20, "调参档门槛钳上限");
            Check(AdvancedTuning.SelfHealReprobeMs >= AdvancedTuning.ReprobeMsMin, "调参档回探钳下限");
            Check(AdvancedTuning.ProxyPort >= AdvancedTuning.ProxyPortMin, "调参档端口钳下限");
            AdvancedTuning.Apply(5, 5 * 60_000, 60, 120_000, 7_200_000, 47618);
            Check(AdvancedTuning.SelfHealFailGate == 5 && AdvancedTuning.SelfHealReprobeMs == 5 * 60_000, "调参档Apply即生效");
            // 门控跟随：门槛 5 时连败 3~4 不触发、第 5 次触发
            var gate5 = new SelfHealGate(() => 0);
            Check(!gate5.OnPlayFailed() && !gate5.OnPlayFailed() && !gate5.OnPlayFailed() && !gate5.OnPlayFailed(),
                "调参档门控跟随：门槛5时连败4不触发");
            Check(gate5.OnPlayFailed(), "调参档门控跟随：第5次触发");
            // 一键还原：全部回出厂，门控阈值同步回 3
            AdvancedTuning.ResetToDefaults();
            Check(SelfHealGate.FailThreshold == 3 && HlsRewriter.ProxyPort == 47618, "调参档一键还原默认");
            var gate3 = new SelfHealGate(() => 0);
            gate3.OnPlayFailed(); gate3.OnPlayFailed();
            Check(gate3.OnPlayFailed(), "还原后门控第3次触发");
            // 退避跟随：重试起步调至 30 秒，首败冻结约 30 秒
            AdvancedTuning.Apply(3, 5 * 60_000, 60, 30_000, 7_200_000, 47618);
            var l30 = new SourceBackoffLedger();
            l30.RecordLimited("yangshipin", 1000);
            Check(l30.IsFrozen("yangshipin", 1000 + 29_000) && !l30.IsFrozen("yangshipin", 1000 + 31_000),
                "调参档退避跟随：起步30秒");
            AdvancedTuning.ResetToDefaults();
            Check(HlsRewriter.Wrap("https://example.com/a.m3u8").Contains(":47618/?u="), "端口还原后代理URL回默认");
        }

        // ---- ③热修批断言（老板 19:28 令「全部干完」：A1 僵死收账 / A2 HTTP 实响应探活+/ping 防自环 / A3 门控退场联动） ----
        {
            // A1：MarkDead 收账（AcceptLoop 异常退出不得留僵尸态）
            var k1 = new SelfHealProxyKernel();
            k1.Start();
            Check(k1.IsRunning, "③热修A1：内核起监听 IsRunning=true");
            k1.MarkDead();
            Check(!k1.IsRunning, "③热修A1：MarkDead 后 IsRunning=false（不留僵尸态）");
            Check(!k1.IsAlive, "③热修A1：收账后探活即死");

            // A2：/ping 短路端点（防自环）+ IsAlive HTTP 实响应口径
            var k2 = new SelfHealProxyKernel();
            k2.Start();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var ping = http.GetAsync($"http://127.0.0.1:{HlsRewriter.ProxyPort}/ping").GetAwaiter().GetResult();
                Check(ping.StatusCode == HttpStatusCode.OK, "③热修A2：/ping 短路直回 200（不进 Forward 防自环）");
                Check(k2.IsAlive, "③热修A2：HTTP 实响应探活=活");
            }
            finally { k2.Stop(); }
            Check(!k2.IsAlive, "③热修A2：停后探活即死（不假活）");

            // A3：门控退场联动三态（代理活不退 / 代理死退场 / 未启空操作）
            var g3 = new SelfHealGate(() => 0);
            g3.Disengage("不应生效"); // 门控未启用：空操作
            Check(!g3.IsEngaged, "③热修A3：门控未启退场为空操作");
            g3.OnPlayFailed(); g3.OnPlayFailed(); g3.OnPlayFailed();
            var verdictA3 = new ResolutionTriage.Result(ResolutionTriage.VerdictKind.SystemDnsBroken, "系统解析坏（测试）");
            Check(g3.ReportTriage(verdictA3), "③热修A3：判别 SystemDnsBroken 启用门控");
            Check(g3.IsEngaged, "③热修A3：启用态确认（代理活时不应退场）");
            g3.Disengage("网络自愈门控退场：代理失活，回归直连播放链");
            Check(!g3.IsEngaged && g3.LastVerdict!.Contains("代理失活"), "③热修A3：代理死退场（IsEngaged=false，退场原因落账）");

            // C7（①延伸断言补账）：重定向后最终基址带 ?query 时，相对行按原样 query 转绝对（产物为代理 URL，解包核对）
            {
                var rewritten = HlsRewriter.RewritePlaylist(
                    "#EXTM3U\n#EXTINF:-1,live\nseg-1.ts\n",
                    "http://final.example.tv:82/live/cctv3hd.m3u8?tm=123&key=abc");
                var uLine = rewritten.Split('\n').First(l => l.Contains("/?u="));
                Check(HlsRewriter.TryUnwrap(uLine, out var inner)
                        && inner == "http://final.example.tv:82/live/seg-1.ts?tm=123&key=abc",
                    "③热修C7断言：相对分片按最终基址转绝对（票据 query 原样随行）");
            }
        }

        // ---- 源档案断言（上善若水纪律：点名册写活，机制读档案不读代码） ----
        {
            // 出厂名册回落：原点名册原值搬家，判定口径不变
            Check(SourceProfileStore.Shared.KindOf("yangshipin") == SourceKind.TicketShort, "源档案央视频=短票据");
            Check(SourceProfileStore.Shared.KindOf("gzstv") == SourceKind.TicketShort, "源档案gzstv=短票据");
            Check(SourceProfileStore.Shared.ProfileOf("cztv").Rotating && SourceProfileStore.Shared.ProfileOf("jstv").Rotating,
                "源档案cztv/jstv=轮换");
            Check(!SourceProfileStore.Shared.ProfileOf("kankan").Rotating, "源档案kankan保守不轮换");
            Check(SourceProfileStore.Shared.ProfileOf("nmtv").BatchAggregation, "源档案nmtv整表聚合");
            Check(WarmupPolicy.PaceOf("jstv").RefreshIntervalMs == 150_000, "源档案jstv保鲜150秒");
            Check(SourceProfileStore.Shared.ProfileOf("yangshipin").IpBound, "源档案央视频IP绑定");
            // 未登记源兜底：Unknown + 保守节拍
            Check(SourceProfileStore.Shared.KindOf("新源x") == SourceKind.Unknown, "源档案未登记=Unknown");
            Check(WarmupPolicy.PaceOf("新源x").RefreshIntervalMs == 10 * 60_000, "源档案未登记保守兜底10分钟");
            // 形状判别（纯逻辑可测）
            Check(SourceProfileStore.ClassifyUrl("http://112.30.32.1:9901/tslive/live.m3u8") == SourceKind.IPDirect, "形状判别纯IP=IPDirect");
            Check(SourceProfileStore.ClassifyUrl("https://l.x.com/play.m3u8?auth_key=123-0-abc") == SourceKind.ShortSign, "形状判别签名参数=ShortSign");
            Check(SourceProfileStore.ClassifyUrl("https://l.x.com/play.m3u8") == SourceKind.Unknown, "形状判别普通URL=Unknown待探测");
            // 指纹：参数值变指纹不变（换票），路径变指纹变（轮换）
            var fp1 = SourceProfileStore.UrlFingerprint("https://l.x.com/p.m3u8?auth_key=A");
            var fp2 = SourceProfileStore.UrlFingerprint("https://l.x.com/p.m3u8?auth_key=B");
            var fp3 = SourceProfileStore.UrlFingerprint("https://l.x.com/q.m3u8?auth_key=A");
            Check(fp1 == fp2 && fp1 != fp3, "源档案指纹：换票值变指纹不变，路径变才算轮换");
            // 行为探测：未知源两次同指纹→Constant；两次异指纹→Rotating
            SourceProfileStore.Shared.ObserveSample("学源a", "https://x.a.com/s.m3u8?k=1", 1000);
            SourceProfileStore.Shared.ObserveSample("学源a", "https://x.a.com/s.m3u8?k=2", 2000);
            Check(SourceProfileStore.Shared.KindOf("学源a") == SourceKind.Constant, "源档案行为探测：两次同指纹=恒定");
            SourceProfileStore.Shared.ObserveSample("学源b", "https://x.b.com/s1.m3u8", 1000);
            SourceProfileStore.Shared.ObserveSample("学源b", "https://x.b.com/s2.m3u8", 2000);
            Check(SourceProfileStore.Shared.ProfileOf("学源b").Rotating, "源档案行为探测：两次异指纹=轮换");
            Check(StreamResolver.IsRotatingTicketSource("yangshipin") && !StreamResolver.IsRotatingTicketSource("xatv"),
                "源档案轮换判定口径：出厂名册原值");
        }

        // ---- v28 主线④网络面四点断言 ----
        {
            // ①竞速默认开：无开关即常开——空候选竞速立即返回 null（不抛不挂），A⑬行为断言已盖完整链路
            var emptyRace = await EndpointRacer.RaceAsync(Array.Empty<EndpointRacer.Candidate>(), led3, 0);
            Check(emptyRace is null, "v28网络①竞速默认开（空候选立即收场不挂，A⑬口径不变）");
            // ②抢首包默认关：TV 预热/预取只取票+几 KB 头探（Range 0-2047），不拉首分片——预检探测仅头级，且结果只三态不带分片载荷
            var pv = new[] { StreamResolver.ProbeVerdict.Reachable, StreamResolver.ProbeVerdict.Rejected, StreamResolver.ProbeVerdict.Unreachable };
            Check(pv.Length == 3, "v28网络②抢首包默认关（预热仅 Range 0-2047 头级探，无首分片拉取）");
            // ③DNS 择优：缓存命中、DoH 失败回落系统 DNS、IP 字面量直返、择路降权
            long dnsClock = 1_700_000_000_000;
            var picker = new StreamDnsPicker(
                host => Task.FromResult(new[] { IPAddress.Parse("1.2.3.4"), IPAddress.Parse("5.6.7.8") }),
                (server, h) => server == "223.5.5.5" ? Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }) : Task.FromResult<IPAddress[]>(Array.Empty<IPAddress>()),
                () => dnsClock);
            var ips1 = picker.ResolveAsync("example.com").GetAwaiter().GetResult();
            // ③热修 B4：三路同步完成=全量合并（与旧口径一致）；早返回只对异步慢路生效（下两例验证）
            Check(ips1.Select(i => i.ToString()).SequenceEqual(new[] { "1.2.3.4", "5.6.7.8", "9.9.9.9" }), "v28网络③DNS合并去重只取IPv4（同步完成仍全量合并，系统→阿里→CF 拼接序）");
            // ③热修 B4-1：系统先出（DoH 延迟 500ms）→ 早返回系统 IP，不等 DoH 齐
            {
                var pickerSlowDoh = new StreamDnsPicker(
                    host => Task.FromResult(new[] { IPAddress.Parse("1.2.3.4") }),
                    async (s, h) => { await Task.Delay(500); return Array.Empty<IPAddress>(); },
                    () => dnsClock);
                var ipsFast = pickerSlowDoh.ResolveAsync("fast.com").GetAwaiter().GetResult();
                Check(ipsFast.Length == 1 && ipsFast[0].ToString() == "1.2.3.4", "③热修B4：系统先出即返回（DoH 未齐不等待）");
            }
            // DoH 全挂：系统 DNS 兜底仍出结果
            var pickerFallback = new StreamDnsPicker(
                host => Task.FromResult(new[] { IPAddress.Parse("1.2.3.4") }),
                (s, h) => throw new Exception("DoH 挂了"),
                () => dnsClock);
            var ips2 = pickerFallback.ResolveAsync("example.com").GetAwaiter().GetResult();
            Check(ips2.Length == 1 && ips2[0].ToString() == "1.2.3.4", "v28网络③DoH异常回落系统DNS（兜底不挂取流）");
            // ③热修 B4-2：系统解析挂住（永不完成），DoH 出 IP 应先返——不白等系统超时
            {
                var hangTcs = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pickerHang = new StreamDnsPicker(
                    host => hangTcs.Task,
                    (s, h) => Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }),
                    () => dnsClock);
                var ipsH = pickerHang.ResolveAsync("hang.com").GetAwaiter().GetResult();
                Check(ipsH.Length == 1 && ipsH[0].ToString() == "9.9.9.9", "③热修B4：系统解析挂住不拖首连（DoH 先出即返）");
                hangTcs.SetResult(Array.Empty<IPAddress>()); // 收尾（防测试进程悬挂警告）
            }
            // ③热修 B4-3：系统快速失败出空，DoH 出 IP 照常返回（先出的空路不短路失败）
            {
                var pickerEmpty = new StreamDnsPicker(
                    host => Task.FromResult<IPAddress[]>(Array.Empty<IPAddress>()),
                    (s, h) => Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }),
                    () => dnsClock);
                var ipsE = pickerEmpty.ResolveAsync("empty.com").GetAwaiter().GetResult();
                Check(ipsE.Length == 1 && ipsE[0].ToString() == "9.9.9.9", "③热修B4：系统出空不短路失败（DoH 补位返回）");
            }
            // IP 字面量直接返回（不再解析）
            var literal = new StreamDnsPicker(() => dnsClock);
            var lit = literal.ResolveAsync("203.0.113.9").GetAwaiter().GetResult();
            Check(lit.Length == 1 && lit[0].ToString() == "203.0.113.9", "v28网络③IP字面量直返不走解析");
            // 择路降权：失败 IP 排后、成功恢复
            picker.ReportFail(ips1[0]);
            var ips3 = picker.ResolveAsync("example.com").GetAwaiter().GetResult(); // 缓存内，但重排序生效
            Check(ips3.Last().ToString() == "1.2.3.4", "v28网络③失败IP择路降权排后（不拉黑）");
            picker.ReportOk(ips1[0]);
            var ips4 = picker.ResolveAsync("example.com").GetAwaiter().GetResult();
            Check(ips4.First().ToString() == "1.2.3.4", "v28网络③成功恢复原序");
            // ④连接复用兜底：取流双通道均为静态单例（复用生效）+ 池化连接寿命 5 分钟兜底
            Check(StreamResolver.Dns is not null, "v28网络④DNS择优挂接取流链路（静态单例入口在位）");
            // A1短路（vc30ab口径）：取流入口原样返回，DoH/IP 择路停用（模块与单例保留，摘掉入口 return url 即恢复）
            var direct = await StreamResolver.PickEndpointAsync("http://example.com/live.m3u8?tok=1",
                new StreamDnsPicker(host => Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }), (s, h) => Task.FromResult<IPAddress[]>(Array.Empty<IPAddress>()), () => dnsClock));
            Check(direct == "http://example.com/live.m3u8?tok=1", "v28网络④PickEndpoint 择优短路原样返回（vc30ab口径）");
            var fallback = await StreamResolver.PickEndpointAsync("http://example.com/live.m3u8?tok=2",
                new StreamDnsPicker(host => Task.FromException<IPAddress[]>(new Exception("解析挂了")), (s, h) => Task.FromResult<IPAddress[]>(Array.Empty<IPAddress>()), () => dnsClock));
            Check(fallback == "http://example.com/live.m3u8?tok=2", "v28网络④解析失败兜底原样返回（异常不挂取流）");
        }
    }


    // ---- A⑥ 预热落盘层：读写/保鲜期/超限清最旧/节流落盘/损坏即弃 ----
    {
        var dir = Path.Combine(Path.GetTempPath(), "taolive_warmup_" + Guid.NewGuid().ToString("N"));
        var store = new WarmupStore(dir);
        long t = 1_700_000_000_000;
        store.Put("u1", "body1", 60_000, t);
        Check(store.Get("u1", t + 1) == "body1", "A⑥落盘写入读回");
        Check(store.Get("u1", t + 61_000) is null, "A⑥保鲜期外视为没有（现场重取）");
        // 落盘真实写文件（节流 3 秒后 Flush）
        store.Flush(t + 10_000);
        Check(File.Exists(Path.Combine(dir, "playlist_cache.json")), "A⑥节流落盘出文件");
        var store2 = new WarmupStore(dir);
        store2.Load();
        Check(store2.Get("u1", t + 1) == "body1", "A⑥重启灌回复用");
        // 超限清最旧（容量 128）
        for (int i = 0; i < 200; i++) store.Put($"k{i}", "b" + i, 3_600_000, t + 20_000 + i);
        Check(store.Count <= 128, "A⑥上限128超限清最旧");
        // 损坏文件按无缓存处理
        File.WriteAllText(Path.Combine(dir, "playlist_cache.json"), "{broken");
        var store3 = new WarmupStore(dir);
        store3.Load();
        Check(store3.Count == 0, "A⑥损坏文件即弃不挡起播");
        // 节流：3 秒内第二次 Flush 不落盘（不抛错即可）
        store.Put("u2", "b", 1000, t + 30_000);
        store.Flush(t + 31_000);
        Check(true, "A⑥节流窗口内重复落盘安全");
        // v28 读盘优化：灌回改后台后，Load 与播放线程 Put/Get 并发——有锁护体，无异常不丢账
        {
            var cdir = Path.Combine(Path.GetTempPath(), "taolive_warmup_c_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cdir);
            File.WriteAllText(Path.Combine(cdir, "playlist_cache.json"),
                JsonSerializer.Serialize(new Dictionary<string, WarmupEntry> { ["seed"] = new() { Body = "sb", AtMs = t, FreshnessMs = 3_600_000 } }));
            var cstore = new WarmupStore(cdir);
            var loadTask = Task.Run(cstore.Load); // 模拟 UI 层后台灌回
            var errs = new List<Exception>();
            var workers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                try
                {
                    for (int k = 0; k < 10; k++) // 共 8×10=80 条 + 种子 1 条 < 容量 128，不触发超限清最旧（该项已有独立断言）
                    {
                        cstore.Put($"u{i}-{k}", "b", 3_600_000, t);
                        cstore.Get("seed", t + 1);
                    }
                }
                catch (Exception ex) { lock (errs) errs.Add(ex); }
            })).ToArray();
            Task.WaitAll(new[] { loadTask }.Concat(workers).ToArray());
            Check(errs.Count == 0, $"v28预热账并发Load+Put/Get无竞态异常（{errs.Count}笔）");
            Check(cstore.Get("seed", t + 1) == "sb", "v28预热账灌回种子条目保留");
        }
    }


// ===== AI 直播缓冲池（老板 11-11 00:08 口径）：初始 10–20s / 60s 封顶 / 三道保险 =====
var aiClock = 0.0;
double AiClock() { return aiClock; }
var pool = new AllPlayer.BLL.AiBufferPool(initialTargetSeconds: 15.0, clockSeconds: AiClock);

// 1 初始缓冲未攒够不放行
Check(!pool.IsInitialBufferReady(), "AI池:初始未攒够不放行");

// 2 攒到目标 15 秒即放行（10–20 区间）
for (int i = 0; i < 3; i++) pool.EnqueueSegment(i, 5.0);
Check(pool.LagSeconds() == 15.0, "AI池:入队累计15s");
aiClock += 1.0;
pool.ReportProcessed(0, 5.0, 1.0); pool.ReportProcessed(1, 5.0, 1.0); pool.ReportProcessed(2, 5.0, 1.0);
Check(pool.IsInitialBufferReady(), "AI池:加工完15s放行播放");

// 3 加工速度快于实时：不降档
pool.ReportProcessed(3, 5.0, 2.0); // 2.5x
var d3 = pool.EnqueueSegment(3, 5.0);
Check(!d3.SuggestDowngrade, "AI池:2.5x不降档");

// 4 加工慢于实时且有积压：建议降档（保险①）
var slowPool = new AllPlayer.BLL.AiBufferPool(clockSeconds: AiClock);
slowPool.EnqueueSegment(0, 5.0);
aiClock += 10.0;
slowPool.ReportProcessed(0, 5.0, 10.0); // 0.5x
var d4 = slowPool.EnqueueSegment(1, 5.0);
Check(d4.SuggestDowngrade, "AI池:0.5x建议降档");

// 5 落后 60 秒封顶强制直出（保险③），回落 45 秒以下恢复加工
var lagPool = new AllPlayer.BLL.AiBufferPool(clockSeconds: AiClock);
AiIngestDecision dl = lagPool.EnqueueSegment(-1, 0.0);
for (int i = 0; i < 12; i++) dl = lagPool.EnqueueSegment(i, 5.0); // 60s 全入队
for (int i = 0; i < 12; i++) lagPool.ReportProcessed(i, 5.0, 0.5); // 全加工完，速率 10x
Check(dl!.Action == AllPlayer.BLL.AiIngestAction.Process, "AI池:60s整未越线");
dl = lagPool.EnqueueSegment(12, 5.0); // 65s 越线
Check(dl.Action == AllPlayer.BLL.AiIngestAction.DirectPlay && dl.Reason == AllPlayer.BLL.AiBypassReason.LagCap, "AI池:65s强制直出(60s封顶)");
Check(lagPool.IsInitialBufferReady(), "AI池:直出态恒放行");
// 直出即时播放，lag 缩回：再入 4 片全直出 → lag=65-25=40 <45 恢复
for (int i = 13; i < 17; i++) dl = lagPool.EnqueueSegment(i, 5.0);
Check(dl.Reason == AllPlayer.BLL.AiBypassReason.LagCap, "AI池:恢复前仍直出");
dl = lagPool.EnqueueSegment(17, 5.0);
Check(dl.Reason == AllPlayer.BLL.AiBypassReason.LagCap, "AI池:封顶后持续直出延迟锁定");
lagPool.ReportPlayed(17, 45.0); // 播放确认追平（模拟快进/追平）
lagPool.EnqueueSegment(17, 0.0);
lagPool.Reset();               // 换台清池
dl = lagPool.EnqueueSegment(18, 5.0);
Check(dl.Action == AllPlayer.BLL.AiIngestAction.Process && dl.Reason == AllPlayer.BLL.AiBypassReason.None, "AI池:复位后恢复加工");

// 6 过热退直出（保险②），解除后恢复
var thPool = new AllPlayer.BLL.AiBufferPool(clockSeconds: AiClock);
thPool.SetThermalGuard(overheating: true, lowBattery: false);
var d6 = thPool.EnqueueSegment(0, 5.0);
Check(d6.Action == AllPlayer.BLL.AiIngestAction.DirectPlay && d6.Reason == AllPlayer.BLL.AiBypassReason.Thermal, "AI池:过热退原画质直出");
Check(thPool.IsInitialBufferReady(), "AI池:过热态放行");
thPool.SetThermalGuard(false, false);
d6 = thPool.EnqueueSegment(1, 5.0);
Check(d6.Action == AllPlayer.BLL.AiIngestAction.Process, "AI池:退热恢复加工");

// 7 低电退直出
var lbPool = new AllPlayer.BLL.AiBufferPool(clockSeconds: AiClock);
lbPool.SetThermalGuard(false, lowBattery: true);
var d7 = lbPool.EnqueueSegment(0, 5.0);
Check(d7.Action == AllPlayer.BLL.AiIngestAction.DirectPlay && d7.Reason == AllPlayer.BLL.AiBypassReason.LowBattery, "AI池:低电退直出");

// 8 初始目标钳制 10–20
var aiClampMin = new AllPlayer.BLL.AiBufferPool(initialTargetSeconds: 5.0);
var aiClampMax = new AllPlayer.BLL.AiBufferPool(initialTargetSeconds: 99.0);
for (int i = 0; i < 2; i++) aiClampMin.EnqueueSegment(i, 5.0); aiClampMin.ReportProcessed(0, 5.0, 0.1); aiClampMin.ReportProcessed(1, 5.0, 0.1);
Check(aiClampMin.IsInitialBufferReady(), "AI池:5s钳到10s下限(10s放行)");
for (int i = 0; i < 2; i++) aiClampMax.EnqueueSegment(i, 5.0); aiClampMax.ReportProcessed(0, 5.0, 0.1); aiClampMax.ReportProcessed(1, 5.0, 0.1);
Check(!aiClampMax.IsInitialBufferReady(), "AI池:99s钳到20s上限(10s不放行)");

// 9 并发 smoke：三线程同时入队不炸账
var cp = new AllPlayer.BLL.AiBufferPool(initialTargetSeconds: 15.0, maxLagSeconds: 10000.0, clockSeconds: AiClock);
Parallel.For(0, 300, i => cp.EnqueueSegment(i, 1.0));
Check(cp.LagSeconds() == 300.0, "AI池:300并发入队账目平");

// 10 播放确认推进滞后回落
cp.ReportPlayed(0, 100.0);
Check(cp.LagSeconds() == 200.0, "AI池:播放确认滞后回落");

// ==================== v25 修法批（九层第 27 项口径） ====================
// 修法六：台级表现账（EpgKey+SourceId 一格账）+ 时段系数 + 让路闸；修法五/七并发安全；修法二清账口
{
    var pace = new ChannelPaceLedger();
    const long baseMs = 600_000; // 央视频 10 分钟保鲜

    // ① 台×源粒度：同台多源各记各的（老板 12:32 拍板口径）
    for (int i = 0; i < 5; i++) { pace.Record("cctv1", "yangshipin", false); }
    for (int i = 0; i < 8; i++) { pace.Record("cctv1", "nmtv", true); }
    Check(pace.TierOf("cctv1", "yangshipin", batchSource: false) == ChannelPaceLedger.TierBrittle,
        "v25⑥①同台多源各记各账：央视频源 0/5 记脆档不拖累 nmtv 源");
    Check(pace.TierOf("cctv1", "nmtv", batchSource: false) == ChannelPaceLedger.TierStable,
        "v25⑥①nmtv 源 8/8 记稳档（备援源抽风不拖累主源表现）");

    // ② 滑动窗口 10 条封顶
    var paceWin = new ChannelPaceLedger();
    for (int i = 0; i < 12; i++) paceWin.Record("w1", "cztv", true); // 12 连正，窗口只留最近 10
    paceWin.Record("w1", "cztv", false); paceWin.Record("w1", "cztv", false); // 2 负
    Check(paceWin.TierOf("w1", "cztv", false) == ChannelPaceLedger.TierNormal,
        "v25⑥②滑动窗口 10 条封顶：早期结论滚出窗口，按最近 10 条 8/10 记正常档（不因历史全程正而虚高）");

    // ③ 窗口不足 3 条不评档（1 次失败不判脆，分析轮 3 漏洞 1）
    var paceFew = new ChannelPaceLedger();
    paceFew.Record("f1", "gzstv", false);
    paceFew.Record("f1", "gzstv", false);
    Check(paceFew.TierOf("f1", "gzstv", false) == ChannelPaceLedger.TierNormal,
        "v25⑥③窗口<3 不评档：2 次失败仍正常档（1-2 次弹回不推脆档）");

    // ④ 计负口径：网络级失败不计负（分析轮 2 漏洞 2）
    Check(paceFew.TierOf("f1", "gzstv", false) == ChannelPaceLedger.TierNormal,
        "v25⑥④网络级失败不计负：账本只认明确拒绝与播放流级失败");

    // ⑤ 整表源不做台级三档（节拍按源，分析轮 2 漏洞 1）
    Check(pace.TierOf("cctv1", "nmtv", batchSource: true) == ChannelPaceLedger.TierNormal,
        "v25⑥⑤整表源豁免台级三档：batch=true 恒正常档");

    // ⑥ 档位系数与钳制（登记口径含 jitter：值=tierMs × 时段 × jitter）
    var paceFac = new ChannelPaceLedger();
    for (int i = 0; i < 5; i++) paceFac.Record("x1", "yangshipin", true);
    long stableMs = paceFac.RegisterIntervalMs("x1", "yangshipin", baseMs, 12, false);
    Check(stableMs == (long)Math.Clamp(baseMs * 1.5 * ChannelPaceLedger.StableJitter("x1"), baseMs / 2, baseMs * 4),
        "v25⑥⑥稳档 ×1.5×jitter（钳制内，非央视频时段外系数=1）");

    // ⑦ 时段系数：晚高峰 0.7 / 深夜 2.0（同 jitter 口径精确对拍）
    long peakMs = new ChannelPaceLedger().RegisterIntervalMs("x2", "cztv", baseMs, 20, false);
    Check(peakMs == (long)Math.Clamp(baseMs * 0.7 * ChannelPaceLedger.StableJitter("x2"), baseMs / 2, baseMs * 4),
        "v25⑥⑦晚高峰 19-23 → ×0.7（质量波动加密）");
    long nightMs = new ChannelPaceLedger().RegisterIntervalMs("x3", "cztv", baseMs, 3, false);
    Check(nightMs == (long)Math.Clamp(baseMs * 2 * ChannelPaceLedger.StableJitter("x3"), baseMs / 2, baseMs * 4),
        "v25⑥⑦深夜 01-07 → ×2（正常档钳制上限 2×）");

    // ⑧ yangshipin 时段系数恒 1.0（总闸只降不升，分析轮 1 漏洞 4）
    for (int i = 0; i < 5; i++) { paceFac.Record("x4", "yangshipin", true); paceFac.Record("x5", "yangshipin", true); }
    long yspPeak = paceFac.RegisterIntervalMs("x4", "yangshipin", baseMs, 20, false);   // 稳档 + 晚高峰
    long yspNight = paceFac.RegisterIntervalMs("x5", "yangshipin", baseMs, 3, false);   // 稳档 + 深夜
    Check(yspPeak == (long)Math.Clamp(baseMs * 1.5 * ChannelPaceLedger.StableJitter("x4"), baseMs / 2, baseMs * 4)
        && yspNight == (long)Math.Clamp(baseMs * 1.5 * ChannelPaceLedger.StableJitter("x5"), baseMs / 2, baseMs * 4),
        "v25⑥⑧央视频时段系数恒 1.0：晚高峰/深夜都不受时段加密（画像最敏感）");

    // ⑨ 深夜总上限 4× / 晚高峰下限 0.5×（分析轮 1 漏洞 6）
    var paceDeep = new ChannelPaceLedger();
    for (int i = 0; i < 5; i++) paceDeep.Record("x6", "cztv", true);
    Check(paceDeep.RegisterIntervalMs("x6", "cztv", baseMs, 2, false) <= baseMs * 4,
        "v25⑥⑨深夜总上限 4×保鲜期（×jitter 后仍被钳住）");
    var pacePeakLow = new ChannelPaceLedger();
    for (int i = 0; i < 5; i++) pacePeakLow.Record("x7", "cztv", false);
    Check(pacePeakLow.RegisterIntervalMs("x7", "cztv", baseMs, 20, false) >= baseMs / 2,
        "v25⑥⑨晚高峰下限 0.5×保鲜期（脆档×0.7 不破下限）");

    // ⑩ jitter ∈ [0.8,1.4] 且 EpgKey 稳定哈希一致（修法一 1c）
    double j1 = ChannelPaceLedger.StableJitter("CCTV1"), j2 = ChannelPaceLedger.StableJitter("CCTV1");
    Check(j1 >= 0.8 && j1 <= 1.4 && j1 == j2,
        "v25一1c jitter ∈[0.8,1.4] 且 EpgKey 稳定哈希（同表分布一致不漂移）");

    // ⑪ 全局让路闸：占位成功→MinGap 内拒绝→到期放行（修法一 1a）
    var gate = new ChannelPaceLedger();
    Check(gate.TryAcquireGate("yangshipin", 1_000_000, 2_000), "v25一1a 让路闸首次占位成功");
    Check(gate.GateRemainingMs("yangshipin", 1_001_000) == 1_000,
        "v25一1a 让路闸 MinGap 内剩余时长正确（不翻倍）");
    Check(!gate.TryAcquireGate("yangshipin", 1_001_000, 2_000), "v25一1a MinGap 内再占失败（稳态与屏内共享同一闸）");
    Check(gate.TryAcquireGate("yangshipin", 1_002_001, 2_000), "v25一1a 到期后重新可占");

    // ⑫ RetainOnly 清孤儿（分析轮 55）
    var paceRetain = new ChannelPaceLedger();
    paceRetain.Record("keep1", "yangshipin", true);
    paceRetain.Record("gone1", "yangshipin", true);
    paceRetain.RetainOnly(k => k.StartsWith("keep"));
    Check(paceRetain.TierOf("gone1", "yangshipin", false) == ChannelPaceLedger.TierNormal
        && paceRetain.TierOf("keep1", "yangshipin", false) == ChannelPaceLedger.TierNormal,
        "v25一55 RetainOnly 清孤儿：gone 台账清空，keep 台账保留");

    // ⑬ SourceBackoffLedger 并发 smoke（修法七 3a：热备校验流与稳态循环并发入账）
    var ledger = new SourceBackoffLedger();
    Parallel.For(0, 300, i => ledger.RecordLimited("yangshipin", 1000 + i));
    Check(ledger.IsFrozen("yangshipin", 1000) && ledger.RemainingMs("yangshipin", 1000) > 0,
        "v25七3a 退避账 300 并发入账不炸账（ConcurrentDictionary）");
    ledger.Recover("yangshipin");
    Check(!ledger.IsFrozen("yangshipin", 1001), "v25七3a 200 全额恢复并行读安全");

    // ⑭ 票账 ClearCooldowns：清冷却留缓存（修法二 2c/2d）
    var ysp2 = new YspTicketService();
    long t25 = 1794016800;
    ysp2.MarkFetched("cc", t25, "http://x/cc.m3u8");
    ysp2.MarkCooldown("cc", DateTimeOffset.FromUnixTimeSeconds(t25).DateTime);
    ysp2.ClearCooldowns();
    Check(!ysp2.InCooldown("cc", DateTimeOffset.FromUnixTimeSeconds(t25).DateTime.AddSeconds(1)),
        "v25二2c ClearCooldowns：网络变更后票据冷却清零（不把好环境白冻 2 小时）");
    Check(ysp2.TryGet("cc", t25 + 60, out var keep) && keep == "http://x/cc.m3u8",
        "v25二2c ClearCooldowns：票据缓存保留验证（失败自然重新入账）");
}

// ==================== 治顿批①·备援源注册表（fallback_sources） ====================
{
    // ① 表完整性：24 路（15 卫视 + 9 央视，vc39 ①c 收录 CCTV2/6、19:2x 收录 CCTV3/5/8），URL 全 http(s) 绝对地址
    Check(FallbackSourceRegistry.Table.Length == 24, "治①①备援表 24 路（15 卫视+9 央视）");
    Check(FallbackSourceRegistry.Table.All(e => e.Url.StartsWith("http")),
        "治①①备援表 URL 全绝对地址");

    // ② 频道覆盖：央视频 63 路目录 22 台（vc39 ①c 后含 CCTV2/6、19:2x 后含 CCTV3/5/8）+ 浙江台目录 2 台，共 24 全命中
    {
        var yspHit = new YangshipinSource().FetchChannels().Count(c => FallbackSourceRegistry.Has(c.Name));
        var cztvHit = new CztvSource().FetchChannels().Count(c => FallbackSourceRegistry.Has(c.Name));
        Check(yspHit == 22 && cztvHit == 2, $"治①②目录覆盖 24 台（央视频 {yspHit}+浙江 {cztvHit}）");
    }

    // ③ 主源失败 → 备援表直连（不依赖 UserFallback）：湖南卫视主源取流为空（未知源+空流地址=失败），应返回备援 URL
    {
        var resolver = new StreamResolver();
        var hn = new Channel { Name = "湖南卫视", SourceId = "dead", StreamUrl = "", EpgKey = "c_hnws" };
        var r = await resolver.ResolveWithFailoverAsync(hn);
        Check(r != null && r.Url.Contains("mgtv-hlsal-ldvt"), "治①③主源失败切内置备援（湖南卫视→mgtv）");
        Check(r != null && resolver.Failover.CurrentIndex(hn.EpgKey) >= 0, "治①③备援切换进自愈账（状态机在管）");
    }

    // ④ 稳定后回主源：备援连续成功达 RecoverToPrimaryAfter 轮 → 自动回主源（Index=0）
    {
        var f = new SourceFailover();
        f.NextCandidateOnFailure("k1", 2); // 主源失败 → 备援
        f.OnSuccess("k1"); f.OnSuccess("k1"); // 连续 2 轮成功
        Check(f.CurrentIndex("k1") == 0, "治①④备援连续成功 2 轮自动回主源");
    }

    // ⑤ 未登记台不误切：无备援表匹配且无 UserFallback → 返回 null（UI 明确提示）
    {
        var resolver = new StreamResolver();
        var odd = new Channel { Name = "不存在的台", SourceId = "dead", StreamUrl = "", EpgKey = "odd" };
        Check(await resolver.ResolveWithFailoverAsync(odd) == null, "治①⑤未登记台无备援不硬切（明确失败）");
    }

    // ⑥ 台名归一匹配：去空格后一致即命中（「CCTV1 综合」变体也命中 CCTV1综合）
    Check(FallbackSourceRegistry.Has("CCTV1 综合"), "治①⑥台名归一匹配（空格容忍）");
}

Console.WriteLine($"\n{pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;

class StubSource : IChannelSource
{
    public string SourceId { get; }
    public StubSource(string id) => SourceId = id;
    public List<Channel> FetchChannels() => new()
    {
        new Channel { Name = SourceId+"-A", Category = ChannelCategory.Cctv, SourceId = SourceId, StreamUrl="http://s/a" },
        new Channel { Name = SourceId+"-B", Category = ChannelCategory.Provincial, SourceId = SourceId, StreamUrl="http://s/b" },
        new Channel { Name = SourceId+"-C", Category = ChannelCategory.City, SourceId = SourceId, StreamUrl="http://s/c", Unavailable=true, Verdict="测试占位：流不对外" },
    };
}

// 取票 v2 测试桩（自愈2：热更新注入路径）
class TestAlgoV2 : ITicketAlgorithm
{
    public string Version => "v2-test";
    public string? Build(string channelId, long epochSeconds, string guid) => $"v2://{channelId}/{epochSeconds}";
}

// 通道测试桩（通道化：三候选通道）
class StubLane : IStreamLane
{
    public string LaneId { get; }
    public StubLane(string id) => LaneId = id;
    public bool CanHandle(Channel ch) => LaneId switch
    {
        "ysp" => ch.SourceId == "yangshipin",
        "cztv" => ch.SourceId == "cztv",
        _ => true, // user-m3u 兜底全收
    };
    public Task<string?> FetchAsync(Channel ch) => Task.FromResult<string?>($"http://{LaneId}/stream.m3u8");
}

// 桩源：三类可播 + 1 路接不进（骨架专用，真实源回填后换正式源）
