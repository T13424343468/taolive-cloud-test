namespace TaoLive.Foundation;

/// <summary>
/// 预取双曲线常量（老板 2026-11-15 02:39 确认原样继承）。
/// 数据来源：PlaybackParams 已镜像同值，此处为唯一事实源，Model 端引用本值。
/// </summary>
public static class PrefetchCurve
{
    /// <summary>起播门槛线：缓冲到 12 秒即出画（真机反馈批#12 修 2026-11-15：原 7 秒起播后网络抖动
    /// 频繁掉水，卡后重入门槛同为 7 秒，表现为「首帧出了但卡住几秒才流畅」；老板令——要么先别出画，
    /// 出就得流畅，首帧快让位于出画顺滑。多出的 5 秒起播等待由换台遮罩深底兜住，老板无感知旧帧）。
    /// 卡后重入门槛同用此值（LoadControl 第 3/4 参同源）。</summary>
    public static readonly TimeSpan StartupThreshold = TimeSpan.FromSeconds(12);

    /// <summary>首帧门槛线（vc68 三次定标：12s 原始→0.5s 出画令→3s 老板定值 2026-11-15「把首帧改3秒内」）：
    /// 起播缓冲到 3 秒即出画——真机实锤 0.5 秒出画后画面落不了屏（热切接管黑屏）且预防烧线，
    /// 3 秒在「先出画」与「出画能站住」之间取平；出画后内核后台继续填水到预取水位线。
    /// 卡后重入门槛仍用 StartupThreshold（12 秒），出得快也不掉顺滑（#12 意图保留）。</summary>
    public static readonly TimeSpan FirstFrameGate = TimeSpan.FromSeconds(3);

    /// <summary>预取水位线：出画后继续填到窗口取满（对应 10~30 秒延迟取舍）。</summary>
    public static readonly TimeSpan PrefillWaterline = TimeSpan.FromSeconds(30);
}
