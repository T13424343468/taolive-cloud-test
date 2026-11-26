using System;
using System.Collections.Generic;
using System.Linq;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc94 并发拉水批（老板 2026-11-26 拍板三刀）：低档起播升档、热备预攒水深水档、并发拉流激进版。
/// 纯逻辑层全量断言（清单解析/预取器/升档判据）+ 会话层深水档接线断言（热备 StartFull、冷切回默认）。
/// </summary>
public class Vc94ParallelWaterTests
{
    // ---- HlsPlaylistInspector ----

    private const string MediaPlaylist = """
        #EXTM3U
        #EXT-X-TARGETDURATION:10
        #EXTINF:9.5,
        seg_001.ts
        #EXTINF:8.25,
        seg_002.ts
        #EXTINF:10.0,
        https://cdn.example.com/abs/seg_003.ts
        """;

    [Fact]
    public void Inspector_parses_extinf_and_resolves_relative_urls()
    {
        var ok = HlsPlaylistInspector.TryParse(MediaPlaylist, "https://live.example.com/hls/index.m3u8", out var segments);
        Assert.True(ok);
        Assert.Equal(3, segments.Count);
        Assert.Equal("https://live.example.com/hls/seg_001.ts", segments[0].Url);
        Assert.Equal("https://live.example.com/hls/seg_002.ts", segments[1].Url);
        Assert.Equal("https://cdn.example.com/abs/seg_003.ts", segments[2].Url); // 绝对地址原样
        Assert.Equal(9.5, segments[0].DurationSec, 3);
        Assert.Equal(8.25, segments[1].DurationSec, 3);
        Assert.Equal(10.0, segments[2].DurationSec, 3);
    }

    [Fact]
    public void Inspector_rejects_empty_and_master_for_segments()
    {
        Assert.False(HlsPlaylistInspector.TryParse(null, null, out _));
        Assert.False(HlsPlaylistInspector.TryParse("", null, out _));
        // 主清单无 EXTINF：分片表为空
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2149280,RESOLUTION=1920x1080
            hi.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=502355
            lo.m3u8
            """;
        Assert.False(HlsPlaylistInspector.TryParse(master, "https://x/a.m3u8", out _));
    }

    [Fact]
    public void Inspector_parses_variant_bandwidths_only_from_master()
    {
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2149280,RESOLUTION=1920x1080
            hi.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=502355,CODECS="avc1"
            lo.m3u8
            """;
        var ok = HlsPlaylistInspector.TryParseVariantBandwidths(master, out var bws);
        Assert.True(ok);
        Assert.Equal(new List<long> { 2149280, 502355 }, bws);

        // 媒体清单无变体：false（单档流不降档口径的数据面）
        Assert.False(HlsPlaylistInspector.TryParseVariantBandwidths(MediaPlaylist, out _));
        Assert.False(HlsPlaylistInspector.TryParseVariantBandwidths(null, out _));
    }

    // ---- StartupGear ----

    [Fact]
    public void StartupGear_lifts_on_water_or_hold()
    {
        Assert.True(StartupGear.ShouldLift(StartupGear.RampUpWaterMs, 0)); // 水位到即升
        Assert.True(StartupGear.ShouldLift(0, StartupGear.MaxHoldMs));     // 持时到兜底升
        Assert.False(StartupGear.ShouldLift(5_000, 5_000));                // 双未达：继续低档
        Assert.True(StartupGear.ShouldLift(15_001, 1));
        Assert.True(StartupGear.ShouldLift(1, 20_001));
    }

    // ---- ParallelPrefetcher ----

    private static readonly string FiveSegPlaylist = """
        #EXTM3U
        #EXTINF:5,
        https://cdn.example.com/s1.ts
        #EXTINF:5,
        https://cdn.example.com/s2.ts
        #EXTINF:5,
        https://cdn.example.com/s3.ts
        #EXTINF:5,
        https://cdn.example.com/s4.ts
        #EXTINF:5,
        https://cdn.example.com/s5.ts
        """;

    private static ParallelPrefetcher MakePrefetcher(long watermarkMs = 30_000, Func<long>? waterProbe = null, Func<long>? mono = null)
        => new(watermarkMs, waterProbe, new TemperLedger(mono));

