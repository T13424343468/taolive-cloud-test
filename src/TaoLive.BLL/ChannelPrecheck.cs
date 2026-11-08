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

/// <summary>来源级健康记录（v1.0.9 第⑬项）：每来源单独记录通断与最近核验时间。</summary>
public class SourceHealthEntry
{
    public string SourceId { get; set; } = "";
    public string EpgKey { get; set; } = "";
    public ChannelHealth State { get; set; } = ChannelHealth.Unknown;
    public long CheckedAtMs { get; set; }   // 最近核验时间（Unix 毫秒，跨启动可比；0=未核验）
    public int ConsecutiveFails { get; set; } // 连续失败计数（v1.0.10：达阈值自动淘汰交回重探）
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
    /// 该台在健康表里最近验证可用的来源变体（v1.0.9 第⑬项）：
    /// 播放选源与卡死自救换源优先取它；同频道没有已验证来源时返回当前台本身。
    /// </summary>
    public Channel? BestAliveSource(Channel ch, IEnumerable<Channel> catalog)
    {
        Channel? best = null;
        long bestAt = -1;
        foreach (var cand in catalog)
        {
            if (GroupKey(cand) != GroupKey(ch) || cand.Unavailable) continue;
            var entry = SourceHealthOf(cand);
            if (entry is null || entry.State != ChannelHealth.Alive) continue;
            if (entry.CheckedAtMs > bestAt)
            {
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
    /// </summary>
    public void ReportSourceResult(Channel ch, bool ok, long nowMs)
    {
        WriteSourceResult(ch, ok, nowMs);
    }

    /// <summary>统一写入口径：探活结论、连续失败淘汰、落盘脏标记都在这一处（预检循环与播放实测共用）。</summary>
    private void WriteSourceResult(Channel ch, bool ok, long nowMs)
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
            var variants = group.ToList();
            var lead = variants[0];
            if (!keepKnownState || Of(lead) == ChannelHealth.Unknown)
                MarkState(lead, ChannelHealth.Checking);
            var anyOk = false;
            foreach (var ch in variants)
            {
                if (ct.IsCancellationRequested) return;
                bool ok;
                try { ok = await _probe(ch).ConfigureAwait(false); }
                catch { ok = false; }
                WriteSourceResult(ch, ok, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                anyOk |= ok;
                if (_intervalMs > 0)
                {
                    try { await Task.Delay(_intervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
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
