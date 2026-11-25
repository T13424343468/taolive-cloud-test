using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 自愈5·NetGuard 断网判定与恢复沿（方案书 v82，白屏镜红①补测）：
/// 单次失败不误判（阈值=自愈门槛旋钮，出厂 3）、连续失败达阈值翻断网、断网后任一成功出恢复沿且清账、
/// 成功即重置失败连计（防历史抖动累计误判）。
/// </summary>
public class NetGuardTests
{
    [Fact]
    public void Single_failure_does_not_trip_offline()
    {
        var guard = new NetGuard();
        Assert.False(guard.OnFailure());
        Assert.False(guard.Offline);
    }

    [Fact]
    public void Consecutive_failures_reach_threshold_trip_offline()
    {
        var guard = new NetGuard();
        for (int i = 0; i < NetGuard.OfflineAfterFails - 1; i++)
        {
            Assert.False(guard.OnFailure());
        }
        Assert.False(guard.Offline);
        Assert.True(guard.OnFailure());
        Assert.True(guard.Offline);
    }

    [Fact]
    public void Success_after_offline_returns_recovery_edge_and_resets()
    {
        var guard = new NetGuard();
        for (int i = 0; i < NetGuard.OfflineAfterFails; i++) guard.OnFailure();
        Assert.True(guard.Offline);

        Assert.True(guard.OnSuccess()); // 恢复沿：断网 → 在线
        Assert.False(guard.Offline);
        Assert.False(guard.OnSuccess()); // 已在线：无沿
    }

    [Fact]
    public void Fail_streak_resets_on_success()
    {
        var guard = new NetGuard();
        for (int i = 0; i < NetGuard.OfflineAfterFails - 1; i++) guard.OnFailure();
        guard.OnSuccess(); // 成功即清失败连计
        Assert.False(guard.OnFailure()); // 重数不触发断网
        Assert.False(guard.Offline);
    }

    [Fact]
    public void Threshold_couples_to_selfheal_gate_knob()
    {
        // 老板 2026-11-17 拍板（假旋钮接通第 1 位）：门槛读 AdvancedTuning.SelfHealFailGate，
        // 出厂 3（原常量 4 退役为改动前口径）；门槛 1 时单次失败即断网。
        var guard = new NetGuard();
        TaoLive.Foundation.AdvancedTuning.Apply(1, TaoLive.Foundation.AdvancedTuning.DefaultReprobeMs,
            TaoLive.Foundation.AdvancedTuning.DefaultListCacheSec, TaoLive.Foundation.AdvancedTuning.DefaultRetryBaseMs,
            TaoLive.Foundation.AdvancedTuning.DefaultRetryMaxMs, TaoLive.Foundation.AdvancedTuning.DefaultPrefetchWindowSec,
            TaoLive.Foundation.AdvancedTuning.DefaultLiveLagSec);
        try
        {
            Assert.Equal(1, NetGuard.OfflineAfterFails);
            Assert.True(guard.OnFailure());
            Assert.True(guard.Offline);
            Assert.True(guard.OnSuccess()); // 恢复沿仍按既有口径
        }
        finally
        {
            TaoLive.Foundation.AdvancedTuning.ResetToDefaults();
        }
    }
}
