using TaoLive.Session;

namespace TaoLive.Tests;

/// <summary>测试固定时钟：不真等，快拍断言；Advance 快进看门狗窗口。NowMs 随 Advance 同步推进（单调账）。</summary>
public sealed class FixedClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    private long _ms;

    public DateTimeOffset UtcNow => _now;

    /// <summary>单调毫秒读数：自构造起累计，Advance 一并快进。</summary>
    public long NowMs => _ms;

    /// <summary>快进指定时长（仅推进测试时钟，不真实等待）。</summary>
    public void Advance(TimeSpan delta)
    {
        _now += delta;
        _ms += (long)delta.TotalMilliseconds;
    }
}
