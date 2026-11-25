using System.Net;
using TaoLive.Catalog;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

// W8 测试归正（总镜子 R1 制度镜红②收口）：频道预检直接测生产 Session 版 ChannelPrecheck/ChannelHealth
// （vc53 引擎一比一），不再走 Catalog 死副本别名——生产类必须有直接覆盖，假覆盖清零。

namespace TaoLive.Tests;

/// <summary>
/// W8 功能批全量用例：换台去抖、清屏亮牌口径、先出画面次序、OSD 文案、
/// 手动换线路、403 退避账、源节拍政策、频道预检、预热器。
/// 固定时钟快拍不真等；预热用假 HTTP 客户端（TicketResolver.Http 静态注入）。
/// </summary>
public class W8FeatureTests
{
    // 预检探测走全局单车道（画面平稳优先令）：本类每个用例前清车道状态，
    // 防其他测试类遗留的「播放中」门控（≥10s 间隔）把探测拖进假死。
    public W8FeatureTests()
    {
        BackgroundLane.SetPlaying(false);
        BackgroundLane.SetFirstFrameLanded(true); // 首帧门控放行：测试环境无真实首帧，置 true 防车道无限等
        BackgroundLane.IdleGapOverrideMs = 0; // 预热过车道削峰后，空闲档间隔压 0 防全表预热用例被硬等待拖慢
    }

    // ---- 换台去抖（SwitchDebouncer） ------------------------------------------

    [Fact]
    public void Debouncer_not_due_inside_window()
    {
        var d = new SwitchDebouncer();
        d.Tap(nowMs: 1_000, "cctv1");
        Assert.False(d.IsDue(1_000 + SwitchDebouncer.WindowMs - 1));
        Assert.True(d.HasTarget);
        Assert.Equal("cctv1", d.Target);
    }

    [Fact]
    public void Debouncer_due_at_window_and_target_is_last_tap()
    {
        var d = new SwitchDebouncer();
        d.Tap(1_000, "cctv1");
        d.Tap(1_100, "cctv2");
        Assert.False(d.IsDue(1_100 + SwitchDebouncer.WindowMs - 1));
        Assert.True(d.IsDue(1_100 + SwitchDebouncer.WindowMs));
        Assert.Equal("cctv2", d.Target); // 目标台以最后一次按键为准
    }

    [Fact]
    public void Debouncer_clear_resets_state()
    {
        var d = new SwitchDebouncer();
        d.Tap(1_000, "cctv1");
        d.Clear();
        // 清空后无待执行目标（IsDue 只在 HasTarget 成立时有意义，一期口径逐字保留）
        Assert.False(d.HasTarget);
        Assert.Equal(string.Empty, d.Target);
    }

    // ---- 清屏亮牌口径（SwitchRevealPolicy） ------------------------------------

    [Fact]
    public void Veil_on_switch_always_reveal_only_on_first_frame()
    {
        Assert.True(SwitchRevealPolicy.ShouldVeilOnSwitch(frameEverShown: true));
        Assert.True(SwitchRevealPolicy.ShouldVeilOnSwitch(frameEverShown: false));
        Assert.False(SwitchRevealPolicy.ShouldReveal(newStreamFirstFrame: false));
        Assert.True(SwitchRevealPolicy.ShouldReveal(newStreamFirstFrame: true));
    }

    [Fact]
    public void Corruption_fallback_only_at_limit()
    {
        Assert.False(SwitchRevealPolicy.ShouldFallbackCarrier(SwitchRevealPolicy.CorruptionLimit - 1));
        Assert.True(SwitchRevealPolicy.ShouldFallbackCarrier(SwitchRevealPolicy.CorruptionLimit));
    }

    // ---- 先出画面次序（FastStartPlanner） ---------------------------------------

    [Fact]
    public void Start_order_bandwidth_ascending_height_tiebreak()
    {
        var variants = new (int Height, long Bandwidth)[]
        {
            (1080, 6_000_000), (720, 2_000_000), (1080, 2_000_000),
        };
        var order = FastStartPlanner.StartOrder(variants);
        Assert.Equal(new[] { (720, 2_000_000L), (1080, 2_000_000L), (1080, 6_000_000L) }, order);
        Assert.Equal((720, 2_000_000L), FastStartPlanner.First(variants));
    }

