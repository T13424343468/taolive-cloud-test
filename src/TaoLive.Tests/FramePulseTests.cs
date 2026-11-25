using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 帧回调观测（方案书 v76/v20）：滑窗 P95、间隙不折算卡顿、连续 3 窗判疑似卡顿。
/// 时钟全注入快拍推进，不真等。
/// </summary>
public class FramePulseTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static DateTimeOffset T(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void 空窗与样本不足_返回null()
    {
        var pulse = new FramePulse();
        Assert.Null(pulse.WindowP95Ms()); // 未记录

        pulse.Record(T(0));
        Assert.Null(pulse.WindowP95Ms()); // 仅 1 拍无间隔
    }

    [Fact]
    public void 稳定出帧_P95在阈内()
    {
        DateTimeOffset now = T0; // 注入钟与打点同步推进（滑窗按钟裁剪）
        var pulse = new FramePulse(clock: () => now);
        // 100 帧 × 100ms 间隔 → P95 ≈ 100ms，远低 250ms 阈
        for (int i = 1; i <= 100; i++) { now = T(i * 100); pulse.Record(now); }
        var p95 = pulse.WindowP95Ms();
        Assert.NotNull(p95);
        Assert.True(p95 <= FramePulse.P95ThresholdMs, $"P95={p95} 应 ≤ {FramePulse.P95ThresholdMs}");
        Assert.False(pulse.StallSuspicion);
    }

    [Fact]
    public void 卡顿间隔抬升P95_单窗超阈不判卡_连续三窗才判()
    {
        DateTimeOffset now = T0;
        var pulse = new FramePulse(clock: () => now);

        // 连续 3 正常窗：每窗 100 拍 × 50ms 间隔，取数推进游程，应全程不判卡
        int nowMs = 0;
        for (int w = 0; w < 3; w++)
        {
            for (int i = 0; i <= 100; i++) { nowMs += 50; now = T(nowMs); pulse.Record(now); }
            nowMs += 10_000 - 100 * 50; // 补满一窗
            now = T(nowMs);
            Assert.False(pulse.StallSuspicion);
        }

        // 连续 3 卡顿窗：每窗 100 拍 × 500ms 间隔（P95=500 > 250）→ 第三窗判疑似卡顿
        // 与壳层一致按 10 秒节拍逐窗取数评估（满窗才计，游程连续；攒到最后一次取数会丢过期样本）
        for (int w = 0; w < 3; w++)
        {
            for (int i = 0; i < 10; i++) { nowMs += 500; now = T(nowMs); pulse.Record(now); }
            nowMs += 10_000 - 10 * 500; // 补满一窗
            now = T(nowMs);
            if (w < 2) Assert.False(pulse.StallSuspicion, $"第 {w + 1} 窗不足连续 3 窗，不应提前判卡");
            else Assert.True(pulse.StallSuspicion, "连续 3 窗 P95>250ms 应判疑似卡顿");
        }
    }

    [Fact]
    public void 断流间隙不折算卡顿_恢复后P95回到阈内()
    {
        var pulse = new FramePulse(clock: () => T(0));
        for (int i = 0; i <= 50; i++) pulse.Record(T(i * 100));
        // 断流 30 秒（>3 秒连续出帧上限）：只在恢复后打 10 拍正常帧
        for (int i = 0; i <= 10; i++) pulse.Record(T(30_000 + i * 100));
        // 断流间隙不计样本，但老窗里断流前的样本仍在 10 秒窗外 → 有效样本只来自恢复段
        var p95 = pulse.WindowP95Ms();
        Assert.NotNull(p95);
        Assert.True(p95 <= FramePulse.P95ThresholdMs, $"恢复后 P95={p95} 应回阈内");
    }

    [Fact]
    public void 滑窗只看最近一窗_老样本过期即出窗()
    {
        var pulse = new FramePulse(clock: () => T(0));
        // 前 100 拍卡顿间隔 500ms（超阈）
        for (int i = 0; i <= 100; i++) pulse.Record(T0.AddMilliseconds(i * 500));
        var stale = pulse.WindowP95Ms(); // 时钟钉死 T(0)，全部样本仍在窗内
        Assert.True(stale > FramePulse.P95ThresholdMs);

        // 时钟推到 20 秒后，只补正常拍
        var late = new FramePulse(clock: () => T(20_000));
        for (int i = 0; i <= 100; i++) late.Record(T(10_000 + i * 100));
        Assert.True(late.WindowP95Ms() <= FramePulse.P95ThresholdMs);
    }
}
