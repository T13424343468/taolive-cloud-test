using System;

namespace TaoLive.BLL;

/// <summary>
/// 卡流看门狗判定（v1.0.8 第1项·重点：中央一台播两三秒卡死）：
/// 直播流停止推进时 VLC 不触发 EncounteredError，唯一可靠信号是播放时钟停走。
/// 抽成纯函数供专项断言（重点项专用用例），LiveVideoHost 按此判定走重连。
/// </summary>
public static class PlaybackWatchdog
{
    /// <summary>看门狗巡检周期（毫秒）。fix56（2026-11-14 真机令「实时监听、提前反应」）：2000→1000 收紧节拍，
    /// 6 秒停滞与 2.5 秒提前介入的判定阈值不变，反应时点最多提前约 1~2 秒。</summary>
    public const int PollMs = 1000;

    /// <summary>播放时钟停滞判定阈值（毫秒）：6 秒无推进即按断流处理。</summary>
    public const int StallMs = 6000;

    /// <summary>是否判为卡流：首帧已出 + 时钟停滞超阈值。未出首帧时由连接流程自管，看门狗不介入。</summary>
    public static bool IsStalled(bool frameShown, long lastTickMs, long nowMs, int thresholdMs = StallMs) =>
        frameShown && nowMs - lastTickMs >= thresholdMs;

    /// <summary>回前台存活判定（v1.0.8 第6项·重点：退出再开黑屏）：首帧已出后时钟近 5 秒内推进过才算活；
    /// 后台播放被系统杀掉或表面失效时 IsPlaying 可能为真但时钟早已停走，必须按已死处理。</summary>
    public const int LivenessMs = 5000;

    public static bool IsLive(bool frameShown, long lastTickMs, long nowMs, int livenessMs = LivenessMs) =>
        frameShown && nowMs - lastTickMs < livenessMs;

    /// <summary>fix29 防假活：画面帧停滞判定的看门狗周期数（fix56 巡检 1 秒/拍：3 周期 ≈ 3 秒不涨即按卡流，反应较旧 6 秒口径提前）。</summary>
    public const int DecodedStallTicksMax = 3;

    /// <summary>
    /// fix29 防假活判定：时钟在走（IsStalled 不触发）但画面帧数持续不涨——坏分片循环、伪流等
    /// 「假活」场景看门狗原本永远管不到，这里补上第二道防线。帧数在涨或统计读不到时不触发
    /// （由调用方保证只对读得到统计的媒体判，避免误伤）。
    /// </summary>
    public static bool IsFakeLive(int decodedNow, int decodedLast, int stallTicks) =>
        decodedNow == decodedLast && decodedNow > 0 && stallTicks + 1 >= DecodedStallTicksMax;
}

/// <summary>
/// fix56 提前预警（2026-11-14 真机令「实时监听画面、卡顿前做出反应」）：
/// 原看门狗只认「时钟停走」，出手时画面已经冻住；水位在掉时画面其实还活着，
/// 「供水不足」是比卡顿更早的信号。本监视器随看门狗 1 秒一拍采样：时钟还在走、
/// 但估算水位持续不足（播放器缓存换算秒数低于阈值）→ 连续 3 秒即预警出手，
/// 抢在画面冻住之前静默重取流。只在跨越阈值那一拍报一次（防连环出手），
/// 水位恢复或时钟停滞即清零重计；换台随宿主清账。
/// </summary>
public class EarlyWarningMonitor
{
    /// <summary>预警水位阈值（秒）：估算水位低于该值=源头供不上播放，画面随时会冻。</summary>
    public const double DrainWaterSec = 1.0;

    /// <summary>连续供水不足采样次数（1 秒一拍）：连续 3 秒供不上才动手，短抖动不动。</summary>
    public const int DrainSamples = 3;

    private int _streak;

    /// <summary>喂一拍采样。clockAlive=时钟仍在推进；waterSec=估算水位秒数（缓存窗口×缓冲百分比）。
    /// 返回 true=本拍跨越预警线，应抢在卡顿前出手（仅跨越那一拍为真）。</summary>
    public bool Sample(bool clockAlive, double waterSec)
    {
        if (!clockAlive || waterSec >= DrainWaterSec) { _streak = 0; return false; }
        _streak++;
        return _streak == DrainSamples; // 只在跨线那一拍报，防连环出手（宿主另有轮次额度）
    }

    /// <summary>换台/重连：连计清零，新会话新算。</summary>
    public void Reset() => _streak = 0;
}

/// <summary>
/// fix29 首帧等待兜底（CCTV-7 卡死盲区）：起播后迟迟不出首帧时，看门狗按「未出首帧交给连接流程」让位，
/// 静默自救路径又不接连接流程——两边互相等就无限期卡死。兜底口径：超时先走自救阶梯（重取流→降清晰度→
/// 重启内核），阶梯走完仍无画面交回上层收口（判死本台、放开探活自动跳台），绝不无限卡死。
/// </summary>
public static class FirstFramePolicy
{
    /// <summary>起播后多久还没出首帧就升级自救（毫秒）。</summary>
    public const int WaitMs = 8000;