    [Fact]
    public void Upgrade_target_highest_and_no_op_when_top()
    {
        var variants = new (int Height, long Bandwidth)[]
        {
            (720, 2_000_000), (1080, 6_000_000),
        };
        Assert.Equal((1080, 6_000_000L), FastStartPlanner.UpgradeTarget(variants, 720));
        Assert.Null(FastStartPlanner.UpgradeTarget(variants, 1080));
        Assert.Null(FastStartPlanner.UpgradeTarget(variants[..1], 720)); // 单档不折腾
    }

    [Fact]
    public void Upgrade_failed_stays_on_current()
    {
        var variants = new (int Height, long Bandwidth)[]
        {
            (720, 2_000_000), (1080, 6_000_000),
        };
        Assert.Equal((720, 2_000_000L), FastStartPlanner.OnUpgradeFailed(variants, 720));
    }

    // ---- OSD 文案（SwitchHud） --------------------------------------------------

    [Fact]
    public void Hud_text_formats()
    {
        Assert.Equal("5  CCTV-5", SwitchHud.ChannelChanged(5, "CCTV-5"));
        Assert.Equal("地方体育", SwitchHud.ChannelChanged(0, "地方体育"));
        Assert.Equal("正在换信号", SwitchHud.Connecting);
        Assert.Equal("切换到线路 2", SwitchHud.LineSwitching(1));
        Assert.Equal("信号中断，按 OK 重试", SwitchHud.AllLinesFailed);
    }

    // ---- 手动换线路（PlaybackSession.SwitchLine） --------------------------------

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void Switch_line_replays_target_endpoint()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z")));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);
        Assert.Equal(3, session.LineCount);
        Assert.Equal(0, session.CurrentLineIndex);

        session.SwitchLine(2);
        Assert.Equal(2, session.CurrentLineIndex);
        Assert.Equal("https://s/c.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(0, session.FailoverCount); // 换线路归零自救计数
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/c.m3u8" }, player.PlayedUrls);
    }

    [Fact]
    public void Switch_line_stops_old_stream_first()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z")));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame(); // Playing
        player.Log.Clear();

