using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.BLL;

/// <summary>
/// 画面平稳优先令·后台网络全局单车道（老板 2026-11-14 00:32 令，长期）：
/// - 任何时候保画面平稳为主，其它后台机制（预检/预热/续票/备援校验）一律让路；
/// - 严禁多源同时探测/拉取：全部后台网络活动收敛到全局并发=1 的执行队列；
/// - 播放取流/换台不进车道（优先级最高，天然插队）；车道只管后台任务串行；
/// - 首帧未落地零后台网络：FirstFrameLanded=false 时后台任务一律等待（消费不超一份）；
/// - 有画面在播时后台单请求间隔 ≥10 秒（整轮 10 分钟级）；空闲（无画面）维持调用方既有节拍。
/// 纯逻辑可测：时钟注入。唤醒后的等待用 Task.Delay 轮询（电视端 IO 定时器足量，避免复杂唤醒原语）。
/// </summary>
public static class BackgroundLane
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>首帧是否落地（UI 在首帧事件置 true；换台/断流起播时置 false）。volatile：读多写一。</summary>
    private static int _firstFrameLanded;
    public static bool FirstFrameLanded => Volatile.Read(ref _firstFrameLanded) != 0;
    public static void SetFirstFrameLanded(bool v) => Volatile.Write(ref _firstFrameLanded, v ? 1 : 0);

    /// <summary>当前是否有画面在播（播放中=后台降速档；无=空闲档）。</summary>
    private static int _playing;
    public static bool Playing => Volatile.Read(ref _playing) != 0;
    public static void SetPlaying(bool v) => Volatile.Write(ref _playing, v ? 1 : 0);

    /// <summary>播放中后台单请求最小间隔（毫秒）：83 路 × 10s ≈ 14 分钟一轮，符合「整轮 10 分钟级」令。</summary>
    public const int PlayingGapMs = 10_000;

    /// <summary>空闲档后台单请求最小间隔（毫秒）：至少给 600ms——防极端调用方零节拍打穿车道。</summary>
    public const int IdleGapMs = 600;

    private static long _lastRunMs; // Environment.TickCount64 口径
    private static readonly object GateLock = new();

    /// <summary>注入时钟（测试确定性）。生产=Environment.TickCount64。</summary>
    public static Func<long> Now { get; set; } = static () => Environment.TickCount64;

    /// <summary>探测当前还需等待的毫秒数（诊断/测试用）：0=可立即执行。</summary>
    public static int PendingWaitMs()
    {
        if (!FirstFrameLanded) return int.MaxValue; // 首帧未落地：无限等（直到 UI 置位）
        int gap = Playing ? PlayingGapMs : IdleGapMs;
        long elapsed = Now() - Volatile.Read(ref _lastRunMs);
        return elapsed >= gap ? 0 : gap - (int)elapsed;
    }

    /// <summary>
    /// 后台网络任务过车道：首帧门控 → 间隔让路 → 全局串行执行。
    /// 串行=同一时刻至多一个后台网络任务在飞（严禁多源同时探测/拉取的唯一执行点）。
    /// 播放请求绝不调用本方法（不排队、即发）。
    /// </summary>
    public static async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        await WaitTurnAsync(ct).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _lastRunMs, Now());
            await work().ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async Task WaitTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // 首帧门控：未落地时零后台网络（含排队中的也不放行）
            while (!FirstFrameLanded)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            int wait = PendingWaitMs();
            if (wait <= 0) break;
            await Task.Delay(Math.Min(wait, 2_000), ct).ConfigureAwait(false); // 2 秒粒度轮询，让位不占核
        }
        await Gate.WaitAsync(ct).ConfigureAwait(false); // 拿到执行权（先到先得）
    }
}
