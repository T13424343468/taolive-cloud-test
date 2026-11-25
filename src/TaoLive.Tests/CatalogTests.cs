using System.Text;
using TaoLive.Catalog;
using TaoLive.Model;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 台单与取票契约（2.0 W4）：一期摸底算法逐字移植的对拍向量——
/// cKey/取票窗口/冷却/auth_key/kankan 签名/jstv 签名/整表匹配，向量原样搬运，
/// 移植走样任何一拍立即红灯。
/// </summary>
public class CatalogTests
{
    private static Func<int, byte[]> FixedRandom()
    {
        int idx = 0;
        return n =>
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)((idx + i) & 0xFF);
            idx += n;
            return b;
        };
    }

    [Fact]
    public void Ysp_ckey_matches_reference_implementation_byte_by_byte()
    {
        var (ck, _, _) = YspCkey.Create("2024078201", 1700000000, "00112233445566778899aabbccddeeff", FixedRandom());
        const string expect = "--01ni8d9JxziWOkUzGW0L-8zC7JA6zYlHfdtB11Kqv1LGpYA5BSlmTHHzkeO69rd4w9vSXz1Ot3AGwCrCZijM4fW4EnFabufOS9aSPwM3tTvUgz2xmUxLZUxqZ43Em5BwG0XOIBgvmtV3z0t98hGlDNK7u_OM7oqijAQd2-96nIv0KqaKkTlddVXPNEkdPc5HI9ylUHlZ5hr1-TAy6Ccdr6BT8ApN7sUDYgHylKL6TSuCpafdMLaZJlLJFEj_G4kOcIb8n8kyX-eh0LWAyowRPLh8NxX6ZI2d3ZLWib600I3cziSsDYqSiSJadsyGsPr8Ai-zZ5-0Pi0kozWi5JB3zDmdYJSFpRdWRTlqna4V5gc7P1JUcU1NZK4S66UE2L12MMqjDgJ4qTectmzzns2mx7hG5Z2xaIeePLPPn4KObpPzqeOFljcPcDFf3z5qfvd5TDXvH35kGjWPgT7Yb-D1bQsflwoAk";
        Assert.Equal(expect, ck);
    }

    [Fact]
    public void Ysp_ticket_window_and_exponential_cooldown()
    {
        var svc = new YspTicketService();
        long t0 = 1794016800; // 2026-11-07 10:00 +08
        Assert.True(svc.ShouldFetch("c1", t0));
        svc.MarkFetched("c1", t0, "http://x/c1.m3u8");
        Assert.False(svc.ShouldFetch("c1", t0 + 240), "5分钟内不重取");
        Assert.True(svc.ShouldFetch("c1", t0 + 301), "5分钟窗口到期重取");
        Assert.True(svc.TryGet("c1", t0 + 240, out var hit) && hit == "http://x/c1.m3u8");
        Assert.False(svc.TryGet("c1", t0 + 301, out _));

        var now = DateTimeOffset.FromUnixTimeSeconds(t0).DateTime;
        svc.MarkCooldown("c2", now);
        Assert.True(svc.InCooldown("c2", now.AddMinutes(1)));
        Assert.False(svc.InCooldown("c2", now.AddMinutes(2).AddSeconds(1)), "首败退避约2分钟到期放行");
        svc.MarkCooldown("c2", now);
        Assert.True(svc.InCooldown("c2", now.AddMinutes(3)) && !svc.InCooldown("c2", now.AddMinutes(4).AddSeconds(1)), "连败退避加深至4分钟");
        svc.MarkCooldown("c2", now);
        Assert.True(svc.InCooldown("c2", now.AddMinutes(5)) && !svc.InCooldown("c2", now.AddMinutes(8).AddSeconds(1)), "三连败退避8分钟");
        svc.MarkFetched("c2", t0, "http://x/c2.m3u8");
        Assert.False(svc.InCooldown("c2", now.AddSeconds(1)), "取票成功退避全额恢复");
        Assert.True(YspTicketService.CooldownBase == TimeSpan.FromMinutes(2) && YspTicketService.CooldownMax == TimeSpan.FromHours(2));
    }

    [Fact]
    public void Cztv_auth_key_matches_field_vector()
    {
        var signed = CztvSign.SignPlayUrl("https://zwebl03.cztv.com/live/channel01720Pnew.m3u8", 1793992552, "tbck3bbata3xr3u1");
        Assert.EndsWith("?auth_key=1793992552-tbck3bbata3xr3u1-0-a5788374601837dfa197e39b6ed797a3", signed);
        Assert.Equal("https://api.example.com/x.m3u8",
            CztvSign.SignPlayUrl("https://api.example.com/x.m3u8", 1, "r"));
    }

    [Fact]
    public void Kankan_signature_outputs_32_hex()
    {
        var pars = new SortedDictionary<string, string> { ["channelId"] = "1" };
        var sig = KankanSign.Sign(pars, 1793988075, "abcdwxyz");
        Assert.Equal(32, sig.Length);
        Assert.True(sig.All(Uri.IsHexDigit));
    }

    [Fact]
    public void Jstv_sign_produces_tencent_style_url()
    {
        var (secret, txTime) = JstvSign.Sign(JstvSign.DecodedKey(), "jscs", 1794016800);
        Assert.Equal(8, txTime.Length); // hex(now+180s)
        Assert.Equal(32, secret.Length);
        Assert.StartsWith("https://lived.jstv.com/jscspro.m3u8?txSecret=", JstvSign.PlayUrl("jscs", 1794016800));
    }

    [Fact]
    public void Nmtv_xxtea_roundtrip()
    {
        var plain = Encoding.UTF8.GetBytes("Hello,NmtvXXTEA.行级匹配口径保持。");
        var enc = NmtvXxtea.Encrypt(plain);
        var dec = NmtvXxtea.Decrypt(enc);
        Assert.Equal(plain, dec);
    }

    [Fact]
    public void Nmtv_row_match_finds_stream_before_title()
    {
        // 一期「包头」根因口径：行内 data.streamUrl 位于 title 之前
        const string json = "{\"data\":[{\"aiGene\":0,\"data\":{\"streamUrl\":\"https://livestream-bt.nmtv.cn/nmtv/2358general.m3u8?ticket=1\"},\"title\":\"包头\"}]}";
        Assert.Equal("https://livestream-bt.nmtv.cn/nmtv/2358general.m3u8?ticket=1",
            NmtvRow.LocateStreamUrl(json, "包头"));
        Assert.Null(NmtvRow.LocateStreamUrl(json, "不存在"));
    }

    [Fact]
    public void Gxtv_row_match_finds_encode_m3u8_in_row()
    {
        const string json = "{\"rows\":[{\"name\":\"都市频道\",\"encodeM3u8\":\"https://liangtv.cn/x.m3u8\"},{\"name\":\"新闻频道\",\"encodeM3u8\":\"https://liangtv.cn/y.m3u8\"}]}";
        Assert.Equal("https://liangtv.cn/x.m3u8", GxtvRow.LocateStreamUrl(json, "都市频道"));
        Assert.Equal("https://liangtv.cn/y.m3u8", GxtvRow.LocateStreamUrl(json, "新闻频道"));
        Assert.Null(GxtvRow.LocateStreamUrl(json, "不在表内"));
    }

    [Fact]
    public void Channel_table_builds_full_catalog_in_numbering_order()
    {
        var channels = ChannelTable.Build();
        // 台单规模：央视频 63 + cztv 4 + gzstv 7 + kankan 8 + nmtv 10 + jstv 3 + xatv 4 + gxtv 9 + 直连 15 = 123
        Assert.Equal(123, channels.Count);
        Assert.All(channels, c => Assert.True(c.IsValid));
        // 编号顺序：分类依序（央视→CGTN→专项→省台→城市台），同分类内不乱序
        var catIndexes = channels.Select(c => Array.IndexOf(ChannelTable.CategoryOrder, c.CategoryName)).ToList();
        Assert.Equal(catIndexes, catIndexes.OrderBy(i => i).ToList());
        Assert.Equal("央视", channels[0].CategoryName);
        Assert.StartsWith("c_", channels[0].Id);
    }

    [Fact]
    public async Task Direct_endpoints_pass_through_resolver_without_network()
    {
        var resolver = new TicketResolver();
        var ep = new SourceEndpoint("http://l.cztvcloud.com/channels/lantian/SXshangyu1/720p.m3u8?zzhed", SourceKind.Cdn, 1);
        var resolved = await resolver.ResolveAsync(ep, "cd_上虞新闻综合", "上虞新闻综合");
        Assert.Equal(ep.Url, resolved);
    }

    [Fact]
    public void Device_identity_persists_atomically_and_reuses_guid()
    {
        // vc73 审查清零 P2：身份落盘走 tmp+move 原子写（断电不整文件损坏），
        // 且同目录重启复用同一 guid；写完不留 .tmp 残件
        var dir = Path.Combine(Path.GetTempPath(), "taolive-test-" + Guid.NewGuid().ToString("N"));
        DeviceIdentity.Reset();
        try
        {
            DeviceIdentity.Attach(dir);
            var guid1 = DeviceIdentity.GetGuid();
            Assert.Matches("^[0-9a-f]{32}$", guid1);
            var guid2 = DeviceIdentity.GetGuid(); // 同目录再取：复用同一身份
            Assert.Equal(guid1, guid2);
            Assert.True(File.Exists(Path.Combine(dir, "device_guid.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "device_guid.txt.tmp"))); // 原子替换后不留残件

            DeviceIdentity.Reset(); // 模拟重启（清缓存不清盘）：读盘复用
            DeviceIdentity.Attach(dir);
            Assert.Equal(guid1, DeviceIdentity.GetGuid());
        }
        finally
        {
            DeviceIdentity.Reset();
            try { Directory.Delete(dir, recursive: true); } catch { /* 测试目录清理失败不碍断言 */ }
        }
    }
}
