using System;
using TaoLive.BLL;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using LibVLCSharp.Shared;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 道映直播播放宿主（直播专用裁剪版，不接全量播放壳）：
/// - 低延迟直播参数（network-caching 1200ms 起，重连时递增 2500→4000→6000）；
/// - 卡流看门狗（v1.0.8）：直播流停止推进时 VLC 不报错，看门狗 2 秒巡检一次，
///   播放时钟 6 秒无推进即按断流重连（2/4/6 秒梯度），绝不让画面冻死；
/// - 换台先停旧播放再起新流（v1.0.8）：坏状态播放器不再带病起播；
/// - 代数守卫（v1.0.8）：换台/重连推进代数，旧重连定时器与事件回调全部作废，防旧台覆盖新台；
/// - 断流重连 3 次，间隔 2/4/6 秒；耗尽后只报一次「信号中断，请稍后重试或换台」；
/// - 无进度条/无录制/无磁力——直播用不到的一律不带；
/// - v1.0.7 起支持双载体：表面视图（稳定档）/纹理视图（v1.0.6 引入，带自检回退）。
/// </summary>
public interface IVideoCarrierView
{
    LibVLCSharp.Shared.MediaPlayer? MediaPlayer { get; set; }
}

/// <summary>重连前刷新出的新播放入口（票据过期换票；Url 为空表示沿用原地址重试）。</summary>
public class RefreshedStream
{
    public string? Url;
    public IReadOnlyList<(string Key, string Value)>? Options;
}

public class LiveVideoHost : IDisposable
{
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private IVideoCarrierView? _videoCarrier;
    private string? _currentUrl;
    /// <summary>A类第9项：水位低预防介入阈值（老板拍板 2.5s→4s，减少取票频率；判死线 6s 与巡检 2s 不动）。</summary>
    public const int EarlyRescueMs = 4000;

    private List<string> _currentOptions = new();
    private int _retriesLeft;
    private bool _userStopped;
    private bool _exhaustedNotified;   // 重试耗尽只报一次
    private bool _mutedForBackground;
    private int _savedVolume = 100;    // 退后台前音量（恢复用户调节，不回默认满音量）
    private int _volume = 100;         // fix34（移植）音量缓存：VLC 原生音量读写移出主线程（计算走缓存，赋值投线程池）

    private readonly GenerationCounter _genCounter = new();  // 代数守卫：旧定时器与回调作废依据
    private long _lastTickMs;                // 最近播放时钟（毫秒，Environment.TickCount64 基准）
    private Timer? _watchdog;                // 卡流看门狗：2 秒巡检
    private volatile bool _streamDead;       // 断流标志：报错/看门狗触发到重连成功之间为真（IsAlive 口径）
    private int _cachingIndex;               // 重连网络缓存档位（重连递增，软解跟不上自适应）
    private int _stallCount;                 // 本台会话内卡死次数（⑫分级自救的行进依据）
    private int _kernelRebuilding;           // 软重启互斥（⑫③级）；vc52 黄四 CAS 化：check-then-set 非原子，Timer 回调可并发重入
    private System.Threading.Timer? _statsRetryTimer; // ⑤首帧后轨道解析重试（每500ms至多10次）
    private int _statsRetries;                              // ⑤已重试次数
    public bool StatsExhausted => _statsRetries >= StatsLinePolicy.MaxRetries; // ⑤到限仍无数据显「--」
    private bool _earlyRescueUsed;           // ⑨B本轮卡顿已提前介入（水位低先动，不等6秒）
    private int _earlyRescueStreak;          // v23 第 5 条：⑨B静默重取流连续介入次数（连续满 EarlyRescueEscalateAt 升级自救阶梯）
    private long _lastEarlyRescueMs;         // v23 第 5 条：最近一次⑨B介入时刻（持续走钟 EarlyRescueResetMs 才清计数，假活不清零）
    private long _lastRescueTriggerMs;       // 1.0.12 修正①：最近一次自救触发时刻（Buffering 事件与看门狗 5 秒窗去重）
    private long _rescueWindowStartMs;       // 1.0.12 修正⑤：90 秒介入节流窗口起点
    private int _rescueWindowCount;          // 1.0.12 修正⑤：窗口内已介入次数（封顶 StallRescue.RescueCapPerWindow）
    private bool _rescueThrottleLogged;      // 1.0.12 修正⑤：节流期只记一次日志，防刷屏
    private int _refreshFailures;            // 1.0.12 发现 A：连续换票失败次数（第 2 次起换票委托切备援；成功即清零）
    private bool _inventoryRefetch;          // 1.0.12 发现 B：静默重接补库存档——首帧后已补过一次 3 秒库存
    private int _inventoryRunning;           // v25 修法五：补库存占位旗标（CAS 语义：仅补库存路径检查与置位；
                                             // 自救路径不检查不置位，gen 作废天然抢占——不对称互斥，方案修法五 1）
    private long _hotWindowStartMs;          // v25 修法七 2b：热备切换独立 90 秒窗起点（不占自救 5 秒去重/90 秒窗 2 次）
    private int _hotWindowCount;             // v25 修法七 2b：独立窗口内已切换次数（封顶 4，防主备乒乓）
    private float _bufferPct = 100f;          // ⑩播放器缓冲百分比（Buffering 事件回报，0–100）
    private BufferAction? _lastBufferAction;  // ⑩上一次水位动作（变化才落日志，防刷屏）
    private long _bufferLowFirstMs;           // vc38: 低水位毛刺确认（0=无未决低读；92条 playback P2）
    private readonly object _startLock = new();   // PL5: 起播临界区——热备/换台/重连写 _currentUrl+StartInternal 串行
    private long _disposed;                       // UR2: 宿主已释放（Dispose 后 StartInternal/Play 一律拒绝）
    private System.Threading.Timer? _firstFrameTimer;    // PL2: 首帧超时兜底定时器
    private int _firstFrameExtends;                      // vc37: 首帧查活续期轮次（起播重置，封顶 2）
    private const int FirstFrameTimeoutMs = 20_000;      // PL2: 首帧硬超时=问热备→自救阶梯；vc37 12s→20s（真机 11-12：慢网络起播被 12s 自救打断循环）

    // vc52 卡死根治（真机 11-13 复发实锤）：播放器原生操作全量串行队列——
    // 病根=fix32 起播投 Task.Run 后线程池不保序，连切几台时多个 Stop+Play 并发咬住同一播放器的
    // 原生锁，RestartKernel 又在他线程直接 Dispose，正在跑的操作踩已释放对象，原生层僵死；
    // UI 线程一碰载体挂载即整机无响应。修法=单读者先进先出队列承载全部原生 Stop/Play/Dispose/软重启，
    // 队列内代数守卫：过期操作整段跳过（晚到旧操作不再踩新台）；UI 线程零原生等待（Stop/Dispose 只排队）。
    private readonly Channel<Action> _startQueue = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _startQueuePumpRunning;   // 队列泵单飞护栏（CAS 起泵，全生命周期常驻）
    private int _startQueueDepth;         // 队列深度观测（防积压预警落日志，不干预行进）

    private void EnsureStartQueuePump()
    {
        if (Interlocked.CompareExchange(ref _startQueuePumpRunning, 1, 0) != 0) return;
        _ = Task.Run(PumpStartQueueAsync);
    }

    /// <summary>vc52：原生操作统一入队口——深度计数每笔必增（泵侧每笔必减，口径对齐），
    /// 积压 ≥8 笔落一次预警日志（连切多台时可见，不干预行进）；随后确保泵活并投递。
    /// 所有原生 Stop/Play/Dispose/软重启都必须经此入队，禁止绕过队列直碰播放器原生层。</summary>
    private void EnqueueNativeOp(Action op)
    {
        int depth = Interlocked.Increment(ref _startQueueDepth);
        if (depth >= 8)
        {
            try { CrashNet.Record($"[LiveVideoHost] 原生操作队列积压 {depth} 笔（连切多台，逐台消解中）"); } catch { }
        }
        EnsureStartQueuePump();
        _startQueue.Writer.TryWrite(op);
    }

