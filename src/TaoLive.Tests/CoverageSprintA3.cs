using Microsoft.Extensions.DependencyInjection;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 覆盖冲刺 A3（vc90 分支覆盖补账）：NetReadiness 等网就绪三态（探通/探败超时/单探异常续探）、
/// ServiceCollectionExtensions DI 装配三分支（kernelFactory 注入/共用单内核/换台钩子）。
/// </summary>
public class CoverageSprintA3
{
    // ---- NetReadiness ----

    [Fact]
    public async Task WaitAsync_probe_ok_returns_true_immediately()
    {
        var calls = 0;
        var ok = await NetReadiness.WaitAsync(
            timeoutMs: 15_000,
            probe: (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(true); });
        Assert.True(ok);
        Assert.Equal(1, calls); // 探通即回，不空转重探
    }

    [Fact]
    public async Task WaitAsync_probe_failure_until_deadline_returns_false()
    {
        var calls = 0;
        var ok = await NetReadiness.WaitAsync(
            timeoutMs: 0, // 到时上限未通：照常放行进默认台
            probe: (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(false); });
        Assert.False(ok);
        Assert.Equal(1, calls); // 超时即回，不无限重探
    }

    [Fact]
    public async Task WaitAsync_probe_exception_is_reported_and_retried()
    {
        var calls = 0;
        var reports = new List<string>();
        var ok = await NetReadiness.WaitAsync(
            timeoutMs: 3_000, // 留出重探窗口：首探异常不终止等待
            probe: (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("单探炸了"); // 异常被捕获续探
                return Task.FromResult(true);
            },
            report: reports.Add);
        Assert.True(ok);
        Assert.Equal(2, calls);
        Assert.Contains(reports, r => r.Contains("单探炸了")); // 单探异常上报续探
    }

    [Fact]
    public async Task WaitAsync_zero_timeout_with_failing_probe_reports_and_fails()
    {
        var reports = new List<string>();
        var ok = await NetReadiness.WaitAsync(
            timeoutMs: 0,
            probe: (_, _, _) => throw new TimeoutException("连不上"),
            report: reports.Add);
        Assert.False(ok);
        Assert.Contains(reports, r => r.Contains("连不上"));
    }

    // ---- ServiceCollectionExtensions ----

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void AddTaoLiveSession_with_kernel_factory_resolves_manager()
    {
        // kernelFactory 注入：每会话独享内核实例（Current+Standby 双实例热切前提）
        var services = new ServiceCollection();
        services.AddTaoLiveSession(kernelFactory: () => new LoggingPlayer());
        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<PlaybackSessionManager>();
        Assert.NotNull(manager);
        Assert.Empty(manager.Sessions); // 装配即用，未换台前无会话
    }

    [Fact]
    public void AddTaoLiveSession_without_factory_requires_player_registration()
    {
        // 不注入 kernelFactory：沿用共用单内核，IPlayer 必须已登记
        var services = new ServiceCollection();
        services.AddTaoLiveSession();
        services.AddSingleton<IPlayer>(new LoggingPlayer());
        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<PlaybackSessionManager>();
        Assert.NotNull(manager);
    }

    [Fact]
    public void AddTaoLiveSession_without_factory_and_player_fails_on_resolve()
    {
        // 共用单内核路径：IPlayer 未登记时装配点在解析期报缺（GetRequiredService 契约）
        var services = new ServiceCollection();
        services.AddTaoLiveSession();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PlaybackSessionManager>());
    }

    [Fact]
    public void AddTaoLiveSession_on_switching_channel_hook_fires()
    {
        // 换台钩子：SwitchTo 即触发（壳层接邻台预取的单车道让路口径）
        Channel? hooked = null;
        var services = new ServiceCollection();
        services.AddTaoLiveSession(
            kernelFactory: () => new LoggingPlayer(),
            onSwitchingChannel: ch => hooked = ch);
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<PlaybackSessionManager>();

        var channel = MakeChannel("cctv1", "https://s/a.m3u8");
        manager.SwitchTo(channel);
        Assert.Same(channel, hooked); // 钩子收到本次换台频道
    }

    [Fact]
    public void AddTaoLiveSession_uses_registered_clock_and_dispatcher()
    {
        // 已登记 IClock/IMainThreadDispatcher 时装配点取登记实现，不落默认
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.Parse("2026-11-26T00:00:00Z")));
        services.AddSingleton<IMainThreadDispatcher>(new ImmediateMainThreadDispatcher());
        services.AddTaoLiveSession(kernelFactory: () => new LoggingPlayer());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PlaybackSessionManager>());
    }
}
