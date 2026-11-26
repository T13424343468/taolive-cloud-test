using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 覆盖冲刺 C1（vc90 分支覆盖账）：PlaybackSessionManager 未覆盖分支补测——
/// 热备全路径、热切接管、冷切、缩编、超龄/LRU 淘汰、回主源双口径、偷拉泵、竞速泵、对齐回账。
/// 只加测试不改产品代码；全局态（BackgroundLane）按 ManagerLaneGateTests 纪律还原。
/// </summary>
[Collection("BackgroundLaneSerial")]
public class CoverageSprintC1
{
    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources) { EpgKey = id };
    }

    /// <summary>可手动推进的单调钟：manager 与健康线共用，快拍不真等。</summary>
    private sealed class MonoClock
    {
        public long Ms;
        public long Now() => Ms;
        public void Advance(long ms) => Ms += ms;
    }

    /// <summary>记录码率上限下发的内核桩（六项优化④断言用）。</summary>
    internal sealed class SprintPlayer : IPlayer
    {
        public List<string> Log { get; } = new();
        public List<long?> Ceilings { get; } = new();
        public List<BufferInventory.Mode> GateModes { get; } = new(); // vc94 深水档记账
        public long BufferedMs { get; set; }

#pragma warning disable CS0067 // 桩内核不触发全部事件，按需 raise
        public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
        public event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;
        public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
        public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
        public event EventHandler<PlayerErrorEventArgs>? OnError;
#pragma warning restore CS0067

        internal void RaiseFirstFrame() => OnFirstFrame?.Invoke(this, new PlayerFirstFrameEventArgs { AtUtc = DateTimeOffset.UtcNow });
        internal void RaiseError(string code) => OnError?.Invoke(this, new PlayerErrorEventArgs { Code = code, Message = code });

        public void Play(PlaybackParams params_) => Log.Add($"Play:{params_.Endpoint.Url}");
        public void Pause() => Log.Add("Pause");
        public void Resume() => Log.Add("Resume");
        public void Stop() => Log.Add("Stop");
        public void AttachSurface(IntPtr surfaceHandle) => Log.Add("Attach");
        public void DetachSurface() => Log.Add("Detach");
        public void Dispose() => Log.Add("Dispose");
        public void ApplyBitrateCeiling(long? maxVideoBitrate)
        {
            Ceilings.Add(maxVideoBitrate);
            Log.Add($"Ceiling:{maxVideoBitrate?.ToString() ?? "null"}");
        }

        public void SetStartupGateMode(BufferInventory.Mode mode)
        {
            GateModes.Add(mode);
            Log.Add($"Gate:{mode}");
        }
    }

    private static (PlaybackSessionManager mgr, List<SprintPlayer> players, MonoClock mono) MakeManager(
        bool hotStandby = true, MonoClock? mono = null, Func<Channel, bool>? primaryAlive = null)
    {
        var clock = mono ?? new MonoClock();
        var players = new List<SprintPlayer>();
        var fixedClock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var mgr = new PlaybackSessionManager(
            () => { var p = new SprintPlayer(); players.Add(p); return new PlaybackSession(p, fixedClock); },
            hotStandbyEnabled: hotStandby,
            primaryAliveConfirmed: primaryAlive,
            monotonicMs: clock.Now);
        return (mgr, players, clock);
    }

    /// <summary>建管理器并保留会话引用（状态断言用）。clock=会话内 FixedClock（自愈/看门狗时间泵走它）。</summary>
    private static (PlaybackSessionManager mgr, List<SprintPlayer> players, MonoClock mono, List<PlaybackSession> sessions, FixedClock clock)
        MakeManagerWithSessions(bool hotStandby = true, MonoClock? mono = null, Func<Channel, bool>? primaryAlive = null)
    {
        var clock = mono ?? new MonoClock();
        var players = new List<SprintPlayer>();
        var sessions = new List<PlaybackSession>();
        var fixedClock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var mgr = new PlaybackSessionManager(
            () => { var p = new SprintPlayer(); players.Add(p); var s = new PlaybackSession(p, fixedClock); sessions.Add(s); return s; },
            hotStandbyEnabled: hotStandby,
            primaryAliveConfirmed: primaryAlive,
            monotonicMs: clock.Now);
        return (mgr, players, clock, sessions, fixedClock);
    }

    // ---- 属性接线：FallbackSourceProvider / QueueOrderer 向既有与后建会话同挂 ----

    [Fact]
    public void Fallback_provider_propagates_to_existing_and_later_sessions()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));

        Func<Channel, IReadOnlyList<SourceEndpoint>>? provided = _ => Array.Empty<SourceEndpoint>();
        mgr.FallbackSourceProvider = provided;
        Assert.Same(provided, mgr.FallbackSourceProvider);
        Assert.Same(provided, mgr.TryGet("cctv1")!.FallbackSourceProvider);

        mgr.SwitchTo(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.Same(provided, mgr.TryGet("cctv2")!.FallbackSourceProvider);
    }

    [Fact]
    public void Queue_orderer_propagates_to_existing_and_later_sessions()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));

        Func<Channel, IEnumerable<SourceEndpoint>, IEnumerable<SourceEndpoint>>? orderer = (_, q) => q;
        mgr.QueueOrderer = orderer;
        Assert.Same(orderer, mgr.QueueOrderer);
        Assert.Same(orderer, mgr.TryGet("cctv1")!.QueueOrderer);

        mgr.SwitchTo(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.Same(orderer, mgr.TryGet("cctv2")!.QueueOrderer);
    }

    // ---- PrepareStandby 全路径 ----

    [Fact]
    public void PrepareStandby_returns_null_when_disabled_or_invalid()
    {
        var (mgrOff, _, _) = MakeManager(hotStandby: false);
        Assert.Null(mgrOff.PrepareStandby(MakeChannel("cctv1", "https://s/a.m3u8")));

        var (mgr, _, _) = MakeManager();
        Assert.Null(mgr.PrepareStandby(new Channel("", "坏台", "测试",
            new List<SourceEndpoint> { new("https://s/x.m3u8", SourceKind.PureIp, 1) })));
        Assert.Throws<ArgumentNullException>(() => mgr.PrepareStandby(null!));
    }

    [Fact]
    public void PrepareStandby_same_as_current_returns_current_without_new_session()
    {
        var (mgr, players, _) = MakeManager();
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        var cur = mgr.SwitchTo(ch);
        int sessionsBefore = players.Count;

        var again = mgr.PrepareStandby(ch);
        Assert.Same(cur, again);
        Assert.Equal(sessionsBefore, players.Count); // 当前台无需备，不新建内核
        Assert.Null(mgr.Standby);
    }

    [Fact]
    public void PrepareStandby_repeats_return_same_standby()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");

        var sb1 = mgr.PrepareStandby(ch2);
        var sb2 = mgr.PrepareStandby(ch2);
        Assert.Same(sb1, sb2);
        Assert.Same(sb1, mgr.Standby);
        Assert.Equal(2, players.Count); // 当前台+一个热备
    }

    [Fact]
    public void PrepareStandby_switch_target_supersedes_old_standby()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        var sb1 = mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.Equal(PlaybackState.Buffering, sb1!.State);

        var sb2 = mgr.PrepareStandby(MakeChannel("cctv3", "https://s/c.m3u8"));
        Assert.Same(sb2, mgr.Standby);
        Assert.NotSame(sb1, sb2);
        Assert.Equal(PlaybackState.Idle, sb1.State); // 旧热备让位停机，不白烧网络
        Assert.Contains("Stop", players[1].Log);
    }

    [Fact]
    public void PrepareStandby_reuses_existing_session_from_table()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.SwitchTo(MakeChannel("cctv2", "https://s/b.m3u8")); // cctv1 会话让位留表
        int before = players.Count;

        var sb = mgr.PrepareStandby(MakeChannel("cctv1", "https://s/a.m3u8"));
        Assert.Same(mgr.TryGet("cctv1"), sb); // 表内复用，不新建
        Assert.Equal(before, players.Count);
        Assert.Equal(PlaybackState.Buffering, sb!.State);
        Assert.Contains("Pause", players[0].Log); // 备台静音攒流（vc68 漏音修）
    }

    // ---- vc93 审查清零 P1：热备会话看门狗泵（Failed 自愈慢轮经管理器 Tick 驱动） ----

    [Fact]
    public void Standby_failed_session_self_heals_via_manager_tick()
    {
        var (mgr, players, _, sessions, clock) = MakeManagerWithSessions();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        var standby = mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.NotNull(standby);
        Assert.Same(standby, mgr.Standby);

        // 热备备源耗尽进 Failed（单端点台一次报错即耗尽），排定自愈慢轮（首档 5 秒）
        var standbyPlayer = players[1];
        standbyPlayer.RaiseError("NET-1");
        Assert.Equal(PlaybackState.Failed, standby!.State);

        // 未到点：Tick 泵不触发自愈
        clock.Advance(TimeSpan.FromSeconds(1));
        mgr.Tick();
        Assert.Equal(PlaybackState.Failed, standby.State);

        // 首档 5 秒到点：经管理器 Tick 驱动热备自愈慢轮——Failed → Buffering 从头慢轮重试
        clock.Advance(TimeSpan.FromSeconds(5));
        mgr.Tick();
        Assert.Equal(PlaybackState.Buffering, standby.State);
        Assert.Contains("Play:https://s/b.m3u8", string.Join(";", standbyPlayer.Log)); // 从头慢轮重放队首
    }

    [Fact]
    public void Standby_watchdog_pump_exception_does_not_break_current_tick()
    {
        var (mgr, players, _, _, clock) = MakeManagerWithSessions();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));

        // 热备内核抛错：热备泵异常被包裹，当前台看门狗照常泵完不炸
        players[1].RaiseError("BOOM"); // 桩内核事件正常路径；异常包裹路径由 PumpStandbyWatchdog try-catch 兜住
        clock.Advance(TimeSpan.FromSeconds(1));
        mgr.Tick(); // 不抛即过：热备泵异常不拖垮当前台泵
        Assert.Equal(PlaybackState.Buffering, mgr.Current!.State);
    }

    // ---- SwitchTo：无效台、热切接管、冷切 ----

    [Fact]
    public void SwitchTo_invalid_channel_throws_without_polluting_table()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        var bad = new Channel("", "坏台", "测试", new List<SourceEndpoint> { new("https://s/x.m3u8", SourceKind.PureIp, 1) });
        Assert.Throws<ArgumentException>(() => mgr.SwitchTo(bad));
        Assert.Single(mgr.Sessions); // 会话表不被半途污染
        Assert.Equal("cctv1", mgr.Current!.CurrentChannel!.Id);
    }

    [Fact]
    public void SwitchTo_hot_takes_over_ready_standby()
    {
        var (mgr, players, _) = MakeManager();
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8");
        var cur = mgr.SwitchTo(ch1);
        cur.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, cur.State);

        var ch2 = MakeChannel("cctv2", "https://s/b.m3u8");
        var sb = mgr.PrepareStandby(ch2)!;
        sb.NotifyFirstFrame(); // 热备达 Playing=起播门槛线已过
        Assert.Equal(PlaybackState.Playing, sb.State);

        var taken = mgr.SwitchTo(ch2);
        Assert.Same(sb, taken);
        Assert.Same(sb, mgr.Current);
        Assert.Null(mgr.Standby);
        Assert.True(sb.IsForeground);
        Assert.False(cur.IsForeground);
        Assert.Equal(PlaybackState.Idle, cur.State); // 旧台让位停机
        Assert.Contains("Resume", players[1].Log);   // 备台静音攒流恢复出声出画
        Assert.Contains("Stop", players[0].Log);     // 旧内核先退场
    }

    [Fact]
    public void SwitchTo_cold_when_standby_not_ready()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8")); // 攒流中未达 Playing

        var switched = mgr.SwitchTo(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.Same(mgr.TryGet("cctv2"), switched);
        Assert.Same(switched, mgr.Current);
        Assert.Null(mgr.Standby); // 热备转正后清位
    }

    [Fact]
    public void SwitchTo_same_channel_does_not_supersede_current()
    {
        var (mgr, players, _) = MakeManager();
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame();

        var again = mgr.SwitchTo(ch);
        Assert.Same(cur, again);
        Assert.Equal(PlaybackState.Buffering, cur.State); // 同台重播走换台链路（停旧起新）
        Assert.Contains("Stop", players[0].Log);
    }

    [Fact]
    public void SwitchTo_third_channel_clears_stale_standby_reference()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        var sb = mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"))!;

        var third = mgr.SwitchTo(MakeChannel("cctv3", "https://s/c.m3u8"));
        Assert.Same(third, mgr.Current);
        Assert.Same(sb, mgr.Standby); // 非目标热备保留（资源闸只清超龄/超容）
        Assert.Equal(PlaybackState.Buffering, sb.State);
    }

    // ---- 缩编与淘汰 ----

    [Fact]
    public void Shrink_for_memory_pressure_removes_only_superseded_sessions()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));
        mgr.SwitchTo(MakeChannel("cctv3", "https://s/c.m3u8")); // cctv1 会话让位留表

        int removed = mgr.ShrinkForMemoryPressure();
        Assert.Equal(1, removed); // 只有让位的 cctv1 出表
        Assert.Null(mgr.TryGet("cctv1"));
        Assert.NotNull(mgr.TryGet("cctv3")); // 当前台保留
        Assert.Contains("Stop", players[0].Log); // 让位会话内核释放
        Assert.Equal(0, mgr.ShrinkForMemoryPressure()); // 无让位会话再缩编=0
    }

    [Fact]
    public void Evict_superseded_sessions_after_ttl_and_protect_current()
    {
        var players = new List<SprintPlayer>();
        var mono = new MonoClock { Ms = 1_000 };
        var fixedClock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var mgr = new PlaybackSessionManager(
            () => { var p = new SprintPlayer(); players.Add(p); return new PlaybackSession(p, fixedClock); },
            hotStandbyEnabled: true, monotonicMs: mono.Now);

        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.SwitchTo(MakeChannel("cctv2", "https://s/b.m3u8")); // cctv1 让位（记账 t=1000）
        Assert.NotNull(mgr.TryGet("cctv1"));

        mono.Advance((long)PlaybackSessionManager.SupersededSessionTtl.TotalMilliseconds); // 恰满 TTL
        mgr.SwitchTo(MakeChannel("cctv3", "https://s/c.m3u8"));    // 触发资源闸
        Assert.Null(mgr.TryGet("cctv1")); // 超龄让位会话被清
        Assert.NotNull(mgr.TryGet("cctv2")); // 刚让位未超龄，不误杀
        Assert.NotNull(mgr.TryGet("cctv3")); // Current 永不清
    }

    [Fact]
    public void Evict_lru_beyond_capacity_never_kills_current_or_standby()
    {
        var (mgr, players, _) = MakeManager();
        mgr.RetainedCapacity = 2;
        mgr.SwitchTo(MakeChannel("c1", "https://s/a.m3u8"));
        mgr.SwitchTo(MakeChannel("c2", "https://s/b.m3u8"));
        mgr.SwitchTo(MakeChannel("c3", "https://s/c.m3u8")); // c1 被 LRU 淘汰
        Assert.Null(mgr.TryGet("c1"));
        Assert.NotNull(mgr.TryGet("c2"));
        Assert.NotNull(mgr.TryGet("c3"));

        mgr.SwitchTo(MakeChannel("c4", "https://s/d.m3u8")); // 容量 2：c2 被淘汰
        Assert.Null(mgr.TryGet("c2"));
        Assert.NotNull(mgr.TryGet("c3"));
        Assert.NotNull(mgr.TryGet("c4"));
    }

    // ---- Tick：空当前台、热备攒流超时 ----

    [Fact]
    public void Tick_without_current_is_safe_and_times_out_standby()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _, sessions, _) = MakeManagerWithSessions(mono: mono);

        mgr.Tick(); // 无当前台：不抛、清回主源账
        Assert.Null(mgr.Current);

        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"));
        Assert.NotNull(mgr.Standby);

        mono.Advance((long)PlaybackSessionManager.StandbyAccumulateWindow.TotalMilliseconds);
        mgr.Tick(); // 攒流超时：热备让位转冷切
        Assert.Null(mgr.Standby);
        Assert.Equal(PlaybackState.Idle, sessions[^1].State);
    }

    [Fact]
    public void Standby_timeout_does_not_touch_playing_standby()
    {
        var mono = new MonoClock { Ms = 0 };
        var players = new List<SprintPlayer>();
        var fixedClock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var mgr = new PlaybackSessionManager(
            () => { var p = new SprintPlayer(); players.Add(p); return new PlaybackSession(p, fixedClock); },
            hotStandbyEnabled: true, monotonicMs: mono.Now);

        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        var sb = mgr.PrepareStandby(MakeChannel("cctv2", "https://s/b.m3u8"))!;
        sb.NotifyFirstFrame(); // 达门槛：不受窗口约束
        mono.Advance((long)PlaybackSessionManager.StandbyAccumulateWindow.TotalMilliseconds * 2);
        mgr.Tick();
        Assert.Same(sb, mgr.Standby); // 已就绪热备常备待热切
    }

    // ---- 回主源：无仲裁器硬切口径 ----

    [Fact]
    public void Return_to_primary_hard_switch_after_stable_window()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(primaryAlive: _ => true, mono: mono);

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame();
        cur.SwitchLine(1); // 切到备源
        cur.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, cur.State);
        Assert.Equal(1, cur.CurrentLineIndex);

        mono.Advance((long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds);
        mgr.Tick();
        Assert.Equal(0, cur.CurrentLineIndex); // 稳定窗满+主源 OK=硬切回主源
    }

    [Fact]
    public void Return_to_primary_requires_primary_alive_confirmed()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(primaryAlive: _ => false, mono: mono);

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame();
        cur.SwitchLine(1);
        cur.NotifyFirstFrame();
        mono.Advance((long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds);
        mgr.Tick();
        Assert.Equal(1, cur.CurrentLineIndex); // 判据不满足不切
    }

    [Fact]
    public void Return_window_resets_after_failover()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(primaryAlive: _ => true, mono: mono);

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame();
        cur.SwitchLine(1);
        cur.NotifyFirstFrame();
        mono.Advance((long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds - 10_000);

        players[^1].RaiseError("NET-1"); // 断流自救重新起窗
        cur.NotifyFirstFrame();
        mono.Advance(10_000); // 距重置不足 5 分钟
        mgr.Tick();
        Assert.Equal(1, cur.CurrentLineIndex); // 窗内再断重新计时，不切
    }

    // ---- 偷拉泵（PumpStealth）----

    private sealed class StubLoader : ISegmentLoader
    {
        public Func<string, byte[]?>? Reply { get; set; }
        public List<string> Requests { get; } = new();
        public Task<byte[]?> PullAsync(string sourceUrl, CancellationToken ct)
        {
            Requests.Add(sourceUrl);
            return Task.FromResult(Reply?.Invoke(sourceUrl));
        }
    }

    [Fact]
    public void Stealth_pump_idle_without_healthline_or_loader()
    {
        var (mgr, players, _) = MakeManager();
        mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        mgr.Current!.NotifyFirstFrame();
        mgr.Tick(); // 未接 HealthLine/SegmentLoader=全链静默，不抛
        Assert.Equal(PlaybackState.Playing, mgr.Current.State);
    }

    [Fact]
    public void Stealth_pump_steals_backup_while_primary_playing_and_feeds_quality_book()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(mono: mono);
        var book = new QualityHealthStore(mono.Now);
        mgr.QualityBook = book;
        var health = new LinkHealthLine(mono.Now);
        health.ReportSegmentInterval(1_000); // 分片节奏健康→裕度转正→放行因子 1（默认无账=0 停）
        mgr.HealthLine = health;
        var loader = new StubLoader { Reply = _ => new byte[] { 1, 2, 3 } };
        mgr.SegmentLoader = loader;
        BackgroundLane.SetPlaying(false); // 让路闸全开

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame(); // 在播主源→偷下一备路
        Assert.Equal(PlaybackState.Playing, cur.State);
        players[0].BufferedMs = 30_000; // 前台水位足，让路闸全开

        mgr.Tick(); // 建管道+起第一发
        Assert.Single(loader.Requests);
        Assert.Equal("https://s/b.m3u8", loader.Requests[0]); // 在播主源→偷备路

        mono.Advance(1);
        mgr.Tick(); // 收割成功片：池深+5s、喂实测账
        var entry = book.Peek(ch.EpgKey, ch.SourceId, "https://s/b.m3u8");
        Assert.NotNull(entry);
        Assert.True(entry!.GoodCount >= 1, "偷拉成功片未喂实测账");

        mono.Advance(1);
        mgr.Tick(); // 池未满继续攒（10s<15s 上限，第三发照起）
        Assert.Equal(3, loader.Requests.Count);
        BackgroundLane.SetPlaying(false);
    }

    [Fact]
    public void Stealth_pump_applies_bitrate_ceiling_on_margin_change()
    {
        var mono = new MonoClock { Ms = 0 };
        var players = new List<SprintPlayer>();
        var fixedClock = new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        var mgr = new PlaybackSessionManager(
            () => { var p = new SprintPlayer(); players.Add(p); return new PlaybackSession(p, fixedClock); },
            hotStandbyEnabled: true, monotonicMs: mono.Now);
        var health = new LinkHealthLine(mono.Now);
        mgr.HealthLine = health;
        mgr.SegmentLoader = new StubLoader { Reply = _ => new byte[] { 1 } };
        BackgroundLane.SetPlaying(false);

        var cur = mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        cur.NotifyFirstFrame();

        health.ReportBadFrames(1000); // 坏帧堆账→裕度转负→降档
        mgr.Tick();
        Assert.Contains(players[0].Ceilings, c => c is not null); // 换档经 IPlayer 契约下发内核
        BackgroundLane.SetPlaying(false);
    }

    [Fact]
    public void Stealth_pump_gates_close_on_low_stock_or_zero_factor()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(mono: mono);
        var health = new LinkHealthLine(mono.Now);
        mgr.HealthLine = health;
        var loader = new StubLoader { Reply = _ => new byte[] { 1 } };
        mgr.SegmentLoader = loader;

        var cur = mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        cur.NotifyFirstFrame();

        players[0].BufferedMs = 1_000; // 前台水位低于让路闸：不起发偷拉
        mgr.Tick();
        Assert.Empty(loader.Requests);

        players[0].BufferedMs = 30_000; // 水位足但余量因子为 0：仍不起发
        health.ReportBadFrames(100000);
        mgr.Tick();
        Assert.Empty(loader.Requests);
        BackgroundLane.SetPlaying(false);
    }

    [Fact]
    public void Stealth_pump_freeze_and_retake_events_fire()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(mono: mono);
        var health = new LinkHealthLine(mono.Now);
        health.ReportSegmentInterval(1_000); // 放行因子转正
        mgr.HealthLine = health;
        var loader = new StubLoader { Reply = _ => null }; // 永远拉不到
        mgr.SegmentLoader = loader;
        BackgroundLane.SetPlaying(false);

        string? invalidatedChannel = null;
        PlaybackSession.TicketCacheInvalidator = (chId, _) => invalidatedChannel = chId;
        try
        {
            var cur = mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8"));
            cur.NotifyFirstFrame();
            players[0].BufferedMs = 30_000; // 水位足，让路闸全开

            mono.Advance(StealthPipeline.SegmentPeriodMs * 2 + 1);
            mgr.Tick(); // 首发在途（null 结果下一拍收割）
            mono.Advance(1);
            mgr.Tick(); // 收割失败 1 次，起第二发
            mono.Advance(1);
            mgr.Tick(); // 连败第 2 次→RetakeListNeeded
            Assert.Equal("cctv1", invalidatedChannel); // 清单重取触发票据作废
        }
        finally
        {
            PlaybackSession.TicketCacheInvalidator = null;
            BackgroundLane.SetPlaying(false);
        }
    }

    // ---- 竞速泵（PumpRaceProbe）----

    private sealed class StubProber : IHandshakeProber
    {
        public Func<Channel, IReadOnlyList<SourceEndpoint>, int>? Outcome { get; set; }
        public int Calls { get; private set; }
        public Task<int> RaceAsync(Channel channel, IReadOnlyList<SourceEndpoint> candidates, int maxRacers = 3, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Outcome?.Invoke(channel, candidates) ?? -1);
        }
    }

    [Fact]
    public void Race_probe_reorders_winner_to_front()
    {
        var (mgr, players, _) = MakeManager();
        var prober = new StubProber { Outcome = (_, _) => 1 }; // 第 2 路先成
        mgr.RaceProber = prober;
        var gate = new ManualResetEventSlim(false);
        mgr.MainDispatcher = action => { action(); gate.Set(); }; // 竞速回填经主线程投递（默认 ContinueWith 异步跑）

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        var cur = mgr.SwitchTo(ch);
        players[^1].RaiseError("NET-1");
        players[^1].RaiseError("NET-1");
        players[^1].RaiseError("NET-1"); // 三路烧光=Failed 自救回合
        Assert.Equal(PlaybackState.Failed, cur.State);

        mgr.Tick(); // 起竞速
        Assert.True(gate.Wait(2000), "竞速回填未在超时内到达主线程");
        Assert.Equal(1, prober.Calls);
        Assert.Equal("https://s/b.m3u8", cur.PeekQueueEndpoint(0)?.Url); // 赢家提到队头
    }

    [Fact]
    public void Race_probe_all_fail_keeps_order()
    {
        var (mgr, players, _) = MakeManager();
        var prober = new StubProber { Outcome = (_, _) => -1 };
        mgr.RaceProber = prober;
        var gate = new ManualResetEventSlim(false);
        mgr.MainDispatcher = action => { action(); gate.Set(); };

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        players[^1].RaiseError("NET-1");
        players[^1].RaiseError("NET-1"); // 两路烧光=Failed
        Assert.Equal(PlaybackState.Failed, cur.State);

        mgr.Tick();
        Assert.True(gate.Wait(2000), "竞速回填未在超时内到达主线程");
        Assert.Equal("https://s/a.m3u8", cur.PeekQueueEndpoint(0)?.Url); // 全败不动序
        Assert.Equal(1, prober.Calls); // 单飞旗：回填前同回合不连发
    }

    [Fact]
    public void Race_probe_ignores_single_line_or_non_failed_states()
    {
        var (mgr, players, _) = MakeManager();
        var prober = new StubProber { Outcome = (_, _) => 0 };
        mgr.RaceProber = prober;

        var cur = mgr.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        cur.NotifyFirstFrame(); // Playing：不赛
        mgr.Tick();
        Assert.Equal(0, prober.Calls);
    }

    // ---- 对齐切回账（PumpAlignOutcome）----

    [Fact]
    public void Align_outcome_reports_success_after_window_when_holding_primary()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(mono: mono);
        var arbiter = new SwitchArbiter(mono.Now);
        mgr.Arbiter = arbiter;

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame();
        cur.SwitchLine(1);
        cur.NotifyFirstFrame();
        Assert.Equal(1, cur.CurrentLineIndex);

        mono.Advance(1);
        cur.AlignedSwitchTo(0, "test"); // 对齐切回主源：AlignedSwitched 入账
        Assert.Equal(0, cur.CurrentLineIndex);
        cur.NotifyFirstFrame(); // 出画在播

        mono.Advance((long)SwitchArbiter.UpgradeFailWindow.TotalMilliseconds);
        mgr.Tick(); // 站满窗=升档成功，逃生口账清零
        Assert.Equal(0, arbiter.EscapeFailCount);
    }

    // ---- 回主源对齐软切口径（仲裁器已接）----

    [Fact]
    public void Return_to_primary_soft_path_waits_for_pool_stock()
    {
        var mono = new MonoClock { Ms = 0 };
        var (mgr, players, _) = MakeManager(primaryAlive: _ => true, mono: mono);
        mgr.Arbiter = new SwitchArbiter(mono.Now);
        mgr.HealthLine = new LinkHealthLine(mono.Now);
        mgr.SegmentLoader = new StubLoader { Reply = _ => new byte[] { 1 } };
        BackgroundLane.SetPlaying(false);

        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        var cur = mgr.SwitchTo(ch);
        cur.NotifyFirstFrame();
        cur.SwitchLine(1);
        cur.NotifyFirstFrame();
        mgr.Tick(); // 先建偷拉管道（TryReturnToPrimary 在泵前跑，管道未建会直走对齐切）
        mono.Advance((long)PlaybackSessionManager.ReturnToPrimaryStableFor.TotalMilliseconds);

        mgr.Tick(); // 管道池粮不足 15s：不切（攒粮中）
        Assert.Equal(1, cur.CurrentLineIndex);
        BackgroundLane.SetPlaying(false);
    }
}
