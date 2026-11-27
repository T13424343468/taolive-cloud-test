using Android.Runtime;
using Android.Views;
using MediaItemLiveConfiguration = AndroidX.Media3.Common.MediaItem.LiveConfiguration;
using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Players;

/// <summary>
/// ExoPlayer（Media3）适配器：2.0 第一内核（老板 2026-11-15 03:37 拍板，不采用 LibVLC 打底）。
/// 实现契约：Foundation/IPlayer——事件语义（次序即文档）与 IPlayer 契约保持一致，
/// 会话层只认 IPlayer 不认内核本尊。
/// 事件映射：
///   内核进入 Buffering → OnBufferingStart；
///   内核进入 Ready → OnBufferingEnd + OnFirstFrame（每次 Play 仅一次）；
///   内核进入 Ended → 视同卡流 OnBufferingStart（R2-72 发现即改：直播流被服务端掐断时内核进
///   ENDED，此前未映射 → 无错误回调、无卡流事件，状态机停在 Playing，画面冻结且看门狗/
///   备源自救永不触发——冻画黑洞。直播语义 ENDED=流断，交会话卡流路径自救）；
///   OnPlayerError → OnError（终态，直到下一次 Play）。
/// </summary>
public sealed class ExoPlayerPlayer : IPlayer
{
    private readonly Forwarder _forwarder;
    private readonly FrameTickForwarder _frameForwarder;
    private readonly DiagAnalyticsForwarder _diagForwarder;
    private AndroidX.Media3.ExoPlayer.IExoPlayer _player;
    private readonly Android.OS.Handler _main;
    private Func<TaoLive.Model.SourceEndpoint, Task<string?>>? _streamResolver;
    private bool _firstFrameRaisedThisPlay;
    private int _playGeneration; // 播放代次（闪退镜红② 11-15）：迟到 resolver 回调对不上代次即弃，不碰已释放内核
    private volatile bool _released; // vc74 P3⑧：释放旗——Dispose 摘听→Release 窗口期在途回调查旗即弃（收窄窗口口径，vc75 定案）
    private IntPtr _surfaceHandle;
    private bool _hasSurface;
    private readonly Android.Content.Context _context;
    private AndroidX.Media3.ExoPlayer.Video.PlaceholderSurface? _warmupSink; // vc66 哑面预热：攒流期焐热视频解码器

    /// <summary>主刀·时间平移快启（2026-11-25 九件套）：起播定位的落后目标（毫秒）。
    /// 挂 24 小时=任何直播清单窗口都会被内核自钳到窗口最深处（最老已播画面），
    /// 窗口给多深就落后多深；超过 24h 的直播 DVR 窗口按不存在口径（真机源清单最长以小时计）。</summary>
    private const long TimeShiftStartMs = 24 * 60 * 60 * 1000L;

    /// <summary>
    /// 取流注入点（2.0 W4 口径）：API 取票口端点播前经 resolver 换真实流（频道号+运行时取票），
    /// 直连端点原样放行。null=全部直连。解析失败按 OnError 交会话备源自救，契约不变。
    /// 频道上下文（缓存键/整表行匹配名）由壳层在 Play 前设置。
    /// </summary>
    public ExoPlayerPlayer(Android.Content.Context context,
        Func<TaoLive.Model.SourceEndpoint, Task<string?>>? streamResolver = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _forwarder = new Forwarder(this);
        _context = context;
        _frameForwarder = new FrameTickForwarder(this); // 帧回调（v76：10Hz 节流）随内核装配一次，Play 全程复用
        _main = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        _diagForwarder = new DiagAnalyticsForwarder(this); // 内核诊断监听（埋点补充 2026-11-15：解码报错/丢帧/欠载/带宽账）
        _player = BuildPlayer(context, blindZone: false); // 监听器装配统一走 BuildPlayer（盲区档重建同口径）
        _streamResolver = streamResolver;
    }