    // vc52 红一：泵不可自愈修复——旧写法 while 直挂，WaitToReadAsync 一旦抛异常泵即永久死亡，
    // 而 _startQueuePumpRunning 停在 1，EnqueueNativeOp 的 CAS 起泵永远失败，队列只进不出全僵
    // （播放/停播/释放全积压，真机卡死同型）。整段 try/catch/finally：finally 无条件复位单飞护栏，
    // 复位后若仍有积压（复位与入队竞窗内新入队的操作）补一脚 EnsureStartQueuePump 消化
    private async Task PumpStartQueueAsync()
    {
        var reader = _startQueue.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var op))
                {
                    try { op(); }
                    catch (Exception ex)
                    {
                        try { CrashNet.Record("[LiveVideoHost] 起播队列操作异常（自救链兜底）：" + ex.Message); } catch { }
                    }
                    Interlocked.Decrement(ref _startQueueDepth);
                }
            }
        }
        catch (Exception ex)
        {
            try { CrashNet.Record("[LiveVideoHost] 起播队列泵异常退出（自愈重启）：" + ex.Message); } catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _startQueuePumpRunning, 0);
            if (Volatile.Read(ref _startQueueDepth) > 0) EnsureStartQueuePump();
        }
    }


    // vc39 ⑤深版网络自愈：门控+三分判别+本地代理内核（BLL 纯逻辑），宿主只做触发与播放链接线。
    // 进程级单例：内核端口监听全局一份；门控为全局态（系统解析坏是整机事实，不按台隔离）。
    private static readonly SelfHealGate HealGate = new();
    private static readonly SelfHealProxyKernel HealKernel = new();
    private static readonly ResolutionTriage HealTriage = new();
    private static int _healStarted;              // 内核 CAS 启动旗标（幂等）
    private static int _triageBusy;               // 判别互斥（失败风暴下不并发判别）
    /// <summary>⑩设备总内存（秒级水位上限自适应），由宿主创建方注入。</summary>
    public long TotalMemBytes { get; set; }

    /// <summary>1.0.12 发现 B：3 秒库存档位（默认「起播即 3 秒」拍板口径；真机验证静默重接保帧后可切补库存档）。</summary>
    public BufferInventory.Mode InventoryMode { get; set; } = BufferInventory.Default;

    // ⑫画面信息行（第④项）：原/现帧率与分辨率快照。分辨率在起播解析媒体轨道取得（原=现，无缩放）；
    // 现帧率按展示帧差实测，测不出如实给「检测中」。
    private double? _origFps; private int _origW, _origH;
    private long _fpsLastMs; private uint _fpsLastDisplayed; private double? _measuredFps;
    private Media? _currentMedia; // ④帧统计来源（Media.Statistics；换台/重连时换新释放旧）
    // fix35（移植 2026-11-13 修复批A）：观测缓存化——UI 线程零原生调用。
    // _kernelPlaying 由内核事件回写（原生事件回调线程），IsAlive 只读缓存；
    // _cachedDisplayed 由看门狗后台线程每 2 秒热点回写，SnapshotStats 只读缓存。
    // 两者均为 32 位原子量，跨线程直读直写（同 _lastTickMs 口径）。
    private int _kernelPlaying;
    private uint _cachedDisplayed;

    public event Action<string>? StatusChanged;

    /// <summary>PL4: UI 派发委托——软重建设播环境须回 UI 线程挂 View（审计 playback-loop-PL4），由宿主创建方注入。</summary>
    public Action<Action>? UiDispatcher { get; set; }

    /// <summary>画面统计快照（第④项）：原帧率/现帧率/原分辨率/现分辨率。测量缺失给 0/null，UI 显示「检测中」。</summary>
    public (double? OrigFps, double? CurFps, int OrigW, int OrigH, int CurW, int CurH) SnapshotStats()
    {
        // fix35（移植）：不再读 Media.Statistics（原生调用），只读看门狗回写的缓存
        var shown = _cachedDisplayed;
        if (shown > 0)
        {
            long now = Environment.TickCount64;
            if (_fpsLastDisplayed > 0 && now - _fpsLastMs > 800)
            {
                double fps = (shown - _fpsLastDisplayed) * 1000.0 / (now - _fpsLastMs);
                if (fps > 0 && fps < 240) _measuredFps = fps;
            }
            _fpsLastDisplayed = shown;
            _fpsLastMs = now;
        }
        return (_origFps, _measuredFps, _origW, _origH, _origW, _origH);
    }

    /// <summary>
    /// 重连前刷新入口（v1.0.8）：断流重连不再拿旧地址硬重试——直播源票据 5 分钟级过期，
    /// 旧 URL 重试一百次也是失败。UI 层挂入换票委托，重连前先取新鲜入口；取不到（源端
    /// 失效/冷却中）沿用原地址再试一次，重试次数照旧计。
    /// 1.0.12 发现 A：参数=连续换票失败次数（1 起）——第 1 次重连同源换票（普通卡顿秒级恢复）；
    /// 第 2 次起同源失败即走备援选择（健康表优先最近验证可用来源），备援成功记健康表，恢复自动回主源。
    /// ⑫/⑬联动：静默重取流走同一委托。
    /// </summary>
    public Func<int, Task<RefreshedStream?>>? ReconnectRefresher { get; set; }

    /// <summary>首帧事件：第一次收到播放时钟即认为已有画面输出（用于撤掉「正在连接」遮罩）。</summary>
    public event Action? FrameShown;

    private bool _frameShown;

    public MediaPlayer? Player => _player;

    /// <summary>
    /// 播放是否实际存活（v1.0.8 重进黑屏防护，判定走 PlaybackWatchdog.IsLive 专项断言）：
    /// - 未手动停止、未进入断流等待、播放器在播；
    /// - 首帧已出的还要求播放时钟近 5 秒内推进过——后台播放被系统杀掉或表面失效时，
    ///   IsPlaying 可能仍为 true 但时钟早已停走，必须按已死处理，回前台重播而不是永远黑屏。
    /// </summary>
    public bool IsAlive
    {
        get
        {
            if (_player is null || _userStopped || _streamDead) return false;
            if (Volatile.Read(ref _kernelPlaying) == 0) return false; // fix35（移植）：读缓存，零原生调用
            if (!_frameShown) return true; // 连接流程进行中，亮牌与重连由 Play 流程管理
            return PlaybackWatchdog.IsLive(_frameShown, Volatile.Read(ref _lastTickMs), Environment.TickCount64);
        }
    }

    public void Attach(IVideoCarrierView videoView)
    {
        _videoCarrier = videoView;
        Core.Initialize();
        // vc39 ⑤：自愈打点接崩溃日志通道（BLL 不反引 UI，委托注入）；代理内核进程内只起一次
        SelfHealProxyKernel.Reporter ??= msg => { try { CrashNet.Record(msg); } catch { } };
        if (Interlocked.CompareExchange(ref _healStarted, 1, 0) == 0) HealKernel.Start();
        // vc38（92条 playback P2）：载体重挂与软重启共用同一互斥——重挂撞上软重启进行中时
        // 不再并行双建内核（旧病：Attach 直进 BuildKernel 不占 _kernelRebuilding，
        // 与看门狗线程 RestartKernel 并发会造出两个 MediaPlayer 抢同一载体）；
        // 重启流程 BuildKernel 读 _videoCarrier，会把新内核挂到新载体上，此处让位即正确。
        // vc52 黄四：check-then-set 改 CAS 占位，重挂/软重启动并发窗口不再双入
        if (Interlocked.CompareExchange(ref _kernelRebuilding, 1, 0) != 0) return;
        try { BuildKernel(); }
        finally { Volatile.Write(ref _kernelRebuilding, 0); }
    }

    /// <summary>建播放内核（Attach 与⑫软重启共用）：LibVLC + 软解播放器 + 事件接线 + 看门狗。</summary>
    private void BuildKernel()
    {
        _libVlc = new LibVLC(enableDebugLogs: false);
        // 真机反馈：Amlogic t982 硬解黑屏，默认软解保画面（v1.0.2）
        _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = false };
        // fix35（移植）：新内核播放态从假开始，等 Playing 事件回写
        _kernelPlaying = 0;
        _cachedDisplayed = 0;
        // fix34（移植）：新播放器音量为默认满档，缓存对齐（新内核=new MediaPlayer，无旧音量残留）
        _volume = 100;
        // PL4: RestartKernel 可能由看门狗/自救线程调用，载体 View 挂播放器必须回 UI 线程
        // vc52 红五：UI 派发的挂载 lambda 异步回 UI 线程执行，期间宿主可能已 Dispose 或队列里又建了
        // 新内核——晚到挂载把已释放/过期播放器塞给载体（原生引用失效，真机闪退同型）。落卡双复查：
        // _disposed 复查（Dispose 后不挂）+ ReferenceEquals 复查（_player 已换代时不挂旧内核）
        var carrier = _videoCarrier;
        if (carrier != null)
        {
            var player = _player;
            if (UiDispatcher != null)
                UiDispatcher(() =>
                {
                    if (Interlocked.Read(ref _disposed) != 0) return;
                    if (!ReferenceEquals(_player, player)) return;
                    try { carrier.MediaPlayer = player; } catch { }
                });
            else carrier.MediaPlayer = player;
        }
        // fix35（移植）：内核播放态事件回写——IsAlive 判活改读缓存，UI 线程零原生调用
        _player.Playing += (_, _) => Volatile.Write(ref _kernelPlaying, 1);
        _player.Paused += (_, _) => Volatile.Write(ref _kernelPlaying, 0);
        _player.EncounteredError += (_, _) => OnStreamError();
        _player.Buffering += (_, e) =>
        {
            Volatile.Write(ref _bufferPct, e.Cache); // ⑩水位实测来源（0–100）
            // 1.0.12 修正①·主信号：缓冲接近耗尽（VLC 实时回报）即时触发提前介入，
            // 发现延迟从 2–6 秒压到 ≤1 秒；看门狗降级为兜底，双触发走 5 秒去重窗
            if (e.Cache <= StallRescue.BufferingLowPct) OnBufferingLow();
        };
        _player.Stopped += (_, _) => Volatile.Write(ref _kernelPlaying, 0); // fix35（移植）：停播即回写缓存（换台/重连的 Stop 有代数守卫）
        _player.TimeChanged += (_, _) =>
        {
            Volatile.Write(ref _lastTickMs, Environment.TickCount64);
            _earlyRescueUsed = false; // ⑨B时钟在走：本轮提前介入额度恢复
            Volatile.Write(ref _bufferLowFirstMs, 0); // vc38: 时钟在走=播放正常，未决低读毛刺确认一并清
            if (_frameShown) return;
            _frameShown = true;
            _earlyRescueStreak = 0; // v23 第 5 条：首帧清零（真首帧才算，微动不清）
            HealGate.OnPlayOk(); // vc39 ⑤：起播成功记账（清连续失败；门控过保持窗自动退出）
            // 1.0.12 发现 B·静默重接补库存档：首帧出画后静默重接一次，把库存补到 3 秒（先开画后补库存，
            // 换台首帧不慢）；默认「起播即 3 秒」档不需要这步
            // v25 修法五：补库存 CAS 占位——与 Buffering/看门狗自救并发时，自救不检查不置位（gen 作废天然抢占），
            // 补库存 CAS 失败=自救已抢跑，本次首帧批次一次性放弃不追补（不把待补标记带回）
            if (InventoryMode == BufferInventory.Mode.SilentRefetch && !_inventoryRefetch)
            {
                _inventoryRefetch = true;
                if (Interlocked.CompareExchange(ref _inventoryRunning, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await SilentRefreshAsync(announce: false); }
                        finally { Volatile.Write(ref _inventoryRunning, 0); }
                    });
                }
                // CAS 失败：补库存进行中（自救抢跑），首帧批次丢失不追补（修法五：一次性放弃）
            }
            StartStatsRetry(); // ⑤首帧后再解析轨道：起播后每500ms重试至多10次，后台不阻塞
            FrameShown?.Invoke();
        };
        if (_watchdog is null)
            _watchdog = new Timer(OnWatchdogTick, null, 2000, 2000);
    }

    /// <summary>⑫③软重启播放内核：重建设播环境（不动系统），同流继续。互斥防连环重启。
    /// vc52：原生停/释放段整段入队——与起播队列同一串行通道，彻底杜绝「他线程 Dispose 撞上正在跑的
    /// 队列操作踩已释放对象」的咬锁（真机 11-13 整机卡死实锤根因之二）。</summary>
    public void RestartKernel()
    {
        if (Volatile.Read(ref _kernelRebuilding) != 0 || _userStopped || _currentUrl is null) return;
        if (Interlocked.CompareExchange(ref _kernelRebuilding, 1, 0) != 0) return; // vc52 黄四：CAS 占位防并发重入
        long genNow = _genCounter.Current;
        EnqueueNativeOp(() =>
        {
            try
            {
                if (_userStopped || !_genCounter.IsCurrent(genNow)) return;
                _genCounter.NextGeneration(); // PL9: 软重启推进代数，作废旧定时器/旧回调，防旧代回调踩新内核
                try { _player?.Stop(); } catch { /* 已停不碍重建 */ }
                try { _currentMedia?.Dispose(); } catch { }
                _currentMedia = null;
                try { _player?.Dispose(); } catch { }
                try { _libVlc?.Dispose(); } catch { }
                _player = null; _libVlc = null;
                _frameShown = false;
                BuildKernel();
                StartInternal();
            }
            catch (Exception ex)
            {
                // vc52 红二：op 内 BuildKernel/StartInternal 抛异常时，旧写法只有 finally 复位互斥，
                // 无人接盘——内核已毁起播未成，画面黑死。落日志后交自救链（OnStreamError 内部自查
                // _userStopped，先问热备再走 2/4/6 秒阶梯与耗尽兜底，不与 finally 复位冲突）
                try { CrashNet.Record("[LiveVideoHost] 软重启失败（自救链接管）：" + ex.Message); } catch { }
                OnStreamError();
            }
            finally { Volatile.Write(ref _kernelRebuilding, 0); }
        });
    }

    private void OnWatchdogTick(object? state)
    {
        if (_userStopped || _currentUrl is null) return;
        MaybeRetestHealGate(); // vc39 ⑤：门控启用期每 5 分钟复测系统解析，恢复自动退出
        if (!_frameShown) return;                 // 还没出首帧：连接流程自己管理，不看门狗
        // fix35（移植）：帧统计缓存回写——后台巡检线程读原生统计，UI 层 SnapshotStats 只读缓存
        var statsMedia = _currentMedia;
        if (statsMedia != null)
        {
            try
            {
                var st = statsMedia.Statistics;
                if (st.DisplayedPictures > 0) _cachedDisplayed = (uint)st.DisplayedPictures;
            }
            catch { /* 释放竞态口径：统计读不碍播放 */ }
        }
        long last = Volatile.Read(ref _lastTickMs);
        long age = Environment.TickCount64 - last;
        if (!PlaybackWatchdog.IsStalled(_frameShown, last, Environment.TickCount64))
        {
            // v23 第 5 条：时钟恢复（持续走钟满 EarlyRescueResetMs）才清连续介入计数；
            // 时钟微动的「假活」不产生持续走钟窗口，计数保留，升级阶梯不会被绕过
            if (age < 1500 && _earlyRescueStreak > 0 &&
                Environment.TickCount64 - _lastEarlyRescueMs >= StallRescue.EarlyRescueResetMs)
                _earlyRescueStreak = 0; // 真恢复：连续介入计数清零
            // ⑨B缓冲水位低提前介入：时钟 4 秒没走就先静默重取流（先出画面口径），不等 6 秒卡死；
            // 每轮卡顿只提前介入一次，重置额度靠 TimeChanged 恢复；
            // 1.0.12 修正①⑤：Buffering 事件（主信号）与看门狗（兜底）统一走介入入口——5 秒去重 + 90 秒窗上限 2 次
            if (age >= EarlyRescueMs && !_earlyRescueUsed && Volatile.Read(ref _kernelRebuilding) == 0)
                TriggerEarlyRescue($"看门狗兜底：时钟 {age}ms 未走");
            ObserveBufferWatermark(age); // ⑩水位实接：默认贴边、抖动攒水位、平稳追回，动作变化落日志
            return;
        }
        Volatile.Write(ref _lastTickMs, Environment.TickCount64); // 防连发：触发一次后重新计时
        HandleStall();                            // ⑫分级快速自救：快过断流重连阶梯
    }

    /// <summary>1.0.12 修正①·Buffering 主信号触发点：缓冲接近耗尽即时介入（VLC 内核线程回调，守卫后走统一入口）。
    /// vc38 毛刺确认（92条 playback P2）：单次低读先记账不动手，确认窗（BufferingGlitchConfirmMs）内
    /// 第二次低读才算真低水位——码流尖峰抖动的单拍毛刺不再误触发静默重取流；时钟恢复即清确认。</summary>
    private void OnBufferingLow()
    {
        if (_userStopped || !_frameShown || Volatile.Read(ref _kernelRebuilding) != 0 || _streamDead) return;
        var now = Environment.TickCount64;
        var first = Volatile.Read(ref _bufferLowFirstMs);
        if (first == 0 || now - first > StallRescue.BufferingGlitchConfirmMs)
        {
            Volatile.Write(ref _bufferLowFirstMs, now);
            return;
        }
        Volatile.Write(ref _bufferLowFirstMs, 0);
        TriggerEarlyRescue("Buffering 缓冲空");
    }

    /// <summary>
    /// ⑨B 提前介入统一入口（1.0.12 修正①⑤）：Buffering 事件为主信号、看门狗为兜底，
    /// 同一次卡顿 5 秒去重窗内不重复计（阶梯不跳级）；90 秒窗口介入上限 2 次（限频代替拉黑，
    /// 探活与重连共用同一请求预算口径，稳定性审查#1：限频不挡自救行进）；
    /// 连续介入满 EarlyRescueEscalateAt 次仍升级分级自救阶梯。via=触发来源（打点用）。
    /// </summary>
    private void TriggerEarlyRescue(string via)
    {
        long now = Environment.TickCount64;
        if (now - _lastRescueTriggerMs < StallRescue.DedupWindowMs) return; // 修正①：同一卡顿双触发去重
        _lastRescueTriggerMs = now;
        if (now - _rescueWindowStartMs > StallRescue.RescueWindowMs)
        {
            _rescueWindowStartMs = now;
            _rescueWindowCount = 0;
            _rescueThrottleLogged = false;
        }
        if (_rescueWindowCount >= StallRescue.RescueCapPerWindow)
        {
            // v25 真机修复：节流只限「静默重取流」这类取流请求，不挡自救行进——降档与软重启内核
            // 不消耗取流预算；节流期内连续无效仍按阶梯升级，绝不冻死在「预算用完就没人管」
            if (!_rescueThrottleLogged)
            {
                _rescueThrottleLogged = true;
                try { CrashNet.Record($"[LiveVideoHost] ⑨B 介入节流：{StallRescue.RescueWindowMs / 1000} 秒内已达 {StallRescue.RescueCapPerWindow} 次上限，重取流歇口（节流不挡自救行进）"); } catch { }
            }
            _earlyRescueStreak++;
            _lastEarlyRescueMs = now;
            if (_earlyRescueStreak >= StallRescue.EarlyRescueEscalateAt)
            {
                _earlyRescueStreak = 0;
                try { CrashNet.Record($"[LiveVideoHost] ⑨B 节流期内连续 {StallRescue.EarlyRescueEscalateAt} 次介入无效，升级分级自救阶梯"); } catch { }
                HandleStall();
            }
            return;
        }
        _rescueWindowCount++;
        _earlyRescueUsed = true;
        _earlyRescueStreak++;
        _lastEarlyRescueMs = now;
        try { CrashNet.Record($"[LiveVideoHost] ⑨B 水位低提前介入（{via}，连续第 {_earlyRescueStreak} 次）：静默重取流"); } catch { }
        if (_earlyRescueStreak >= StallRescue.EarlyRescueEscalateAt)
        {
            _earlyRescueStreak = 0;
            try { CrashNet.Record($"[LiveVideoHost] ⑨B 连续 {StallRescue.EarlyRescueEscalateAt} 次介入无效，升级分级自救阶梯"); } catch { }
            HandleStall(); // 静默重取流→降清晰度→软重启内核，阶梯行进
            return;
        }
        _ = SilentRefreshAsync(announce: true);
    }

    /// <summary>⑤首帧后轨道解析重试：每 500ms 一次至多 10 次（StatsLinePolicy 口径），成功即停。</summary>
    private void StartStatsRetry()
    {
        _statsRetryTimer?.Dispose();
        _statsRetries = 0;
        _statsRetryTimer = new System.Threading.Timer(_ =>
        {
            if (_userStopped || _currentMedia is null) { _statsRetryTimer?.Dispose(); return; }
            _statsRetries++;
            if (_origW > 0 || _statsRetries >= StatsLinePolicy.MaxRetries)
            {
                _statsRetryTimer?.Dispose(); // 成功或到限：停止重试（到限显「--」）
                return;
            }
            ParseMediaStats(_currentMedia); // 后台线程解析，不阻塞播放
        }, null, StatsLinePolicy.IntervalMs, StatsLinePolicy.IntervalMs);
    }

    /// <summary>
    /// v25 修法七：热备帧级切换入口（UI 注入）。主路断/卡时先问热备——备路就绪即直接换流
    /// （I 帧边界对齐，画面跳 2–3 秒可接受），把 v24「断流→探测→重连」秒级黑屏压成帧级。
    /// 切换预算独立（2b）：不占 5 秒去重窗（不同于自救重接，备路是现成流零成本），
    /// 90 秒窗独立计数上限 4 次，防主备乒乓。打点=主路断到切换发起毫秒数（真机验证口径）。
    /// </summary>
    public Func<Task<(string Url, IReadOnlyList<(string Key, string Value)> Options)?>?>? HotStandbySwitch { get; set; }

    private async Task<bool> TryHotStandbyAsync(string via)
    {
        if (HotStandbySwitch is null || _userStopped) return false;
        long brokeAt = Environment.TickCount64;
        long now = brokeAt;
        if (now - _hotWindowStartMs > StallRescue.RescueWindowMs)
        {
            _hotWindowStartMs = now;
            _hotWindowCount = 0;
        }
        if (_hotWindowCount >= 4) return false; // 修法七 2b：独立窗 4 次，防乒乓
        long hotGen = _genCounter.Current;   // PL1: 备路等待期间换台/新一代起播, 结果作废
        var standby = await HotStandbySwitch();
        if (_userStopped || standby is null || !_genCounter.IsCurrent(hotGen)) return false;
        _hotWindowCount++;
        lock (_startLock)   // PL5: 热备写 _currentUrl+StartInternal 串行，防与换台/重连并发落账
        {
            if (_userStopped || !_genCounter.IsCurrent(hotGen)) return false;   // PL1: 落账前再守一次
            // vc52 黄六：Url=null 的元组不落账——直写 _currentUrl=null 会令 StartInternal 静默
            // return（_frameShown=false、看门狗在 _currentUrl is null 早退），画面定格无自救
            if (string.IsNullOrEmpty(standby.Value.Url)) return false;
            _currentUrl = standby.Value.Url;
            // 缺键才补：热备流只带 Referer/UA 两项，并入现有表保持既有选项（同 A 类 P1 口径）
            _currentOptions = MergeOptions(_currentOptions, standby.Value.Options);
            StartInternal(silent: true); // 定格保帧：同台自救不铺清场遮罩
        }
        try { CrashNet.Record($"[热备] {via}：帧级切备援发起（主路断到切换 {Environment.TickCount64 - brokeAt}ms，90 秒独立窗第 {_hotWindowCount}/4 次）"); } catch { }
        return true;
    }

    /// <summary>
    /// ⑫画面卡住分级快速自救（用户拍板「可以就这么定」）：静默重取流 → 软重启播放内核。
    /// （画质增强摘除后无档可降，二级并入静默重取流。）
    /// 静默级不发状态不占断流重连次数；重取流消费⑬健康表（ReconnectRefresher 内优先取最近验证可用来源）。
    /// v25 修法七：自救前先问热备——备路就绪即帧级切换，不走重票。
    /// </summary>
    private async void HandleStall()
    {
        if (Volatile.Read(ref _kernelRebuilding) != 0) return;
        try
        {
            if (await TryHotStandbyAsync("分级自救前主路断")) return; // v25 修法七：热备就绪帧级切换
            _stallCount++;
            var action = StallRescue.Plan(_stallCount - 1, hasLowerQuality: false); // 画质增强已摘除：恒无可降档
            switch (action)
            {
                case StallRescueAction.RestartKernel:
                    StatusChanged?.Invoke("画面卡住，正在重启播放内核…");
                    RestartKernel();
                    break;
                default:
                    _ = SilentRefreshAsync(announce: true);
                    break;
            }
        }
        catch (Exception ex) { try { CrashNet.Record($"[LiveVideoHost] 分级自救异常：{ex.Message}"); } catch { } }
    }

    /// <summary>
    /// ⑩播放器侧水位实接（BufferPolicy 口径）：以当前 network-caching 窗口 × 播放器缓冲百分比
    /// 估算秒级水位（内存环形，不落磁盘）；网络平稳且贴边=零延迟感，水位低/抖动=攒水位
    /// （重连缓存阶梯递增），平稳未满=平滑追回、缓降阶梯回贴边。动作只影响下一次重连的
    /// 缓冲池大小，不打断当前画面；变化才落日志（CrashNet 通道），界面不显示延迟。
    /// </summary>
    private void ObserveBufferWatermark(long tickAgeMs)
    {
        if (_userStopped || _currentUrl is null) return;
        var action = BufferPolicy.Observe(
            CacheLadder.At(_cachingIndex) / 1000.0 * (Volatile.Read(ref _bufferPct) / 100.0),
            networkSteady: tickAgeMs < 1500, // 近 1.5 秒内时钟走过=网络平稳
            TotalMemBytes);
        if (_lastBufferAction == action) { return; }
        _lastBufferAction = action;
        switch (action)
        {
            case BufferAction.BuildUp: // 水位低/抖动：下一次重连缓冲池加大（阶梯封顶）
                var next = CacheLadder.Next(_cachingIndex);
                if (next != _cachingIndex)
                {
                    _cachingIndex = next;
                    CrashNet.Record($"[BufferPolicy] 水位低攒缓冲：重连缓存目标 → {CacheLadder.At(_cachingIndex)}ms");
                }
                break;
            case BufferAction.HoldEdge:
                // 1.0.12 修正②：水位只涨不降——取消平稳贴边自动降档（固定 3 秒库存不被水位自适应自己降回去）；
                // HoldEdge 仅记账（_lastBufferAction 在方法头已更新），不再动 _cachingIndex
                break;
        }
    }

    /// <summary>
    /// 静默重取流：换新鲜入口立即快速重接，不发状态、不动断流重连计数。
    /// 1.0.12 定格口径：同台自救不铺清场遮罩——载体视图不销毁，VLC 停后 surface 天然保帧
    /// （定格最后一帧，真机验证项）；announce=true 时亮「正在换信号」角标（信息条），补库存重接不声张。
    /// </summary>
    private async Task SilentRefreshAsync(bool announce = false)
    {
        long gen = _genCounter.NextGeneration(); // 新一代：旧重连/旧自救路径全部作废
        if (announce) StatusChanged?.Invoke("正在换信号，请稍候…"); // 1.0.12 定格+角标：不黑屏不亮全屏牌
        RefreshedStream? fresh = null;
        if (ReconnectRefresher != null)
            fresh = await RefreshStreamAsync("静默重取流");
        // vc52 黄二：写账与起播收 _startLock（与热备同口径）——无锁直写时热备路径可并发落账，
        // 产生「A 路径 URL + B 路径合并请求头」交叉错配（CDN 拒绝，白耗重连阶梯与切换预算）
        lock (_startLock)
        {
            if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 期间换台：作废
            if (fresh?.Url != null && fresh.Url != _currentUrl)
            {
                _currentUrl = fresh.Url;
                // A 类 P1·缺键才补：静默重取给出的选项并入现有表（同键以新值为准，未给的键沿用旧表），
                // 整表覆盖会丢掉 Play 时注入的 Referer/UA 请求头——第二实例被 CDN 拒的实锤根因
                if (fresh.Options != null)
                    _currentOptions = MergeOptions(_currentOptions, fresh.Options);
            }
            StartInternal(silent: true);
        }
    }

    /// <summary>
    /// 1.0.12 发现 A·换票统一走此入口：按连续失败次数分级——第 1 次同源换票（普通卡顿秒级恢复），
    /// 第 2 次起委托收到 attempt≥2 即走备援选择（健康表优先）；成功清零、失败累加（异常同败）。
    /// </summary>
    private async Task<RefreshedStream?> RefreshStreamAsync(string via)
    {
        if (ReconnectRefresher == null) return null;
        int attempt = Volatile.Read(ref _refreshFailures) + 1;
        try
        {
            var fresh = await ReconnectRefresher(attempt);
            // v25 真机修复：原地址「假成功」不清账——同 URL 返回等于没换票，不清连续失败计数，
            // 否则 attempt 永远停在同级、备援选择永远轮不到（升级行进只认真换了地址）
            if (fresh?.Url != null && fresh.Url != _currentUrl) Volatile.Write(ref _refreshFailures, 0);
            else Interlocked.Increment(ref _refreshFailures);
            return fresh;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _refreshFailures);
            CrashNet.Record($"[LiveVideoHost] {via}换票失败（{FailureKind(ex)}）：{ex.Message}（沿用原地址重接）");
            return null;
        }
    }

    /// <summary>
    /// 起播一路直播流（URL 已由 StreamResolver 签好；透传头等为选项列表传入）。
    /// 换台先停旧播放（v1.0.8）：坏状态播放器不带病起播，代数推进作废旧回调。
    /// </summary>
    public void Play(string url, IReadOnlyList<(string Key, string Value)> options)
    {
        if (Interlocked.Read(ref _disposed) != 0) return;   // UR2
        _currentUrl = url;
        _currentOptions = options.Select(o => $":{o.Key}={o.Value}").ToList();
        _retriesLeft = 3;
        _userStopped = false;
        _exhaustedNotified = false;
        _cachingIndex = 0;
        _stallCount = 0;      // ⑫：换台自救行进清零，分级从静默重取流重新走
        _earlyRescueStreak = 0; // v23 第 5 条：换台清零连续介入计数
        _lastEarlyRescueMs = 0;
        _lastRescueTriggerMs = 0;   // 1.0.12 修正①：去重窗随换台清零
        _rescueWindowCount = 0;     // 1.0.12 修正⑤：介入节流窗口随换台重开（新台新预算）
        _rescueThrottleLogged = false;
        _refreshFailures = 0;       // 1.0.12 发现 A：连续换票失败计数随换台清零
        StopExhaustedRecovery();    // v25 真机修复：换台=新台新预算，兜底循环收摊（防旧台兜底叠进新台）
        _inventoryRefetch = false;  // 1.0.12 发现 B：补库存标记随换台重置
        _measuredFps = null;  // ④：换台重新实测
        _fpsLastDisplayed = 0;
        _fpsLastMs = 0;
        _cachedDisplayed = 0; // fix35（移植）：换台清帧统计缓存，与新内核对齐
        _origFps = null; _origW = 0; _origH = 0;
        _genCounter.NextGeneration(); // 新一代：旧重连定时器全作废
        StartInternal();
    }

    /// <summary>音量步进（保留接口）：遥控音量走独立硬件键由系统处理，此接口暂不接线。
    /// fix34（移植）：不再主线程直读直写 _player.Volume（VLC 原生锁）——缓存上计算、原生赋值投线程池，
    /// 签名不变；极小概率与内核侧赋值交错时以后写结果为准，仅影响瞬时音量无碍业务。</summary>
    public int AdjustVolume(int delta)
    {
        var vol = Math.Clamp(_volume + delta, 0, 100);
        _volume = vol;
        var target = _player;
        if (target is { } p) Task.Run(() => { try { p.Volume = vol; } catch { } });
        return vol;
    }

    private void StartInternal(bool silent = false)
    {
        if (Interlocked.Read(ref _disposed) != 0) return;   // UR2
        if (_player is null || _currentUrl is null) return;
        long gen = _genCounter.NextGeneration(); // 本次起播代数（Stop 事件/旧定时器作废）
        _frameShown = false; // 每次起播/重连都要重新等首帧，遮罩才撤得对
        _streamDead = false; // 起播即视为存活，等首帧与时钟验证
        _firstFrameExtends = 0; // vc37 查活续期轮次随起播重置
        Volatile.Write(ref _lastTickMs, Environment.TickCount64);
        if (!silent) StatusChanged?.Invoke("连接中…");
        // vc52 串行队列版（fix32 主刀迭代）：原生 Stop/媒体构建/Play 整段入队——单读者先进先出，
        // 连切几台也严格按「先停旧台再起新台」逐台执行，多操作再无并发咬锁；队列内代数守卫，
        // 过期操作整段跳过（晚到旧操作不踩新台）；UI 线程零原生等待
        var player = _player;
        var oldMedia = _currentMedia;
        var cachingIdx = _cachingIndex;
        var inventoryFull = (_inventoryRefetch || InventoryMode == BufferInventory.Mode.StartFull);
        long genAtEnqueue = gen;
        EnqueueNativeOp(() =>
        {
            // vc52 收口：入队到执行之间宿主可能已释放/停播（FIFO 下本 op 先于释放 op 执行），
            // 队列内复查不放行脏起播——_disposed 不推进代数，代数守卫拦不住它
            if (Interlocked.Read(ref _disposed) != 0 || _userStopped) return;
            if (!_genCounter.IsCurrent(genAtEnqueue)) return; // 过期操作整段跳过：换台/重连已推进代数
            try
            {
                player?.Stop(); // 换台/重连先停旧播放；Stop 触发的回调由代数守卫作废
                try { oldMedia?.Dispose(); } catch { } // ④旧媒体统计源释放
                // vc39 ⑤：门控启用且代理存活 → 播本地代理 URL（127.0.0.1/?u=原URL），代理按存活通道（DoH 择优）回源；
                // _currentUrl 保持原 URL（换票比较/换台逻辑不受影响），只在起播这一层换地址
                string playUrl = _currentUrl!;
                if (HealGate.IsEngaged && HealKernel.IsAlive && !HlsRewriter.TryUnwrap(playUrl, out _))
                {
                    playUrl = HlsRewriter.Wrap(playUrl);
                    try { CrashNet.Record($"[LiveVideoHost] 网络自愈门控启用中：本次起播走本地代理（原源 {MaskUrl(_currentUrl)}）"); } catch { }
                }
                var media = new Media(_libVlc!, new Uri(playUrl));
                _currentMedia = media; // ④保留引用：Media.Statistics 提供帧统计
                // 直播低延迟：缓存阶梯（首连 1200ms，重连 2500→4000→6000 自适应软解跟不上的流）；
                // 1.0.12 发现 B：3 秒库存两档——默认「起播即 3 秒」（库存档兜底，修正②：重连缓存递增口径并入固定库存档，
                // 不被水位自适应降回去）；补库存档先开画（修正④：换台口径=先开画后补库存），首帧后静默重接补到 3 秒
                int ladder = CacheLadder.At(cachingIdx);
                int caching = inventoryFull
                    ? Math.Max(BufferInventory.TargetMs, ladder)
                    : ladder;
                media.AddOption($":network-caching={caching}");
                media.AddOption($":live-caching={Math.Max(1200, caching)}");
                foreach (var opt in _currentOptions) media.AddOption(opt);
                // fix32（修复批A移植）：轨道解析整段投后台——media.Parse 是阻塞原生重活，慢一拍整机卡一拍
                var statsTarget = media;
                _ = Task.Run(() => ParseMediaStats(statsTarget));
                player?.Play(media);
            }
            catch (Exception ex)
            {
                try { CrashNet.Record("[LiveVideoHost] 队列起播异常（自救链兜底）：" + ex.Message); } catch { }
                OnStreamError();
            }
        });
        StartFirstFrameTimer();   // PL2: 首帧超时兜底
    }

    /// <summary>PL2: 首帧超时兜底——起播 12s 未出首帧即按断流处理（先热备后自救阶梯）, 防画面永挂黑屏（审计 playback-loop-PL2）。</summary>
    private void StartFirstFrameTimer()
    {
        _firstFrameTimer?.Dispose();
        _firstFrameTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_userStopped || _frameShown) return;
                // vc37 查活续期：流时钟近 1.5 秒内走过 = 流活着，首帧判定未触发不重播——
                // 自救打断正在缓冲的起播是第二刀首连/切台卡顿放大器（真机 11-12）；续期最多 2 轮，超限才走自救
                if (_firstFrameExtends < 2 && Environment.TickCount64 - Volatile.Read(ref _lastTickMs) < 1500)
                {
                    _firstFrameExtends++;
                    try { CrashNet.Record($"[LiveVideoHost] 首帧超时但流时钟在走（查活通过），续期等首帧（第 {_firstFrameExtends}/2 轮）"); } catch { }
                    StartFirstFrameTimer();
                    return;
                }
                try { CrashNet.Record($"[LiveVideoHost] 起播 {FirstFrameTimeoutMs / 1000}s 未出首帧, 按断流自救"); } catch { }
                long genAtFrameTimeout = _genCounter.Current; // vc52 黄三：同口径快照代数，晚到不进阶梯
                _ = Task.Run(async () =>
                {
                    if (await TryHotStandbyAsync("首帧超时")) return;
                    OnStreamErrorFallthrough(genAtFrameTimeout);   // 计自救阶梯：2/4/6 秒换票重接
                });
            }
            catch { }
        }, null, FirstFrameTimeoutMs, Timeout.Infinite);
    }

    /// <summary>解析媒体视频轨道（第④项）：原帧率/原分辨率；无缩放滤镜时现=原，测量缺失显示「检测中」。</summary>
    private void ParseMediaStats(Media media)
    {
        try
        {
            media.Parse(MediaParseOptions.ParseLocal);
            foreach (var t in media.Tracks)
            {
                if (t.TrackType != TrackType.Video) continue;
                var vt = t.Data.Video; // MediaTrackData.Video（VideoTrack 结构体：Width/Height/FrameRateNum/Den 为 uint）
                if (vt.Width > 0)
                {
                    _origW = (int)vt.Width; _origH = (int)vt.Height;
                    if (vt.FrameRateDen > 0) _origFps = vt.FrameRateNum / (double)vt.FrameRateDen;
                }
                break;
            }
        }
        catch (Exception ex) { CrashNet.Record($"[LiveVideoHost] 媒体轨道解析失败（url={MaskUrl(_currentUrl)}）：{ex.Message}"); }
    }

    private void OnStreamError()
    {
        if (_userStopped) return;
        // vc52 黄三：旧内核 EncounteredError 晚到会污染新会话——原生事件线程排队，换台/软重启推进代数后
        // 晚到的旧事件直通自救阶梯，消耗新会话 _retriesLeft 并误显「信号中断」。入口快照代数：
        // 晚到（代数已不当前）不问热备不进阶梯；入口内自查 _userStopped 口径不变
        long genAtError = _genCounter.Current;
        _streamDead = true; // 断流等待期按已死处理（回前台须重播，不能拿死流当活）
        // v25 修法七：断流先问热备——备路就绪即帧级切换（不等 2/4/6 秒重连），失败继续原阶梯
        _ = Task.Run(async () =>
        {
            if (!_genCounter.IsCurrent(genAtError)) return; // 晚到旧代事件整段作废
            if (await TryHotStandbyAsync("VLC 断流")) return;
            OnStreamErrorFallthrough(genAtError);
        });
    }

    private void OnStreamErrorFallthrough(long? genAtError = null)
    {
        if (_userStopped) return;
        // vc52 黄三：断流事件在问热备/等待期推进代数（换台）→ 阶梯入口再守一次，不消耗新会话重试预算
        if (genAtError is { } g && !_genCounter.IsCurrent(g)) return;
        NotePlayFailure("断流"); // vc39 ⑤：起播失败记账（≥3 连败+系统解析坏证据链成立才启用门控）
        if (_retriesLeft <= 0)
        {
            if (_exhaustedNotified) return;
            _exhaustedNotified = true;
            StatusChanged?.Invoke("信号中断，正在持续自动重试，也可先换台");
            StartExhaustedRecovery(); // v25 真机修复：耗尽不躺平——常驻兜底按节奏静默重试直到恢复
            return;
        }
        var delay = (3 - _retriesLeft + 1) * 2; // 2/4/6 秒
        _retriesLeft--;
        _cachingIndex = CacheLadder.Next(_cachingIndex); // 重连缓存递增
        StatusChanged?.Invoke($"信号中断，{delay} 秒后重连（剩 {_retriesLeft + 1} 次）…");
        long gen = _genCounter.Current;
        var timer = new System.Timers.Timer(delay * 1000) { AutoReset = false };
        timer.Elapsed += async (_, _) =>
        {
            timer.Dispose();
            if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 代数守卫：换台后旧重连作废
            // 重连前重新取票（v1.0.8）：过期票据重试必败，先取新鲜入口再起播；
            // 1.0.12 发现 A：走统一换票入口按连续失败分级——第 1 次同源、第 2 次起委托切备援
            var fresh = await RefreshStreamAsync("重连");
            // vc52 黄二：写账与起播收 _startLock（与热备同口径），防 URL/请求头交叉错配
            lock (_startLock)
            {
                if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 换票期间换台
                if (fresh?.Url != null && fresh.Url != _currentUrl)
                {
                    _currentUrl = fresh.Url;
                    // A 类 P1·缺键才补：同上，重连换票绝不整表覆盖丢请求头
                    if (fresh.Options != null)
                        _currentOptions = MergeOptions(_currentOptions, fresh.Options);
                }
                StartInternal();
            }
        };
        timer.Start();
    }

    private System.Threading.Timer? _exhaustedRecoveryTimer; // v25 真机修复：耗尽后常驻兜底
    private int _exhaustedCycle;                             // 兜底重试轮次（每 N 轮升一次软重启内核）
    private int _recoveryBusy;                               // 兜底重试互斥（上轮未完不叠发）

    /// <summary>
    /// vc39 ⑤起播失败记账：达到阈值（连续 3 次）时做三分判别——系统解析坏才启用门控并切代理重播；
    /// 路由死/出网断只落日志如实告示（不可自愈，不自欺）。判别互斥：失败风暴下不并发。
    /// </summary>
    private void NotePlayFailure(string via)
    {
        try
        {
            if (!HealGate.OnPlayFailed()) return;
            if (Interlocked.CompareExchange(ref _triageBusy, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    // ③热修 A3·门控困死退出联动：门控启用期代理失活 → 退场回直连重播。
                    // 否则播放失败判别 verdict 仍 SystemDnsBroken，ReportTriage 已启用分支不重启代理不退出 → 永困死代理
                    if (HealGate.IsEngaged && !HealKernel.IsAlive)
                    {
                        HealGate.Disengage("网络自愈门控退场：代理失活，回归直连播放链");
                        try { CrashNet.Record($"[LiveVideoHost] {HealGate.LastVerdict}"); } catch { }
                        RestartKernel();
                        return;
                    }
                    if (_currentUrl is null) return;
                    var uri = new Uri(_currentUrl);
                    int port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
                    var verdict = await HealTriage.TriageAsync(uri.Host, port: port);
                    // 接线位 C（老板 2026-11-12 批准）：判别日志注明纯 IP 源免疫口径——纯 IP 直连源（源档案 IPDirect）
                    // 不走系统解析，解析僵死场景天然可用；备援排序/选源策略侧优先纯 IP 源因子（方案稿「解析坏优先纯 IP 源因子」条）
                    try { CrashNet.Record($"[LiveVideoHost] 网络自愈三分判别（{via} 连败 {SelfHealGate.FailThreshold} 起）：{verdict.Detail}（纯 IP 直连源对解析僵死免疫，备援优先）"); } catch { }
                    if (HealGate.ReportTriage(verdict) && HealKernel.IsAlive)
                    {
                        try { CrashNet.Record("[LiveVideoHost] 网络自愈门控启用：播放链切本地代理，按存活通道（DoH 择优）回源"); } catch { }
                        RestartKernel(); // 立即按代理链重播当前台（不等 2/4/6 秒阶梯走完）
                    }
                }
                catch (Exception ex) { try { CrashNet.Record($"[LiveVideoHost] 网络自愈判别异常：{ex.Message}"); } catch { } }
                finally { Volatile.Write(ref _triageBusy, 0); }
            });
        }
        catch { } // 记账路径绝不干扰主自救阶梯
    }

    /// <summary>vc39 ⑤门控启用期复测：每 RetestMs 由看门狗驱动一次判别，系统恢复（过保持窗）自动退出门控。</summary>
    private void MaybeRetestHealGate()
    {
        if (!HealGate.ShouldRetest()) return;
        if (Interlocked.CompareExchange(ref _triageBusy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // ③热修 A3·门控困死退出联动（复测路径同守）：代理失活不判别直接退场，回直连重播
                if (HealGate.IsEngaged && !HealKernel.IsAlive)
                {
                    HealGate.Disengage("网络自愈门控退场：代理失活，回归直连播放链");
                    try { CrashNet.Record($"[LiveVideoHost] {HealGate.LastVerdict}"); } catch { }
                    RestartKernel();
                    return;
                }
                if (_currentUrl is null) return;
                var uri = new Uri(_currentUrl);
                int port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
                var verdict = await HealTriage.TriageAsync(uri.Host, port: port);
                try { CrashNet.Record($"[LiveVideoHost] 网络自愈复测：{verdict.Detail}"); } catch { }
                bool wasEngaged = HealGate.IsEngaged;
                HealGate.ReportTriage(verdict);
                if (wasEngaged && !HealGate.IsEngaged)
                {
                    try { CrashNet.Record("[LiveVideoHost] 网络自愈门控退出：系统解析恢复，回归直连播放链"); } catch { }
                    RestartKernel(); // 回直连重播（代理只在解析坏期间兜底）
                }
            }
            catch (Exception ex) { try { CrashNet.Record($"[LiveVideoHost] 网络自愈复测异常：{ex.Message}"); } catch { } }
            finally { Volatile.Write(ref _triageBusy, 0); }
        });
    }

    /// <summary>
    /// v25 真机修复·耗尽兜底：断流重连阶梯耗尽后原口径永久躺平（首帧已丢、看门狗失明、
    /// 画面冻死到用户手动切台）。改为常驻慢循环：首次 30 秒后、之后每 60 秒静默重试——
    /// 重武装快速阶梯（2/4/6 秒换票）并重取流重接；每 3 轮升级一次软重启内核。
    /// 首帧恢复或用户换台即收摊。
    /// </summary>
    private void StartExhaustedRecovery()
    {
        if (_exhaustedRecoveryTimer != null) return; // 常驻：已在跑不重复起
        _exhaustedCycle = 0;
        _exhaustedRecoveryTimer = new System.Threading.Timer(_ =>
        {
            if (_userStopped || _frameShown) { StopExhaustedRecovery(); return; }
            if (Interlocked.Exchange(ref _recoveryBusy, 1) == 1) return;
            try
            {
                _exhaustedCycle++;
                bool rebuild = _exhaustedCycle % StallRescue.ExhaustedRebuildEveryCycles == 0;
                try { CrashNet.Record($"[LiveVideoHost] 耗尽兜底第 {_exhaustedCycle} 轮：{(rebuild ? "软重启内核" : "重取流重接")}（{StallRescue.ExhaustedRecoveryPeriodMs / 1000} 秒节奏）"); } catch { }
                _retriesLeft = 3;       // 重武装快速阶梯：本轮再失败仍走 2/4/6 秒换票
                _exhaustedNotified = false;
                if (rebuild) RestartKernel();
                else _ = ExhaustedRefreshAsync();
            }
            finally { Volatile.Write(ref _recoveryBusy, 0); }
        }, null, StallRescue.ExhaustedRecoveryFirstDelayMs, StallRescue.ExhaustedRecoveryPeriodMs);
    }

    private void StopExhaustedRecovery()
    {
        _exhaustedRecoveryTimer?.Dispose();
        _exhaustedRecoveryTimer = null;
    }

    private async Task ExhaustedRefreshAsync()
    {
        long gen = _genCounter.Current; // 期间换台：作废（Play 流程自己接管）
        var fresh = await RefreshStreamAsync("耗尽兜底");
        // vc52 黄二：写账与起播收 _startLock（与热备同口径），防 URL/请求头交叉错配
        lock (_startLock)
        {
            if (_userStopped || _frameShown || !_genCounter.IsCurrent(gen)) return;
            if (fresh?.Url != null && fresh.Url != _currentUrl)
            {
                _currentUrl = fresh.Url;
                if (fresh.Options != null)
                    _currentOptions = MergeOptions(_currentOptions, fresh.Options); // 缺键才补，同 A 类 P1 口径
            }
            StartInternal(silent: true);
        }
    }

    public void Stop()
    {
        _frameShown = false;
        _streamDead = true;
        _userStopped = true;
        _genCounter.NextGeneration(); // 作废未决重连
        StopExhaustedRecovery(); // v25 真机修复：手动停止即收摊
        _firstFrameTimer?.Dispose(); _firstFrameTimer = null;    // PL2
        // vc52：原生停播入队——UI 线程（OnPause）零原生等待，停播与起播/软重启同队列保序
        var player = _player;
        if (player is { } p)
        {
            EnqueueNativeOp(() => { try { p.Stop(); } catch { /* 已停/已释放不碍 */ } });
        }
    }

    /// <summary>退后台：画面照常走（纹理载体不回收），声音先闭掉（记住原音量）。
    /// fix34（移植）：静音/恢复也走缓存+线程池原生赋值，与 AdjustVolume 同口径，缓存不跑偏。</summary>
    public void MuteForBackground()
    {
        if (_player is null || _mutedForBackground) return;
        _mutedForBackground = true;
        _savedVolume = _volume;
        _volume = 0;
        var muted = _player;
        Task.Run(() => { try { if (muted is { } p) p.Volume = 0; } catch { } });
    }

    /// <summary>回前台：声音原样恢复（退前用户调到多少回多少，不回默认满音量）。</summary>
    public void UnmuteAfterBackground()
    {
        if (_player is null || !_mutedForBackground) return;
        _mutedForBackground = false;
        var restore = _savedVolume;
        _volume = restore;
        var unmuted = _player;
        Task.Run(() => { try { if (unmuted is { } p) p.Volume = restore; } catch { } });
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);   // UR2: 释放后拒绝一切起播路径
        _watchdog?.Dispose();
        _watchdog = null;
        _firstFrameTimer?.Dispose(); _firstFrameTimer = null;     // PL2
        StopExhaustedRecovery(); // v25 真机修复：宿主释放随行收摊
        _statsRetryTimer?.Dispose(); // ⑤重试定时器随宿主释放
        _statsRetryTimer = null;

        _userStopped = true;
        _genCounter.NextGeneration(); // vc52 收口：推进代数作废一切未决起播 op（Dispose 不推进代数会漏拦）
        var media = _currentMedia; _currentMedia = null;
        var player = _player; _player = null;
        var libVlc = _libVlc; _libVlc = null;
        _videoCarrier = null;
        // vc52：原生释放整段入队——与停/起同队列保序，杜绝「释放撞上正在跑的队列操作」；
        // 释放顺序=先 Stop 静默播放器再逐层 Dispose（vc52 收口：原顺序 media 先于 Stop，
        // 释放正在播放的 Media 踩原生引用窗口，与闪退同型）；退出路径有界等待（2 秒上限）
        // 对齐 fix51 口径，不把待释放对象带进程即死
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueNativeOp(() =>
        {
            try { player?.Stop(); } catch { }
            try { media?.Dispose(); } catch { }
            try { player?.Dispose(); } catch { }
            try { libVlc?.Dispose(); } catch { }
            released.TrySetResult();
        });
        try { released.Task.Wait(2000); } catch { }
    }

    /// <summary>【十二】失败归因（ConnectFailure 分类口径）：逐类落日志，不笼统「连接失败」。</summary>
    internal static string FailureKind(Exception ex) => ConnectFailure.Classify(ex.ToString()).ToString();

    /// <summary>A 类 P1·选项合并（缺键才补）：实现收敛在 BLL（MediaOptionMerge），宿主层薄委托。</summary>
    internal static List<string> MergeOptions(IReadOnlyList<string> existing, IReadOnlyList<(string Key, string Value)> fresh)
        => MediaOptionMerge.Merge(existing, fresh);

    /// <summary>【八】脱敏源地址：只留协议+主机，路径与查询串不落日志。</summary>
    private static string MaskUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "-";
        try { var u = new Uri(url); return $"{u.Scheme}://{u.Host}"; }
        catch { return url.Length > 24 ? url[..24] + "…" : url; }
    }
}
