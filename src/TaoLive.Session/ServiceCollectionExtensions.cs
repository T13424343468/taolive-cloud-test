using Microsoft.Extensions.DependencyInjection;
using TaoLive.Foundation;

namespace TaoLive.Session;

/// <summary>
/// DI 装配点（方案书 v5 口径）：壳层只调用 AddTaoLiveSession() 一次，
/// 避免各壳各拼导致行为漂移。
/// kernelFactory（可选）：注入后每会话独享内核实例（Current+Standby 双实例热切的资源前提）；
/// 不注入则沿用共用单内核（仅冷切，W1 契约不变）。
/// onSwitchingChannel（可选）：换台钩子，壳层接 ChannelPrefetcher（邻台预取，单车道让路口径）。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTaoLiveSession(this IServiceCollection services,
        Func<IPlayer>? kernelFactory = null, Action<Model.Channel>? onSwitchingChannel = null)
    {
        services.AddSingleton<PlaybackSessionManager>(sp =>
        {
            var clock = sp.GetService<IClock>() ?? new SystemClock();
            var dispatcher = sp.GetService<IMainThreadDispatcher>() ?? new ImmediateMainThreadDispatcher();
            if (kernelFactory is not null)
            {
                // vc104 老板令：以 93 为基线重建——恢复热备深水档（vc101 拆除的双实例并行攒流回归）；
                // 慢片竞速保持拆除（RacingHttpDataSource.RecordSlowSegment 纯记账，不回滚）
                return new PlaybackSessionManager(
                    () => new PlaybackSession(kernelFactory(), clock), dispatcher,
                    onSwitchingChannel, hotStandbyEnabled: true);
            }
            var player = sp.GetRequiredService<IPlayer>();
            return new PlaybackSessionManager(() => new PlaybackSession(player, clock), dispatcher);
        });
        return services;
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    // 单调时钟（方案书 v12 铁律：运行期计时账一律按单调钟，四镜·卡顿镜 11-15 修）
    public long NowMs => Environment.TickCount64;
}