    /// <summary>
    /// 内核实例统一构建（vc85 微卡修法①）：盲区态决定「已播缓冲保留」档——
    /// 盲区源（非 .m3u8 直流）已播保留 25s→60s（微卡修法①「贴边回退留水」），播放点前面常备存量，
    /// 微卡回退 10 秒有地可落；HLS 源走原 25s（LiveConfiguration 留水已另行接管，不重复吃内存）。
    /// 仅在 Play 前盲区态变化时重建（起播期画面本来黑着，重建零视觉代价）；监听器装配与构造同口径。
    /// </summary>
    private AndroidX.Media3.ExoPlayer.IExoPlayer BuildPlayer(Android.Content.Context context, bool blindZone)
    {
        // vc94 并发拉流：预取器随内核装配——水位探针=内核 BufferedMs 实读，
        // 水位线=取满窗口（FillWindowMs 30s）；重建即换代清账（旧预取器退场，不串台）。
        // vc96 试探版（老板拍板「试探性并发」）：默认单路起步、水位不涨才逐路升级、被拒即缩；
        // 源脾气入持久化账 SourceTemperStore（FilesDir，重启不清零，下次调度直接按账定打法）。
        if (_prefetcher is { } oldPf)
        {
            oldPf.FetchRequested -= ParallelPrefetchDataSource.HandleFetchRequested;
            oldPf.Reset();
            _prefetcher = null;
        }
        ParallelPrefetcher? prefetcher = null;
        if (ParallelPrefetcher.Enabled)
        {
            var temper = _temperStore ??= new SourceTemperStore(_context.FilesDir?.AbsolutePath ?? ".");
            prefetcher = new ParallelPrefetcher(
                watermarkMs: BufferInventory.FillWindowMs,
                waterProbeMs: () => BufferedMs,
                temper: temper);
            prefetcher.FetchRequested += ParallelPrefetchDataSource.HandleFetchRequested;
            _prefetcher = prefetcher;
        }
        // vc96 P2：预取器与低档观察改实例级工厂接线（随内核实例装配），不再写静态属性——
        // 后建内核实例不再覆盖前者在用的接线（静态属性仅作未接线时的进程级兜底）。

        var playerBuilder = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(context);
        // 已播保留档只进 TrackSelector（minDurationToRetainAfterDiscardMs，微卡修法①）：
        // LoadControl 水位（存货上限）原样不动——盲区档只多留已播，不多占存货上限
        playerBuilder.SetLoadControl(BuildLoadControl(context, _gateMode));
        // 六项优化⑤慢分片竞速：内核数据源整面包竞速壳——分片首字节慢出均值（×2，下限 1 秒）
        // 即对同源发第二请求赛跑先到先用；清单/快链路不赛。HTTP 头纪律与默认工厂原样不动。
        playerBuilder.SetMediaSourceFactory(new AndroidX.Media3.ExoPlayer.Source.DefaultMediaSourceFactory(
            new RacingDataSourceFactory(prefetcher, OnMinVariantObserved)));
        // #76 锁画质防抖（2026-11-15 老板令，治「放 1 秒卡半秒」循环）：默认 ABR 在估速贴档位线抖动时
        // 逐秒重选流——每次切换渲染器拆建一遍（真机「视频渲染器停用」实锤），画面停半秒再续。
        // 改黏性自适应：升档要缓冲满 30 秒、降档判据放宽到 60 秒、估速按折扣取档——宁稳勿抖，
        // 起播按当前带宽选一档合理画质放住，不再随估速逐秒换挡。
        // vc71（2026-11-16）：折扣 0.5→0.15 限档验证包。2.0.13 真机「画面停半秒再续」复发疑回旧病：
        // 五折取档把估速 6.7~14.6Mbps 锁在最高 fhd 档，若盒子解 fhd 只有 ~2fps，症状同款；
        // 且 OnDroppedVideoFrames 的 ≥5 帧过滤把慢解的零星丢帧吞了（vc70「零丢帧行」有盲区）。
        // 压到一五折迫使选档落到低档（当年 799kbps 估速选低档跑得动=「之前都没抖」）：
        // 低档不抖即坐实「最高档盒子跑不动」，下一版按解码能力定档上限（疑点先降档、降不动再换线）。
        playerBuilder.SetTrackSelector(new AndroidX.Media3.ExoPlayer.TrackSelection.DefaultTrackSelector(
            context,
            new AndroidX.Media3.ExoPlayer.TrackSelection.AdaptiveTrackSelection.Factory(
                /* minDurationForQualityIncreaseMs */ 30_000,
                /* maxDurationForQualityDecreaseMs */ 60_000,
                /* minDurationToRetainAfterDiscardMs */ blindZone ? 60_000 : 25_000,
                /* bandwidthFraction */ 0.15f)));
        var player = playerBuilder.Build()!;
        player.AddListener(_forwarder);
        player.SetVideoFrameMetadataListener(_frameForwarder!);
        player.AddAnalyticsListener(_diagForwarder);
        return player;
    }

    /// <summary>盲区态切换（微卡修法①）：起播源盲区属性与当前内核保留档不符时重建内核实例。
    /// vc94 深水档扩展：库存档（_gateMode）与已建内核档（_builtGate）不一致同样重建——
    /// 热备攒流切 StartFull、冷切回默认档，重建统一在 Play 路径（起播期零视觉代价）。
    /// 只在 Play 路径调（起播期，画面未出），旧实例 release 幂等；重建失败退回旧实例照播（保守退化）。</summary>
    private void EnsurePlayerMode(bool blindZone)
    {
        if ((_blindZone == blindZone && _builtGate == _gateMode) || _released) return;
        var oldBlindZone = _blindZone; // vc96 P2：旧值先存，异常按旧值回滚（不再取反猜）
        var oldBuiltGate = _builtGate;
        var old = _player;
        var oldPrefetcher = _prefetcher; // vc96 第2轮：回滚时预取器接线一并还原（旧内核工厂持旧预取器）
        AndroidX.Media3.ExoPlayer.IExoPlayer? built = null;
        try
        {
            _blindZone = blindZone;
            _builtGate = _gateMode;
            built = BuildPlayer(_context, blindZone);
            if (_hasSurface)
            {
                // 真面重挂（同 AttachSurface 还原口径）：盲区档重建后真面继续归壳层所有，仅转移挂接
                var surface = Java.Lang.Object.GetObject<Android.Views.Surface>(_surfaceHandle, JniHandleOwnership.DoNotTransfer);
                if (surface is not null) built.SetVideoSurface(surface);
            }
            _player = built; // 新内核装配完整才换缝
            old.Release();
        }
        catch (Exception ex)
        {
            Reporter?.Invoke($"[内核] 盲区档切换失败退回原实例：{ex.Message}");
            _blindZone = oldBlindZone;   // 回滚标记与实例一致（vc96 P2：按旧值回滚）
            _builtGate = oldBuiltGate;
            _player = old;
            if (built is not null)
            {
                try { built.Release(); } catch { /* 新实例释放失败无害 */ } // 换新失败：释放新实例退回旧实例，防泄漏
            }
            // vc96 第2轮：BuildPlayer 可能已换代预取器（旧者退订+Reset、新者入账）——回滚即还原旧接线，
            // 否则旧内核工厂持退订后的旧预取器，预取静默断链
            var newPrefetcher = _prefetcher;
            if (!ReferenceEquals(newPrefetcher, oldPrefetcher))
            {
                if (newPrefetcher is { } np)
                {
                    np.FetchRequested -= ParallelPrefetchDataSource.HandleFetchRequested;
                    np.Reset();
                }
                if (oldPrefetcher is { } op)
                {
                    op.FetchRequested += ParallelPrefetchDataSource.HandleFetchRequested;
                }
                _prefetcher = oldPrefetcher;
            }
        }
    }

