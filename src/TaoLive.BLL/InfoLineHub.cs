using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

/// <summary>信息行消息优先级（总装协同口径·高到低）：连接状态 &gt; IP 限流提示 &gt; 常规。</summary>
public enum InfoLinePriority
{
    Normal = 0,     // 常规频道名等
    RateLimit = 1,  // IP 限流提示
    Connect = 2,    // 连接状态（正在连接/重试）
}

/// <summary>
/// A 类第2项·信息行单写入口（总装协同口径一）：
/// 所有信息行写入必须过同一道闸——同文重刷直接吞（防重刷总闸）；低优先级不得打断
/// 高优先级正在显示的内容（让位，退队后到期再上）；过期或同高优先级可顶替。
/// 纯逻辑可测：时间由调用方注入，本类不碰任何 UI。
/// </summary>
public class InfoLineHub
{
    /// <summary>各优先级展示保持期：到期即让位（低优先级排队内容或后续常规可上）。</summary>
    public static readonly IReadOnlyDictionary<InfoLinePriority, long> HoldMs = new Dictionary<InfoLinePriority, long>
    {
        [InfoLinePriority.Connect] = 6_000,
        [InfoLinePriority.RateLimit] = 8_000,
        [InfoLinePriority.Normal] = 0, // 常规内容不占闸，随到随显
    };

    private InfoLinePriority _curPrio = InfoLinePriority.Normal;
    private string _curText = "";
    private long _shownAtMs;
    private string? _parked;                      // 让位期间暂存的最高优先级后来者（同级后来覆盖先前）
    private InfoLinePriority _parkedPrio = InfoLinePriority.Normal;

    /// <summary>当前是否正展示某条内容（文本对账用）。</summary>
    public (string Text, InfoLinePriority Priority) Current => (_curText, _curPrio);

    /// <summary>提交一条消息：返回应渲染的文本；null=被吞（同文重刷或让位）。nowMs=TickCount64 基准。</summary>
    public string? Submit(string text, InfoLinePriority prio, long nowMs)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // 总闸：同文重刷一律吞（不论优先级），界面绝不闪
        if (text == _curText) return null;
        bool currentHolds = HoldMs[_curPrio] > 0 && nowMs - _shownAtMs < HoldMs[_curPrio];
        if (currentHolds && prio < _curPrio)
        {
            // 让位：低优先级不顶高优先级，只记最新一条，等当前到期再上
            if (_parked is null || prio >= _parkedPrio) { _parked = text; _parkedPrio = prio; }
            return null;
        }
        _curText = text; _curPrio = prio; _shownAtMs = nowMs;
        return text;
    }

    /// <summary>轮询：当前展示已到期且有让位排队内容时放行（UI 可在刷新节拍顺带调用；也可只靠下一次 Submit）。</summary>
    public string? Poll(long nowMs)
    {
        if (_parked is null) return null;
        bool currentHolds = HoldMs[_curPrio] > 0 && nowMs - _shownAtMs < HoldMs[_curPrio];
        if (currentHolds) return null;
        var text = _parked;
        _curText = text; _curPrio = _parkedPrio; _shownAtMs = nowMs;
        _parked = null;
        return text;
    }

    /// <summary>换台等场景清闸：正在展示的内容立即作废，排队内容一并丢弃（调用方随后自显新内容）。</summary>
    public void Reset()
    {
        _curText = ""; _curPrio = InfoLinePriority.Normal; _shownAtMs = 0;
        _parked = null;
    }

    /// <summary>按文案归类优先级（单写入口的 UI 侧便捷判定；连接/重连 &gt; 限流 &gt; 常规）。</summary>
    public static InfoLinePriority PriorityOf(string text)
    {
        if (text is null) return InfoLinePriority.Normal;
        if (text.Contains("连接中") || text.Contains("重连") || text.Contains("正在自动换")) return InfoLinePriority.Connect;
        if (text.Contains("限流")) return InfoLinePriority.RateLimit;
        return InfoLinePriority.Normal;
    }
}
