﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TaoLive.Model;

namespace TaoLive.Session;

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

    // 跨线程字段（vc73 审查清零 P2）：预检/播放实测线程写、选源与落盘线程读——
    // 32 位设备上 long 直接读写会撕裂（高半字与低半字不一致），改走 Interlocked 保证原子。
    private long _checkedAtMs; // 最近核验时间（Unix 毫秒，跨启动可比；0=未核验）
    public long CheckedAtMs
    {
        get => Interlocked.Read(ref _checkedAtMs);
        set => Interlocked.Exchange(ref _checkedAtMs, value);
    }

    public int ConsecutiveFails { get; set; } // 连续失败计数（v1.0.10：达阈值自动淘汰交回重探）
    public int ConsecutiveOks { get; set; }   // 连续探活成功计数（收口批①回主源判据；2026-11-16 老板批第①条：随健康账落盘，重启续账不清零）
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
    /// <summary>打点出口（BLL 不反引 UI）：由宿主层注入；未注入静默（订阅方异常可丢，状态账不受影响）。</summary>
    public static Action<string>? Reporter { get; set; }

    private static void Report(string msg)
    {
        try { Reporter?.Invoke($"[ChannelPrecheck] {msg}"); } catch { }
    }
    private readonly Func<Channel, Task<bool>> _probe;
    private readonly int _intervalMs;
    private readonly ConcurrentDictionary<string, ChannelHealth> _states = new();
    private ConcurrentDictionary<string, SourceHealthEntry> _sourceHealth = new();

    /// <summary>连续失败多少次自动淘汰该来源记录（v1.0.10）：淘汰后交回预检重探，绝不把挂了的源一直当可用。</summary>
    public const int RetireAfterConsecutiveFails = 3;

    /// <summary>健康账新鲜期（老板令 2026-11-15）：超过 24 小时未核验的条目作废——读作未探测，交回预检重探。</summary>
    public const long StaleAfterMs = 24 * 60 * 60 * 1000L;

    private static bool IsStale(SourceHealthEntry e) =>
        e is null || (e.CheckedAtMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.CheckedAtMs >= StaleAfterMs);

    /// <summary>状态变化事件：(台 EPG 键, 新状态)。UI 层订阅后刷新列表。</summary>
    public event Action<string, ChannelHealth>? StateChanged;

    /// <summary>源健康账持久化（v1.0.10）：UI 注入后，每次健康表变化自动节流落盘；启动时经 Attach 灌回历史账。
    /// #78 健康账唯一令（2026-11-15）：全应用有且只有这一本账——所有选源读它、所有选源动作（预检探活/播放实测/自救换线）写它。
    /// 幽灵评分账（SourceScoreStore）退役：产线从未挂载、零写入，留着即「第二本账」违规。</summary>
    public SourceHealthStore? HealthStore { get; private set; }

    /// <summary>
    /// 注入持久化存储：会话内已有结论与历史账合并（同键取核验时间新者）后，
    /// 改为与账本共享同一本健康表——此后预检/播放实测回写直接落在账本条目上，节流落盘即所见即所存。
    /// </summary>
    public void AttachHealthStore(SourceHealthStore store)
    {
        HealthStore = store ?? throw new ArgumentNullException(nameof(store));
        store.Load(); // 挂账即载入（2026-11-16 老板批第①条）：重启回显老账——此前挂了账却从没读过，落盘形同虚设
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

    /// <summary>查询某台当前状态（未探测=Unknown）。
    /// 重启回显（2026-11-16 老板批第①条）：频道级在播态只存内存，重启后按健康账回显上次结论，
    /// 不再整表清灰干等——预检复测期间保持显示（keepKnownState 口径照旧），探测完才校准翻转。</summary>
    public ChannelHealth Of(Channel ch)
    {
        if (_states.TryGetValue(ch.EpgKey, out var s)) return s;
        return DeriveFromLedger(ch.EpgKey);
    }

    /// <summary>健康账回显判定：同台组内任一非陈旧活源=可用；仅死账（保质期内）=失效；无账=未探测。</summary>
    private ChannelHealth DeriveFromLedger(string epgKey)
    {
        var hasDead = false;
        foreach (var kv in _sourceHealth)
        {
            if (kv.Value.EpgKey != epgKey || IsStale(kv.Value)) continue;
            if (kv.Value.State == ChannelHealth.Alive) return ChannelHealth.Alive;
            hasDead = true;
        }
        return hasDead ? ChannelHealth.Dead : ChannelHealth.Unknown;
    }

    /// <summary>查询某台某来源的健康记录（未探测返回 null；超 24 小时未核验=作废重探，老板令 2026-11-15）。</summary>
    public SourceHealthEntry? SourceHealthOf(Channel ch)
    {
        if (!_sourceHealth.TryGetValue(SourceKey(ch), out var e)) return null;
        return IsStale(e) ? null : e;
    }

    /// <summary>列表健康点门槛（老板 2026-11-16 深夜拍板降门槛：最近 1 轮通过即绿，变绿快 3 倍，判定略糙可接受；
    /// 原口径=连续 3 轮全通才绿）。失败即回红（死账 TTL/连挂淘汰口径不变）。</summary>
    public const int MarkGreenMinConsecutiveOks = 1;

    /// <summary>
    /// 列表健康点三态（方案书收口批②；绿门槛频道级语义 2026-11-16 老板拍板）：
    /// 最近 1 轮探活通过=绿（Alive）、检测中/未探=灰（Checking）、全败=红（Dead）。
    /// 绿判据改频道级：同台组（同 EPG 键）内任一来源连续探活达标即整台绿——播放只给正在播的
    /// 来源记活账，列表行绑定的可能是同台另一变体，按行内单源判绿会漏亮（断点一修复）。
    /// 只读健康账展示，不写账（v33 条款）。
    /// </summary>
    public ChannelHealth MarkOf(Channel ch)
    {
        var state = Of(ch);
        if (state == ChannelHealth.Dead) return ChannelHealth.Dead;
        if (state == ChannelHealth.Alive)
        {
            return AnyGroupSourceGreen(ch.EpgKey) ? ChannelHealth.Alive : ChannelHealth.Checking;
        }
        return ChannelHealth.Checking;
    }

    /// <summary>同台组（同 EPG 键）内任一来源连续探活达标即绿（绿点频道级语义，2026-11-16）。</summary>
    private bool AnyGroupSourceGreen(string epgKey) =>
        _sourceHealth.Any(kv => kv.Value.EpgKey == epgKey
            && kv.Value.State == ChannelHealth.Alive
            && kv.Value.ConsecutiveOks >= MarkGreenMinConsecutiveOks);

    /// <summary>
    /// 该台在健康表里最近验证可用的来源变体（v1.0.9 第⑬项）：
    /// 播放选源与卡死自救换源优先取它；同频道没有已验证来源时返回当前台本身。
    /// </summary>
    public Channel? BestAliveSource(Channel ch, IEnumerable<Channel> catalog)
    {
        // #78 健康账唯一令（2026-11-15）：幽灵评分账退役——选源一律读唯一健康账，同分序退化「最新核验优先」
        // （v28 既有退化路径，无第二本账可读）。
        Channel? bestFallback = null;
        long bestAt = -1;
        foreach (var cand in catalog)
        {
            if (GroupKey(cand) != GroupKey(ch) || cand.Unavailable) continue;
            var entry = SourceHealthOf(cand);
            if (entry is null || entry.State != ChannelHealth.Alive) continue;
            if (entry.CheckedAtMs > bestAt)
            {
                bestAt = entry.CheckedAtMs;
                bestFallback = cand;
            }
        }
        return bestFallback ?? (Of(ch) == ChannelHealth.Alive ? ch : null);
    }

    /// <summary>直接标记状态（播放实测/预检覆盖写）。仅在状态真正变化时发事件，防重复刷列表。
    /// vc52 P8：StateChanged 订阅方异常不得毒化状态写入——事件从回写点抛出会炸进调用方
    /// （播放实测/自救换源线程），状态已落账但事件炸了等于白写；订阅方异常只落日志。</summary>
    public void MarkState(Channel ch, ChannelHealth state)
    {
        // 对照基准只看本会话在播态账（不读健康账回显）：探活回写先落健康账再 MarkState，
        // 若 prev 读回显账，新写结论永远被误判「无变化」，首翻事件丢失（回归 2026-11-16）
        var prev = _states.TryGetValue(ch.EpgKey, out var s) ? s : ChannelHealth.Unknown;
        if (prev == state) return;
        _states[ch.EpgKey] = state;
        try { StateChanged?.Invoke(ch.EpgKey, state); }
        catch (Exception ex) { Report($"状态事件订阅方异常（不影响状态账本）：{ex.Message}"); }
    }

    /// <summary>
    /// 来源级结果回写（v1.0.9 第⑬项）：播放实测与卡死自救换源的结论写进健康表，
    /// 播放实测覆盖预检结论；同频道任一来源通即整台转可用。
    /// v1.0.10：连续失败达阈值自动淘汰（交回重探）；每次变化节流落盘。
    /// playback=播放实测强证据（真机反馈批#4 修 2026-11-15）：成功即圆点绿门槛直接达标。
    /// </summary>
    public void ReportSourceResult(Channel ch, bool ok, long nowMs, long ms = 0, bool timeout = false, bool playback = false)
    {
        WriteSourceResult(ch, ok, nowMs, ms, timeout, playback);
    }

    /// <summary>统一写入口径：探活结论、连续失败淘汰、落盘脏标记都在这一处（预检循环与播放实测共用）。
    /// #78 健康账唯一令（2026-11-15）：写账只落唯一健康账；幽灵评分账写入已随账退役。</summary>
    private void WriteSourceResult(Channel ch, bool ok, long nowMs, long ms = 0, bool timeout = false, bool playback = false)
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
        // 播放实测=强证据（#4 修）：真画面在手，绿点判据直接达标；预检轻探照旧逐轮累计
        var prevOks = entry.ConsecutiveOks;
        entry.ConsecutiveOks = ok
            ? (playback ? MarkGreenMinConsecutiveOks : entry.ConsecutiveOks + 1)
            : 0;
        if (!ok && entry.ConsecutiveFails >= RetireAfterConsecutiveFails)
        {
            _sourceHealth.TryRemove(key, out _); // 连挂自动淘汰：不再被选源取用，交回预检重探
            HealthStore?.MarkDirty();
            return;
        }
        HealthStore?.MarkDirty();
        // 绿门槛跨越补发刷新（断点二 2026-11-16 拍板）：来源账从不足额跨到足额时，频道级状态可能
        // 未翻转（MarkState 仅翻转发事件），列表绿点会干等下次翻转——此处补发一次事件，刷新节流在壳层。
        if (ok && prevOks < MarkGreenMinConsecutiveOks && entry.ConsecutiveOks >= MarkGreenMinConsecutiveOks)
        {
            try { StateChanged?.Invoke(ch.EpgKey, ChannelHealth.Alive); }
            catch (Exception ex) { Report($"状态事件订阅方异常（不影响状态账本）：{ex.Message}"); }
        }
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
            // vc38（92条 S1·预检链兜底）：单台分组异常（StateChanged 订阅方抛错/账本写入异常等）
            // 不掀整轮预检——后续台照常探测，本组留待下一轮复测
            try
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
                    try { ok = await BackgroundLane.RunAsync(() => _probe(ch), ct).ConfigureAwait(false); } // 画面平稳优先令：过全局单车道（首帧门控+播放中≥10s 间隔+全局串行）
                    catch (OperationCanceledException) { return; } // 车道取消（停预检）原样收场，不记探测失败
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
            catch (OperationCanceledException) { return; }
            catch { /* 单台分组异常：兜底吞掉，整轮预检不断 */ }
        }
    }
}

