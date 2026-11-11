using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AllPlayer.BLL;
using AllPlayer.DAL;
using AllPlayer.Model;
using LibVLCSharp.Shared;

namespace AllPlayer.UI.Android.Shared
{
    /// <summary>
    /// 电视版与手机版共用的播放宿主: 负责播放内核初始化、播放列表、
    /// 断点续播、磁力边下边播、网络流录制、字幕/音轨切换。
    /// 两个 UI 项目各自只负责界面与交互, 全部播放逻辑在这里。
    /// v3.22.0: 画面出口抽象为 IVideoCarrierView (纹理/表面可插拔)。
    /// </summary>
    public class VideoHost : IDisposable
    {
        private LibVLC? _libVLC;
        private MediaPlayer? _player;
        private IVideoCarrierView? _videoCarrier;
        // 安卓上 libVLC 的事件走主线程, 内核调用若也在主线程会互相卡死: 全部经串行队列在后台顺序执行
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _playerQueue = new();

        public VideoHost()
        {
            _ = Task.Run(() =>
            {
                foreach (var act in _playerQueue.GetConsumingEnumerable())
                {
                    try { act(); }
                    catch (Exception ex) { LogLine("队列异常: " + ex); }
                }
            });
        }

        private void Enqueue(Action act) => _playerQueue.Add(act);

        private void LogLine(string msg)
        {
            try
            {
                if (_logPath == null) return;
                global::System.IO.File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\n");
            }
            catch { }
        }
        private readonly List<VideoItem> _playlist = new List<VideoItem>();
        private int _index = -1;
        private MagnetStreamService? _magnet;
        private CancellationTokenSource? _magnetCts;
        private HlsStreamService? _hls;         // v3.24: m3u8 边播边存服务 (同一套入口与开关)
        private CancellationTokenSource? _hlsCts;
        private string? _currentPath;       // 正在播的地址 (文件路径或本地流 URL)
        private string? _streamKeyOverride; // v3.24.2: m3u8 播放时记录源清单地址, 续播/最近播放记账用它 (本地清单地址每次会话都变, 拿来记账等于永远续不上)
        private volatile bool _decodeErrSeen;   // 捕获到硬解失败迹象
        private volatile bool _swFallbackTried; // 软解兜底每会话只试一次
        private string? _recordTsPath;      // 录制中的 TS 落盘位置
        private bool _recording;
        private bool _noHistoryOnce;   // v3.13.1: 外部点播 (content:// 临时拷贝) 单次豁免——不进最近播放、不留续播记录
        private double? _pendingResumeMs;
        private long _pendingCoverStartMs;   // v3.25.8 封面帧起播: 预览取帧时间点 (毫秒), 无续播记录时起播即跳过去
        private string? _logPath;
        // v3.21.2: 回前台重建内核时, 画面比例/倍速在 vout 未就绪前设置会被丢弃——
        // 存下来, 等内核真正开始播放(Playing)后由事件补设一次
        private string? _pendingAspect;
        private float _pendingRate;

        /// <summary>状态文字 (给界面显示)</summary>
        public event Action<string>? StatusChanged;
        /// <summary>播放结束 (自动切下一曲)</summary>
        public event Action? PlaybackEnded;
        /// <summary>进度变化 (当前位置毫秒, 总时长毫秒)</summary>
        public event Action<long, long>? TimeUpdated;
        /// <summary>当前播放的视频变了 (新路径, 用于刷新界面标题)</summary>
        public event Action<string?>? CurrentPathChanged;

        // v3.22.6 (老板反馈播放中仍亮播放键): 内核真正起播/真正暂停的回执——
        // Play/Pause 是异步入队, UI 在点击瞬间读状态会读到旧值, 必须靠这两个回执补刷
        public event Action? PlaybackStarted;
        public event Action? PlaybackPaused;

        // v3.23 P0-1 断流自救: 重试 3 次用尽后通知界面给明确文案 (点播放可手动重试)
        public event Action? StreamRetryGaveUp;

        // v3.23 P0-1: 断流自救状态——停滞探针 + 退避重试 (仅远程网络流, 手动暂停永不触发)
        private volatile bool _manualPaused;          // 手动/系统暂停中 (Paused 回执置位, Playing 回执清除)
        private long _lastProgressTick;               // 最近一次进度回执时刻 (TickCount64)
        private long _lastReportedTime;               // 最近一次进度值 (毫秒), 判断是否真在前进
        private int _retryAttempts;                   // 本次会话已自动重试次数 (统一经 Volatile 原语读写)
        private volatile bool _stallWatchRunning;     // 停滞探针循环在跑
        private volatile bool _retryInFlight;         // 一次重试进行中 (延时+重播), 期间探针不再叠加
        private volatile bool _gaveUpReported;        // v3.23 双检修复: 用尽通知只发一次, 用户换片或进度恢复才复位
        private readonly object _retryGate = new object();
        private readonly StreamRetryPolicy _retryPolicy = new StreamRetryPolicy();
        private long _pendingResumeSeekMs = long.MinValue; // v3.25.3: >0 = 待跳续播点 (时间回执或 3 秒兜底消费一次即清)
        private volatile bool _loopReplay;                 // v3.25.4: 循环重播一次性标志 (PlayPath 消费, 不读续播)

        /// <summary>看一眼下一曲的名字 (自动连播提示用, v3.23 P1-1); 没有下一曲返回 null</summary>
        public string? PeekNextName()
        {
            var next = _index + 1;
            return next >= 0 && next < _playlist.Count ? Path.GetFileName(_playlist[next].Path) : null;
        }

        /// <summary>进程级网络可用性 (查不到当有网, 不误报)</summary>
        private bool HasNetwork()
        {
            try
            {
                var ctx = _videoCarrier?.HostContext ?? global::Android.App.Application.Context;
                var cm = (global::Android.Net.ConnectivityManager)ctx.GetSystemService(global::Android.Content.Context.ConnectivityService)!;
                var caps = cm.GetNetworkCapabilities(cm.ActiveNetwork);
                return caps != null && caps.HasCapability(global::Android.Net.NetCapability.Internet);
            }
            catch { return true; }
        }

        /// <summary>进度回执记账: 真在前进 (>5 秒增量) 即清空重试计数 (自救成功后回到正常)。
        /// v3.23 双检修复: 进度恢复同时清「用尽已报」标志, 同一流恢复后重试能力随之恢复。</summary>
        private void NoteProgress(long timeMs)
        {
            _lastProgressTick = Environment.TickCount64;
            if (timeMs > _lastReportedTime + 5000)
            {
                Volatile.Write(ref _retryAttempts, 0);
                _gaveUpReported = false;
            }
            _lastReportedTime = timeMs;
        }

