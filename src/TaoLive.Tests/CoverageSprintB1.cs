using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>车道全局态护栏：任何走 BackgroundLane 的用例开工先重置，
/// 防前序用例中途失败泄漏关门态（首帧门关=后续车道用例无限等）。</summary>
internal static class LaneTestGuard
{
    public static void Open()
    {
        BackgroundLane.SetFirstFrameLanded(true);
        BackgroundLane.SetPlaying(false);
        BackgroundLane.SetWaterStable(true);
        BackgroundLane.SetDrawerOpen(false);
        BackgroundLane.IdleGapOverrideMs = 0; // 测试环境压 0：防车道间隔硬等待拖慢全测
    }
}

/// <summary>
/// 覆盖冲刺 B1（vc90 分支覆盖补账·Session 小文件组）：
/// LinkHealthLine 四项喂账与裕度合成、ChannelPrefetcher 邻台/最近台预取与 TryHost 判别、
/// QualityHealthStore 微卡窗口/实测账/盲区/坏帧/短命票/排序分、CommandReplay 解析边界。
/// 时钟注入快拍不真等；BackgroundLane 全局态用例内还原。
/// </summary>
public class CoverageSprintB1
{
    private static Channel MakeCh(string id, string streamUrl = "https://s/a.m3u8")
    {
        var ch = new Channel(id, $"频道-{id}", "测试",
            new List<SourceEndpoint> { new(streamUrl, SourceKind.Cdn, 10) });
        ch.EpgKey = id;
        return ch;
    }

    // ---- LinkHealthLine ----

    /// <summary>可手动推进的单调钟。</summary>
    private sealed class MonoClock
    {
        public long Ms;
        public long Now() => Ms;
        public void Advance(long ms) => Ms += ms;
    }

