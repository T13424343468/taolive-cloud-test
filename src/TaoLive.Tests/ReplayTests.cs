using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// v97 重放测试（方案书六章，R1 红条收口）：命令流回放=回归测试固定资产。
/// 重放格式（v60）=按键 ID+时间戳的 JSONL 逐行；用例覆盖三主序列：
/// 换台主链路、断流备源自救、热备接管。
/// </summary>
public class ReplayTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    /// <summary>重放驱动（测试固定资产）：按 t 序执行命令流，时间戳推进固定时钟。</summary>
    private static void Run(PlaybackSessionManager manager, List<LoggingPlayer> players,
        Dictionary<string, Channel> table, string jsonl)
    {
        foreach (var step in CommandReplay.Parse(jsonl))
        {
            switch (step.Cmd)
            {
                case "switch": // 换台主链路：进会话起播+挂面
                    manager.SwitchTo(table[step.Arg!]);
                    manager.Current!.AttachSurface(new IntPtr(1));
                    break;
                case "first_frame":
                    manager.Current!.NotifyFirstFrame();
                    break;
                case "prepare_standby":
                    manager.PrepareStandby(table[step.Arg!]);
                    break;
                case "standby_ready":
                    manager.Standby!.NotifyFirstFrame();
                    break;
                case "play_error": // 注入内核错误（断流自救：进备源自救）——当前会话的内核实例
                    players[^1].RaiseError(step.Arg ?? "SOURCE_ERROR");
                    break;
                default:
                    throw new FormatException($"未知命令：{step.Cmd}");
            }
        }
    }

    [Fact]
    public void Replay_channel_switch_main_sequence()
    {
        var manager = MakeManager(out var players);
        var table = new Dictionary<string, Channel>
        {
            ["cctv1"] = MakeChannel("cctv1", "https://s/a1.m3u8", "https://s/a2.m3u8"),
            ["cctv2"] = MakeChannel("cctv2", "https://s/b1.m3u8"),
        };

        Run(manager, players, table, """
            {"t":100,"cmd":"switch","arg":"cctv1"}
            {"t":150,"cmd":"first_frame"}
            {"t":60000,"cmd":"switch","arg":"cctv2"}
            {"t":60200,"cmd":"first_frame"}
            """);

        Assert.Equal(PlaybackState.Playing, manager.Current!.State);
        Assert.Equal("cctv2", manager.Current.CurrentChannel!.Id);
        // 换台契约：旧会话让位不响应内核事件（Supersede 后 Idle）
        Assert.Equal(PlaybackState.Idle, manager.TryGet("cctv1")!.State);
    }

    [Fact]
    public void Replay_stream_failure_self_heals_to_backup_source()
    {
        var manager = MakeManager(out var players);
        var table = new Dictionary<string, Channel>
        {
            ["cctv1"] = MakeChannel("cctv1", "https://s/a1.m3u8", "https://s/a2.m3u8"),
        };

        Run(manager, players, table, """
            {"t":100,"cmd":"switch","arg":"cctv1"}
            {"t":150,"cmd":"first_frame"}
            {"t":60000,"cmd":"play_error","arg":"SOURCE_ERROR"}
            {"t":60010,"cmd":"first_frame"}
            """);

        // 断流自救：备源接管，计数 1，最终回到 Playing
        Assert.Equal(1, manager.Current!.FailoverCount);
        Assert.Equal("https://s/a2.m3u8", manager.Current.CurrentEndpoint!.Url);
        Assert.Equal(PlaybackState.Playing, manager.Current.State);
    }

    [Fact]
    public void Replay_hot_standby_takeover()
    {
        var manager = MakeManager(out var players);
        var table = new Dictionary<string, Channel>
        {
            ["cctv1"] = MakeChannel("cctv1", "https://s/a1.m3u8"),
            ["cctv2"] = MakeChannel("cctv2", "https://s/b1.m3u8"),
        };

        Run(manager, players, table, """
            {"t":100,"cmd":"switch","arg":"cctv1"}
            {"t":150,"cmd":"first_frame"}
            {"t":30000,"cmd":"prepare_standby","arg":"cctv2"}
            {"t":30100,"cmd":"standby_ready"}
            {"t":30200,"cmd":"switch","arg":"cctv2"}
            """);

        // vc68 二轮拍板（老板定死热切保住）：就绪热备走接管——旧内核先 Supersede 摘面停机，
        // 新内核 AttachSurface 后 ResumePlayback，接管即出画不重播（热切契约）
        Assert.Equal("cctv2", manager.Current!.CurrentChannel!.Id);
        Assert.Equal(PlaybackState.Playing, manager.Current.State); // 接管即出画（热切契约）
        Assert.Equal(PlaybackState.Idle, manager.TryGet("cctv1")!.State);
    }

    private static PlaybackSessionManager MakeManager(out List<LoggingPlayer> players)
    {
        var list = new List<LoggingPlayer>();
        players = list;
        return new PlaybackSessionManager(
            () => { var p = new LoggingPlayer(); list.Add(p); return new PlaybackSession(p, new FixedClock(T0)); },
            dispatcher: null, onSwitchingChannel: null, hotStandbyEnabled: true);
    }

    [Fact]
    public void Replay_parse_rejects_malformed_lines_and_sorts_by_time()
    {
        var steps = CommandReplay.Parse("""
            {"t":200,"cmd":"first_frame"}
            // 注释行跳过
            {"t":100,"cmd":"switch","arg":"cctv1"}
            """);
        Assert.Equal([100, 200], steps.Select(s => s.T));
        Assert.Throws<FormatException>(() => CommandReplay.Parse("{\"t\":1,\"cmd\":}"));
        Assert.Throws<FormatException>(() => CommandReplay.Parse("{\"t\":-1,\"cmd\":\"ok\"}"));
    }
}
