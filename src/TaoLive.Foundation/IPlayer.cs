using TaoLive.Model;

namespace TaoLive.Foundation;

/// <summary>
/// 播放内核统一契约：内核实现都要长成这个形状，
/// 会话层只认接口不认内核本尊（2.0 单内核=ExoPlayer，老板 2026-11-15 03:37 拍板；
/// 热切靠 Current+Standby 双实例，双实例共用同一内核类，事件语义一致由 Tests 契约用例把关）。
/// </summary>
public interface IPlayer : IDisposable
{
    /// <summary>起播。返回即表示指令已受理，成败走事件。</summary>
    void Play(PlaybackParams params_);

    /// <summary>暂停。已停止/未起播时调用为无害操作。</summary>
    void Pause();

    /// <summary>恢复播放（vc68 热备静音攒流）：暂停期间内核照常缓冲攒流，Resume 即出声出画。
    /// 未暂停/已停止时调用为无害操作。</summary>
    void Resume();

    /// <summary>停止并释放内部资源；不释放输出面（Surface 归属壳层）。</summary>
    void Stop();

    /// <summary>
    /// 接输出面：传入 Surface 句柄（Android 端为 IntPtr of Surface）。
    /// 契约（研发路红灯整改）：Surface 持有方统一为壳层 View，
    /// 内核只做 attach/detach，不持有不引用 Surface 生命周期。
    /// </summary>
    void AttachSurface(IntPtr surfaceHandle);

    /// <summary>摘输出面：换源/换台/退出前必调，防黑屏花屏。重复 detach 无害。</summary>
    void DetachSurface();

    /// <summary>
    /// 缓冲库存读数（毫秒）：播放位置之后的缓冲余量（直播向前的存货）。
    /// 预防性换源（老板令 #24）据此在卡顿前动手，不等内核进入卡流态。
    /// 契约：内核给不出可靠读数（未就绪/直播源无数）返回 0——会话层把 0 当「无读数」不判趋势，
    /// 只对 &gt;0 且低于库存线的读数做预防性介入；真见底走既有卡流自救路径。
    /// </summary>
    long BufferedMs { get; }

    /// <summary>读数是否可靠（vc85 水位跟手令，老板 2026-11-16 拍板）：内核在重接/换票/起播准备期
    /// 给不出读数——false=壳层保持上一拍显示，不闪归零；true=按 BufferedMs 实读刷新。
    /// 默认实现 true 兼容既有实现与测试替身；Android 内核按实况覆写。</summary>
    bool BufferedMsKnown => true;

    // —— 事件时序语义（契约，两内核必须一致；次序即文档）——

    /// <summary>
    /// 第一帧落地。生命周期：Play 后到且仅到一次（重连成功不再触发，走 OnBufferingEnd）。
    /// 之前必然先 OnBufferingStart（除非秒出帧，则两事件同一调度拍内按此先后）。
    /// </summary>
    event EventHandler<PlayerFirstFrameEventArgs> OnFirstFrame;

    /// <summary>
    /// 缓冲开始。与 OnBufferingEnd 必须成对；OnBufferingStart 后必有对应 End，
    /// 反之不必然（End 可先于任何 Start 出现于起播瞬间）。
    /// </summary>
    event EventHandler<PlayerBufferingEventArgs> OnBufferingStart;

    /// <summary>缓冲结束：画面恢复出帧。断流自救期间会成对反复出现。</summary>
    event EventHandler<PlayerBufferingEventArgs> OnBufferingEnd;

    /// <summary>
    /// 不可恢复错误。触发后内核进入终态：不再出任何事件（含 Buffering），直到下一次 Play。
    /// 会话层收到即走重连/降级策略。
    /// </summary>
    event EventHandler<PlayerErrorEventArgs> OnError;

    /// <summary>
    /// 帧回调（方案书 v74/v76/v20）：渲染线程帧将出时触发，内核按 10Hz 节流上报（约每 100ms 一拍），
    /// 供壳层埋点做画面平稳无感量化断言（帧间隔 P95 ≤ 250ms，连续 3 窗判卡顿）。
    /// 调用线程=内核渲染线程，订阅方自行并发安全；不起播零事件。
    /// </summary>
    event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;
}

public sealed class PlayerFirstFrameEventArgs : EventArgs
{
    public required DateTimeOffset AtUtc { get; init; }
}

public sealed class PlayerBufferingEventArgs : EventArgs
{
    public required bool IsSeeking { get; init; }
}

public sealed class PlayerErrorEventArgs : EventArgs
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed class PlayerFrameTickEventArgs : EventArgs
{
    public required DateTimeOffset AtUtc { get; init; }
}
