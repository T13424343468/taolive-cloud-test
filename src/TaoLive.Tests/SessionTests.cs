using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// W3 会话层全量用例：状态迁移矩阵、换台顺序、断流备源自救、
/// 起播看门狗、多会话切换与主线程事件面。固定时钟快拍不真等。
/// </summary>
public class SessionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void Fresh_session_change_channel_plays_first_endpoint()
    {
        var session = new PlaybackSession(new LoggingPlayer(), new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(T0, session.StartedAtUtc);
        Assert.Equal(0, session.FailoverCount);
    }

    [Fact]
    public void Channel_switch_keeps_surface_attached_stop_then_play()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.AttachSurface(new IntPtr(1));
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        session.ChangeChannel(ch);
        Assert.Equal(PlaybackState.Buffering, session.State);
        var log = player.Log;
        // 面账归一（2026-11-16 冻帧方案 A）：换台不再摘面——Stop 旧源后直接起播并重挂真面，全程无 Detach
        Assert.DoesNotContain("Detach", log);
        var stop = log.IndexOf("Stop");
        Assert.True(stop >= 0, $"stop order broken: {string.Join(",", log)}");
        var play = log.FindIndex(stop + 1, e => e.StartsWith("Play:", StringComparison.Ordinal));
        Assert.True(play >= 0, $"play order broken: {string.Join(",", log)}");
        // 重挂真面：第二次换台后内核再次收到 Attach（哑面无机可乘，冻帧根因断路）
        Assert.Contains("Attach", log[(play + 1)..]);
    }

    [Fact]
    public void Detach_clears_account_so_replay_never_reattaches_dead_surface()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.AttachSurface(new IntPtr(1));
        session.NotifyFirstFrame();

        session.DetachSurface(); // 壳层纹理销毁路径：清面账（句柄清零）
        var attachCountAtDetach = player.Log.Count(e => e == "Attach");
        session.ChangeChannel(ch); // 重起播（换台同链路）
        // 死面防复活：面账已清，重起播不得再挂已销毁的纹理
        Assert.Equal(attachCountAtDetach, player.Log.Count(e => e == "Attach"));
    }

    [Fact]
    public void Player_error_fails_over_to_next_source_in_order()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("NET-1");
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);

        player.RaiseError("NET-2");
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/c.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(2, session.FailoverCount);
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8" }, player.PlayedUrls);
    }

    [Fact]
    public void Light_error_while_picture_alive_does_not_failover()
    {
        // vc82 第一刀（老板真机 2026-11-16 实锤：音画正常也被 5 秒连切 3 次）：
        // 画面在播+库存未干，内核轻错误只记账不切线——音画正常就换源是误杀好线
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        player.RaiseError("NET-1");
        player.RaiseError("NET-1");
        player.RaiseError("NET-1");
        Assert.Equal(PlaybackState.Playing, session.State); // 画面仍是裁判：不进 Failed 不换线
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(0, session.FailoverCount);
        Assert.Equal(new[] { "https://s/a.m3u8" }, player.PlayedUrls); // 内核只起播一次，无重播
    }

    [Fact]
    public void Error_while_playing_with_empty_stock_still_fails_over()
    {
        // vc82 对照面：画面虽在播（Playing 态）但库存见底——真断，照原路换源不姑息
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        player.BufferedMs = 0; // 存货耗尽
        player.RaiseError("NET-1");
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);
    }

    [Fact]
    public void Auto_failover_throttle_freezes_after_burst()
    {
        // vc82 第二刀（老板令「连环限速」）：滚动窗内自动换源满 3 次即冻结不切——
        // 把「5 秒连环快切烧线」变成「定点慢试」；冻结走空态+自愈慢轮，不再继续烧线
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8", "https://s/d.m3u8", "https://s/e.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("E1"); // →b（1）
        player.RaiseError("E2"); // →c（2）
        player.RaiseError("E3"); // →d（3，满额）
        Assert.Equal("https://s/d.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(3, session.FailoverCount);

        AllSourcesFailedEventArgs? args = null;
        session.AllSourcesFailed += (_, e) => args = e;
        player.RaiseError("E4"); // 窗内满 3 次：冻结不切 e
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Equal(3, session.FailoverCount); // 没有第 4 次快切
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8", "https://s/d.m3u8" }, player.PlayedUrls);
        Assert.NotNull(args); // 冻结走空态口径
    }

    [Fact]
    public void Throttle_cap_reads_failover_knob_cap1_freezes_after_single_failover()
    {
        // 防快切闸第 7 旋钮接通（老板 2026-11-17 拍板）：封顶次数读「防快切闸」旋钮——
        // 调到 1 次/分钟，窗内第 1 次换源后第 2 次即冻结（出厂 3 档口径由既有断言覆盖）。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
            AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
            AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.DefaultPrefetchWindowSec,
            AdvancedTuning.DefaultLiveLagSec, failoverCapPerMin: 1);
        try
        {
            var session = new PlaybackSession(player, clock);
            var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
            session.ChangeChannel(ch);

            player.RaiseError("E1"); // →b（1/1，已满额）
            Assert.Equal(PlaybackState.Buffering, session.State);
            Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
            player.RaiseError("E2"); // 窗内满 1 次：冻结不切 c
            Assert.Equal(PlaybackState.Failed, session.State);
            Assert.Equal(1, session.FailoverCount); // 没有第 2 次快切
        }
        finally
        {
            AdvancedTuning.ResetToDefaults(); // 全局旋钮还原，不污染其他测试
        }
    }

    [Fact]
    public void Failover_cap_knob_clamps_floor_one_per_minute()
    {
        // 蓝图 v2.5 全局钳制：下限 1 次/分钟调不灭，上限 10；坏调整只退化到保守行为。
        try
        {
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
                AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.DefaultPrefetchWindowSec,
                AdvancedTuning.DefaultLiveLagSec, failoverCapPerMin: 0);
            Assert.Equal(1, AdvancedTuning.FailoverCapPerMin);
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
                AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.DefaultPrefetchWindowSec,
                AdvancedTuning.DefaultLiveLagSec, failoverCapPerMin: 100);
            Assert.Equal(10, AdvancedTuning.FailoverCapPerMin);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Throttled_freeze_schedules_self_heal_slow_retry()
    {
        // vc82 冻结不装死：限速冻结后按自愈退避节奏自动慢试，不静默挂起
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8", "https://s/d.m3u8", "https://s/e.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("E1");
        player.RaiseError("E2");
        player.RaiseError("E3");
        player.RaiseError("E4"); // 冻结
        Assert.Equal(PlaybackState.Failed, session.State);

        clock.Advance(PlaybackSession.SelfHealFirstDelay);
        Assert.True(session.CheckWatchdog()); // 到点自愈：从头慢轮
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/a.m3u8", player.PlayedUrls[^1]); // 回首线慢试，不烧 e
    }

    [Fact]
    public void Self_heal_gives_up_after_cap_and_keeps_empty_state()
    {
        // vc82：自愈退避满 6 次仍全断即收摊——保持空态等按键，不无限烧
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("E1"); // 耗尽 → 自愈第 1 档 5s
        int plays = 1;
        var delays = new[] { 5, 5, 10, 20, 40, 60, 60 }; // 首排 5s + 每次重试后 5/10/20/40/60/60
        for (int i = 0; i < PlaybackSession.SelfHealMaxAttempts; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(delays[i]));
            Assert.True(session.CheckWatchdog());
            Assert.Equal(PlaybackState.Buffering, session.State);
            player.RaiseError("E-again"); // 每次重试又耗尽（单线台）
            plays++;
        }
        Assert.Equal(PlaybackSession.SelfHealMaxAttempts + 1, player.PlayedUrls.Count); // 初播 1 + 自愈 6
        clock.Advance(TimeSpan.FromSeconds(delays[^1]));
        Assert.True(session.CheckWatchdog()); // 满额后改重探间隔轻试探活（老板 2026-11-17 假旋钮接通第 2 位）
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(plays + 1, player.PlayedUrls.Count); // 轻试一次，播放账 +1
        player.RaiseError("E-reprobe"); // 轻试又耗尽（单线台，回 Failed）
        Assert.Equal(PlaybackState.Failed, session.State);
    }

    [Fact]
    public void Self_heal_reprobe_interval_couples_to_knob()
    {
        // 老板 2026-11-17 拍板（假旋钮接通第 2 位）：满额后轻试节拍读「自救重探间隔」旋钮
        // （范围 60s~30min）。默认 5 分钟——间隔内不轻试，过线即轻试。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, 120_000, // 重探 2 分钟
            AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
            AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.DefaultPrefetchWindowSec,
            AdvancedTuning.DefaultLiveLagSec);
        try
        {
            var session = new PlaybackSession(player, clock);
            var ch = MakeChannel("cctv1", "https://s/a.m3u8");
            session.ChangeChannel(ch);
            player.RaiseError("E1"); // 耗尽 → 自愈
            var delays = new[] { 5, 5, 10, 20, 40, 60, 60 };
            for (int i = 0; i < PlaybackSession.SelfHealMaxAttempts; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(delays[i]));
                session.CheckWatchdog();
                player.RaiseError("E-again");
            }
            clock.Advance(TimeSpan.FromSeconds(delays[^1]));
            // 满额后：重探间隔 2 分钟——1 分钟内不轻试，过线才轻试
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(PlaybackSession.SelfHealMaxAttempts + 1, player.PlayedUrls.Count);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.True(session.CheckWatchdog());
            Assert.Equal(PlaybackSession.SelfHealMaxAttempts + 2, player.PlayedUrls.Count); // 轻试 +1
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Self_heal_resets_when_picture_recovers()
    {
        // vc82：画面恢复即全愈，退避账清零——下次全断从第一档重新起算，不背旧账
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("E1"); // 耗尽
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.CheckWatchdog()); // 自愈第 1 次
        session.NotifyFirstFrame(); // 画面恢复
        Assert.Equal(PlaybackState.Playing, session.State);

        player.BufferedMs = 0; // 再断（库存见底=真断）
        player.RaiseError("E2"); // 耗尽，重新排自愈
        clock.Advance(TimeSpan.FromSeconds(5)); // 若账未清，下一档是 10s，此处泵不到
        Assert.True(session.CheckWatchdog()); // 清了账：5s 首档即到
        Assert.Equal(PlaybackState.Buffering, session.State);
    }

    [Fact]
    public void Light_error_account_clears_on_real_failover()
    {
        // vc82：轻错账只记本线——真断切线后账清零，不跟去新线
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();

        player.RaiseError("NET-1"); // 轻错入账
        clock.Advance(TimeSpan.FromSeconds(2));
        player.BufferedMs = 0; // 真断
        player.RaiseError("NET-1"); // 切线（轻错账随清）
        Assert.Equal(1, session.FailoverCount);

        session.NotifyFirstFrame(); // 新线回播放
        player.BufferedMs = 20_000;
        clock.Advance(TimeSpan.FromSeconds(31)); // 首线轻错账即使保留也已滑出窗
        player.RaiseError("NET-1"); // 新线轻错：新账第 1 条（若旧账未清则计为 2，打点口径即错）
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.Equal(1, session.FailoverCount); // 仍不切线
    }

    [Fact]
    public void Proactive_failover_on_repeated_stall_churn()
    {
        // 老板令 #26：滚动窗内短卡流满 3 次即预防性换线（库存充足也切），不等下一次卡死 15 秒
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        for (int i = 0; i < 2; i++) // 前两次短卡流（各自恢复）：账不够，不切
        {
            player.RaiseBufferingStart();
            player.RaiseBufferingEnd();
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.CheckProactiveFailover());
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);

        player.RaiseBufferingStart(); // 第三次：满 3 次坐实坏线
        player.RaiseBufferingEnd();
        Assert.True(session.CheckProactiveFailover());
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);

        session.NotifyFirstFrame(); // 新线回播放：欠载账已清，不跟去新线
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.CheckProactiveFailover());
        Assert.Equal(1, session.FailoverCount);
    }

    [Fact]
    public void Proactive_failover_on_sustained_smoothness_suspicion()
    {
        // vc70 慢帧流（老板真机「画面不停地抖」台账定案）：帧在上游被节流（画面每半秒一帧），
        // 探活通/库存足/带宽够/解码无错，既有自救全不触发——壳层平稳疑点攒满 3 次=坏线，预防性换源
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        for (int i = 0; i < 2; i++) // 前两次疑点：账不够，不切
        {
            session.NotifySmoothnessSuspect();
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.False(session.CheckProactiveFailover());
            Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        }

        session.NotifySmoothnessSuspect(); // 第三次：满 3 次坐实慢帧流
        Assert.True(session.CheckProactiveFailover());
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);

        session.NotifyFirstFrame(); // 新线回播放：平稳账已清，不跟去新线
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(session.CheckProactiveFailover());
        Assert.Equal(1, session.FailoverCount);
    }

    [Fact]
    public void Smoothness_suspects_outside_window_do_not_trip()
    {
        // vc70：疑点账滚动窗（90 秒）滑出即不坐实——偶发疑点不切线，宁稳勿烧
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();

        session.NotifySmoothnessSuspect();
        clock.Advance(TimeSpan.FromSeconds(91)); // 首个疑点滑出窗
        session.NotifySmoothnessSuspect();
        session.NotifySmoothnessSuspect();
        Assert.False(session.CheckProactiveFailover());
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(0, session.FailoverCount);
    }

    [Fact]
    public void Exhausted_sources_fires_all_failed_and_stays_failed()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);

        AllSourcesFailedEventArgs? args = null;
        session.AllSourcesFailed += (_, e) => args = e;
        player.RaiseError("NET-1");

        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.NotNull(args);
        Assert.Equal("cctv1", args!.ChannelId);
        Assert.Equal(0, session.FailoverCount);
    }

    [Fact]
    public void Proactive_failover_switches_line_on_sustained_low_inventory()
    {
        // 老板令 #24 + vc68 误烧闸：库存先攒满过线（线是好的）再持续跌落，才准预防性换源
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        session.CheckProactiveFailover(); // 攒满过线：本线路取得预防介入资格（vc68 误烧闸）
        player.BufferedMs = 5_000;
        Assert.False(session.CheckProactiveFailover()); // 第一拍：单次抖动不切
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);

        clock.Advance(TimeSpan.FromSeconds(2)); // 第二拍（间隔≥1 秒）仍低于线：预防性切线
        Assert.True(session.CheckProactiveFailover());
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);
        Assert.Equal(PlaybackState.Buffering, session.State);
    }

    [Fact]
    public void Proactive_never_trips_on_line_that_never_filled()
    {
        // vc68 误烧闸（老板真机报「出画一帧卡死→黑屏断连」实锤）：起播头几秒库存必然低于
        // 7 秒线（3 秒出画内核几乎没存货）——从没攒满过线的线路不准按库存线预防介入，
        // 宁稳勿烧；否则预防连烧 3 条线出「全线路不可达」。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 3_000 }; // 出画即播，库存常态低于 7 秒线
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();

        for (int i = 0; i < 30; i++)
        {
            session.CheckProactiveFailover(); // 攒账多拍
            clock.Advance(TimeSpan.FromSeconds(2));
            session.CheckProactiveFailover(); // 第 2 拍坐实也不切：线从没攒满过
        }
        Assert.Equal(0, session.FailoverCount);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Proactive_failover_resets_on_recovery_and_requires_two_spaced_samples()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 5_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();

        // 同拍内重复泵不累计（两拍须间隔≥1 秒）
        Assert.False(session.CheckProactiveFailover());
        Assert.False(session.CheckProactiveFailover());

        // 库存回线：趋势账清零，再低于线须从第一拍重新起算
        clock.Advance(TimeSpan.FromSeconds(2));
        player.BufferedMs = 20_000;
        Assert.False(session.CheckProactiveFailover());
        player.BufferedMs = 5_000;
        Assert.False(session.CheckProactiveFailover()); // 新账第一拍
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.CheckProactiveFailover());
        Assert.Equal(1, session.FailoverCount);
    }

    [Fact]
    public void Proactive_failover_skipped_without_spare_line_or_within_cooldown()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 5_000 };
        var session = new PlaybackSession(player, clock);
        // 单线频道：没有备线，预防无从介入
        var single = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(single);
        session.NotifyFirstFrame();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(session.CheckProactiveFailover());

        // 冷却窗：真实换线后 60 秒内只攒账不切，到期恢复预防介入（三线频道：报错切到第 2 线后仍有备线）
        var multi = MakeChannel("cctv2", "https://s/a2.m3u8", "https://s/b2.m3u8", "https://s/c2.m3u8");
        session.ChangeChannel(multi);
        session.NotifyFirstFrame();
        clock.Advance(TimeSpan.FromSeconds(70));
        player.BufferedMs = 0; // vc82 画面裁判令：真断（库存见底）才切线，先干库存再报错
        player.RaiseError("NET-1"); // 真实换线，冷却账起算
        session.NotifyFirstFrame(); // 新线回 Playing
        player.BufferedMs = 20_000;
        session.CheckProactiveFailover(); // 冷却窗内只攒账不切（资格未取，窗后第一拍再取）
        clock.Advance(PlaybackSession.ProactiveCooldown);
        Assert.False(session.CheckProactiveFailover()); // 到期第一拍：攒满过线取资格（vc68 误烧闸，不切）
        player.BufferedMs = 5_000;
        Assert.False(session.CheckProactiveFailover()); // 新账第一拍
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.CheckProactiveFailover()); // 第二拍坐实趋势即切
        Assert.Equal(2, session.FailoverCount);
    }

    [Fact]
    public void All_sources_failed_stops_kernel()
    {
        // vc68 耗尽必停（老板真机报「黑屏只出音」实锤）：备源烧光出空态前必须停内核——
        // 此前只进 Failed 不停机，最后一颗内核的音频照放，空态下「只出音」。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);
        var failed = false;
        session.AllSourcesFailed += (_, _) => failed = true;

        player.RaiseError("E1");

        Assert.True(failed);
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Contains("Stop", player.Log); // 渲染必须结束：空态下无内核在放
        Assert.Single(player.PlayedUrls); // 停机不重播：OK 重试从 Failed 再起
    }

    [Fact]
    public void Watchdog_open_timeout_switches_source_without_crash()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock) { OpenTimeout = TimeSpan.FromSeconds(15) };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(session.CheckWatchdog());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Watchdog_stall_timeout_fails_over_after_reconnecting()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();
        session.NotifyStalled();
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        Assert.False(session.CheckWatchdog());
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.True(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Kernel_buffering_event_wires_stall_selfheal()
    {
        // 卡流自救接线（四镜·卡顿镜 11-15 修）：内核 OnBufferingStart 在 Playing 态自动进 Reconnecting，
        // 看门狗超时后走备源自救；起播期（Buffering 态）缓冲事件不误触发。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseBufferingStart();
        Assert.Equal(PlaybackState.Buffering, session.State);

        player.RaiseFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);
        player.RaiseBufferingStart();
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.True(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Recovered_stream_returns_to_playing_without_failover()
    {
        // 恢复沿（真机反馈批#5 修 2026-11-15）：Reconnecting 中内核恢复出帧（OnBufferingEnd）即回 Playing，
        // 看门狗不误杀——此前矩阵无恢复沿，短卡流自愈后 15 秒仍被切备源（真机「断流后一直黑屏」根因）。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        player.RaiseBufferingStart(); // 播放中短卡流
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        player.RaiseBufferingEnd(); // 画面自愈恢复出帧
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.Equal(0, session.FailoverCount);
        Assert.Single(player.PlayedUrls); // 未发生换源，好画面保住

        clock.Advance(TimeSpan.FromSeconds(60)); // 看门狗泵过：Playing 态无超时账可判，不误杀
        Assert.False(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Playing, session.State);
    }

    [Fact]
    public void Superseded_session_stops_responding_to_player_errors()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.Supersede();
        Assert.Equal(PlaybackState.Idle, session.State);

        player.RaiseError("NET-LATE");
        Assert.Equal(PlaybackState.Idle, session.State);
        Assert.Single(player.PlayedUrls);
    }

    [Theory]
    [InlineData(PlaybackState.Idle, PlaybackState.Playing)]
    [InlineData(PlaybackState.Failed, PlaybackState.Playing)]
    // (Reconnecting → Idle) 自 BUG-1 修复起为合法迁移，移出非法表（SessionFailoverTests 正向覆盖）
    [InlineData(PlaybackState.Playing, PlaybackState.Buffering)]
    public void Illegal_transitions_throw(PlaybackState jumpFrom, PlaybackState jumpTo)
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");

        switch (jumpFrom)
        {
            case PlaybackState.Idle:
                break;
            case PlaybackState.Failed:
                session.Transition(PlaybackState.Failed, "test");
                break;
            case PlaybackState.Playing:
                session.ChangeChannel(ch);
                player.RaiseFirstFrame();
                break;
            case PlaybackState.Reconnecting:
                session.ChangeChannel(ch);
                player.RaiseFirstFrame();
                session.NotifyStalled();
                break;
        }

        Assert.Throws<InvalidOperationException>(() => session.Transition(jumpTo, "test"));
    }

    [Fact]
    public void Manager_switch_supersedes_previous_and_reuses_table_session()
    {
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8", "https://s/b2.m3u8");

        var first = manager.SwitchTo(ch1);
        Assert.Equal(PlaybackState.Buffering, first.State);
        Assert.Same(first, manager.Current);

        var second = manager.SwitchTo(ch2);
        Assert.Equal(PlaybackState.Idle, first.State);
        Assert.Equal(PlaybackState.Buffering, second.State);
        Assert.Same(second, manager.Current);

        var back = manager.SwitchTo(ch1);
        Assert.Same(first, back);
        Assert.Equal(PlaybackState.Buffering, back.State);
        Assert.Equal(2, manager.Sessions.Count);
        Assert.Collection(players, _ => { }, _ => { });
    }

    [Fact]
    public void Manager_events_arrive_via_dispatcher_in_order()
    {
        var clock = new FixedClock(T0);
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), clock),
            new ImmediateMainThreadDispatcher());
        var states = new List<PlaybackState>();
        var switched = new List<PlaybackSession>();
        manager.SessionStateChanged += (_, e) => states.Add(e.State);
        manager.CurrentChanged += (_, s) => switched.Add(s);

        manager.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        Assert.Equal(new[] { PlaybackState.Buffering }, states);
        Assert.Single(switched);
    }

    [Fact]
    public void Manager_tick_pumps_current_session_watchdog_only()
    {
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8", "https://s/b2.m3u8");
        manager.SwitchTo(ch1);
        manager.SwitchTo(ch2);

        clock.Advance(TimeSpan.FromSeconds(30));
        manager.Tick();
        Assert.Equal(PlaybackState.Buffering, manager.Current!.State);
        Assert.Equal("https://s/b2.m3u8", manager.Current.CurrentEndpoint?.Url);
        Assert.Equal(PlaybackState.Idle, manager.TryGet("cctv1")!.State);
    }

    [Fact]
    public void Start_replay_clears_proactive_suspicion_accounts()
    {
        // vc73 审查清零 P2：新一轮起播（含 OK 重试）重新起账——同线重试不得背走旧疑点账，
        // 否则攒够即误切（旧实现只清 _proactiveCount，_stallTimes/_smoothnessTimes 跟随新轮）
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer { BufferedMs = 20_000 };
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.NotifyFirstFrame();
        for (int i = 0; i < 3; i++) session.NotifySmoothnessSuspect(); // 旧轮疑点账攒满（若不追随即触发预防切）

        session.Supersede(); // 回 Idle，模拟壳层 OK 重试前的会话处置
        session.Start(new PlaybackParams(ch, ch.Sources[0], PreferLowLatency: false));
        session.NotifyFirstFrame();
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(session.CheckProactiveFailover()); // 新轮重新起账：旧疑点不再触发
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(0, session.FailoverCount);
    }

    [Fact]
    public void Start_with_unqueued_endpoint_registers_it_as_current_line()
    {
        // vc73 审查清零 P2：未入队端点不再「记账归 0」——实际播的就是它，插入队列首位，
        // CurrentEndpoint/CurrentLineIndex 与真实播放流一致，自救次序从原队列首继续
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var stray = new SourceEndpoint("https://s/stray.m3u8", SourceKind.Cdn, 5);
        session.Start(new PlaybackParams(ch, stray, PreferLowLatency: false));

        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/stray.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(0, session.CurrentLineIndex);

        session.NotifyFirstFrame();
        player.RaiseError("NET-1"); // 自救次序真实：下一线=原队列首
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Manager_begin_session_registers_on_playing_and_is_not_orphaned()
    {
        // vc73 审查清零 P2：Begin 是公开 API，会话取得频道（Playing）即入表——
        // 否则 Current 易主后旧会话无人停、无 LRU/TTL，孤儿内核白烧到进程退出
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8", "https://s/b2.m3u8");

        var custom = manager.Begin(); // W1 语义：裸建会话
        custom.ChangeChannel(ch1);
        Assert.Empty(manager.Sessions); // 未取得频道（未到 Playing）不入表
        custom.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, custom.State);
        Assert.Single(manager.Sessions); // 取得频道即入表

        manager.SwitchTo(ch2);
        Assert.Equal(PlaybackState.Idle, custom.State); // 易主即被接管路径让位停机，不再孤儿
        Assert.Contains("Stop", players[0].Log);
    }
}

