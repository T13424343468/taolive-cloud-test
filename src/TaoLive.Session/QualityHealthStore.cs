using System;
using System.Collections.Concurrent;
using System.Linq;

namespace TaoLive.Session;

// ============ vc85 蓝本 v2.1 八项落法①·实测账（老板 2026-11-17 拍板「采纳」定稿）============
// 健康账分两本：探活账（SourceHealthStore，绿点+回主源判据，管「通不通」）与实测账（本文件，
// 选源降权，管「好不好」）。四原则：瞬时断流不记坏账——实测账只记微卡与盲区，一次网络抖动不
// 当坏线罪证；记账时机在切换结果侧记（蓝本 v2.1 第 12 条）；健康账三级键（台 EpgKey / 源
// SourceId / 接入点 endpoint）——绿点展示到台级，排序内部到接入点级（蓝本 v2.1 第 6 条）。
// 实测账不落盘：质量账只服务近期择路，重启清零重测，比探活账（跨启动可比）口径更近。

/// <summary>实测账条目。跨线程：播放泵线程写、择路线程读——计数用 Interlocked（32 位撕裂账同探活账口径）。</summary>
public sealed class QualityHealthEntry
{
    public string K { get; set; } = "";
    public long MicrolagCount;   // 微卡顿累计（进缓冲事件，短窗反复才上账；择路降权依据）
    public long MicrolagTrips;   // 当前窗口内进缓冲次数（vc96 P3：按 MicrolagTripsPerTick 次阈值坐实，常量不再死置）
    public long GoodCount;       // 播放实测成功计数（长播健康/切换结果侧记）
    public long LastGoodUtcMs;   // 最近实测成功（Unix 毫秒，0=无实测）
    public bool BlindZone;       // 盲区标记：非 .m3u8 直流/事后判定无时间锚的源「天然贴边」（微卡修法三条③）
    public long MicrolagLastMs;  // 最近一次微卡记账（单调毫秒，衰减基准）
    public long BadFrames;       // 累计坏帧（六项优化⑥：内核视频故障/慢帧疑点折算，只记不判，排序降权口径归 SortScore）
    public long ShortTicketCount; // 短命票计数（vc87 票账降权：起播短窗内即报 HTTP 错的坏票，排序降权口径归 SortScore）
}

/// <summary>
/// 源实测账（内存本，进程生命周期）：微卡上账（微卡修法三条②）、盲区标记（③）、播放实测成功记账。
/// 排序打分纯函数 <see cref="SortScore"/> 供 QueueOrderer 合成探活账使用，断言可直探。
/// </summary>
public sealed class QualityHealthStore
{
    /// <summary>微卡记账窗口：窗内进缓冲满 <see cref="MicrolagTripsPerTick"/> 次记 1 次微卡（出厂 60s/3 次：
    /// 真机「看一秒卡半秒」实测反复进缓冲节奏，60 秒 3 次已不是单次抖动；真机定标只动此表）。</summary>
    public static readonly TimeSpan MicrolagWindow = TimeSpan.FromSeconds(60);
    public const int MicrolagTripsPerTick = 3;

    /// <summary>最近实测成功提权窗：窗内有成功实测的源在排序里吃提权（出厂 10 分钟：直播源质量半衰期短）。</summary>
    public static readonly TimeSpan RecentGoodBonus = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, QualityHealthEntry> _entries = new();
    private readonly Func<long> _monoMs;
    private readonly Func<long> _utcMs;

