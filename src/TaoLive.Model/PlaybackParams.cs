namespace TaoLive.Model;

/// <summary>
/// 播放参数：一次起播所需的全部输入。
/// 预取双曲线唯一事实源在 <see cref="TaoLive.Foundation.PrefetchCurve"/>（vc96 第 4 轮审查清零：
/// 本类型曾镜像同名常量且值已漂移（7 秒 vs 事实源 12 秒）、全仓零引用，属误导性死码，成建制清除——
/// 起播/重入/首帧/兜底四线门槛一律读 PrefetchCurve，禁再建第二本账）。
/// </summary>
public sealed record PlaybackParams(
    Channel Channel,
    SourceEndpoint Endpoint,
    bool PreferLowLatency);
