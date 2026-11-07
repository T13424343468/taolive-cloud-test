using System;
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

    public event Action<string>? StatusChanged;

    /// <summary>
    /// 重连前刷新入口（v1.0.8）：断流重连不再拿旧地址硬重试——直播源票据 5 分钟级过期，
    /// 旧 URL 重试一百次也是失败。UI 层挂入换票委托，重连前先取新鲜入口；取不到（源端
    /// 失效/冷却中）沿用原地址再试一次，重试次数照旧计。
    /// </summary>
    public Func<Task<RefreshedStream?>>? ReconnectRefresher { get; set; }

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
        _libVlc = new LibVLC(enableDebugLogs: false);
        // 真机反馈：Amlogic t982 硬解黑屏，默认软解保画面（v1.0.2）
        _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = false };
        _videoCarrier.MediaPlayer = _player;
        _player.EncounteredError += (_, _) => OnStreamError();
        _player.Stopped += (_, _) => { /* 换台/重连的 Stop 有代数守卫，此处无需动作 */ };
        _player.TimeChanged += (_, _) =>
        {
            Volatile.Write(ref _lastTickMs, Environment.TickCount64);
            if (_frameShown) return;
            _frameShown = true;
            FrameShown?.Invoke();
        };

        // 卡流看门狗（v1.0.8）：直播流停止推进时 VLC 不触发错误，靠播放时钟判断。
        // 2 秒巡检一次；首帧出过后播放时钟 6 秒无推进 = 流冻住，按断流走重连。
        _watchdog = new Timer(OnWatchdogTick, null, 2000, 2000);
    }

    private void OnWatchdogTick(object? state)
    {
        if (_userStopped || _currentUrl is null) return;
        if (!_frameShown) return;                 // 还没出首帧：连接流程自己管理，不看门狗
        long last = Volatile.Read(ref _lastTickMs);
        if (!PlaybackWatchdog.IsStalled(_frameShown, last, Environment.TickCount64)) return;
        Volatile.Write(ref _lastTickMs, Environment.TickCount64); // 防连发：触发一次后重新计时
        OnStreamError();
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

    private void StartInternal()
    {
        if (_player is null || _currentUrl is null) return;
        long gen = _genCounter.NextGeneration(); // 本次起播代数（Stop 事件/旧定时器作废）
        _frameShown = false; // 每次起播/重连都要重新等首帧，遮罩才撤得对
        _streamDead = false; // 起播即视为存活，等首帧与时钟验证
        Volatile.Write(ref _lastTickMs, Environment.TickCount64);
        StatusChanged?.Invoke("连接中…");
        _player.Stop(); // 换台/重连先停旧播放；Stop 触发的回调由代数守卫作废
        using var media = new Media(_libVlc!, new Uri(_currentUrl));
        // 直播低延迟：缓存阶梯（首连 1200ms，重连 2500→4000→6000 自适应软解跟不上的流）
        var caching = CacheLadder.At(_cachingIndex);
        media.AddOption($":network-caching={caching}");
        media.AddOption($":live-caching={Math.Max(1200, caching)}");
        foreach (var opt in _currentOptions) media.AddOption(opt);
        _player.Play(media);
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
