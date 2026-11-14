using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// 断流自救重试策略 (纯逻辑, v3.23 P0-1, 可单测):
    /// 远程网络流 15 秒无进度判卡住 / 内核报错时, 按 2/4/8 秒退避自动重试, 最多 3 次;
    /// 用尽后交还用户 (界面给明确文案 + 手动重试入口)。手动暂停永不触发。
    /// </summary>
    public sealed class StreamRetryPolicy
    {
        /// <summary>单次播放会话最多自动重试次数</summary>
        public const int MaxAttempts = 3;
        private static readonly int[] DelaysMs = { 2000, 4000, 8000 };

        /// <summary>已重试 attemptsSoFar 次后是否还允许再试</summary>
        public bool ShouldRetry(int attemptsSoFar) => attemptsSoFar < MaxAttempts;

        /// <summary>第 attemptsSoFar(从0计) 次重试前的退避间隔 (毫秒)</summary>
        public int NextDelayMs(int attemptsSoFar) =>
            DelaysMs[Math.Clamp(attemptsSoFar, 0, DelaysMs.Length - 1)];

        /// <summary>是否远程网络流 (http/https); 本地文件与 content 路径不在自救范围</summary>
        public static bool IsNetworkPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>停滞判定阈值 (毫秒): 播放中超过该时长无进度即判卡住</summary>
        public const int StallThresholdMs = 15000;
    }
}