/// <summary>记录型内核桩：把指令顺序落日志，供顺序断言；事件可手动触发。</summary>
internal sealed class LoggingPlayer : IPlayer
{
    public List<string> Log { get; } = new();
    public List<string> PlayedUrls { get; } = new();

    /// <summary>库存读数（IPlayer 契约，老板令 #24）：测试可设，模拟内核缓冲余量。</summary>
    public long BufferedMs { get; set; }

    public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
    public event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
    public event EventHandler<PlayerErrorEventArgs>? OnError;

    internal void RaiseFirstFrame() =>
        OnFirstFrame?.Invoke(this, new PlayerFirstFrameEventArgs { AtUtc = DateTimeOffset.UtcNow });
    internal void RaiseFrameTick() =>
        OnFrameTick?.Invoke(this, new PlayerFrameTickEventArgs { AtUtc = DateTimeOffset.UtcNow });

    internal void RaiseBufferingStart() =>
        OnBufferingStart?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });

    internal void RaiseBufferingEnd() =>
        OnBufferingEnd?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });

    internal void RaiseError(string code) =>
        OnError?.Invoke(this, new PlayerErrorEventArgs { Code = code, Message = code });

    public void Play(PlaybackParams params_)
    {
        PlayedUrls.Add(params_.Endpoint.Url);
        Log.Add($"Play:{params_.Endpoint.Url}");
    }

    public void Pause() => Log.Add("Pause");

    public void Resume() => Log.Add("Resume"); // vc68：热备静音攒流契约（IPlayer.Resume）

    public void Stop() => Log.Add("Stop");

    public void AttachSurface(IntPtr surfaceHandle) => Log.Add("Attach");

    public void DetachSurface() => Log.Add("Detach");

    public void Dispose() => Log.Add("Dispose");
}
