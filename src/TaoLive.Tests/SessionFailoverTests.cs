using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 备源自救扩容（W7 测试班）：多源失败→全耗尽→重试语义、Failover 事件参数、
/// 看门狗与内核错误竞态、非法源过滤。固定时钟快拍不真等。
/// </summary>
public class SessionFailoverTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void Failover_exhaustion_then_manual_retry_restarts_from_first_endpoint()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("E1");
        player.RaiseError("E2");
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Equal(1, session.FailoverCount);

        // 耗尽后壳层 OK 重试：Failed → Buffering 从首端点重新起播，计数清零
        var chRetry = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(chRetry);
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(0, session.FailoverCount);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Failover_event_carries_from_to_attempt_and_reason()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);

        FailoverEventArgs? evt = null;
        session.Failover += (_, e) => evt = e;
        player.RaiseError("NET-1");

        Assert.NotNull(evt);
        Assert.Equal("https://s/a.m3u8", evt!.From.Url);
        Assert.Equal("https://s/b.m3u8", evt.To.Url);
        Assert.Equal(1, evt.Attempt);
        Assert.Contains("player error", evt.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_timeout_on_last_endpoint_fires_all_failed_with_reason()
    {
        // vc87 进度闸：零进度看门狗超时=真死，不再吃慢连宽限，直接切线——
        // 两路全零进度时两拍内即全耗尽（坏票 6 秒循环病灶根修后的快切口径）。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer(); // 零增长：真死
        var session = new PlaybackSession(player, clock) { OpenTimeout = TimeSpan.FromSeconds(5) };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        AllSourcesFailedEventArgs? args = null;
        session.AllSourcesFailed += (_, e) => args = e;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.CheckWatchdog()); // 第一路 open 超时：零进度不进宽限（vc87 进度闸）
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.CheckWatchdog()); // 第二路也超时（不重记宽限机会）→ 全耗尽
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.NotNull(args);
        Assert.Equal("cctv1", args!.ChannelId);
        Assert.Contains("open timeout", args.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchdog_does_not_fire_after_first_frame_success()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(session.CheckWatchdog()); // Playing 态看门狗不动
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Watchdog_slow_filling_source_gets_extended_window()
    {
        // vc67 进度感知：慢灌源（内核存货在涨、到不了 3 秒首帧门槛）不被零增长判死窗切，
        // 放宽到首帧门槛×7=21 秒——vc66 真机出画基线 13-16 秒，×5=15 秒盖不住 16 秒尾巴（差 1 秒误杀）。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        player.BufferedMs = 1_000; // 缓冲在灌（>0 有增长）
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(session.CheckWatchdog()); // 判死窗 12 秒内不切，慢灌源不切
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);

        player.BufferedMs = 2_000; // 继续在灌
        clock.Advance(TimeSpan.FromSeconds(9)); // 累计 12s：有增长→慢灌窗 21s 让路
        Assert.False(session.CheckWatchdog());
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);

        clock.Advance(TimeSpan.FromSeconds(6)); // 累计 18s：6 秒无增长→零增长判死窗 12s 到点（vc86 宽限 B：先原地重试）
        Assert.True(session.CheckWatchdog());
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url); // 宽限 1/2：慢灌窗内到不了的源先原地重试

        clock.Advance(TimeSpan.FromSeconds(12)); // 累计 30s：宽限回 Buffering 清进度账，重放存货 2000ms 记新增长→慢灌窗 21s 让路
        Assert.False(session.CheckWatchdog());

        clock.Advance(TimeSpan.FromSeconds(9)); // 累计 39s：增长账变陈（>3s 无新增长）→零增长窗 12s 到点
        Assert.True(session.CheckWatchdog());
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url); // 宽限 2/2

        clock.Advance(TimeSpan.FromSeconds(12)); // 累计 51s：同口径回灌让路
        Assert.False(session.CheckWatchdog());

        clock.Advance(TimeSpan.FromSeconds(9)); // 累计 60s：预算耗尽，该切还得切
        Assert.True(session.CheckWatchdog());
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Watchdog_dead_source_cut_at_derived_window_not_three_seconds()
    {
        // vc68 三轮根修（真机实锤 3004ms≥3000ms·0ms 存货·四线全灭）：判死窗盖过内核 Media3 默认连接超时 8 秒——
        // 冷连接慢热源不再整 3 秒被处决；真死快刀走内核报错信号直判，不依赖此窗。
        // 刀二（老板 2026-11-25 九件套定稿）：判死窗 12 秒收 8 秒，流时钟查活续期（3 秒内有增长放宽慢灌窗）
        // 一起动——零进度真死 8 秒到点即切，慢灌源由续期让路不受收窗影响。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer(); // BufferedMs 恒 0：零增长
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.False(session.CheckWatchdog()); // 7 秒不切（旧 3 秒窗这里已经误杀慢热源）
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(session.CheckWatchdog()); // 满 8 秒判死窗到点：零进度不进宽限（vc87 进度闸），直接切线
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.True(session.CheckWatchdog()); // 第二路同样零进度真死 → 全耗尽
        Assert.Equal(PlaybackState.Failed, session.State);
    }

    [Fact]
    public void Watchdog_windows_stay_consistent_with_gates()
    {
        // vc66/vc67/vc68 三次同款死局的防复发闸：门槛动，窗必须跟着动——推导式+不变量断言双保险。
        Assert.True(PlaybackSession.DefaultOpenTimeout > PrefetchCurve.FirstFrameGate,
            "判死窗必须>首帧门槛：窗≤门槛起播必被误杀（刀二判死窗 8 秒仍盖住门槛 7 秒）");
        Assert.True(PlaybackSession.DefaultOpenTimeout >= TimeSpan.FromSeconds(8),
            "判死窗必须盖过内核 Media3 默认连接超时 8 秒（vc68 真机实锤；刀二收 8 秒=同值赛跑，零进度真死殊途同归都走换源）");
        Assert.True(PlaybackSession.SlowStartWindow > PlaybackSession.DefaultOpenTimeout,
            "慢灌窗必须>判死窗：慢热源要有证明时间（查活续期放宽到 21 秒，8 秒兜底只咬零进度）");
        Assert.True(PlaybackSession.SlowRecoveryWindow > PlaybackSession.SlowStartWindow,
            "重灌窗必须>慢灌窗：重灌要满 12 秒媒体，比首帧更久");
        Assert.Equal(PrefetchCurve.FirstFrameGate.TotalMilliseconds * 3,
            PlaybackSession.SlowStartWindow.TotalMilliseconds); // vc89：门槛 7s×3=21s 口径平移（原 3s×7 同值），三窗次序不打架
        Assert.Equal(PrefetchCurve.OpenTimeout, PlaybackSession.DefaultOpenTimeout); // 刀二：12 秒收 8 秒（老板 2026-11-25 九件套定稿）
    }

    [Fact]
    public void Fast_drain_weak_line_cuts_in_three_seconds_on_reconnect()
    {
        // vc89 干库秒切（老板拍板「一次平稳」）：起播头几秒就干库的弱线（从没攒满过库存）重灌期
        // 零进度 3 秒即快切备线——不撞 12 秒判死钟，「黑十来秒」病根拔除。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();  // 出画（内核桩不拦）
        player.RaiseBufferingStart(); // 干库进重灌：画面已播过
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.CheckWatchdog()); // 2 秒：窗内不切（画面还有 7 秒存货在播）
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(session.CheckWatchdog());  // 3 秒整：秒切备线，不等 12 秒
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);
    }

    [Fact]
    public void Filled_line_keeps_slow_recovery_window_on_reconnect()
    {
        // vc89 干库秒切不误伤好线：攒满过库存的线（_inventoryEverFilled=true）重灌期照原窗 24 秒，
        // 瞬时断流有自愈证明时间，3 秒快刀只打「从没灌动过」的弱线。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame(); // 先出画（预防泵只认 Playing 态）
        player.BufferedMs = BufferInventory.TargetMs; // 攒满库存档下限（7 秒）
        session.CheckProactiveFailover(); // 预防泵过账：本线路取得「攒满资格」
        player.BufferedMs = 0; // 之后真干库：零进度读数（不吃秒切刀的唯一变量=攒满资格）
        player.RaiseBufferingStart(); // 干库进重灌
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(session.CheckWatchdog()); // 好线不秒切，走原重灌窗 24 秒
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(session.CheckWatchdog()); // 满 24 秒窗照旧切
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Weak_fill_probe_prewarms_next_line_ticket()
    {
        // vc89 灌速探针（老板拍板「一次平稳」）：起播期灌速 0.1×实时（< 0.25×阈值）坐实弱线，
        // 发一次预热信号（参数=下一条备线）；同线不重发，灌速达标的线不发。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);
        var prewarmed = new List<SourceEndpoint>();
        session.WeakFillDetected += (_, ep) => prewarmed.Add(ep);

        session.CheckWatchdog(); // 起拍泵：开采样窗（探针最短采样 2 秒，窗从首拍起算）
        player.BufferedMs = 200; // 2 秒采样窗只灌进 200ms 存货=0.1×实时
        clock.Advance(TimeSpan.FromSeconds(2));
        session.CheckWatchdog();
        Assert.Single(prewarmed);
        Assert.Equal("https://s/b.m3u8", prewarmed[0].Url);

        clock.Advance(TimeSpan.FromSeconds(3));
        session.CheckWatchdog();
        Assert.Single(prewarmed); // 同线重试不重发（每线路一次）

        // 换线重记机会：切到 b 线（c 备援）后弱灌再发一次，参数跟线走
        session.SwitchLine(1);
        session.CheckWatchdog(); // 新线起拍：重开采样窗
        player.BufferedMs = 100;
        clock.Advance(TimeSpan.FromSeconds(2));
        session.CheckWatchdog();
        Assert.Equal(2, prewarmed.Count);
        Assert.Equal("https://s/c.m3u8", prewarmed[1].Url);
    }

    [Fact]
    public void Healthy_fill_probe_stays_silent()
    {
        // vc89 灌速探针不误报：灌速 2.5×实时（达标线）不发预热信号——好线不该有额外动作。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        SourceEndpoint? prewarmed = null;
        session.WeakFillDetected += (_, ep) => prewarmed = ep;

        session.CheckWatchdog(); // 起拍泵：开采样窗（与弱线例同口径，防虚跑）
        player.BufferedMs = 5_000; // 2 秒采样窗灌进 5 秒存货=2.5×实时，达标线
        clock.Advance(TimeSpan.FromSeconds(2));
        session.CheckWatchdog();
        Assert.Null(prewarmed);
    }

    [Fact]
    public void Bad_ticket_http_error_retries_same_endpoint_once_with_fresh_ticket()
    {
        // vc69 坏票不自焚（真机 00:08 实锤：缓存过期票 4xx → 4 条线数秒烧光 → 全线路不可达）：
        // HTTP 状态错先原地作废票据换新票重试一次，不烧线不切台；机会用尽后照常走备源。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        var invalidated = new List<(string ChannelId, string Url)>();
        PlaybackSession.TicketCacheInvalidator = (id, ep) => invalidated.Add((id, ep.Url));
        try
        {
            player.RaiseError("ERROR_CODE_IO_BAD_HTTP_STATUS");
            // 原地换票重试：同端点重播一次，不算切线
            Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
            Assert.Equal(new[] { "https://s/a.m3u8", "https://s/a.m3u8" }, player.PlayedUrls);
            Assert.Equal(0, session.FailoverCount);
            Assert.Equal(("cctv1", "https://s/a.m3u8"), invalidated.Single());

            // 宽限窗内（16 秒）零增长不判死——换新票+内核重连需要时间
            clock.Advance(TimeSpan.FromSeconds(15));
            Assert.False(session.CheckWatchdog());
            Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);

            // 换新票仍坏（第二次 HTTP 错）：本线路机会已用 → 正常切线
            player.RaiseError("ERROR_CODE_IO_BAD_HTTP_STATUS");
            Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
            Assert.Equal(1, session.FailoverCount);

            // 切线后机会重记：新线路的 HTTP 错再给一次原地换票
            player.RaiseError("ERROR_CODE_IO_BAD_HTTP_STATUS");
            Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
            Assert.Equal(new[] { "https://s/a.m3u8", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/b.m3u8" },
                player.PlayedUrls);
        }
        finally { PlaybackSession.TicketCacheInvalidator = null; }
    }

    [Fact]
    public void Ticket_retry_grace_keeps_watchdog_off_until_grace_ends()
    {
        // vc69 宽限窗定标：取票 5s+内核连接 8s+余量 3s=16 秒；期满了仍零增长才是真死。
        // vc87 进度闸：期满零进度不再吃慢连宽限预算（零进度=真死快切），照旧判死切线。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer(); // 零增长
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        PlaybackSession.TicketCacheInvalidator = (_, _) => { };
        try
        {
            player.RaiseError("ERROR_CODE_IO_BAD_HTTP_STATUS"); // 起播即坏票：换票宽限至 16 秒

            clock.Advance(TimeSpan.FromSeconds(15));
            Assert.False(session.CheckWatchdog()); // 15 秒：旧 12 秒窗会切，宽限窗压住
            Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(session.CheckWatchdog()); // 16 秒期满：零进度不进慢连宽限（vc87 进度闸），照旧判死切线
            Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
            Assert.Equal(1, session.FailoverCount);
        }
        finally { PlaybackSession.TicketCacheInvalidator = null; }
    }

    [Fact]
    public void Non_http_error_skips_ticket_retry_and_fails_over_directly()
    {
        // 非 HTTP 错（解码器/网络层断连等）不走坏票重试：直切备源，不白等
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        var invalidateCalls = 0;
        PlaybackSession.TicketCacheInvalidator = (_, _) => invalidateCalls++;
        try
        {
            player.RaiseError("ERROR_CODE_IO_NETWORK_CONNECTION_FAILED");
            Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
            Assert.Equal(1, session.FailoverCount);
            Assert.Equal(0, invalidateCalls);
            Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8" }, player.PlayedUrls);
        }
        finally { PlaybackSession.TicketCacheInvalidator = null; }
    }

    [Fact]
    public void Without_invalidator_wired_http_error_fails_over_directly()
    {
        // 未接作废器（如单测环境）：不启用重试，HTTP 错直切备源（既有契约不回退）
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("ERROR_CODE_IO_BAD_HTTP_STATUS");
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8" }, player.PlayedUrls);
    }

    [Fact]
    public void Timeout_class_kernel_error_gets_slow_grace_then_fails_over()
    {
        // vc86 慢源宽限 B（老板令）：内核超时类报错先原地宽限重试，预算封顶 2 次；耗尽走既有换源，
        // 真死不无限原地打转（F2 预算闸）。慢连宽限触发同步挂 SlowGraceStarted（F7「正在换信号」提示源）。
        // vc87 进度闸：宽限的前提=内核仍有进度（存货>0 或在灌）——本用例给 2s 存货模拟「慢而通」；
        // 零进度快切契约见 Zero_progress_timeout_error_skips_grace_and_fails_over。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 2_000 }; // 有进度：慢而通，准进宽限
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        var graceCount = 0;
        session.SlowGraceStarted += (_, _) => graceCount++;

        player.RaiseError("ERROR_CODE_IO_NETWORK_TIMEOUT"); // 超时类：宽限 1/2 原地重试
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(PlaybackState.Buffering, session.State);

        player.RaiseError("ERROR_CODE_IO_NETWORK_TIMEOUT"); // 宽限 2/2
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(PlaybackState.Buffering, session.State);

        player.RaiseError("ERROR_CODE_IO_NETWORK_TIMEOUT"); // 预算耗尽 → 走既有换源，真死不原地打转
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);
        Assert.Equal(2, graceCount); // F7：宽限触发即提示位信号，耗尽切线不再触发
    }

    [Fact]
    public void Zero_progress_timeout_error_skips_grace_and_fails_over()
    {
        // vc87 进度闸（老板拍板「三方案一次到位」，坏票 6 秒循环病灶根修）：
        // 零进度（零存货+3 秒内零增长）的超时类错误=真死，不进慢连宽限白等 2×24s，直接换源快切。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer(); // 零增长：源端即拒
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        var graceCount = 0;
        session.SlowGraceStarted += (_, _) => graceCount++;

        player.RaiseError("ERROR_CODE_IO_NETWORK_TIMEOUT"); // 零进度：宽限闸不放行
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url); // 直接换源
        Assert.Equal(1, session.FailoverCount);
        Assert.Equal(0, graceCount); // 全程零宽限
    }

    [Fact]
    public void Video_stall_signal_trips_proactive_failover()
    {
        // vc87 停滞信号接入（老板拍板「三方案一次到位」）：壳层「出画中 10 秒零视频帧」锚点
        // 交会话记账，滚动窗内满 2 次即预防换线（判据④），与库存/欠载/慢帧并列。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 30_000 }; // 库存足：排除其余三判据
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();

        session.NotifyVideoStall(); // 第 1 次停滞：不切
        Assert.False(session.CheckProactiveFailover());
        clock.Advance(TimeSpan.FromSeconds(10));
        session.NotifyVideoStall(); // 第 2 次停滞：坐实坏线
        Assert.True(session.CheckProactiveFailover());
        Assert.Equal(1, session.FailoverCount);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);

        // 介入后账清零：新线重新起账，老账不跟去新线
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(session.CheckProactiveFailover());
    }

    [Fact]
    public void Short_ticket_error_enters_quality_ledger_and_downweights_sort()
    {
        // vc87 票账降权（老板拍板「三方案一次到位」）：端点起播 15 秒内即报 HTTP 错=短命坏票，
        // 实测账上账（封顶 4 条每条 +250，拟口径待老板复核），排序降权不给头名。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        var book = new QualityHealthStore();
        session.QualityBook = book;
        PlaybackSession.TicketCacheInvalidator = (_, _) => { };
        try
        {
            clock.Advance(TimeSpan.FromSeconds(6)); // 坏票 6 秒即被源端拒（真机台账口径）
            player.RaiseError("ERROR_CODE_IO_BAD_HTTP_STATUS");

            var entry = book.Peek("", "cctv1", "https://s/a.m3u8"); // 账键序=EpgKey|SourceId|endpointUrl（实测账三级键口径）
            Assert.NotNull(entry);
            Assert.Equal(1, entry!.ShortTicketCount);

            // 短命票降权可见：同底账下带短票的端点排序分高于干净端点
            long dirty = QualityHealthStore.SortScore(entry, probeDead: false);
            long clean = QualityHealthStore.SortScore(book.Peek("", "cctv1", "https://s/b.m3u8"), probeDead: false);
            Assert.Equal(250, dirty - clean);
        }
        finally { PlaybackSession.TicketCacheInvalidator = null; }
    }

    [Fact]
    public void Proactive_failover_capped_per_channel_round()
    {
        // vc67 预防介入上限 + vc68 误烧闸：慢网盒子库存常态低位，预防切换每冷却窗烧一条线
        // →「出音一会又信号中断」。上限 3 次；到顶后预防退场，真卡流交看门狗与内核报错。
        // 误烧闸口径：每轮线路先攒满过线（取得资格）再跌落坐实，才准预防介入。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 5_000 }; // 库存常态低位（<7 秒线），慢网盒子实况
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1",
            "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8", "https://s/d.m3u8", "https://s/e.m3u8");
        session.ChangeChannel(ch);

        for (int i = 0; i < 3; i++)
        {
            player.RaiseFirstFrame(); // 回 Playing（每次预防切线后，攒满账重记）
            player.BufferedMs = 20_000;
            session.CheckProactiveFailover(); // 攒满过线：本线路取得预防介入资格
            player.BufferedMs = 5_000; // 跌落
            clock.Advance(TimeSpan.FromSeconds(2));
            session.CheckProactiveFailover(); // 攒第 1 拍
            clock.Advance(TimeSpan.FromSeconds(2));
            session.CheckProactiveFailover(); // 第 2 拍坐实 → 切线
            clock.Advance(TimeSpan.FromSeconds(61)); // 过冷却窗
        }

        Assert.Equal(3, session.FailoverCount);
        Assert.Equal("https://s/d.m3u8", session.CurrentEndpoint?.Url);

        // 第 4 次：预算用尽，预防退场——库存再低也不切
        player.RaiseFirstFrame();
        player.BufferedMs = 20_000;
        session.CheckProactiveFailover(); // 攒满过线（资格取得，但预算已尽）
        player.BufferedMs = 5_000;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.CheckProactiveFailover());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.CheckProactiveFailover());
        Assert.Equal("https://s/d.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Inventory_floor_couples_to_lag_knob_10s_lag_gives_5s_line()
    {
        // 老板 2026-11-17 拍板：触发线 = min(7 秒，落后目标 ÷ 2)。落后 10 秒档线=5 秒——
        // 库存 6 秒（旧 7 秒线必命中）不切，跌破 5 秒线才按两拍坐实，防贴边档降档横跳。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
            AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
            AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.DefaultPrefetchWindowSec, 10);
        try
        {
            var session = new PlaybackSession(player, clock);
            var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
            session.ChangeChannel(ch);
            player.RaiseFirstFrame(); // 回 Playing 才进预防泵

            player.BufferedMs = 6_000; // 低于旧 7 秒线、高于新 5 秒线
            session.CheckProactiveFailover();
            clock.Advance(TimeSpan.FromSeconds(2));
            session.CheckProactiveFailover();
            Assert.Equal(0, session.FailoverCount); // 线上不坐实，不预防介入

            player.BufferedMs = 4_000; // 跌破 5 秒线
            session.CheckProactiveFailover(); // 第 1 拍
            clock.Advance(TimeSpan.FromSeconds(2));
            session.CheckProactiveFailover(); // 第 2 拍坐实 → 切线
            Assert.Equal(1, session.FailoverCount);
            Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Inventory_line_naturally_disabled_at_edge_lag_zero()
    {
        // 落后 0 档（贴边）：触发线=min(7,0)=0，「水位<0」不可能=绝对线永不命中；
        // 贴边只认外推预测跌停（v2.5 蓝本）与微卡修法/看门狗，绝对线不再天天命中横跳。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 500 };
        AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
            AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
            AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.DefaultPrefetchWindowSec, 0);
        try
        {
            var session = new PlaybackSession(player, clock);
            var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
            session.ChangeChannel(ch);
            player.RaiseFirstFrame();
            for (int i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.False(session.CheckProactiveFailover());
            }
            Assert.Equal(0, session.FailoverCount);
            Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Late_error_after_exhaustion_is_ignored_until_retry()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);
        player.RaiseError("E1");
        Assert.Equal(PlaybackState.Failed, session.State);

        player.RaiseError("E-LATE"); // Failed 态不再响应内核错误（终态语义）
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Single(player.PlayedUrls);
    }

    [Fact]
    public void Change_channel_during_reconnecting_succeeds()
    {
        // BUG-1 已修（2026-11-15）：Transition 矩阵补 (Reconnecting → Idle)，
        // 卡流中间态换台不再抛——壳层上下键打在 Reconnecting 窗口安全。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8");
        session.ChangeChannel(ch1);
        player.RaiseFirstFrame();
        session.NotifyStalled();
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        session.ChangeChannel(ch2); // 不抛，正常走换台次序
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(ch2, session.CurrentChannel);
    }

    [Fact]
    public void Failover_skips_invalid_endpoints_and_keeps_order()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var sources = new[]
        {
            new SourceEndpoint("https://s/a.m3u8", SourceKind.Cdn, 3),
            new SourceEndpoint("not-a-url", SourceKind.Cdn, 2),     // 非法源应被过滤
            new SourceEndpoint("https://s/b.m3u8", SourceKind.Cdn, 1),
        };
        var ch = new Channel("cctv1", "CCTV-1", "测试", sources);
        session.ChangeChannel(ch);

        player.RaiseError("E1");
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8" }, player.PlayedUrls);
    }

    [Fact]
    public void Failover_reattaches_surface_after_switch()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.AttachSurface(new IntPtr(7));

        int attachBefore = player.Log.Count(e => e == "Attach");
        player.RaiseError("E1");
        int attachAfter = player.Log.Count(e => e == "Attach");
        Assert.True(attachAfter > attachBefore, "备源切换后必须重挂 Surface");
    }
}