    /// <summary>_monoMs：单调钟（微卡窗口/防振铃）；_utcMs：墙钟（LastGoodUtcMs 跨启动可比口径，虽不落盘仍存原值）。</summary>
    public QualityHealthStore(Func<long>? monoMs = null, Func<long>? utcMs = null)
    {
        _monoMs = monoMs ?? (() => Environment.TickCount64 & 0x7FFFFFFFFFFFFFFF);
        _utcMs = utcMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal ConcurrentDictionary<string, QualityHealthEntry> Entries => _entries;

    private static string Key(string epgKey, string sourceId, string endpointUrl) =>
        $"{epgKey ?? ""}|{sourceId ?? ""}|{endpointUrl ?? ""}";

    /// <summary>进缓冲记账（播放泵线程）：窗口内进缓冲满 <see cref="MicrolagTripsPerTick"/> 次才坐实微卡
    /// （vc96 P3：阈值按常量真生效——第 1 拍开窗、第 2 拍攒账不坐实、第 3 拍坐实），单次抖动不入账
    /// （四原则：瞬时断流不记坏账）。</summary>
    public void ReportBufferingTrip(string epgKey, string sourceId, string endpointUrl)
    {
        var e = _entries.GetOrAdd(Key(epgKey, sourceId, endpointUrl), k => new QualityHealthEntry { K = k });
        lock (e)
        {
            long now = _monoMs();
            long windowStart = now - (long)MicrolagWindow.TotalMilliseconds;
            // 窗口内已有未坐实记录：本次进缓冲是窗内第 N 拍；用 MicrolagLastMs 兼作窗口最近进缓冲时刻。
            // 攒满 MicrolagTripsPerTick 拍即坐实并清窗口，未满仅续窗（下一拍继续攒）。
            if (e.MicrolagLastMs > 0 && e.MicrolagLastMs >= windowStart) // 0=窗口已清/无账哨兵，不判在窗
            {
                e.MicrolagTrips++;
                e.MicrolagLastMs = now; // 窗内再来一拍：滚动窗口按最近进缓冲续
                if (e.MicrolagTrips < MicrolagTripsPerTick) return; // 未满阈值：不坐实（vc96 P3）
            }
            else
            {
                e.MicrolagLastMs = now; // 新窗口第 1 拍：开窗攒账
                e.MicrolagTrips = 1;
                return;
            }
            e.MicrolagCount++;
            e.MicrolagLastMs = 0; // 坐实一次清窗口：下个窗口重新攒，不连环记同一波
            e.MicrolagTrips = 0;
        }
    }

    /// <summary>播放实测成功（切换结果侧/长播侧记）：提权依据。</summary>
    public void ReportGood(string epgKey, string sourceId, string endpointUrl)
    {
        var e = _entries.GetOrAdd(Key(epgKey, sourceId, endpointUrl), k => new QualityHealthEntry { K = k });
        lock (e)
        {
            e.GoodCount++;
            e.LastGoodUtcMs = _utcMs();
        }
    }

    /// <summary>盲区标记（微卡修法三条③）：非 .m3u8 直流/无时间锚源天然贴边，健康线降权、排序不给头名。幂等。</summary>
    public void MarkBlindZone(string epgKey, string sourceId, string endpointUrl)
    {
        var e = _entries.GetOrAdd(Key(epgKey, sourceId, endpointUrl), k => new QualityHealthEntry { K = k });
        lock (e) e.BlindZone = true;
    }

    /// <summary>坏帧上账（六项优化⑥）：内核视频故障/慢帧疑点折算喂账——只记不判，择路降权归排序合成。</summary>
    public void ReportBadFrames(string epgKey, string sourceId, string endpointUrl, int count)
    {
        if (count <= 0) return;
        var e = _entries.GetOrAdd(Key(epgKey, sourceId, endpointUrl), k => new QualityHealthEntry { K = k });
        lock (e) e.BadFrames += count;
    }

    /// <summary>短命票上账（vc87 票账降权）：端点起播短窗内即报 HTTP 状态错=短命坏票
    /// （真机台账：央视频坏票 6 秒即被源端拒），只记不判，择路降权归排序合成。</summary>
    public void ReportShortTicket(string epgKey, string sourceId, string endpointUrl)
    {
        var e = _entries.GetOrAdd(Key(epgKey, sourceId, endpointUrl), k => new QualityHealthEntry { K = k });
        lock (e) e.ShortTicketCount++;
    }

    /// <summary>是否盲区源（蓝本口径：URL 不含 .m3u8 即直流盲区；时间锚事后判定由播放侧补记）。</summary>
    public static bool IsBlindZoneUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) && !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 排序分（纯函数，越小越优先）。合成口径（README 需求记档 95 行待细化③）：
    /// 最近实测成功优先（-80）&gt; 微卡降权（每坐实 +120）&gt; 坏帧降权（每帧 +2，封顶 +240=两张微卡，
    /// 出厂理由：坏帧是画面级证据但单事件有噪声，上限防一次解码炸账压过多次微卡）&gt;
    /// 短命票降权（vc87：每条 +250，封顶 4 条=+1000——坏票是取流层硬证据但单事件有噪声，
    /// 拟口径待老板复核，封顶防一次坏票风暴压过盲区档）&gt;
    /// 盲区天然贴边（+500，排序不给头名）；
    /// 探活账 Dead 由调用方再叠加沉底项。观察档（共族垫底）沿用注册表先例，由源序在稳定序兜底。
    /// </summary>
    public static long SortScore(QualityHealthEntry? e, bool probeDead)
    {
        long score = 0;
        if (probeDead) score += 10_000; // 探活账死账：垫底（与既有 #74 死台沉底同量级）
        if (e is null) return score;
        if (e.BlindZone) score += 500;
        score += e.MicrolagCount * 120;
        score += Math.Min(e.BadFrames, 120) * 2;
        score += Math.Min(e.ShortTicketCount, 4) * 250; // vc87 短命票降权：封顶 4 条（拟口径待老板复核）
        if (e.LastGoodUtcMs > 0
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.LastGoodUtcMs <= (long)RecentGoodBonus.TotalMilliseconds)
        {
            score -= 80;
        }
        return score;
    }

    /// <summary>查条目（只读快照，择路线程用；无账返回 null）。</summary>
    public QualityHealthEntry? Peek(string epgKey, string sourceId, string endpointUrl) =>
        _entries.TryGetValue(Key(epgKey, sourceId, endpointUrl), out var e) ? e : null;
}
