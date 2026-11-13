using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

// ============ vc53 修复批任务 4·抖动换源（老板 2026-11-14 00:3x 拍板入批）============
// 「画面时不时抖一下」的根因常在源侧（CDN 抖动/节点劣化），水位反复攒了又掉=抖动实锤。
// 与卡死自救（StallRescue）分工：卡死=时钟停走走分级自救；抖动=画面还在走但水位反复掉底，
// 先于卡死换一次源（同台换票/备援），别等真卡死才动手。纯逻辑可测；执行由 LiveVideoHost
// 在水位观测点喂事件并分派（换票走既有静默重取流链路）。

/// <summary>
/// 抖动换源判定器（单实例跟一个播放会话）：观测窗内「水位低攒缓冲」动作反复出现达阈值
/// 即判抖动，建议换源一次；换源后冷却期内不再建议（防抖动换源循环拖垮播放）。
/// </summary>
public sealed class JitterSwitchPolicy
{
    /// <summary>抖动观测窗（毫秒）：与自救介入节流窗同口径（90 秒）。</summary>
    public const long WindowMs = 90_000;

    /// <summary>窗内攒水位动作次数阈值：90 秒内 3 次反复低水位=抖动实锤（单次低读只是毛刺，vc38 已有确认窗）。</summary>
    public const int EventsToSwitch = 3;

    /// <summary>换源后冷却（毫秒）：5 分钟内不再因抖动换源（真机验证节奏，防乒乓）。</summary>
    public const long CooldownMs = 300_000;

    private readonly Queue<long> _stamps = new();
    private long _lastSwitchMs = long.MinValue;
    private readonly Func<long> _nowMs;

    public JitterSwitchPolicy(Func<long>? nowMs = null) =>
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>记一次攒水位动作（调用方在水位动作翻转为 BuildUp 时喂一次，毛刺确认由上游口径把守）。
    /// 冷却期内不记新账：刚换过源，窗内旧抖动不许攒出新的换源建议（防乒乓）。</summary>
    public void Record()
    {
        long now = _nowMs();
        if (_lastSwitchMs != long.MinValue && now - _lastSwitchMs < CooldownMs) return;
        _stamps.Enqueue(now);
        while (_stamps.Count > EventsToSwitch) _stamps.Dequeue();
    }

    /// <summary>是否建议换源：非冷却期 + 窗内攒水位动作达阈值。建议成立后由调用方 MarkSwitched。</summary>
    public bool ShouldSwitch()
    {
        long now = _nowMs();
        if (_lastSwitchMs != long.MinValue && now - _lastSwitchMs < CooldownMs) return false;
        Prune(now);
        return _stamps.Count >= EventsToSwitch;
    }

    /// <summary>换源已执行：进入冷却，账面清零重攒。</summary>
    public void MarkSwitched()
    {
        _lastSwitchMs = _nowMs();
        _stamps.Clear();
    }

    private void Prune(long now)
    {
        while (_stamps.Count > 0 && now - _stamps.Peek() >= WindowMs) _stamps.Dequeue();
    }
}