    [Fact]
    public void HealthLine_report_stock_drop_ignores_nonpositive_elapsed()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportStockDrop(bufferedNowMs: 1000, bufferedLastMs: 2000, sinceMs: 0); // 无时间差：不入账
        // 无账时裕度=0（间隔无账 0 + 吞吐无账 0 + 无坏帧），AllowFactor=0（不>0）
        Assert.Equal(0.0, line.Margin());
        Assert.Equal(0.0, line.AllowFactor());
    }

    [Fact]
    public void HealthLine_drop_rate_drives_margin_negative_and_gate_closed()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        // 2 秒水位 1 秒掉完=20000ms/s（last-now=20000ms、since=1000ms），打满负项（-0.5 权重）
        line.ReportStockDrop(1000, 21000, sinceMs: 1000);
        Assert.Equal(-0.5, line.Margin(), 6);
        Assert.Equal(0.0, line.AllowFactor()); // 裕度<0 停
    }

    [Fact]
    public void HealthLine_ema_merges_second_observation_with_decay()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportStockDrop(1000, 3000, sinceMs: 1000); // 首拍直接坐账 -20000
        clock.Advance(8000); // 半衰窗整窗：alpha≈0.5
        line.ReportStockDrop(3000, 1000, sinceMs: 1000); // 新观察 +20000（水位在涨）
        // EMA 归并后裕度应回到 0 附近（正负对冲），且严格大于首拍
        var m = line.Margin();
        Assert.InRange(m, -0.25, 0.0);
        Assert.True(m > -0.5 + 0.001, "第二拍未按 EMA 归并（应部分抵消首拍）");
    }

    [Fact]
    public void HealthLine_segment_interval_normalizes_to_margin()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportSegmentInterval(0); // 非正值不入账
        Assert.Equal(0.0, line.Margin());
        line.ReportSegmentInterval(10_000); // 2 倍切片周期：间隔项打满负项（-0.25）
        Assert.Equal(-0.25, line.Margin(), 6);
        line.ReportSegmentInterval(5_000); // 恰好基准：间隔项 0
        Assert.Equal(0.0, line.Margin(), 6);
        line.ReportSegmentInterval(3_000); // 比基准快：间隔项线性正贡献（(3000-5000)/5000=-0.4 → +0.1）
        Assert.Equal(0.1, line.Margin(), 6);
    }

    [Fact]
    public void HealthLine_throughput_bonus_clamped_and_nonpositive_ignored()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportThroughput(-5); // 负值按 0 计（无账口径）
        Assert.Equal(0.0, line.Margin());
        line.ReportThroughput(4_000); // 2 Mbps 富余打满：+0.25
        Assert.Equal(0.25, line.Margin(), 6);
        line.ReportThroughput(3_000); // 半档富余：+0.125
        Assert.Equal(0.125, line.Margin(), 6);
    }

    [Fact]
    public void HealthLine_bad_frames_negative_and_nonpositive_ignored()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportBadFrames(0); // 非正值不入账
        Assert.Equal(0.0, line.Margin());
        line.ReportBadFrames(10); // 10 帧打满负项：-0.30
        Assert.Equal(-0.30, line.Margin(), 6);
        line.ReportBadFrames(1000); // 封顶不叠加：仍 -0.30
        Assert.Equal(-0.30, line.Margin(), 6);
    }

    [Fact]
    public void HealthLine_margin_clamped_to_minus_one_on_combined_worst()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportStockDrop(0, 40_000, sinceMs: 1000); // 掉速 40000ms/s：stock 钳 -1
        line.ReportSegmentInterval(20_000); // 间隔打满
        line.ReportBadFrames(50); // 坏帧打满
        Assert.Equal(-1.0, line.Margin(), 6); // 合成钳 [-1,1]
        Assert.Equal(0.0, line.AllowFactor());
    }

    [Fact]
    public void HealthLine_gate_opens_only_on_positive_margin()
    {
        var clock = new MonoClock();
        var line = new LinkHealthLine(clock.Now);
        line.ReportThroughput(4_000); // +0.25 全速
        Assert.Equal(1.0, line.AllowFactor());
        line.ReportBadFrames(10); // -0.30 对冲后 -0.05 停
        Assert.Equal(0.0, line.AllowFactor());
    }

    // ---- ChannelPrefetcher ----

    [Fact]
    public async Task Prefetch_neighbors_null_and_empty_inputs_noop()
    {
        var calls = 0;
        var prefetcher = new ChannelPrefetcher(_ => { calls++; return Task.FromResult<ResolvedStream?>(null); });
        await prefetcher.PrefetchNeighborsAsync(null!, new List<Channel> { MakeCh("c1") }); // current null
        await prefetcher.PrefetchNeighborsAsync(MakeCh("c1"), null!); // 表 null
        await prefetcher.PrefetchNeighborsAsync(MakeCh("c1"), new List<Channel>()); // 空表
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Prefetch_neighbors_current_not_in_catalog_noop()
    {
        var calls = 0;
        var prefetcher = new ChannelPrefetcher(_ => { calls++; return Task.FromResult<ResolvedStream?>(null); });
        var catalog = new List<Channel> { MakeCh("c1"), MakeCh("c2"), MakeCh("c3") };
        await prefetcher.PrefetchNeighborsAsync(MakeCh("c9"), catalog); // 不在目录：idx<0 直接返回
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Prefetch_neighbors_head_and_tail_take_available_side_only()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(null); });
        var catalog = new List<Channel> { MakeCh("c1"), MakeCh("c2"), MakeCh("c3") };

        await prefetcher.PrefetchNeighborsAsync(catalog[0], catalog); // 头台：只有下一台
        Assert.Equal(new[] { "c2" }, prefetched.Select(c => c.Id));

        prefetched.Clear();
        await prefetcher.PrefetchNeighborsAsync(catalog[^1], catalog); // 尾台：只有上一台
        Assert.Equal(new[] { "c2" }, prefetched.Select(c => c.Id));
    }

    [Fact]
    public async Task Prefetch_neighbors_matches_by_epgkey_when_not_reference_equal()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(null); });
        var catalog = new List<Channel> { MakeCh("c1"), MakeCh("c2"), MakeCh("c3") };
        // 同 EpgKey/SourceId 的等值台（非同引用）也能定位邻台
        var twin = MakeCh("c2");
        await prefetcher.PrefetchNeighborsAsync(twin, catalog);
        // 产品口径：先取下一台再取上一台（「下一台+上一台」）
        Assert.Equal(new[] { "c3", "c1" }, prefetched.Select(c => c.Id));
    }

    [Fact]
    public async Task Prefetch_recent_take_limit_and_empty_ledger_noop()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(null); });
        var catalog = new List<Channel> { MakeCh("c1"), MakeCh("c2"), MakeCh("c3"), MakeCh("c4") };

        await prefetcher.PrefetchRecentAsync(catalog[0], catalog, new List<(string, long)>()); // 空账本空转
        Assert.Empty(prefetched);

        // take=1：只取账本最前 1 个
        var ledger = new List<(string, long)> { ("c3", 900_000), ("c4", 800_000) };
        await prefetcher.PrefetchRecentAsync(catalog[0], catalog, ledger, take: 1);
        Assert.Equal(new[] { "c3" }, prefetched.Select(c => c.Id));
    }

    [Fact]
    public async Task Prefetch_recent_skips_blank_and_duplicate_keys()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(null); });
        var catalog = new List<Channel> { MakeCh("c1"), MakeCh("c2"), MakeCh("c3") };
        var ledger = new List<(string, long)> { ("", 100), ("c2", 900_000), ("c3", 800_000) };
        await prefetcher.PrefetchRecentAsync(catalog[0], catalog, ledger);
        // 空键跳过；c2 是邻台已覆盖跳过；c3 照取
        Assert.Equal(new[] { "c3" }, prefetched.Select(c => c.Id));
    }

    [Fact]
    public async Task Prefetch_channel_null_noop_and_dns_prewarm_failure_swallowed()
    {
        LaneTestGuard.Open();
        var resolved = 0;
        var dnsCalls = 0;
        var prefetcher = new ChannelPrefetcher(
            _ => { resolved++; return Task.FromResult<ResolvedStream?>(null); },
            prewarmDns: _ => { dnsCalls++; throw new InvalidOperationException("DNS 预解析炸了也不挡预取"); });

        await prefetcher.PrefetchChannelAsync(null!); // null 台直接返回
        Assert.Equal(0, resolved);

        var ch = MakeCh("c1");
        ch.StreamUrl = "https://s/a.m3u8"; // 有流地址才有域名可预解析
        await prefetcher.PrefetchChannelAsync(ch); // DNS 抛异常被吞，取票照发
        Assert.Equal(1, dnsCalls);
        Assert.Equal(1, resolved);
    }

    [Fact]
    public async Task Prefetch_channel_uses_entry_url_when_stream_empty()
    {
        LaneTestGuard.Open();
        var seen = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { seen.Add(ch); return Task.FromResult<ResolvedStream?>(null); });
        var ch = MakeCh("c1");
        ch.StreamUrl = ""; // 无播放地址：取入口页作流地址喂 DNS
        await prefetcher.PrefetchChannelAsync(ch);
        Assert.Single(seen);
    }

    [Fact]
    public void TryHost_rejects_null_relative_and_ip_literal_urls()
    {
        Assert.False(ChannelPrefetcher.TryHost(null, out _));
        Assert.False(ChannelPrefetcher.TryHost("", out _));
        Assert.False(ChannelPrefetcher.TryHost("/relative/path", out _)); // 非绝对 URI
        Assert.False(ChannelPrefetcher.TryHost("http://10.0.0.1/live.m3u8", out _)); // IPv4 字面量免解析
        Assert.False(ChannelPrefetcher.TryHost("http://[::1]/live.m3u8", out _)); // IPv6 字面量免解析
    }

    [Fact]
    public void TryHost_extracts_host_from_domain_urls()
    {
        Assert.True(ChannelPrefetcher.TryHost("https://cdn.example.com/live.m3u8", out var host));
        Assert.Equal("cdn.example.com", host);
        Assert.True(ChannelPrefetcher.TryHost("http://LOCALhost:8080/x", out host)); // 大小写归一
        Assert.Equal("localhost", host);
    }

    // ---- QualityHealthStore ----

    private const string Epg = "cctv1";
    private const string Src = "src1";
    private const string Ep = "https://s/a.m3u8";

    [Fact]
    public void Quality_buffering_trip_requires_window_to_confirm()
    {
        var clock = new MonoClock();
        var store = new QualityHealthStore(clock.Now);
        clock.Advance(120_000); // 先推离零点：0 既是窗口清空哨兵又是合法时钟值，避免首拍歧义
        store.ReportBufferingTrip(Epg, Src, Ep);
        var e = store.Peek(Epg, Src, Ep);
        Assert.NotNull(e);
        Assert.Equal(0, e!.MicrolagCount); // 首拍只记窗口起点，不坐实

        clock.Advance(10_000); // 窗内（60s）第二拍：坐实 1 次微卡并清窗口
        store.ReportBufferingTrip(Epg, Src, Ep);
        Assert.Equal(1, e.MicrolagCount);
        Assert.Equal(0, e.MicrolagLastMs); // 坐实后清窗口

        clock.Advance(70_000); // 超窗：重新攒，不坐实
        store.ReportBufferingTrip(Epg, Src, Ep);
        Assert.Equal(1, e.MicrolagCount);
    }

    [Fact]
    public void Quality_good_blindzone_badframes_shortticket_recorded()
    {
        var utcBase = 1_700_000_000_000L;
        var store = new QualityHealthStore(utcMs: () => utcBase);
        store.ReportGood(Epg, Src, Ep);
        store.MarkBlindZone(Epg, Src, Ep);
        store.MarkBlindZone(Epg, Src, Ep); // 幂等
        store.ReportBadFrames(Epg, Src, Ep, 0); // 非正值不入账
        store.ReportBadFrames(Epg, Src, Ep, 5);
        store.ReportShortTicket(Epg, Src, Ep);
        var e = store.Peek(Epg, Src, Ep);
        Assert.NotNull(e);
        Assert.Equal(1, e!.GoodCount);
        Assert.Equal(utcBase, e.LastGoodUtcMs);
        Assert.True(e.BlindZone);
        Assert.Equal(5, e.BadFrames);
        Assert.Equal(1, e.ShortTicketCount);
    }

    [Fact]
    public void Quality_is_blindzone_url_semantics()
    {
        Assert.True(QualityHealthStore.IsBlindZoneUrl("https://s/a.ts")); // 非 m3u8 直流
        Assert.False(QualityHealthStore.IsBlindZoneUrl("https://s/A.M3U8.bak")); // 含 .m3u8 子串（大小写不敏感）即非盲区
        Assert.False(QualityHealthStore.IsBlindZoneUrl("https://s/a.m3u8"));
        Assert.False(QualityHealthStore.IsBlindZoneUrl("https://s/x.M3U8"));
        Assert.False(QualityHealthStore.IsBlindZoneUrl(""));
        Assert.False(QualityHealthStore.IsBlindZoneUrl("   "));
    }

    [Fact]
    public void Quality_sort_score_synthesis_and_null_entry()
    {
        // 无账：探活死账垫底，否则 0
        Assert.Equal(10_000, QualityHealthStore.SortScore(null, probeDead: true));
        Assert.Equal(0, QualityHealthStore.SortScore(null, probeDead: false));

        var e = new QualityHealthEntry { K = "k", BlindZone = true, MicrolagCount = 2, BadFrames = 130, ShortTicketCount = 9 };
        // 500 + 2*120=240 + min(130,120)*2=240 + min(9,4)*250=1000 = 1980
        Assert.Equal(1_980, QualityHealthStore.SortScore(e, probeDead: false));

        e.LastGoodUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // 窗内有实测成功：-80
        Assert.Equal(11_900, QualityHealthStore.SortScore(e, probeDead: true)); // 死账再 +10000
        Assert.Equal(1_900, QualityHealthStore.SortScore(e, probeDead: false));
    }

    [Fact]
    public void Quality_sort_score_stale_good_no_bonus()
    {
        var e = new QualityHealthEntry { LastGoodUtcMs = 1 }; // 远古实测：超出 10 分钟窗，不吃提权
        Assert.Equal(0, QualityHealthStore.SortScore(e, probeDead: false));
    }

    // ---- CommandReplay ----

    [Fact]
    public void Replay_parse_null_and_blank_input_yields_empty()
    {
        Assert.Empty(CommandReplay.Parse(null!));
        Assert.Empty(CommandReplay.Parse(""));
        Assert.Empty(CommandReplay.Parse("   \n  \n"));
        Assert.Empty(CommandReplay.Parse("// 纯注释\n// 第二行"));
    }

    [Fact]
    public void Replay_parse_accepts_case_insensitive_fields_and_sorts()
    {
        var steps = CommandReplay.Parse("{\"T\":300,\"CMD\":\"ok\"}\n{\"t\":100,\"cmd\":\"up\"}");
        Assert.Equal([100, 300], steps.Select(s => s.T));
        Assert.Equal("up", steps[0].Cmd);
        Assert.Null(steps[0].Arg);
    }

    [Fact]
    public void Replay_parse_rejects_null_step_and_missing_cmd()
    {
        // JSON 字面量 null → 反序列化得 null step
        Assert.Throws<FormatException>(() => CommandReplay.Parse("null"));
        // 缺 cmd
        Assert.Throws<FormatException>(() => CommandReplay.Parse("{\"t\":1}"));
        // cmd 空串
        Assert.Throws<FormatException>(() => CommandReplay.Parse("{\"t\":1,\"cmd\":\"\"}"));
    }

    [Fact]
    public void Replay_key_ids_table_anchors()
    {
        // 按键 ID 表固化随包：锚定防漂移
        Assert.Equal(8, CommandReplay.KeyIds.Count);
        Assert.Equal("ok", CommandReplay.KeyIds["KEY_OK"]);
        Assert.Equal("digit", CommandReplay.KeyIds["KEY_DIGIT"]);
    }
}
