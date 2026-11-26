using TaoLive.Foundation;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 覆盖冲刺 A1（vc90 分支覆盖补账）：BufferPolicy 水位三态与库存两档、
/// AdvancedTuning 全量钳制/收敛/一键还原、SessionTrace 台账出口三态。
/// 全局静态旋钮（AdvancedTuning）测试内还原，不污染其他用例。
/// </summary>
public class CoverageSprintA1
{
    // ---- BufferPolicy ----

    [Fact]
    public void Observe_unsteady_builds_up_even_with_deep_buffer()
    {
        // 网络抖动优先：水位再深也先攒，不追边缘
        Assert.Equal(BufferAction.BuildUp, BufferPolicy.Observe(60, networkSteady: false, 0));
    }

    [Fact]
    public void Observe_below_low_watermark_builds_up()
    {
        // 默认上限 60 秒，低水位线=20 秒；低于即攒
        Assert.Equal(BufferAction.BuildUp, BufferPolicy.Observe(19.9, networkSteady: true, 0));
    }

    [Fact]
    public void Observe_steady_below_ninety_percent_catches_up()
    {
        // 平稳且未到 0.9*上限：平滑追回直播边缘
        Assert.Equal(BufferAction.CatchUp, BufferPolicy.Observe(30, networkSteady: true, 0));
    }

    [Fact]
    public void Observe_steady_at_edge_holds_edge()
    {
        // 平稳且水位贴满（>=0.9*上限）：保持边缘零延迟感
        Assert.Equal(BufferAction.HoldEdge, BufferPolicy.Observe(54, networkSteady: true, 0));
        Assert.Equal(BufferAction.HoldEdge, BufferPolicy.Observe(60, networkSteady: true, 0));
    }

