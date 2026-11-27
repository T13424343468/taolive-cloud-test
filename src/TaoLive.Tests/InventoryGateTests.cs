using TaoLive.Foundation;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc93 第 6 刀（补库存档接线，老板 2026-11-26 拍板）+ 水位链断言：
/// - 首帧出画门槛按库存档分档：vc121 硬门禁（老板 2026-11-28 05:45 令「不管快慢源攒水够7秒才出画」）后
///   两档同口径=7 秒——快源 5 秒档/8 秒强制档/15 秒硬顶全删，唯一开画条件=水位≥TargetMs；
/// - 默认档为 SilentRefetch（c7a0aca 激活），内核门槛必须吃到 7 秒档（低水位出画根治）；
/// - 水位链：MaxWatermarkSeconds=AdvancedTuning.PrefetchWindowSec（设置项 10–300s，出厂 60）；
/// - 防误烧闸 _inventoryEverFilled 判据（ProactiveInventoryFloor）用 TargetMs(7s) 联动，不受出画门槛影响；
/// - 卡后重入门槛仍 StartupThreshold 12 秒，与首帧门槛分家。
/// 全局静态旋钮（AdvancedTuning）测试内还原，不污染其他用例。
/// </summary>
public class InventoryGateTests
{
    // ---- 第 6 刀：首帧出画门槛分档（vc121 硬门禁后两档同口径） ----

    [Fact]
    public void SilentRefetch_opens_frame_at_seven_seconds()
    {
        // vc121 硬门禁（老板 2026-11-28 05:45 令）：vc103 的 1 秒回退口径废止——不管快慢源
        // 必须攒水够 7 秒才出画，攒不满不开画，真死源交看门狗判死切线
        Assert.Equal(7000, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.SilentRefetch));
        Assert.Equal(BufferInventory.SilentRefetchOpenMs, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.SilentRefetch));
        Assert.Equal(BufferInventory.TargetMs, BufferInventory.SilentRefetchOpenMs);
    }

    [Fact]
    public void StartFull_opens_frame_at_seven_seconds()
    {
        Assert.Equal(7000, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.StartFull));
        Assert.Equal(BufferInventory.TargetMs, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.StartFull));
    }

    [Fact]
    public void Default_mode_is_silent_refetch_so_kernel_gate_eats_seven_second_gate()
    {
        // 低水位出画根治断言：默认档=SilentRefetch，内核门槛取值必须=7 秒档（vc121 硬门禁口径）
        AdvancedTuning.ResetToDefaults(); // 出厂口径还原，防其他用例改档影响 NotEqual 判据
        try
        {
            Assert.Equal(BufferInventory.Mode.SilentRefetch, BufferInventory.Default);
            Assert.Equal(7000, BufferInventory.FirstFrameOpenMs(BufferInventory.Default));
            // vc121 硬门禁定稿：出画门槛与 7 秒补库存底水有意同口径（都是 7 秒，一把尺）——
            // 旧「1 秒 ≠ 7 秒防联动」断言随硬门禁废止，改断言同口径防再漂移
            Assert.Equal((double)BufferInventory.SilentRefetchOpenMs, PlaybackSession.ProactiveInventoryFloor.TotalMilliseconds);
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    [Fact]
    public void Open_gate_is_strictly_below_reentry_gate()
    {
        // 两档门槛都不得吃掉卡后重入门槛（12 秒）——首帧快、重入稳，两头分家
        Assert.True(BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.SilentRefetch)
                    < PrefetchCurve.StartupThreshold.TotalMilliseconds);
        Assert.True(BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.StartFull)
                    < PrefetchCurve.StartupThreshold.TotalMilliseconds);
    }

    // ---- 水位链：MaxWatermarkSeconds=PrefetchWindowSec ----

    [Fact]
    public void Watermark_chain_follows_prefetch_window_setting()
    {
        AdvancedTuning.ResetToDefaults(); // 出厂口径还原
        try
        {
            Assert.Equal(AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.PrefetchWindowSec);
            // 水位读数与设置项同源（任意内存入参都吃旋钮，不写死）
            Assert.Equal(AdvancedTuning.PrefetchWindowSec, BufferPolicy.MaxWatermarkSeconds(0));
            Assert.Equal(AdvancedTuning.PrefetchWindowSec, BufferPolicy.MaxWatermarkSeconds(long.MaxValue));
            Assert.Equal(60, BufferPolicy.MaxWatermarkSeconds(0)); // 出厂 60 秒
        }
        finally { AdvancedTuning.ResetToDefaults(); }
    }

    [Fact]
    public void Watermark_chain_tracks_knob_changes()
    {
        AdvancedTuning.ResetToDefaults();
        try
        {
            AdvancedTuning.Apply(
                AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                AdvancedTuning.DefaultListCacheSec, AdvancedTuning.DefaultRetryBaseMs,
                AdvancedTuning.DefaultRetryMaxMs, 120, // 水位旋钮拧到 120 秒
                AdvancedTuning.DefaultLiveLagSec, AdvancedTuning.DefaultFailoverCapPerMin);
            Assert.Equal(120, BufferPolicy.MaxWatermarkSeconds(0));
        }
        finally { AdvancedTuning.ResetToDefaults(); }
    }

    // ---- 防误烧闸判据与出画门槛无关 ----

    [Fact]
    public void Proactive_inventory_floor_uses_target_not_open_gate()
    {
        AdvancedTuning.ResetToDefaults();
        try
        {
            // 出厂 LiveLagSec=60：触发线=min(7 秒, 60*0.5 秒)=7 秒（TargetMs 联动；vc102 两档门槛统一 7 秒后与出画档同值）
            Assert.Equal(BufferInventory.TargetMs, PlaybackSession.ProactiveInventoryFloor.TotalMilliseconds);
            Assert.Equal(7000, PlaybackSession.ProactiveInventoryFloor.TotalMilliseconds);
        }
        finally { AdvancedTuning.ResetToDefaults(); }
    }
}
