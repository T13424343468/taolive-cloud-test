using System;

namespace TaoLive.BLL;

/// <summary>
/// 卡流看门狗判定（v1.0.8 第1项·重点：中央一台播两三秒卡死）：
/// 直播流停止推进时 VLC 不触发 EncounteredError，唯一可靠信号是播放时钟停走。
/// 抽成纯函数供专项断言（重点项专用用例），LiveVideoHost 按此判定走重连。
/// </summary>
public static class PlaybackWatchdog
{
    /// <summary>看门狗巡检周期（毫秒）。</summary>
    public const int PollMs = 2000;

    /// <summary>播放时钟停滞判定阈值（毫秒）：6 秒无推进即按断流处理。</summary>
    public const int StallMs = 6000;

    /// <summary>是否判为卡流：首帧已出 + 时钟停滞超阈值。未出首帧时由连接流程自管，看门狗不介入。</summary>
    public static bool IsStalled(bool frameShown, long lastTickMs, long nowMs, int thresholdMs = StallMs) =>
        frameShown && nowMs - lastTickMs >= thresholdMs;

    /// <summary>回前台存活判定（v1.0.8 第6项·重点：退出再开黑屏）：首帧已出后时钟近 5 秒内推进过才算活；
    /// 后台播放被系统杀掉或表面失效时 IsPlaying 可能为真但时钟早已停走，必须按已死处理。</summary>
    public const int LivenessMs = 5000;

    public static bool IsLive(bool frameShown, long lastTickMs, long nowMs, int livenessMs = LivenessMs) =>
        frameShown && nowMs - lastTickMs < livenessMs;
}

/// <summary>重连网络缓存阶梯（毫秒）：首连 1200，重连逐级 2500→4000→6000 自适应软解。</summary>
public static class CacheLadder
{
    public static readonly int[] Values = { 1200, 2500, 4000, 6000 };

    public static int At(int index) => Values[Math.Clamp(index, 0, Values.Length - 1)];

    public static int Next(int index) => Math.Min(Math.Max(index, 0) + 1, Values.Length - 1);
}

/// <summary>
/// 代数守卫（v1.0.8 第2项，与重点第1/6项联动）：换台/重连各推进一代，
/// 旧重连定时器与事件回调按代数作废，防旧台重连覆盖新台、防黑屏通道复活。
/// </summary>
public class GenerationCounter
{
    private long _gen;
    public long Current => System.Threading.Volatile.Read(ref _gen);
    public long NextGeneration() => System.Threading.Interlocked.Increment(ref _gen);
    public bool IsCurrent(long gen) => Current == gen;
}
