using System;
using System.Threading;
using System.Threading.Tasks;

using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 画面平稳优先令·后台网络全局单车道（老板 2026-11-14 00:32 令，长期）：
/// - 任何时候保画面平稳为主，其它后台机制（预检/预热/续票/备援校验）一律让路；
/// - 严禁多源同时探测/拉取：全部后台网络活动收敛到全局并发=1 的执行队列；
/// - 播放取流/换台不进车道（优先级最高，天然插队）；车道只管后台任务串行；
/// - 首帧未落地零后台网络：FirstFrameLanded=false 时后台任务一律等待（消费不超一份）；
/// - 有画面在播时后台单请求间隔 ≥10 秒（整轮 10 分钟级）；空闲（无画面）维持调用方既有节拍。
/// - 车道优先级（老板 01:16 令，vc53 任务12）：播放救助（Rescue）> 常规后台（Standard）> 慢探摸源（Scout）。
/// 适用范围（老板 2026-11-14 拍板）：仅电视端（TV）；手机端豁免，书面口径见
/// projects/1377423/审查/单车道令适用范围-电视端豁免-手机端.md。
/// 纯逻辑可测：时钟注入。唤醒后的等待用 Task.Delay 轮询（电视端 IO 定时器足量，避免复杂唤醒原语）。
/// </summary>
public static class BackgroundLane
{
    /// <summary>车道档位（老板 01:16 令）：
    /// Rescue=救急搜源（播放救助：主源/备援耗尽才触发，属播放链自愈——跳过首帧门控与播放间隔，
    /// 只守全局串行；否则播放起不来时自救反被自家车道锁死）；
    /// Standard=常规后台（预检/预热/续票/EPG，现行口径：首帧门控+间隔）；
    /// Scout=慢探摸源（仅待机闲时：播放中停摸=人在看零摸源，首帧门控+间隔同常规）。</summary>
    public enum LaneTier { Rescue = 0, Standard = 1, Scout = 2 }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>首帧门控（空闲默认放行）：开机常驻预检在设计上要先于任何播放跑起来（开机预检握手令），
    /// 故初值=放行（空闲档）；起播/换台/自救过渡期由会话状态机置 false（零后台网络），首帧落地置 true 恢复。
    /// volatile：读多写一。</summary>
    private static int _firstFrameLanded = 1;
    public static bool FirstFrameLanded => Volatile.Read(ref _firstFrameLanded) != 0;
    public static void SetFirstFrameLanded(bool v) => Volatile.Write(ref _firstFrameLanded, v ? 1 : 0);

    /// <summary>当前是否有画面在播（播放中=后台降速档；无=空闲档）。</summary>
    private static int _playing;
    public static bool Playing => Volatile.Read(ref _playing) != 0;
    public static void SetPlaying(bool v) => Volatile.Write(ref _playing, v ? 1 : 0);

    /// <summary>播放中后台单请求最小间隔（毫秒）：83 路 × 10s ≈ 14 分钟一轮，符合「整轮 10 分钟级」令。</summary>
    public const int PlayingGapMs = 10_000;

    /// <summary>播放中间隔覆盖（老板 2026-11-16 深夜拍板·主班裁量口径）：>0 时播放间隔用它，
    /// 列表抽屉开着压 5 秒（正盯绿点等变绿，快最有价值）、收着列表 0=回 10 秒护画面平稳；0/负值按默认计。</summary>
    public static int PlayingGapOverrideMs { get; set; } = 0;

    /// <summary>水位稳闸（老板 2026-11-17 放行口径定稿）：播放水位连续 15 秒 &gt;15 秒线才判稳；
    /// 不稳时后台不放行（偷拉主源时主源池同尺）。volatile 读多写一；初值 1=空闲/开机档放行。</summary>
    private static int _waterStable = 1;
    public static bool WaterStable => Volatile.Read(ref _waterStable) != 0;
    public static void SetWaterStable(bool v) => Volatile.Write(ref _waterStable, v ? 1 : 0);

