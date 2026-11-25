namespace TaoLive.Session;

/// <summary>
/// 会话层运行台账出口（2.0 真机反馈批·埋点补充 2026-11-15）：会话/管理器打点统一经此交上层落账，
/// 层向纪律（Session 不反向依赖壳层）由静态委托承接——壳层装配时接线（MainActivity → CrashNet）；
/// 未接线（单测/未装配环境）即为空操作，零 IO、零副作用。
/// 打点口径：故障面按标签起头（[起播]/[看门狗]/[热备]/[回主源]），内容带频道与关键毫秒数，可对账可排查。
/// </summary>
public static class SessionTrace
{
    /// <summary>台账出口；壳层接 CrashNet.Record。未接线为空。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Emit(string message)
    {
        try { Sink?.Invoke(message); }
        catch { /* 台账出口异常不反灌业务链路 */ }
    }
}