    [Fact]
    public void MaxWatermark_seconds_follows_prefetch_knob()
    {
        try
        {
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
                AdvancedTuning.DefaultRetryMaxMs, 30);
            Assert.Equal(30, BufferPolicy.MaxWatermarkSeconds(0)); // totalMemBytes 兼容参数不参与分档
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Inventory_default_is_silent_refetch()
    {
        // 老板 2026-11-25 令：默认「先开画后补库存」
        Assert.Equal(BufferInventory.Mode.SilentRefetch, BufferInventory.Default);
    }

    [Fact]
    public void StartCachingMs_startfull_floor_at_target()
    {
        // StartFull：库存档兜底，不低 7 秒
        Assert.Equal(BufferInventory.TargetMs, BufferInventory.StartCachingMs(BufferInventory.Mode.StartFull, 1000));
        Assert.Equal(9000, BufferInventory.StartCachingMs(BufferInventory.Mode.StartFull, 9000));
    }

    [Fact]
    public void StartCachingMs_silent_refetch_uses_ladder_directly()
    {
        // SilentRefetch：按阶梯低缓存先开画，不抬到 7 秒
        Assert.Equal(1000, BufferInventory.StartCachingMs(BufferInventory.Mode.SilentRefetch, 1000));
        Assert.Equal(9000, BufferInventory.StartCachingMs(BufferInventory.Mode.SilentRefetch, 9000));
    }

    [Fact]
    public void FillWindowCachingMs_takes_largest_of_target_and_ladder()
    {
        Assert.Equal(BufferInventory.FillWindowMs, BufferInventory.FillWindowCachingMs(1000)); // 阶梯小：取满窗口 30 秒
        Assert.Equal(40_000, BufferInventory.FillWindowCachingMs(40_000)); // 阶梯大：按阶梯
    }

    // ---- AdvancedTuning ----

    [Fact]
    public void Apply_clamps_all_knobs_to_legal_range()
    {
        try
        {
            AdvancedTuning.Apply(
                failGate: 999, reprobeMs: 999_999_999, listCacheSec: 9999,
                retryBaseMs: 999_999_999, retryMaxMs: 99_999_999_999,
                prefetchWindowSec: 9999, liveLagSec: 999, failoverCapPerMin: 99);

            Assert.Equal(AdvancedTuning.FailGateMax, AdvancedTuning.SelfHealFailGate);
            Assert.Equal(AdvancedTuning.ReprobeMsMax, AdvancedTuning.SelfHealReprobeMs);
            Assert.Equal(AdvancedTuning.ListCacheSecMax, AdvancedTuning.ListCacheSec);
            Assert.Equal(AdvancedTuning.RetryBaseMsMax, AdvancedTuning.RetryBaseMs);
            Assert.Equal(AdvancedTuning.RetryMaxMsMax, AdvancedTuning.RetryMaxMs);
            Assert.Equal(AdvancedTuning.PrefetchWindowSecMax, AdvancedTuning.PrefetchWindowSec);
            Assert.Equal(AdvancedTuning.LiveLagSecMax, AdvancedTuning.LiveLagSec);
            Assert.Equal(AdvancedTuning.FailoverCapMax, AdvancedTuning.FailoverCapPerMin);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Apply_clamps_to_minimums_and_converges_retry_max_to_base()
    {
        try
        {
            // retryMax 钳到下限后仍小于 retryBase：按 retryBase 收敛（防退避窗倒挂）
            AdvancedTuning.Apply(
                failGate: 0, reprobeMs: 0, listCacheSec: 0,
                retryBaseMs: 30 * 60_000, retryMaxMs: 0,
                prefetchWindowSec: 0, liveLagSec: -5, failoverCapPerMin: 0);

            Assert.Equal(AdvancedTuning.FailGateMin, AdvancedTuning.SelfHealFailGate);
            Assert.Equal(AdvancedTuning.ReprobeMsMin, AdvancedTuning.SelfHealReprobeMs);
            Assert.Equal(AdvancedTuning.ListCacheSecMin, AdvancedTuning.ListCacheSec);
            Assert.Equal(AdvancedTuning.RetryBaseMsMax, AdvancedTuning.RetryBaseMs);
            Assert.Equal(AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs); // 收敛口径
            Assert.Equal(AdvancedTuning.PrefetchWindowSecMin, AdvancedTuning.PrefetchWindowSec);
            Assert.Equal(AdvancedTuning.LiveLagSecMin, AdvancedTuning.LiveLagSec); // 0=贴边播
            Assert.Equal(AdvancedTuning.FailoverCapMin, AdvancedTuning.FailoverCapPerMin); // 调不灭
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Apply_short_overloads_keep_unset_knobs()
    {
        try
        {
            // 5 参重载：预取/落后/换源封顶沿用现值
            AdvancedTuning.Apply(2, 120_000, 30, 60_000, 120_000);
            Assert.Equal(2, AdvancedTuning.SelfHealFailGate);
            Assert.Equal(120_000, AdvancedTuning.SelfHealReprobeMs);
            Assert.Equal(30, AdvancedTuning.ListCacheSec);
            Assert.Equal(60_000, AdvancedTuning.RetryBaseMs);
            Assert.Equal(120_000, AdvancedTuning.RetryMaxMs);
            Assert.Equal(AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.PrefetchWindowSec);

            // 6 参重载：落后/换源封顶沿用现值
            AdvancedTuning.Apply(3, 120_000, 45, 60_000, 120_000, 45);
            Assert.Equal(45, AdvancedTuning.PrefetchWindowSec);
            Assert.Equal(AdvancedTuning.DefaultLiveLagSec, AdvancedTuning.LiveLagSec);

            // 7 参重载：换源封顶沿用现值
            AdvancedTuning.Apply(4, 120_000, 50, 60_000, 120_000, 50, 10);
            Assert.Equal(50, AdvancedTuning.PrefetchWindowSec);
            Assert.Equal(10, AdvancedTuning.LiveLagSec);
            Assert.Equal(AdvancedTuning.DefaultFailoverCapPerMin, AdvancedTuning.FailoverCapPerMin);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void ResetToDefaults_restores_factory_values()
    {
        try
        {
            AdvancedTuning.Apply(20, 30 * 60_000, 600, 30 * 60_000, 24 * 60 * 60_000, 300, 60, 10);
            AdvancedTuning.ResetToDefaults();

            Assert.Equal(AdvancedTuning.DefaultFailGate, AdvancedTuning.SelfHealFailGate);
            Assert.Equal(AdvancedTuning.DefaultReprobeMs, AdvancedTuning.SelfHealReprobeMs);
            Assert.Equal(AdvancedTuning.DefaultListCacheSec, AdvancedTuning.ListCacheSec);
            Assert.Equal(AdvancedTuning.DefaultRetryBaseMs, AdvancedTuning.RetryBaseMs);
            Assert.Equal(AdvancedTuning.DefaultRetryMaxMs, AdvancedTuning.RetryMaxMs);
            Assert.Equal(AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.PrefetchWindowSec);
            Assert.Equal(AdvancedTuning.DefaultLiveLagSec, AdvancedTuning.LiveLagSec);
            Assert.Equal(AdvancedTuning.DefaultFailoverCapPerMin, AdvancedTuning.FailoverCapPerMin);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    // ---- SessionTrace ----

    [Fact]
    public void Emit_delivers_message_to_wired_sink()
    {
        string? received = null;
        try
        {
            SessionTrace.Sink = msg => received = msg;
            SessionTrace.Emit("[测试] 打点一条");
            Assert.Equal("[测试] 打点一条", received);
        }
        finally
        {
            SessionTrace.Sink = null; // 还原出口，不污染其他用例
        }
    }

    [Fact]
    public void Emit_swallows_sink_exception()
    {
        // 台账出口异常不反灌业务链路
        try
        {
            SessionTrace.Sink = _ => throw new InvalidOperationException("台账炸了");
            SessionTrace.Emit("[测试] 该打点不抛");
        }
        finally
        {
            SessionTrace.Sink = null;
        }
    }

    [Fact]
    public void Emit_without_sink_is_noop()
    {
        // 未接线（单测/未装配环境）即为空操作，零副作用
        SessionTrace.Sink = null;
        SessionTrace.Emit("[测试] 无出口静默");
    }
}