    /// <summary>连续无首帧多少次后交回上层（1~3 次分别对应阶梯三级：重取流/降清晰度/重启内核）。</summary>
    public const int HandOverAfterFails = 4;

    public static bool ShouldHandOver(int fails) => fails >= HandOverAfterFails;
}

/// <summary>
/// fix28 修正①的订正（fix29 落地）：首连重连轮数按「频道」记账——同一频道反复失败轮数持续累计，
/// 满 3 轮放开冷启动探活（判死自动跳台）；换了频道才清零重计。原先按播放任务序号记账会因
/// 重连本身换新序号而永远停在 1，真死台逃逸实际不生效。
/// </summary>
public class ConnectRetryLedger
{
    private string? _channelKey;

    public int Count { get; private set; }

    public int OnFail(string channelKey)
    {
        if (_channelKey != channelKey) { _channelKey = channelKey; Count = 0; }
        return ++Count;
    }

    /// <summary>连续 3 轮仍败=疑似真死台：放开冷启动探活，判死自动跳下一个有信号的台。</summary>
    public bool ShouldReleaseGate => Count >= 3;
}

/// <summary>重连网络缓存阶梯（毫秒）：首连 1200，升档 5→10→20 秒深水攒片（fix55 拿满口径）。</summary>
public static class CacheLadder
{
    /// <summary>fix55（2026-11-14 真机令拍板「起播后不停拿、拿满」）：起播保持 1200ms 秒连口径不变，
    /// 水位升档大幅加深——升档后按 5→10→20 秒深水攒片；内核切片监听照常第一时间取新片，攒满为止。
    /// 20 秒深水在主流码率下约占 5~10MB 内存，电视端可承受。</summary>
    public static readonly int[] Values = { 1200, 5000, 10000, 20000 };

    public static int At(int index) => Values[Math.Clamp(index, 0, Values.Length - 1)];

    public static int Next(int index) => Math.Min(Math.Max(index, 0) + 1, Values.Length - 1);
}

/// <summary>
/// fix30 自救风暴断路器：60 秒窗口内自救动作超 6 次即进入冷却——连环自救（重取流/降清晰度/
/// 内核重建）会打满 CPU（软解+原生重建），整机被拖死连系统按键都无响应（真机「卡死只能关机
/// 重启」的帮凶）。冷却期只记录不动手，窗口内频次自然回落自动恢复。纯逻辑供专项断言。
/// </summary>
public class RescueBreaker
{
    /// <summary>滑动窗口内的自救次数上限。</summary>
    public const int MaxPerWindow = 6;

    /// <summary>滑动窗口宽度（毫秒）。</summary>
    public const int WindowMs = 60_000;

    private readonly long[] _stamps = new long[MaxPerWindow];
    private int _idx;

    /// <summary>窗口内频次未超限=允许自救。</summary>
    public bool CanRescue(long nowMs)
    {
        int hit = 0;
        for (int i = 0; i < _stamps.Length; i++)
            if (_stamps[i] > 0 && nowMs - _stamps[i] < WindowMs) hit++;
        return hit < MaxPerWindow;
    }

    /// <summary>记录一次自救（环形槽位，最旧的被覆盖）。</summary>
    public void OnRescue(long nowMs)
    {
        _stamps[_idx] = nowMs;
        _idx = (_idx + 1) % _stamps.Length;
    }
}

/// <summary>
/// fix31 长窗兜底断路器（冻结稿 v2）：fix30 的 60 秒窗口 6 次上限管「内核自救频次」，
/// 但真机失败循环节奏约 8~24 秒/轮——60 秒内最多凑出 3~4 次，永远摸不到上限，断路器被时间稀释绕过，
/// 失败循环（首帧兜底判死跳台 → 下一台再 8 秒循环）无限空转。本断路器管「兜底循环总量」：
/// 10 分钟长窗内「首帧兜底走完/重连换候选/链路短重试」三类事件累计 ≥12 次 → 进 90 秒静默观察期
/// （停止自动兜底与自动跳台，保持当前画面，按键全可用，用户换台即退出观察期并清账）；
/// 观察期最多连续 3 轮，仍全败按现行「全源不可达」无信号收口（保持拦截直到换台）。
/// 与 RescueBreaker 两级并存，内部异常一律退回现行链路（放行），绝不把台连死。
/// </summary>
public class FallbackLoopBreaker
{
    /// <summary>长窗宽度（毫秒）：10 分钟。</summary>
    public const int WindowMs = 600_000;

    /// <summary>窗口内三类兜底事件累计上限。</summary>
    public const int MaxEvents = 12;

    /// <summary>静默观察期时长（毫秒）：期内停自动兜底与自动跳台，保持当前画面。</summary>
    public const int ObserveMs = 90_000;