        /// <summary>停滞探针: 播放远程流时每 2 秒查一次, 15 秒无进度触发自救</summary>
        private void StartStallWatch()
        {
            if (_stallWatchRunning) return;
            if (!StreamRetryPolicy.IsNetworkPath(_currentPath)) return;
            _stallWatchRunning = true;
            _lastProgressTick = Environment.TickCount64;
            _ = Task.Run(async () =>
            {
                while (_stallWatchRunning)
                {
                    try { await Task.Delay(2000); } catch { break; }
                    try
                    {
                        if (!_stallWatchRunning || _retryInFlight || _manualPaused) continue;
                        var path = _currentPath;
                        if (path == null || !StreamRetryPolicy.IsNetworkPath(path))
                        {
                            // v3.23 双检修复: 换到本地文件/停止后探针退场, 不再常驻空转
                            StopStallWatch();
                            break;
                        }
                        if (!AppSettings.Current.StreamAutoRetry) continue;
                        if (Environment.TickCount64 - Volatile.Read(ref _lastProgressTick) > StreamRetryPolicy.StallThresholdMs)
                            TryStreamRetry("stall");
                    }
                    catch { }
                }
            });
        }

        public void StopStallWatch() => _stallWatchRunning = false;

        /// <summary>断流自救: 按策略退避重播当前远程流; 用尽则通知界面。手动暂停/本地文件/开关关闭均直接返回。</summary>
        private void TryStreamRetry(string reason)
        {
            lock (_retryGate)
            {
                var path = _currentPath;
                if (path == null || _player == null || _manualPaused || _retryInFlight) return;
                if (!StreamRetryPolicy.IsNetworkPath(path)) return;
                if (!AppSettings.Current.StreamAutoRetry) return;
                if (!_retryPolicy.ShouldRetry(Volatile.Read(ref _retryAttempts)))
                {
                    // v3.23 双检修复: 用尽只报一次——探针每 2 秒都会再进来, 不加闸会连环弹提示
                    if (!_gaveUpReported)
                    {
                        _gaveUpReported = true;
                        LogLine("断流自救: 重试次数用尽, 交还用户");
                        StreamRetryGaveUp?.Invoke();
                    }
                    return;
                }

                _retryInFlight = true;
                var attempts = Volatile.Read(ref _retryAttempts);
                Volatile.Write(ref _retryAttempts, attempts + 1);
                var delay = _retryPolicy.NextDelayMs(attempts);
                LogLine($"断流自救: 原因={reason} 第 {attempts + 1}/{StreamRetryPolicy.MaxAttempts} 次, {delay}ms 后重播");
                StatusChanged?.Invoke($"网络流中断, {delay / 1000} 秒后自动重试 (第 {attempts + 1}/{StreamRetryPolicy.MaxAttempts} 次)");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay);
                        try { SaveResume(); } catch { }
                        PlayPath(path, fromStreamRetry: true);
                    }
                    catch { }
                    finally
                    {
                        _lastProgressTick = Environment.TickCount64;
                        _retryInFlight = false;
                    }
                });
            }
        }

        public MediaPlayer? Player => _player;
        public string? CurrentPath => _currentPath;

        /// <summary>
        /// 画面出口重挂 (v3.11 修「有声音没画面」): 播放页显示或回到前台时把播放器重新接到当前画面出口。
        /// 面板先隐藏后显示时 Surface 可能还没挂稳就开始出流, 表现就是声音正常画面黑着;
        /// 重设一次 MediaPlayer 出口, 载体会把现成的画面层重新交给播放器。
        /// </summary>
        public void ReattachVideoView()
        {
            if (_videoCarrier == null || _player == null) return;
            try { _videoCarrier.MediaPlayer = _player; } catch { }
        }

        /// <summary>v3.18 修「一加 15T 切后台回来黑屏」: 光重设出口不重建 (重设同一播放器是空操作),
        /// 把画面出口视图从父容器摘下再原位装回, 逼系统重走 surface 创建链, 然后重挂出口。
        /// v3.22.0: 纹理载体退后台不回收, 回前台走轻路径, 本方法仅表面载体回退模式下仍会用到。</summary>
        public void ForceSurfaceRecreate()
        {
            if (_videoCarrier == null) return;
            try
            {
                var v = _videoCarrier.AsView;
                if (v.Parent is global::Android.Views.ViewGroup p)
                {
                    int idx = p.IndexOfChild(v);
                    p.RemoveView(v);
                    p.AddView(v, idx);
                }
                ReattachVideoView();
            }
            catch { }
        }

        /// <summary>主题重绘等场景复用已有播放内核, 只把画面出口换成新视图, 不重建 LibVLC/MediaPlayer。
        /// v3.22.1 (0004 评审 P2-7): 先把新载体挂上新内核, 再显式给旧载体解线释放旧 AWindow——
        /// 顺序与 P1-4 交接原则一致 (先挂新再释放旧), 旧载体不残留悬挂的播放内核引用。</summary>
        public void AttachView(IVideoCarrierView carrier)
        {
            if (_videoCarrier != null && !ReferenceEquals(_videoCarrier, carrier))
            {
                var old = _videoCarrier;
                _videoCarrier = carrier;
                ReattachVideoView();
                try { old.MediaPlayer = null; } catch { }   // P2-7: 旧载体显式解线, 内部 AWindow 随之释放
                return;
            }
            _videoCarrier = carrier;
            ReattachVideoView();
        }

        public bool IsPlaying => _player?.IsPlaying ?? false;

        /// <summary>初始化播放内核 (在 Activity.OnCreate 调用一次)</summary>
        public void Init(IVideoCarrierView carrier)
        {
            _videoCarrier = carrier;
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC(enableDebugLogs: true, "--no-video-title-show", "--verbose=2");
                _player = new MediaPlayer(_libVLC) { EnableHardwareDecoding = true };
                _videoCarrier!.MediaPlayer = _player;   // 关键: 把画面出口接到播放器 (载体内部挂屏)
                try
                {
                    var logsDir = new global::Java.IO.File(_videoCarrier.HostContext.GetExternalFilesDir(null), "logs");
                    logsDir.Mkdirs();
                    _logPath = global::System.IO.Path.Combine(logsDir.AbsolutePath!, "vlc.log");
                    if (global::System.IO.File.Exists(_logPath) && new global::System.IO.FileInfo(_logPath).Length > 300000)
                        global::System.IO.File.Delete(_logPath);
                    global::System.IO.File.AppendAllText(_logPath, "\n=== 启动 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\n");
                    _libVLC.Log += (s2, e2) =>
                    {
                        try
                        {
                            var lvl = e2.Level.ToString();
                            if (!lvl.Contains("Warning") && !lvl.Contains("Error")) return;
                            var _m = e2.FormattedLog ?? "";
                            if (_m.Contains("Could not open decoder") || _m.Contains("no suitable decoder") || _m.Contains("Failed to create video decoder") || (_m.Contains("decoder") && _m.Contains("Error")))
                                _decodeErrSeen = true;
                            global::System.IO.File.AppendAllText(_logPath!, DateTime.Now.ToString("HH:mm:ss") + " [" + e2.Level + "] " + e2.FormattedLog + "\n");
                        }
                        catch { }
                    };
                }
                catch { }
            }
            catch (Exception ex)
            {
                _videoCarrier.AsView.Post(() => Toast.MakeText(_videoCarrier.HostContext, "播放内核初始化失败: " + ex.Message, ToastLength.Long)!.Show());
                StatusChanged?.Invoke("播放内核初始化失败");
                return;
            }
            _player = CreateAndWirePlayer();
            _videoCarrier!.MediaPlayer = _player;   // 关键: 把画面出口接到播放器 (载体内部挂屏)
        }

        /// <summary>新建播放内核并挂好全部事件 (v3.20.7 抽出: 初始化与回前台重建内核共用)。
        /// LibVLC 事件委托挂在实例上, 新建实例必须重新挂, 否则重建后事件全部失联。</summary>
        private MediaPlayer CreateAndWirePlayer()
        {
            var p = new MediaPlayer(_libVLC!) { EnableHardwareDecoding = true };
            p.EndReached += (s, e) =>
            {
                if (_reopenGuard) { LogLine("原位重启停播, 忽略结束事件"); return; }   // v3.20.6: 换源 Stop 触发的结束不换片
                StopStallWatch();   // v3.23: 播完收探针, 不给下曲误报
                SaveResume();
                if (LoopEnabled && _currentPath != null)
                {
                    var path = _currentPath;
                    _loopReplay = true;   // v3.25.4 清零: 循环重播从头发, 不跳续播点 (原逻辑每次重播都跳回上次位置, 片头永远看不到)
                    _ = Task.Run(() => { try { PlayPath(path); } catch { } });
                }
                else
                    PlaybackEnded?.Invoke();
            };
            p.TimeChanged += (s, e) =>
            {
                // v3.25.3 清零: 续播跳转改首帧回执触发 (原固定 1.2 秒盲等会先播错帧再跳回), 3 秒兜底防首帧迟迟不来
                var pending = Interlocked.Read(ref _pendingResumeSeekMs);
                if (pending > 0) TryFireResumeSeek(pending);
                NoteProgress(e.Time); TimeUpdated?.Invoke(e.Time, p.Length);
            };
            p.EncounteredError += (s, e) =>
            {
                LogLine("内核报错: 无法解码或无法访问");
                StatusChanged?.Invoke(PlaybackFailureText.For(_currentPath, HasNetwork()));
                TryStreamRetry("error");   // v3.23: 远程流报错先自救, 用尽/不适用时内部只发用尽通知
            };
            p.Playing += (s, e) =>
            {
                LogLine("内核开始播放"); StatusChanged?.Invoke(""); StartHwFallbackWatch();
                _manualPaused = false;                    // v3.23: 真正起播回执 = 暂停态清除
                StartStallWatch();                        // v3.23: 远程流起播即挂停滞探针
                try { PlaybackStarted?.Invoke(); } catch (Exception ex) { LogLine("起播回执异常: " + ex.Message); }
                // v3.21.2: 起播即画面输出就绪的信号, 此刻补设重建前保留的比例/倍速, 避免过早设置被丢弃
                if (_pendingAspect != null || _pendingRate > 0)
                {
                    var a = _pendingAspect; var r = _pendingRate; _pendingAspect = null; _pendingRate = 0;
                    LogLine($"起播补设画面状态: aspect={a} rate={r}");
                    Enqueue(() =>
                    {
                        try { if (!string.IsNullOrEmpty(a) && _player != null) _player.AspectRatio = a; } catch { }
                        try { if (r > 0 && _player != null) _player.SetRate(r); } catch { }
                    });
                }
            };
            p.Paused += (s, e) =>
            {
                LogLine("内核暂停");
                _manualPaused = true;   // v3.23: 暂停中停滞探针静默, 永不触发自救
                try { PlaybackPaused?.Invoke(); } catch (Exception ex) { LogLine("暂停回执异常: " + ex.Message); }
            };
            return p;
        }

        /// <summary>v3.20.7 黑屏终极兜底: 切后台后旧播放器实例的画面输出层可能已被系统判死,
        /// 原位重启媒体 (v3.20.4~v3.20.6) 都救不回——干脆整个换新内核:
        /// 新建 MediaPlayer 复用同一 LibVLC, 新实例必然重新绑定新画面层, 回来必有画面。
        /// 代价是重头开片加载 2~3 秒; 播放位置由调用方 posMs 续上。
        /// v3.20.8: 新实例状态迁移——倍速/画面比例/外挂字幕原样带回 (v3.20.7 真机画面缩角即丢状态)。
        /// 增强换源的同实例原位重启仍走 ReopenInPlace, 两不冲突。</summary>
        public void RebuildPlayerInPlace(long posMs)
        {
            var path = _currentPath;
            if (path == null || _libVLC == null || _videoCarrier == null) return;
            var old = _player;
            float oldRate = 0; string? oldAspect = null;
            try { oldRate = old?.Rate ?? 0; } catch { }
            try { oldAspect = old?.AspectRatio; } catch { }
            LogLine("回前台重建播放内核: 新实例接手 " + path);
            _reopenGuard = true;   // 停旧实例可能触发 EndReached, 先屏蔽防误换片
            _player = CreateAndWirePlayer();
            // v3.21.2: 画面出口必须接到新内核, 否则新内核拿不到 surface;
            // 比例/倍速改为挂起状态, 由 Playing 事件在画面输出就绪后补设
            _videoCarrier.MediaPlayer = _player;
            _pendingRate = oldRate;
            _pendingAspect = oldAspect;
            var media = new Media(_libVLC, path);
            PrepareMediaOptions(media, path);
            Enqueue(() =>
            {
                try { old?.Stop(); } catch { }
                try { old?.Dispose(); } catch { }
                _reopenGuard = false;
                _player.Media = media;
                _player.Play();
                try { if (oldRate > 0) _player.SetRate(oldRate); } catch { }
                try { if (!string.IsNullOrEmpty(oldAspect)) _player.AspectRatio = oldAspect; } catch { }
            });
            TryLoadSameNameSubtitle(path);   // v3.20.8: 外挂字幕跟新内核重挂
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                Enqueue(() => { try { _player.Time = posMs; } catch { } });
            });
        }

        /// <summary>起播后监看硬解失败：15 秒内捕获到解码错误则自动切软解重播一次</summary>
        private async void StartHwFallbackWatch()
        {
            if (_decodeErrSeen || _swFallbackTried || _currentPath == null) return;
            await Task.Delay(15000);
            if (!_decodeErrSeen || _swFallbackTried || _currentPath == null || _player == null) return;
            _swFallbackTried = true;
            try
            {
                _player.EnableHardwareDecoding = false;
                LogLine("检测到硬件解码失败，自动切换软解码重试");
                StatusChanged?.Invoke("硬件解码异常，已自动切换软解码重试…");
                var p = _currentPath;
                _ = Task.Run(() => { try { PlayPath(p); } catch { } });
            }
            catch { }
        }

        // ---------- 播放控制 ----------

        /// <summary>设置播放列表并从 index 开始播</summary>
        public void PlayList(IEnumerable<VideoItem> items, int index)
        {
            _playlist.Clear();
            _playlist.AddRange(items);
            _index = index;
            if (index >= 0 && index < _playlist.Count)
                PlayItem(_playlist[index]);
        }

        /// <summary>播放单个文件/URL (不进列表)</summary>
        public async Task PlayStandaloneAsync(string uri, string? saveDirForMagnet = null)
        {
            if (uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                await PlayMagnetAsync(uri, saveDirForMagnet);
                return;
            }
            // v3.24: m3u8 地址走 HLS 边播边存, 与磁力同一套开关
            if (HlsStreamService.IsHlsUrl(uri))
            {
                await PlayHlsAsync(uri, saveDirForMagnet);
                return;
            }
            _playlist.Clear();
            _playlist.Add(new VideoItem { Path = uri });
            _index = 0;
            PlayPath(uri);
        }

        /// <summary>r3 直播专用: 本机中转地址直进 PlayPath——绝不过 HLS 边播边存 (直播不落盘),
        /// 不进连播列表; 断流自救/载体自检等既有保险照常生效。</summary>
        public Task PlayLiveAsync(string uri)
        {
            _playlist.Clear();
            _index = -1;
            PlayPath(uri);
            return Task.CompletedTask;
        }

        private void PlayItem(VideoItem item) => PlayPath(item.Path);

        /// <summary>v3.25.3: 消费一次待跳续播点 (CAS 取走即清, 时间回执与 3 秒兜底谁先到谁触发, 防双跳)</summary>
        private void TryFireResumeSeek(long target)
        {
            if (target <= 0) return;   // v3.25.5 清零: 已清标记 (long.MinValue) 不是跳转目标, 换片后晚醒的兜底任务绝不误跳
            if (Interlocked.CompareExchange(ref _pendingResumeSeekMs, long.MinValue, target) != target) return;
            LogLine($"首帧回执即跳续播点: {target}ms");
            Enqueue(() => { try { if (_player != null) _player.Time = target; } catch { } });
        }

        /// <summary>本地流清单地址 (m3u8 伺服的入口, 每次会话端口/令牌都变)</summary>
        private static bool IsLocalStreamPath(string p) => p.Contains("/playlist?token=");

        /// <summary>记账地址: m3u8 播放用源清单地址, 其余原样 (磁力保持既有口径)</summary>
        private string ResumeKeyOf(string path)
            => IsLocalStreamPath(path) && _streamKeyOverride != null ? _streamKeyOverride! : path;

        private void PlayPath(string path, bool fromStreamRetry = false)
        {
            if (_player == null || _libVLC == null) return;

            // v3.23 P0-1: 新的一次用户点播重置自救计数; 自救重播不重置 (失败要累计到用尽)
            // v3.23 双检修复: 同时清「用尽已报」与上一部残留的进度值, 新会话从零记账
            if (!fromStreamRetry)
            {
                Volatile.Write(ref _retryAttempts, 0);
                _retryInFlight = false;
                _gaveUpReported = false;
                _lastReportedTime = long.MinValue;
                Interlocked.Exchange(ref _pendingResumeSeekMs, long.MinValue);   // v3.25.3: 新点播清掉上一部残留的待跳续播点
            }

            // v3.22.1 (0004 评审 P1-2): attempt 语义收紧为「已起播尝试」——真正发起播放才打自检标记,
            // 首帧落地 (TimeUpdated) / 正常退后台清标记; 只开 App 不播放、主题重绘都不再打标记,
            // 杜绝非播放会话的假阳性, 避免下次启动被误判纹理不兼容而永久回退。
            if (_videoCarrier is AllPlayerTextureView)
                CarrierPolicy.MarkAttempt(_videoCarrier.HostContext);

            bool externalOnce = _noHistoryOnce;   // v3.13.1: 外部点播只播一次, 不进历史
            _noHistoryOnce = false;
            // v3.24.2: 非本地清单地址 (普通文件/网络直链/磁力本机流) 清掉上一部的源地址记账
            if (!IsLocalStreamPath(path)) _streamKeyOverride = null;
            if (!externalOnce) SaveResume(); // 记住上一部

            _currentPath = path;
            CurrentPathChanged?.Invoke(path);
            if (!externalOnce)
            {
                var resumeKey = ResumeKeyOf(path);
                _pendingResumeMs = AppSettings.Current.Resume.TryGetValue(resumeKey, out var ms) && ms > 30000 ? ms : null;
                PlaybackHistory.RecordRecent(AppSettings.Current, resumeKey, DateTimeOffset.Now);
            }
            else
            {
                _pendingResumeMs = null; // 外部点播临时拷贝: 不读续播、不进最近播放
            }

            // v3.25.4 清零: 循环重播不跳续播 (重播从头看, 跳回上次位置会永远错过片头)
            if (_loopReplay) { _loopReplay = false; _pendingResumeMs = null; }

            // v3.25.8 封面帧起播 (用户拍板): 没有续播记录时, 从预览取帧的时间点直接开播,
            // 预览到真实画面同帧衔接, 不再回跳片头黑帧; 有续播记录以续播为准, 流媒体/外部点播未预置则从零
            var coverStartMs = Interlocked.Exchange(ref _pendingCoverStartMs, 0);
            if (coverStartMs > 0 && _pendingResumeMs == null) _pendingResumeMs = coverStartMs;

            var media = new Media(_libVLC, path);
            PrepareMediaOptions(media, path);

            var p0 = _player;
            LogLine("起播: " + path);
            EnsureAudioFocus();   // v3.25.19: 起播申请音频焦点, 来电/他应用出声不再抢着播
            EnsureMediaSession();   // v3.25.20: 建媒体会话, 耳机线控/蓝牙按键才路由得进来
            UpdateMediaSession(true);
            Enqueue(() =>
            {
                p0.Media = media;
                p0.Play();
            });

            // 记住的倍速跟着每一次起播走 (切集/重播不丢)
            var saved = AppSettings.Current.PlaybackRate;
            if (saved > 0 && Math.Abs(saved - 1.0) > 0.01)
                Enqueue(() => { try { p0.SetRate((float)saved); } catch { } });

            TryLoadSameNameSubtitle(path);

            // 断点续播: 首帧回执 (时间回执) 即跳上次位置, 不再盲等固定 1.2 秒; 3 秒兜底防首帧迟迟不来
            if (_pendingResumeMs != null)
            {
                var target = (long)_pendingResumeMs.Value;
                Interlocked.Exchange(ref _pendingResumeSeekMs, target);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    TryFireResumeSeek(Interlocked.Read(ref _pendingResumeSeekMs));
                });
            }
        }

        /// <summary>播放 content:// 等系统地址 (文件选择器返回的地址): 拷到缓存目录后播放。
        /// v3.13.1 自愈: 每次进新片先清掉上一批临时拷贝, 缓存目录绝不无限堆积; 外部请求不留进最近播放。</summary>
        public void PlayContentUri(global::Android.Net.Uri uri)
        {
            if (_player == null || _libVLC == null || _videoCarrier == null) return;
            var ctx = _videoCarrier.HostContext;
            if (ctx == null) return;
            var cacheDir = ctx.CacheDir;
            if (cacheDir == null) return;
            // 缓存自愈 (全系缓存自愈标准): 临时拷贝只留最新一份, 上限=1 封顶, 旧拷贝当场清
            try
            {
                var olds = cacheDir.ListFiles();
                if (olds != null)
                    foreach (var f in olds)
                        if (f.Name.StartsWith("选中视频_", StringComparison.Ordinal)) f.Delete();
            }
            catch { }
            var seg = uri.LastPathSegment ?? "video";
            var ext = System.IO.Path.GetExtension(seg);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            var tmp = new Java.IO.File(cacheDir, "选中视频_" + DateTime.Now.Ticks + ext);
            using (var src = ctx.ContentResolver!.OpenInputStream(uri))
            {
                if (src == null) return;   // 来源流拿不到：放弃拷贝, 按点播失败处理
                using (var dst = new FileStream(tmp.AbsolutePath, FileMode.Create))
                    src.CopyTo(dst);
            }
            _noHistoryOnce = true;   // 外部点播是临时拷贝, 绝不写进最近播放与续播记录
            PlayPath(tmp.AbsolutePath);
        }

        /// <summary>录制开关: 开 = 边播边存为 TS; 关 = 停止录制并把 TS 交给转换队列</summary>
        public bool Recording
        {
            get => _recording;
            set
            {
                if (value == _recording) return;
                _recording = value;
                if (value)
                {
                    // 重新发起带 sout 的播放
                    var dir = AppSettings.Current.SaveFolder;
                    Directory.CreateDirectory(dir);
                    _recordTsPath = Path.Combine(dir,
                        $"录制_{DateTime.Now:yyyyMMdd_HHmmss}.ts");
                    StatusChanged?.Invoke("边播边存: 开 (保存到 " + dir + ")");
                    ReplayCurrentWithRecord();
                }
                else
                {
                    StatusChanged?.Invoke("边播边存: 关");
                    var ts = _recordTsPath;
                    _recordTsPath = null;
                    ReplayCurrentWithRecord();
                    if (ts != null)
                    {
                        // 通知各端"缓存文件已生成" (手机端拿它做 TS→MP4 无损转封装)
                        RecordedFileCompleted?.Invoke(ts);
                        if (AppSettings.Current.AutoConvertRecorded)
                            RecordConverter.Enqueue(ts);
                    }
                }
            }
        }

        /// <summary>录制结束、TS 文件已落盘 (参数=文件路径)</summary>
        public event Action<string>? RecordedFileCompleted;

        /// <summary>重放当前媒体 (开/关录制需要重建 media 的 sout 选项)</summary>
        private void ReplayCurrentWithRecord()
        {
            if (_currentPath == null) return;
            var pos = _player?.Time ?? 0;
            var path = _currentPath;
            _currentPath = null;
            if (path.StartsWith("http://127.0.0.1") || File.Exists(path))
            {
                if (!_playlist.IsEmpty() && _index >= 0 && _index < _playlist.Count &&
                    _playlist[_index].Path == path)
                    PlayItem(_playlist[_index]);
                else
                    PlayPath(path);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    var p = _player; if (p != null) Enqueue(() => { try { p.Time = pos; } catch { } });
                });
            }
        }

        /// <summary>给 media 附加录制选项 (sout 落盘 TS) 与续播参数</summary>
        private void PrepareMediaOptions(Media media, string? originalPath)
        {
            // v3.25.23 智能字幕: 开启时给生成字幕留出底部空间, 不遮片源自带字幕区
            // v3.25.25 E15 如实记限制: sub-margin 只能在 media 创建时加。LibVLCSharp 3.10.1 无 SpuMargin/SetSpuMargin
            // (反射核实仅 SetSpu/SetSpuDelay), VideoLAN.LibVLC.Android 3.0 原生也没有 libvlc_video_set_spu_margin (VLC 4.0 才有)。
            // 播放中途才打开智能字幕的片, 下一次起播 (media 重建) 时才生效。
            if (AppSettings.Current.SmartSubtitleEnabled)
                media.AddOption(":sub-margin=140");
            if (_recording && _recordTsPath != null)
            {
                media.AddOption($":sout=#standard{{access=file,mux=ts,dst={_recordTsPath}}}");
                media.AddOption(":sout-keep");
            }
        }

        private void TryLoadSameNameSubtitle(string videoPath)
        {
            try
            {
                if (!videoPath.StartsWith("/")) return; // 仅本地文件
                var dir = Path.GetDirectoryName(videoPath) ?? "";
                var sub = SubtitleLocator.Pick(videoPath,
                    Directory.EnumerateFiles(dir).Take(200));
                if (sub != null)
                {
                    LogLine("外挂字幕: " + sub);
                    _player?.AddSlave(MediaSlaveType.Subtitle, sub, true);
                }
            }
            catch { }
        }

        /// <summary>挂载智能字幕生成的 SRT (v3.25.23): 复用外挂字幕链路, 播放中可挂。
        /// v3.25.25 E11 修: 调用方在后台线程, AddSlave 走 _playerQueue 队列口径, 不再跨线程直调内核。</summary>
        public void MountSubtitleFile(string srtPath)
        {
            try
            {
                if (string.IsNullOrEmpty(srtPath) || !File.Exists(srtPath)) return;
                LogLine("智能字幕挂载: " + srtPath);
                var p = _player;
                if (p != null) Enqueue(() => { try { p.AddSlave(MediaSlaveType.Subtitle, srtPath, true); } catch { } });
            }
            catch { }
        }

        // ---------- 磁力 ----------

        private async Task PlayMagnetAsync(string magnetUri, string? saveDir)
        {
            // v3.10: 起新引擎前先停旧的, 反复播磁力不再泄漏后台引擎
            _magnetCts?.Cancel();
            if (_magnet != null) await _magnet.StopAsync();
            // v3.24: 换片换引擎, m3u8 服务不再留后台
            _hlsCts?.Cancel();
            if (_hls != null) await _hls.StopAsync();
            _hls = null; _hlsCts = null;
            _magnetCts = new CancellationTokenSource();
            _magnet = new MagnetStreamService();
            _magnet.StatusChanged += s => StatusChanged?.Invoke(s);
            var url = await _magnet.StartAsync(magnetUri, saveDir, _magnetCts.Token);
            PlayPath(url);
        }

        /// <summary>m3u8 起播 (v3.24): 与磁力同一套流程 —— 先建边播边存, 再把本机清单地址交给播放器</summary>
        private async Task PlayHlsAsync(string hlsUrl, string? saveDir)
        {
            _hlsCts?.Cancel();
            if (_hls != null) await _hls.StopAsync();
            // 换片换引擎, 磁力引擎不再留后台
            _magnetCts?.Cancel();
            if (_magnet != null) await _magnet.StopAsync();
            _magnet = null; _magnetCts = null;
            _hlsCts = new CancellationTokenSource();
            _hls = new HlsStreamService();
            _hls.StatusChanged += s => StatusChanged?.Invoke(s);
            var url = await _hls.StartAsync(hlsUrl, saveDir, _hlsCts.Token);
            _streamKeyOverride = hlsUrl; // v3.24.2: 记住源清单地址, 续播/最近播放记账挂回源
            PlayPath(url);
        }

        /// <summary>
        /// 磁力可用性检测 (v3.10): 只验元数据并选片, 不进播放器。
        /// 成功返回 (文件名, 字节数); 失败 (超时/解析不了) 抛异常。检测完即停引擎,
        /// 不在后台偷跑流量 —— 真正点开播放时再起一遍, 元数据多半秒回。
        /// </summary>
        public async Task<(string Name, long Size)> PrepareMagnetAsync(string magnetUri, string? saveDir)
        {
            _magnetCts?.Cancel();
            if (_magnet != null) await _magnet.StopAsync();
            _magnetCts = new CancellationTokenSource();
            _magnet = new MagnetStreamService();
            _magnet.StatusChanged += s => StatusChanged?.Invoke(s);
            try
            {
                await _magnet.StartAsync(magnetUri, saveDir, _magnetCts.Token, warmHead: false);
                return (_magnet.FoundFileName ?? "磁力视频", _magnet.FoundFileSize);
            }
            finally
            {
                await _magnet.StopAsync();
                _magnet = null;
                _magnetCts = null;
            }
        }

        /// <summary>
        /// m3u8 可用性检测 (v3.24): 只拉清单并解析出分片, 不起伺服不落盘。
        /// 成功返回显示名; 失败 (超时/解析不了/无分片) 抛异常。
        /// </summary>
        public async Task<string> PrepareHlsAsync(string hlsUrl)
        {
            _hlsCts?.Cancel();
            if (_hls != null) await _hls.StopAsync();
            _hlsCts = new CancellationTokenSource();
            _hls = new HlsStreamService();
            _hls.StatusChanged += s => StatusChanged?.Invoke(s);
            try
            {
                await _hls.StartAsync(hlsUrl, null, _hlsCts.Token);
                return _hls.FoundFileName ?? "m3u8 视频";
            }
            finally
            {
                await _hls.StopAsync();
                _hls = null;
                _hlsCts = null;
            }
        }

        public async Task StopMagnetAsync()
        {
            if (_magnetCts != null) try { _magnetCts.Cancel(); } catch { }
            if (_magnet != null) await _magnet.StopAsync();
            _magnet = null;
            _magnetCts = null;
            // v3.24: m3u8 服务一齐收
            if (_hlsCts != null) try { _hlsCts.Cancel(); } catch { }
            if (_hls != null) await _hls.StopAsync();
            _hls = null;
            _hlsCts = null;
        }

        /// <summary>磁力播放的局域网流地址 (给电视投屏用); 不是磁力播放时为 null</summary>
        public string? MagnetLanStreamUrl => _magnet?.LanStreamUrl;

        /// <summary>m3u8 本地清单的局域网地址 (投屏用, 电视够不着 127.0.0.1)</summary>
        public string? HlsLanStreamUrl => _hls?.LanStreamUrl;

        // ---------- 列表导航 ----------

        public void PlayNext()
        {
            if (_index + 1 < _playlist.Count)
            {
                _index++;
                PlayItem(_playlist[_index]);
            }
            else StatusChanged?.Invoke("已经是最后一个了");
        }

        public void PlayPrevious()
        {
            if (_index > 0)
            {
                _index--;
                PlayItem(_playlist[_index]);
            }
            else StatusChanged?.Invoke("已经是第一个了");
        }

        public void Pause()
        {
            _manualPaused = true;   // v3.23: 手动暂停即静默自救
            _mediaPlaying = false;   // v3.25.20: 线控/蓝牙按键切换的判断依据
            UpdateMediaSession(false);
            Enqueue(() => _player?.Pause());
        }
        public void Play()
        {
            _mediaPlaying = true;   // v3.25.20
            UpdateMediaSession(true);
            Enqueue(() => _player?.Play());
        }

        /// <summary>v3.25.8 封面帧起播: 界面层在起播前预置预览取帧的时间点 (毫秒, 0 = 从头);
        /// 仅本次点播生效, 下一次 Play 未预置自动作废。</summary>
        public void PrimeCoverStart(long ms) => Interlocked.Exchange(ref _pendingCoverStartMs, ms > 0 ? ms : 0);

        /// <summary>把当前播放位置写进续播记录 (退出/切后台时调用)</summary>
        public void SaveProgress() => SaveResume();
        public void Stop()
        {
            SaveResume();
            AbandonAudioFocus();   // v3.25.19: 停播交还音频焦点
            _mediaPlaying = false;   // v3.25.20
            UpdateMediaSession(false);
            Enqueue(() => _player?.Stop());
        }

        // ================= 音频焦点 (v3.25.19) =================
        // 起播申请焦点: 他应用/来电出声时暂停, 短暂打断(如导航提示)恢复后自动续播;
        // 拔耳机/断蓝牙(音频变外放广播)立即暂停, 不再外放尴尬。

        private global::Android.Media.AudioManager? _audioMgr;
        private AudioFocusListener? _focusListener;
        private global::Android.Media.AudioFocusRequestClass? _focusRequest;
        private volatile bool _resumeOnFocusGain;
        private bool _noisyRegistered;

        private sealed class AudioFocusListener : global::Java.Lang.Object, global::Android.Media.AudioManager.IOnAudioFocusChangeListener
        {
            private readonly VideoHost _host;
            public AudioFocusListener(VideoHost host) { _host = host; }

            public void OnAudioFocusChange([global::Android.Runtime.GeneratedEnum] global::Android.Media.AudioFocus focusChange)
            {
                switch (focusChange)
                {
                    case global::Android.Media.AudioFocus.Loss:
                        _host._resumeOnFocusGain = false;
                        _host.Pause();
                        break;
                    case global::Android.Media.AudioFocus.LossTransient:
                    case global::Android.Media.AudioFocus.LossTransientCanDuck:
                        _host._resumeOnFocusGain = true;
                        _host.Pause();
                        break;
                    case global::Android.Media.AudioFocus.Gain:
                        if (_host._resumeOnFocusGain)
                        {
                            _host._resumeOnFocusGain = false;
                            _host.Play();
                        }
                        break;
                }
            }
        }

        private sealed class NoisyReceiver : global::Android.Content.BroadcastReceiver
        {
            private readonly VideoHost _host;
            public NoisyReceiver(VideoHost host) { _host = host; }

            public override void OnReceive(global::Android.Content.Context? context, global::Android.Content.Intent? intent)
            {
                if (intent?.Action == global::Android.Media.AudioManager.ActionAudioBecomingNoisy)
                {
                    _host._resumeOnFocusGain = false;
                    _host.Pause();   // 拔耳机/断蓝牙: 立即暂停, 不外放
                }
            }
        }

        private void EnsureAudioFocus()
        {
            try
            {
                var ctx = _videoCarrier?.HostContext ?? global::Android.App.Application.Context;   // v3.25.21: 载体可空, 兜底应用上下文
                _audioMgr ??= (global::Android.Media.AudioManager)(ctx.GetSystemService(global::Android.Content.Context.AudioService)!);
                _focusListener ??= new AudioFocusListener(this);
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    // v3.25.20: 26+ 走 AudioFocusRequest (旧三参重载已废弃), 23-25 保留旧口径
                    var req = _focusRequest ??= new global::Android.Media.AudioFocusRequestClass.Builder(global::Android.Media.AudioFocus.Gain)
                        .SetAudioAttributes(new global::Android.Media.AudioAttributes.Builder()!
                            .SetUsage(global::Android.Media.AudioUsageKind.Media)!
                            .SetContentType(global::Android.Media.AudioContentType.Movie)!
                            .Build()!)
                        .SetOnAudioFocusChangeListener(_focusListener)
                        .Build()!;
                    _audioMgr.RequestAudioFocus(req);   // v3.25.21: 局部快照, 字段跨调用不保空值态
                }
                else
                {
                    _audioMgr.RequestAudioFocus(_focusListener, global::Android.Media.Stream.Music, global::Android.Media.AudioFocus.Gain);
                }
                if (!_noisyRegistered)
                {
                    // ACTION_AUDIO_BECOMING_NOISY 是受保护系统广播, 免导出标志
                    var filter = new global::Android.Content.IntentFilter(global::Android.Media.AudioManager.ActionAudioBecomingNoisy);
                    ctx.RegisterReceiver(new NoisyReceiver(this), filter);
                    _noisyRegistered = true;
                }
            }
            catch (Exception ex) { LogLine("音频焦点申请未成功: " + ex.Message); }
        }

        private void AbandonAudioFocus()
        {
            try
            {
                _resumeOnFocusGain = false;
                if (_audioMgr == null) return;
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    // v3.25.20: 26+ 走 AudioFocusRequest (旧重载已废弃), 23-25 保留旧口径
                    if (_focusRequest != null) _audioMgr.AbandonAudioFocusRequest(_focusRequest);
                }
                else if (_focusListener != null)
                {
                    _audioMgr.AbandonAudioFocus(_focusListener);
                }
            }
            catch { }
        }

        // ================= 媒体会话与线控按键 (v3.25.20) =================
        // 蓝牙耳机/耳机线控的暂停、播放、上一曲、下一曲走系统媒体按键路由,
        // 必须有活跃的 MediaSession 才收得到。播放态同步进 PlaybackState, 按键按真实状态切换。

        private global::Android.Media.Session.MediaSession? _mediaSession;
        private volatile bool _mediaPlaying;

        private sealed class MediaButtonCallback : global::Android.Media.Session.MediaSession.Callback
        {
            private readonly VideoHost _host;
            public MediaButtonCallback(VideoHost host) { _host = host; }

            public override bool OnMediaButtonEvent(global::Android.Content.Intent? mediaButtonIntent)
            {
                try
                {
                    if (mediaButtonIntent?.Action == global::Android.Content.Intent.ActionMediaButton)
                    {
                        // v3.25.20: 33+ 取参走双参重载 (单参已废弃), 旧系统保留单参
                        global::Android.Views.KeyEvent? ev = OperatingSystem.IsAndroidVersionAtLeast(33)
                            ? (global::Android.Views.KeyEvent?)mediaButtonIntent.GetParcelableExtra(
                                global::Android.Content.Intent.ExtraKeyEvent,
                                Java.Lang.Class.FromType(typeof(global::Android.Views.KeyEvent)))
                            : (global::Android.Views.KeyEvent?)mediaButtonIntent.GetParcelableExtra(global::Android.Content.Intent.ExtraKeyEvent);
                        if (ev != null && ev.Action == global::Android.Views.KeyEventActions.Down)
                        {
                            switch (ev.KeyCode)
                            {
                                case global::Android.Views.Keycode.MediaPlayPause:
                                    if (_host._mediaPlaying) { _host._resumeOnFocusGain = false; _host.Pause(); }
                                    else _host.Play();
                                    return true;
                                case global::Android.Views.Keycode.MediaPlay:
                                    _host.Play(); return true;
                                case global::Android.Views.Keycode.MediaPause:
                                    _host._resumeOnFocusGain = false; _host.Pause(); return true;
                                case global::Android.Views.Keycode.MediaNext:
                                    _host.PlayNext(); return true;
                                case global::Android.Views.Keycode.MediaPrevious:
                                    _host.PlayPrevious(); return true;
                            }
                        }
                    }
                }
                catch { }
                if (mediaButtonIntent == null) return false;   // v3.25.21: 空意图不外传, 按未处理回绝
                return base.OnMediaButtonEvent(mediaButtonIntent);
            }
        }

        private void EnsureMediaSession()
        {
            if (_mediaSession != null) return;
            try
            {
                var ctx = _videoCarrier?.HostContext ?? global::Android.App.Application.Context;   // v3.25.21: 载体可空, 兜底应用上下文
                var session = new global::Android.Media.Session.MediaSession(ctx, "TaoLive");
                session.SetCallback(new MediaButtonCallback(this));
                session.SetFlags(global::Android.Media.Session.MediaSessionFlags.HandlesMediaButtons
                    | global::Android.Media.Session.MediaSessionFlags.HandlesTransportControls);
                _mediaSession = session;
            }
            catch (Exception ex) { LogLine("媒体会话创建未成功: " + ex.Message); }
        }

        private void UpdateMediaSession(bool playing)
        {
            try
            {
                var session = _mediaSession;   // v3.25.21: 局部快照, 字段跨调用不保空值态
                if (session == null) return;
                var state = new global::Android.Media.Session.PlaybackState.Builder()
                    .SetActions(global::Android.Media.Session.PlaybackState.ActionPlay
                        | global::Android.Media.Session.PlaybackState.ActionPause
                        | global::Android.Media.Session.PlaybackState.ActionPlayPause
                        | global::Android.Media.Session.PlaybackState.ActionSkipToNext
                        | global::Android.Media.Session.PlaybackState.ActionSkipToPrevious)!
                    .SetState(playing ? global::Android.Media.Session.PlaybackStateCode.Playing
                                      : global::Android.Media.Session.PlaybackStateCode.Paused,
                              _player?.Time ?? 0, 1f)!
                    .Build()!;
                session.SetPlaybackState(state);
                session.Active = true;
            }
            catch { }
        }

        public void SeekTo(long ms) { var p = _player; if (p != null) Enqueue(() => { try { p.Time = ms; } catch { } }); }

        private volatile bool _reopenGuard;   // v3.20.6 原位重启停播期间屏蔽结束事件, 防止误触发换片/收尾

        /// <summary>原地换源（v3.17 seek 跟随/批量换源）：同一文件被外部替换（AI 合并）后重开，从 posMs 继续播。
        /// 不走 PlayPath 的历史/续播/列表副作用；时间轴一致（合并带 MD5+时长双校验），posMs 直接有效。
        /// v3.20.6: 显式先停再开——给暂停/异常状态的播放器直接换媒体可能不生效 (真机黑屏)，
        /// Stop 彻底重置解码与输出状态, 新媒体起播必然重建输出。</summary>
        public void ReopenInPlace(long posMs)
        {
            var path = _currentPath;
            if (path == null || _player == null || _libVLC == null) return;
            var media = new Media(_libVLC, path);
            PrepareMediaOptions(media, path);
            var p = _player;
            LogLine("原地换源: 增强内容生效 " + path);
            _reopenGuard = true;
            Enqueue(() =>
            {
                try { p.Stop(); } catch { }
                _reopenGuard = false;
                try { p.Media = media; p.Play(); } catch { }
            });
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                var p2 = _player; if (p2 != null) Enqueue(() => { try { p2.Time = posMs; } catch { } });
            });
        }
        public void SetRate(float rate) { var p = _player; if (p != null) Enqueue(() => { try { p.SetRate(rate); } catch { } }); }

        /// <summary>画面比例: null=跟随片源, 否则 "16:9" / "4:3" 等</summary>
        public void SetAspectRatio(string? aspect) { var p = _player; if (p != null) Enqueue(() => { try { p.AspectRatio = aspect; } catch { } }); }
        public void SetMute(bool mute) { var p = _player; if (p != null) Enqueue(() => { try { p.Volume = mute ? 0 : 100; } catch { } }); }

        /// <summary>循环播放当前视频 (3.x 无原生 Loop, 结束后自动重播)</summary>
        public bool LoopEnabled { get; set; }

        // ---------- 字幕 / 音轨 ----------

        public void SubtitleDelay(float seconds) { var p = _player; if (p != null) Enqueue(() => { try { p.SetSpuDelay(-((long)(seconds * 1000000))); } catch { } }); }

        public int AudioTrackCount => _player?.AudioTrackCount ?? 0;
        public int AudioTrack
        {
            get => _player?.AudioTrack ?? -1;
            set { var p = _player; if (p != null) Enqueue(() => { try { p.SetAudioTrack(value); } catch { } }); }
        }
        public int SpuTrackCount => _player?.SpuCount ?? 0;
        public int SpuTrack
        {
            get => _player?.Spu ?? -1;
            set { var p = _player; if (p != null) Enqueue(() => { try { p.SetSpu(value); } catch { } }); }
        }

        // ---------- 续播记录 ----------

        private void SaveResume()
        {
            try
            {
                if (_player == null || _currentPath == null) return;
                var pos = _player.Time;
                var len = _player.Length;
                var key = ResumeKeyOf(_currentPath); // v3.24.2: m3u8 用源清单地址记账, 换会话也能续上
                if (pos > 30000 && len > 0 && pos < len - 10000)
                    AppSettings.Current.Resume[key] = pos;
                else
                    AppSettings.Current.Resume.Remove(key);
                PlaybackHistory.TrimResume(AppSettings.Current);
                SettingsFile.Save(AppSettings.Current);
            }
            catch { }
        }

        public void Dispose()
        {
            SaveResume();
            try
            {
                _magnetCts?.Cancel();
                _magnet?.StopAsync().Wait(2000);
                _hlsCts?.Cancel();
                _hls?.StopAsync().Wait(2000);
                _player?.Dispose();
                _libVLC?.Dispose();
            }
            catch { }
        }
    }

    internal static class ListExt
    {
        public static bool IsEmpty<T>(this List<T> list) => list.Count == 0;
    }
}