    /// <summary>列表抽屉开着（老板放行口径边界③）：单开一档，不受水位稳全停约束，仍受首帧门控，间隔压 5 秒。</summary>
    private static int _drawerOpen;
    public static bool DrawerOpen => Volatile.Read(ref _drawerOpen) != 0;
    public static void SetDrawerOpen(bool v) => Volatile.Write(ref _drawerOpen, v ? 1 : 0);

    /// <summary>空闲档后台单请求最小间隔（毫秒）：至少给 600ms——防极端调用方零节拍打穿车道。</summary>
    public const int IdleGapMs = 600;

    /// <summary>空闲档间隔覆盖（测试口径）：默认=IdleGapMs；测试环境压小防车道硬等待拖慢全测。负值按 0 计。</summary>
    public static int IdleGapOverrideMs { get; set; } = IdleGapMs;

    private static long _lastRunMs; // Environment.TickCount64 口径
    private static readonly object GateLock = new();

    /// <summary>注入时钟（测试确定性）。生产=Environment.TickCount64。</summary>
    public static Func<long> Now { get; set; } = static () => Environment.TickCount64;

    /// <summary>探测当前还需等待的毫秒数（诊断/测试用）：0=可立即执行。</summary>
    public static int PendingWaitMs()
    {
        if (!FirstFrameLanded) return int.MaxValue; // 首帧未落地：无限等（直到 UI 置位）
        // 水位稳闸（老板放行口径 2026-11-17）：画面在播但水位未稳、抽屉也没开 → 后台全停（无限等）；
        // 开机/失败档无画面在播天然放行（Playing=false 不进此支）；抽屉开单开一档不受全停。
        if (Playing && !WaterStable && !DrawerOpen) return int.MaxValue;
        int gap = Playing
            ? (PlayingGapOverrideMs > 0 ? PlayingGapOverrideMs : PlayingGapMs)
            : Math.Max(0, IdleGapOverrideMs);
        long elapsed = Now() - Volatile.Read(ref _lastRunMs);
        return elapsed >= gap ? 0 : gap - (int)elapsed;
    }

    /// <summary>
    /// 后台网络任务过车道：首帧门控 → 间隔让路 → 全局串行执行（档位语义见 LaneTier）。
    /// 串行=同一时刻至多一个后台网络任务在飞（严禁多源同时探测/拉取的唯一执行点）。
    /// 播放请求绝不调用本方法（不排队、即发）。
    /// </summary>
    public static async Task RunAsync(Func<Task> work, CancellationToken ct = default, LaneTier tier = LaneTier.Standard)
    {
        await RunAsync<object?>(async () => { await work().ConfigureAwait(false); return null; }, ct, tier).ConfigureAwait(false);
    }

    /// <summary>带返回值的车道执行（探针 bool 等结果透传）：排队与限速逻辑与无返回版本完全一致。</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default, LaneTier tier = LaneTier.Standard)
    {
        await WaitTurnAsync(ct, tier).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _lastRunMs, Now());
            return await work().ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async Task WaitTurnAsync(CancellationToken ct, LaneTier tier)
    {
        if (tier == LaneTier.Rescue)
        {
            // 救助档：只守全局串行（播放链自愈不排队等门控/间隔——自救被自家车道锁死=自残）
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // 摸源档：播放中停摸（老板 01:16：慢探仅待机闲时，人在看=零摸源）
            if (tier == LaneTier.Scout && Playing)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            // 首帧门控：未落地时零后台网络（含排队中的也不放行）
            while (!FirstFrameLanded)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            // 水位稳闸（vc86 放行口径）：同 PendingWaitMs——画面在播水位未稳、抽屉没开，全停等待
            while (Playing && !WaterStable && !DrawerOpen)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            int wait = PendingWaitMs();
            if (wait > 0)
            {
                await Task.Delay(Math.Min(wait, 2_000), ct).ConfigureAwait(false); // 2 秒粒度轮询，让位不占核
                continue;
            }
            await Gate.WaitAsync(ct).ConfigureAwait(false); // 拿到执行权（先到先得）
            // 拿锁后复核（vc96 P2 过闸竞态）：排队期间间隔/门控可能已变，不过闸则让位重排
            if (PendingWaitMs() <= 0) return;
            Gate.Release();
        }
    }
}
