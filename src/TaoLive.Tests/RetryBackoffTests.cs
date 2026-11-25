using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 收口批③：OK 重试接退避账（指数退避管重试节奏，断路器管换源，共账不共判据）+ 会话 LRU 按内存分档。
/// 计时按注入单调钟快拍推进，不真等。
/// </summary>
public class RetryBackoffTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    private sealed class MonoClock
    {
        public long Now;
        public long Read() => Now;
    }

    // ---- 退避账：指数节奏与封顶 ----

    [Fact]
    public void First_retry_passes_then_immediate_retry_throttled_for_full_base_window()
    {
        var mono = new MonoClock();
        var account = new RetryBackoffAccount(mono.Read);

        Assert.True(account.TryAcquire("ch", out _));              // 首试放行
        Assert.False(account.TryAcquire("ch", out var waitMs));    // 立刻再试被退避
        Assert.Equal(AdvancedTuning.RetryBaseMs, waitMs);          // 还须等满起步窗

        mono.Now += AdvancedTuning.RetryBaseMs;
        Assert.True(account.TryAcquire("ch", out _));              // 窗满放行（第 2 次）
        Assert.False(account.TryAcquire("ch", out var waitMs2));
        Assert.Equal(Math.Min(AdvancedTuning.RetryBaseMs * 2, AdvancedTuning.RetryMaxMs), waitMs2); // 指数翻倍
    }

    [Fact]
    public void Delay_doubles_and_caps_at_max()
    {
        var account = new RetryBackoffAccount();
        Assert.Equal(AdvancedTuning.RetryBaseMs, account.DelayMsFor(1));
        Assert.Equal(Math.Min(AdvancedTuning.RetryBaseMs * 2, AdvancedTuning.RetryMaxMs), account.DelayMsFor(2));
        Assert.Equal(Math.Min(AdvancedTuning.RetryBaseMs * 4, AdvancedTuning.RetryMaxMs), account.DelayMsFor(3));
        Assert.Equal(AdvancedTuning.RetryMaxMs, account.DelayMsFor(20)); // 封顶，免溢出
    }

    [Fact]
    public void Channels_keep_separate_ledgers()
    {
        var mono = new MonoClock();
        var account = new RetryBackoffAccount(mono.Read);
        Assert.True(account.TryAcquire("a", out _));
        Assert.True(account.TryAcquire("b", out _)); // 跨台不互扰
    }

    // ---- 管理器接线：放行判定 + 起播清账 + 容量分档 ----

    [Fact]
    public void Manager_retry_gate_passes_first_and_throttles_until_playback_clears()
    {
        var mono = new MonoClock();
        var player = new LoggingPlayer();
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(player, new FixedClock(T0)),
            monotonicMs: mono.Read);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        manager.SwitchTo(ch);
        player.RaiseError("E1"); // 单路耗尽 → Failed
        Assert.Equal(PlaybackState.Failed, manager.Current!.State);

        Assert.True(manager.TryManualRetry("cctv1", out _));  // 首次放行
        Assert.False(manager.TryManualRetry("cctv1", out _)); // 未起播再按被退避

        // 重试起播成功（Playing）→ 清账：再失败重试重新从起步计
        mono.Now += AdvancedTuning.RetryBaseMs;
        manager.SwitchTo(ch); // 模拟 OK 重试（PlayAt → SwitchTo）
        manager.TryGet("cctv1")!.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, manager.Current!.State);
        player.RaiseError("E2");
        Assert.True(manager.TryManualRetry("cctv1", out _)); // 起步窗内即可再放行
    }

    [Fact]
    public void Capacity_for_splits_by_memory_class_threshold()
    {
        Assert.Equal(PlaybackSessionManager.LowMemorySessions,
            PlaybackSessionManager.CapacityFor(PlaybackSessionManager.LowMemoryClassThresholdMb));
        Assert.Equal(PlaybackSessionManager.HighMemorySessions,
            PlaybackSessionManager.CapacityFor(PlaybackSessionManager.LowMemoryClassThresholdMb + 1));
        Assert.Equal(8, PlaybackSessionManager.CapacityFor(256));
    }

    [Fact]
    public void Eviction_respects_retained_capacity()
    {
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(T0)))
        {
            RetainedCapacity = PlaybackSessionManager.LowMemorySessions, // 低配档 4
        };
        for (int i = 1; i <= 6; i++)
        {
            manager.SwitchTo(MakeChannel($"ch{i}", $"https://s/{i}.m3u8"));
        }
        Assert.Equal(PlaybackSessionManager.LowMemorySessions, manager.Sessions.Count); // 超容即淘汰
        Assert.NotNull(manager.Current);
    }
}
