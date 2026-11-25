using System;

using TaoLive.Foundation;

namespace TaoLive.Session;

/// <summary>
/// OK 重试退避账（方案书收口批③）：指数退避管重试节奏，断路器管换源，共账不共判据。
/// Failed 态手动重试（壳层 OK 键）经 TryAcquire 放行——第 n 次重试须距上次失败
/// 退避 AdvancedTuning.RetryBaseMs × 2^(n-1)，封顶 RetryMaxMs（全局调参，写活不写死）；
/// 播放成功起播（Playing）即清账重计。计时一律按单调钟（方案书 v12 铁律）。
/// </summary>
public sealed class RetryBackoffAccount
{
    private readonly object _gate = new();
    private readonly Func<long> _monotonicMs;
    private readonly Dictionary<string, (int Attempts, long LastMs)> _ledger = new(StringComparer.Ordinal);

    public RetryBackoffAccount(Func<long>? monotonicMs = null) =>
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);

    /// <summary>第 n 次重试前须退避的毫秒数：Base×2^(n-1) 封顶 Max（n 从 1 计；n=0 首试不退避）。</summary>
    public long DelayMsFor(int attempts)
    {
        long baseMs = Math.Max(1, AdvancedTuning.RetryBaseMs);
        long maxMs = Math.Max(baseMs, AdvancedTuning.RetryMaxMs);
        long delay = baseMs;
        for (int i = 1; i < attempts; i++)
        {
            delay = Math.Min(delay * 2, maxMs); // 逐次翻倍并封顶，免溢出
        }
        return delay;
    }

    /// <summary>
    /// 请求放行一次重试：退避窗未满则拒绝并带出还须等待的毫秒数；
    /// 放行即记账（次数 +1，记单调时间）。channelId 为空串按独立账目处理（不跨台互扰）。
    /// </summary>
    public bool TryAcquire(string channelId, out long retryAfterMs)
    {
        lock (_gate)
        {
            var now = _monotonicMs();
            var (attempts, lastMs) = _ledger.TryGetValue(channelId, out var entry)
                ? entry : (0, 0L);
            long delay = DelayMsFor(attempts);
            long elapsed = now - lastMs;
            if (attempts > 0 && elapsed < delay)
            {
                retryAfterMs = delay - elapsed;
                return false;
            }
            _ledger[channelId] = (attempts + 1, now);
            retryAfterMs = 0;
            return true;
        }
    }

    /// <summary>播放成功起播（Playing）即清账：下次失败重试从起步退避重新计。</summary>
    public void Clear(string channelId)
    {
        lock (_gate) _ledger.Remove(channelId);
    }
}