/// <summary>管理器扩容：会话表复用语义、Tick 只泵当前、事件参数完整性。</summary>
public class SessionManagerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void TryGet_returns_null_for_unknown_channel()
    {
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(T0)));
        Assert.Null(manager.TryGet("cctv-unknown"));
        Assert.Null(manager.Current);
    }

    [Fact]
    public void Foreground_flag_follows_current_session()
    {
        // vc69 让道修（真机 00:07-00:09 实锤：7 个后台会话翻状态重开零后台网络门）：
        // 车道门控只认前台会话——管理器切台统一置位，让位即清（Supersede 内清旗）。
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(T0)));
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8");

        var first = manager.SwitchTo(ch1);
        Assert.True(first.IsForeground);

        var second = manager.SwitchTo(ch2);
        Assert.True(second.IsForeground);
        Assert.False(first.IsForeground); // 让位即失前台，门控从此不碰
    }

    [Fact]
    public void Same_channel_switch_during_reconnecting_succeeds()
    {
        // BUG-1 已修：Reconnecting 中同频道切回（原地重起播）不再抛，复用会话不新建。
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");

        var first = manager.SwitchTo(ch);
        first.AttachSurface(new IntPtr(1));
        manager.TryGet("cctv1")!.NotifyFirstFrame();
        manager.TryGet("cctv1")!.NotifyStalled();

        var reused = manager.SwitchTo(ch); // 不抛，复用会话原地重起播
        Assert.Same(first, reused);
        Assert.Single(players); // 不新建会话
    }

    [Fact]
    public void Tick_without_current_session_is_noop()
    {
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(T0)));
        manager.Tick(); // 不抛异常即合格
        Assert.Null(manager.Current);
    }

    [Fact]
    public void State_changed_event_carries_session_and_state()
    {
        var clock = new FixedClock(T0);
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), clock),
            new ImmediateMainThreadDispatcher());
        var events = new List<(PlaybackSession Session, PlaybackState State)>();
        manager.SessionStateChanged += (_, e) => events.Add((e.Session, e.State));

        var session = manager.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        manager.TryGet("cctv1")!.NotifyFirstFrame();

        Assert.Equal(new[] { PlaybackState.Buffering, PlaybackState.Playing },
            events.Select(e => e.State).ToList());
        Assert.All(events, e => Assert.Same(session, e.Session));
    }
}
