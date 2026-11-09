using System;
using TaoLive.BLL;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LibVLCSharp.Shared;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 道映直播播放宿主（不复用 TaoPlay 全量播放壳，直播专用裁剪版）：
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
    private List<string> _currentOptions = new();
    private int _retriesLeft;
    private bool _userStopped;
    private bool _exhaustedNotified;   // 重试耗尽只报一次
    private bool _mutedForBackground;
    private int _savedVolume = 100;    // 退后台前音量（恢复用户调节，不回默认满音量）

    private readonly GenerationCounter _genCounter = new();  // 代数守卫：旧定时器与回调作废依据
    private long _lastTickMs;                // 最近播放时钟（毫秒，Environment.TickCount64 基准）
    private Timer? _watchdog;                // 卡流看门狗：2 秒巡检
    private volatile bool _streamDead;       // 断流标志：报错/看门狗触发到重连成功之间为真（IsAlive 口径）
    private int _cachingIndex;               // 重连网络缓存档位（重连递增，软解跟不上自适应）
    private int _stallCount;                 // 本台会话内卡死次数（⑫分级自救的行进依据）
    private bool _kernelRebuilding;          // 软重启互斥（⑫③级）
    private System.Threading.Timer? _statsRetryTimer; // ⑤首帧后轨道解析重试（每500ms至多10次）
    private int _statsRetries;                              // ⑤已重试次数
    public bool StatsExhausted => _statsRetries >= StatsLinePolicy.MaxRetries; // ⑤到限仍无数据显「--」
    private bool _earlyRescueUsed;           // ⑨B本轮卡顿已提前介入（水位低先动，不等6秒）
    private float _bufferPct = 100f;          // ⑩播放器缓冲百分比（Buffering 事件回报，0–100）
    private BufferAction? _lastBufferAction;  // ⑩上一次水位动作（变化才落日志，防刷屏）
    private long _lastDeescalateMs;           // ⑩水位追回：降档节流（30 秒最多降一档）

    // fix29 首帧等待兜底：起播后迟迟不出首帧时看门狗与静默自救互相让位的盲区，这里兜底收口
    private System.Threading.Timer? _firstFrameTimer; // 首帧等待兜底计时器（每次起播重新武装，代数守卫作废旧表）
    private int _firstFrameFails;                     // 连续无首帧次数（首帧落地/换台清零；阶梯行进依据）

    // fix29 防假活：时钟在走但画面帧数持续不涨（坏分片循环/伪流），看门狗补第二道防线
    private int _lastDecodedCount;                    // 上次看门狗巡检读到的画面帧数
    private int _decodedStallTicks;                   // 帧数停滞累计周期（满阈值按卡流处理）

    // fix30 退出/卡死双保险（老板 2026-11-08 真机反馈「卡死之后退出不了，只能关机重启」）：
    // VLC 原生 Stop/Play/Dispose 一旦卡住，宿主调用线程跟着死——此前换台/退后台/热切都在
    // 主线程直接调原生，内核卡住=按键全无反应=只能关机。内核线程把全部原生操作收口串行，
    // 主线程只投递即返回，任何时候按键、换台、退出都不再被播放内核卡死。
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _kernelQueue = new();
    private readonly Thread _kernelThread;
    private readonly TaoLive.BLL.RescueBreaker _rescueBreaker = new(); // fix30 自救风暴断路器（防连环自救打满 CPU 拖死整机）

    public LiveVideoHost()
    {
        _kernelThread = new Thread(KernelLoop) { IsBackground = true, Name = "taolive-kernel" };
        _kernelThread.Start();
    }

    /// <summary>fix30：投递内核操作（无界队列不阻塞调用线程；串行执行天然防并发锁竞争）。</summary>
    private void EnqueueKernel(Action act) => _kernelQueue.Add(act);

    private void KernelLoop()
    {
        foreach (var act in _kernelQueue.GetConsumingEnumerable())
        {
            try { act(); }
            catch (Exception ex) { try { CrashNet.Record("[Kernel] 内核操作异常（内核线程内收口）：" + ex.Message); } catch { } }
        }
    }

    // ---- 双实例无感切换（v1.0.10【六】【九】A）：新流亮了再换，全程不黑不闪 ----
    private LibVLC? _swapLibVlc;              // 第二实例播放环境
    private MediaPlayer? _swapPlayer;         // 第二实例播放器（连新档流）
    private Media? _swapMedia;                // 第二实例媒体
    private IVideoCarrierView? _swapCarrier;  // 第二实例载体（UI 建好已在视图树，暂不可见）
    private bool _swapFrameShown;             // 第二实例首帧是否到齐
    private EventHandler<System.EventArgs>? _swapOnError;                       // 第二实例临时挂接（提交时摘除）
    private EventHandler<MediaPlayerTimeChangedEventArgs>? _swapOnTime;

    /// <summary>⑩设备总内存（秒级水位上限自适应），由宿主创建方注入。</summary>
    public long TotalMemBytes { get; set; }

    // fix31 长窗兜底断路（冻结稿 v2）：观察期/收口期由上层拉起——停自动兜底与自动重连，
    // 保持当前画面，按键全可用；观察期满或换台后由上层放行，看门狗下一拍自动恢复
    private volatile bool _rescuePaused;

    /// <summary>true=暂停宿主内自动兜底（看门狗自救/静默重取流/断流重连全部静默，保持当前画面）。</summary>
    public bool RescuePaused { get => _rescuePaused; set => _rescuePaused = value; }

    /// <summary>⑩最近一次缓冲水位快照（资源观测日志用；格式「缓存目标ms/缓冲%」，无数据为 null）。</summary>
    public string? LastBufferWatermark { get; private set; }

    // ⑫画面信息行（第④项）：原/现帧率与分辨率快照。分辨率在起播解析媒体轨道取得（原=现，无缩放）；
    // 现帧率按展示帧差实测，测不出如实给「检测中」。
    private double? _origFps; private int _origW, _origH;
    private long _fpsLastMs; private uint _fpsLastDisplayed; private double? _measuredFps;
    private Media? _currentMedia; // ④帧统计来源（Media.Statistics；换台/重连时换新释放旧）

    public event Action<string>? StatusChanged;

    /// <summary>画面统计快照（第④项）：原帧率/现帧率/原分辨率/现分辨率。测量缺失给 0/null，UI 显示「检测中」。</summary>
    public (double? OrigFps, double? CurFps, int OrigW, int OrigH, int CurW, int CurH) SnapshotStats()
    {
        var media = _currentMedia;
        if (media != null)
        {
            var st = media.Statistics; // MediaStats 结构体：DisplayedPictures 字段
            if (st.DisplayedPictures > 0)
            {
                long now = Environment.TickCount64;
                if (_fpsLastDisplayed > 0 && now - _fpsLastMs > 800)
                {
                    double fps = (st.DisplayedPictures - _fpsLastDisplayed) * 1000.0 / (now - _fpsLastMs);
                    if (fps > 0 && fps < 240) _measuredFps = fps;
                }
                _fpsLastDisplayed = (uint)st.DisplayedPictures;
                _fpsLastMs = now;
            }
        }
        return (_origFps, _measuredFps, _origW, _origH, _origW, _origH);
    }

    /// <summary>
    /// 重连前刷新入口（v1.0.8）：断流重连不再拿旧地址硬重试——直播源票据 5 分钟级过期，
    /// 旧 URL 重试一百次也是失败。UI 层挂入换票委托，重连前先取新鲜入口；取不到（源端
    /// 失效/冷却中）沿用原地址再试一次，重试次数照旧计。
    /// ⑫/⑬联动：静默重取流走同一委托（消费来源级健康表优先取最近验证可用来源）。
    /// </summary>
    public Func<Task<RefreshedStream?>>? ReconnectRefresher { get; set; }

    /// <summary>⑫②级降清晰度：是否还有可降档（UI 按 AI 增强档位判断）；无档可降时自救直接并入重取流。</summary>
    public Func<bool>? HasLowerQuality { get; set; }

    /// <summary>⑫②级降清晰度请求：UI 收到后降一档 AI 增强（软解负载减轻），随后静默重接。</summary>
    public event Action? LowerQualityRequested;

    /// <summary>首帧事件：第一次收到播放时钟即认为已有画面输出（用于撤掉「正在连接」遮罩）。</summary>
    public event Action? FrameShown;

    /// <summary>fix29 首帧兜底：自救阶梯走完仍无画面（UI 收到即判死本台、放开探活自动跳台收口）。</summary>
    public event Action? FirstFrameExhausted;

    /// <summary>双实例无感切换：第二实例新流首帧到齐（UI 收到即提交；提交前旧画面原样保留，绝不黑屏）。</summary>
    public event Action? SwapReady;

    /// <summary>双实例无感切换：第二实例起流失败/播放错误（UI 收到即放弃切换，留当前档）。</summary>
    public event Action? SwapFailed;

    private bool _frameShown;

    /// <summary>双实例切换是否有第二实例在连（未决期间自救让路，不与切换叠加）。</summary>
    public bool IsSwapPending => _swapPlayer != null;

    /// <summary>当前播放流地址（档位切换沿用同一流地址、只换画质参数时用）。</summary>
    public string? CurrentUrl => _currentUrl;

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
            if (!_player.IsPlaying) return false;
            if (!_frameShown) return true; // 连接流程进行中，亮牌与重连由 Play 流程管理
            return PlaybackWatchdog.IsLive(_frameShown, Volatile.Read(ref _lastTickMs), Environment.TickCount64);
        }
    }

    public void Attach(IVideoCarrierView videoView)
    {
        _videoCarrier = videoView;
        Core.Initialize();
        BuildKernel();
    }

    /// <summary>建播放内核（Attach 与⑫软重启共用）：LibVLC + 软解播放器 + 事件接线 + 看门狗。</summary>
    private void BuildKernel()
    {
        _libVlc = new LibVLC(enableDebugLogs: false);
        // 真机反馈：Amlogic t982 硬解黑屏，默认软解保画面（v1.0.2）
        _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = false };
        _videoCarrier!.MediaPlayer = _player;
        WireKernelEvents(_player);
        if (_watchdog is null)
            _watchdog = new Timer(OnWatchdogTick, null, 2000, 2000);
    }

    /// <summary>主实例事件接线（BuildKernel 与双实例提交共用：提交后第二实例立即接管全部宿主职责）。</summary>
    private void WireKernelEvents(MediaPlayer player)
    {
        player.EncounteredError += (_, _) => OnStreamError();
        player.Buffering += (_, e) => Volatile.Write(ref _bufferPct, e.Cache); // ⑩水位实测来源（0–100）
        player.Stopped += (_, _) => { /* 换台/重连的 Stop 有代数守卫，此处无需动作 */ };
        player.TimeChanged += (_, _) =>
        {
            Volatile.Write(ref _lastTickMs, Environment.TickCount64);
            _earlyRescueUsed = false; // ⑨B时钟在走：本轮提前介入额度恢复
            if (_frameShown) return;
            _frameShown = true;
            _firstFrameFails = 0; // fix29：首帧落地，兜底计数清零（新台新算）
            StartStatsRetry(); // ⑤首帧后再解析轨道：起播后每500ms重试至多10次，后台不阻塞
            FrameShown?.Invoke();
        };
    }

    /// <summary>⑫③软重启播放内核：重建设播环境（不动系统），同流继续。互斥防连环重启。</summary>
    public void RestartKernel()
    {
        if (_kernelRebuilding || _userStopped || _currentUrl is null) return;
        if (IsSwapPending) return; // 双实例切换未决：切换本身就是更好的自救，不叠加重启
        _kernelRebuilding = true;
        // fix30：旧内核原生释放+重建整段投递内核线程——与起播/停播同队列串行，
        // 绝不让调用线程（看门狗/主线程）等 VLC 原生锁
        var player = _player;
        var libVlc = _libVlc;
        _player = null; _libVlc = null; // 下次起播由内核线程重建后回填
        _frameShown = false;
        EnqueueKernel(() =>
        {
            try
            {
                try { player?.Stop(); } catch { /* 已停不碍重建 */ }
                try { _currentMedia?.Dispose(); } catch { }
                _currentMedia = null;
                try { player?.Dispose(); } catch { }
                try { libVlc?.Dispose(); } catch { }
                if (_userStopped) return; // fix30：排队期间用户已停播/退出，重建作废
                BuildKernel();
                StartInternalCore(silent: true);
            }
            finally { _kernelRebuilding = false; }
        });
    }

    /// <summary>fix30：内核线程内的直接起播（RestartKernel 重建后同队列接续，不再二次投递）。</summary>
    private void StartInternalCore(bool silent)
    {
        StartInternal(silent);
    }

    private void OnWatchdogTick(object? state)
    {
        if (_userStopped || _currentUrl is null) return;
        if (_rescuePaused) return; // fix31 观察期/收口期：停自动兜底，保持当前画面（期满上层放行后看门狗下一拍自动恢复）
        if (!_frameShown) return;                 // 还没出首帧：连接流程自己管理，不看门狗
        long last = Volatile.Read(ref _lastTickMs);
        long age = Environment.TickCount64 - last;
        if (!PlaybackWatchdog.IsStalled(_frameShown, last, Environment.TickCount64))
        {
            // fix29 防假活：时钟在走但画面帧数持续不涨——坏分片循环/伪流等假活场景，看门狗补第二道防线
            if (_currentMedia is { } media)
            {
                var st = media.Statistics;
                if (st.DisplayedPictures > 0)
                {
                    if (PlaybackWatchdog.IsFakeLive((int)st.DisplayedPictures, _lastDecodedCount, _decodedStallTicks))
                    {
                        _decodedStallTicks = 0;
                        Volatile.Write(ref _lastTickMs, Environment.TickCount64); // 防连发：触发一次后重新计时
                        try { CrashNet.Record("[LiveVideoHost] fix29 防假活：时钟在走但画面帧 6 秒未涨，按卡流自救"); } catch { }
                        HandleStall();
                        return;
                    }
                    if ((int)st.DisplayedPictures == _lastDecodedCount) _decodedStallTicks++;
                    else _decodedStallTicks = 0;
                    _lastDecodedCount = (int)st.DisplayedPictures;
                }
            }
            // ⑨B缓冲水位低提前介入：时钟 2.5 秒没走就先静默重取流（先出画面口径），不等 6 秒卡死；
            // 每轮卡顿只提前介入一次，重置额度靠 TimeChanged 恢复
            if (age >= 2500 && !_earlyRescueUsed && !_kernelRebuilding && !IsSwapPending)
            {
                _earlyRescueUsed = true;
                try { CrashNet.Record($"[LiveVideoHost] ⑨B 水位低提前介入：时钟 {age}ms 未走，静默重取流"); } catch { }
                _ = SilentRefreshAsync();
            }
            ObserveBufferWatermark(age); // ⑩水位实接：默认贴边、抖动攒水位、平稳追回，动作变化落日志
            return;
        }
        Volatile.Write(ref _lastTickMs, Environment.TickCount64); // 防连发：触发一次后重新计时
        HandleStall();                            // ⑫分级快速自救：快过断流重连阶梯
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
    /// ⑫画面卡住分级快速自救（用户拍板「可以就这么定」）：静默重取流 → 降清晰度 → 软重启播放内核。
    /// 静默级不发状态不占断流重连次数；重取流消费⑬健康表（ReconnectRefresher 内优先取最近验证可用来源）。
    /// </summary>
    private void HandleStall()
    {
        if (_kernelRebuilding) return;
        if (_rescuePaused) return; // fix31 观察期/收口期：自救阶梯静默（含首帧兜底阶梯），保持当前画面
        if (IsSwapPending) return; // 双实例切换未决：新流亮了画面自然接上，不叠加自救
        // fix30 自救风暴断路器：60 秒窗口内自救超 6 次即冷却静默观察——连环自救会打满 CPU
        //（软解+内核重建），整机被拖死连系统按键都无响应（真机「只能关机重启」的帮凶之一）。
        // 冷却期只记录不动手，窗口内频次回落自动恢复自救。
        if (!_rescueBreaker.CanRescue(Environment.TickCount64))
        {
            try { CrashNet.Record("[LiveVideoHost] fix30 自救风暴断路：60 秒窗口自救过频，冷却静默观察（防 CPU 打满拖死整机）"); } catch { }
            return;
        }
        _rescueBreaker.OnRescue(Environment.TickCount64);
        _stallCount++;
        var action = StallRescue.Plan(_stallCount - 1, HasLowerQuality?.Invoke() ?? false);
        switch (action)
        {
            case StallRescueAction.RestartKernel:
                StatusChanged?.Invoke("画面卡住，正在重启播放内核…");
                RestartKernel();
                break;
            default:
                if (action == StallRescueAction.LowerQuality) LowerQualityRequested?.Invoke();
                _ = SilentRefreshAsync();
                break;
        }
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
        LastBufferWatermark = $"{CacheLadder.At(_cachingIndex)}ms/{(int)Volatile.Read(ref _bufferPct)}%"; // fix31 ③：资源观测日志水位快照
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
            case BufferAction.HoldEdge: // 平稳贴边：阶梯缓降回零延迟（30 秒最多一档，不闪不卡）
                long now = Environment.TickCount64;
                if (_cachingIndex > 0 && now - _lastDeescalateMs > 30_000)
                {
                    _lastDeescalateMs = now;
                    _cachingIndex--;
                    CrashNet.Record($"[BufferPolicy] 水位平稳贴边：重连缓存目标 → {CacheLadder.At(_cachingIndex)}ms（平滑追回直播边缘）");
                }
                break;
        }
    }

    /// <summary>静默重取流：换新鲜入口立即快速重接，不发状态、不动断流重连计数。</summary>
    private async Task SilentRefreshAsync()
    {
        if (_rescuePaused) return; // fix31 观察期/收口期：静默重取流暂停，保持当前画面
        long gen = _genCounter.NextGeneration(); // 新一代：旧重连/旧自救路径全部作废
        if (ReconnectRefresher != null)
        {
            try
            {
                var fresh = await ReconnectRefresher();
                if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 期间换台：作废
                if (fresh?.Url != null && fresh.Url != _currentUrl)
                {
                    _currentUrl = fresh.Url;
                    if (fresh.Options != null)
                        _currentOptions = fresh.Options.Select(o => $":{o.Key}={o.Value}").ToList();
                }
            }
            catch (Exception ex) { CrashNet.Record($"[LiveVideoHost] 静默重取流换源失败（{FailureKind(ex)}）：{ex.Message}（沿用原地址快速重接）"); }
        }
        if (_userStopped || !_genCounter.IsCurrent(gen)) return;
        StartInternal(silent: true);
    }

    /// <summary>
    /// 起播一路直播流（URL 已由 StreamResolver 签好；透传头与画质滤镜合并为选项列表）。
    /// 换台先停旧播放（v1.0.8）：坏状态播放器不带病起播，代数推进作废旧回调。
    /// </summary>
    public void Play(string url, IReadOnlyList<(string Key, string Value)> options)
    {
        _currentUrl = url;
        _currentOptions = options.Select(o => $":{o.Key}={o.Value}").ToList();
        _retriesLeft = 3;
        _userStopped = false;
        _exhaustedNotified = false;
        _cachingIndex = 0;
        _stallCount = 0;      // ⑫：换台自救行进清零，分级从静默重取流重新走
        _firstFrameFails = 0; // fix29：换台清首帧兜底计数（新台新算）
        _measuredFps = null;  // ④：换台重新实测
        _fpsLastDisplayed = 0;
        _fpsLastMs = 0;
        _origFps = null; _origW = 0; _origH = 0;
        _genCounter.NextGeneration(); // 新一代：旧重连定时器全作废
        StartInternal();
    }

    /// <summary>音量步进（保留接口）：遥控音量走独立硬件键由系统处理，此接口暂不接线。</summary>
    public int AdjustVolume(int delta)
    {
        if (_player is null) return -1;
        var vol = Math.Clamp(_player.Volume + delta, 0, 100);
        _player.Volume = vol;
        return vol;
    }

    private void StartInternal(bool silent = false)
    {
        long gen = _genCounter.NextGeneration(); // 本次起播代数（Stop 事件/旧定时器作废）
        _frameShown = false; // 每次起播/重连都要重新等首帧，遮罩才撤得对
        _streamDead = false; // 起播即视为存活，等首帧与时钟验证
        Volatile.Write(ref _lastTickMs, Environment.TickCount64);
        if (!silent) StatusChanged?.Invoke("连接中…");
        // fix30：原生起播整段投递内核线程——Play 可从主线程（换台/回前台）调入，
        // 绝不在主线程等 VLC 原生锁；队列串行保证同一内核不并发操作。
        // 内核重建期间（fix30 RestartKernel 已清 _player）起播不失败：闭包内现建内核再播。
        var player = _player;
        var libVlc = _libVlc;
        var url = _currentUrl;
        if (url is null) return;
        var opts = _currentOptions;
        var caching = CacheLadder.At(_cachingIndex);
        EnqueueKernel(() =>
        {
            try
            {
                if (player is null)
                {
                    // fix30：内核重建竞态兜底——重建未完成时到来的起播在闭包内现建内核
                    if (_player is null) BuildKernel();
                    player = _player!;
                    libVlc = _libVlc!;
                }
                if (libVlc is null) return; // 内核实例缺位（理论不可达）：按本次起播失败退回，由下一轮重连兜底
                player.Stop(); // 换台/重连先停旧播放；Stop 触发的回调由代数守卫作废
                var oldMedia = _currentMedia;
                _currentMedia = null;
                try { oldMedia?.Dispose(); } catch { }
                var media = new Media(libVlc, new Uri(url));
                _currentMedia = media; // ④保留引用：Media.Statistics 提供帧统计
                // 直播低延迟：缓存阶梯（首连 1200ms，重连 2500→4000→6000 自适应软解跟不上的流）
                media.AddOption($":network-caching={caching}");
                media.AddOption($":live-caching={Math.Max(1200, caching)}");
                foreach (var opt in opts) media.AddOption(opt);
                ParseMediaStats(media); // ④原/现帧率与分辨率：起播解析媒体轨道
                _lastDecodedCount = 0; _decodedStallTicks = 0; // fix29：新媒体统计从头记账
                player.Play(media);
            }
            catch (Exception ex) { try { CrashNet.Record("[Kernel] 起播异常（内核线程内收口）：" + ex.Message); } catch { } }
        });
        ArmFirstFrameTimer(gen); // fix29：无首帧盲区兜底（每次起播重新武装，代数守卫作废旧表）
    }

    /// <summary>
    /// fix29 首帧等待兜底（CCTV-7 卡死盲区）：起播后迟迟不出首帧时，看门狗按「未出首帧交给连接流程」让位、
    /// 静默自救路径又不接连接流程——两边互相等就无限期卡死。这里兜底：超时先走自救阶梯（HandleStall：
    /// 重取流→降清晰度→重启内核，各自救路径经 StartInternal 会重新武装本计时器），阶梯走完仍无画面
    /// 交回上层收口（FirstFrameExhausted：判死本台、放开探活自动跳台），绝不无限卡死。
    /// </summary>
    private void ArmFirstFrameTimer(long gen)
    {
        _firstFrameTimer?.Dispose();
        _firstFrameTimer = new System.Threading.Timer(_ =>
        {
            if (_userStopped || !_genCounter.IsCurrent(gen) || _frameShown) return;
            _firstFrameFails++;
            if (FirstFramePolicy.ShouldHandOver(_firstFrameFails))
            {
                _firstFrameTimer?.Dispose();
                _firstFrameTimer = null;
                try { CrashNet.Record($"[LiveVideoHost] fix29 首帧兜底：自救阶梯走完仍无画面（连续 {_firstFrameFails} 次），交回上层收口"); } catch { }
                try { FirstFrameExhausted?.Invoke(); } catch { }
                return;
            }
            try { CrashNet.Record($"[LiveVideoHost] fix29 首帧兜底：起播 {FirstFramePolicy.WaitMs}ms 未出画面（第 {_firstFrameFails} 次），走自救阶梯"); } catch { }
            HandleStall();
        }, null, FirstFramePolicy.WaitMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>解析媒体视频轨道（第④项）：原帧率/原分辨率；无缩放滤镜时现=原，测量缺失显示「检测中」。媒体尚未挂上时静默跳过。</summary>
    private void ParseMediaStats(Media? media)
    {
        if (media is null) return; // 旧流已停/新媒体未挂：本次轮空，由下一周期补测
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
        _streamDead = true; // 断流等待期按已死处理（回前台须重播，不能拿死流当活）
        if (_retriesLeft <= 0)
        {
            if (_exhaustedNotified) return;
            _exhaustedNotified = true;
            StatusChanged?.Invoke("信号中断，请稍后重试或换台");
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
            if (_rescuePaused) return; // fix31 观察期/收口期：断流重连暂停，保持当前画面（期满上层放行后由看门狗/重试链路恢复）
            // 重连前重新取票（v1.0.8）：过期票据重试必败，先换新鲜入口再起播
            if (ReconnectRefresher != null)
            {
                try
                {
                    var fresh = await ReconnectRefresher();
                    if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 换票期间换台
                    if (fresh?.Url != null && fresh.Url != _currentUrl)
                    {
                        _currentUrl = fresh.Url;
                        if (fresh.Options != null)
                            _currentOptions = fresh.Options.Select(o => $":{o.Key}={o.Value}").ToList();
                    }
                }
                catch (Exception ex) { CrashNet.Record($"[LiveVideoHost] 重连换票失败（{FailureKind(ex)}）：{ex.Message}（沿用原地址重试）"); }
            }
            StartInternal();
        };
        timer.Start();
    }

    public void Stop()
    {
        _frameShown = false;
        _streamDead = true;
        _userStopped = true;
        _firstFrameTimer?.Dispose(); _firstFrameTimer = null; // fix29：停播作废首帧兜底
        _firstFrameFails = 0;
        AbortSwap("停止播放"); // 换台/停播未决切换一并作废：第二实例不残留
        _genCounter.NextGeneration(); // 作废未决重连
        // fix30：原生停播投递内核线程——主线程绝不等待 VLC 原生锁，卡死时退出/按键照常
        var player = _player;
        if (player != null) EnqueueKernel(() => { try { player.Stop(); } catch { } });
    }

    // ---- 双实例无感切换（v1.0.10【六】【九】A）：新流亮了再换，全程不黑不闪不卡 ----

    /// <summary>
    /// 后台起第二实例连新流（新档变体或同流新画质参数）。载体由 UI 建好并已加入视图树（暂不可见）。
    /// 当前画面在提交前原样保留——绝不重起当前台。返回 false=未能受理（调用方留在当前档）。
    /// </summary>
    public bool BeginSwap(IVideoCarrierView carrier, string url, IReadOnlyList<(string Key, string Value)> options)
    {
        if (_swapPlayer != null || _userStopped || _currentUrl is null) return false;
        try
        {
            _swapCarrier = carrier;
            _swapLibVlc = new LibVLC(enableDebugLogs: false);
            _swapPlayer = new MediaPlayer(_swapLibVlc) { EnableHardwareDecoding = false }; // 软解口径与主实例一致
            carrier.MediaPlayer = _swapPlayer;
            _swapFrameShown = false;
            _swapOnError = (_, _) => { try { SwapFailed?.Invoke(); } catch { } };
            _swapOnTime = (_, _) =>
            {
                if (_swapFrameShown) return;
                _swapFrameShown = true;
                try { SwapReady?.Invoke(); } catch { }
            };
            _swapPlayer.EncounteredError += _swapOnError;
            _swapPlayer.TimeChanged += _swapOnTime;
            var media = new Media(_swapLibVlc, new Uri(url));
            var caching = CacheLadder.At(_cachingIndex);
            media.AddOption($":network-caching={caching}");
            media.AddOption($":live-caching={Math.Max(1200, caching)}");
            foreach (var opt in options) media.AddOption($":{opt.Key}={opt.Value}");
            // fix24（老板拍板）：与主画面同一套请求头——绑头来源（央视频 JWT 绑 UA / Referer 防盗链）
            // 第二实例不带必被拒，起流永远失败、升级空转；缺键才补，不覆盖调用方显式给的参数
            var given = new HashSet<string>(options.Select(o => o.Key));
            foreach (var opt in _currentOptions)
            {
                var body = opt.StartsWith(':') ? opt.Substring(1) : opt;
                var eq = body.IndexOf('=');
                var key = eq > 0 ? body.Substring(0, eq) : body;
                if (!given.Contains(key)) media.AddOption(opt);
            }
            _swapMedia = media;
            _swapPlayer.Play(media);
            CrashNet.Record($"[HotSwap] 双实例切换开始：url={MaskUrl(url)}（第二实例后台连新流，当前画面原样保留）");
            return true;
        }
        catch (Exception ex)
        {
            CrashNet.Record($"[HotSwap] 第二实例起流异常，留当前档：{ex.Message}");
            try { CleanupSwapKernel(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 提交切换：第二实例新流已亮，顶上成主实例——用户只见新画面，无黑无闪。
    /// 只在第二实例首帧到齐后受理；绝不出「半程黑屏」。
    /// </summary>
    public void CommitSwap()
    {
        if (_swapPlayer is null || !_swapFrameShown || _userStopped) return;
        try
        {
            // 摘除第二实例临时挂接，避免与主实例全量接线重复触发
            if (_swapOnError != null) _swapPlayer.EncounteredError -= _swapOnError;
            if (_swapOnTime != null) _swapPlayer.TimeChanged -= _swapOnTime;
            _swapOnError = null; _swapOnTime = null;

            // 撤旧内核（旧画面由第二实例画面顶替，无间隙）：Stop→释放，与换台撤旧同一口径
            // fix30：旧内核原生释放投递内核线程——提交可从主线程调入，绝不在主线程碰原生锁
            var oldPlayer = _player;
            var oldLibVlc = _libVlc;
            var oldMedia = _currentMedia;

            // 第二实例顶上成主实例，接全量宿主职责（看门狗/重连/统计/自救）
            _player = _swapPlayer;
            _libVlc = _swapLibVlc;
            _currentMedia = _swapMedia;
            _videoCarrier = _swapCarrier; // 旧载体视图由 UI 移出视图树
            _swapPlayer = null; _swapLibVlc = null; _swapMedia = null; _swapCarrier = null;
            WireKernelEvents(_player);
            _frameShown = true; // 新流已亮，无需再等首帧
            _origFps = null; _origW = 0; _origH = 0; _measuredFps = null; _fpsLastMs = 0; _fpsLastDisplayed = 0;
            _statsRetries = 0;
            if (oldPlayer != null)
            {
                EnqueueKernel(() =>
                {
                    try { oldPlayer.Stop(); } catch { /* 已停不碍提交 */ }
                    try { oldMedia?.Dispose(); } catch { }
                    try { oldPlayer.Dispose(); } catch { }
                    try { oldLibVlc?.Dispose(); } catch { }
                });
            }
            ParseMediaStats(_currentMedia); // ④新媒体轨道解析（首帧已过，直接取）
            StartStatsRetry();
            Volatile.Write(ref _lastTickMs, Environment.TickCount64);
            CrashNet.Record("[HotSwap] 切换提交：新画面顶上，全程无黑无闪");
        }
        catch (Exception ex)
        {
            CrashNet.Record($"[HotSwap] 提交异常（保留第二实例状态待收口）：{ex.Message}");
        }
    }

    /// <summary>放弃切换：第二实例撤掉，留当前档——当前画面从头到尾无任何扰动。</summary>
    public void AbortSwap(string reason)
    {
        if (_swapPlayer is null) return;
        CleanupSwapKernel();
        CrashNet.Record($"[HotSwap] 放弃切换，留在当前档：{reason}");
    }

    private void CleanupSwapKernel()
    {
        // fix30：第二实例原生释放投递内核线程（主线程只清托管状态，绝不碰原生锁）
        var swapPlayer = _swapPlayer;
        var swapLibVlc = _swapLibVlc;
        var swapMedia = _swapMedia;
        var onErr = _swapOnError;
        var onTime = _swapOnTime;
        _swapPlayer = null; _swapLibVlc = null; _swapMedia = null; _swapCarrier = null;
        _swapFrameShown = false; _swapOnError = null; _swapOnTime = null;
        if (swapPlayer is null) return;
        EnqueueKernel(() =>
        {
            try { if (onErr != null) swapPlayer.EncounteredError -= onErr; } catch { }
            try { if (onTime != null) swapPlayer.TimeChanged -= onTime; } catch { }
            try { swapPlayer.Stop(); } catch { }
            try { swapMedia?.Dispose(); } catch { }
            try { swapPlayer.Dispose(); } catch { }
            try { swapLibVlc?.Dispose(); } catch { }
        });
    }

    /// <summary>退后台：画面照常走（纹理载体不回收），声音先闭掉（记住原音量）。</summary>
    public void MuteForBackground()
    {
        if (_player is null || _mutedForBackground) return;
        _mutedForBackground = true;
        _savedVolume = _player.Volume;
        _player.Volume = 0;
    }

    /// <summary>回前台：声音原样恢复（退前用户调到多少回多少，不回默认满音量）。</summary>
    public void UnmuteAfterBackground()
    {
        if (_player is null || !_mutedForBackground) return;
        _mutedForBackground = false;
        _player.Volume = _savedVolume;
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _firstFrameTimer?.Dispose(); // fix29：首帧兜底计时器随宿主释放
        _firstFrameTimer = null;
        _statsRetryTimer?.Dispose(); // ⑤重试定时器随宿主释放
        _statsRetryTimer = null;

        _userStopped = true;
        CleanupSwapKernel(); // 双实例第二实例随宿主释放，不残留（fix30：原生释放经内核线程）
        var media = _currentMedia;
        var player = _player;
        var libVlc = _libVlc;
        _currentMedia = null; _player = null; _libVlc = null;
        if (media != null || player != null || libVlc != null)
        {
            // fix30：原生释放投递内核线程（进程结束自然回收；绝不阻塞销毁方）
            EnqueueKernel(() =>
            {
                try { media?.Dispose(); } catch { }
                try { player?.Dispose(); } catch { }
                try { libVlc?.Dispose(); } catch { }
            });
        }
        _kernelQueue.CompleteAdding(); // fix30：内核队列收口（内核线程随进程退出）
        _videoCarrier = null;
    }

    /// <summary>【十二】失败归因（ConnectFailure 分类口径）：逐类落日志，不笼统「连接失败」。</summary>
    internal static string FailureKind(Exception ex) => ConnectFailure.Classify(ex.ToString()).ToString();

    /// <summary>【八】脱敏源地址：只留协议+主机，路径与查询串不落日志。</summary>
    private static string MaskUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "-";
        try { var u = new Uri(url); return $"{u.Scheme}://{u.Host}"; }
        catch { return url.Length > 24 ? url[..24] + "…" : url; }
    }
}