    private bool _blindZone; // 当前内核保留档：true=盲区（已播保留 60s）
    private BufferInventory.Mode _gateMode = BufferInventory.Default; // vc94 深水档：当前起播库存档（热备=StartFull，冷切=默认）
    private BufferInventory.Mode _builtGate = BufferInventory.Default; // vc94 深水档：已建内核的库存档（不一致即重建）
    private ParallelPrefetcher? _prefetcher; // vc94 并发拉流：预取器随内核装配（重建换代清账）
    private SourceTemperStore? _temperStore; // vc96 试探版：持久化源脾气账（随内核装配一次，跨重建复用）

    // —— vc94 低档起播账（老板 2026-11-26 拍板第 1 刀）——
    private const int LowGearPollMs = 500; // 升档轮询节拍
    private bool _lowGearActive;           // 低档持有中（钳在最低档）
    private long _lowGearSinceMs;          // 低档起持时刻（单调钟 Environment.TickCount64）
    private long? _deferredGovernorCeiling; // 低档期暂存的治理器钳制（升档时落）
    private bool _trackCapActive;          // 当前是否落有选轨钳（ResetLowGear 判据）

    /// <summary>
    /// 缓冲深水接线（vc53 fix56 思路在 ExoPlayer 内核原生落位）：
    /// - 起播门槛线=首帧 5 秒即出画（vc66 提速令），出画后继续填水到预取水位线；
    ///   卡后重入门槛 12 秒（#12 顺滑令保留）——首帧快、重入稳，两头都要；
    /// - 预取水位线=按设备内存自适应安全水位（BufferPolicy.MaxWatermarkSeconds：内存足 60s、低配 30-45s），
    ///   出画后内核后台继续把窗口填满——保满预取即此；
    /// - 水位读写下沉 Players 适配层口径：水位由内核 LoadControl 承担，会话层不掺内部。
    /// </summary>
    private static AndroidX.Media3.ExoPlayer.DefaultLoadControl BuildLoadControl(Android.Content.Context context, BufferInventory.Mode gateMode)
    {
        long totalMem = 0;
        try
        {
            if (context.GetSystemService(Android.Content.Context.ActivityService)
                is Android.App.ActivityManager am)
            {
                var mi = new Android.App.ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                totalMem = mi.TotalMem;
            }
        }
        catch { /* 内存探测失败按低配保守（BufferPolicy 未知内存=30s） */ }
        int watermarkMs = BufferPolicy.MaxWatermarkSeconds(totalMem) * 1000;
        int startupGateMs = (int)PrefetchCurve.StartupThreshold.TotalMilliseconds;
        // vc93 第 6 刀（老板 2026-11-26 拍板接线）：首帧出画门槛按库存档分档——
        // SilentRefetch（默认档）=1 秒出画（先出画后补库存，首帧不慢）；StartFull（回退档）=7 秒（出就流畅）。
        // vc94 深水档（老板 2026-11-26 拍板）：档位由会话层下发（热备攒流=StartFull 接管即深水，冷切=默认档）。
        // 卡后重入门槛仍 StartupThreshold 12 秒不动；首帧永远取库存中最老画面，门槛只管何时出画。
        int firstFrameGateMs = BufferInventory.FirstFrameOpenMs(gateMode);
        // 步进式 builder（消 CS8602 可空链，同构造器口径）：绑定 fluent 链挂在可空返回上会报空解引用
        var loadBuilder = new AndroidX.Media3.ExoPlayer.DefaultLoadControl.Builder();
        // vc66 提速令：首帧门槛与重入门槛分家——卡后重入仍 12 秒（出就得流畅，#12 意图保留）
        loadBuilder.SetBufferDurationsMs(
            watermarkMs, watermarkMs,
            firstFrameGateMs,
            startupGateMs);
        return loadBuilder.Build()!;
    }

    /// <summary>频道上下文：缓存键与整表行匹配名（nmtv/gxtv 按 name 定位），Play 前由壳层设置。</summary>
    public string? CurrentChannelKey { get; set; }
    public string? CurrentChannelName { get; set; }

    /// <summary>
    /// 内核诊断出口（埋点补充 2026-11-15）：解码器报错/丢帧/欠载等内核事件经壳层接 CrashNet 落运行台账
    /// （真机 logcat 不进自诊断，Console.Error 通道保留）；未接线为空操作。
    /// </summary>
    public static Action<string>? Reporter { get; set; }

