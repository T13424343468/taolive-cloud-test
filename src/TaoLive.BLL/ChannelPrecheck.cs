using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.BLL;

/// <summary>频道可连状态（v1.0.8 开机预检）：未探测/探测中/可用/失效。</summary>
public enum ChannelHealth
{
    Unknown,
    Checking,
    Alive,
    Dead,
}

/// <summary>来源级健康记录（v1.0.9 第⑬项）：每来源单独记录通断与最近核验时间。
/// v1.0.16 抢流方案 A：新增延迟/卡顿率/连续成功/最近使用四项记账（评分择优用，向后兼容缺省）。</summary>
public class SourceHealthEntry
{
    public string SourceId { get; set; } = "";
    public string EpgKey { get; set; } = "";
    public ChannelHealth State { get; set; } = ChannelHealth.Unknown;
    public long CheckedAtMs { get; set; }   // 最近核验时间（Unix 毫秒，跨启动可比；0=未核验）
    public int ConsecutiveFails { get; set; } // 连续失败计数（v1.0.10：达阈值自动淘汰交回重探）
    public int FirstDelayMs { get; set; }     // v1.0.16 方案 A/E：首包/探测延迟 EMA（0=未知）
    public double StallRate { get; set; }     // v1.0.16 方案 A：卡顿率 0–1（0=未观测，评分按流畅满分处理）
    public int SuccessStreak { get; set; }    // v1.0.16 方案 A：连续成功次数（失败清零，衰减不清零）
    public long LastUsedMs { get; set; }      // v1.0.16 方案 A：最近被选去播放的时刻（0=从未；衰减基准）
}

/// <summary>
/// 开机预检握手机制（v1.0.8，用户 2026-11-07 拍板；v1.0.9 第⑬项升级为按频道全源检测）：
/// - 按顺序对每个台轻量探测（只取票/取节目单，不建视频传输），限速执行防被服务器判异常流量；
/// - 同名频道在各来源的变体逐一探测，任一来源可达即整台可用，全源不可达才进无信号组；
/// - 来源级健康表单独记录每源通断与核验时间，供播放选源与卡死自救换源共用；
/// - 失效台可复测（Requeue）：票据/网络恢复即自动转回可用，恢复可切。
/// </summary>
public class ChannelPrecheck
{
    private readonly Func<Channel, Task<bool>> _probe;
    private readonly int _intervalMs;
    private readonly ConcurrentDictionary<string, ChannelHealth> _states = new();
    private ConcurrentDictionary<string, SourceHealthEntry> _sourceHealth = new();

    /// <summary>连续失败多少次自动淘汰该来源记录（v1.0.10）：淘汰后交回预检重探，绝不把挂了的源一直当可用。</summary>
    public const int RetireAfterConsecutiveFails = 3;

    /// <summary>状态变化事件：(台 EPG 键, 新状态)。UI 层订阅后刷新列表。</summary>
    public event Action<string, ChannelHealth>? StateChanged;

    /// <summary>
    /// 循环暂停闸（老板 2026-11-08 拍板：连接优先 + 续接循环）：
    /// 连接优先时在任意台节点上就地挂起，冷了之后从暂停点接着往下探（不从头重转一轮）。
    /// null=无闸照常跑；等待期间取消令牌触发即退出（正常退出，不属异常）。
    /// </summary>
    public Func<CancellationToken, Task>? PauseGate { get; set; }

    /// <summary>源健康账持久化（v1.0.10）：UI 注入后，每次健康表变化自动节流落盘；启动时经 Attach 灌回历史账。</summary>
    public SourceHealthStore? HealthStore { get; private set; }

    /// <summary>
    /// 注入持久化存储：会话内已有结论与历史账合并（同键取核验时间新者）后，
    /// 改为与账本共享同一本健康表——此后预检/播放实测回写直接落在账本条目上，节流落盘即所见即所存。
    /// </summary>
    public void AttachHealthStore(SourceHealthStore store)
    {
        HealthStore = store ?? throw new ArgumentNullException(nameof(store));
        // 会话内已有结论并入账本（不覆盖账本里更新的记录）
        foreach (var kv in _sourceHealth)
        {
            if (!store.Entries.TryGetValue(kv.Key, out var existing) || kv.Value.CheckedAtMs >= existing.CheckedAtMs)
                store.Entries[kv.Key] = kv.Value;
        }
        _sourceHealth = store.Entries; // 共账：单一事实来源，回写即入账本
    }

