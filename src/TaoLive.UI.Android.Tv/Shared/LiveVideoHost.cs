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

    // ⑫画面信息行（第④项）：原/现帧率与分辨率快照。分辨率在起播解析媒体轨道取得（原=现，无缩放）；
    // 现帧率按展示帧差实测，测不出如实给「检测中」。
    private double? _origFps; private int _origW, _origH;
    private long _fpsLastMs; private uint _fpsLastDisplayed; private double? _measuredFps;

    public event Action<string>? StatusChanged;

    /// <summary>画面统计快照（第④项）：原帧率/现帧率/原分辨率/现分辨率。测量缺失给 0/null，UI 显示「检测中」。</summary>
    public (double? OrigFps, double? CurFps, int OrigW, int OrigH, int CurW, int CurH) SnapshotStats()
    {
        var st = _player?.Statistics;
        if (st != null && st.DisplayedPictures > 0)
        {
            long now = Environment.TickCount64;
            if (_fpsLastDisplayed > 0 && now - _fpsLastMs > 800)
            {
                double fps = (st.DisplayedPictures - _fpsLastDisplayed) * 1000.0 / (now - _fpsLastMs);
                if (fps > 0 && fps < 240) _measuredFps = fps;
            }
            _fpsLastDisplayed = st.DisplayedPictures;
            _fpsLastMs = now;
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
        _player.Stopped += (_, _) => { /* 换台/重连的 Stop 有代数守卫，此处无需动作 */ };
        _player.TimeChanged += (_, _) =>
        {
            Volatile.Write(ref _lastTickMs, Environment.TickCount64);
            if (_frameShown) return;
            _frameShown = true;
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
        if (!PlaybackWatchdog.IsStalled(_frameShown, last, Environment.TickCount64)) return;
        Volatile.Write(ref _lastTickMs, Environment.TickCount64); // 防连发：触发一次后重新计时
        HandleStall();                            // ⑫分级快速自救：快过断流重连阶梯
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
                _ = SilentRefreshAsync();
                break;
        }
    }

    /// <summary>静默重取流：换新鲜入口立即快速重接，不发状态、不动断流重连计数。</summary>
    private async Task SilentRefreshAsync()
    {
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
            catch { /* 换源失败：沿用原地址快速重接 */ }
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
        using var media = new Media(_libVlc!, new Uri(_currentUrl));
        // 直播低延迟：缓存阶梯（首连 1200ms，重连 2500→4000→6000 自适应软解跟不上的流）
        var caching = CacheLadder.At(_cachingIndex);
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
                if (t.Data is VideoTrack vt)
                {
                    _origW = vt.Width; _origH = vt.Height;
                    if (vt.FrameRateDen > 0) _origFps = vt.FrameRateNum / (double)vt.FrameRateDen;
                }
                break;
            }
        }
        catch { /* 轨道信息取不到：画面信息行显示检测中，绝不影响播放 */ }
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
                catch { /* 换票失败：沿用原地址重试，重试次数照旧计 */ }
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
        _userStopped = true;
        _player?.Dispose();
        _libVlc?.Dispose();
        _videoCarrier = null;
    }
}
