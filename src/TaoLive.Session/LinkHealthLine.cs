using System;

namespace TaoLive.Session;

/// <summary>
/// 链路健康线（vc85 蓝本 v2.5，六项优化⑥「坏帧信号进健康线」的承载）：
/// 把信号源统一观察量（水位掉速 + 分片间隔 + 下载吞吐 + 坏帧率）合成一个裕度读数，
/// 裕度 &gt; 0 全速、&lt; 0 停（偷拉管道闸门与择路降权共用），钳制 [-1, 1] 防单点打爆。
/// 纯内存单点账：喂账方（会话微卡/坏帧/偷拉拉速）多线程，读数方（偷拉泵/排序）走快照——
/// 观察量按指数滑动归并，最后写赢即可，不追求逐笔精确（质量账只服务近期择路）。
/// </summary>
public sealed class LinkHealthLine
{
    // —— 出厂权重（只调常数不改结构；理由挂账）——
    /// <summary>水位项权重：掉速越快裕度越负（直接观测「快要断粮」，最高权重）。</summary>
    internal const double WeightDropRate = 0.5;
    /// <summary>间隔项权重：分片越拖越久（供给节奏不稳），次高。</summary>
    internal const double WeightInterval = 0.25;
    /// <summary>吞吐项权重：下载速度富余加正裕度（带宽厚=有余粮），与间隔项对价。</summary>
    internal const double WeightThroughput = 0.25;
    /// <summary>坏帧项权重：坏帧越多裕度越负（画面烂=源差，六项优化⑥），封顶防单事件打爆。</summary>
    internal const double WeightBadFrames = 0.30;

    /// <summary>掉速 EMA 半衰窗：约 8 秒（快速跟上恶化、又不被单拍抖动牵着走）。</summary>
    public static readonly TimeSpan DropEmaHalfLife = TimeSpan.FromSeconds(8);

    /// <summary>坏帧账半衰窗（vc96 第2轮）：约 10 分钟——坏帧信号只服务近期择路，
    /// 攒过的坏帧随时间指数衰减归零；不衰减则坏帧终身累积，裕度永久带负项，
    /// 偷拉管道被永久钳死（AllowFactor 恒 0）、回主源水位条件永不满足。</summary>
    public static readonly TimeSpan BadFrameHalfLife = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Func<long> _monoMs;
    private double _dropRatePerSec;      // 水位掉速（毫秒水位 / 秒，正值=在掉）
    private long _dropEmaAtMs = -1;     // -1=尚无首拍（0 是合法时钟值，不能当哨兵）
    private double _lastIntervalMs = -1; // 最近分片间隔（毫秒；-1=无账）
    private double _lastThroughputKbps;  // 最近下载吞吐（kbps；0=无账）
    private long _badFrameCount;         // 累计坏帧（喂账方累加，封顶换算；按半衰窗指数衰减，vc96 第2轮）
    private long _badFramesAtMs = -1;    // 坏帧账最近落账/衰减时刻（-1=尚无账，0 是合法时钟值不能当哨兵）

    public LinkHealthLine(Func<long>? monoMs = null)
    {
        _monoMs = monoMs ?? (() => Environment.TickCount64 & 0x7FFFFFFFFFFFFFFF);
    }

    /// <summary>喂水位掉速观察：前后两拍水位差换算每秒掉速并并进 EMA（毫秒/秒，负值=在涨）。</summary>
    public void ReportStockDrop(long bufferedNowMs, long bufferedLastMs, long sinceMs)
    {
        if (sinceMs <= 0) return;
        var dropMsPerSec = (bufferedLastMs - bufferedNowMs) * 1000.0 / sinceMs;
        lock (_gate)
        {
            var now = _monoMs();
            // EMA 归并：新观察按时间差折半衰权（指数滑动，半衰窗内旧账仍有分量）
            var alpha = Math.Exp(-(now - _dropEmaAtMs) / DropEmaHalfLife.TotalMilliseconds * 0.693);
            _dropRatePerSec = _dropEmaAtMs < 0 ? dropMsPerSec : alpha * _dropRatePerSec + (1 - alpha) * dropMsPerSec;
            _dropEmaAtMs = now;
        }
    }

    /// <summary>喂分片节奏：偷拉/播放侧每次取片的上片间隔（毫秒）。</summary>
    public void ReportSegmentInterval(long intervalMs)
    {
        if (intervalMs <= 0) return;
        lock (_gate) _lastIntervalMs = intervalMs;
    }

