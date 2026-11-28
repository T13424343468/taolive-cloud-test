using System;
using Xunit;
using TaoLive.Foundation;

namespace TaoLive.Tests;

/// <summary>
/// 源端频控共享闸纯逻辑测试（vc144 老板令「403 收敛」）：冷却进出、步进封顶、成功清账、
/// 主机归账（同主机共账、跨主机不串）、频控信号口径（403/429 才记账是调用方职责，闸只管账）。
/// </summary>
public class SourceRateGateTests
{
    private long _now;

    public SourceRateGateTests()
    {
        SourceRateGate.Reset();
        _now = 1_000_000;
        SourceRateGate.Now = () => _now;
    }

    [Fact]
    public void FreshUrl_IsNotCooling()
    {
        Assert.False(SourceRateGate.IsCooling("https://a.example.com/live.m3u8"));
    }

    [Fact]
    public void Rejected_EntersCooldown_AndExpires()
    {
        var url = "https://a.example.com/live.m3u8";
        SourceRateGate.OnRejected(url);
        Assert.True(SourceRateGate.IsCooling(url));
        _now += SourceRateGate.BaseCooldownMs - 1;
        Assert.True(SourceRateGate.IsCooling(url)); // 窗内仍冷却
        _now += 1;
        Assert.False(SourceRateGate.IsCooling(url)); // 窗过即解
    }

    [Fact]
    public void RepeatedRejections_Escalate_AndCap()
    {
        var url = "https://a.example.com/live.m3u8";
        SourceRateGate.OnRejected(url);
        _now += SourceRateGate.BaseCooldownMs; // 窗过再拒
        SourceRateGate.OnRejected(url);
        // 第二拒=120s：第一拒窗过后再过 60s 仍应冷却
        _now += SourceRateGate.BaseCooldownMs;
        Assert.True(SourceRateGate.IsCooling(url));
        // 连拒到封顶：10 分钟封顶不再膨胀
        for (var i = 0; i < 10; i++)
        {
            _now += SourceRateGate.MaxCooldownMs;
            SourceRateGate.OnRejected(url);
        }
        _now += SourceRateGate.MaxCooldownMs - 1;
        Assert.True(SourceRateGate.IsCooling(url));
        _now += 1;
        Assert.False(SourceRateGate.IsCooling(url));
    }

    [Fact]
    public void OnOk_ClearsAccount()
    {
        var url = "https://a.example.com/live.m3u8";
        SourceRateGate.OnRejected(url);
        Assert.True(SourceRateGate.IsCooling(url));
        SourceRateGate.OnOk(url);
        Assert.False(SourceRateGate.IsCooling(url));
        // 清账后再拒：从头计（60s 首窗，不是步进窗）
        SourceRateGate.OnRejected(url);
        _now += SourceRateGate.BaseCooldownMs;
        Assert.False(SourceRateGate.IsCooling(url));
    }

    [Fact]
    public void SameHost_SharesAccount_OtherHost_Isolated()
    {
        SourceRateGate.OnRejected("https://a.example.com/x/live.m3u8");
        // 同主机不同路径：共账
        Assert.True(SourceRateGate.IsCooling("https://a.example.com/y/seg.ts"));
        // 跨主机：不串
        Assert.False(SourceRateGate.IsCooling("https://b.example.com/x/live.m3u8"));
        // 一路成功清账，同主机全解
        SourceRateGate.OnOk("https://a.example.com/y/seg.ts");
        Assert.False(SourceRateGate.IsCooling("https://a.example.com/x/live.m3u8"));
    }

    [Fact]
    public void InvalidUrl_FallsBackToRawString_DoesNotThrow()
    {
        SourceRateGate.OnRejected("not-a-url");
        Assert.True(SourceRateGate.IsCooling("not-a-url"));
        Assert.False(SourceRateGate.IsCooling("https://other.example.com/x"));
    }
}