    /// <summary>观察期最多连续轮数：仍全败按「全源不可达」收口。</summary>
    public const int MaxObserveRounds = 3;

    /// <summary>事件裁定：Ok=放行走现行链路；ObserveEnter=本次触发进入观察期；ObserveHold=观察期内只入账不动手；Exhausted=连 3 轮观察仍全败，收口。</summary>
    public enum Step { Ok = 0, ObserveEnter, ObserveHold, Exhausted }

    private readonly long[] _stamps = new long[MaxEvents]; // 环形槽位（时长以窗口滑出为准，槽位只做容纳）
    private readonly object _lock = new(); // 复检建议1：宿主线程（ReconnectRefresher）与 UI 线程并发入账/清账，加锁防 long 撕裂读
    private int _idx;
    private long _observeStart = -1;
    private int _observeRounds;
    private bool _latched;

    /// <summary>记一笔兜底事件并裁定。时间戳须为单调正数（毫秒；0/负值不参与窗口统计）。</summary>
    public Step OnFallbackEvent(long nowMs)
    {
        try { lock (_lock) { return OnFallbackEventCore(nowMs); } }
        catch { lock (_lock) { ResetForChannel(); } return Step.Ok; } // 断路器异常退回现行链路，绝不把台连死
    }

    /// <summary>观察期内或收口后是否应停自动兜底（供定时点二次确认，防观察期开始于排定之后）。</summary>
    public bool IsBlocking(long nowMs)
    {
        lock (_lock)
        {
            return _latched || (_observeStart >= 0 && nowMs - _observeStart < ObserveMs);
        }
    }

    /// <summary>用户换台：退出观察期/收口态，计数随台清零（新台新算）。</summary>
    public void OnChannelChanged()
    {
        lock (_lock) { ResetForChannel(); }
    }

    private Step OnFallbackEventCore(long nowMs)
    {
        if (_latched) return Step.Exhausted; // 收口后保持拦截，换台才恢复
        if (_observeStart >= 0)
        {
            if (nowMs - _observeStart < ObserveMs) { Record(nowMs); return Step.ObserveHold; }
            _observeStart = -1; // 观察期满：恢复自动链路（轮数保留，仍全败则升级下一轮或收口）
        }
        Record(nowMs);
        if (CountEvents(nowMs) >= MaxEvents)
        {
            _observeRounds++;
            if (_observeRounds > MaxObserveRounds) { _latched = true; return Step.Exhausted; }
            _observeStart = nowMs;
            return Step.ObserveEnter;
        }
        return Step.Ok;
    }

    /// <summary>窗口内事件统计（虚缝：测试桩覆写以断言「断路器异常退回现行链路」）。</summary>
    protected virtual int CountEvents(long nowMs)
    {
        int hit = 0;
        for (int i = 0; i < _stamps.Length; i++)
            if (_stamps[i] > 0 && nowMs - _stamps[i] < WindowMs) hit++;
        return hit;
    }

    private void Record(long nowMs)
    {
        _stamps[_idx] = nowMs > 0 ? nowMs : 1; // 0 当作 1ms：保证「记过」可辨（时间戳为单调正数）
        _idx = (_idx + 1) % _stamps.Length;
    }

    private void ResetForChannel()
    {
        Array.Clear(_stamps);
        _idx = 0;
        _observeStart = -1;
        _observeRounds = 0;
        _latched = false;
    }
}

/// <summary>
/// fix32 内核队列上界（2026-11-13 真机卡死批）：内核线程被原生锁堵死期间，自救反复投递曾令
/// 队列无界堆积、闭包持有旧内核致内存单调涨（真机「看一二十分钟才发作」的慢积累）。
/// 满载丢弃可丢项并落账，最坏积压封顶。
/// </summary>
public static class KernelQueuePolicy
{
    /// <summary>内核操作队列容量上界（个）。</summary>
    public const int Capacity = 128;
}

/// <summary>
/// fix33 双实例热切窗口上界（2026-11-13 真机卡死批）：热切未决期间新旧两路软解并存，
/// 盒子 CPU 弱，久拖不决即整机拖死（真机「只能关机重启」帮凶之一）。
/// 受理后窗口超时仍未提交/未失败即自动放弃切换，留当前档。
/// </summary>
public static class HotSwapPolicy
{
    /// <summary>热切受理后多久未提交即自动放弃（毫秒）。</summary>
    public const int WindowMs = 30_000;
}

/// <summary>
/// 代数守卫（v1.0.8 第2项，与重点第1/6项联动）：换台/重连各推进一代，
/// 旧重连定时器与事件回调按代数作废，防旧台重连覆盖新台、防黑屏通道复活。
/// </summary>
public class GenerationCounter
{
    private long _gen;
    public long Current => System.Threading.Volatile.Read(ref _gen);
    public long NextGeneration() => System.Threading.Interlocked.Increment(ref _gen);
    public bool IsCurrent(long gen) => Current == gen;
}