/// <summary>
/// 频道列表宽度度量（vc41 自适应体系，老板 2026-11-12 20:17/20:33 拍板）：
/// - 面板宽 = 屏宽定比 2/3：每次打开一样宽，运行期位置不变；
/// - 旧「内容自适应+半屏钳制+下限 360dp」口径退役；
/// - 一行完整验算：最坏台名在当前字号档装不下面板内宽 → 整行字号降档，面板宽度永不动。
/// </summary>
public static class ListMetrics
{
    /// <summary>面板宽比例（与 UI 层 UiScale.PanelRatio 同源）。</summary>
    public const float PanelRatio = 2f / 3f;

    /// <summary>面板宽（像素）= 屏宽 × 2/3。</summary>
    public static int ComputePanelWidthPx(int screenW) => (int)(screenW * PanelRatio);

    /// <summary>频道行内容宽（U 单位，vc41 常量与 UI 行布局逐项对齐）：双边距(0.75×2) + 台标(2.5) + 台标右(0.25)
    /// + 数字(3×0.55×1.25) + 数字右(0.5) + 星(1.25) + 星右(0.375) + 台名(len×mul×1.12，当前行加粗余量)
    /// + 状态点(1.125) + 点左(0.375)，固定项和=9.9375U。</summary>
    public static float RowContentU(int nameLen, float nameMulU) =>
        1.5f + 2.5f + 0.25f + 3 * 0.55f * 1.25f + 0.5f + 1.25f + 0.375f
        + nameLen * nameMulU * 1.12f + 1.125f + 0.375f;

    /// <summary>面板内宽（U 单位）= 40 × 2/3 − 行双边距 1.5（vc41 行边距 24px=0.75U×2）。</summary>
    public static float PanelInnerU() => 40f * PanelRatio - 1.5f;

    /// <summary>一行完整判定：最坏台名行内容 ≤ 面板内宽（禁折行禁省略号的唯一放行条件）。</summary>
    public static bool FitsPanel(int worstNameLen, float nameMulU) =>
        RowContentU(worstNameLen, nameMulU) <= PanelInnerU();
}
