using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 收口批①：75 条备源队列注入会话（vc97 老板令 2026-11-27 剔除全部海外路）+ 稳定后回主源（迟滞判据）+ 活性按健康账轻排序（同台活源在前）。
/// 固定时钟快拍不真等；回主源计时按注入单调钟推进。
/// </summary>
public class FallbackQueueTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    // ---- 活性按健康账轻排序（收口批①+v28：同台活源在前、账新者在前，只动同台内排序） ----

    [Fact]
    public void FallbackRegistry_table_anchors_75_entries()
    {
        // R2-27 后补锚（99f7e69 头注承诺「测试断言 Table.Length==84 锚定」此前无实体断言）：防表漂移——收录须按老板令增删并同步此锚
        // vc97 老板令 2026-11-27「把外网的备用源全部去掉，不能有外网」：84→75（剔除 74.91×6、63.141、107.150、antik.sk 共 9 条海外路）
        Assert.Equal(75, FallbackSourceRegistry.Table.Length);
    }

    [Fact]
    public void FromRegistry_light_sorts_alive_sources_first_by_recency()
    {
        // 注册表真实候选（新疆卫视 3 路）：按候选顺序注入健康账提示——
        // 首路无账（保持纪律序垫到活源后）、次路死账、三路活账（两路活账中账新者在前）
        var ch = new Channel("xj", "新疆卫视", "测试", new List<SourceEndpoint>());
        var urls = FallbackQueueSource.FromRegistry(ch).Select(s => s.Url).ToList();
        Assert.True(urls.Count >= 3, "注册表新疆卫视应有 ≥3 路候选");

        long at = T0.ToUnixTimeMilliseconds();
        var sorted = FallbackQueueSource.FromRegistry(ch, e => e.Url switch
        {
            var u when u == urls[0] => null,                                    // 无账：保持纪律序
            var u when u == urls[1] => new FallbackQueueSource.FallbackHealthHint(false, at),      // 失效账
            var u when u == urls[2] => new FallbackQueueSource.FallbackHealthHint(true, at + 60_000), // 活账（新）
            _ => null,
        }).Select(s => s.Url).ToList();

        // 活源（账新者）排最前；无账与死账保持原注册表纪律序
        Assert.Equal(urls[2], sorted[0]);
        Assert.Equal(urls[0], sorted[1]);
        Assert.Equal(urls[1], sorted[2]);
        if (urls.Count > 3) Assert.Equal(urls.Skip(3), sorted.Skip(3));
    }

    [Fact]
    public void FromRegistry_without_health_lookup_keeps_registry_order()
    {
        var ch = new Channel("xj", "新疆卫视", "测试", new List<SourceEndpoint>());
        var expected = FallbackSourceRegistry.GetAll("新疆卫视").Select(e => e.Url).ToList();
        var actual = FallbackQueueSource.FromRegistry(ch).Select(s => s.Url).ToList();
        Assert.Equal(expected, actual);
    }

    // ---- 备源队列吃内置表 ----

    [Fact]
    public void Change_channel_appends_registry_fallbacks_after_channel_sources()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0))
        {
            FallbackSourceProvider = ch => new[]
            {
                new SourceEndpoint($"http://10.0.0.1/{ch.Id}.m3u8", SourceKind.PureIp, 0),
                new SourceEndpoint($"http://10.0.0.2/{ch.Id}.m3u8", SourceKind.Cdn, 1),
            },
        };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        Assert.Equal(4, session.LineCount); // 本台 2 路 + 内置表 2 路
        player.RaiseError("E1");
        player.RaiseError("E2");
        Assert.Equal($"http://10.0.0.1/cctv1.m3u8", session.CurrentEndpoint?.Url); // 本台耗尽后进内置表
        player.RaiseError("E3");
        Assert.Equal($"http://10.0.0.2/cctv1.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Fallback_queue_dedupes_urls_already_in_channel()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0))
        {
            FallbackSourceProvider = ch => new[]
            {
                new SourceEndpoint("https://s/a.m3u8", SourceKind.PureIp, 0), // 与本台首路同址，不入队
                new SourceEndpoint("http://10.0.0.9/x.m3u8", SourceKind.Cdn, 1),
            },
        };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        Assert.Equal(3, session.LineCount);
    }

    [Fact]
    public void No_provider_keeps_queue_unchanged()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        Assert.Equal(2, session.LineCount);
        Assert.Empty(FallbackQueueSource.FromRegistry(MakeChannel("cctv-无登记台")));
    }

    [Fact]
    public void Registry_conversion_maps_valid_endpoints_for_registered_channel()
    {
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        var named = new Channel(ch.Id, "东方卫视", "测试", ch.SourceEndpoints); // 内置表在册台名
        var fallbacks = FallbackQueueSource.FromRegistry(named);
        Assert.NotEmpty(fallbacks); // 东方卫视在内置表有登记
        Assert.All(fallbacks, e => Assert.True(e.IsValid));
    }

    // ---- 稳定后回主源（迟滞判据）----

    private sealed class MonoClock
    {
        public long Now;
        public long Read() => Now;
    }

    private static PlaybackSessionManager MakeManager(LoggingPlayer player, MonoClock mono, bool aliveConfirmed = true) =>
        new(() => new PlaybackSession(player, new FixedClock(T0)),
            primaryAliveConfirmed: _ => aliveConfirmed,
            monotonicMs: mono.Read);

    [Fact]
    public void Returns_to_primary_after_stability_window_and_health_confirmed()
    {
        var mono = new MonoClock();
        var player = new LoggingPlayer();
        var manager = MakeManager(player, mono);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");

        var session = manager.SwitchTo(ch);
        player.RaiseError("E1"); // 断流自救 → 第二路
        manager.TryGet("cctv1")!.NotifyFirstFrame();
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);

        mono.Now += (long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds + 1;
        manager.Tick();

        Assert.Equal(0, session.CurrentLineIndex); // 已回主源
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void No_return_before_stability_window_elapses()
    {
        var mono = new MonoClock();
        var player = new LoggingPlayer();
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(player, new FixedClock(T0)),
            primaryAliveConfirmed: _ => true,
            monotonicMs: mono.Read);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var session = manager.SwitchTo(ch);
        player.RaiseError("E1");
        manager.TryGet("cctv1")!.NotifyFirstFrame();

        mono.Now += (long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds - 1000;
        manager.Tick();

        Assert.Equal(1, session.CurrentLineIndex); // 窗口未满不回
    }

    [Fact]
    public void No_return_when_health_not_confirmed()
    {
        var mono = new MonoClock();
        var player = new LoggingPlayer();
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(player, new FixedClock(T0)),
            primaryAliveConfirmed: _ => false, // 健康账连续命中 <3 次
            monotonicMs: mono.Read);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var session = manager.SwitchTo(ch);
        player.RaiseError("E1");
        manager.TryGet("cctv1")!.NotifyFirstFrame();

        mono.Now += (long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds + 1;
        manager.Tick();

        Assert.Equal(1, session.CurrentLineIndex); // 健康账未确认不回
    }

    [Fact]
    public void Stability_window_restarts_after_interruption()
    {
        var mono = new MonoClock();
        var player = new LoggingPlayer();
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(player, new FixedClock(T0)),
            primaryAliveConfirmed: _ => true,
            monotonicMs: mono.Read);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var session = manager.SwitchTo(ch);
        player.RaiseError("E1");
        manager.TryGet("cctv1")!.NotifyFirstFrame();

        // 稳定一段后断流（离开 Playing）→ 重新计时
        mono.Now += 60_000;
        manager.TryGet("cctv1")!.NotifyStalled();
        mono.Now += (long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds;
        manager.Tick();

        Assert.Equal(1, session.CurrentLineIndex); // 断流重置计时，不回
    }

    [Fact]
    public void Return_rearms_when_primary_breaks_again()
    {
        var mono = new MonoClock();
        var player = new LoggingPlayer();
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(player, new FixedClock(T0)),
            primaryAliveConfirmed: _ => true,
            monotonicMs: mono.Read);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var session = manager.SwitchTo(ch);
        player.RaiseError("E1"); // 切备源
        manager.TryGet("cctv1")!.NotifyFirstFrame();
        mono.Now += (long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds + 1;
        manager.Tick();
        Assert.Equal(0, session.CurrentLineIndex); // 已回主源

        // 回后主源再断 → 重新计时，窗口未满不回
        player.RaiseError("E2");
        manager.TryGet("cctv1")!.NotifyFirstFrame();
        mono.Now += 60_000;
        manager.Tick();
        Assert.Equal(1, session.CurrentLineIndex);

        mono.Now += (long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds;
        manager.Tick();
        Assert.Equal(0, session.CurrentLineIndex); // 窗口满且健康确认 → 再回
    }
}