    [Fact]
    public void Prefetcher_caps_concurrency_at_max_extra_connections()
    {
        var requested = new List<string>();
        var pf = MakePrefetcher();
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        Assert.Equal(ParallelPrefetcher.MaxExtraConnections, pf.ActiveFetches); // 封顶 2（总 3 含在播那路）
        Assert.Equal(2, requested.Count);
        // 在途未回报前不补发
        pf.Pump();
        Assert.Equal(2, requested.Count);
    }

    [Fact]
    public void Prefetcher_stops_when_water_full()
    {
        var requested = new List<string>();
        long water = 30_000; // ≥watermark：水满
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        Assert.Empty(requested); // 水满才撤：不发新
        Assert.Equal(0, pf.ActiveFetches);
        // 水落下去再泵：恢复发
        water = 0;
        pf.Pump();
        Assert.Equal(ParallelPrefetcher.MaxExtraConnections, pf.ActiveFetches);
    }

    [Fact]
    public void Prefetcher_trytake_serves_cached_and_advances_consumed()
    {
        var pf = MakePrefetcher();
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        pf.ReportFetched("https://cdn.example.com/s1.ts", new byte[] { 1, 2, 3 }, pf.Generation);
        Assert.Equal(3, pf.CacheBytes);

        Assert.True(pf.TryTake("https://cdn.example.com/s1.ts", out var data)); // 命中即服务
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        Assert.Equal(0, pf.CacheBytes); // 字节移交即出账

        Assert.False(pf.TryTake("https://cdn.example.com/s2.ts", out _)); // 未预取：走内层
        Assert.False(pf.TryTake("https://cdn.example.com/never.ts", out _)); // 清单外：不服务
    }