    public ChannelPrecheck(Func<Channel, Task<bool>> probe, int intervalMs = 600)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _intervalMs = Math.Max(0, intervalMs);
    }

    private static string SourceKey(Channel ch) => $"{ch.SourceId}:{ch.EpgKey}";

    private static string GroupKey(Channel ch) => NormalizeName(ch.Name);

    /// <summary>同频道各来源归并键：台名去空格后一致视为同一频道（不硬编码机型与来源）。</summary>
    internal static string NormalizeName(string name)
    {
        var trimmed = (name ?? "").Replace(" ", "").Replace("　", "");
        return string.IsNullOrEmpty(trimmed) ? (name ?? "") : trimmed;
    }

    /// <summary>查询某台当前状态（未探测=Unknown）。同频道任一来源可达=可用。</summary>
    public ChannelHealth Of(Channel ch) =>
        _states.TryGetValue(ch.EpgKey, out var s) ? s : ChannelHealth.Unknown;

    /// <summary>查询某台某来源的健康记录（未探测返回 null）。</summary>
    public SourceHealthEntry? SourceHealthOf(Channel ch) =>
        _sourceHealth.TryGetValue(SourceKey(ch), out var e) ? e : null;

    /// <summary>
    /// 该台在健康表里评分最高的可用来源变体（v1.0.16 方案 A：从「最近验证」升级为「评分最高」）：
    /// 评分 = 延迟、卡顿率、连续成功加权（指数衰减不清零）；并列取核验时间新者。
    /// 播放选源与卡死自救换源优先取它；同频道没有已验证来源时返回当前台本身。
    /// </summary>
    public Channel? BestAliveSource(Channel ch, IEnumerable<Channel> catalog)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Channel? best = null;
        double bestScore = double.MinValue;
        long bestAt = -1;
        foreach (var cand in catalog)
        {
            if (GroupKey(cand) != GroupKey(ch) || cand.Unavailable) continue;
            var entry = SourceHealthOf(cand);
            if (entry is null || entry.State != ChannelHealth.Alive) continue;
            double score = SourceScorer.Score(entry, nowMs) ?? 0.0;
            if (score > bestScore || (score == bestScore && entry.CheckedAtMs > bestAt))
            {
                bestScore = score;
                bestAt = entry.CheckedAtMs;
                best = cand;
            }
        }
        return best ?? (Of(ch) == ChannelHealth.Alive ? ch : null);
    }

    /// <summary>直接标记状态（播放实测/预检覆盖写）。仅在状态真正变化时发事件，防重复刷列表。</summary>
    public void MarkState(Channel ch, ChannelHealth state)
    {
        var prev = Of(ch);
        if (prev == state) return;
        _states[ch.EpgKey] = state;
        StateChanged?.Invoke(ch.EpgKey, state);
    }

    /// <summary>
    /// 来源级结果回写（v1.0.9 第⑬项）：播放实测与卡死自救换源的结论写进健康表，
    /// 播放实测覆盖预检结论；同频道任一来源通即整台转可用。
    /// v1.0.10：连续失败达阈值自动淘汰（交回重探）；每次变化节流落盘。
    /// v1.0.16 方案 A/E：可选带实测延迟（毫秒），EMA 记入 FirstDelayMs；成功累加连续成功，失败清零。
    /// </summary>
    public void ReportSourceResult(Channel ch, bool ok, long nowMs, int? delayMs = null)
    {
        WriteSourceResult(ch, ok, nowMs, delayMs);
    }

    /// <summary>被选去播放时记账（v1.0.16 方案 A）：刷新最近使用时刻，作为评分衰减基准。</summary>
    public void MarkUsed(Channel ch, long nowMs)
    {
        var entry = _sourceHealth.GetOrAdd(SourceKey(ch), _ => new SourceHealthEntry
        {
            SourceId = ch.SourceId,
            EpgKey = ch.EpgKey,
        });
        entry.LastUsedMs = nowMs;
        HealthStore?.MarkDirty();
    }

    /// <summary>
    /// 卡顿率顺路回写（v1.0.20 批1，冻结稿 A「近期卡顿率：巡检每 5 秒已喂帧统计，顺路回写」）：
    /// 巡检节拍对当前播放源观测一次，EMA(α=0.5) 收敛近期卡顿率——零额外请求。
    /// 只记快慢不改通断：State/CheckedAtMs 原样（不刷新核验时间），判死仍走连败 3 次原口径。
    /// </summary>
    public void ReportStallObserved(Channel ch, bool stalled)
    {
        var entry = _sourceHealth.GetOrAdd(SourceKey(ch), _ => new SourceHealthEntry
        {
            SourceId = ch.SourceId,
            EpgKey = ch.EpgKey,
        });
        entry.StallRate = (entry.StallRate + (stalled ? 1.0 : 0.0)) / 2.0;
        HealthStore?.MarkDirty();
    }

    /// <summary>统一写入口径：探活结论、连续失败淘汰、落盘脏标记都在这一处（预检循环与播放实测共用）。</summary>
    private void WriteSourceResult(Channel ch, bool ok, long nowMs, int? delayMs = null)
    {
        var key = SourceKey(ch);
        var entry = _sourceHealth.GetOrAdd(key, _ => new SourceHealthEntry
        {
            SourceId = ch.SourceId,
            EpgKey = ch.EpgKey,
        });
        entry.State = ok ? ChannelHealth.Alive : ChannelHealth.Dead;
        entry.CheckedAtMs = nowMs > 0 ? nowMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entry.ConsecutiveFails = ok ? 0 : entry.ConsecutiveFails + 1;
        if (ok) entry.SuccessStreak++;
        else entry.SuccessStreak = 0;
        if (delayMs is { } d && d > 0)
        {
            // EMA 平滑（α=0.5）：单次毛刺不支配，趋势尽快跟上
            entry.FirstDelayMs = entry.FirstDelayMs <= 0 ? d : (entry.FirstDelayMs + d) / 2;
        }
        if (!ok && entry.ConsecutiveFails >= RetireAfterConsecutiveFails)
        {
            _sourceHealth.TryRemove(key, out _); // 连挂自动淘汰：不再被选源取用，交回预检重探
            HealthStore?.MarkDirty();
            return;
        }
        HealthStore?.MarkDirty();
        if (ok) MarkState(ch, ChannelHealth.Alive);
    }

    /// <summary>失效台复测入口：置回探测中，探测轮次走完自动更新。</summary>
    public void Requeue(Channel ch) => MarkState(ch, ChannelHealth.Checking);

    /// <summary>
    /// 按台单顺序逐台探测，每台之间限速间隔。不阻塞调用方播放流程（后台 Task 执行）。
    /// 探测抛异常按失效处理——预检自己不吞播放在途状态，只记录结论。
    /// 同频道（台名归并）的多个来源变体逐一探测，任一来源通=整台可用。
    /// keepKnownState（循环预检轮次用，v1.0.8）：true 时已知状态的台不先置「检测中」，
    /// 复测期间原状态保持显示（可用台常绿、失效台留在无信号组），探测完才翻转——列表不闪烁。
    /// </summary>
    public async Task RunAsync(IEnumerable<Channel> channels, CancellationToken ct = default, bool keepKnownState = false)
    {
        foreach (var group in channels.GroupBy(GroupKey))
        {
            if (ct.IsCancellationRequested) return;
            // 续接循环（老板 2026-11-08 拍板）：连接优先时在每个台节点前就地挂起，恢复后从断点继续，不从头重转
            if (PauseGate is { } gate) await gate(ct).ConfigureAwait(false);
            var variants = group.ToList();
            var lead = variants[0];
            if (!keepKnownState || Of(lead) == ChannelHealth.Unknown)
                MarkState(lead, ChannelHealth.Checking);
            var anyOk = false;
            foreach (var ch in variants)
            {
                if (ct.IsCancellationRequested) return;
                bool ok;
                // v1.0.16 方案 E（预热记账，零新增请求）：预热探测顺路记延迟——探测耗时即轻探往返近似首包耗时
                long probeStart = Environment.TickCount64;
                try { ok = await _probe(ch).ConfigureAwait(false); }
                catch (Exception ex) { ok = false; BllLog.Warn("ChannelPrecheck", $"预检探源异常（{ch.SourceId}，台 {ch.Name}，url={BllLog.MaskUrl(ch.StreamUrl)}）——按失效处理，交回重探", ex); }
                long probeDelay = Environment.TickCount64 - probeStart;
                WriteSourceResult(ch, ok, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ok ? (int)Math.Min(probeDelay, int.MaxValue) : null);
                anyOk |= ok;
                if (_intervalMs > 0)
                {
                    try { await Task.Delay(_intervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { BllLog.Info("ChannelPrecheck", "预检循环取消（正常退出，不属异常）"); return; }
                }
            }
            foreach (var ch in variants)
                MarkState(ch, anyOk ? ChannelHealth.Alive : ChannelHealth.Dead);
        }
    }
}

/// <summary>
/// 频道列表宽度度量（v1.0.8，用户拍板「保证信息完整前提下越小越好」）：
/// - 常态按内容实际宽度 + 左右留白，越小越好；
/// - 内容再窄也有下限兜底（360dp，保证台名/状态可读）；
/// - 内容再宽也钳在半屏内（绝不盖住整屏）。
/// </summary>
public static class ListMetrics
{
    public const float MinWidthDp = 360f;

    /// <summary>maxRowTextPx：最宽一行的内容像素宽；density：屏幕密度；screenW：屏幕像素宽。</summary>
    public static int ComputeWidthPx(float maxRowTextPx, float density, int screenW)
    {
        float total = Math.Max(0, maxRowTextPx) + 48 * density; // 行内容 + 左右留白
        float lo = MinWidthDp * density;
        float hi = screenW / 2f;
        return (int)Math.Clamp(total, Math.Min(lo, hi), Math.Max(lo, hi));
    }
}
