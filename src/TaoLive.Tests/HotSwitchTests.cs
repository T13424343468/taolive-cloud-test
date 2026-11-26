using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// M3 热切接线用例（方案书 v5：Current+Standby 双实例热切）：
/// 热备攒流不掺画面、就绪即换面接管不露黑、未就绪回落冷切、
/// 旧热备让位不白烧网络、LRU 资源闸淘汰让位会话释放内核、
/// 换台钩子真实触发邻台预取。固定时钟快拍不真等。
/// </summary>
public class HotSwitchTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    private static (PlaybackSessionManager Manager, List<LoggingPlayer> Players) MakeManager(
        Action<Channel>? onSwitchingChannel = null)
    {
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(
            () => { var p = new LoggingPlayer(); players.Add(p); return new PlaybackSession(p, new FixedClock(T0)); },
            dispatcher: null, onSwitchingChannel, hotStandbyEnabled: true);
        return (manager, players);
    }

    private static void MakePlaying(PlaybackSession session)
    {
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);
    }

    [Fact]
    public void Prepare_standby_buffers_without_surface()
    {
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.Current!.AttachSurface(new IntPtr(1));
        MakePlaying(manager.Current);

        var standby = manager.PrepareStandby(ch2);
        Assert.NotNull(standby);
        Assert.Equal(PlaybackState.Buffering, standby!.State); // 无面起播：攒流不接管画面
        Assert.Equal(IntPtr.Zero, standby.SurfaceHandle);       // 热备不占输出面
        Assert.Equal(standby, manager.Standby);
        Assert.Equal(manager.Current, manager.TryGet("cctv1")); // 当前画面不动
        // 当前会话内核未被热备起播打扰（Stop 未出现于当前内核日志）
        Assert.DoesNotContain("Stop", players[0].Log);
    }

    [Fact]
    public void Switch_to_ready_standby_takes_over_surface_hot()
    {
        // vc68 拍板（老板令「这版最重要的就是热切」）：接管路径保留，次序修正——
        // 旧内核先摘面停机（退场），新内核再挂面恢复（接管）：解码器与屏一对一，不再两颗抢一面。
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.Current!.AttachSurface(new IntPtr(1));
        MakePlaying(manager.Current);
        var standby = manager.PrepareStandby(ch2)!;
        MakePlaying(standby); // 热备攒到门槛线（Playing）

        var currentChanged = new List<PlaybackSession>();
        manager.CurrentChanged += (_, s) => currentChanged.Add(s);

        var taken = manager.SwitchTo(ch2);

        // 热切：热备接管画面，旧会话让位，主界面无感
        Assert.Equal(standby, taken);
        Assert.Equal(standby, manager.Current);
        Assert.Null(manager.Standby);
        Assert.Equal(new IntPtr(1), manager.Current!.SurfaceHandle); // 输出面原面移交
        Assert.Equal(PlaybackState.Playing, manager.Current.State); // 接管即出画，不回缓冲
        Assert.Equal(PlaybackState.Idle, manager.TryGet("cctv1")!.State); // 旧会话已让位
        Assert.Single(currentChanged);
        // vc68 接管次序：旧内核先退场（Detach+Stop），新内核后接管（Attach→Resume）——一对一连接
        Assert.Contains("Detach", players[0].Log);
        Assert.Contains("Stop", players[0].Log);
        Assert.Equal(new[] { "Attach", "Resume" }, players[1].Log[^2..]);
        // 热备内核未被重播（热切不重付起播成本）
        Assert.Single(players[1].PlayedUrls);
    }

    [Fact]
    public void Switch_to_unready_standby_falls_back_to_cold_switch()
    {
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.Current!.AttachSurface(new IntPtr(1));
        MakePlaying(manager.Current);
        var standby = manager.PrepareStandby(ch2)!;
        // 热备仍在 Buffering（未到门槛线）

        var taken = manager.SwitchTo(ch2);

        // 冷切回落：目标会话复用（不另建内核），重新起播至 Playing 前
        Assert.Equal(standby, taken);
        Assert.Equal(standby, manager.Current);
        Assert.Null(manager.Standby);
        Assert.Equal(2, players[1].PlayedUrls.Count); // 攒流一次 + 冷切重播一次
        Assert.Equal(PlaybackState.Buffering, manager.Current!.State);
    }

    [Fact]
    public void New_standby_supersedes_old_standby_to_stop_wasting_network()
    {
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");
        var ch3 = MakeChannel("cctv3", "https://s/c.m3u8");

        manager.SwitchTo(ch1);
        MakePlaying(manager.Current!);
        var sb1 = manager.PrepareStandby(ch2)!;
        var sb2 = manager.PrepareStandby(ch3)!;

        // 换备目标：旧热备让位（停内核），新热备接管预备位
        Assert.Equal(PlaybackState.Idle, sb1.State);
        Assert.Equal(sb2, manager.Standby);
        Assert.Contains("Stop", players[1].Log);
    }

    [Fact]
    public void Shared_kernel_manager_has_no_hot_standby()
    {
        var clock = new FixedClock(T0);
        var sharedPlayer = new LoggingPlayer();
        var manager = new PlaybackSessionManager(() => new PlaybackSession(sharedPlayer, clock));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");

        Assert.Null(manager.PrepareStandby(ch)); // 共用单内核不开热备（W1 冷切契约不变）
        Assert.Null(manager.Standby);
    }

    [Fact]
    public void Session_table_evicts_lru_superseded_sessions_and_disposes_kernels()
    {
        var (manager, players) = MakeManager();
        var channels = Enumerable.Range(1, 10)
            .Select(i => MakeChannel($"ch{i}", $"https://s/{i}.m3u8")).ToList();

        manager.SwitchTo(channels[0]);
        for (int i = 1; i < channels.Count; i++)
        {
            manager.SwitchTo(channels[i]);
        }

        // 资源闸：会话表封顶，超容的最久未用已让位会话被淘汰并释放内核
        Assert.True(manager.Sessions.Count <= PlaybackSessionManager.MaxRetainedSessions,
            $"会话表超容：{manager.Sessions.Count}");
        Assert.NotNull(manager.Current);
        var disposedCount = players.Count(p => p.Log.Contains("Dispose"));
        Assert.True(disposedCount > 0, "超容会话未释放内核");
        // 最早被换下的会话已不在表
        Assert.Null(manager.TryGet("ch1"));
        Assert.NotNull(manager.TryGet($"ch{channels.Count}"));
    }

    [Fact]
    public void Superseded_session_evicted_after_ttl_by_monotonic_clock()
    {
        // 老板令 #21 回归账：超龄清理按让位时间账（单调钟）判，
        // 不得拿 LRU 序数当毫秒——否则一开机就整表误清。
        long nowMs = 10_000;
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(
            () => { var p = new LoggingPlayer(); players.Add(p); return new PlaybackSession(p, new FixedClock(T0)); },
            hotStandbyEnabled: true,
            monotonicMs: () => nowMs);
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        nowMs += 60_000; // 看了 1 分钟
        manager.SwitchTo(ch2); // ch1 让位，超龄账自此起算
        Assert.NotNull(manager.TryGet("cctv1")); // TTL 内留表：同频道切回秒回切

        nowMs += (long)PlaybackSessionManager.SupersededSessionTtl.TotalMilliseconds;
        manager.SwitchTo(MakeChannel("cctv3", "https://s/c.m3u8")); // 触发资源闸清超龄
        Assert.Null(manager.TryGet("cctv1")); // 超龄让位会话被清
        Assert.True(players[0].Log.Contains("Dispose"), "超龄会话未释放内核");
        Assert.NotNull(manager.TryGet("cctv2")); // 仍在 TTL 内的让位会话不清
        Assert.NotNull(manager.TryGet("cctv3")); // 当前会话永不清
    }

    [Fact]
    public void Memory_pressure_shrink_clears_superseded_keeps_current_and_standby()
    {
        // 老板令 #25：内存告警先自瘦身——让位会话全清，当前台/热备不动
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");
        var ch3 = MakeChannel("cctv3", "https://s/c.m3u8");
        manager.SwitchTo(ch1);
        manager.SwitchTo(ch2); // ch1 让位
        manager.PrepareStandby(ch3); // ch3 热备

        Assert.Equal(1, manager.ShrinkForMemoryPressure());
        Assert.Null(manager.TryGet("cctv1")); // 让位会话清出表
        Assert.True(players[0].Log.Contains("Dispose"), "让位会话内核未释放");
        Assert.NotNull(manager.TryGet("cctv2")); // 当前台不动
        Assert.NotNull(manager.TryGet("cctv3")); // 热备不动
        Assert.Equal(manager.Current, manager.TryGet("cctv2"));
        Assert.Equal(0, manager.ShrinkForMemoryPressure()); // 无让位会话再缩编为零
    }

    [Fact]
    public void Switching_channel_triggers_prefetch_hook_with_switched_channel()
    {
        var prefetched = new List<Channel>();
        var (manager, _) = MakeManager(onSwitchingChannel: ch => prefetched.Add(ch));
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.SwitchTo(ch2);

        Assert.Equal(new[] { ch1, ch2 }, prefetched); // 每次换台即触发（壳层接 ChannelPrefetcher 邻台预取）
    }

    [Fact]
    public void Standby_surface_handover_does_not_touch_current_before_takeover()
    {
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.Current!.AttachSurface(new IntPtr(1));
        MakePlaying(manager.Current);
        var standby = manager.PrepareStandby(ch2)!;
        MakePlaying(standby);

        // 热备就绪但用户未切台：当前会话画面持续（无 Detach/Stop）
        Assert.DoesNotContain("Stop", players[0].Log);
        Assert.Equal(PlaybackState.Playing, manager.Current!.State);
        Assert.Equal(standby, manager.Standby);
    }

    [Fact]
    public void Shutdown_supersedes_every_session_including_orphan_kernels()
    {
        // vc68 退出令（老板真机报「退出软件了都还在持续出音」）：退场全表清场——
        // 只让位 Current+Standby 时，表内 Failed 漏停的孤儿内核无人接管，退软件后音频照放。
        var (manager, players) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");
        var ch3 = MakeChannel("cctv3", "https://s/c.m3u8");

        manager.SwitchTo(ch1);
        MakePlaying(manager.Current!);
        manager.SwitchTo(ch2); // ch1 让位留表（vc67 下 Failed 态即孤儿漏音内核）
        MakePlaying(manager.Current!);
        manager.PrepareStandby(ch3);

        manager.Shutdown();

        // 全表会话归 Idle、内核全停（含非 Current/Standby 的表内会话）
        Assert.DoesNotContain(manager.Sessions.Values, s => s.State != PlaybackState.Idle);
        Assert.Null(manager.Current);
        Assert.Null(manager.Standby);
        Assert.All(players, p => Assert.Contains("Stop", p.Log));
    }

    [Fact]
    public void Standby_not_ready_within_window_is_superseded_on_tick()
    {
        var players = new List<LoggingPlayer>();
        long now = 0;
        var manager = new PlaybackSessionManager(
            () => { var p = new LoggingPlayer(); players.Add(p); return new PlaybackSession(p, new FixedClock(T0)); },
            dispatcher: null, null, hotStandbyEnabled: true, monotonicMs: () => now);
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.Current!.AttachSurface(new IntPtr(1));
        MakePlaying(manager.Current);
        var standby = manager.PrepareStandby(ch2)!;
        Assert.Equal(PlaybackState.Buffering, standby.State);

        // 窗口未满：热备照常攒流不放弃
        now = (long)PlaybackSessionManager.StandbyAccumulateWindow.TotalMilliseconds - 1;
        manager.Tick();
        Assert.Equal(standby, manager.Standby);
        Assert.DoesNotContain("Stop", players[1].Log);

        // 窗口满仍攒不到门槛：放弃热备转冷切（方案书 v75），不再烧网络
        now = (long)PlaybackSessionManager.StandbyAccumulateWindow.TotalMilliseconds;
        manager.Tick();
        Assert.Null(manager.Standby);
        Assert.Equal(PlaybackState.Idle, standby.State); // 已让位：内核已停
        Assert.Contains("Stop", players[1].Log);
        // 会话留表：冷切复用不重建
        Assert.Equal(standby, manager.TryGet("cctv2"));

        // 冷切仍可用：超时放弃后 SwitchTo 走既有冷切路径
        var taken = manager.SwitchTo(ch2);
        Assert.Equal(standby, taken);
        Assert.Equal(taken, manager.Current);
    }

    [Fact]
    public void Ready_standby_survives_tick_past_window()
    {
        var players = new List<LoggingPlayer>();
        long now = 0;
        var manager = new PlaybackSessionManager(
            () => { var p = new LoggingPlayer(); players.Add(p); return new PlaybackSession(p, new FixedClock(T0)); },
            dispatcher: null, null, hotStandbyEnabled: true, monotonicMs: () => now);
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        manager.SwitchTo(ch1);
        manager.Current!.AttachSurface(new IntPtr(1));
        MakePlaying(manager.Current);
        var standby = manager.PrepareStandby(ch2)!;
        MakePlaying(standby); // 窗口内已攒到门槛线

        // 已达门槛的热备不受窗口约束，常备待热切（v75 只清「攒不到」的）
        now = (long)PlaybackSessionManager.StandbyAccumulateWindow.TotalMilliseconds * 10;
        manager.Tick();
        Assert.Equal(standby, manager.Standby);
        Assert.DoesNotContain("Stop", players[1].Log);
    }
}

