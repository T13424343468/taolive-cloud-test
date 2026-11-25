using System.Runtime.CompilerServices;

namespace TaoLive.Session;

/// <summary>
/// 画面平稳观测（方案书 v76/v20）：帧回调（IPlayer.OnFrameTick，内核 10Hz 节流）进滑窗，
/// 出帧间隔 P95 与卡顿疑似判定归此一件，壳层只埋点不掺计算。
/// - 窗口 10 秒滑窗；窗内间隔 ≤ 连续出帧上限（3 秒，换台/断流间隙由 Buffering 事件管，不折算卡顿）；
/// - P95 阈值 250ms（v20 定标方法，终值按制度从严口径定标锁定，总监终裁）；
/// - 连续 3 窗超阈=疑似卡顿（真机验收看壳层 [画面平稳] 日志行）。
/// 单调钟账：窗口推进按注入时钟，墙钟只用于打点时间戳。
/// </summary>
public sealed class FramePulse
{
    /// <summary>连续出帧上限：帧间隔超过此值视为非连续出帧（断流/换台间隙），不纳入 P95 也不计超阈。</summary>
    public static readonly TimeSpan ContinuousGapLimit = TimeSpan.FromSeconds(3);
    /// <summary>滑窗宽：评估样本只取最近一窗。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    /// <summary>帧间隔 P95 判卡阈值（毫秒）。</summary>
    public const int P95ThresholdMs = 250;
    /// <summary>连续超阈窗数判疑似卡顿（v20：连续 3 窗判卡顿）。</summary>
    public const int StallWindows = 3;

    private readonly Func<DateTimeOffset> _now;
    private readonly Queue<DateTimeOffset> _ticks = new();
    private int _stalledWindowRun;

    public FramePulse(Func<DateTimeOffset>? clock = null) => _now = clock ?? (static () => DateTimeOffset.UtcNow);

    /// <summary>记录一拍帧回调（渲染线程 10Hz 节流节拍）。非线程安全：单渲染线程顺序调用。</summary>
    public void Record(DateTimeOffset atUtc)
    {
        _ticks.Enqueue(atUtc);
        Trim();
    }

    /// <summary>
    /// 最近一窗帧间隔 P95（毫秒）。窗内有效间隔不足 2 拍（无法算间隔）返回 null；
    /// 间隙（间隔 &gt; ContinuousGapLimit）不纳入样本。落日志由壳层负责。
    /// </summary>
    public int? WindowP95Ms()
    {
        Trim();
        var gaps = new List<int>();
        DateTimeOffset? prev = null;
        foreach (var t in _ticks)
        {
            if (prev is { } p)
            {
                var gap = t - p;
                if (gap <= ContinuousGapLimit) gaps.Add((int)gap.TotalMilliseconds);
            }
            prev = t;
        }
        if (gaps.Count < 2) return null;
        gaps.Sort();
        return gaps[(int)Math.Ceiling(gaps.Count * 0.95) - 1];
    }

    /// <summary>
    /// 疑似卡顿：连续 <see cref="StallWindows"/> 个已计窗 P95 超阈。窗界按固定节拍（窗口宽）推进，
    /// 每次取数时把已滚过的窗各自评估。零取数不评估（壳层按 10 秒节拍取）。
    /// </summary>
    public bool StallSuspicion
    {
        get
        {
            EvaluateWindows();
            return _stalledWindowRun >= StallWindows;
        }
    }

    private DateTimeOffset _windowCursor = DateTimeOffset.MinValue;

    /// <summary>把 cursor 到当前的每个满窗逐一评估，维持连续超阈游程。</summary>
    private void EvaluateWindows()
    {
        var now = _now();
        if (_windowCursor == DateTimeOffset.MinValue) { _windowCursor = now; return; }
        while (_windowCursor + Window <= now)
        {
            _windowCursor += Window;
            var p95 = WindowP95MsAt(_windowCursor);
            _stalledWindowRun = p95 is > P95ThresholdMs ? _stalledWindowRun + 1 : 0;
        }
    }

    /// <summary>指定窗界（窗尾）内有效间隔 P95；样本不足按正常窗计（不添超阈游程）。</summary>
    private int? WindowP95MsAt(DateTimeOffset windowEnd)
    {
        var gaps = new List<int>();
        DateTimeOffset? prev = null;
        foreach (var t in _ticks)
        {
            if (t > windowEnd - Window && t <= windowEnd)
            {
                if (prev is { } p && t - p <= ContinuousGapLimit) gaps.Add((int)(t - p).TotalMilliseconds);
                prev = t;
            }
            else if (t > windowEnd) break;
            else prev = t;
        }
        if (gaps.Count < 2) return null;
        gaps.Sort();
        return gaps[(int)Math.Ceiling(gaps.Count * 0.95) - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Trim()
    {
        var horizon = _now() - Window;
        while (_ticks.Count > 0 && _ticks.Peek() < horizon) _ticks.Dequeue();
    }
}
