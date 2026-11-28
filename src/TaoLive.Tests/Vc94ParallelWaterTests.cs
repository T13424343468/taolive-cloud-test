using System;
using System.Collections.Generic;
using System.Linq;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc94 并发拉水批（老板 2026-11-26 拍板三刀）+ vc140 水位大脑改写（老板 2026-11-28 拍板「大手术
/// 一次到位」）：低档起播升档、热备预攒水深水档保留；vc94 试探并发引擎收编进灌水器后，本文件
/// 预取器段改测 <see cref="FillThrottle"/>（档位/趋势采样/被拒即缩/退避/重整，语义原样保留）。
/// 纯逻辑层全量断言（清单解析/试探引擎/升档判据）+ 会话层深水档接线断言（热备 StartFull、冷切回默认）。
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

    // ---- FillThrottle（vc140 水位大脑：试探引擎收编进灌水器） ----

    private static FillThrottle MakeThrottle(long watermarkMs = 30_000,
        SourceTemperStore? temper = null, Func<long>? mono = null)
        => new(watermarkMs, temper, mono);

    [Fact]
    public void Throttle_starts_single_lane_no_probe()
    {
        var t = MakeThrottle();
        Assert.Equal(0, t.ProbeLevel);   // 默认单路：不预开任何并发
        t.OnSample(0);                   // 首采样只记账，不升档
        Assert.Equal(0, t.ProbeLevel);
    }

    [Fact]
    public void Throttle_escalates_one_level_per_window_when_water_stalls()
    {
        long now = 0;
        long water = 10_000;
        var t = MakeThrottle(watermarkMs: 30_000, mono: () => now);

        t.OnSample(water);               // 首采样：只记账
        Assert.Equal(0, t.ProbeLevel);

        now += FillThrottle.ProbeSampleIntervalMs;
        t.OnSample(water);               // 水位不涨（持平）→ 升 1 档
        Assert.Equal(1, t.ProbeLevel);

        now += FillThrottle.ProbeSampleIntervalMs; // 观察期未满：不升
        t.OnSample(water);
        Assert.Equal(1, t.ProbeLevel);

        now += FillThrottle.EscalateCooldownMs;    // 观察期满 + 仍不涨：升 2 档（封顶）
        t.OnSample(water);
        Assert.Equal(2, t.ProbeLevel);

        now += FillThrottle.EscalateCooldownMs;    // 已封顶：不再升
        t.OnSample(water);
        Assert.Equal(FillThrottle.MaxExtraConnections, t.ProbeLevel);
    }

    [Fact]
    public void Throttle_no_escalation_when_water_rising()
    {
        long now = 0;
        long water = 5_000;
        var t = MakeThrottle(watermarkMs: 30_000, mono: () => now);
        t.OnSample(water);
        for (var i = 1; i <= 5; i++)
        {
            now += FillThrottle.ProbeSampleIntervalMs;
            water += 5_000;              // 水位在涨：单路供得上
            t.OnSample(water);
        }
        Assert.Equal(0, t.ProbeLevel);   // 好源全程单路
    }

    [Fact]
    public void Throttle_no_escalation_when_water_full()
    {
        long now = 0;
        const long water = 30_000;
        var t = MakeThrottle(watermarkMs: 30_000, mono: () => now);
        t.OnSample(water);               // 水满：收兵不升档
        now += FillThrottle.ProbeSampleIntervalMs + FillThrottle.EscalateCooldownMs;
        t.OnSample(water);
        Assert.Equal(0, t.ProbeLevel);
    }

    [Fact]
    public void Throttle_rejection_collapses_to_single_and_marks_persisted_temper()
    {
        long now = 0;
        long water = 10_000;
        var dir = Path.Combine(Path.GetTempPath(), "taolive-temper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var temper = new SourceTemperStore(dir, nowUtcMs: () => now);
            temper.Load();
            var t = MakeThrottle(watermarkMs: 30_000, temper: temper, mono: () => now);
            t.OnSample(water);
            now += FillThrottle.ProbeSampleIntervalMs;
            t.OnSample(water);           // 升 1 档
            now += FillThrottle.EscalateCooldownMs;
            t.OnSample(water);           // 升 2 档
            Assert.Equal(2, t.ProbeLevel);

            // 429/403 被拒：档位归零退回单路 + 源脾气入持久化账
            t.OnRejected("https://cdn.example.com/s1.ts");
            Assert.Equal(0, t.ProbeLevel);
            Assert.True(t.IsHostConservative("cdn.example.com"));
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
    public void Throttle_good_marks_good_ledger_and_clears_backoff()
    {
        long now = 0;
        var dir = Path.Combine(Path.GetTempPath(), "taolive-temper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var temper = new SourceTemperStore(dir, nowUtcMs: () => now);
            temper.Load();
            var t = MakeThrottle(watermarkMs: 30_000, temper: temper, mono: () => now);
            t.OnFailed("https://cdn.example.com/s1.ts");
            Assert.True(t.IsBackedOff("https://cdn.example.com/s1.ts"));
            t.OnGood("https://cdn.example.com/s1.ts"); // 试探平安：好账+1，退避清
            Assert.False(t.IsBackedOff("https://cdn.example.com/s1.ts"));
            Assert.Equal(1, temper.GoodStreakOf("cdn.example.com"));
            Assert.False(temper.IsConservative("cdn.example.com"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Throttle_non_rejected_failure_backs_off_exponentially_without_temper()
    {
        long now = 0;
        var t = MakeThrottle(watermarkMs: 30_000, mono: () => now);
        t.OnFailed("https://cdn.example.com/s1.ts"); // 普通失败：不记脾气，只退避
        Assert.False(t.IsHostConservative("cdn.example.com"));
        Assert.True(t.IsBackedOff("https://cdn.example.com/s1.ts"));

        now += FillThrottle.FailBackoffBaseMs;       // 退避 2 秒到期
        Assert.False(t.IsBackedOff("https://cdn.example.com/s1.ts"));

        t.OnFailed("https://cdn.example.com/s1.ts"); // 再败：指数翻倍（4 秒）
        now += FillThrottle.FailBackoffBaseMs;
        Assert.True(t.IsBackedOff("https://cdn.example.com/s1.ts"));
        now += FillThrottle.FailBackoffBaseMs;
        Assert.False(t.IsBackedOff("https://cdn.example.com/s1.ts"));
    }

    [Fact]
    public void Throttle_prune_backoff_keeps_valid_urls_only()
    {
        long now = 0;
        var t = MakeThrottle(watermarkMs: 30_000, mono: () => now);
        t.OnFailed("https://cdn.example.com/s1.ts");
        t.OnFailed("https://cdn.example.com/s2.ts");
        t.PruneBackoff(new[] { "https://cdn.example.com/s1.ts" }); // 同台滚动：滚出清单的作废
        Assert.True(t.IsBackedOff("https://cdn.example.com/s1.ts"));
        Assert.False(t.IsBackedOff("https://cdn.example.com/s2.ts"));
    }

    [Fact]
    public void Throttle_reset_clears_gear_and_backoff_but_not_temper()
    {
        long now = 0;
        long water = 10_000;
        var dir = Path.Combine(Path.GetTempPath(), "taolive-temper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var temper = new SourceTemperStore(dir, nowUtcMs: () => now);
            temper.Load();
            var t = MakeThrottle(watermarkMs: 30_000, temper: temper, mono: () => now);
            t.OnSample(water);
            now += FillThrottle.ProbeSampleIntervalMs;
            t.OnSample(water);           // 升 1 档
            t.OnRejected("https://cdn.example.com/s1.ts");
            t.OnFailed("https://cdn.example.com/s2.ts");
            t.Reset();                   // 换台：档位/采样/退避全清
            Assert.Equal(0, t.ProbeLevel);
            Assert.False(t.IsBackedOff("https://cdn.example.com/s2.ts"));
            Assert.True(t.IsHostConservative("cdn.example.com")); // 源脾气持久化账不清
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
