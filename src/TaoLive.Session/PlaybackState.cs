namespace TaoLive.Session;

/// <summary>
/// 播放会话五态（老板 2026-11-15 拍板：禁止布尔标志位乱跳）。
/// Reconnecting 即换台中间态：缓冲断流、换台中断都经它走向终局。
/// 合法迁移矩阵：
///   Idle → Buffering | Failed
///   Buffering → Playing | Reconnecting | Failed | Idle（换台中断旧源）
///   Playing → Reconnecting | Failed | Idle（正常 Stop / 换台中断）
///   Reconnecting → Buffering | Playing（恢复沿） | Failed | Idle（卡流窗口换台，BUG-1 补）
///   Failed → Buffering（重试 / 备源切换） | Idle（放弃 / 换台中断）
/// 其余迁移一律抛 InvalidOperationException（用例把关）。
/// </summary>
public enum PlaybackState
{
    Idle = 0,
    Buffering,
    Playing,
    Reconnecting,
    Failed,
}
