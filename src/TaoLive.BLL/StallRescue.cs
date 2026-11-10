using System;

namespace TaoLive.BLL;

// ============ 道映 v1.0.9 第⑫项·画面卡住分级快速自救（用户 2026-11-08 10:40 拍板）============
// 「可以就这么定」：静默重取流 → 降清晰度 → 软重启播放内核，三级递进，快过旧看门狗 2/4/6 秒重连阶梯。
// 纯逻辑可测；执行由 LiveVideoHost 按判定分派（消费⑬健康表换源，成功/失败回写）。

/// <summary>卡死自救动作：按本台会话内卡死次数分级行进。</summary>
public enum StallRescueAction
{
    SilentRefresh = 0,  // ①静默重取流：换新票/新地址快速重接，不发状态、不占重连次数
    LowerQuality = 1,   // ②降清晰度：AI 增强降档减轻软解负载后静默重接（无可降档时跳过）
    RestartKernel = 2,  // ③软重启播放内核：重建设播环境，不动系统
}

/// <summary>分级快速自救判定器（纯函数）：第 1 次卡死静默重取流，第 2 次降清晰度（无可降则重取流），第 3 次起软重启内核。</summary>
public static class StallRescue
{
    /// <summary>第几次卡死动用内核软重启（1 起计：1=重取流，2=降清晰度，3=软重启内核）。</summary>
    public const int KernelRestartAt = 3;

    /// <summary>
    /// v23 第 5 条·⑨B 提前介入连续无效升级线：静默重取流连续介入满 N 次仍未止住停滞
    /// （时钟微动「假活」循环介入的老实锤），第 N 次起不再静默重取流，升级走分级自救阶梯
    /// （静默重取流→降清晰度→软重启内核）。
    /// </summary>
    public const int EarlyRescueEscalateAt = 3;

    /// <summary>介入计数清零窗口（毫秒）：时钟恢复（持续走钟）满此时长才清连续介入计数——
    /// 时钟微动的「假活」不清零，防止循环静默重取流永远升不了级。</summary>
    public const int EarlyRescueResetMs = 30_000;

    /// <summary>
    /// stallIndex：本台会话内第几次卡死（0 起）。hasLowerQuality：是否还有可降档。
    /// 无可降档时第 2 级直接并入重取流，绝不空转一档。
    /// </summary>
    public static StallRescueAction Plan(int stallIndex, bool hasLowerQuality) =>
        stallIndex switch
        {
            0 => StallRescueAction.SilentRefresh,
            1 => hasLowerQuality ? StallRescueAction.LowerQuality : StallRescueAction.SilentRefresh,
            _ => StallRescueAction.RestartKernel,
        };
}
