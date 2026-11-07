using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

/// <summary>
/// 开机预检握手机制（v1.0.8，用户 2026-11-07 拍板）：
/// - 按顺序对每个台轻量探测（只取票/取节目单，不建视频传输），限速执行防被服务器判异常流量；
/// - 记录各台可连状态，事件实时通知 UI 刷新列表；
/// - 只保持「入口有效+状态已知」，不占用带宽与解码——与直播边播边扔的属性一致；
/// - 失效台可复测（Requeue）：票据/网络恢复即自动转回可用，恢复可切。
/// </summary>
public class ChannelPrecheck
{
    private readonly Func<Channel, Task<bool>> _probe;
    private readonly int _intervalMs;
    private readonly ConcurrentDictionary<string, ChannelHealth> _states = new();

    /// <summary>状态变化事件：(台 EPG 键, 新状态)。UI 层订阅后刷新列表。</summary>
    public event Action<string, ChannelHealth>? StateChanged;

    public ChannelPrecheck(Func<Channel, Task<bool>> probe, int intervalMs = 600)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _intervalMs = Math.Max(0, intervalMs);
    }

    /// <summary>查询某台当前状态（未探测=Unknown）。</summary>
    public ChannelHealth Of(Channel ch) =>
        _states.TryGetValue(ch.EpgKey, out var s) ? s : ChannelHealth.Unknown;

    /// <summary>直接标记状态（播放实测/预检覆盖写）。仅在状态真正变化时发事件，防重复刷列表。</summary>
    public void MarkState(Channel ch, ChannelHealth state)
    {
        var prev = Of(ch);
        if (prev == state) return;
        _states[ch.EpgKey] = state;
        StateChanged?.Invoke(ch.EpgKey, state);
    }

    /// <summary>失效台复测入口：置回探测中，探测轮次走完自动更新。</summary>
    public void Requeue(Channel ch) => MarkState(ch, ChannelHealth.Checking);

    /// <summary>
    /// 按台单顺序逐台探测，每台之间限速间隔。不阻塞调用方播放流程（后台 Task 执行）。
    /// 探测抛异常按失效处理——预检自己不吞播放在途状态，只记录结论。
    /// keepKnownState（循环预检轮次用，v1.0.8）：true 时已知状态的台不先置「检测中」，
    /// 复测期间原状态保持显示（可用台常绿、失效台留在无信号组），探测完才翻转——列表不闪烁。
    /// </summary>
    public async Task RunAsync(IEnumerable<Channel> channels, CancellationToken ct = default, bool keepKnownState = false)
    {
        foreach (var ch in channels)
        {
            if (ct.IsCancellationRequested) return;
            if (!keepKnownState || Of(ch) == ChannelHealth.Unknown)
                MarkState(ch, ChannelHealth.Checking);
            bool ok;
            try { ok = await _probe(ch).ConfigureAwait(false); }
            catch { ok = false; }
            MarkState(ch, ok ? ChannelHealth.Alive : ChannelHealth.Dead);
            if (_intervalMs > 0)
            {
                try { await Task.Delay(_intervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
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