    /// <summary>喂吞吐：最近一次取片的实测下载速度（kbps）。</summary>
    public void ReportThroughput(long kbps)
    {
        lock (_gate) _lastThroughputKbps = Math.Max(0, kbps);
    }

    /// <summary>喂坏帧（六项优化⑥）：壳层/内核坏帧信号折算入账，只记不判。</summary>
    public void ReportBadFrames(int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            DecayBadFramesLocked(_monoMs());
            _badFrameCount += count;
        }
    }

    /// <summary>坏帧账时间衰减（vc96 第2轮，锁内调用）：按半衰窗指数衰减并回写账面与时刻——
    /// 与「质量账只服务近期择路」口径一致，坏帧攒过即随时间淡出，不终身带罚。</summary>
    private void DecayBadFramesLocked(long nowMs)
    {
        if (_badFramesAtMs < 0 || _badFrameCount <= 0)
        {
            _badFramesAtMs = nowMs;
            return;
        }
        var elapsed = nowMs - _badFramesAtMs;
        if (elapsed > 0)
        {
            var factor = Math.Exp(-elapsed / BadFrameHalfLife.TotalMilliseconds * 0.693);
            // 折算残账：≥1 帧期间向上取整保信号不提前消失；低于 1 帧归零——
            // 若恒向上取整，残账永远卡 1 帧，margin 恒带 -0.03 负项，低码率档（吞吐≤2.24Mbps
            // 补不回正）AllowFactor 恒 0，偷拉管道仍被永久钳死（vc96 第3轮修正）。
            var decayed = _badFrameCount * factor;
            _badFrameCount = decayed < 1 ? 0 : (long)Math.Ceiling(decayed);
        }
        _badFramesAtMs = nowMs;
    }

    /// <summary>
    /// 合成裕度 [-1, 1]：&gt;0 全速、&lt; 0 停。归一基准（出厂口径）：
    /// 掉速 20 秒/秒（2 秒水位 1 秒掉完）即打满负项；分片间隔超 2 倍切片周期（10 秒）打满负项；
    /// 吞吐 2 Mbps 起判富余（直播流标称 1~4 Mbps 量级，2 Mbps 上下=刚好够）；坏帧 10 帧打满负项。
    /// </summary>
    public double Margin()
    {
        lock (_gate)
        {
            DecayBadFramesLocked(_monoMs()); // 读路径同步衰减：坏帧停喂后残账也随时间淡出，不终身带罚（vc96 第2轮）
            double stock = Math.Clamp(_dropRatePerSec / 20_000.0, -1, 1);
            double interval = _lastIntervalMs < 0 ? 0 : Math.Clamp((_lastIntervalMs - 5000.0) / 5000.0, -1, 1);
            double throughput = _lastThroughputKbps <= 0 ? 0 : Math.Clamp((_lastThroughputKbps - 2000.0) / 2000.0, -1, 1);
            double bad = Math.Clamp(_badFrameCount / 10.0, 0, 1);
            double margin = -WeightDropRate * stock
                   + WeightInterval * (-interval)     // 间隔拉长（正值）= 负贡献
                   + WeightThroughput * throughput    // 吞吐富余 = 正贡献
                   - WeightBadFrames * bad;           // 坏帧 = 纯负项
            return Math.Clamp(margin, -1, 1);
        }
    }

    /// <summary>偷拉管道闸门因子：裕度&gt;0 全速（1.0）、&lt;=0 停（0.0）——口径即蓝本「&gt;0 全速、&lt;0 停」。</summary>
    public double AllowFactor()
    {
        var m = Margin();
        return m > 0 ? 1.0 : 0.0;
    }

    /// <summary>复位（vc96 第3轮，换台口径）：观察账全清——坏帧/掉速/间隔/吞吐都是本台线路观察量，
    /// 换台后旧账跟去新台会把新台裕度压负（30 帧坏帧账按 10 分钟半衰约 51 分钟才归零），
    /// 偷拉管道被旧账钳死、码率被旧账压档；与码率治理器「账只记本台，换台即 Reset」同口径。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _dropRatePerSec = 0;
            _dropEmaAtMs = -1;
            _lastIntervalMs = -1;
            _lastThroughputKbps = 0;
            _badFrameCount = 0;
            _badFramesAtMs = -1;
        }
    }
}
