using System;
using TaoLive.Foundation;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc96 试探版并发预取总开关：
/// 默认开——单路起步、水位不涨才逐档升级（封顶 2 路额外），被拒即缩档并记源脾气账。
/// 静态开关跨测试共享，用 try/finally 存恢复，防串染。
/// </summary>
public class Vc96PrefetchGateTests
{
    [Fact]
    public void Prefetch_gate_defaults_on()
    {
        Assert.True(ParallelPrefetcher.Enabled); // 默认开：试探版口径，单路起步不等于关预取
    }

    [Fact]
    public void Prefetch_gate_can_be_disabled_and_restored()
    {
        try
        {
            ParallelPrefetcher.Enabled = false;
            Assert.False(ParallelPrefetcher.Enabled); // 关闸口径：装配点退回单路链路
        }
        finally
        {
            ParallelPrefetcher.Enabled = true; // 存恢复：不串染其他用例
        }
        Assert.True(ParallelPrefetcher.Enabled);
    }
}
