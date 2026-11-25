using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 收口批② Tv 壳层五件的可测层：五标签过滤（照 vc53 DrawerSupport 口径）、
/// 等网就绪（轻量 TCP 探测+超时放行）、列表健康点三态（只读账）。
/// </summary>
public class TvShellPortsTests
{
    private static Channel Ch(string id, string name, ChannelCategory cat) => new()
    {
        Id = id, SourceId = id, Name = name, Category = cat, EpgKey = id,
        SourceEndpoints =
        {
            new SourceEndpoint($"https://s/{id}.m3u8", SourceKind.Cdn, 10),
        },
    };

    private static readonly List<Channel> Catalog =
    [
        Ch("a", "CCTV-1", ChannelCategory.Cctv),
        Ch("b", "CGTN", ChannelCategory.Cgtn),
        Ch("c", "风云剧场", ChannelCategory.Special),
        Ch("d", "湖南卫视", ChannelCategory.Provincial),
        Ch("e", "上海台", ChannelCategory.City),
        Ch("f", "自建源", ChannelCategory.UserSource),
    ];

    private static bool IsFav(Channel c) => c.Id == "a";

    // ---- 五标签 ----

    [Fact]
    public void Labels_match_vc53_roster()
    {
        Assert.Equal(["全部", "收藏", "央视", "卫视", "地方台"],
            Enum.GetValues<ListTabs.Tab>().Select(ListTabs.Label));
    }

    [Fact]
    public void NextTab_cycles_all_fav_cctv_sat_local()
    {
        Assert.Equal(ListTabs.Tab.Favorite, ListTabs.NextTab(ListTabs.Tab.All, rightward: true));
        Assert.Equal(ListTabs.Tab.All, ListTabs.NextTab(ListTabs.Tab.Local, rightward: true));
        Assert.Equal(ListTabs.Tab.Local, ListTabs.NextTab(ListTabs.Tab.All, rightward: false));
    }

    [Fact]
    public void Filter_follows_vc53_category_mapping()
    {
        Assert.Equal(6, ListTabs.Filter(ListTabs.Tab.All, Catalog, IsFav).Count);
        Assert.Equal(["a"], ListTabs.Filter(ListTabs.Tab.Favorite, Catalog, IsFav).Select(c => c.Id));
        // CGTN/专项并入央视（vc53 v22 第 7 项口径）
        Assert.Equal(["a", "b", "c"], ListTabs.Filter(ListTabs.Tab.Cctv, Catalog, IsFav).Select(c => c.Id));
        Assert.Equal(["d"], ListTabs.Filter(ListTabs.Tab.Satellite, Catalog, IsFav).Select(c => c.Id));
        // 地方台只放城市台/自有源
        Assert.Equal(["e", "f"], ListTabs.Filter(ListTabs.Tab.Local, Catalog, IsFav).Select(c => c.Id));
    }

    // ---- 等网就绪 ----

    [Fact]
    public async Task NetReadiness_returns_on_first_probe_ok()
    {
        var probes = 0;
        var ok = await NetReadiness.WaitAsync(5_000, (_, _, _) =>
        {
            probes++;
            return Task.FromResult(true);
        });
        Assert.True(ok);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task NetReadiness_times_out_and_releases_for_default_channel()
    {
        var ok = await NetReadiness.WaitAsync(60, (_, _, _) => Task.FromResult(false));
        Assert.False(ok); // 超时照常放行（进默认台，起播失败走既有重试）
    }

    [Fact]
    public async Task NetReadiness_probe_exception_keeps_probing_until_timeout()
    {
        var ok = await NetReadiness.WaitAsync(60, (_, _, _) => throw new IOException("断网"));
        Assert.False(ok);
    }

    // ---- 健康点三态 ----

    private static async Task<ChannelPrecheck> PrecheckAfterRoundsAsync(int okRounds, Channel ch)
    {
        var precheck = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
        // 核验时间用真实当前时刻（Unix 毫秒）：24 小时新鲜期（老板令 2026-11-15）会把 1970 年的假时间戳读作作废
        long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < okRounds; i++)
            precheck.ReportSourceResult(ch, ok: true, nowMs: baseMs + i);
        await Task.Yield();
        return precheck;
    }

    [Fact]
    public async Task Health_mark_green_after_one_ok()
    {
        // 老板 2026-11-16 深夜降门槛令：最近 1 轮通过即绿（原连续 3 轮口径作废），变绿快 3 倍
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var one = await PrecheckAfterRoundsAsync(1, ch);
        Assert.Equal(ChannelHealth.Alive, one.MarkOf(ch)); // 1 轮通过=绿
    }