/// <summary>
/// 刀三（老板 2026-11-25 九件套定稿）：预取圈扩到「最近看过的台」——直接消费观看时长账本快照，
/// 跳过当前台与邻台已覆盖者，账本里的台不在目录则跳过不发包。
/// </summary>
public class PrefetchRecentTests
{
    private static Channel MakeCh(string id)
    {
        var ch = new Channel(id, $"频道-{id}", "测试", new List<SourceEndpoint> { new($"https://s/{id}.m3u8", SourceKind.Cdn, 10) });
        ch.EpgKey = id; // 账本按 EpgKey 对账（构造器不赋值，测试显式给）
        return ch;
    }

    private static List<Channel> MakeCatalog(params string[] ids) => ids.Select(MakeCh).ToList();

    [Fact]
    public async Task Recent_ledger_extends_circle_beyond_neighbors()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(new ResolvedStream { Channel = ch }); });
        var catalog = MakeCatalog("cctv1", "cctv2", "cctv3", "cctv4", "cctv5");
        var current = catalog[2]; // cctv3：邻台=cctv2/cctv4

        // 账本快照：cctv5 看最多、cctv1 次之（都不是邻台）
        var ledger = new List<(string, long)> { ("cctv5", 900_000), ("cctv1", 300_000) };
        await prefetcher.PrefetchRecentAsync(current, catalog, ledger);

        Assert.Equal(new[] { "cctv5", "cctv1" }, prefetched.Select(c => c.Id).ToList());
    }

    [Fact]
    public async Task Neighbors_and_current_are_skipped()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(new ResolvedStream { Channel = ch }); });
        var catalog = MakeCatalog("cctv1", "cctv2", "cctv3", "cctv4");
        var current = catalog[1]; // cctv2：邻台=cctv1/cctv3

        // 账本：邻台 cctv1 与当前台 cctv2 都该被跳过；cctv4 在目录内照取
        var ledger = new List<(string, long)> { ("cctv1", 900_000), ("cctv2", 800_000), ("cctv4", 100_000) };
        await prefetcher.PrefetchRecentAsync(current, catalog, ledger);

        Assert.Equal(new[] { "cctv4" }, prefetched.Select(c => c.Id).ToList());
    }

    [Fact]
    public async Task Ledger_entries_missing_from_catalog_are_skipped()
    {
        LaneTestGuard.Open();
        var prefetched = new List<Channel>();
        var prefetcher = new ChannelPrefetcher(ch => { prefetched.Add(ch); return Task.FromResult<ResolvedStream?>(new ResolvedStream { Channel = ch }); });
        var catalog = MakeCatalog("cctv1", "cctv2", "cctv3");
        var current = catalog[0]; // cctv1：邻台=cctv2

        // cctv9 已不在目录（下架/换表）：跳过不发包，cctv3 照取
        var ledger = new List<(string, long)> { ("cctv9", 900_000), ("cctv3", 100_000) };
        await prefetcher.PrefetchRecentAsync(current, catalog, ledger);

        Assert.Equal(new[] { "cctv3" }, prefetched.Select(c => c.Id).ToList());
    }
}