    /// <summary>构造后补挂取流注入点（内核工厂场景：独享内核创建后挂本实例的解析器）。</summary>
    public ExoPlayerPlayer WithStreamResolver(Func<TaoLive.Model.SourceEndpoint, Task<string?>> resolver)
    {
        _streamResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    // —— 事件出口（契约语义，供会话层订阅）——

    public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
    public event EventHandler<PlayerErrorEventArgs>? OnError;
    public event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;

    /// <summary>vc66 视频故障信号（老板令「根因上屏」）：视频解码器报错/视频渲染器停用即触发——
    /// 壳层只出声守卫据此把零帧账直接判过期，快速递进自救，不等零帧窗硬等。仅诊断口径，不影响内核状态机。</summary>
    public event EventHandler<string>? OnVideoFault;

    internal void HandleVideoFault(string detail) => OnVideoFault?.Invoke(this, detail);

    public void Play(PlaybackParams params_)
    {
        ArgumentNullException.ThrowIfNull(params_);
        _playGeneration++; // 新一轮播放：作废旧代次迟到回调（闪退镜红②）
        _firstFrameRaisedThisPlay = false;
        ResetLowGear(); // vc94 低档起播：新一轮起播清低档账（钳制解除，ABR 自由选档，治理器经 PumpStealth 重下发）
        // 频道上下文自取（双实例热切前提：每会话独享内核，壳层不再代设）
        if (!string.IsNullOrEmpty(params_.Channel.Id)) CurrentChannelKey = params_.Channel.Id;
        if (!string.IsNullOrEmpty(params_.Channel.Name)) CurrentChannelName = params_.Channel.Name;
        if (_streamResolver is null)
        {
            StartMedia(params_.Endpoint.Url, params_.Channel);
            return;
        }
        // 取票口：异步换真实流，回到主线程再挂 MediaItem（ExoPlayer 要求在其创建线程调用）
        var channelKey = CurrentChannelKey;
        var channelName = CurrentChannelName;
        var pending = params_;
        var generation = _playGeneration;
        _ = Task.Run(async () =>
        {
            string? url = null;
            try { url = await _streamResolver(pending.Endpoint).ConfigureAwait(false); }
            catch { url = null; }
            _main.Post(() =>
            {
                // 代次校验（闪退镜红② 11-15）：resolver 迟到回调落在 Stop/Dispose/新一轮 Play 之后即弃，
                // 不对已让位或已释放的内核 StartMedia（防 IllegalStateException 与慢源覆盖后播流）
                if (generation != _playGeneration) return;
                if (string.IsNullOrEmpty(url))
                    HandleError("RESOLVE_FAILED", $"取流失败（{channelName ?? channelKey ?? "channel"}）");
                else
                    StartMedia(url, pending.Channel);
            });
        });
    }

    private void StartMedia(string url, TaoLive.Model.Channel? channel = null)
    {
        try
        {
            var uri = Android.Net.Uri.Parse(url);
            // 盲区档对齐（微卡修法①，Play 路径单点）：非 .m3u8 直流=盲区源（天然贴边赛跑），
            // 已播保留档切 60s；HLS=25s。EnsurePlayerMode 只在档位变化时重建内核（起播期零视觉代价）。
            // 同步与 resolver 异步两路都在主线程进入本方法（ExoPlayer 创建线程契约），重建合法。
            bool blindZone = !IsHls(uri);
            EnsurePlayerMode(blindZone);
            // 请求头纪律（vc53→vc100 修订）：频道表与备源表仍无自定义 UA/Referer，但内核 UA 不再走
            // Media3 默认（Dalvik）——vc100 起由 RacingHttpDataSource.CreateInner 统一挂
            // TicketResolver.ClientUserAgent（与取票同 UA，CDN 按票指纹放行；11-27 真机实锤 Dalvik UA 被 4xx）。
            // 将来某路源带校验需补 Referer 时，改在 ExoPlayerBuilder 构造期注入工厂，
            // 不在每次 Play 动态挂（IExoPlayer 绑定无 SetMediaSourceFactory，云闸已实锤）。
            var builder = new AndroidX.Media3.Common.MediaItem.Builder();
            builder.SetUri(uri);
            if (!blindZone) // vc73 审查清零 P2：按路径判 HLS，query 带 .m3u8 的非 HLS 不再误设 MIME
            {
                builder.SetMimeType(AndroidX.Media3.Common.MimeTypes.ApplicationM3u8);
                // 主刀·时间平移快启（老板 2026-11-25 拍板九件套定稿口径）：首帧取播放窗口内最老已播画面——
                // 起播定位挂超大落后目标，内核按清单窗口自钳到窗口最深处（窗口给多深就落后多深），
                // 最老分片早已落盘可即取，首帧不必等直播边缘分片产出；起播后 1× 恒速，落后深度恒定
                // （min/max 留未设=内核不做追边调速，「不追实时——保持恒定延迟稳定播」口径）。
                // 口径变更注明：原「播放落后」旋钮的起播定位语义（2026-11-16 22:5x 拍板，含 0=贴边缘播档）
                // 被本刀取代，HLS 一律挂时间平移定位；旋钮数值仅保留库存触发线联动（ProactiveInventoryFloor），
                // 与 goal.md 主刀口径冲突处以 goal.md 为准。
                var liveConfig = new MediaItemLiveConfiguration.Builder().Build()
                    ?? throw new InvalidOperationException("LiveConfiguration build returned null");
                liveConfig.TargetOffsetMs = TimeShiftStartMs;
                builder.SetLiveConfiguration(liveConfig);
            }
            var mediaItem = builder.Build() ?? throw new InvalidOperationException("MediaItem build returned null");
            EnsureWarmVideoSink(); // vc66 哑面预热：无真面起播（热备攒流）也把视频解码器初始化到位，热切接管零解码器冷启
            _player.SetMediaItem(mediaItem);
            _player.Prepare();
            _player.PlayWhenReady = true;
        }
        catch (Exception ex)
        {
            // 同步路径与异步同口径（闪退镜黄⑩ 11-15）：起播异常接 HandleError 交会话备源自救，不裸奔冒泡主线程
            HandleError("START_FAILED", $"起播失败：{ex.Message}");
        }
    }

    public void Pause()
    {
        _player.PlayWhenReady = false;
    }

    /// <summary>vc68 热备静音攒流：备台热切接管时恢复出声出画（PlayWhenReady=true，缓冲攒流不中断）。</summary>
    public void Resume()
    {
        _player.PlayWhenReady = true;
    }

    public void Stop()
    {
        // vc72 审查清零 P1：Stop 同 Dispose 口径作废在途 resolver 回调——否则让位会话的内核
        // 会被迟到的取流回调复活，旧台无声画但外放音频（vc68「退软件音频照放」残余通路）。
        _playGeneration++;
        _firstFrameRaisedThisPlay = false;
        ResetLowGear(); // vc94 低档起播：停机清低档账（同 Play 口径）
        _player.Stop();
        _player.ClearMediaItems();
    }

    /// <summary>接输出面：句柄为壳层 Android.Views.Surface 的 Java 对象指针。</summary>
    public void AttachSurface(IntPtr surfaceHandle)
    {
        _surfaceHandle = surfaceHandle;
        _hasSurface = surfaceHandle != IntPtr.Zero;
        if (_hasSurface)
        {
            ReleaseWarmupSink(); // 真面就位即退哑面：解码器保持热态，换面不换码（setSurface 廉价）
            // 壳层传入的是 Android.Views.Surface 的 Java 对象指针，需还原包装（壳层仍持有所有权，不转移）。
            // Media3 契约（官方 Javadoc 原文，vc73 审查清零核验定案）：所有权在壳层，Surface 销毁须壳层摘面（DetachSurface），内核不代释。
            try
            {
                var surface = Java.Lang.Object.GetObject<Android.Views.Surface>(surfaceHandle, JniHandleOwnership.DoNotTransfer);
                if (surface is not null)
                {
                    _player.SetVideoSurface(surface);
                }
            }
            catch (Exception ex)
            {
                // 死面挂接（闪退镜黄⑥ 11-15）：SurfaceDestroyed 后热切重挂不砸主线程，摘面待壳层重挂
                _hasSurface = false;
                _surfaceHandle = IntPtr.Zero;
                // 挂面失败出口（埋点补充 2026-11-15）：此前走 Console.Error 只进 logcat，自诊断对不上账——统一落运行台账
                Console.Error.WriteLine($"[ExoPlayer] attach surface failed: {ex.Message}");
                Reporter?.Invoke($"[内核挂面] 挂面失败（热切重挂遇死面）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// vc66 哑面预热（老板令「加快解码器渲染速度」）：无真面起播时挂内核官方哑面（PlaceholderSurface），
    /// 视频解码器随攒流初始化并解码——热备「无面起播」此前不解码视频，热切接管才现初始化解码器，
    /// 慢热全压在换台瞬间（音先画后的主要时延之一）。真面 AttachSurface 即退哑面，解码器不冷启。
    /// </summary>
    private void EnsureWarmVideoSink()
    {
        if (_hasSurface || _warmupSink is not null) return;
        try
        {
            _warmupSink = AndroidX.Media3.ExoPlayer.Video.PlaceholderSurface.NewInstance(_context, false);
            if (_warmupSink is { } sink) _player.SetVideoSurface(sink);
            Reporter?.Invoke("[内核预热] 无面起播挂哑面：视频解码器随攒流预热（vc66 提速令）");
        }
        catch (Exception ex)
        {
            _warmupSink = null;
            Reporter?.Invoke($"[内核预热] 哑面预热不可用，退回无面攒流：{ex.Message}");
        }
    }

    private void ReleaseWarmupSink()
    {
        if (_warmupSink is not { } sink) return;
        _warmupSink = null;
        try { _player.ClearVideoSurface(sink); } catch { /* 内核已释放等边界：释放自身即可 */ }
        sink.Release();
    }

    /// <summary>摘输出面：换源/换台/退出前必调，防黑屏花屏。重复 detach 无害。</summary>
    public void DetachSurface()
    {
        if (_hasSurface)
        {
            _player.ClearVideoSurface();
            _hasSurface = false;
            _surfaceHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 缓冲库存读数（IPlayer 契约，老板令 #24）：内核缓冲位置 − 播放位置=播放点之后的存货毫秒。
    /// 未就绪（TIME_UNSET/负数）、位置异常一律按 0=「无读数」上交，会话层不据此判趋势防误切。
    /// </summary>
    public long BufferedMs
    {
        get
        {
            try
            {
                long buffered = _player.BufferedPosition;
                long position = _player.CurrentPosition;
                if (buffered < 0 || position < 0) return 0; // TIME_UNSET=未就绪：按无读数
                return Math.Max(0, buffered - position);
            }
            catch
            {
                return 0; // 内核已释放等边界：按无读数，交既有卡流自救路径兜底
            }
        }
    }

    /// <summary>读数是否可靠（vc85 水位跟手令）：TIME_UNSET/位置异常/内核已释放=不可靠，
    /// 壳层据此保持上一拍水位显示，不把「读不出」当 0 闪跳归零。</summary>
    public bool BufferedMsKnown
    {
        get
        {
            try
            {
                long buffered = _player.BufferedPosition;
                long position = _player.CurrentPosition;
                return buffered >= 0 && position >= 0;
            }
            catch
            {
                return false; // 内核已释放等边界：按不可靠，壳层保上一拍
            }
        }
    }

    /// <summary>
    /// 回退跳原语（vc85 蓝本三原语之一，微卡修法 10 秒口径）：播放点退回 backMs 接着播，
    /// 断掉盲区直流源「贴边赛跑」链条。SeekTo 须在内核创建线程（主线程）调用——_main.Post 走队列；
    /// 位置钳 0（越界回退按起点续），内核已释放或位置未就绪静默弃（调用方回退失败即照旧走自救路径）。
    /// </summary>
    public void SeekBack(long backMs)
    {
        if (_released || backMs <= 0) return;
        _main.Post(() =>
        {
            if (_released) return; // 在途队列回调对上释放即弃（vc74 P3⑧ 同口径）
            try
            {
                long pos = _player.CurrentPosition;
                if (pos <= 0) return; // 未就绪：无可退存量，交既有卡流自救
                long target = Math.Max(0, pos - backMs);
                _player.SeekTo(target);
                Reporter?.Invoke($"[内核] 回退跳 {backMs}ms：{pos}→{target}（贴边赛跑断链，微卡修法）");
            }
            catch { /* 内核重建/释放等边界：回退失败无害，交既有卡流自救路径 */ }
        });
    }

    /// <summary>分片级码率自适应（六项优化④，vc85 蓝本 v2.5）：链路健康裕度钳制选轨视频码率上限——
    /// 主线程进（TrackSelectionParameters 内核线程契约）；int.MaxValue=解除钳制。直流盲区源无多档清单，
    /// 内核对单档流钳制无害（选轨无变体可选=原样）；已释放静默弃。
    /// vc94 低档起播：低档持有期内治理器钳制暂存不落（<see cref="_deferredGovernorCeiling"/>），
    /// 升档时按暂存值落钳——低档期两套钳制不打架，升档后治理口径无缝接管。</summary>
    public void ApplyBitrateCeiling(long? maxVideoBitrate)
    {
        if (_released) return;
        if (_lowGearActive)
        {
            _deferredGovernorCeiling = maxVideoBitrate; // 低档期暂存，升档时落钳
            return;
        }
        ApplyTrackCapInternal(maxVideoBitrate);
    }

    /// <summary>起播库存档下发（vc94 深水档，IPlayer 契约）：热备攒流切 StartFull（接管即深水），
    /// 冷切回默认档。只记账不重建——重建统一走 StartMedia→EnsurePlayerMode（起播期零视觉代价）。</summary>
    public void SetStartupGateMode(BufferInventory.Mode mode) => _gateMode = mode;

    /// <summary>低档起播回调（vc94 第 1 刀，老板拍板）：清单发现多档即钳到最低档起播——
    /// 主线程落钳（TrackSelectionParameters 契约），起 500ms 轮询等升档条件
    /// （<see cref="StartupGear.ShouldLift"/>：水位 15 秒或持有 20 秒）。
    /// 单档/无带宽信息不进本回调（防黑屏口径在数据源层把住）。</summary>
    private void OnMinVariantObserved(long minBandwidth)
    {
        if (_released) return;
        _main.Post(() =>
        {
            if (_released || _lowGearActive) return; // 已在低档：不重复落钳
            _lowGearActive = true;
            _lowGearSinceMs = Environment.TickCount64;
            ApplyTrackCapInternal(minBandwidth);
            Reporter?.Invoke($"[低档起播] 清单多档，钳最低档 {minBandwidth}bps 起播（水位 15s 或持有 20s 升档）");
            PollLowGear();
        });
    }

    /// <summary>低档轮询（500ms 一拍，主线程）：升档条件到即落钳解除，不到继续排下一拍。</summary>
    private void PollLowGear()
    {
        if (_released || !_lowGearActive) return;
        long heldMs = Environment.TickCount64 - _lowGearSinceMs;
        if (StartupGear.ShouldLift(BufferedMs, heldMs))
        {
            _lowGearActive = false;
            var liftTo = _deferredGovernorCeiling;
            _deferredGovernorCeiling = null;
            ApplyTrackCapInternal(liftTo); // null=解除钳制（治理器无暂存时自由选档）
            Reporter?.Invoke($"[低档起播] 升档：水位/持时达标（持有 {heldMs}ms），{(liftTo is { } v ? $"落治理钳 {v}bps" : "解除钳制")}");
            return;
        }
        _main.PostDelayed(PollLowGear, LowGearPollMs);
    }

    /// <summary>低档账清零（Play/Stop 口径）：钳制解除、暂存作废、轮询自然停（下一拍查旗即弃）。</summary>
    private void ResetLowGear()
    {
        if (!_lowGearActive && !_trackCapActive) return;
        _lowGearActive = false;
        _deferredGovernorCeiling = null;
        ApplyTrackCapInternal(null);
    }

    /// <summary>选轨钳制单点（主线程）：null=解除。_trackCapActive 记账供 ResetLowGear 判有无钳可解。</summary>
    private void ApplyTrackCapInternal(long? maxVideoBitrate)
    {
        if (_released) return;
        _main.Post(() =>
        {
            if (_released) return; // 在途队列回调对上释放即弃（vc74 P3⑧ 同口径）
            try
            {
                var builder = _player.TrackSelectionParameters?.BuildUpon();
                if (builder is null) return; // 绑定面空读数（未就绪）：静默弃
                builder.SetMaxVideoBitrate(maxVideoBitrate is { } cap
                    ? (int)Math.Min(cap, int.MaxValue)
                    : int.MaxValue);
                var built = builder.Build();
                if (built is not null) _player.TrackSelectionParameters = built;
                _trackCapActive = maxVideoBitrate is not null;
            }
            catch (Exception ex)
            {
                Reporter?.Invoke($"[码率自适应] 钳制下发失败忽略：{ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_released) return; // vc96 P3：不可重入——重复 Dispose 不再二次 Release 内核
        _playGeneration++; // 释放后作废一切在途回调（闪退镜红②）
        _released = true;  // vc74 P3⑧：监听摘除→Release 与内核/渲染线程在途回调有窗口，回调桥入口查旗收窄窗口（vc75 定案：查旗到执行间非原子，Dispose 可在窗内完成——诊断噪声由 Reporter 有界队列与会话守卫兜底，口径是收窄非消除）
        ReleaseWarmupSink();
        _player.RemoveListener(_forwarder);
        _player.RemoveAnalyticsListener(_diagForwarder);
        _player.ClearVideoFrameMetadataListener(null);
        _frameForwarder.Dispose();
        _player.Release();
        // vc96 P2：清静态接线——静态属性若仍指向本实例的预取器/观察，置空防悬空引用
        if (RacingDataSourceFactory.Prefetcher is { } pf && ReferenceEquals(pf, _prefetcher))
        {
            RacingDataSourceFactory.Prefetcher = null;
        }
        if (RacingDataSourceFactory.MinVariantBitrateObserver is { } obs
            && ReferenceEquals(obs.Target, this))
        {
            RacingDataSourceFactory.MinVariantBitrateObserver = null;
        }
        if (_prefetcher is { } ownedPf)
        {
            ownedPf.FetchRequested -= ParallelPrefetchDataSource.HandleFetchRequested;
            ownedPf.Reset();
            _prefetcher = null;
        }
    }

    private void HandleBufferingStarted()
    {
        OnBufferingStart?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });
    }

    private void HandleReady()
    {
        OnBufferingEnd?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });
        if (!_firstFrameRaisedThisPlay)
        {
            _firstFrameRaisedThisPlay = true;
            OnFirstFrame?.Invoke(this, new PlayerFirstFrameEventArgs { AtUtc = DateTimeOffset.UtcNow });
        }
    }

    private void HandleError(string code, string message)
    {
        OnError?.Invoke(this, new PlayerErrorEventArgs { Code = code, Message = message });
    }

    /// <summary>
    /// 帧回调上报（10Hz 节流，方案书 v76）：渲染线程高频回调按 100ms 拍合并，
    /// 订阅方（壳层埋点）拿到的节拍即采样点；TimeSpan 判断免锁（单渲染线程顺序回调）。
    /// vc74 P3⑧：释放后渲染线程在途回调即弃。
    /// </summary>
    internal void HandleFrameAboutToBeRendered()
    {
        if (_released) return;
        var now = DateTimeOffset.UtcNow;
        if (_lastTickRaised != default && now - _lastTickRaised < TimeSpan.FromMilliseconds(100)) return;
        _lastTickRaised = now;
        OnFrameTick?.Invoke(this, new PlayerFrameTickEventArgs { AtUtc = now });
    }
    private DateTimeOffset _lastTickRaised;

    /// <summary>帧元数据监听桥：Media3 渲染线程回调 → 契约 OnFrameTick。</summary>
    private sealed class FrameTickForwarder : Java.Lang.Object, AndroidX.Media3.ExoPlayer.Video.IVideoFrameMetadataListener
    {
        private readonly ExoPlayerPlayer _owner;
        public FrameTickForwarder(ExoPlayerPlayer owner) : base() => _owner = owner;

        public void OnVideoFrameAboutToBeRendered(long presentationTimeUs, long eventTimeMs,
            AndroidX.Media3.Common.Format? format, Android.Media.MediaFormat? mediaFormat)
            => _owner.HandleFrameAboutToBeRendered();
    }

    /// <summary>
    /// 内核诊断监听桥（埋点补充 2026-11-15）：Media3 AnalyticsListener 白名单回调 → Reporter 落运行台账。
    /// 覆盖面（#10 白屏只出声 / #9 卡帧的最直接证据链）：
    /// - [解码] 视频/音频解码器报错（codec error）；
    /// - [解码] 视频丢帧累计（OnDroppedVideoFrames，偶发不记、成批才记，控量）；
    /// - [解码] 视频/音频渲染器停用（OnVideoDisabled/OnAudioDisabled，带解码器计数终账）；
    /// - [解码] 音频欠载（OnAudioUnderrun，「只出声卡顿」证据）；
    /// - [带宽] 估速变化（节流：估速较上次记账值倍变或距上次 ≥60s 才记，防刷屏）。
    /// 回调签名按 1.11.1 绑定程序集反射核对（AnalyticsListenerEventTime/DecoderCounters 属性名 DroppedBufferCount 等）。
    /// </summary>
    private sealed class DiagAnalyticsForwarder : Java.Lang.Object, AndroidX.Media3.ExoPlayer.Analytics.IAnalyticsListener
    {
        private readonly ExoPlayerPlayer _owner;
        public DiagAnalyticsForwarder(ExoPlayerPlayer owner) : base() => _owner = owner;

        private long _lastBwLogMs = -1;
        private long _lastBwBitrate = -1;
        private int _droppedAccum; // vc71：丢帧 10 秒累积落账（≥5 帧过滤会吞掉慢解的零星丢帧，vc70「零丢帧行」盲区）
        private long _lastDropLogMs = -1;

        private string Ch() => _owner.CurrentChannelName ?? _owner.CurrentChannelKey ?? "?";

        private void Report(string message) => ExoPlayerPlayer.Reporter?.Invoke(message);

        // vc74 P3⑧：释放旗——Dispose 摘听→Release 与内核/渲染线程在途回调有窗口，
        // 各回调入口查旗收窄窗口（查旗到执行间非原子，vc75 定案：诊断账噪声由
        // Reporter 有界队列兜底、故障递进由会话 _active/状态守卫兜底，不再向已释放会话层递进）。
        private bool Alive => !_owner._released;

        public void OnVideoCodecError(AndroidX.Media3.ExoPlayer.Analytics.AnalyticsListenerEventTime? eventTime,
            Java.Lang.Exception? videoCodecError)
        {
            if (!Alive) return;
            Report($"[解码] 视频解码器报错（{Ch()}）：{videoCodecError?.Message}");
            _owner.HandleVideoFault($"视频解码器报错：{videoCodecError?.Message}"); // vc66：只出声守卫快速递进信号
        }

        public void OnAudioCodecError(AndroidX.Media3.ExoPlayer.Analytics.AnalyticsListenerEventTime? eventTime,
            Exception audioCodecError)
        {
            if (!Alive) return;
            Report($"[解码] 音频解码器报错（{Ch()}）：{audioCodecError?.Message}");
        }

        public void OnDroppedVideoFrames(AndroidX.Media3.ExoPlayer.Analytics.AnalyticsListenerEventTime? eventTime,
            int droppedFrames, long elapsedMs)
        {
            if (!Alive) return;
            // vc71：不再按单次 ≥5 帧过滤（慢解瓶颈每账期只丢 1~2 帧会被吞，2.0.13 真机复发旧病时证据缺位）。
            // 改 10 秒累积窗口落账：攒到的帧全量上报，解码慢无处可藏。
            _droppedAccum += Math.Max(0, droppedFrames);
            var now = Android.OS.SystemClock.ElapsedRealtime();
            if (_lastDropLogMs >= 0 && now - _lastDropLogMs < 10_000) return;
            FlushDroppedAccum(true); // 10s 窗到点整账出
            _lastDropLogMs = now;
        }

        public void OnVideoDisabled(AndroidX.Media3.ExoPlayer.Analytics.AnalyticsListenerEventTime? eventTime,
            AndroidX.Media3.ExoPlayer.DecoderCounters? decoderCounters)
        {
            if (!Alive) return;
            // vc73 审查清零 P2：解码器停用即尾巴出账——最后一批丢帧若之后不再有丢帧事件，
            // 10 秒窗账会滞留到下一台/下一次出账才出现，证据错台错线；停用是账期终点，立即落账。
            FlushDroppedAccum(false);
            if (decoderCounters is null) return;
            Report($"[解码] 视频渲染器停用（{Ch()}）：缓冲丢帧 {decoderCounters.DroppedBufferCount}"
                + $" · 跳至关键帧 {decoderCounters.DroppedToKeyframeCount} · 丢输入帧 {decoderCounters.DroppedInputBufferCount}");
        }

        /// <summary>丢帧账出账（vc73 审查清零 P2 提取共用）：攒到帧>0 即上报后清零。
        /// windowFull=true 为 10 秒窗到点整账；false 为解码器停用的尾账补出（窗未满，防滞留错台）。</summary>
        private void FlushDroppedAccum(bool windowFull)
        {
            if (_droppedAccum > 0)
                Report($"[解码] 视频丢帧累计 {_droppedAccum} 帧（{Ch()}，>0 即解码跟不上或渲染欠载，慢帧证据，"
                    + (windowFull ? "10s 窗整账）" : "10s 窗未满停用补出尾账）"));
            _droppedAccum = 0;
        }

        public void OnAudioUnderrun(AndroidX.Media3.ExoPlayer.Analytics.AnalyticsListenerEventTime? eventTime,
            int bufferSize, long bufferSizeMs, long elapsedSinceLastFeedMs)
        {
            if (!Alive) return;
            Report($"[解码] 音频欠载 {elapsedSinceLastFeedMs}ms（{Ch()}，buffer {bufferSize}B/{bufferSizeMs}ms，只出声卡顿证据）");
        }

        public void OnBandwidthEstimate(AndroidX.Media3.ExoPlayer.Analytics.AnalyticsListenerEventTime? eventTime,
            int totalLoadTimeMs, long totalBytesLoaded, long bitrateEstimate)
        {
            if (!Alive) return;
            var now = Android.OS.SystemClock.ElapsedRealtime();
            bool bigMove = _lastBwBitrate > 0
                && (bitrateEstimate >= _lastBwBitrate * 2 || bitrateEstimate <= _lastBwBitrate / 2);
            bool intervalDue = now - _lastBwLogMs >= 60_000;
            if (_lastBwLogMs >= 0 && !bigMove && !intervalDue) return;
            _lastBwLogMs = now;
            _lastBwBitrate = bitrateEstimate;
            Report($"[带宽] 估速 {bitrateEstimate / 1000} kbps（{Ch()}，累计 {totalBytesLoaded / 1024}KB/{totalLoadTimeMs}ms）"); // vc67 修：原 /8/1000 实为 kB/s 标 kbps，差 8 倍口径
        }
    }

    /// <summary>HLS 判据（vc73 审查清零 P2 修）：按 URI 路径判断，不再对整 URL 做子串匹配——
    /// query 带 ".m3u8" 的非 HLS 地址（如带 .m3u8 参数的防盗链签名）此前会被误设 HLS MIME。
    /// 参数收 nullable：不同版本安卓 binding 对 Uri.Parse 可空注解不一致（云闸 11-16 实锤），
    /// 收 nullable + 空安全判定，两侧编译口径都过。</summary>
    private static bool IsHls(Android.Net.Uri? uri) =>
        (uri?.Path ?? "").EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 内核事件桥：实现 Media3 Player.Listener 绑定接口（IPlayerListener，全默认方法，
    /// 只重写契约需要的两个回调；OnPlaybackStateChanged 映射 Buffering/Ready/Ended 三态，
    /// Ended 视同卡流上报（R2-72 发现即改），其余内核态按契约未定义语义不映射）。
    /// </summary>
    private sealed class Forwarder : Java.Lang.Object, AndroidX.Media3.Common.IPlayerListener
    {
        private readonly ExoPlayerPlayer _owner;

        public Forwarder(ExoPlayerPlayer owner) : base() => _owner = owner;

        public void OnPlaybackStateChanged(int playbackState)
        {
            if (_owner._released) return; // vc74 P3⑧：释放后在途回调即弃
            switch (playbackState)
            {
                case AndroidX.Media3.Common.BasePlayer.InterfaceConsts.StateBuffering:
                    _owner.HandleBufferingStarted();
                    break;
                case AndroidX.Media3.Common.BasePlayer.InterfaceConsts.StateReady:
                    _owner.HandleReady();
                    break;
                case AndroidX.Media3.Common.BasePlayer.InterfaceConsts.StateEnded:
                    // R2-72 发现即改：直播流被服务端掐断时内核进 ENDED，此前未映射——
                    // 无错误回调、无卡流事件，状态机停在 Playing，画面冻结且看门狗/备源自救永不触发（冻画黑洞）。
                    // 直播语义 ENDED=流断：视同卡流上报，交会话层卡流路径（NotifyStalled→Reconnecting→超时切备源）。
                    // 用户主动 Stop 不产生 ENDED（ExoPlayer stop 保持状态）； stray 事件由 NotifyStalled 的 Playing 守卫挡住。
                    _owner.HandleBufferingStarted();
                    break;
            }
        }

        public void OnPlayerError(AndroidX.Media3.Common.PlaybackException? error)
        {
            if (_owner._released) return; // vc74 P3⑧：释放后在途回调即弃
            var code = error?.ErrorCodeName ?? "UNKNOWN";
            // 真机诊断口径（11-15 老板镜）：诊断页只看到「player error」查不到因，
            // 补内核原始码+根因链，一键清理页可见（ErrorCodeName/Cause 链全落）。
            var cause = error?.Cause?.Message;
            var message = error?.Message is { } m
                ? (cause is { } c && c != m ? $"{m} | cause={c}" : m)
                : "unknown playback error";
            _owner.HandleError(code, message);
        }
    }
}