    [Fact]
    public async Task Health_mark_dead_when_all_fail_and_grey_when_untouched()
    {
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var untouched = new ChannelPrecheck(_ => Task.FromResult(true));
        Assert.Equal(ChannelHealth.Checking, untouched.MarkOf(ch)); // 未探=灰
        var dead = await PrecheckAfterRoundsAsync(0, ch);
        dead.ReportSourceResult(ch, ok: false, nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        dead.MarkState(ch, ChannelHealth.Dead);
        Assert.Equal(ChannelHealth.Dead, dead.MarkOf(ch)); // 全败=红
    }

    [Fact]
    public async Task Health_mark_green_when_any_group_source_reaches_threshold()
    {
        // 绿门槛频道级语义（2026-11-16 断点一修复）：同台两个来源变体（同 EPG 键、不同 SourceId），
        // 播放只给正在播的变体记活账——列表行绑定的另一个变体也要读到达标账=整台绿
        var row = Ch("a", "CCTV-1", ChannelCategory.Cctv);        // 列表行绑定的变体
        var playing = Ch("a2", "CCTV-1", ChannelCategory.Cctv);   // 正在播的变体（同 EPG 键）
        playing.EpgKey = row.EpgKey;
        var precheck = await PrecheckAfterRoundsAsync(3, playing);
        precheck.MarkState(row, ChannelHealth.Alive); // 台级账已是 Alive（播放回写同路）
        Assert.Equal(ChannelHealth.Alive, precheck.MarkOf(row)); // 任一来源达标=整台绿
    }

    [Fact]
    public async Task Green_threshold_crossing_fires_state_changed()
    {
        // 绿门槛跨越补发刷新（2026-11-16 断点二修复）：第 3 轮探通跨过门槛时，
        // 频道级状态可能未翻转（上轮已 Alive），须补发一次事件让列表立即重绘
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var precheck = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
        var events = new List<string>();
        precheck.StateChanged += (epgKey, state) => events.Add($"{epgKey}:{state}");
        long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        precheck.ReportSourceResult(ch, ok: true, nowMs: baseMs);      // 首通：频道翻转事件 + 跨绿事件（门槛 1，探活账 0→1=真实变化）各发一次
        precheck.ReportSourceResult(ch, ok: true, nowMs: baseMs + 1);  // 第 2 轮：已绿无翻转无跨越，不发
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { $"{ch.EpgKey}:{ChannelHealth.Alive}", $"{ch.EpgKey}:{ChannelHealth.Alive}" }, events);
    }

    // ---- 健康账重启续账（2026-11-16 老板批第①条：账本落盘+重启不清灰）----

    private static string TempLedgerDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taolive-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Health_ledger_reload_restores_green_and_oks_across_restart()
    {
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var dir = TempLedgerDir();
        try
        {
            // 上一世：3 轮探通=绿，落盘
            var p1 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
            p1.AttachHealthStore(new SourceHealthStore(dir));
            long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < 3; i++) p1.ReportSourceResult(ch, ok: true, nowMs: baseMs + i);
            Assert.Equal(ChannelHealth.Alive, p1.MarkOf(ch));
            p1.HealthStore!.FlushNow();

            // 新一世：挂账即载入——绿点直接回显，连续成功账不清零
            var p2 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
            p2.AttachHealthStore(new SourceHealthStore(dir));
            Assert.Equal(3, p2.SourceHealthOf(ch)!.ConsecutiveOks);
            Assert.Equal(ChannelHealth.Alive, p2.MarkOf(ch)); // 重启不清灰：老账即显示
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Health_ledger_reload_one_ok_shows_green()
    {
        // 老板降门槛令（2026-11-16 深夜）：1 轮即绿后，重启续账的老账 1 轮也直接回显绿，不虚标不闪灰
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var dir = TempLedgerDir();
        try
        {
            var p1 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
            p1.AttachHealthStore(new SourceHealthStore(dir));
            long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p1.ReportSourceResult(ch, ok: true, nowMs: baseMs);
            p1.HealthStore!.FlushNow();

            var p2 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
            p2.AttachHealthStore(new SourceHealthStore(dir));
            Assert.Equal(ChannelHealth.Alive, p2.Of(ch)); // 台级回显老账（不闪灰）
            Assert.Equal(ChannelHealth.Alive, p2.MarkOf(ch)); // 绿点门槛 1 轮：老账直接回绿
            p2.ReportSourceResult(ch, ok: true, nowMs: baseMs + 100); // 连续成功账继续累加不清零
            Assert.Equal(2, p2.SourceHealthOf(ch)!.ConsecutiveOks);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Health_ledger_reload_dead_entry_shows_red_within_ttl()
    {
        // 上一世全败的台：重启后按死账回显红（DeadTtl 内），预检轮继续校准
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var dir = TempLedgerDir();
        try
        {
            var p1 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
            p1.AttachHealthStore(new SourceHealthStore(dir));
            p1.ReportSourceResult(ch, ok: false, nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            p1.HealthStore!.FlushNow();

            var p2 = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
            p2.AttachHealthStore(new SourceHealthStore(dir));
            Assert.Equal(ChannelHealth.Dead, p2.MarkOf(ch));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
