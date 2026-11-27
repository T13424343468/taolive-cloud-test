using System;
using TaoLive.Foundation;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc101 老板令「并发整个都不要了」：并发预取总开关默认拆除——
/// 默认关（装配点退回纯单路链路），开关仅保留回滚口径；静态开关跨测试共享，用 try/finally 存恢复，防串染。
/// </summary>
public class Vc96PrefetchGateTests
{
    [Fact]
    public void Prefetch_gate_defaults_off()
    {
        Assert.False(ParallelPrefetcher.Enabled); // vc101 默认关：并发预取整体拆除
    }

    [Fact]
    public void Prefetch_gate_can_be_toggled_and_restored()
    {
        try
        {
            ParallelPrefetcher.Enabled = true;
            Assert.True(ParallelPrefetcher.Enabled); // 开关仍可拨（回滚口径保留）
        }
        finally
        {
            ParallelPrefetcher.Enabled = false; // 存恢复：不串染其他用例
        }
        Assert.False(ParallelPrefetcher.Enabled);
    }
}
