namespace TaoLive.Foundation;

/// <summary>
/// 持久化公共口径（编码规范〇.7.3 归一：同类逻辑三现必须合并，2026-11-15 总镜子 R1 收口）。
/// 打点落盘防抖节拍唯一事实源：源档案库/评分账/健康账三个 Store 共用，禁散落私拷。
/// </summary>
public static class PersistencePolicy
{
    /// <summary>打点落盘防抖（毫秒）：账本脏后延迟落盘，窗口内合并写；v85 落盘防抖口径 ≥1 秒，取 3 秒。</summary>
    public const int FlushDebounceMs = 3000;
}
