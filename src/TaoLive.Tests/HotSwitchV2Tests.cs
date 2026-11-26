using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc85 蓝本 v2.5 双向无缝切换断言（老板令 2026-11-17「一次改完统一验」）：
/// - 仲裁器三原语转移表（人工穷举：优先级覆盖/同向冷却/逃生口休眠/双绿复位/裁决超时/回退防振铃）；
/// - 偷拉管道（让路闸/池深自钳/换向清池/连败重取/清单冻结/接管原子清）；
/// - 链路健康线（坏帧负项/钳制）；
/// - 会话微卡回退（盲区 10 秒回退+30 秒防振铃+非盲区不回退）；
/// - 降档软着陆（预防判据命中走对齐切，不烧限速账）；
/// - 竞速回填（胜者提队头，在播线路身份保持）。
/// 全部快拍不真等；时钟注入可控。
/// </summary>
public class HotSwitchV2Tests
{
    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    // ---- 仲裁器三原语转移表 ----

    private sealed class MutableClock
    {
        public long Now;
        public long Get() => Now;
        public void Advance(long ms) => Now += ms;
    }

    [Fact]
    public void Arbiter_low_priority_does_not_displace_pending_high()
    {
        var clock = new MutableClock();
        var arb = new SwitchArbiter(clock.Get);
        Assert.True(arb.Submit(SwitchIntentKind.SoftLanding)); // 挂账中
        Assert.False(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 低优让位高优：不排队
        Assert.False(arb.Submit(SwitchIntentKind.AlignUpgrade)); // 低优让位高优
        Assert.True(arb.Submit(SwitchIntentKind.LifeSaving)); // 保命覆盖一切
        arb.Consume();
    }

    [Fact]
    public void Arbiter_same_direction_cooldown_5min()
    {
        var clock = new MutableClock();
        var arb = new SwitchArbiter(clock.Get);
        Assert.True(arb.Submit(SwitchIntentKind.ReturnPrimary));
        arb.Consume();
        Assert.False(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 5 分钟内同类拒绝
        Assert.False(arb.Submit(SwitchIntentKind.AlignUpgrade));  // 同向升档共冷却
        clock.Advance((long)SwitchArbiter.AlignUpgradeCooldown.TotalMilliseconds + 1);
        Assert.True(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 冷却过即放行
        arb.Consume();
    }

    [Fact]
    public void Arbiter_escape_hatch_three_fails_sleeps_primary_and_double_green_resets()
    {
        var clock = new MutableClock();
        var arb = new SwitchArbiter(clock.Get);
        Assert.True(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 放行一次：对齐基准时刻=0
        arb.Consume();
        for (int i = 0; i < SwitchArbiter.EscapeFailLimit; i++)
        {
            clock.Advance(10_000); // 升级失败窗（60s）内三次再断
            arb.ReportOutcome(false);
        }
        Assert.True(arb.IsPrimaryDormant); // 1 小时窗内满 3 次失败 → 主源休眠
        Assert.False(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 休眠期内回主源拒绝
        Assert.False(arb.Submit(SwitchIntentKind.AlignUpgrade));
        clock.Advance((long)SwitchArbiter.AlignUpgradeCooldown.TotalMilliseconds + 1);
        arb.ResetDormancy(); // 双绿复位
        Assert.False(arb.IsPrimaryDormant);
        Assert.Equal(0, arb.EscapeFailCount);
        Assert.True(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 复位+冷却过后放行
        arb.Consume();
    }

    [Fact]
    public void Arbiter_report_outcome_ignores_breaks_far_from_upgrade()
    {
        var clock = new MutableClock();
        var arb = new SwitchArbiter(clock.Get);
        Assert.True(arb.Submit(SwitchIntentKind.ReturnPrimary));
        arb.Consume();
        clock.Advance((long)SwitchArbiter.UpgradeFailWindow.TotalMilliseconds * 10); // 升级失败窗远外
        arb.ReportOutcome(false);
        Assert.Equal(0, arb.EscapeFailCount); // 与升档无关的断流不冤枉逃生口账
    }

    [Fact]
    public void Arbiter_sweep_pending_releases_stale_decision()
    {
        var clock = new MutableClock();
        var arb = new SwitchArbiter(clock.Get);
        Assert.True(arb.Submit(SwitchIntentKind.SoftLanding)); // 挂账
        clock.Advance(SwitchArbiter.DecisionTimeoutMs + 1);
        arb.SweepPending(); // 裁决超时自动放行清账
        Assert.True(arb.Submit(SwitchIntentKind.ReturnPrimary)); // 挂账已清：低优不再被挡
        arb.Consume();
    }

    [Fact]
    public void Arbiter_rewind_anti_ring_once_per_30s()
    {
        var clock = new MutableClock();
        var arb = new SwitchArbiter(clock.Get);
        Assert.True(arb.TryRequestAutoRewind());
        Assert.False(arb.TryRequestAutoRewind()); // 30 秒内至多一次
        clock.Advance((long)SwitchArbiter.RewindAntiRing.TotalMilliseconds + 1);
        Assert.True(arb.TryRequestAutoRewind());
    }

    // ---- 偷拉管道 ----

    private sealed class ScriptedLoader : ISegmentLoader
    {
        public int Calls;
        public Func<byte[]?> Next = () => new byte[1];
        public Task<byte[]?> PullAsync(string sourceUrl, CancellationToken ct)
        {
            Calls++;
            var result = Next();
            return Task.FromResult(result);
        }
    }

    [Fact]
    public void Pipeline_gate_closed_below_15s_does_not_pull()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        pipe.Retarget(ch, ch.Sources[0]);
        pipe.UpdateGates(playingStockMs: StealthPipeline.GatherGateMs - 1, allowFactor: 1.0);
        pipe.Tick();
        Assert.Equal(0, loader.Calls); // 前台水位不足 15s：让路不偷拉
        Assert.Equal(0, pipe.PooledStockMs);
    }

    [Fact]
    public void Pipeline_pulls_when_gate_open_and_caps_pool_depth()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        pipe.Retarget(ch, ch.Sources[0]);
        pipe.UpdateGates(StealthPipeline.GatherGateMs, 1.0);
        // 每两拍收一发：起拍发包→下拍收割+再发，循环攒池
        for (int i = 0; i < 20 && pipe.PooledStockMs < StealthPipeline.MaxPoolDepthMs; i++)
        {
            pipe.Tick();
            clock.Advance(1);
        }
        Assert.Equal(StealthPipeline.MaxPoolDepthMs, pipe.PooledStockMs); // 池深自钳 15s
        long frozenCalls = loader.Calls;
        pipe.Tick();
        Assert.Equal(frozenCalls, loader.Calls); // 池满即停，不再发包
        Assert.Equal(StealthPipeline.MaxPoolDepthMs, pipe.TakeOver()); // 接管原子取走
        Assert.Equal(0, pipe.PooledStockMs);
    }

    [Fact]
    public void Pipeline_two_consecutive_fails_raise_retake_list()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader { Next = () => null };
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        int retakes = 0;
        pipe.RetakeListNeeded += _ => retakes++;
        pipe.Retarget(ch, ch.Sources[0]);
        pipe.UpdateGates(StealthPipeline.GatherGateMs, 1.0);
        for (int i = 0; i < 6; i++) { pipe.Tick(); clock.Advance(1); }
        Assert.Equal(1, retakes); // 连败 2 次判清单需重取（只触发一次）
        Assert.True(pipe.PooledStockMs == 0);
    }

    [Fact]
    public void Pipeline_freeze_announced_after_two_segment_periods_without_supply()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        int frozen = 0;
        pipe.SupplyFrozen += _ => frozen++;
        pipe.Retarget(ch, ch.Sources[0]);
        pipe.UpdateGates(StealthPipeline.GatherGateMs, 0.0); // 裕度 0：不发包（可测的静止态）
        clock.Advance(2L * StealthPipeline.SegmentPeriodMs + 1);
        pipe.Tick();
        Assert.Equal(1, frozen); // 超 2×切片周期无更新=断供，一次性宣告
        pipe.Tick();
        Assert.Equal(1, frozen); // 不重复宣告
    }

    [Fact]
    public void Pipeline_retarget_clears_pool_and_voids_inflight()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8", "https://s/b.m3u8");
        pipe.Retarget(ch, ch.Sources[0]);
        pipe.UpdateGates(StealthPipeline.GatherGateMs, 1.0);
        pipe.Tick(); // 发第一发
        pipe.Retarget(ch, ch.Sources[1]); // 换向：作废在途+清池
        Assert.Equal(0, pipe.PooledStockMs);
        Assert.Equal(ch.Sources[1].Url, pipe.Target?.Url);
        pipe.Tick();
        Assert.Equal(0, pipe.PooledStockMs); // 旧在途不回新账（代次防串）
    }

    // ---- 刀六·保温心跳（池满校验通过后续拉保鲜，秒切备胎） ----

    /// <summary>攒满池到 15s 上限（成功拉片=校验通过），返回池满时的发包数。</summary>
    private long FillPoolToCap(StealthPipeline pipe, ScriptedLoader loader, MutableClock clock, Channel ch)
    {
        pipe.Retarget(ch, ch.Sources[0]);
        pipe.UpdateGates(StealthPipeline.GatherGateMs, 1.0);
        for (int i = 0; i < 20 && pipe.PooledStockMs < StealthPipeline.MaxPoolDepthMs; i++)
        {
            pipe.Tick();
            clock.Advance(1);
        }
        Assert.Equal(StealthPipeline.MaxPoolDepthMs, pipe.PooledStockMs);
        return loader.Calls;
    }

    [Fact]
    public void Pipeline_keepwarm_heartbeat_pulls_at_interval_after_pool_full()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        long fullCalls = FillPoolToCap(pipe, loader, clock, ch);
        pipe.Tick();
        Assert.Equal(fullCalls, loader.Calls); // 池满即停（心跳未到期不发包）
        clock.Advance(StealthPipeline.KeepWarmIntervalMs);
        pipe.Tick();
        Assert.Equal(fullCalls + 1, loader.Calls); // 心跳到期：续拉一片保鲜
        pipe.Tick(); // 收割心跳
        Assert.Equal(StealthPipeline.MaxPoolDepthMs, pipe.PooledStockMs); // 池深仍自钳，心跳不破上限
    }

