using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AllPlayer.BLL;
using AllPlayer.Model;
using AllPlayer.DAL;
using OperationCanceledException = System.OperationCanceledException;
using AllPlayer.UI.Android.Shared;
using Uri = Android.Net.Uri;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 道映手机版 v3.0 重做版 — 核心 (partial, 主页见 MainActivity.Home, 播放页见 MainActivity.Player)。
    /// 设计约定 (Tao 系最新标准):
    /// · 启动即钉基调: 先按设置应用三象主题 (0水墨/1天灰/2墨夜), 再建界面;
    /// · 颜色只出自 TaoPalette 事实源, 界面层零写死色值;
    /// · 耗时操作 (扫描/删除/设置落盘) 全部后台线程, 主线程只碰内存与界面;
    /// · 按钮纯图标加大、整格命中; 文案说人话: 报错给可执行指引、状态如实。
    /// </summary>
    [Activity(Label = "道映", MainLauncher = true, Exported = true,
        LaunchMode = LaunchMode.SingleTask,
        ScreenOrientation = ScreenOrientation.Portrait,
        Theme = "@android:style/Theme.Material.NoActionBar",
        SupportsPictureInPicture = true,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
    // v3.13.1: 开放「接收外部播放」入口——TaoZip 等文件管理器点视频可直接定向到道播放起播
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataSchemes = new[] { "content" }, DataMimeType = "video/*")]
    public partial class MainActivity : Activity
    {
        // ---- 媒体库与播放内核 ----
        internal readonly List<VideoItem> _videos = new();
        internal readonly List<VideoItem> _filtered = new();
        internal readonly VideoHost _host = new();
        internal bool _inPlayer;
        internal CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _castWatchCts;   // v3.23 (P2-2) 投屏状态看守
        internal readonly Dictionary<string, Uri> _mediaUris = new(StringComparer.OrdinalIgnoreCase);

        // ---- 面板与关键控件 (主页/播放页各自的 partial 里赋值) ----
        internal FrameLayout _root = null!;
        internal LinearLayout _homePanel = null!;
        internal FrameLayout _playerPanel = null!;
        internal TextView _status = null!, _title = null!, _timeCur = null!, _timeDur = null!, _magnetStatus = null!, _homeHint = null!;   // v3.13 时间拆当前/总时长两处
        internal TextView _playerStatus = null!, _gestureHint = null!;
        internal ListView _list = null!;
        internal LinearLayout _controls = null!;
        internal SeekBar _seek = null!;
        internal VideoAdapter _adapter = null!;
        internal FrameLayout? _unlockBtn;
        internal View? _fullscreenBtn;   // v3.3: 全屏也是玻璃圆钮 (FrameLayout), 图标切换走 SetGlassIcon
        internal IconView? _themeBtn, _settingsBtn;   // v3.10 右上只留主题/设置两钮: 其余功能收进设置钮的菜单卡片
        internal View? _playerMoreBtn;            // 播放页更多圆钮: 顶部控制行右缘, 功能卡片在其下方唤起
        internal View? _backBtn, _castBtn, _lockBtn;   // v3.13 播放页: 返回/投屏(右上)/锁定(左侧居中)
        internal TextView? _speedBtn;   // v3.13 倍速(右下, 文字钮需显示倍率)
        internal bool _hostInit, _recordWired;   // v3.13 播放内核只初始化一次 / 转码回调只挂一次 (主题原位重绘不重复)
        internal LinearLayout _playerTopBar = null!;   // v3.4 顶部控制行 (取代原顶部标题条)
        internal FrameLayout? _pauseBtn;          // 播放页播放/暂停圆钮
        internal FrameLayout? _centerPlayBtn;     // v3.22.4 画面正中播放浮钮 (暂停态亮, 播放态隐)
        internal Java.Lang.Runnable? _hideUnlockRun;  // v3.22.4 锁定态解锁键 2.5 秒自动消失
        internal long _lockBackArmedMs;           // v3.22.4 锁定态返回键二次确认时间戳
        internal TextView? _aiStatusFps, _aiStatusRes;   // v3.20.5 底栏中间状态小字: 第一行帧率/第二行分辨率
        internal TextView? _smartStatus;                 // v3.25.23 智能字幕状态小字 (底栏第三行, 同亮白描影口径)
        internal SmartSubtitleService? _smartSub;        // v3.25.23 智能字幕编排: 模型下载/抽音频/识别/挂字幕全自动

        // ---- v3.23.1 开场黑场过渡 (真机反馈打开画面抖动) ----
        internal FrameLayout? _openingCover;           // 视频真实尺寸定版前盖住画面的黑场层 (v3.25.2 改 FrameLayout 叠首帧缩略图)
        internal ImageView? _openingThumb;             // v3.25.2 黑屏首帧·本地: 黑场层上的首帧缩略图 (本地文件列表已解出的才亮)
        internal ImageView? _openingThumbHd;           // v3.25.8 原画级首帧封面: 叠在小图上, 解好原画尺寸帧后淡入换上
        internal string? _openingThumbPath;            // v3.25.8 当前封面归属的路径 (异步回图只认当前要播的片, 防串图)
        internal bool _openingRevealed;                // 揭幕回执 (幂等, 防重复揭)
        internal Java.Lang.Runnable? _openingSafety;   // 尺寸迟迟不报 (纯音轨/坏流) 的兜底揭幕

        // ---- v3.18.6 拖动进度条预览 ----
        internal LinearLayout? _seekPreview;                 // 拖动中浮层 (预览图+时间)
        internal ImageView? _seekPrevImg;                    // 预览帧
        internal TextView? _seekPrevTime;                    // 拖到的时间
        internal global::Android.Media.MediaMetadataRetriever? _seekRetr;   // 预览取帧器 (换片重建)
        internal string? _seekRetrPath;                      // 取帧器当前绑定路径
        internal readonly object _seekRetrLock = new();
        internal readonly Dictionary<int, Bitmap?> _seekFrames = new();   // 秒级帧缓存 (值可空=负缓存)
        internal long _seekLastFetchMs;                      // 抽帧节流
        internal bool _seekPreviewWired;                     // CurrentPathChanged 只挂一次

        // ---- 下载管理 ----
        internal DownloadsPanel _dlPanel = null!;
        internal DownloadService _downloads = null!;

        // ---- 界面状态 ----
        internal bool _galleryMode = true;
        internal string _filterKw = "";
        internal readonly Dictionary<string, Bitmap> _thumbs = new();
        internal readonly HashSet<string> _thumbBusy = new();

        // ---- 播放页状态 ----
        internal bool _locked, _fullscreen, _seekDragging, _boosting;
        internal float _currentRate = 1f, _boostRate = 1f;
        internal float _contentFps;            // v3.25.1: 视频内容真实帧率 (探到了才向系统声明)
        internal long _lastPosMs;
        internal string? _lastEnhanceStartedPath;   // v3.21.0 换片去重: 同一片不重复触发增强入队
        internal int _aspectIdx = 3;   // 0跟随片源 1=16:9 2=4:3 3=铺满 (r2 默认铺满: 真机反馈直播横屏右/底黑边, 2026-11-11)
        internal float _gLastX, _gLastY;
        internal int _gMode;       // 0未定 1水平快进 2左侧亮度 3右侧音量
        internal long _gSeekMs;

        // ---- 基础设施 ----
        internal readonly Handler _uiHandler = new(Looper.MainLooper!);
        internal Java.Lang.Runnable? _hideRun, _homeHintRun;
        internal Java.Lang.Runnable? _hintHideRun;   // v3.23.2 提示条自管消失的专属定时
        internal Java.Lang.Runnable? _magnetDebounce;   // v3.10 磁力输入防抖: 停一秒才起检测, 连续粘贴只认最后一次
        internal bool _needRescanOnResume;
        private static readonly SemaphoreSlim _scanGate = new(1, 1);
        private static readonly object _settingsIoLock = new();
        private static bool _netOptTraceHooked; // v3.25.0: 日志出口只挂一次, 活动重建不重复
        private static bool _fpsProbeWired;     // v3.25.1: 内容帧率探测只挂一次

        // ================= 生命周期 =================

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (s, e) => SaveCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => SaveCrash(e.ExceptionObject as Exception);
            try { OnCreateInner(savedInstanceState); }
            catch (Exception ex) { SaveCrash(ex); throw; }
        }

        private void OnCreateInner(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // v3.25.26 真机误报修: 磁盘预检注入安卓 StatFs 口径 (DriveInfo 探 rootfs 必误报「存储空间不足」)
            AllPlayer.BLL.DownloadService.FreeBytesProbe = DiskProbe.FreeBytes;

            // v3.25.8 手机素质提前探清为常量: 启动即探最高刷新率/面板像素, 之后实时增强封顶读常量
            DeviceSpecProbe.WarmUp(this);
            _liveAiHost = this;   // v3.25.8 真机修: 现役实例登记, 装配成功回调和活动重建补挂都落它

            // 设置目录: 应用私有目录 (无需任何权限)
            AppSettings.BaseDir = GetExternalFilesDir(null)!.AbsolutePath;
            AppSettings.Current = SettingsFile.Load(AppSettings.BaseDir);
            _galleryMode = AppSettings.Current.GalleryMode;   // v3.18.2: 排列模式重启保持

            // v3.25.13: 密码箱功能整体退场的一次性收尾 —— 此前收进箱里的文件原路搬回原位置,
            // 然后清掉索引与箱目录; 后台线程执行, 文件搬不回的留原处并如实提示, 绝不丢文件
            RestoreLegacyVaultFiles();

            // v3.25.0: 网络优化诊断 (建连耗时三段拆分) 只进日志不上屏; 静态去重, 防活动重建后重复挂接
            if (!_netOptTraceHooked)
            {
                _netOptTraceHooked = true;
                AllPlayer.BLL.NetOpt.Trace += AppLog.W;
            }

            // v3.23 (P0-3): 启动后台清扫磁力边播的临时残留——上次进程被杀留下的分片目录已无活跃会话, 直接清
            // v3.25.25 (E13 修): 增扫智能字幕缓存目录下超 24 小时的孤儿 PCM (识别中途被杀的残留), 24 小时内不动防误删进行中的识别
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    TempSweeper.Sweep(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AllPlayer"));
                    TempSweeper.SweepOrphanPcm(System.IO.Path.Combine(CacheDir!.AbsolutePath, "taosub"), TimeSpan.FromHours(24));
                    // v3.25.28 E7 修: 智能字幕 SRT/哨兵缓存整组按期清扫 (清单口径「N 天照既有清扫惯例」——
                    // 既有 24h 惯例是孤儿 pcm 临时文件; SRT 是成果缓存, 全片重算代价高, 按 30 天清;
                    // 哨兵与 SRT 整组同删, 保 E9「哨兵无=完整」口径不破, 见 TempSweeper.SweepStaleSubCaches)
                    TempSweeper.SweepStaleSubCaches(System.IO.Path.Combine(CacheDir!.AbsolutePath, "taosub"), TimeSpan.FromDays(30));
                }
                catch { }   // 清扫失败不挡启动
            });

            // v3.18.5: AI 装配挪到启动最前面(后台线程, 不挡界面)——模型越早装好, 基准越早可用
            InitAiEnhance();
            InitLive();   // r3 直播主线: 台单/取流/缓冲池家底后台装配

            // 启动即钉基调: 界面构建之前先落主题, 深色系统的机器也不许开局错底
            TaoTheme.ApplyFromSettings();

            EnsureDefaultFolders();
            BuildHome();
            BuildPlayer();
            BuildOverlayPanels();
            SetContentView(_root);

            // v3.25.23 智能字幕编排: 状态回传走 UI 线程包装 (服务回调在后台线程), 挂字幕复用现有外挂链路
            _smartSub = new SmartSubtitleService(
                currentPath: () => _host.CurrentPath,
                status: s => RunOnUiThread(() => { if (_smartStatus != null) _smartStatus.Text = s; }),
                mountSrt: p => _host.MountSubtitleFile(p),
                cacheDir: CacheDir!.AbsolutePath);

            RequestStorageAccess();
            StartScan();

            _host.StatusChanged += s => RunOnUiThread(() =>
            {
                _magnetStatus.Text = s;
                _playerStatus.Text = s;
                _playerStatus.Visibility = string.IsNullOrEmpty(s) ? ViewStates.Gone : ViewStates.Visible;
            });
            _host.TimeUpdated += (t, len) => RunOnUiThread(() =>
            {
                _lastPosMs = t;
                try { if (_host.CurrentPath != null) OnWatchProgressChunkAware(_host.CurrentPath, t); } catch { }   // v3.15: 分段续存调度（5s 节流，档位关内部忽略）
                if (!_seekDragging && _seek.Max > 0)
                {
                    _seek.Progress = (int)(t * 1000 / Math.Max(len, 1));
                    _timeCur.Text = FormatMs(t);   // v3.13 时间分列进度条两端
                    _timeDur.Text = FormatMs(len);
                }
                // 记住总时长, 供列表画观看进度条 (时长变化或首次得知时落盘)
                var cp = _host.CurrentPath;
                if (len > 0 && cp != null && (!AppSettings.Current.Durations.TryGetValue(cp, out var old) || Math.Abs(old - len) > 1000))
                {
                    AppSettings.Current.Durations[cp] = len;
                    SaveSettingsSafe();   // 播放中频繁触发, 绝不为主线程写盘
                }
            });
            _host.PlaybackEnded += () => RunOnUiThread(() =>
            {
                SaveTsIfRecording();
                // v3.23 (P1-1): 自动连播切下一集前提示片名, 像电视连播的预告
                var nextName = _host.PeekNextName();
                if (nextName != null) ShowHint($"下一个：{nextName}", hideAfterMs: 3000);   // v3.23.2: 提示自管, 3 秒必收
                _host.PlayNext();
            });
            // v3.22.6: 内核真正起播/真正暂停的回执一到就补刷按钮——
            // 点击瞬间读的是旧状态, 全靠这两道回执把「播放中亮播放键」的脱节收掉
            _host.PlaybackStarted += () => RunOnUiThread(() => { try { UpdatePlaybackUi(); _smartSub?.Kick(); } catch { } });   // v3.25.23 连播换集自动跟字幕
            _host.PlaybackPaused += () => RunOnUiThread(() => { try { UpdatePlaybackUi(); } catch { } });
            // v3.23 (P0-1): 重试次数用尽, 如实告知并给可执行出路
            _host.StreamRetryGaveUp += () => RunOnUiThread(() =>
                TaoUi.Toast(this, "网络流反复中断, 已停止重试, 点播放重试", long_: true));
            // 换集/切视频时同步刷新标题 (含自动连播和循环重播)
            _host.CurrentPathChanged += p => RunOnUiThread(() =>
            {
                // r3 直播态: 标题挂台名, 不走文件增强管线 (分片账目走缓冲池)
                if (_isLive && _liveCurrent != null)
                {
                    _title.Text = _liveCurrent.Name;
                    UpdateAiStatusText();
                    return;
                }
                var name = p == null ? "" : System.IO.Path.GetFileName(p);
                _title.Text = name;
                AiSegmentRunner.InvalidateSpec();   // v3.20.9: 新片的增强规格还没跑出来, 旧片规格不作数
                UpdateAiStatusText();               // 状态字立即回到「处理中未出规格」, 不挂旧片数据
                // v3.21.0 换片修复: 所有换片路径（上下曲/自动连播/外部点播）都给新片重新起增强
                if (p != null && !string.Equals(Normalize(p), Normalize(_lastEnhanceStartedPath ?? ""), StringComparison.OrdinalIgnoreCase))
                {
                    _lastEnhanceStartedPath = p;
                    OnPlaybackStartChunkAware(p);
                }
            });

            // v3.13.1: 冷启动也可能带 ACTION_VIEW 进来 (进程没活时点 TaoZip 视频走 OnCreate)
            HandleExternalPlay(Intent);
        }

        // ================= 外部播放 (v3.13.1) =================

        /// <summary>应用活着时再次被 ACTION_VIEW 唤起 (SingleTask): 直接接新片起播。</summary>
        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleExternalPlay(intent);
        }

        /// <summary>接收外部播放请求: TaoZip 等文件管理器用 content:// 定向点视频, 亮出播放页后拷贝起播。</summary>
        private void HandleExternalPlay(Intent? intent)
        {
            if (intent == null || intent.Action != Intent.ActionView) return;
            var uri = intent.Data;
            if (uri == null) return;
            try
            {
                ShowPlayer(true);                       // 先亮播放页, 画面出口挂稳再起播 (v3.11 同款次序)
                _host.PlayContentUri(uri);
                var name = QueryDisplayName(uri);
                if (name != null) _title.Text = name;   // 标题显示原文件名, 不给用户看缓存临时名
            }
            catch (Exception ex)
            {
                TaoUi.Toast(this, "接收播放请求没成功: " + ex.Message, long_: true);
            }
        }

        /// <summary>向内容提供器要原文件名 (TaoZip FileProvider 会带 DISPLAY_NAME), 要不到就返回空。</summary>
        private string? QueryDisplayName(global::Android.Net.Uri uri)
        {
            try
            {
                using var c = ContentResolver!.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
                if (c != null && c.MoveToFirst())
                {
                    var idx = c.GetColumnIndex(IOpenableColumns.DisplayName);
                    if (idx >= 0) return c.GetString(idx);
                }
            }
            catch { }
            return null;
        }

        // ================= 主题原位重绘 (v3.13) =================

        /// <summary>主题切换只重画样式: 界面视图树原位重建, _videos/_filtered/扫描进度原样保留,
        /// 不再 Recreate 触发整页重建与媒体库重扫。新视图构建时从 TaoTheme 取新色值, 零残留。</summary>
        private void ReapplyTheme()
        {
            var kw = _filterKw;
            var statusText = _status.Text;
            var magnetText = _magnetStatus.Text;
            var hintVisible = _homeHint.Visibility == ViewStates.Visible;
            _inPlayer = false;                   // 换主题入口只在主页, 播放页状态一并复位
            BuildHome();
            BuildPlayer();
            BuildOverlayPanels();
            SetContentView(_root);
            _searchInput.Text = kw;              // 触发 TextChanged 走一遍重过滤 (数据不动, 只重绑定)
            RebuildFiltered();                   // 空关键词时 TextChanged 不触发, 这里兜底刷新绑定
            _status.Text = statusText;
            _magnetStatus.Text = magnetText;
            if (hintVisible) _homeHint.Visibility = ViewStates.Visible;
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (_inPlayer) ApplySystemBars();   // v3.25.17: 回前台重压一次系统栏, 防系统弹回
            // v3.20.7 用户令「任何时候不允许黑屏」: 回前台不再赌 surface 重建链——
            // v3.20.4 原位重启 / v3.20.5 画面层重挂 / v3.20.6 显式停再开, 真机都没能根治:
            // 旧实例的解码输出层被系统判死后, 同实例上怎么重开媒体都救不回。
            // v3.22.0 方案A (用户批准): 画面载体改自写纹理视图——退后台不回收画面层,
            // 回前台原样保留, 根治真机「切后台返回画面缩成贴底横条」:
            // 纹理载体走轻路径 (仅重挂出口), 表面载体 (低配自检回退) 保留原重建链不变。
            if (_inPlayer && _host.Player != null && _host.CurrentPath != null)
            {
                if (_carrier is AllPlayerTextureView)
                {
                    try
                    {
                        AppLog.W($"回前台(纹理载体): pos={_host.Player.Time}ms 不重建, 仅重挂出口");
                        MarkCarrierOk();                       // 进程活着退到前台 = 载体兼容
                        ApplyRotationMode();                   // 方向按当前设置原样重设, 防后台转向残留
                        _carrier.AsView.Post(() => _host.ReattachVideoView());
                        // v3.22.1 (0004 评审 P1-3): 纹理模式回前台轻路径加零兜底保险——300ms 后
                        // AWindow 摘下再挂上强制重挂, 防极端机型回前台画面不恢复
                        _carrier.AsView.PostDelayed(new Java.Lang.Runnable(() =>
                        {
                            try { (_carrier as AllPlayerTextureView)?.ForceRemount(); } catch { }
                        }), 300);
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        long pos = _host.Player.Time;
                        bool wasPlaying = _host.IsPlaying;
                        AppLog.W($"回前台恢复: pos={pos}ms wasPlaying={wasPlaying} → 重建画面层+播放内核");
                        // v3.21.2: 先把方向定死, 避免重建完又因转向触发二次布局让画面错位;
                        // 再摘装画面层, 等 surface 真正重走创建链后再挂新内核, 按画面输出的真实就绪信号补设比例/倍速
                        ApplyRotationMode();
                        _carrier.AsView.PostDelayed(new Java.Lang.Runnable(() =>
                        {
                            try
                            {
                                _host.ForceSurfaceRecreate();
                                _carrier.AsView.PostDelayed(new Java.Lang.Runnable(() =>
                                {
                                    try
                                    {
                                        _host.RebuildPlayerInPlace(Math.Max(0, pos));
                                        if (!wasPlaying)
                                        {
                                            // 暂停态回来仍保持暂停 (等媒体起播稳定后再按停, 与「回来手动继续」约定一致)
                                            _carrier.AsView.PostDelayed(new Java.Lang.Runnable(() =>
                                            { try { _host.Pause(); } catch { } }), 1600);
                                        }
                                    }
                                    catch { }
                                }), 300);
                            }
                            catch { }
                        }), 500);
                    }
                    catch (Exception ex) { AppLog.W($"回前台恢复异常: {ex.GetType().Name} {ex.Message}"); }
                }
            }
            // v3.22.4 (老板口径): 回前台一律按播放内核真实状态重刷按钮, 杜绝「停着却亮暂停键」的脱节;
            // 纹理载体回前台保持暂停 (老板拍板: 保持暂停, 点播放再续), 不自动续播
            if (_inPlayer) RunOnUiThread(() => { try { UpdatePlaybackUi(); } catch { } });
            // 从系统权限页回来时, 若刚授权则自动重扫
            if (_needRescanOnResume)
            {
                _needRescanOnResume = false;
                StartScan();
            }
            else if (!MediaPermissionGranted && _videos.Count == 0)
            {
                RequestStorageAccess();
            }
        }

        /// <summary>切到后台: 存进度; 退出应用则停播, 只是暂时离开则自动暂停 (回来手动继续)</summary>
        protected override void OnPause()
        {
            base.OnPause();
            // v3.22.1 (0004 评审 P2-9): 有播放会话才算载体存活证据; 未起播不落 ok, 不污染自检状态
            if (_host.CurrentPath != null) MarkCarrierOk();
            try
            {
                if (!_inPlayer || _host.Player == null) return;
                if (IsFinishing)
                {
                    SaveTsIfRecording();
                    _host.Stop();
                }
                else if (_host.IsPlaying)
                {
                    _host.SaveProgress();
                    _host.Pause();
                    RunOnUiThread(() =>
                    {
                        try { UpdatePlaybackUi(); } catch { }   // v3.22.4: 切后台自动暂停后按钮立即同步 (停着亮播放键+中间出播放钮)
                        TaoUi.Toast(this, "已暂停并记住进度");
                    });
                }
                else
                {
                    _host.SaveProgress();
                }
            }
            catch { }
        }

        public override void OnBackPressed()
        {
            if (TaoPopup.DismissAll()) return;   // 卡片弹层 (菜单/对话框): 返回键先收它
            if (_dlPanel.HandleBack()) return;   // 下载管理: 收起面板
            if (_inPlayer)
            {
                if (_locked)
                {
                    // v3.22.4: 锁定态按返回第一次只提示, 2 秒内第二次才退出 (防手滑关掉播放器)
                    if (Java.Lang.JavaSystem.CurrentTimeMillis() - _lockBackArmedMs < 2000) { _lockBackArmedMs = 0; }
                    else { _lockBackArmedMs = Java.Lang.JavaSystem.CurrentTimeMillis(); TaoUi.Toast(this, "再按一次返回键退出"); return; }
                }
                OnPlaybackExitChunkAware();   // v3.15: 退出播放钩子（有待合并段就异步合并收口）
                SaveTsIfRecording();
                _host.Stop();
                ShowPlayer(false);
                return;
            }
            Finish();
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
            {
                _needRescanOnResume = false;
                StartScan();
                return;
            }
            // v3.25.20: Android 14+ 用户选了"仅部分照片视频"也算授权——有得看就扫, 顶部状态行会提示放开全部
            if (MediaPermissionPartial)
            {
                _needRescanOnResume = false;
                StartScan();
                return;
            }
            RunOnUiThread(() =>
            {
                _status.Text = "没有权限就读不了视频, 点这里重新授权";
                TaoPopup.ShowMessage(this, "需要媒体权限",
                    "扫描手机里的视频需要「照片和视频」权限。点下面的按钮去设置里允许, 回来就会自动开始扫描。",
                    new (string, Action?, bool)[]
                    {
                        ("暂不", null, false),
                        ("去应用设置", () =>
                        {
                            try
                            {
                                StartActivity(new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                                    Uri.Parse("package:" + PackageName)));
                            }
                            catch { TaoUi.Toast(this, "没能打开设置, 请手动到系统设置里给道映授权"); }
                        }, false),
                    });
            });
        }

        protected override void OnDestroy()
        {
            if (_liveAiHost == this) _liveAiHost = null;   // v3.25.8 真机修: 现役实例随页退场
            _scanCts?.Cancel();
            _castWatchCts?.Cancel();   // v3.23: 投屏看守随页退出
            ReleaseDownloadLocks();   // v3.25.20: 下载保持锁随页退场
            ReleaseSeekPreviewResources();
            _host.Dispose();
            base.OnDestroy();
        }

        // ================= 下载保持锁 (v3.25.20) =================
        // 有任务在下载时持有 WifiLock(高性能) + WakeLock(部分): 灭屏后省电激进的机型
        // 大文件下载不再停滞; 任务停了立即释放, 不白耗电。

        private global::Android.Net.Wifi.WifiManager.WifiLock? _dlWifiLock;
        private global::Android.OS.PowerManager.WakeLock? _dlWakeLock;

        private void UpdateDownloadLocks()
        {
            try
            {
                bool need = _downloads.Tasks.Any(t => t.Status == "下载中");
                if (need && _dlWifiLock == null)
                {
                    var wm = (global::Android.Net.Wifi.WifiManager)GetSystemService(global::Android.Content.Context.WifiService)!;
                    var wifiNew = wm.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "道映:download")!;
                    wifiNew.SetReferenceCounted(false);
                    _dlWifiLock = wifiNew;
                }
                if (need && _dlWakeLock == null)
                {
                    var pm = (global::Android.OS.PowerManager)GetSystemService(global::Android.Content.Context.PowerService)!;
                    var wakeNew = pm.NewWakeLock(global::Android.OS.WakeLockFlags.Partial, "道映:download")!;
                    wakeNew.SetReferenceCounted(false);
                    _dlWakeLock = wakeNew;
                }
                var wifi = _dlWifiLock;   // v3.25.21: 局部快照, 字段跨调用不保空值态
                if (wifi != null) { if (need && !wifi.IsHeld) wifi.Acquire(); else if (!need && wifi.IsHeld) wifi.Release(); }
                var wake = _dlWakeLock;
                if (wake != null) { if (need && !wake.IsHeld) wake.Acquire(); else if (!need && wake.IsHeld) wake.Release(); }
            }
            catch { }   // 锁拿不到不挡下载, 顶多灭屏后可能慢
        }

        private void ReleaseDownloadLocks()
        {
            try { if (_dlWifiLock?.IsHeld == true) _dlWifiLock.Release(); } catch { }
            try { if (_dlWakeLock?.IsHeld == true) _dlWakeLock.Release(); } catch { }
            _dlWifiLock = null;
            _dlWakeLock = null;
        }

        // ================= 投屏状态看守 (v3.23 P2-2) =================

        /// <summary>投屏成功后 30 秒间隔问电视「还在放吗」: 连续问不上即提示断开 (电视端回了停止 = 用户按了电视遥控停止)。
        /// v3.23 双检修复: 电视慢启动首连可能超一分钟, 从未连上期间容忍 5 次失败, 连上后连续 2 次问不上才报。</summary>
        internal void StartCastWatch(CastDevice device)
        {
            _castWatchCts?.Cancel();
            _castWatchCts = new CancellationTokenSource();
            var token = _castWatchCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                int nullStreak = 0;
                bool everConnected = false;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await System.Threading.Tasks.Task.Delay(30_000, token);
                        var state = await CastService.TransportStateAsync(device);
                        if (state == null)
                        {
                            nullStreak++;
                            if (nullStreak >= (everConnected ? 2 : 5))
                            {
                                RunOnUiThread(() => TaoUi.Toast(this, "电视投屏已断开", long_: true));
                                return;
                            }
                        }
                        else if (state.Equals("STOPPED", StringComparison.OrdinalIgnoreCase))
                        {
                            RunOnUiThread(() => TaoUi.Toast(this, "电视端已停止播放", long_: true));
                            return;
                        }
                        else
                        {
                            everConnected = true;
                            nullStreak = 0;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }   // 看守线程任何意外都不外泄
            }, token);
        }

        // ================= 设置与权限 =================

        private void EnsureDefaultFolders()
        {
            var s = AppSettings.Current;
            if (s.SaveFolder == "" || !Directory.Exists(s.SaveFolder))
            {
                var dir = new Java.IO.File(GetExternalFilesDir(null), "边播边存");
                s.SaveFolder = dir.AbsolutePath;
            }
            if (s.ScanFolders.Count == 0 || s.ScanFolders.TrueForAll(f => !Directory.Exists(f)))
            {
                s.ScanFolders.Clear();
                foreach (var t in new[] {
                    global::Android.OS.Environment.DirectoryMovies,
                    global::Android.OS.Environment.DirectoryDownloads,
                    global::Android.OS.Environment.DirectoryDcim })
                {
                    var d = global::Android.OS.Environment.GetExternalStoragePublicDirectory(t);
                    if (d != null) s.ScanFolders.Add(d.AbsolutePath);
                }
            }
            SaveSettingsSafe();
        }

        private void RequestStorageAccess()
        {
            // 读取媒体文件: Android 13+ 用 READ_MEDIA_VIDEO, 旧系统用 READ_EXTERNAL_STORAGE, 都是标准弹窗
            var perms = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                ? new[] { "android.permission.READ_MEDIA_VIDEO" }
                : new[] { global::Android.Manifest.Permission.ReadExternalStorage };
            _needRescanOnResume = true;
            RequestPermissions(perms, 1);
        }

        internal bool MediaPermissionGranted =>
            CheckSelfPermission(Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                ? "android.permission.READ_MEDIA_VIDEO"
                : global::Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted;

        /// <summary>v3.25.20: Android 14+ "仅部分照片视频" 授权态——系统给了 READ_MEDIA_VISUAL_USER_SELECTED
        /// 但没给完整 READ_MEDIA_VIDEO, 此时列表只有用户勾选的几条, 必须提示"点这里放开全部", 不然像丢片。</summary>
        internal bool MediaPermissionPartial =>
            Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake
            && CheckSelfPermission("android.permission.READ_MEDIA_VISUAL_USER_SELECTED") == Permission.Granted
            && !MediaPermissionGranted;

        /// <summary>设置落盘统一走后台线程: 主线程写文件会造成卡顿甚至 ANR。
        /// 序列化在入队前完成 (快照), 磁盘写入与可能的并发写串行化, 后写覆盖先写。</summary>
        internal void SaveSettingsSafe()
        {
            var snapshot = AppSettings.Current;
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (_settingsIoLock)
                {
                    try { SettingsFile.Save(snapshot); } catch { }
                }
            });
        }

        // ================= 扫描 =================

        /// <summary>v3.25.13 密码箱功能退场的一次性收尾: 凭旧索引把收进箱里的文件原路搬回原位置,
        /// 没有来历记录的搬进 Movies/道映恢复 目录, 最后清掉索引与箱目录; 全程只搬不删用户视频。
        /// 跑过一遍后再也不进 (索引文件清掉即不会再有第二遍), 失败不挡启动。</summary>
        private void RestoreLegacyVaultFiles()
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var idxPath = System.IO.Path.Combine(AppSettings.BaseDir, "vault-index.json");
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(idxPath))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(idxPath));
                            foreach (var sec in new[] { "Entries", "Pending" })
                                if (doc.RootElement.TryGetProperty(sec, out var obj) && obj.ValueKind == System.Text.Json.JsonValueKind.Object)
                                    foreach (var kv in obj.EnumerateObject())
                                        if (kv.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                            map[kv.Name] = kv.Value.GetString() ?? "";
                        }
                        catch { }
                    }

                    // 箱目录与旧版规则一致: 每个存储卷 Android/data/<包名>/files/vault
                    var vaultDirs = new List<string>();
                    foreach (var vol in StorageVolumes())
                        vaultDirs.Add(System.IO.Path.Combine(vol, "Android", "data", PackageName!, "files", "vault"));

                    // 1) 有账的: 原路搬回; 原位被占就按「名 (2)」编号落回, 保证文件一定回到看得见的地方
                    foreach (var kv in map)
                    {
                        try
                        {
                            if (!File.Exists(kv.Key)) continue;
                            var target = kv.Value;
                            if (string.IsNullOrWhiteSpace(target)) continue;
                            if (File.Exists(target))
                            {
                                var stem = System.IO.Path.GetFileNameWithoutExtension(target);
                                var ext = System.IO.Path.GetExtension(target);
                                for (int i = 2; i < 1000 && File.Exists(target); i++)
                                    target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, $"{stem} ({i}){ext}");
                            }
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                            File.Move(kv.Key, target);
                        }
                        catch { }   // 单个文件失败不挡其余, 文件留原处不丢
                    }

                    // 2) 没账的: 搬进本卷 Movies/道映恢复 (重名自动编号), 然后清掉箱目录
                    foreach (var dir in vaultDirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        try
                        {
                            var recover = System.IO.Path.Combine(global::Android.OS.Environment.GetExternalStoragePublicDirectory(
                                global::Android.OS.Environment.DirectoryMovies)!.AbsolutePath, "道映恢复");
                            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            {
                                try
                                {
                                    var ext = System.IO.Path.GetExtension(f);
                                    if (!FileSystemVideoSource.VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                                    var name = System.IO.Path.GetFileName(f);
                                    var target = System.IO.Path.Combine(recover, name);
                                    var stem = System.IO.Path.GetFileNameWithoutExtension(name);
                                    for (int i = 2; i < 1000 && File.Exists(target); i++)
                                        target = System.IO.Path.Combine(recover, $"{stem} ({i}){ext}");
                                    Directory.CreateDirectory(recover);
                                    File.Move(f, target);
                                }
                                catch { }
                            }
                            Directory.Delete(dir, recursive: true);
                        }
                        catch { }   // 清不掉就留着, 空目录无害
                    }

                    try { if (File.Exists(idxPath)) File.Delete(idxPath); } catch { }
                }
                catch { }
            });
        }

        /// <summary>登记的存储卷根 (与旧版密码箱扫 /storage 的口径一致)。</summary>
        private static List<string> StorageVolumes()
        {
            var vols = new List<string> { "/storage/emulated/0" };
            try
            {
                foreach (var d in new System.IO.DirectoryInfo("/storage").GetDirectories())
                    if (!d.Name.StartsWith("emulated", StringComparison.OrdinalIgnoreCase)) vols.Add(d.FullName);
            }
            catch { }
            return vols;
        }

        internal void StartScan()
        {
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            _videos.Clear(); _filtered.Clear();
            _adapter?.NotifyDataSetChanged();
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _scanGate.Wait(token);
                    try
                    {
                        ScanViaMediaStore(token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        RunOnUiThread(() => _status.Text = $"扫描出了问题: {ex.Message}。点这条文字重试。");
                    }
                    finally { _scanGate.Release(); }
                }
                catch (OperationCanceledException) { }
            });
        }

        /// <summary>用系统媒体库扫描视频 (只需媒体读取权限, 无需所有文件访问)。
        /// 全程带取消令牌: 被更新的扫描接替时, 旧扫描立即退出, 不再碰共享集合。</summary>
        private void ScanViaMediaStore(CancellationToken token)
        {
            string[] proj =
            {
                MediaStore.Video.Media.InterfaceConsts.Id,
                MediaStore.Video.Media.InterfaceConsts.DisplayName,
                MediaStore.Video.Media.InterfaceConsts.Data,
                MediaStore.Video.Media.InterfaceConsts.Size,
                MediaStore.Video.Media.InterfaceConsts.DateModified,
            };
            var found = new List<VideoItem>();
            var uris = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();
            using var cursor = ContentResolver!.Query(MediaStore.Video.Media.ExternalContentUri!, proj, null, null,
                MediaStore.Video.Media.InterfaceConsts.DateModified + " DESC");
            if (cursor != null)
            {
                while (cursor.MoveToNext())
                {
                    token.ThrowIfCancellationRequested();
                    var path = cursor.GetString(2) ?? "";
                    if (path.Length == 0) continue;
                    // 只保留真正的视频文件: 个别机型的媒体库会把图片等杂项混进视频库
                    var ext = System.IO.Path.GetExtension(path);
                    if (!FileSystemVideoSource.VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    // 数据卫生: 藏起来的视频 (点目录/点文件/.nomedia) 不混进普通列表 (Model.PathHiding 同一把尺子)
                    if (PathHiding.IsHiddenPath(path)) continue;
                    found.Add(new VideoItem
                    {
                        Path = path,
                        SizeBytes = cursor.GetLong(3),
                        Modified = DateTimeOffset.FromUnixTimeSeconds(cursor.GetLong(4)).LocalDateTime,
                    });
                    // 记录媒体库 Uri, 供删除时走系统确认流程
                    var mediaUri = MediaStore.Video.Media.ExternalContentUri;
                    if (mediaUri != null)
                        uris[path] = ContentUris.WithAppendedId(mediaUri, cursor.GetLong(0));
                }
            }
            if (token.IsCancellationRequested) return; // 已被新扫描接替, 结果作废
            RunOnUiThread(() =>
            {
                if (token.IsCancellationRequested) return; // 双保险: 排队的界面回调也要作废
                _mediaUris.Clear();
                foreach (var kv in uris) _mediaUris[kv.Key] = kv.Value;
                foreach (var v in found)
                {
                    _videos.Add(v);
                    _filtered.Add(v);
                }
                RebuildFiltered();
                _status.Text = found.Count == 0
                    ? "没找到视频。可以点右上角菜单换扫描目录, 或检查是否给了权限。"
                    : $"扫描完成: {found.Count} 个视频";
                // v3.25.20: "仅部分照片视频"授权时列表缺片, 状态行直接说明并引导放开全部 (状态行可点)
                if (MediaPermissionPartial)
                    _status.Text += " (只显示了已允许的部分视频, 点这里放开全部)";
                PreloadThumbs(0, 24);   // v3.13 扫描完先预解首批预览图, 用户一进列表就有图
            });
        }

        // ================= 菜单与设置 =================

        /// <summary>关于 (v3.13): 右上设置钮的菜单卡片里呼出</summary>
        internal void ShowAbout()
        {
            TaoPopup.ShowMessage(this, "关于",
                "道映 v3.13（安卓重做版）\n本地视频 / 网络流 / 磁力边下边播 / 边播边存\n支持断点续播、字幕音轨与倍速\n\n道映 —— 道法自然，尽收眼底。",
                new (string, Action?, bool)[] { ("好的", null, false) });
        }

        /// <summary>主页菜单卡片 (v3.10): 从设置钮下方右对齐弹出, 带图标按常用性排列;
        /// 排列/最近优先/边播边存/下载管理/重扫/关于全在这里, 右上角只留主题与设置两钮</summary>
        internal void ShowHomeMenu()
        {
            var s = AppSettings.Current;
            TaoPopup.ShowMenu(this, _settingsBtn!, new (IconKind, string, Action)[]
            {
                (CurrentSortIcon(), "排列方式", ToggleGalleryMode),
                (IconKind.Recent, $"最近优先 · {(s.RecentFirst ? "开" : "关")}", ToggleRecentFirst),
                (IconKind.Save, $"边播边存 · {(s.AutoSaveMagnet ? "开" : "关")}", ToggleAutoSave),
                (IconKind.Refresh, $"断线自动重连 · {(s.StreamAutoRetry ? "开" : "关")}", ToggleStreamAutoRetry),   // v3.23
                (IconKind.Cast, $"流访问令牌 · {(s.StreamTokenEnabled ? "开" : "关")}", ToggleStreamToken),   // v3.23
                (IconKind.Magnet, "下载管理", () => _dlPanel.Show()),
                (IconKind.Refresh, "重扫媒体库", StartScan),
                (IconKind.Info, "画质增强（实验）", ShowAiEnhanceDialog),
                (IconKind.Info, "关于", ShowAbout),
            }, belowLeft: true, belowRight: true);
        }

        /// <summary>画质增强（实验）档位选择：默认关；开=按设备分级提示档位。
        /// 自学习口径 v1 = 设备能力分级 + 档位偏好记忆（不宣传"AI 学习"）。
        /// v3.20.0 增设智能自动档：按设备能力拉满偏好上限，热区/电量/功耗守卫实时调档。</summary>
        internal void ShowAiEnhanceDialog()
        {
            var s = AppSettings.Current;
            var tier = (AiEnhanceTier)Math.Max(0, Math.Min(3, s.AiEnhanceTier));
            var runtime = Java.Lang.Runtime.GetRuntime();
            var devTier = AiTierPicker.FromDevice(runtime?.MaxMemory() ?? 0, 0);
            string devHint = devTier switch
            {
                AiEnhanceTier.Off => "本机内存较小，建议保持关闭",
                AiEnhanceTier.Low => "本机建议低档",
                AiEnhanceTier.Mid => "本机建议中档",
                _ => "本机可开高档"
            };
            void Pick(int i)
            {
                AppSettings.Current.AiEnhanceTier = i;
                AppSettings.Current.AiAutoGear = false;
                if (_coordinator != null)
                {
                    _coordinator.Tier = (AiEnhanceTier)i;
                    _runner = new AiSegmentRunner(
                        IsMagnetActive,
                        progress: null,
                        paused: () => _coordinator?.State is AiEnhanceState.PausedGuard or AiEnhanceState.PausedThermal,
                        srTile: AiTierPicker.TileOf((AiEnhanceTier)i),
                        enableInterpolate: i >= 3,
                        srModel: TaoAIKernel.SrModelAnime);
                }
                SaveSettingsSafe();
                UpdateAiStatusText();   // v3.20.3 档位变化同步顶栏状态
                TaoUi.Toast(this, i == 0
                    ? "画质增强已关闭"
                    : $"画质增强已设为{(AiEnhanceTier)i}（{devHint}）");
            }
            void PickAuto()
            {
                AppSettings.Current.AiAutoGear = true;
                if (AppSettings.Current.AiEnhanceTier == 0) AppSettings.Current.AiEnhanceTier = 3;
                var auto = AutoPreferredTier();
                if (_coordinator != null) _coordinator.Tier = auto;
                SaveSettingsSafe();
                UpdateAiStatusText();   // v3.20.3 档位变化同步顶栏状态
                TaoUi.Toast(this, $"智能自动档已开：当前{auto}，发热/耗电时自动降，回常自动拉满");
            }
            TaoPopup.ShowListDialog(this, $"画质增强（实验）\n{devHint}", new (string, Action)[]
            {
                ($"{(tier == AiEnhanceTier.Off && !s.AiAutoGear ? "● " : "")}关闭（默认）", () => Pick(0)),
                ($"{(s.AiAutoGear ? "● " : "")}智能自动（拉满画质·发热耗电自动让路）", () => PickAuto()),
                ($"{(tier == AiEnhanceTier.Low && !s.AiAutoGear ? "● " : "")}低档（后台超分逐段覆盖）", () => Pick(1)),
                ($"{(tier == AiEnhanceTier.Mid && !s.AiAutoGear ? "● " : "")}中档（超分＋更快写回）", () => Pick(2)),
                ($"{(tier == AiEnhanceTier.High && !s.AiAutoGear ? "● " : "")}高档（超分＋插帧·实验）", () => Pick(3)),
            });
        }

        /// <summary>智能自动档的偏好上限（v3.20.0）：按设备内存分级，旗舰直接顶到高档，
        /// 真正的松紧由守卫策略按温度/电量/功耗实时调（一次一档防抖）。</summary>
        internal AiEnhanceTier AutoPreferredTier()
        {
            try
            {
                var am = (ActivityManager?)GetSystemService(ActivityService);
                var mem = new ActivityManager.MemoryInfo();
                am?.GetMemoryInfo(mem);
                return AiTierPicker.FromDevice(mem?.TotalMem ?? 0, 2024);
            }
            catch { return AiEnhanceTier.Mid; }
        }

        // ================= 通用小工具 =================

        internal static string FormatMs(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
        }

        internal void SaveTsIfRecording()
        {
            if (_host.Recording) _host.Recording = false;   // v3.10: 胶囊已删, 落旗标即可, 开关状态永远跟随设置
        }

        /// <summary>黑匣子: 任何崩溃都写入应用日志目录, 供排查</summary>
        internal void SaveCrash(Exception? ex)
        {
            try
            {
                var dir = new Java.IO.File(GetExternalFilesDir(null), "logs");
                dir.Mkdirs();
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir.AbsolutePath, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"),
                    DateTime.Now + "\n" + ex);
            }
            catch { }
        }
    }
}
