using System;

namespace TaoLive.BLL;

/// <summary>
/// v23 修法 A·回前台重接的网络就绪等待步进（纯逻辑可测，常数收口）：
/// 切后台回前台瞬间设备网络往往未就绪（系统挂网恢复/连接池回收），不空打取流——
/// 未就绪按步进等待，等满封顶次数照常发起重接，绝不无限等。
/// </summary>
public static class ResumeNetWaitPolicy
{
    /// <summary>每步等待毫秒（500ms 步进，够系统网络栈恢复，又不拖重接）。</summary>
    public const int StepMs = 500;

    /// <summary>最多等待步数（6 步 ≈ 3 秒封顶，超时照常 Play）。</summary>
    public const int MaxSteps = 6;

    /// <summary>第 step 次（0 起）探测未就绪：是否继续等待（false=超时，照常发起）。</summary>
    public static bool ShouldWait(int step) => step < MaxSteps;
}
