using TaoLive.Foundation;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc93 第 6 刀（补库存档接线，老板 2026-11-26 拍板）+ 水位链断言：
/// - 首帧出画门槛按库存档分档：SilentRefetch=1 秒出画（先出画后补库存）；StartFull=7 秒（出就流畅）；
/// - 默认档为 SilentRefetch（c7a0aca 激活），内核门槛必须吃到 1 秒档（空拨根治）；
/// - 水位链：MaxWatermarkSeconds=AdvancedTuning.PrefetchWindowSec（设置项 10–300s，出厂 60）；
/// - 防误烧闸 _inventoryEverFilled 判据（ProactiveInventoryFloor）用 TargetMs(7s) 联动，不受出画门槛影响；
/// - 卡后重入门槛仍 StartupThreshold 12 秒，与首帧门槛分家。
/// 全局静态旋钮（AdvancedTuning）测试内还原，不污染其他用例。
/// </summary>
public class InventoryGateTests
{
    // ---- 第 6 刀：首帧出画门槛分档 ----

    [Fact]
    public void SilentRefetch_opens_frame_at_one_second()
    {
        Assert.Equal(1000, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.SilentRefetch));
        Assert.Equal(BufferInventory.SilentRefetchOpenMs, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.SilentRefetch));
    }

    [Fact]
    public void StartFull_opens_frame_at_seven_seconds()
    {
        Assert.Equal(7000, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.StartFull));
        Assert.Equal(BufferInventory.TargetMs, BufferInventory.FirstFrameOpenMs(BufferInventory.Mode.StartFull));
    }

    [Fact]
    public void Default_mode_is_silent_refetch_so_kernel_gate_eats_one_second()
    {
        // 空拨根治断言：默认档=SilentRefetch，内核门槛取值必须=1 秒档
        Assert.Equal(BufferInventory.Mode.SilentRefetch, BufferInventory.Default);
        Assert.Equal(1000, BufferInventory.FirstFrameOpenMs(BufferInventory.Default));
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
            // 出厂 LiveLagSec=60：触发线=min(7 秒, 60*0.5 秒)=7 秒（TargetMs 联动，非 1 秒出画档）
            Assert.Equal(BufferInventory.TargetMs, PlaybackSession.ProactiveInventoryFloor.TotalMilliseconds);
            Assert.Equal(7000, PlaybackSession.ProactiveInventoryFloor.TotalMilliseconds);
            Assert.NotEqual(BufferInventory.SilentRefetchOpenMs,
                (int)PlaybackSession.ProactiveInventoryFloor.TotalMilliseconds);
        }
        finally { AdvancedTuning.ResetToDefaults(); }
    }
}
