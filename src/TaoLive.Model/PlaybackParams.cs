namespace TaoLive.Model;

/// <summary>
/// 播放参数：一次起播所需的全部输入。
/// 预取双曲线（老板 2026-11-15 02:39 确认）：起播门槛 7 秒即出画，首帧不等满水位；
/// 预取水位=窗口取满，出画后后台填。
/// </summary>
public sealed record PlaybackParams(
    Channel Channel,
    SourceEndpoint Endpoint,
    bool PreferLowLatency)
{
    /// <summary>起播门槛：缓冲到该秒数即出画（首帧快字当头）。</summary>
    public static readonly TimeSpan StartupThreshold = TimeSpan.FromSeconds(7);

    /// <summary>预取水位：出画后继续缓冲到窗口取满为止。</summary>
    public static readonly TimeSpan PrefillWaterline = TimeSpan.FromSeconds(30);
}