    [Fact]
    public void Pipeline_keepwarm_heartbeat_respects_gate()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        long fullCalls = FillPoolToCap(pipe, loader, clock, ch);
        pipe.UpdateGates(StealthPipeline.GatherGateMs - 1, 1.0); // 前台水位掉下让路线
        clock.Advance(StealthPipeline.KeepWarmIntervalMs * 3);
        pipe.Tick();
        Assert.Equal(fullCalls, loader.Calls); // 让路闸关：心跳同停（画面平稳优先令）
    }

    [Fact]
    public void Pipeline_keepwarm_heartbeat_refreshes_supply_clock_no_freeze()
    {
        var clock = new MutableClock();
        var loader = new ScriptedLoader();
        var pipe = new StealthPipeline(loader, clock.Get);
        var ch = MakeChannel("c1", "https://s/a.m3u8");
        int frozen = 0;
        pipe.SupplyFrozen += _ => frozen++;
        FillPoolToCap(pipe, loader, clock, ch);
        // 心跳续命：每 KeepWarmIntervalMs 成功拉一片，断供判据时钟持续刷新（总跨度 3×周期 > 2×冻结线）
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(StealthPipeline.KeepWarmIntervalMs);
            pipe.Tick(); // 发心跳
            pipe.Tick(); // 收心跳
        }
        Assert.Equal(0, frozen); // 保鲜成功：池粮不过期，不判供
        Assert.Equal(StealthPipeline.MaxPoolDepthMs, pipe.PooledStockMs);
    }

    // ---- 链路健康线 ----

    [Fact]
    public void Health_line_bad_frames_push_margin_negative_and_gate_closed()
    {
        var clock = new MutableClock();
        var line = new LinkHealthLine(clock.Get);
        line.ReportBadFrames(10); // 10 帧打满坏帧负项
        Assert.True(line.Margin() < 0);
        Assert.Equal(0.0, line.AllowFactor()); // 裕度 ≤0 偷拉停
    }

    [Fact]
    public void Health_line_margin_clamped()
    {
        var clock = new MutableClock();
        var line = new LinkHealthLine(clock.Get);
        line.ReportBadFrames(10_000); // 打爆单项也不许出界
        Assert.True(line.Margin() >= -1 && line.Margin() <= 1);
    }

    // ---- 会话微卡回退（盲区 10 秒回退+防振铃） ----

    [Fact]
    public void Microlag_stall_on_blind_source_seeks_back_once_then_escalates()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        session.ChangeChannel(MakeChannel("c1", "https://s/a.ts", "https://s/b.ts")); // 直流盲区源（非 .m3u8）
        session.NotifyFirstFrame(); // 回 Playing
        session.NotifyStalled();
        Assert.Equal(new long[] { PlaybackSession.MicrolagSeekBackMs }, player.SeekBackCalls); // 回退 10 秒
        Assert.Equal(PlaybackState.Playing, session.State); // 存量续播：不进 Reconnecting
        session.NotifyStalled(); // 30 秒防振铃窗内第二拍
        Assert.Equal(PlaybackState.Reconnecting, session.State); // 照旧升级自救
        Assert.Single(player.SeekBackCalls); // 不连环回退
    }

    [Fact]
    public void Microlag_stall_on_hls_source_never_seeks_back()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        session.ChangeChannel(MakeChannel("c1", "https://s/a.m3u8", "https://s/b.m3u8")); // HLS 有时间锚
        session.NotifyFirstFrame();
        session.NotifyStalled();
        Assert.Empty(player.SeekBackCalls); // 非盲区不回退
        Assert.Equal(PlaybackState.Reconnecting, session.State); // 直接走既有自救
    }

    [Fact]
    public void Microlag_seekback_account_resets_on_channel_change()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        session.ChangeChannel(MakeChannel("c1", "https://s/a.ts", "https://s/b.ts"));
        session.NotifyFirstFrame();
        session.NotifyStalled(); // 消耗一次回退
        session.ChangeChannel(MakeChannel("c1", "https://s/c.ts", "https://s/d.ts")); // 换台清账
        session.NotifyFirstFrame();
        session.NotifyStalled();
        Assert.Equal(2, player.SeekBackCalls.Count); // 新台重新有回退名额
    }

    // ---- 降档软着陆 ----

    [Fact]
    public void Proactive_trip_with_soft_landing_uses_aligned_switch_without_failover_account()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
        var player = new LoggingPlayer { BufferedMs = 5_000 };
        var session = new PlaybackSession(player, clock)
        {
            SoftLandingEnabled = true, // 仲裁器接上：预防硬切退役
        };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);
        object? aligned = null;
        session.AlignedSwitched += (_, reason) => aligned = reason;
        player.RaiseFirstFrame();
        player.BufferedMs = 20_000;
        session.CheckProactiveFailover(); // 攒满过线取得资格
        player.BufferedMs = 5_000; // 跌破 7 秒线
        clock.Advance(TimeSpan.FromSeconds(2));
        session.CheckProactiveFailover(); // 第 1 拍
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.CheckProactiveFailover()); // 第 2 拍坐实 → 软着陆
        Assert.Equal("proactive soft landing", aligned);
        Assert.Equal(1, session.CurrentLineIndex); // 对齐切下一路
        Assert.Equal(PlaybackState.Buffering, session.State); // 起新线
        Assert.Equal(0, session.FailoverCount); // 不烧故障限速账（对齐软切豁免口径）
    }

    // ---- 竞速回填 ----

    [Fact]
    public void Race_reorder_keeps_playing_line_identity()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("c1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);
        var playing = session.CurrentEndpoint;
        session.ReorderQueueWinnerFirst(2); // 第 3 路先成
        Assert.Equal("https://s/c.m3u8", session.PeekQueueEndpoint(0)?.Url); // 胜者提到队头
        Assert.Same(playing, session.CurrentEndpoint); // 在播线路身份保持（对象同一）
        Assert.Equal("https://s/a.m3u8", session.PeekQueueEndpoint(1)?.Url); // 余者稳定保序
        session.ReorderQueueWinnerFirst(0); // 0 号=无害空操作
        session.ReorderQueueWinnerFirst(99); // 越界=无害
        Assert.Equal("https://s/c.m3u8", session.PeekQueueEndpoint(0)?.Url);
    }

    // ---- 排序合成（健康账两本） ----

    [Fact]
    public void Sort_score_blind_and_microlag_downgrade_and_good_bonus()
    {
        var book = new QualityHealthStore();
        var ch = MakeChannel("c1", "https://s/a.ts");
        book.MarkBlindZone(ch.EpgKey, ch.SourceId, ch.Sources[0].Url);
        var blindEntry = book.Peek(ch.EpgKey, ch.SourceId, ch.Sources[0].Url);
        long blindScore = QualityHealthStore.SortScore(blindEntry, probeDead: false);
        long cleanScore = QualityHealthStore.SortScore(null, probeDead: false);
        Assert.True(blindScore > cleanScore); // 盲区天然贴边：不给头名
        Assert.Equal(10_000, QualityHealthStore.SortScore(null, probeDead: true)); // 死账沉底量级
    }

    // ---- 分片级码率自适应（六项优化④）：迟滞闸转移表 ----

    [Fact]
    public void Bitrate_governor_starts_unclamped_and_downgrades_one_tier_per_interval()
    {
        var gov = new BitrateGovernor();
        long t0 = 1_000_000;
        Assert.Null(gov.Update(0.5, t0)); // 起播满档：不钳（内核估速自由选档）
        Assert.Equal(BitrateGovernor.TierHd, gov.Update(-0.5, t0 + 1_000)); // 链路负账：降一档
        Assert.Equal(BitrateGovernor.TierHd, gov.Update(-0.5, t0 + 5_000)); // 换档间隔内：不连降（迟滞防抖）
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(-0.5, t0 + 11_000)); // 间隔够：再降一档
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(-0.5, t0 + 22_000)); // 已到 sd 底档：封底
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(0, t0 + 23_000));   // margin=0 无账：不降不升
    }

    [Fact]
    public void Bitrate_governor_upgrade_requires_sustained_margin_and_steps_back_one_tier()
    {
        var gov = new BitrateGovernor();
        long t0 = 2_000_000;
        gov.Update(-0.5, t0); // 落到 hd 档
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(-0.5, t0 + 10_000));
        // 裕度达标起窗但未站满 30 秒：不升
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(0.7, t0 + 15_000));
        // 中途裕度平庸：升档窗清零重记
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(0.3, t0 + 20_000));
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(0.7, t0 + 25_000)); // 重新起窗
        Assert.Equal(BitrateGovernor.TierSd, gov.Update(0.7, t0 + 50_000)); // 窗内未满：不升
        Assert.Equal(BitrateGovernor.TierHd, gov.Update(0.7, t0 + 55_000)); // 站满 30 秒：回一档
        Assert.Null(gov.Update(0.7, t0 + 85_000)); // 再站满：回顶档=解除钳制
    }
}