        session.SwitchLine(1);
        var stop = player.Log.IndexOf("Stop");
        var play = player.Log.IndexOf("Play:https://s/b.m3u8");
        Assert.True(stop >= 0 && play > stop, $"stop must precede play: {string.Join(",", player.Log)}");
    }

    [Fact]
    public void Switch_line_same_index_while_playing_is_noop()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z")));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame(); // Playing on line 0
        player.Log.Clear();

        session.SwitchLine(0); // 同线无操作
        Assert.DoesNotContain(player.Log, e => e.StartsWith("Play:", StringComparison.Ordinal));
        Assert.Equal(PlaybackState.Playing, session.State);
    }

    [Fact]
    public void Switch_line_invalid_index_throws()
    {
        var session = new PlaybackSession(new LoggingPlayer(), new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z")));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SwitchLine(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SwitchLine(-1));
    }

    [Fact]
    public void Switch_line_before_channel_throws()
    {
        var session = new PlaybackSession(new LoggingPlayer(), new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z")));
        Assert.Throws<InvalidOperationException>(() => session.SwitchLine(0));
    }

    // ---- 403 退避账（SourceBackoffLedger） ---------------------------------------

    [Fact]
    public void Backoff_freezes_source_until_expiry_then_recovers()
    {
        var ledger = new SourceBackoffLedger();
        ledger.RecordLimited("s1", nowMs: 1_000);
        Assert.True(ledger.IsFrozen("s1", 1_000));
        Assert.True(ledger.IsFrozen("s1", 1_000 + WarmupPolicy.PaceOf("s1").BackoffBaseMs - 1));
        Assert.False(ledger.IsFrozen("s1", 1_000 + WarmupPolicy.PaceOf("s1").BackoffBaseMs));
        Assert.True(ledger.RemainingMs("s1", 2_000) > 0);

        ledger.Recover("s1");
        Assert.False(ledger.IsFrozen("s1", 2_000));
        Assert.Equal(0, ledger.RemainingMs("s1", 2_000));
    }

    [Fact]
    public void Backoff_deepens_x4_per_streak_capped_at_max()
    {
        var ledger = new SourceBackoffLedger();
        ledger.RecordLimited("s1", 0);
        long first = ledger.RemainingMs("s1", 0);
        ledger.RecordLimited("s1", 0);
        long second = ledger.RemainingMs("s1", 0);
        Assert.Equal(first * 4, second);

        // 连败到封顶：×4 步进不超 BackoffMaxMs
        for (int i = 0; i < 10; i++) ledger.RecordLimited("s1", 0);
        Assert.True(ledger.RemainingMs("s1", 0) <= WarmupPolicy.PaceOf("s1").BackoffMaxMs);
    }

    [Fact]
    public void Backoff_clear_resets_all()
    {
        var ledger = new SourceBackoffLedger();
        ledger.RecordLimited("s1", 0);
        ledger.RecordLimited("s2", 0);
        ledger.Clear();
        Assert.False(ledger.IsFrozen("s1", 1));
        Assert.False(ledger.IsFrozen("s2", 1));
    }

    // ---- 源节拍政策（WarmupPolicy） -----------------------------------------------

    [Fact]
    public void Pace_of_registered_sources_matches_one_phase_table()
    {
        Assert.Equal(10 * 60_000, WarmupPolicy.PaceOf("yangshipin").RefreshIntervalMs);
        Assert.True(WarmupPolicy.PaceOf("nmtv").BatchAggregation);
        Assert.True(WarmupPolicy.PaceOf("gxtv").BatchAggregation);
        Assert.Equal(2 * 60 * 60_000, WarmupPolicy.PaceOf("cztv").RefreshIntervalMs);
        Assert.Equal(6 * 60 * 60_000, WarmupPolicy.PaceOf("xatv").RefreshIntervalMs);
        Assert.False(WarmupPolicy.PaceOf("gzstv").BatchAggregation);
    }

    [Fact]
    public void Pace_of_unregistered_source_falls_back_conservative()
    {
        var pace = WarmupPolicy.PaceOf("unknown-source");
        Assert.Equal(10 * 60_000, pace.RefreshIntervalMs);
        Assert.Equal(2_000, pace.MinGapMs);
        Assert.False(WarmupPolicy.IsBatchSource("unknown-source"));
    }

    // ---- 频道预检（生产 Session 版 ChannelPrecheck 直接覆盖） ---------------------------------------

    [Fact]
    public async Task Precheck_any_alive_source_marks_group_alive()
    {
        // 同名频道两个来源变体（不同 Id，台名一致归并同组）
        var alive = new Channel("src-a", "CCTV-1", "央视", new[]
            { new SourceEndpoint("https://a/stream", SourceKind.Cdn, 1) });
        var dead = new Channel("src-b", "CCTV-1", "央视", new[]
            { new SourceEndpoint("https://b/stream", SourceKind.Cdn, 1) });
        var precheck = new ChannelPrecheck(ch => Task.FromResult(ch.Id == "src-a"));

        await precheck.RunAsync(new[] { alive, dead });
        Assert.Equal(ChannelHealth.Alive, precheck.Of(alive));
        Assert.Equal(ChannelHealth.Alive, precheck.Of(dead)); // 任一来源通=整台可用
    }

    [Fact]
    public async Task Precheck_all_dead_sources_marks_group_dead()
    {
        var ch = new Channel("src-a", "CCTV-2", "央视",
            new[] { new SourceEndpoint("https://a/stream", SourceKind.Cdn, 1) });
        var precheck = new ChannelPrecheck(_ => Task.FromResult(false));
        await precheck.RunAsync(new[] { ch });
        Assert.Equal(ChannelHealth.Dead, precheck.Of(ch));
    }

    [Fact]
    public async Task Precheck_probe_exception_treated_as_dead()
    {
        var ch = new Channel("src-a", "CCTV-3", "央视",
            new[] { new SourceEndpoint("https://a/stream", SourceKind.Cdn, 1) });
        var precheck = new ChannelPrecheck(_ => throw new InvalidOperationException("probe blew up"));
        await precheck.RunAsync(new[] { ch });
        Assert.Equal(ChannelHealth.Dead, precheck.Of(ch));
    }

    [Fact]
    public async Task Precheck_state_change_event_fires_only_on_real_change()
    {
        var ch = new Channel("src-a", "CCTV-4", "央视",
            new[] { new SourceEndpoint("https://a/stream", SourceKind.Cdn, 1) });
        ch.EpgKey = "cctv4"; // 生产契约：StateChanged 按频道组键（EpgKey）发，订阅方据此刷列表组
        var precheck = new ChannelPrecheck(_ => Task.FromResult(true));
        var events = new List<(string, ChannelHealth)>();
        precheck.StateChanged += (id, s) => events.Add((id, s));

        precheck.MarkState(ch, ChannelHealth.Alive);
        precheck.MarkState(ch, ChannelHealth.Alive); // 重复标记不发事件
        await precheck.RunAsync(new[] { ch }, keepKnownState: true); // 复测保持已知状态：首探成功跨绿门槛（1 轮即绿）补发事件，重复标记仍不发
        Assert.Equal(new[] { ("cctv4", ChannelHealth.Alive), ("cctv4", ChannelHealth.Alive) }, events);
    }

    [Fact]
    public void Precheck_requeue_sets_checking()
    {
        var ch = new Channel("src-a", "CCTV-5", "央视",
            new[] { new SourceEndpoint("https://a/stream", SourceKind.Cdn, 1) });
        var precheck = new ChannelPrecheck(_ => Task.FromResult(true));
        precheck.MarkState(ch, ChannelHealth.Dead);
        precheck.Requeue(ch);
        Assert.Equal(ChannelHealth.Checking, precheck.Of(ch));
    }

    [Fact]
    public void Precheck_normalizes_name_for_group_merge()
    {
        Assert.Equal(ChannelPrecheck.NormalizeName("CCTV-1 综合频道"), ChannelPrecheck.NormalizeName("CCTV-1　综合频道"));
        Assert.Equal(ChannelPrecheck.NormalizeName("CCTV-1综合频道"), ChannelPrecheck.NormalizeName("CCTV-1 综合频道"));
    }

    // ---- 预热器（ChannelWarmer，假 HTTP 快拍） --------------------------------------

    private sealed class FakeHttp(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage JsonBody(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };

    [Fact]
    public async Task Warmer_prewarms_web_endpoint_and_recovers_backoff()
    {
        var previous = TicketResolver.Http;
        try
        {
            var fake = new FakeHttp(JsonBody("{\"url\":\"https://stream.example.com/live.m3u8\"}"));
            TicketResolver.Http = new HttpClient(fake);
            var ch = new Channel("gz1", "贵州卫视", "省台", new[]
            {
                new SourceEndpoint("https://api.gzstv.com/v1/tv/1", SourceKind.Web, 1),
            });
            var warmer = new ChannelWarmer(new TicketResolver()) { MinGapOverrideMs = 0 };
            var ok = await warmer.WarmChannelAsync(ch);
            Assert.True(ok);
            Assert.Equal(1, fake.Calls);
            Assert.False(warmer.Backoff.IsFrozen(ChannelWarmer.SourceKeyOf(ch.Sources[0]), Environment.TickCount64));
        }
        finally
        {
            TicketResolver.Http = previous;
        }
    }

    [Fact]
    public async Task Warmer_marks_backoff_on_resolution_failure_and_skips_while_frozen()
    {
        var previous = TicketResolver.Http;
        try
        {
            var fake = new FakeHttp(new HttpResponseMessage(HttpStatusCode.Forbidden));
            TicketResolver.Http = new HttpClient(fake);
            var ch = new Channel("gz1", "贵州卫视", "省台", new[]
            {
                new SourceEndpoint("https://api.gzstv.com/v1/tv/1", SourceKind.Web, 1),
            });
            var warmer = new ChannelWarmer(new TicketResolver()) { MinGapOverrideMs = 0 };
            var sourceKey = ChannelWarmer.SourceKeyOf(ch.Sources[0]);

            var okFirst = await warmer.WarmChannelAsync(ch);
            Assert.False(okFirst);
            var nowMs = Environment.TickCount64;
            Assert.True(warmer.Backoff.IsFrozen(sourceKey, nowMs));

            // 退避期内再预热：直接跳过，不烧请求
            var callsBefore = fake.Calls;
            await warmer.WarmChannelAsync(ch);
            Assert.Equal(callsBefore, fake.Calls);
        }
        finally
        {
            TicketResolver.Http = previous;
        }
    }

    [Fact]
    public async Task Warmer_skips_direct_endpoints_without_http()
    {
        var ch = new Channel("cd-x", "地方台", "城市台", new[]
        {
            new SourceEndpoint("https://192.168.1.10/live.m3u8", SourceKind.PureIp, 1),
        });
        var warmer = new ChannelWarmer(new TicketResolver()) { MinGapOverrideMs = 0 };
        var ok = await warmer.WarmChannelAsync(ch);
        Assert.True(ok); // 直连原样可播，无需换票，视为就绪
    }

    [Fact]
    public async Task Warmer_warm_all_walks_whole_catalog_without_repeat_burn()
    {
        var previous = TicketResolver.Http;
        try
        {
            var fake = new FakeHttp(JsonBody("{\"url\":\"https://stream.example.com/live.m3u8\"}"));
            TicketResolver.Http = new HttpClient(fake);
            var warmer = new ChannelWarmer(new TicketResolver()) { MinGapOverrideMs = 0 };
            var catalog = ChannelTable.Build();
            Assert.True(catalog.Count >= 100); // 台单 123 台规模

            await warmer.WarmAllAsync(catalog);
            var callsAfterFirstPass = fake.Calls;
            Assert.True(callsAfterFirstPass > 0); // 首轮确实发起了预热

            // 第二轮：成功源命中 5 分钟缓存、失败源被退避账挂起——零新增请求（预热不烧重复请求）
            await warmer.WarmAllAsync(catalog);
            Assert.Equal(callsAfterFirstPass, fake.Calls);
        }
        finally
        {
            TicketResolver.Http = previous;
        }
    }

    [Fact]
    public async Task Warmer_enters_background_lane_and_waits_turn()
    {
        // 削峰锚（老板令 2026-11-16 19:40「一个通道排队削峰」）：预热取票进全局后台单车道排队，
        // 车道被占（预检轮/邻台预取在飞）时预热不得插队，释放后才继续——三路合流不抢网。
        var previous = TicketResolver.Http;
        try
        {
            var fake = new FakeHttp(JsonBody("{\"url\":\"https://stream.example.com/live.m3u8\"}"));
            TicketResolver.Http = new HttpClient(fake);
            var ch = new Channel("gz1", "贵州卫视", "省台", new[]
            {
                new SourceEndpoint("https://api.gzstv.com/v1/tv/1", SourceKind.Web, 1),
            });

            var occupierStarted = new TaskCompletionSource();
            var releaseOccupier = new TaskCompletionSource();
            var occupier = BackgroundLane.RunAsync(async () =>
            {
                occupierStarted.SetResult(); // 占住车道执行权（模拟预检轮在飞）
                await releaseOccupier.Task;
            });
            await occupierStarted.Task;

            var warmer = new ChannelWarmer(new TicketResolver()) { MinGapOverrideMs = 0 };
            var warmTask = warmer.WarmChannelAsync(ch); // 车道被占：预热必须排队
            await Task.Delay(50);
            Assert.False(warmTask.IsCompleted, "车道被占时预热插队=削峰失效（三路合流回归锚）");

            releaseOccupier.SetResult();
            await occupier;
            Assert.True(await warmTask); // 车道释放后预热照常完成
        }
        finally
        {
            TicketResolver.Http = previous;
        }
    }

    [Fact]
    public async Task SourceHealth_stale_after_24h_reads_as_unknown_and_reprobes()
    {
        // 老板令 2026-11-15：健康账条目超 24 小时未核验=作废——读作未探测，交回预检重探
        var ch = new Channel("src-a", "CCTV-4", "央视",
            new[] { new SourceEndpoint("https://a/stream", SourceKind.Cdn, 1) });
        var precheck = new ChannelPrecheck(_ => Task.FromResult(true));
        await precheck.RunAsync(new[] { ch });
        Assert.NotNull(precheck.SourceHealthOf(ch)); // 新核验账在，正常可读

        // 把核验时间改到 24 小时前：查询口作废（返回 null），交回重探口径
        var entry = precheck.SourceHealthOf(ch)!;
        entry.CheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ChannelPrecheck.StaleAfterMs - 1000;
        Assert.Null(precheck.SourceHealthOf(ch)); // 旧账作废：读作未探测

        // 重探后账面刷新，恢复可读（重探=预检循环自然覆盖，无需额外触发）
        await precheck.RunAsync(new[] { ch });
        Assert.NotNull(precheck.SourceHealthOf(ch));
    }
}