    [Fact]
    public void Prefetcher_rejected_marks_host_conservative_then_recovers_after_cooldown()
    {
        long now = 0;
        var requested = new List<string>();
        var pf = MakePrefetcher(mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        Assert.Equal(2, pf.ActiveFetches);

        // 429/403 被拒：记源脾气，冷却期内该 host 不再并发
        pf.ReportFailed("https://cdn.example.com/s1.ts", rejected: true, pf.Generation);
        pf.ReportFailed("https://cdn.example.com/s2.ts", rejected: true, pf.Generation);
        Assert.True(pf.IsHostConservative("cdn.example.com"));
        requested.Clear();
        pf.Pump();
        Assert.Empty(requested); // 保守期：降回单连接（零并发）

        // 冷却期过：恢复并发
        now += TemperLedger.DefaultCooldownMs + 1;
        Assert.False(pf.IsHostConservative("cdn.example.com"));
        pf.Pump();
        Assert.Equal(ParallelPrefetcher.MaxExtraConnections, pf.ActiveFetches);
    }

    [Fact]
    public void Prefetcher_non_rejected_failure_does_not_mark_conservative()
    {
        long now = 0;
        var requested = new List<string>();
        var pf = MakePrefetcher(mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        pf.ReportFailed("https://cdn.example.com/s1.ts", rejected: false, pf.Generation); // 普通失败：不记脾气
        pf.ReportFailed("https://cdn.example.com/s2.ts", rejected: false, pf.Generation);
        Assert.False(pf.IsHostConservative("cdn.example.com"));
        requested.Clear();
        pf.Pump();
        Assert.Equal(ParallelPrefetcher.MaxExtraConnections, pf.ActiveFetches); // 照常补发
    }

    [Fact]
    public void Prefetcher_generation_invalidates_inflight_and_stale_cache()
    {
        var requested = new List<string>();
        var pf = MakePrefetcher();
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        var gen1 = pf.Generation;
        pf.Pump();
        pf.ReportFetched("https://cdn.example.com/s1.ts", new byte[] { 9 }, gen1);
        Assert.True(pf.TryTake("https://cdn.example.com/s1.ts", out _));

        // 换代：旧清单分片 URL 全换
        const string next = """
            #EXTM3U
            #EXTINF:5,
            https://cdn.example.com/n1.ts
            #EXTINF:5,
            https://cdn.example.com/n2.ts
            """;
        requested.Clear();
        Assert.True(pf.UpdatePlaylist(next, null));
        Assert.NotEqual(gen1, pf.Generation);

        // 旧代次回报：作废不入账
        pf.ReportFetched("https://cdn.example.com/s2.ts", new byte[] { 8 }, gen1);
        Assert.False(pf.TryTake("https://cdn.example.com/s2.ts", out _));
        Assert.Equal(0, pf.CacheBytes); // 旧缓存按新清单重整已清

        // 新清单照常排片
        pf.Pump();
        Assert.Equal(ParallelPrefetcher.MaxExtraConnections, pf.ActiveFetches);
        Assert.All(requested.Take(2), u => Assert.StartsWith("https://cdn.example.com/n", u));
    }

    [Fact]
    public void Prefetcher_cache_byte_cap_stops_collection()
    {
        var requested = new List<string>();
        var pf = MakePrefetcher();
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        // 顶满字节硬顶：回报按顶满弃，不再收新片
        pf.ReportFetched("https://cdn.example.com/s1.ts", new byte[ParallelPrefetcher.CacheByteCap], pf.Generation);
        Assert.Equal(ParallelPrefetcher.CacheByteCap, pf.CacheBytes);
        requested.Clear();
        pf.Pump();
        Assert.Empty(requested); // 顶满停发
    }

    [Fact]
    public void Prefetcher_effective_water_counts_probe_and_cache_ahead()
    {
        long probe = 10_000;
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => probe);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        pf.ReportFetched("https://cdn.example.com/s1.ts", new byte[] { 1 }, pf.Generation);
        pf.ReportFetched("https://cdn.example.com/s2.ts", new byte[] { 2 }, pf.Generation);
        // 探针 10s + 缓存领先 2 片×5s=20s
        Assert.Equal(20_000, pf.EffectiveWaterMs);
        // 消费一片：领先量掉 5s
        pf.NotifyConsumed("https://cdn.example.com/s1.ts");
        Assert.Equal(15_000, pf.EffectiveWaterMs);
    }

    // ---- 会话层深水档接线 ----

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources) { EpgKey = id };
    }

    private sealed class MonoClock
    {
        public long Ms;
        public long Now() => Ms;
        public void Advance(long ms) => Ms += ms;
    }

    [Fact]
    public void PrepareStandby_sets_startfull_gate_on_both_paths()
    {
        var mono = new MonoClock();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var players = new List<CoverageSprintC1.SprintPlayer>();
        var mgr = new PlaybackSessionManager(
            () => { var p = new CoverageSprintC1.SprintPlayer(); players.Add(p); return new PlaybackSession(p, clock); },
            hotStandbyEnabled: true,
            monotonicMs: mono.Now);

        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        Assert.Equal(BufferInventory.Default, players[0].GateModes.Single()); // 冷切前台：显式回默认档（防残档外溢口径）

        // 新建路径：热备攒流即 StartFull
        var standby = mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.NotNull(standby);
        Assert.Equal(BufferInventory.Mode.StartFull, players[1].GateModes.Single());

        // 复用路径：换备目标再备回 cctv2，走会话表复用，仍 StartFull
        mgr.PrepareStandby(MakeChannel("cctv3", "https://s/c.m3u8"));
        mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.Equal(2, players[1].GateModes.Count);
        Assert.All(players[1].GateModes, m => Assert.Equal(BufferInventory.Mode.StartFull, m));
    }

    [Fact]
    public void Cold_switch_resets_gate_to_default()
    {
        var mono = new MonoClock();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var players = new List<CoverageSprintC1.SprintPlayer>();
        var mgr = new PlaybackSessionManager(
            () => { var p = new CoverageSprintC1.SprintPlayer(); players.Add(p); return new PlaybackSession(p, clock); },
            hotStandbyEnabled: true,
            monotonicMs: mono.Now);

        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8")); // 备台拿到 StartFull
        Assert.Equal(BufferInventory.Mode.StartFull, players[1].GateModes.Single());

        // 热备未就绪（桩不 raise 事件，State=Buffering）→ 冷切：残档外溢被拦，回默认档
        mgr.SwitchTo(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.Equal(BufferInventory.Default, players[1].GateModes.Last());
    }

    [Fact]
    public void Standby_accumulate_window_widened_to_15s()
    {
        // vc94 深水档批：窗口 7→15 秒（门槛 7 秒+下载余量）
        Assert.Equal(TimeSpan.FromSeconds(15), PlaybackSessionManager.StandbyAccumulateWindow);
    }
}
