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

    // ---- ParallelPrefetcher（vc96 试探版） ----

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

    private static ParallelPrefetcher MakePrefetcher(long watermarkMs = 30_000, Func<long>? waterProbe = null,
        SourceTemperStore? temper = null, Func<long>? mono = null)
        => new(watermarkMs, waterProbe, temper, mono);

    [Fact]
    public void Prefetcher_probing_starts_single_connection_no_prefetch()
    {
        var requested = new List<string>();
        var pf = MakePrefetcher();
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        Assert.Empty(requested);      // vc96 试探版：默认单路，不预开任何并发
        Assert.Equal(0, pf.ProbeLevel);
        Assert.Equal(0, pf.ActiveFetches);
    }

    [Fact]
    public void Prefetcher_escalates_one_level_per_window_when_water_stalls()
    {
        long now = 0;
        long water = 10_000;
        var requested = new List<string>();
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));

        pf.Pump();                    // 首采样：只记账，不发
        Assert.Empty(requested);
        now += ParallelPrefetcher.ProbeSampleIntervalMs;
        pf.Pump();                    // 水位不涨（持平）→ 升 1 档：发 1 路
        Assert.Equal(1, pf.ProbeLevel);
        Assert.Equal(1, pf.ActiveFetches);
        Assert.Single(requested);

        now += ParallelPrefetcher.ProbeSampleIntervalMs; // 观察期未满：不升
        pf.Pump();
        Assert.Equal(1, pf.ProbeLevel);
        Assert.Equal(1, pf.ActiveFetches);

        now += ParallelPrefetcher.EscalateCooldownMs;    // 观察期满 + 仍不涨：升 2 档（封顶）
        pf.Pump();
        Assert.Equal(2, pf.ProbeLevel);
        Assert.Equal(2, pf.ActiveFetches);

        now += ParallelPrefetcher.EscalateCooldownMs;    // 已封顶：不再升
        pf.Pump();
        Assert.Equal(2, pf.ProbeLevel);
        Assert.Equal(2, pf.ActiveFetches);
    }

    [Fact]
    public void Prefetcher_no_escalation_when_water_rising()
    {
        long now = 0;
        long water = 5_000;
        var requested = new List<string>();
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        pf.Pump();
        for (var i = 1; i <= 5; i++)
        {
            now += ParallelPrefetcher.ProbeSampleIntervalMs;
            water += 5_000;           // 水位在涨：单路供得上
            pf.Pump();
        }
        Assert.Equal(0, pf.ProbeLevel); // 好源全程单路
        Assert.Empty(requested);
    }

    [Fact]
    public void Prefetcher_rejection_collapses_to_single_and_marks_persisted_temper()
    {
        long now = 0;
        long water = 10_000;
        var dir = Path.Combine(Path.GetTempPath(), "taolive-temper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var temper = new SourceTemperStore(dir, nowUtcMs: () => now);
            temper.Load();
            var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, temper: temper, mono: () => now);
            Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
            now += ParallelPrefetcher.ProbeSampleIntervalMs;
            pf.Pump();                // 升 1 档
            now += ParallelPrefetcher.EscalateCooldownMs;
            pf.Pump();                // 升 2 档
            Assert.Equal(2, pf.ProbeLevel);

            // 429/403 被拒：档位归零退回单路 + 源脾气入持久化账
            pf.ReportFailed("https://cdn.example.com/s1.ts", rejected: true, pf.Generation);
            Assert.Equal(0, pf.ProbeLevel);
            Assert.True(pf.IsHostConservative("cdn.example.com"));
            Assert.True(temper.IsConservative("cdn.example.com"));

            // 落盘核验：新账本实例读同一文件，保守期仍在（跨启动调度依据）
            temper.FlushNow();
            var reloaded = new SourceTemperStore(dir, nowUtcMs: () => now);
            reloaded.Load();
            Assert.True(reloaded.IsConservative("cdn.example.com"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Prefetcher_probe_success_marks_good_ledger()
    {
        long now = 0;
        long water = 10_000;
        var dir = Path.Combine(Path.GetTempPath(), "taolive-temper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var temper = new SourceTemperStore(dir, nowUtcMs: () => now);
            temper.Load();
            var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, temper: temper, mono: () => now);
            Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
            now += ParallelPrefetcher.ProbeSampleIntervalMs;
            pf.Pump();                // 升 1 档，发 1 路
            pf.ReportFetched("https://cdn.example.com/s1.ts", new byte[] { 1 }, pf.Generation);
            Assert.Equal(1, temper.GoodStreakOf("cdn.example.com")); // 试探平安：好账+1
            Assert.False(temper.IsConservative("cdn.example.com"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Prefetcher_non_rejected_failure_does_not_mark_conservative()
    {
        long now = 0;
        long water = 10_000;
        var requested = new List<string>();
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        now += ParallelPrefetcher.ProbeSampleIntervalMs;
        pf.Pump();                    // 升 1 档
        pf.ReportFailed("https://cdn.example.com/s1.ts", rejected: false, pf.Generation); // 普通失败：不记脾气不缩档
        Assert.False(pf.IsHostConservative("cdn.example.com"));
        Assert.Equal(1, pf.ProbeLevel);
        pf.Pump();
        Assert.Equal(1, pf.ActiveFetches); // 照常补发（档位内）
    }

    [Fact]
    public void Prefetcher_generation_invalidates_inflight_and_stale_cache()
    {
        long now = 0;
        long water = 10_000;
        var requested = new List<string>();
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        var gen1 = pf.Generation;
        now += ParallelPrefetcher.ProbeSampleIntervalMs;
        pf.Pump();                    // 升 1 档发 1 路
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

        // 新清单照常排片（档位仍在：换代不清试探档）；
        // 观察期满且水位不涨，按试探口径再升一档：n1 在途 + 升档补发 n2
        now += ParallelPrefetcher.EscalateCooldownMs;
        pf.Pump();
        Assert.Equal(2, pf.ActiveFetches);
        Assert.All(requested, u => Assert.StartsWith("https://cdn.example.com/n", u));
    }

    [Fact]
    public void Prefetcher_cache_byte_cap_stops_collection()
    {
        long now = 0;
        long water = 10_000;
        var requested = new List<string>();
        var pf = MakePrefetcher(watermarkMs: 30_000, waterProbe: () => water, mono: () => now);
        pf.FetchRequested += (_, r) => requested.Add(r.Url);
        Assert.True(pf.UpdatePlaylist(FiveSegPlaylist, null));
        now += ParallelPrefetcher.ProbeSampleIntervalMs;
        pf.Pump();                    // 升 1 档发 1 路
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
