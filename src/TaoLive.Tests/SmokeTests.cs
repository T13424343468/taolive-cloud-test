using Microsoft.Extensions.DependencyInjection;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>W1 冒烟三件套：状态机初始态、DI 解析、Model 契约。</summary>
public class SmokeTests
{
    [Fact]
    public void Session_initial_state_is_Idle()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z"));
        var session = new PlaybackSession(new StubPlayer(), clock);
        Assert.Equal(PlaybackState.Idle, session.State);
        Assert.Null(session.StartedAtUtc);
    }

    [Fact]
    public void Di_container_resolves_session_manager()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPlayer, StubPlayer>();
        services.AddTaoLiveSession();
        using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<PlaybackSessionManager>();
        Assert.NotNull(manager);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void Channel_contract_validates_sources()
    {
        var good = new SourceEndpoint("https://example.com/live.m3u8", SourceKind.Web, 10);
        var bad = new SourceEndpoint("not-a-url", SourceKind.Web, 5);
        var ch = new Channel("cctv1", "CCTV-1 综合", "央视", new[] { good, bad });
        Assert.True(ch.IsValid);
        Assert.True(good.IsValid);
        Assert.False(bad.IsValid);
    }

    [Fact]
    public void Illegal_transition_throws()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z"));
        var session = new PlaybackSession(new StubPlayer(), clock);
        // Idle -> Playing 非法（必须经 Buffering）
        Assert.Throws<InvalidOperationException>(() => session.Transition(PlaybackState.Playing));
    }

    [Fact]
    public void Start_from_failed_is_legal()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-15T00:00:00Z"));
        var session = new PlaybackSession(new StubPlayer(), clock);
        session.Transition(PlaybackState.Failed, "smoke");
        session.Start(MakeParams());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(clock.UtcNow, session.StartedAtUtc);
    }

    private static PlaybackParams MakeParams() =>
        new(new Channel("cctv1", "CCTV-1", "央视", new[] { new SourceEndpoint("https://example.com/a.m3u8", SourceKind.Web, 1) }),
            new SourceEndpoint("https://example.com/a.m3u8", SourceKind.Web, 1),
            PreferLowLatency: false);
}

/// <summary>契约桩：不真播，只为状态机测试服务。事件加 raise 方法供后续班次用。</summary>
public sealed class StubPlayer : IPlayer
{
    public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
    public event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
    public event EventHandler<PlayerErrorEventArgs>? OnError;

    // 测试驱动口：后续契约用例经此触发事件，避免 CS0067 未用告警。
    internal void RaiseFirstFrame(DateTimeOffset at) => OnFirstFrame?.Invoke(this, new PlayerFirstFrameEventArgs { AtUtc = at });
    internal void RaiseBufferingStart() => OnBufferingStart?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });
    internal void RaiseBufferingEnd() => OnBufferingEnd?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });
    internal void RaiseError(string code) => OnError?.Invoke(this, new PlayerErrorEventArgs { Code = code, Message = code });
    internal void RaiseFrameTick(DateTimeOffset at) => OnFrameTick?.Invoke(this, new PlayerFrameTickEventArgs { AtUtc = at });

    public void Play(PlaybackParams params_) { }
    public void Pause() { }
    public void Resume() { } // vc68：热备静音攒流契约（IPlayer.Resume）
    public void Stop() { }
    public void AttachSurface(IntPtr surfaceHandle) { }
    public void DetachSurface() { }
    public void Dispose() { }

    /// <summary>库存读数（IPlayer 契约，老板令 #24）：桩无实播，恒 0=无读数。</summary>
    public long BufferedMs => 0;
}
