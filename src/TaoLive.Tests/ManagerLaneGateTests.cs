using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc76 审查清零 P2 回归：车道门控冷切窗——SwitchTo 冷切路径先接管前台（IsForeground=true）
/// 再 ChangeChannel，Transition(Buffering) 起即由本会话守门（起播期零后台网络）。
/// vc69 让道修后门控只认前台会话，旧次序（ChangeChannel 先于接管）下门全程关不上。
/// </summary>
[Collection("BackgroundLaneSerial")]
public class ManagerLaneGateTests
{
    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void Cold_switch_closes_lane_gate_during_buffering()
    {
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(DateTimeOffset.Parse("2026-11-16T00:00:00Z"))));
        try
        {
            // 前态摆拍：第一台出画在播（门开=有画面在播），模拟真实观看态
            BackgroundLane.SetFirstFrameLanded(true);
            BackgroundLane.SetPlaying(true);
            var first = manager.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
            first.NotifyFirstFrame();
            Assert.Equal(PlaybackState.Playing, first.State);
            Assert.True(BackgroundLane.FirstFrameLanded);

            // 冷切第二台：Buffering 起播期零后台网络门必须关（vc76 P2 断言锚）
            var second = manager.SwitchTo(MakeChannel("cctv5", "https://s/b.m3u8"));
            Assert.Equal(PlaybackState.Buffering, second.State);
            Assert.False(BackgroundLane.FirstFrameLanded, "冷切起播期零后台网络门未关（车道门控冷切窗回归）");
        }
        finally
        {
            BackgroundLane.SetFirstFrameLanded(true); // 还原全局门，不污染其他用例
            BackgroundLane.SetPlaying(false);
        }
    }
}
