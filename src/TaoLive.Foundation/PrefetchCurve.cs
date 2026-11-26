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

    /// <summary>首帧门槛线（vc89 老板定值 2026-11-17：3 秒→7 秒——首帧与库存档下限对齐，
    /// 出画那一刻手里正好 7 秒存货，治「切台放几秒就没库存」的起播余量病根；
    /// 代价=首帧多等约 4 秒，由换台遮罩深底兜住，老板无感知旧帧）。
    /// 出画后内核后台继续填水到预取水位线；卡后重入门槛仍用 StartupThreshold（12 秒），
    /// 出得快也不掉顺滑（#12 意图保留）。</summary>
    public static readonly TimeSpan FirstFrameGate = TimeSpan.FromSeconds(7);

    /// <summary>预取水位线：出画后继续填到窗口取满（对应 10~30 秒延迟取舍）。</summary>
    public static readonly TimeSpan PrefillWaterline = TimeSpan.FromSeconds(30);

    /// <summary>首帧超时兜底线（刀二，老板 2026-11-25 九件套定稿）：起播判死窗 12 秒收 8 秒——
    /// 死源黑屏再少等 4 秒。必须配合流时钟查活续期一起动（vc67 既有机制）：3 秒内缓冲读数有增长
    /// 即判「慢灌在走」放宽到慢灌窗（21 秒），8 秒兜底只咬零进度真死源，慢热源不误杀；
    /// 慢而通另有原地宽限重试（TrySlowConnectGrace，进度闸+预算封顶）双保险。
    /// 8 秒=内核 Media3 默认连接超时同值（vc68 账：窗≤内核超时时两把快刀赛跑，真死殊途同归都走换源，
    /// 零进度真死不进宽限直接快切，无「误杀慢热」面——慢热的证明时间由查活续期给）。</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(8);
}
