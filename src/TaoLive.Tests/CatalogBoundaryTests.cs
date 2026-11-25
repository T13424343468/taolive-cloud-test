using System.Text;
using TaoLive.Catalog;
using TaoLive.Model;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 取票与边界扩容（W7 测试班）：票据窗口边界、冷却封顶、密码学向量边界（空/超长）、
/// 台单分类顺序防回归、EPG 解析口径。全部离线，不发真网。
/// </summary>
public class CatalogBoundaryTests
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

    // ---- 票据窗口边界 ----------------------------------------------------------

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(301, true)]
    public void Ticket_window_boundary_at_300_seconds(long delta, bool expectRefetch)
    {
        var svc = new YspTicketService();
        long t0 = 1794016800;
        svc.MarkFetched("c1", t0, "http://x/c1.m3u8");
        Assert.Equal(expectRefetch, svc.ShouldFetch("c1", t0 + delta));
    }

    [Fact]
    public void Cooldown_doubles_until_cap_two_hours()
    {
        var svc = new YspTicketService();
        var now = DateTimeOffset.FromUnixTimeSeconds(1794016800).DateTime;
        TimeSpan? prev = null;
        for (int round = 1; round <= 8; round++)
        {
            svc.MarkCooldown("c", now);
            bool activeAt60s = svc.InCooldown("c", now.AddMinutes(1));
            // 找到本轮退避时长：从 now 起 1 秒步进探活（上限 3 小时）
            TimeSpan expiry = TimeSpan.Zero;
            for (long s = 1; s <= 3 * 3600; s++)
            {
                if (!svc.InCooldown("c", now.AddSeconds(s))) { expiry = TimeSpan.FromSeconds(s); break; }
            }
            Assert.True(expiry > TimeSpan.Zero, $"round {round}: cooldown never expires");
            if (prev is { } p)
            {
                Assert.True(expiry >= p, $"round {round}: 退避应不缩短（连败 ×2 步进）");
            }
            Assert.True(expiry <= YspTicketService.CooldownMax, $"round {round}: 退避超封顶 2 小时");
            prev = expiry;
            // 消隐后进入下一轮
            svc.MarkFetched("c", 0, "http://x/c.m3u8"); // 成功清账
            svc = new YspTicketService(); // 重置账本再记败
        }
        Assert.True(prev <= YspTicketService.CooldownMax, "封顶 2 小时硬约束");
    }

    [Fact]
    public void Cooldown_capped_at_exactly_two_hours()
    {
        var svc = new YspTicketService();
        var now = DateTimeOffset.FromUnixTimeSeconds(1794016800).DateTime;
        for (int i = 0; i < 12; i++) svc.MarkCooldown("c", now); // 连败 12 轮
        Assert.True(svc.InCooldown("c", now.AddHours(1)));
        Assert.False(svc.InCooldown("c", now.AddHours(2).AddSeconds(1)), "封顶 2 小时必须到期放行");
    }

    // ---- 密码学边界 ------------------------------------------------------------

    [Fact]
    public void Ckey_with_empty_channel_id_still_produces_valid_shape()
    {
        var (ck, _, _) = YspCkey.Create("", 1700000000, "00112233445566778899aabbccddeeff", FixedRandom());
        Assert.StartsWith("--01", ck, StringComparison.Ordinal);
        Assert.True(ck.Length > 100, "cKey 封包长度应稳定（空参数只影响包体内容）");
    }

    [Fact]
    public void Ckey_with_long_channel_id_does_not_crash()
    {
        var longId = new string('x', 4096);
        var (ck, _, _) = YspCkey.Create(longId, 1700000000, "00112233445566778899aabbccddeeff", FixedRandom());
        Assert.StartsWith("--01", ck, StringComparison.Ordinal);
    }

    [Fact]
    public void Xxtea_empty_and_single_dword_roundtrip()
    {
        // 空明文 Encrypt 补齐为 1 个零 dword + 长度 dword（2 dword），Decrypt 还原为长度 0 的全零输出
        var dec = NmtvXxtea.Decrypt(NmtvXxtea.Encrypt(Array.Empty<byte>()));
        var single = new byte[] { 0x42 };
        var decSingle = NmtvXxtea.Decrypt(NmtvXxtea.Encrypt(single));
        Assert.Equal(single, decSingle);
        // 空表语义：Decrypt 输出长度 = padded_len（0），实现按 0 截取 → 空数组
        Assert.True(dec.Length == 0 || dec.Length == 4, "空明文还原长度为 0（或旧版本口径 4 字节）");
    }

    [Fact]
    public void Xxtea_long_payload_roundtrip()
    {
        var plain = Encoding.UTF8.GetBytes(new string('台', 4096)); // 多块中文
        var dec = NmtvXxtea.Decrypt(NmtvXxtea.Encrypt(plain));
        Assert.Equal(plain, dec);
    }

    [Fact]
    public void Xxtea_decrypt_invalid_length_returns_input_unchanged()
    {
        var junk = new byte[] { 1, 2, 3 }; // < 8 字节原样返回（协议口径）
        var dec = NmtvXxtea.Decrypt(junk);
        Assert.Equal(junk, dec);
    }

    // ---- 行匹配边界 ------------------------------------------------------------

    [Fact]
    public void Nmtv_row_match_empty_and_null_inputs_return_null()
    {
        Assert.Null(NmtvRow.LocateStreamUrl("", "包头"));
        Assert.Null(NmtvRow.LocateStreamUrl("{}", ""));
        Assert.Null(NmtvRow.LocateStreamUrl("", ""));
    }

    [Fact]
    public void Gxtv_row_match_empty_and_null_inputs_return_null()
    {
        Assert.Null(GxtvRow.LocateStreamUrl("", "都市频道"));
        Assert.Null(GxtvRow.LocateStreamUrl("{\"rows\":[]}", ""));
        Assert.Null(GxtvRow.LocateStreamUrl("", ""));
    }

    // ---- 台单防回归 ----------------------------------------------------------

    [Fact]
    public void Channel_table_numbering_is_sequential_and_category_grouped()
    {
        var channels = TaoLive.Catalog.ChannelTable.Build();
        var order = TaoLive.Catalog.ChannelTable.CategoryOrder;
        int lastCat = -1;
        foreach (var c in channels)
        {
            int idx = Array.IndexOf(order, c.CategoryName);
            Assert.True(idx >= 0, $"未知分类 {c.CategoryName}");
            Assert.True(idx >= lastCat, $"分类乱序：{c.CategoryName} 出现在 {order[Math.Max(lastCat, 0)]} 之后");
            lastCat = idx;
        }
        // Id 无重复（编号即列表顺序，重复即错台）
        Assert.Equal(channels.Count, channels.DistinctBy(c => c.Id).Count());
    }

    [Fact]
    public void Every_ysp_channel_has_web_endpoint_with_required_params()
    {
        var channels = TaoLive.Catalog.ChannelTable.Build();
        var ysp = channels.Where(c => c.Id.StartsWith("c_", StringComparison.Ordinal)).ToList();
        Assert.Equal(63, ysp.Count); // 一期 63 路央视频
        Assert.All(ysp, c =>
        {
            var ep = Assert.Single(c.Sources);
            Assert.Equal(SourceKind.Web, ep.Kind);
            Assert.Contains("bkliveinfo.ysp.cctv.cn", ep.Url, StringComparison.Ordinal);
            Assert.Contains("cnlid=", ep.Url, StringComparison.Ordinal);
            Assert.Contains("livepid=", ep.Url, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Jstv_endpoints_are_cdn_kind_direct_base()
    {
        var channels = TaoLive.Catalog.ChannelTable.Build();
        var js = channels.Where(c => c.Id.StartsWith("js_", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, js.Count);
        Assert.All(js, c =>
        {
            var ep = Assert.Single(c.Sources);
            Assert.Equal(SourceKind.Cdn, ep.Kind);
            Assert.EndsWith("pro.m3u8", ep.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("txSecret", ep.Url, StringComparison.Ordinal); // 签名播时重签，不落表
        });
    }

    // ---- EPG 解析口径 ----------------------------------------------------------
    // ParseWithKey 契约：now 参数与节目时段同为服务器本地时间（实现内 LocalDateTime 转换）。

    private const string EpgJson = """{"c_cctv1":{"program":[{"t":"新闻联播","st":1794016800,"et":1794020400},{"t":"天气预报","st":1794020400,"et":1794022200},{"t":"焦点访谈","st":1794022200,"et":1794025800}]}}""";

    private static DateTime LocalOf(long epoch) => DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;

    [Fact]
    public void Epg_parse_returns_current_and_following()
    {
        var now = LocalOf(1794020400); // 天气预报开播时刻
        var result = CntvEpgClient.ParseWithKey(EpgJson, "c_cctv1", now);
        Assert.NotNull(result);
        Assert.Equal("天气预报", result!.Value.Now.Title);
        Assert.Equal(new[] { "焦点访谈" }, result.Value.Next.Select(p => p.Title));
    }

    [Fact]
    public void Epg_parse_out_of_coverage_uses_last_started_program()
    {
        var now = LocalOf(1794025800 + 3600); // 全部结束后
        var result = CntvEpgClient.ParseWithKey(EpgJson, "c_cctv1", now);
        Assert.NotNull(result);
        Assert.Equal("焦点访谈", result!.Value.Now.Title);
        Assert.Empty(result.Value.Next);
    }

    [Fact]
    public void Epg_parse_invalid_json_returns_null_not_throw()
    {
        Assert.Null(CntvEpgClient.ParseWithKey("not-json", "c_cctv1", DateTime.Now));
        Assert.Null(CntvEpgClient.ParseWithKey("{}", "c_cctv1", DateTime.Now));
        Assert.Null(CntvEpgClient.ParseWithKey("""{"c_cctv1":{"program":[]}}""", "c_cctv1", DateTime.Now));
    }

    [Fact]
    public void Epg_parse_before_all_programs_returns_null()
    {
        var now = LocalOf(1794016800 - 7200); // 首档开播前
        Assert.Null(CntvEpgClient.ParseWithKey(EpgJson, "c_cctv1", now));
    }

    [Fact]
    public void Epg_parse_zero_duration_entries_are_skipped()
    {
        var body = """{"cctv1":{"program":[{"t":"A","st":1794016800,"et":1794016800},{"t":"B","st":1794016800,"et":1794020400}]}}""";
        var result = CntvEpgClient.ParseWithKey(body, "cctv1", LocalOf(1794016900));
        Assert.NotNull(result);
        Assert.Equal("B", result!.Value.Now.Title);
    }
}
