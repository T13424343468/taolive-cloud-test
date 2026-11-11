using System;
using TaoLive.BLL;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private int _earlyRescueStreak;          // v23 第 5 条：⑨B静默重取流连续介入次数（连续满 EarlyRescueEscalateAt 升级自救阶梯）
    private long _lastEarlyRescueMs;         // v23 第 5 条：最近一次⑨B介入时刻（持续走钟 EarlyRescueResetMs 才清计数，假活不清零）
    private long _lastRescueTriggerMs;       // 1.0.12 修正①：最近一次自救触发时刻（Buffering 事件与看门狗 5 秒窗去重）
    private long _rescueWindowStartMs;       // 1.0.12 修正⑤：90 秒介入节流窗口起点
    private int _rescueWindowCount;          // 1.0.12 修正⑤：窗口内已介入次数（封顶 StallRescue.RescueCapPerWindow）
    private bool _rescueThrottleLogged;      // 1.0.12 修正⑤：节流期只记一次日志，防刷屏
    private int _refreshFailures;            // 1.0.12 发现 A：连续换票失败次数（第 2 次起换票委托切备援；成功即清零）
    private bool _inventoryRefetch;          // 1.0.12 发现 B：静默重接补库存档——首帧后已补过一次 3 秒库存
    private float _bufferPct = 100f;          // ⑩播放器缓冲百分比（Buffering 事件回报，0–100）
    private BufferAction? _lastBufferAction;  // ⑩上一次水位动作（变化才落日志，防刷屏）
    /// <summary>⑩设备总内存（秒级水位上限自适应），由宿主创建方注入。</summary>
    public long TotalMemBytes { get; set; }

    /// <summary>1.0.12 发现 B：3 秒库存档位（默认「起播即 3 秒」拍板口径；真机验证静默重接保帧后可切补库存档）。</summary>
    public BufferInventory.Mode InventoryMode { get; set; } = BufferInventory.Default;

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

    /// <summary>画质滤镜的键集合（A⑩重挂滤镜时先摘旧键再并新键）。</summary>
    private static readonly string[] QualityFilterKeys = { "video-filter", "sharpen-amplitude", "contrast", "saturation", "gamma" };

    /// <summary>
    /// A 类第10项·档位变化后重挂滤镜：先摘掉旧画质键，并上新档滤镜（缺键才补同款合并），静默快速重接。
    /// 未在播（无播放器/已手动停）只记账不重接，下次起播自然带新档。
    /// </summary>
    public void ApplyQualityTier(IReadOnlyList<(string Key, string Value)> filterOptions)
    {
        var stripped = _currentOptions.Where(o =>
        {
            var body = o.StartsWith(':') ? o[1..] : o;
            var eq = body.IndexOf('=');
            var key = eq < 0 ? body : body[..eq];
            return !QualityFilterKeys.Contains(key);
        }).ToList();
        _currentOptions = MergeOptions(stripped, filterOptions);
        if (_player != null && !_userStopped && _currentUrl != null) StartInternal(silent: true);
    }

    /// <summary>
    /// A 类第10项·喂给调速器的实测快照：现帧率、累计掉帧占比（丢帧/(丢帧+显示帧)）、播放时钟是否停滞。
    /// 无媒体统计（未出首帧/统计缺失）时返回 stalled=true 前的保守值：调用方自行只在出过首帧后喂。
    /// </summary>
    public (double? Fps, double DroppedRate, bool Stalled) SnapshotGovernorStats()
    {
        var media = _currentMedia;
        var fps = _measuredFps;
        double droppedRate = 0;
        bool stalled;
        if (media != null)
        {
            var st = media.Statistics;
            long total = (long)st.DisplayedPictures + st.LostPictures;
            if (total > 0) droppedRate = (double)st.LostPictures / total;
            stalled = st.DisplayedPictures > 0 && (fps is null || fps <= 0); // 出过帧但现在测不出帧率=停滞
        }
        else stalled = true;
        return (fps, droppedRate, stalled);
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

    /// <summary>⑫②级降清晰度：是否还有可降档（UI 按 AI 增强档位判断）；无档可降时自救直接并入重取流。</summary>
    public Func<bool>? HasLowerQuality { get; set; }

    /// <summary>⑫②级降清晰度请求：UI 收到后降一档 AI 增强（软解负载减轻），随后静默重接。</summary>
    public event Action? LowerQualityRequested;

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
        _player.EncounteredError += (_, _) => OnStreamError();
        _player.Buffering += (_, e) =>
        {
            Volatile.Write(ref _bufferPct, e.Cache); // ⑩水位实测来源（0–100）
            // 1.0.12 修正①·主信号：缓冲接近耗尽（VLC 实时回报）即时触发提前介入，
            // 发现延迟从 2–6 秒压到 ≤1 秒；看门狗降级为兜底，双触发走 5 秒去重窗
            if (e.Cache <= StallRescue.BufferingLowPct) OnBufferingLow();
        };
        _player.Stopped += (_, _) => { /* 换台/重连的 Stop 有代数守卫，此处无需动作 */ };
        _player.TimeChanged += (_, _) =>
        {
            Volatile.Write(ref _lastTickMs, Environment.TickCount64);
            _earlyRescueUsed = false; // ⑨B时钟在走：本轮提前介入额度恢复
            if (_frameShown) return;
            _frameShown = true;
            _earlyRescueStreak = 0; // v23 第 5 条：首帧清零（真首帧才算，微动不清）
            // 1.0.12 发现 B·静默重接补库存档：首帧出画后静默重接一次，把库存补到 3 秒（先开画后补库存，
            // 换台首帧不慢）；默认「起播即 3 秒」档不需要这步
            if (InventoryMode == BufferInventory.Mode.SilentRefetch && !_inventoryRefetch)
            {
                _inventoryRefetch = true;
                _ = SilentRefreshAsync(announce: false);
            }
            StartStatsRetry(); // ⑤首帧后再解析轨道：起播后每500ms重试至多10次，后台不阻塞
            FrameShown?.Invoke();
        };
        if (_watchdog is null)
            _watchdog = new Timer(OnWatchdogTick, null, 2000, 2000);
    }

    /// <summary>⑫③软重启播放内核：重建设播环境（不动系统），同流继续。互斥防连环重启。</summary>
    public void RestartKernel()
    {
        if (_kernelRebuilding || _userStopped || _currentUrl is null) return;
        _kernelRebuilding = true;
        try
        {
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
        finally { _kernelRebuilding = false; }
    }

    private void OnWatchdogTick(object? state)
    {
        if (_userStopped || _currentUrl is null) return;
        if (!_frameShown) return;                 // 还没出首帧：连接流程自己管理，不看门狗
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
            if (age >= EarlyRescueMs && !_earlyRescueUsed && !_kernelRebuilding)
                TriggerEarlyRescue($"看门狗兜底：时钟 {age}ms 未走");
            ObserveBufferWatermark(age); // ⑩水位实接：默认贴边、抖动攒水位、平稳追回，动作变化落日志
            return;
        }
        Volatile.Write(ref _lastTickMs, Environment.TickCount64); // 防连发：触发一次后重新计时
        HandleStall();                            // ⑫分级快速自救：快过断流重连阶梯
    }

    /// <summary>1.0.12 修正①·Buffering 主信号触发点：缓冲接近耗尽即时介入（VLC 内核线程回调，守卫后走统一入口）。</summary>
    private void OnBufferingLow()
    {
        if (_userStopped || !_frameShown || _kernelRebuilding || _streamDead) return;
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
            if (!_rescueThrottleLogged)
            {
                _rescueThrottleLogged = true;
                try { CrashNet.Record($"[LiveVideoHost] ⑨B 介入节流：{StallRescue.RescueWindowMs / 1000} 秒内已达 {StallRescue.RescueCapPerWindow} 次上限，本窗口不再重取流（1.0.12 修正⑤：限频代替拉黑）"); } catch { }
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
    /// ⑫画面卡住分级快速自救（用户拍板「可以就这么定」）：静默重取流 → 降清晰度 → 软重启播放内核。
    /// 静默级不发状态不占断流重连次数；重取流消费⑬健康表（ReconnectRefresher 内优先取最近验证可用来源）。
    /// </summary>
    private void HandleStall()
    {
        if (_kernelRebuilding) return;
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
                _ = SilentRefreshAsync(announce: true);
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
        if (ReconnectRefresher != null)
        {
            var fresh = await RefreshStreamAsync("静默重取流");
            if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 期间换台：作废
            if (fresh?.Url != null && fresh.Url != _currentUrl)
            {
                _currentUrl = fresh.Url;
                // A 类 P1·缺键才补：静默重取给出的选项并入现有表（同键以新值为准，未给的键沿用旧表），
                // 整表覆盖会丢掉 Play 时注入的 Referer/UA 请求头——第二实例被 CDN 拒的实锤根因
                if (fresh.Options != null)
                    _currentOptions = MergeOptions(_currentOptions, fresh.Options);
            }
        }
        if (_userStopped || !_genCounter.IsCurrent(gen)) return;
        StartInternal(silent: true);
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
            if (fresh?.Url != null) Volatile.Write(ref _refreshFailures, 0);
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
        _earlyRescueStreak = 0; // v23 第 5 条：换台清零连续介入计数
        _lastEarlyRescueMs = 0;
        _lastRescueTriggerMs = 0;   // 1.0.12 修正①：去重窗随换台清零
        _rescueWindowCount = 0;     // 1.0.12 修正⑤：介入节流窗口随换台重开（新台新预算）
        _rescueThrottleLogged = false;
        _refreshFailures = 0;       // 1.0.12 发现 A：连续换票失败计数随换台清零
        _inventoryRefetch = false;  // 1.0.12 发现 B：补库存标记随换台重置
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
        if (_player is null || _currentUrl is null) return;
        long gen = _genCounter.NextGeneration(); // 本次起播代数（Stop 事件/旧定时器作废）
        _frameShown = false; // 每次起播/重连都要重新等首帧，遮罩才撤得对
        _streamDead = false; // 起播即视为存活，等首帧与时钟验证
        Volatile.Write(ref _lastTickMs, Environment.TickCount64);
        if (!silent) StatusChanged?.Invoke("连接中…");
        _player.Stop(); // 换台/重连先停旧播放；Stop 触发的回调由代数守卫作废
        _currentMedia?.Dispose(); // ④旧媒体统计源释放
        var media = new Media(_libVlc!, new Uri(_currentUrl));
        _currentMedia = media; // ④保留引用：Media.Statistics 提供帧统计
        // 直播低延迟：缓存阶梯（首连 1200ms，重连 2500→4000→6000 自适应软解跟不上的流）；
        // 1.0.12 发现 B：3 秒库存两档——默认「起播即 3 秒」（库存档兜底，修正②：重连缓存递增口径并入固定库存档，
        // 不被水位自适应降回去）；补库存档先开画（修正④：换台口径=先开画后补库存），首帧后静默重接补到 3 秒
        int ladder = CacheLadder.At(_cachingIndex);
        int caching = (_inventoryRefetch || InventoryMode == BufferInventory.Mode.StartFull)
            ? Math.Max(BufferInventory.TargetMs, ladder)
            : ladder;
        media.AddOption($":network-caching={caching}");
        media.AddOption($":live-caching={Math.Max(1200, caching)}");
        foreach (var opt in _currentOptions) media.AddOption(opt);
        ParseMediaStats(media); // ④原/现帧率与分辨率：起播解析媒体轨道
        _player.Play(media);
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
            // 重连前重新取票（v1.0.8）：过期票据重试必败，先换新鲜入口再起播；
            // 1.0.12 发现 A：走统一换票入口按连续失败分级——第 1 次同源、第 2 次起委托切备援
            var fresh = await RefreshStreamAsync("重连");
            if (_userStopped || !_genCounter.IsCurrent(gen)) return; // 换票期间换台
            if (fresh?.Url != null && fresh.Url != _currentUrl)
            {
                _currentUrl = fresh.Url;
                // A 类 P1·缺键才补：同上，重连换票绝不整表覆盖丢请求头
                if (fresh.Options != null)
                    _currentOptions = MergeOptions(_currentOptions, fresh.Options);
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
        _genCounter.NextGeneration(); // 作废未决重连
        _player?.Stop();
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
        _statsRetryTimer?.Dispose(); // ⑤重试定时器随宿主释放
        _statsRetryTimer = null;

        _userStopped = true;
        _currentMedia?.Dispose();
        _currentMedia = null;
        _player?.Dispose();
        _libVlc?.Dispose();
        _videoCarrier = null;
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
