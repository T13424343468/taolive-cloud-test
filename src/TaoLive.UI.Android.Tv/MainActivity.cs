using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading; // vc45②：Interlocked 轮转游标
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables; // v22：弹窗自绘 GradientDrawable/ColorDrawable 需要（退出/崩溃日志弹窗）
using Android.OS;
using Android.Views;
using Android.Widget;
using TaoLive.BLL;
using TaoLive.Model;
using TaoLive.UI.Android.Tv.Shared;

namespace TaoLive.UI.Android.Tv;

/// <summary>
/// 道映主界面（电视端）：开机即看 → 默认上次频道；
/// 上下键切台；菜单键呼出频道列表（焦点式，分类树形可折叠+收藏置顶+宽度随内容）；
/// 左右键列表翻页/呼出（v1.0.8 拍板）；设置键单击=软件设置面板、长按=系统设置（v1.0.8 拍板）；
/// 确认键收藏当前台；音量走遥控独立硬件键，软件不接管；无数字键（用户真机遥控无数字区）。
/// 设计基线：单实例 singleTask + onNewIntent（B 档秒开回上次频道）。
/// </summary>
[Activity(
    Label = "TaoLive",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Landscape,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
[IntentFilter(
    new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryLauncher, Intent.CategoryLeanbackLauncher })]
public class MainActivity : Activity
{
    private ChannelCatalog _catalog = null!;
    private StreamResolver _resolver = null!;
    private DynamicSourceStore _dynStore = null!; // vc45②：动态源库（搜源验活收录，落盘 dynamic_sources.json）
    private System.Threading.Timer? _scoutTimer; // vc45②：常规级探索节拍
    private int _scoutCursor; // vc45②：轮转游标（每轮顺播清单走，不反复磨同几台）
    private LiveVideoHost _host = null!;
    private FavoriteStore _favorites = null!;
    private ChannelPrecheck _precheck = null!;  // 开机预检握手（v1.0.8）：各台可连状态
    private System.Threading.Timer? _ticketKeeper; // 票据静默续期（v1.0.8）：当前台定期换票
    private ChannelAdapter? _adapter;

    private FrameLayout _root = null!;
    private FrameLayout _videoContainer = null!;
    private FrameLayout _loadingPanel = null!;
    private TextView _loadingText = null!;
    private TextView _infoBar = null!;
    private LinearLayout _listPanel = null!;
    private ListView? _listView;
    private bool _listOpen;
    // B 包第 12 项：列表页改五标签抽屉（1.0.12 UI② 改序：全部/收藏/央视/卫视/地方台），左右键切标签
    private DrawerSupport.Tab _drawerTab = DrawerSupport.Tab.All;
    private List<TextView>? _tabViews;
    private const int DrawerPad = 24; // vc41 自适应体系：页边距=1.5U（48→24，老板 20:17 拍板「左侧留白太大」收半，三层同口径）
    // B 包第 14 项：底部节目单（央视网官方接口，没数据不硬造）；v22 第 12 项：亮出 7 秒无操作自动收起
    private LinearLayout? _epgPanel;
    private TextView? _epgNow;
    private TextView? _epgNext;
    private CntvEpgClient? _epgClient;
    private System.Threading.CancellationTokenSource? _epgCts;
    private bool _epgHasData;                 // 当前台拉到过节目单（抽屉收起后据此重新亮出）
    private Action? _epgHidePending;          // 7 秒自动收起任务
    private const int EpgAutoHideMs = 7000;
    // v22 第 1 项·确定性清场：换台/重接即铺不透明遮罩盖住旧表面，首帧前遮罩维持（ghost 旧合成帧绝不外露）
    private View? _videoMask;
    private int _playSeq;
    private Channel? _current;
    private InfoLineHub _infoHub = new(); // A②：信息行单写入口（防重刷总闸+优先级让位）
    private bool _carrierTexture;      // 本次会话是否纹理载体（v1.0.7 启动自检决定）
    private bool _frameEverShown;      // 本次启动是否出过画面：出过则换台先留旧画面，延迟亮牌
    private bool _frameForCurrentSwitch; // 本轮换台是否已出首帧
    private Action? _pendingLoading;   // 延迟亮牌任务（换台后仍无首帧才亮）
    private Action? _loadingAnimRun;   // 亮牌期间的「正在连接」点动画
    private int _loadingSeq;
    private Handler _ui = new Handler(Looper.MainLooper!);

    // 退出确认（15:01 老板拍板）：播放态按返回弹窗二次确认；确认后退出界面，
    // 后台保活服务继续跑（进程不结束），重开即热启动。弹窗开着时返回键=取消继续播放。
    private AlertDialog? _exitDialog;
    private bool _resumedOnce;  // 首启 OnResume 与回前台重接的分流（v1.0.9）

    // 收藏排序模式（v1.0.8 第14项，用户 23:23 拍板）：独立状态机，退出恢复默认键位
    private bool _sortMode;
    private Channel? _sortChannel;
    private readonly List<int> _sortBackup = new(); // 返回键取消复原用

    // 软件设置面板（v1.0.8，用户拍板）：底部滑入透明面板；设置键 4077 单击呼出、长按跳系统设置
    private LinearLayout? _settingsPanel;
    private ScrollView? _settingsScroll; // vc42④：面板内容滚动容器（行数增多时面板不撑出屏幕）
    private LinearLayout? _settingsList; // vc42④：设置行容器（滚动内层）
    private bool _settingsPanelOpen;
    private bool _settingsLongFired;
    private bool _precheckEnabled = true;   // 循环预检开关（设置面板可切，持久化）
    private bool _warmupEnabled = true;     // A⑥：后台预热开关（细案 §二设置页，持久化）
    private bool _autostartEnabled = true; // v22 第 15 项：开机自启安装默认开启（启动时按已存设置覆盖）
    private bool _abi32;                     // 启动时设备档位（超高清过滤与日志标注用）
    private long _totalMem;
    private global::Android.Content.ISharedPreferences? _prefs;
    private System.Threading.CancellationTokenSource? _precheckCts;

    // v23 第 1 条：折叠交互整行退场——foldToggle 总开关行与 _expanded/_expandedInit 折叠状态一并清，
    // 列表默认铺开不留空行（BLL 的 ListFoldController 纯逻辑保留不动，测试断言不破）。

    // v1.0.9 第④项：列表旁操作提示去掉，改为实时画面信息（v22 第 9 项改短格式：帧率+分辨率一行、画质一行）
    private TextView? _statsInfo;   // v1.0.9 第④项：列表旁实时画面信息（帧率+分辨率短格式）
    private System.Threading.Timer? _statsTicker;
    // v1.0.9 第⑦/⑪项：重建列表的焦点锚点（分类键或 C:频道号），锚定原位置不跳头
    private string? _pendingFocusAnchor;
    // v1.0.9 第⑬项：本台会话实际取播的来源变体（健康表优先取最近验证可用来源）
    private Channel? _playSource;
    // v1.0.10 第①项：源健康账本地存储（应用私有目录，跨启动保留已验证源）
    private SourceHealthStore? _healthStore;
    private SourceScoreStore? _scoreStore; // v28 主线③：源健康度评分账（与健康账同生命周期）
    private long _tuneBeginMs; // v28 主线③：换台起点（首帧耗时 = 首帧时刻 − 起点）
    private Task? _healthLoadTask; // v28 读盘优化：健康账后台灌回任务（落盘须等灌回，防空账覆盖旧账）
    private readonly SourceBackoffLedger _backoff = new(); // A⑬/A⑥：同一本 403 退避账（竞速与预热共用）
    private readonly Dictionary<string, long> _nextProbeAtMs = new(); // A⑥：每台下次到期时刻（TickCount64）
    private volatile bool _foregroundBusy; // A⑥ PauseGate：前台换台/连接期间预热让路

    // ---- v25 落码新增 ----
    private readonly ChannelPaceLedger _pace = new(); // 修法六：台级表现账+时段系数+全局让路闸（稳态循环与屏内先探共享同一闸）
    private readonly System.Collections.Concurrent.ConcurrentQueue<Channel> _prefetchQueue = new(); // 修法一 5：邻台预取改入队（PauseGate 空闲才消费，不再前台抢流量）
    private readonly HashSet<string> _probeInFlight = new(StringComparer.Ordinal); // 屏内先探去重：同台一次只跑一个探测任务
    private readonly object _probeInFlightLock = new();
    private readonly Dictionary<string, long> _deadUntilMs = new(StringComparer.Ordinal); // 修法六 4：失效台独立档（判死移出主循环，30 分钟低频复测）
    private readonly Dictionary<string, int> _deadStreak = new(StringComparer.Ordinal);
    private long _lastScreenPrecheckMs; // 屏内先探 ≥3 秒节流
    private HotStandbyKeeper? _hotStandby; // 修法七：热备并行取流（新台连接完成 5 秒后起，换台即停）
    private BroadcastReceiver? _envReceiver; // 修法二 2c/2d：网络变更/时钟跳变清账（账本不跨 IP/跨墙钟沿用）
    private long _lastTimeWallMs = -1;       // vc38: 时钟分治基准（上次墙钟，-1=未立基准）
    private long _lastTimeTickMs = -1;       // vc38: 时钟分治基准（上次单时钟，-1=未立基准）

    /// <summary>像素/dp 收敛（ui-remote P1 可缓项，vc38 落）：布局尺寸统一经此换算，不再散写裸像素——
    /// 此前多处 SetPadding/边距直接给整数像素，换密度设备上偏小/失准。</summary>
    private int Dp(float v) => Shared.UiScale.FromDp(v); // vc41 自适应体系：16dp=1U，全部间距 ∝ 屏宽，启动定标后运行期不变

    private int _listRefreshQueued;  // vc38 全表刷新风暴收敛：合帧在途标记
    private string? _lastListSig;    // vc38 状态签名：无变化不重绘
    private const int ListRefreshDebounceMs = 400; // 合帧窗口：预检逐台回报合并为一帧

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        TaoPalette.Select(0); // 水墨默认
        // vc42 显示大小：档位系数随偏好（大=4/3/中=1/小=2/3，老板 21:08 定稿），重建后清冻结重定标
        float dispFactor = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!.GetFloat("ui_scale", 4f / 9f);
        Shared.UiScale.Reset();
        Shared.UiScale.Init(Resources!.DisplayMetrics!, dispFactor); // vc41 自适应体系：启动定标一次，全套令牌冻结

        // 全局兜底：未捕获异常先落日志再放行，崩溃原因可查可报，不再无声闪退（v1.0.7）
        Java.Lang.Thread.DefaultUncaughtExceptionHandler =
            new UncaughtCrashPeer(Java.Lang.Thread.DefaultUncaughtExceptionHandler);

        // 低配设备档位：32 位芯片或内存不足 3GB 软解带不动 4K/8K，超高清台直接不进节目单（雷一：带不动就别点）
        _abi32 = Build.SupportedAbis?.Any(a => a == "armeabi-v7a") == true;
        var am = (ActivityManager)GetSystemService(ActivityService)!;
        var mem = new ActivityManager.MemoryInfo();
        am.GetMemoryInfo(mem);
        _totalMem = mem.TotalMem;
        DeviceProfile.HideUhd = _abi32 || mem.TotalMem < 3L * 1024 * 1024 * 1024;

        // 持久化设置（v1.0.8）：预检/预热/自启开关跨启动记忆
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        _precheckEnabled = _prefs.GetBoolean("precheck_enabled", true);
        _warmupEnabled = _prefs.GetBoolean("warmup_enabled", true);       // A⑥：后台预热（默认开）
        _autostartEnabled = _prefs.GetBoolean("autostart_enabled", true); // v22 第 15 项：新装默认开（读取的是已存设置，与 BootReceiver/SettingsStore 同口径）

        // 调参档接线（老板 2026-11-12 令：旋钮全量进设置页+一键还原）：启动读偏好进 BLL 门面，各策略读门面
        AdvancedTuning.Apply(
            _prefs.GetInt("adv_fail_gate", AdvancedTuning.DefaultFailGate),
            _prefs.GetInt("adv_reprobe_min", 5) * 60_000L,
            _prefs.GetInt("adv_cache_sec", AdvancedTuning.DefaultListCacheSec),
            _prefs.GetInt("adv_retry_base_sec", 120) * 1000L,
            _prefs.GetInt("adv_retry_max_min", 120) * 60_000L,
            _prefs.GetInt("adv_proxy_port", AdvancedTuning.DefaultProxyPort));

        // 源档案接线（上善若水纪律）：档案库与 source_health.json 同目录载入，机制取样持续回写
        SourceProfileStore.Reporter = msg => CrashNet.Record(msg); // BLL 不反引 UI：打点委托在此注入
        SourceProfileStore.Shared.Load(FilesDir!.AbsolutePath);

        _favorites = new FavoriteStore();
        _favorites.Restore(_prefs?.GetString("favorites", null)); // 收藏顺序跨启动记忆（v1.0.8 第14项）
        _catalog = new ChannelCatalog(_favorites);
        _catalog.RegisterSource(new YangshipinSource());
        _catalog.RegisterSource(new CztvSource());
        _catalog.RegisterSource(new GzstvSource());
        // 攻克名单（2026-11-07 全量真连 83/100 后按用户指令暂时下架，逐路攻克后接回）：
        // - 上海 kankan 8 路：取流仅认上海本地出口 IP，沙箱 0/8，待用户真机复测再定去留；
        // - 江苏 jstv 3 路：沙箱 DNS 解析不到流域名，待真机复测；
        // - 广西 gxtv：2026-11-08 电视频道口径接口恢复（POST channelList 直吐 encodeM3u8），
        //   本地 7 路 + CETV-2/4 接回；广西卫视/CETV-1 央视频已覆盖不重号。
        _catalog.RegisterSource(new XatvSource());
        _catalog.RegisterSource(new NmtvSource());
        _catalog.RegisterSource(new GxtvSource());
        _catalog.RegisterSource(new CityCandidatesSource()); // v22 第 16 项：地方台 18 路候选源挂链（13 城市+2 省经济，台账 city_channel_candidates.json）
        _catalog.Rebuild();
        _resolver = new StreamResolver();
        // vc45②：选源智能化接线——动态源库（落盘回灌+变更即存 dynamic_sources.json）+
        // 救急级搜源触发（全候选耗尽立即搜）+ 候选发现器（公开免费清单，验活才入库）。
        // 常规级低频探索见 StartRoutineScout()（空闲时段驱动同一管线）。
        var dynStore = _dynStore = new DynamicSourceStore();
        Task.Run(() => DynamicSourceRegistry.Load(dynStore, FilesDir!.AbsolutePath));
        dynStore.OnChanged = () =>
        {
            try { DynamicSourceRegistry.Save(dynStore, FilesDir!.AbsolutePath); }
            catch (Exception se) { CrashNet.Record($"[Scout] 动态源落盘失败（功能不残废）：{se.Message}"); }
        };
        _resolver.DynamicSources = dynStore;
        _resolver.Scout = new SourceScout(new SourceIntelligenceEngine(), dynStore);
        _resolver.DiscoverCandidates = PublicListDiscover.FindAsync;
        StartRoutineScout();
        _host = new LiveVideoHost { TotalMemBytes = _totalMem, UiDispatcher = a => _ui.Post(a) }; // ⑩内存水位自适应口径注入；PL4 载体重挂回 UI 线程
        _hotStandby = new HotStandbyKeeper(_resolver, _backoff); // v25 修法七：热备与预热共用同一本退避账（不建第二本）
        // v25 修法七 2b：热备帧级切换接线——LiveVideoHost 断流/自救前先问热备（独立预算 90 秒窗 4 次）
        _host.HotStandbySwitch = async () =>
        {
            var keeper = _hotStandby;
            if (keeper is null) return null;
            return await keeper.TrySwitchAsync();
        };
        WireHostEvents();
        RegisterEnvReceiver(); // v25 修法二 2c/2d：网络变更/时钟跳变清账

        // 开机预检握手（v1.0.8）：全台按序轻探（取票/头分片，不建视频传输），限速防判异常。
        // 预检后台进行，不阻塞当前频道播放；状态实时刷新进呼出列表。
        _precheck = new ChannelPrecheck(ProbeChannelAsync, intervalMs: 600);
        // v1.0.10 第①项：源健康账落盘本地——重启后已验证源直接可用，换台不用从零再探
        // A 类第 7 项：央视频 guid 设备身份持久化，与源健康账同目录落盘
        DeviceIdentity.Attach(FilesDir!.AbsolutePath);
        // A 类第6项：预热落盘层（/files/warmup/，与 source_health.json 并列）——长效源起播零网络探测
        _resolver.WarmupDisk = new WarmupStore(System.IO.Path.Combine(FilesDir!.AbsolutePath, "warmup"));
        // v28 读盘优化：预热缓存灌回改后台预载（两本账内部均有锁/并发字典，与首播并发访问无竞态；损坏文件按无缓存处理口径不变）
        Task.Run(_resolver.WarmupDisk.Load);
        // A 类第6项：App 打开即拉起后台预热服务（同进程共享同一预热引擎，绝不起第二份）
        try
        {
            var svc = new Intent(this, typeof(WarmupService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O && _warmupEnabled) StartForegroundService(svc);
            else if (_warmupEnabled) StartService(svc);
        }
        catch (Exception svcEx) { CrashNet.Record($"[Warmup] 保活服务拉起失败（功能不残废）：{svcEx.Message}"); }
        var healthStore = _healthStore = new SourceHealthStore(FilesDir!.AbsolutePath);
        // v28 主线③：评分账与健康账同生命周期——启动灌回→挂预检（选源按分排序；账空退化既有顺序）
        var scoreStore = _scoreStore = new SourceScoreStore(FilesDir.AbsolutePath);
        // v28 读盘优化：健康账读盘改后台；账本灌回完成才挂预检（共账切换）、才进预检循环（顺序依赖）
        _healthLoadTask = Task.Run(() => { healthStore.Load(); scoreStore.Load(); _precheck.AttachScoreStore(scoreStore); });
        _precheck.StateChanged += (key, state) => _ui.Post(() =>
        {
            RefreshListStatuses();
            if (state == ChannelHealth.Dead) OnCurrentDeadByPrecheck(key); // 默认台不可用跳台/亮「无信号」（第16项）
        });

        // 画面载体启动自检（v1.0.7）：纹理载体若上次没活到首帧，永久回退稳定载体，绝不再闪退
        _carrierTexture = CarrierPolicy.ResolveStartupCarrier(this);
        try
        {
            if (_carrierTexture) CarrierPolicy.MarkAttempt(this);
            BuildUi(_carrierTexture);
            // 开机即看：默认上次频道
            Play(DefaultChannel());
            StartBackgroundLoops(healthStore);
        }
        catch (Exception ex)
        {
            // 纹理载体接线当场报错：记录原因，永久回退稳定载体，同一次启动内重建，不闪退
            CrashNet.Record(ex.ToString());
            CarrierPolicy.DisableTexture(this, "启动接线异常: " + ex.Message);
            try
            {
                try { _host.Dispose(); } catch { }
                _host = new LiveVideoHost { TotalMemBytes = _totalMem, UiDispatcher = a => _ui.Post(a) }; // PL4 载体重挂回 UI 线程
                WireHostEvents();
                _carrierTexture = false;
                BuildUi(false);
                Play(DefaultChannel());
                StartBackgroundLoops(healthStore); // #8：回退分支同样补齐定时器，不再只救画面
                Toast.MakeText(this, $"画面载体已自动回退稳定模式（{ex.Message}）", ToastLength.Long)!.Show();
            }
            catch (Exception fatal)
            {
                CrashNet.Record("兜底回退仍失败: " + fatal);
                throw;
            }
        }
    }

    /// <summary>#8 修法：OnCreate 主干与载体回退分支共用的后台循环启动——
    /// 循环预检（v1.0.8 用户拍板，常驻后台轮，保态复测列表不闪烁；全列表 83 路 × 600ms ≈ 50 秒/轮 +
    /// 轮间歇 20 秒 ≈ 1~1.5 分钟一轮，限速防判异常流量）、票据静默续期每 4 分钟。</summary>
    private void StartBackgroundLoops(SourceHealthStore healthStore)
    {
        _precheckCts = new System.Threading.CancellationTokenSource();
        var precheckCts = _precheckCts;
        // v28 读盘优化：预检循环由健康账灌回任务串行启动——灌回完成 → 挂账（共账切换）→ 进循环
        _ = Task.Run(async () =>
        {
            await _healthLoadTask!;
            _precheck.AttachHealthStore(healthStore);
            await PrecheckLoopAsync(precheckCts.Token);
        });
        // 画面信息行已随第二刀摘除，刷新 ticker 一并停发（不创建）
        _ticketKeeper = new System.Threading.Timer(async _ =>
        {
            var cur = _current;
            if (cur is null) return;
            try { await _resolver.ResolvePlayUrlAsync(cur, force: true); }
            catch (Exception ex) { CrashNet.Record($"[TicketKeeper] 续票失败下个周期再试：{ex.Message}"); }
        }, null, 240_000, 240_000);
    }

    /// <summary>播放宿主事件接线（载体重建时随宿主重挂）。</summary>
    private void WireHostEvents()
    {
        // 重连前重新取票（v1.0.8）：换台代数守卫在宿主内，这里只管拿新鲜入口
        // ⑬联动：优先取本台会话实际使用的来源变体（健康表最近验证可用者）
        // 1.0.12 发现 A·备援接入重连链路：换票委托分级切——第 1 次重连同源换票（普通卡顿秒级恢复）；
        // 第 2 次起同源失败即走备援选择（健康表优先最近验证可用来源），备援成功记健康表，
        // 恢复自动回主源（SourceFailover 现成机制复用）。
        _host.ReconnectRefresher = async (attempt) =>
        {
            var src = _playSource ?? _current;
            if (src is null) return null;
            ResolvedStream? fresh;
            if (attempt >= 2)
            {
                CrashNet.Record($"[重连] 连续第 {attempt} 次换票，同源转备援选择（1.0.12 发现 A）");
                fresh = await _resolver.ResolveWithFailoverAsync(src, force: true);
                if (fresh != null)
                    _precheck.ReportSourceResult(fresh.Channel, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // 备援成功记健康表
            }
            else
            {
                fresh = await _resolver.ResolvePlayUrlAsync(src, force: true);
            }
            if (fresh is null) return null;
            return new RefreshedStream { Url = fresh.Url, Options = fresh.Options };
        };
        _host.StatusChanged += text => _ui.Post(() =>
        {
            // 文案分流（v1.0.8）：
            // - 最终失败（请稍后重试或换台）→ 撤遮罩 + 信息条；
            // - 重连等待期 → 亮牌保持/亮起（绝不撤），首帧落地才撤；
            // - 「连接中…」→ 不动遮罩（换台延迟亮牌由 Play 流程管理，不得提前打断）。
            if (text.Contains("请稍后重试或换台"))
            {
                HideLoading();
                FlashInfo(text);
            }
            else if (text.Contains("重连"))
            {
                if (_current != null) ShowLoading(_current); // 重连等待期亮牌：明确「没死机」
                FlashInfo(text);
            }
            else
            {
                FlashInfo(text);
            }
        });
        _host.FrameShown += () => _ui.Post(() =>
        {
            _frameEverShown = true;            // 首帧落地：延迟亮牌基准开启，此后换台先留旧画面
            _frameForCurrentSwitch = true;
            _foregroundBusy = false;           // A⑥ PauseGate：连接完成，预热恢复
            if (_carrierTexture) CarrierPolicy.MarkOk(this); // 纹理载体活到首帧：判兼容
            if (_current != null) _precheck.MarkState(_current, ChannelHealth.Alive); // 播放实测存活
            _videoContainer?.SetBackgroundColor(Color.Transparent); // ⑦新流首帧到齐：撤底色亮画面
            _videoMask.Visibility = ViewStates.Gone; // v22 第 1 项：首帧落地撤清场遮罩，旧合成帧全程未外露
            // ⑬健康表回写：播放实测成功覆盖预检结论（取播来源变体优先；Unix 毫秒，落盘可比）
            // v28 主线③：同一处喂评分账——首帧成功+换台起点到首帧耗时（速度分），timeout=false
            if (_playSource != null)
            {
                var firstFrameMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _tuneBeginMs);
                _precheck.ReportSourceResult(_playSource, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), firstFrameMs);
            }
            HideLoading();
        });
    }

    private void BuildUi(bool useTexture)
    {
        var (bg, ink, accent) = TaoPalette.Current;
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.ParseColor(bg));

        // 1. 全屏视频层：纹理载体（退后台不回收）或表面载体（稳定档），按启动自检选择
        _videoContainer = new FrameLayout(this);
        if (useTexture)
        {
            var videoView = new LiveTextureView(this) { LayoutParameters = new FrameLayout.LayoutParams(-1, -1) };
            _videoContainer.AddView(videoView);
            _host.Attach(videoView);
        }
        else
        {
            var videoView = new LiveSurfaceView(this) { LayoutParameters = new FrameLayout.LayoutParams(-1, -1) };
            _videoContainer.AddView(videoView);
            _host.Attach(videoView);
        }
        _root.AddView(_videoContainer);

        // 1.2 v22 第 1 项·确定性清场遮罩：夹在视频与亮牌之间，换台立即铺上，首帧才撤
        _videoMask = new View(this) { Visibility = ViewStates.Gone };
        _videoMask.SetBackgroundColor(Color.ParseColor(TaoPalette.Current.Bg));
        _videoContainer.AddView(_videoMask, new FrameLayout.LayoutParams(-1, -1));

        // 1.5 起播亮牌（雷二·延迟亮）：换台后 0.8 秒内没出首帧才亮，亮了带台名和呼吸点，
        // 明确反馈「没死机」，首帧落地才撤
        _loadingPanel = new FrameLayout(this);
        _loadingPanel.SetBackgroundColor(InkTranslucency.LoadingColor(this)); // v1.0.9 第③项：底色随亮度自适应
        _loadingText = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = UiScale.T(1.5f),
        };
        _loadingText.SetTextColor(InkTranslucency.TextPrimary(this));
        _loadingPanel.AddView(_loadingText, new FrameLayout.LayoutParams(-1, -2)
        {
            Gravity = GravityFlags.Center,
        });
        _loadingPanel.Visibility = ViewStates.Gone;
        _videoContainer.AddView(_loadingPanel, new FrameLayout.LayoutParams(-1, -1));

        // 2. 切台信息条（顶部居中，3 秒自动隐藏）
        _infoBar = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Invisible,
        };
        _infoBar.SetTextColor(InkTranslucency.TextPrimary(this));
        _infoBar.SetBackgroundColor(InkTranslucency.InfoColor(this)); // v1.0.9 第③项：底色随亮度自适应
        InkTranslucency.ApplyReadabilityShadow(_infoBar);
        _infoBar.SetPadding(Dp(36), Dp(16), Dp(36), Dp(16));
        _infoBar.TextSize = UiScale.T(1.375f);
        _root.AddView(_infoBar, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = Dp(48),
        });

        // 3. 底部节目单（B 包第 14 项）：官方接口有数据才显示（没数据不硬造）
        // v22 第 13 项·z 序修正：节目单条先入根布局，抽屉盖板永远压在其上，绝不叠字
        _epgPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _epgPanel.SetBackgroundColor(InkTranslucency.PanelColor(this));
        _epgPanel.SetPadding(Dp(36), Dp(18), Dp(36), Dp(22));
        _epgPanel.Visibility = ViewStates.Gone;
        _epgNow = new TextView(this) { TextSize = UiScale.T(1.125f) };
        _epgNow.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_epgNow);
        _epgNext = new TextView(this) { TextSize = UiScale.T(0.9375f) };
        _epgNext.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_epgNext);
        _epgPanel.AddView(_epgNow);
        _epgPanel.AddView(_epgNext);
        _root.AddView(_epgPanel, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.Left,
        });

        // 4. 频道列表（菜单键呼出，焦点式；B 包第 12 项改五标签抽屉：全部/央视/卫视/地方台/收藏）
        _listPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listPanel.SetBackgroundColor(InkTranslucency.PanelColor(this)); // v1.0.9 第③项：底色加深并随亮度自适应
        _listPanel.Visibility = ViewStates.Gone;
        _root.AddView(_listPanel, new FrameLayout.LayoutParams(-1, -1));

        SetContentView(_root);
    }

    /// <summary>
    /// 开机默认台（v1.0.8 第16项，用户 23:28 拍板）：收藏非空→收藏第一个；
    /// 收藏空→常规列表第一个（中央一台固定第一台，测试断言兜底）；
    /// 预检判死的台跳过，全部不可用返回 null 由调用方亮「无信号」。
    /// </summary>
    private Channel? DefaultChannel() =>
        StartupPicker.Pick(_favorites.TopGroup(_catalog.All.ToList()), _catalog.All, c => _precheck.Of(c));

    /// <summary>
    /// 预检判死当前台（第16项衔接）：用户已在看的台（首帧已出）不擅自切台——交给看门狗重连；
    /// 还没出画面的台（含开机默认台）预检判死即跳下一个可用台，全死亮「无信号」。
    /// </summary>
    private void OnCurrentDeadByPrecheck(string epgKey)
    {
        if (_current is null || _current.EpgKey != epgKey) return;
        if (_frameForCurrentSwitch || _host.IsAlive) return; // 用户在看：看门狗接管，不打扰
        var all = _catalog.All;
        if (all.Count == 0) return;
        var list = all.ToList();
        var idx = list.FindIndex(c => c.EpgKey == epgKey);
        for (int i = 1; i <= list.Count; i++)
        {
            var cand = list[((idx + i) % list.Count + list.Count) % list.Count];
            if (_precheck.Of(cand) == ChannelHealth.Dead) continue;
            if (cand.EpgKey == epgKey) break; // 绕一圈只剩自己：全死
            FlashInfo($"{_current.Name} 无信号，切到 {cand.Name}");
            Play(cand);
            return;
        }
        FlashInfo("无信号");
    }

    // vc45② 常规级：空闲低频探索——每 30 分钟一轮、每轮最多 3 台轮转（方案定稿 §四「只在空闲时段执行」）：
    // 换台 5 分钟内整轮让路（不抢播放带宽）；搜源管线内受静默校验与预算约束，验活才入库。
    private void StartRoutineScout()
    {
        _scoutTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var scout = _resolver.Scout;
                var discover = _resolver.DiscoverCandidates;
                if (scout == null || discover == null) return;
                var all = _catalog.All.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
                if (all.Count == 0) return;
                for (int i = 0; i < 3; i++)
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _tuneBeginMs < 5 * 60_000) return; // 换台即收工
                    var ch = all[Math.Abs(Interlocked.Increment(ref _scoutCursor)) % all.Count];
                    var known = FallbackSourceRegistry.Has(ch.Name) || (_dynStore?.Has(ch.Name) ?? false);
                    await scout.RunAsync(ch.Name,
                        () => discover(ch.Name),
                        c => _resolver.ValidateCandidateAsync(c.Url, c.Referer,
                            string.IsNullOrEmpty(c.UserAgent) ? ch.UserAgent : c.UserAgent),
                        existingBestScore: 0, // 动态源=与内置表并行的备源，入库判重由库收口（同 URL 更新不叠加）
                        channelKnown: known);
                }
            }
            catch (Exception se) { CrashNet.Record($"[Scout] 常规探索异常（功能不残废）：{se.Message}"); }
        }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    private void Play(Channel? ch)
    {
        if (ch is null) return;
        _hotStandby?.Stop(); // v25 修法七 1c：换台即停热备（会话代数作废，校验流不再漏跑）
        _foregroundBusy = true; // A⑥ PauseGate：换台/连接期间预热让路
        int seq = ++_playSeq;
        _current = ch;
        _frameForCurrentSwitch = false;
        _tuneBeginMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // v28 主线③：换台起点（首帧耗时喂入评分账）
        CloseChannelList();
        // v1.0.10 修复⑦：撤旧画面的同时铺干净底色，GPU 残留旧缓冲绝不外露（花屏竖条）；
        // 新流首帧到齐才撤底色亮画面（FrameShown 处清回透明）
        _videoContainer?.SetBackgroundColor(Color.ParseColor(TaoPalette.Current.Bg));
        // v22 第 1 项·确定性清场：不透明遮罩立即盖住旧表面（换台/重取流/内核重建全走这里），
        // ghost 旧合成帧从根上清掉；首帧前遮罩维持，新画面从纯底上亮出
        _videoMask.Visibility = ViewStates.Visible;
        // 雷二·延迟亮牌：清场遮罩已在（纯底无字），0.8 秒内新画面出来直接静音换流无感；
        // 没出来才在遮罩上亮「正在连接」品牌牌，且亮着就不撤直到首帧
        CancelPendingLoading();
        if (!_frameEverShown)
        {
            ShowLoading(ch); // 冷启动无旧画面可留，直接亮牌
        }
        else
        {
            _pendingLoading = () =>
            {
                if (seq == _playSeq && !_frameForCurrentSwitch) ShowLoading(ch);
            };
            _ui.PostDelayed(_pendingLoading, 800);
        }
        ShowInfoBar($"{ch.Number}  {ch.Name}  连接中…");

        var resolver = _resolver;
        Task.Run(async () =>
        {
            // ⑬/A⑬播放选源：健康账已验证优先（零新增请求）；无验证时同频道多来源竞速（4 秒预算）
            var source = await PickSourceAsync(ch);
            if (seq != _playSeq) return;
            _playSource = source;
            ResolvedStream? resolved = null;
            string kind = nameof(ConnectFailureKind.None); // v28③修复：提升作用域，非异常路径（空清单判死）也能带稳定度口径回写
            try
            {
                // v1.0.10 第①项：优先直连健康账里已验证的来源；取不到流时若换过源，
                // 现场回退本台原始来源现取新流兜底——缓存只加速，绝不把台连死
                if (_frameEverShown)
                {
                    resolved = await resolver.ResolvePlayUrlAsync(source);
                }
                else
                {
                    // A 类第4项·首切快失败：4 秒预算（总装统一口径），超时立即转备援，不让首切干等 10 秒
                    var main = resolver.ResolvePlayUrlAsync(source);
                    var winner = await Task.WhenAny(main, Task.Delay(4000));
                    if (winner == main)
                    {
                        resolved = await main;
                    }
                    else
                    {
                        CrashNet.Record($"[首切] {ch.Name} 主取流 4 秒未归，快失败转备援（A④）");
                        resolved = await resolver.ResolveWithFailoverAsync(source);
                        if (resolved is null) resolved = await main; // 备援也空：回主取流结果兜底，不轻易判死
                    }
                }
                if (seq != _playSeq) return; // 换台后旧任务作废
                if (resolved is null && !ReferenceEquals(source, ch))
                    resolved = await resolver.ResolvePlayUrlAsync(ch, force: true);
            }
            catch (Exception ex)
            {
                // ⑫失败矩阵实接：逐类归因落日志 + 对应提示（不笼统「连接失败」）；v22 第 2 项：限流提示接退避账
                kind = LiveVideoHost.FailureKind(ex);
                CrashNet.Record($"[ConnectFailure][{kind}] 取流异常 {ch.Name}（源={ch.SourceId}）：{ex.Message}");
                resolved = null;
                if (kind == nameof(ConnectFailureKind.RateLimited))
                {
                    var rem = _backoff.RemainingMs(ch.SourceId, System.Environment.TickCount64); // 退避账剩余时长，写死一分钟作废
                    _ui.Post(() => { if (seq == _playSeq) FlashInfo(DrawerSupport.RateLimitHint(ch.Name, rem)); });
                }
                else
                {
                    _ui.Post(() => { if (seq == _playSeq) FlashInfo(ConnectHint(kind)); });
                }
            }
            if (seq != _playSeq) return;
            if (resolved is null)
            {
                _precheck.ReportSourceResult(source, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    timeout: kind == nameof(ConnectFailureKind.ConnectTimeout)); // ⑬：取流失败回写健康表；v28③超时喂稳定度负项
                _ui.Post(() =>
                {
                    if (seq != _playSeq) return;
                    _foregroundBusy = false; // A⑥ PauseGate：取流失败也让路（重试由分级自救承担）
                    HideLoading();
                    FlashInfo($"{ch.Name}  暂时没接上，先看别的台");
                });
                return;
            }
            // 透传头原样并入选项列表（v1.0.8）；画质增强摘除后不再并滤镜
            var opts = new List<(string Key, string Value)>(resolved.Options);
            // 起播打点（v22 第 18 项口径）：参数整串原样入日志可对账
            CrashNet.Record($"[起播] {ch.Name}：选项 {(string.Join(";", opts.Select(o => $"{o.Key}={o.Value}")))}");
            _ui.Post(() =>
            {
                if (IsFinishing || IsDestroyed) return; // UI#2：页面退出后回调不落播放器
                if (seq != _playSeq) return;
                _host.Play(resolved.Url, opts);
                ShowInfoBar($"{ch.Number}  {ch.Name}");
                RefreshEpg(ch); // B 包第 14 项：进台成功即拉官方节目单（换台即刷新，无数据不显示）
                // v25 修法七 1c：连接完成 5 秒后（Keeper 内延迟）起热备校验流——备援候选悄悄拉一路轻校验
                var uaOpt = resolved.Options.FirstOrDefault(o => o.Key == "http-user-agent").Value;
                _hotStandby?.Start(seq, ch, resolved.Url, ch.Referer, string.IsNullOrEmpty(uaOpt) ? ch.UserAgent : uaOpt);
            });
        });

        PrefetchNeighbors(ch);
    }

    /// <summary>⑫逐类提示（ConnectFailure 口径）：不笼统「连接失败」，每类给对应说法。
    /// B 包第 3 项：限流/风控类给原因+预期时长，用户心里有底。</summary>
    private static string ConnectHint(string kind) => kind switch
    {
        nameof(ConnectFailureKind.DnsFailure) => "网络解析不了该台，正在自动换备用来源",
        nameof(ConnectFailureKind.ConnectTimeout) => "电视没联网或网络不稳，请检查网络",
        nameof(ConnectFailureKind.RateLimited) => "取流被限流，正在按退避计划自动重试，先看别的台也行", // v22 第 2 项：写死的一分钟作废，实际时长走退避账
        nameof(ConnectFailureKind.TimeSkew) => "电视时间不对，请校准时间后重试",
        nameof(ConnectFailureKind.Rejected) => "该台暂时被拒绝访问，正在自动换来源",
        nameof(ConnectFailureKind.DecoderSurface) => "播放器内部错误，正在自动重试",
        _ => "暂时连不上，正在自动重试",
    };

    /// <summary>预检轻探（v1.0.8）：换票成功即服务器可达；直连源只取几 KB 头数据验证，不建视频传输。</summary>
    private async Task<bool> ProbeChannelAsync(Channel ch)
        => await ProbeChannelVerdictAsync(ch) == StreamResolver.ProbeVerdict.Reachable;

    /// <summary>v23 修法 B：探测结论分级——Reachable=通；Rejected=服务器明确拒绝（唯一入拉黑账）；
    /// Unreachable=网络级失败（换票失败/超时/断网/非 403/429），不回灌整源冻结。</summary>
    private async Task<StreamResolver.ProbeVerdict> ProbeChannelVerdictAsync(Channel ch)
    {
        var r = await _resolver.ResolvePlayUrlAsync(ch);
        if (r is null) return StreamResolver.ProbeVerdict.Unreachable; // 换票失败无法区分 403 与网络失败：按不回灌处理（票据源另有 30 秒冷却兜底）
        if (!string.Equals(r.Url, ch.StreamUrl, StringComparison.Ordinal))
            return StreamResolver.ProbeVerdict.Reachable; // 换票成功=取票服务器可达且票有效
        return await _resolver.ProbeUrlVerdictAsync(r.Url, ch.Referer, ch.UserAgent);
    }

    /// <summary>
    /// v25 修法一 5：邻台预取改入队——换台瞬间只登记不启动任务；
    /// 消费在 PrecheckLoop 每轮收尾（PauseGate 空闲 + 让路闸占到才发），不再前台抢流量（死参数口子收掉）。
    /// </summary>
    private void PrefetchNeighbors(Channel ch)
    {
        var all = _catalog.All;
        if (all.Count == 0) return;
        var idx = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Number != ch.Number) continue;
            idx = i;
            break;
        }
        if (idx < 0) return;
        var up = all[(idx - 1 + all.Count) % all.Count];
        var down = all[(idx + 1) % all.Count];
        foreach (var nb in new[] { up, down })
        {
            if (nb.Number == ch.Number) continue;
            _prefetchQueue.Enqueue(nb); // 只入队：消费在预热循环收尾（PauseGate 空闲才执行）
        }
    }

    /// <summary>修法一 5：每轮收尾消费一个预取任务——PauseGate 挂起时不动，让路闸未占到时排回队尾（节律不破坏）。</summary>
    private void DrainPrefetchQueue()
    {
        if (_foregroundBusy) return; // PauseGate 挂起：预取就地等待，下轮再消费
        if (!_prefetchQueue.TryDequeue(out var nb)) return;
        var pace = WarmupPolicy.PaceOf(nb.SourceId);
        if (!_pace.TryAcquireGate(nb.SourceId, System.Environment.TickCount64, pace.MinGapMs))
        {
            _prefetchQueue.Enqueue(nb); // 未占到闸：排回队尾
            return;
        }
        Task.Run(async () => { try { await _resolver.ResolvePlayUrlAsync(nb); } catch (Exception ex) { CrashNet.Record($"[Prefetch] 邻台预取失败：{ex.Message}"); } });
    }

    // ---- 循环预检（v1.0.8 用户拍板：频率越高越好，以不被判异常流量为前提）----

    /// <summary>
    /// 常驻循环预检：每轮保态复测（可用台常绿、无信号台留组，探测完才翻转），轮间歇 20 秒；
    /// 单台间隔 600ms + 央视频取票 403 冷却兜底，全程不被判异常流量。关掉开关时空转低耗等待。
    /// </summary>
    /// <summary>
    /// A 类第6项·预热降频调度（细案 §五）：从「20 秒一轮全表」改「按源保鲜期调度」——
    /// 每台按 WarmupPolicy 节拍到期才探（央视频 10 分钟/台，nmtv/gxtv 整表 30 分钟/6 小时一次），
    /// 403 退避期源挂起不发起，前台换台/连接期间预热让路，关掉开关时空转低耗等待。
    /// 稳态央视频 63 台 × 10 分钟 ≈ 6 次/分钟（现行约 63 次/20 秒，降两个数量级）。
    /// </summary>
    private async Task PrecheckLoopAsync(System.Threading.CancellationToken ct)
    {
        try
        {
            // 冷启动让路（细案 §五.4）：全表首轮刷新推迟 3 分钟，起播零探测由播放链路与落盘承担
            await Task.Delay(180_000, ct);
        }
        catch (global::System.OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_precheckEnabled) await RunDueProbesAsync(ct);
                DrainPrefetchQueue(); // v25 修法一 5：预取只在此消费（PauseGate 空闲 + 让路闸占到才发）
                await Task.Delay(5_000, ct);
            }
            catch (global::System.OperationCanceledException) { return; }
            catch (Exception ex) { CrashNet.Record($"[Precheck] 预检单轮失败下一轮再来：{ex.Message}"); }
        }
    }

    /// <summary>
    /// A⑥单轮：只探「已到期且未退避且前台空闲」的台；首见台登记节拍不立即探（读盘起播口径）。
    /// v25 落码（修法一/二/六）：
    /// - 首见登记与到期登记一律 = now + RegisterIntervalMs（档位→时段→jitter 完整口径，1c：jitter 同表分布一致不漂移）；
    /// - 全局让路闸（1a）：稳态循环与屏内先探共享，发起前原子占位（MinGap 原值不翻倍），两方同刻先到先探；
    /// - 票账联动（2）：探测前查票据退避剩余>0 顺延不发起——退避期弹回不入源账不记失败（失败口径=明确拒绝才计负）；
    /// - 失效台独立档（4）：连续 3 轮服务器明确拒绝（403/429）判死移出主循环，独立 30 分钟低频复测，
    ///   连续 3 轮仍死拉长 2 小时，任一轮恢复（200）立即回归正常档；
    /// - 台级账记账（1b/1c）：Reachable 计正；Rejected 计负；Unreachable（网络级）不计负；
    ///   整表源不做台级三档（节拍按源），时段系数照走（低敏感深夜降功耗）；
    /// - 整表源真聚合（nmtv/gxtv）：UI 层仍逐台登记探测，整表抓取在 resolver 源级聚合（60 秒 TTL 内同源只发 1 次）。
    /// </summary>
    private async Task RunDueProbesAsync(System.Threading.CancellationToken ct)
    {
        long now = System.Environment.TickCount64;
        if (_foregroundBusy) return; // PauseGate：前台连接优先，预热就地挂起，下轮再看（不消耗到期时间）
        int localHour = DateTime.Now.Hour; // 设备本地小时（时钟不对系数错位可接受，TimeSkew 已有兜底）
        foreach (var ch in _catalog.All)
        {
            if (ct.IsCancellationRequested) return;
            if (_foregroundBusy) return;
            var key = $"{ch.SourceId}:{ch.EpgKey}";
            bool batch = WarmupPolicy.IsBatchSource(ch.SourceId);
            long baseMs = WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs;

            // 修法六 4：失效台独立档——未到复测时刻的判死台不进主循环（到期的台走下方同一探测路径）
            if (_deadUntilMs.TryGetValue(key, out var deadDue))
            {
                if (now < deadDue) continue;
            }
            else if (!_nextProbeAtMs.TryGetValue(key, out var due))
            {
                // 修法一 1：首见登记 = now + 保鲜期 × 档位 × 时段 × jitter（EpgKey 稳定哈希，同表分布一致）
                _nextProbeAtMs[key] = now + _pace.RegisterIntervalMs(ch.EpgKey, ch.SourceId, baseMs, localHour, batch);
                continue; // 首见只登记，不立即探
            }
            else if (now < due) continue;

            // 修法二 2：票账联动——票据源退避剩余>0 顺延不发起（退避期弹回不入源账不记失败）
            if (_resolver.TicketRemainingCooldownMs(ch) > 0)
            {
                _nextProbeAtMs[key] = now + 60_000;
                continue;
            }
            // 修法一 1a：全局让路闸原子占位（稳态循环与屏内先探共享同一闸；MinGap 原值不翻倍）
            if (!_pace.TryAcquireGate(ch.SourceId, now, WarmupPolicy.PaceOf(ch.SourceId).MinGapMs))
                continue; // 未占到闸：登记不推进，下轮再看（不消耗到期时间）
            _nextProbeAtMs[key] = now + _pace.RegisterIntervalMs(ch.EpgKey, ch.SourceId, baseMs, localHour, batch);
            try
            {
                // v23 修法 B：探测结论分级——拉黑账只认服务器明确拒绝（403/429），
                // 网络级失败不再回灌整源冻结（切后台网络冷启动不再把好源拉黑十分钟）
                var verdict = await ProbeChannelVerdictAsync(ch).ConfigureAwait(false);
                bool ok = verdict == StreamResolver.ProbeVerdict.Reachable;
                _precheck.ReportSourceResult(ch, ok, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                // v22 第 4 项：退避打点——入账/恢复真机可验收（退避期内不发起）
                if (verdict == StreamResolver.ProbeVerdict.Rejected)
                {
                    _backoff.RecordLimited(ch.SourceId, System.Environment.TickCount64); // 403/429 明确拒绝：照旧入退避账
                    _pace.Record(ch.EpgKey, ch.SourceId, false); // 修法六 1b：明确拒绝计负
                    CrashNet.Record($"[退避] {ch.SourceId} 入账：冻结 {TimeSpan.FromMilliseconds(_backoff.RemainingMs(ch.SourceId, System.Environment.TickCount64)).TotalMinutes:F0} 分钟（服务器明确拒绝 403/429）");
                    // 修法六 4：连续 3 轮明确拒绝 → 判死进独立档（复测 30 分钟；再连 3 轮仍死拉长 2 小时）
                    int streak = _deadStreak.TryGetValue(key, out var s) ? s + 1 : 1;
                    _deadStreak[key] = streak;
                    if (streak >= 6)
                    {
                        _deadUntilMs[key] = System.Environment.TickCount64 + 7_200_000L; // 连续 6 轮仍死：拉长 2 小时（幂等覆盖）
                    }
                    else if (streak >= 3 && !_deadUntilMs.ContainsKey(key))
                    {
                        _deadUntilMs[key] = System.Environment.TickCount64 + 1_800_000L;
                        CrashNet.Record($"[预热] {ch.Name} 连续 {streak} 轮明确拒绝：移出主循环，独立档 30 分钟后复测");
                    }
                }
                else if (!ok)
                {
                    _pace.Record(ch.EpgKey, ch.SourceId, true); // 修法六 1b：网络级失败不计负（账本中性）
                    CrashNet.Record($"[退避] {ch.SourceId} 网络级失败：不入拉黑账（v23 修法 B），下轮再探");
                }
                else
                {
                    _pace.Record(ch.EpgKey, ch.SourceId, true); // 修法六 1b：探通计正
                    if (_backoff.IsFrozen(ch.SourceId, System.Environment.TickCount64))
                    {
                        _backoff.Recover(ch.SourceId);
                        CrashNet.Record($"[退避] {ch.SourceId} 探测 200：全额恢复，解除冻结");
                    }
                    // 修法六 4：任一轮恢复立即回归正常档
                    if (_deadUntilMs.Remove(key))
                    {
                        _deadStreak.Remove(key);
                        CrashNet.Record($"[预热] {ch.Name} 失效台复测恢复（200）：立即回归正常档");
                    }
                }
            }
            catch { _precheck.ReportSourceResult(ch, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
        }
    }

    // ---- 软件设置面板（v1.0.8 用户拍板：设置键 4077 单击呼出、长按跳系统设置）----

    /// <summary>
    /// ⑨「去系统设置」三级跳（v1.0.9 用户拍板）：对口设置页（显示/画质）→ 系统设置总入口 → 文字指引，
    /// 分级降级，绝不硬撑；哪级成立用哪级。
    /// </summary>
    private void OpenSystemSettings()
    {
        foreach (var action in new[]
        {
            global::Android.Provider.Settings.ActionDisplaySettings, // ①对口设置页
            global::Android.Provider.Settings.ActionSettings,         // ②系统设置总入口
        })
        {
            try
            {
                StartActivity(new Intent(action));
                return;
            }
            catch (Exception ex) { CrashNet.Record($"[MemoryLevel] 内存分级调不动降级下一级：{ex.Message}"); }
        }
        FlashInfo("请按遥控器「设置/菜单」键进入电视系统设置"); // ③文字指引
    }

    private void ToggleSettingsPanel()
    {
        if (_settingsPanelOpen) CloseSettingsPanel(); else OpenSettingsPanel();
    }

    private void OpenSettingsPanel()
    {
        _settingsPanel ??= BuildSettingsPanel();
        _settingsPanelOpen = true;
        RefreshSettingsRows();
        _settingsPanel.Visibility = ViewStates.Visible;
        // 从底部滑入（透明底，尽量少遮挡画面）
        var slide = new global::Android.Views.Animations.TranslateAnimation(0f, 0f, 420f, 0f)
            { Duration = 220 };
        _settingsPanel.StartAnimation(slide);
        _settingsPanel.RequestFocus();
    }

    private void CloseSettingsPanel()
    {
        _settingsPanelOpen = false;
        if (_settingsPanel != null) _settingsPanel.Visibility = ViewStates.Gone;
    }

    private LinearLayout BuildSettingsPanel()
    {
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetBackgroundColor(InkTranslucency.PanelColor(this)); // v1.0.9 第③项：底色加深并随亮度自适应
        panel.SetPadding(Dp(40), Dp(28), Dp(40), Dp(36));
        panel.SetMinimumWidth(Dp(360)); // vc41 自适应体系：定比令牌
        panel.Visibility = ViewStates.Gone;

        // vc42④：面板内套 ScrollView，行多时整体可滚，面板高度不超出屏幕
        var maxHeight = (int)(Resources!.DisplayMetrics!.HeightPixels * 0.9f);
        _settingsScroll = new MaxHeightScrollView(this, maxHeight); // 内容超高即滚动
        _settingsList = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _settingsScroll.AddView(_settingsList, new FrameLayout.LayoutParams(-2, -2));

        panel.AddView(_settingsScroll, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
        });
        _root.AddView(panel, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
        });
        return panel;
    }

    /// <summary>vc42④：内容高度封顶的 ScrollView——测子页时以 maxHeight 上限收口，行多则滚动。</summary>
    private sealed class MaxHeightScrollView : ScrollView
    {
        private readonly int _maxHeight;
        public MaxHeightScrollView(Context c, int maxH) : base(c)
        {
            _maxHeight = maxH;
            HorizontalScrollBarEnabled = false;
            VerticalScrollBarEnabled = false; // vc42④：滚动条关闭，保持面板观感干净
            Focusable = false; // vc42④：滚动容器不截焦点，光标照常落在设置行上（否则上下键变成滚动面板）
        }
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            base.OnMeasure(widthMeasureSpec,
                MeasureSpec.MakeMeasureSpec(_maxHeight, MeasureSpecMode.AtMost));
        }
    }

    private void RefreshSettingsRows()
    {
        if (_settingsPanel is null || _settingsList is null) return;
        // v23 第 2 条：重建前记录焦点行索引（行数固定、索引稳定），重建后焦点回原行；
        // 1.0 UI⑤：打开崩溃日志即收起设置面板，日志期间不会重建焦点（面板已隐藏）。
        int focusedIdx = -1;
        var focused = CurrentFocus;
        for (int i = 0; i < _settingsList.ChildCount; i++)
            if (_settingsList.GetChildAt(i) == focused) { focusedIdx = i; break; }
        _settingsList.RemoveAllViews();

        void AddLabel(string text)
        {
            var tv = new TextView(this) { Text = text, TextSize = UiScale.T(0.9375f) };
            tv.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.SetPadding(Dp(24), Dp(18), Dp(24), Dp(6));
            _settingsList!.AddView(tv);
        }
        void AddRow(string text, Action onClick, string color, Action? onLongClick = null)
        {
            var tv = new TextView(this) { Text = text, TextSize = UiScale.T(1.1875f) };
            tv.SetTextColor(Color.ParseColor(color));
            tv.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
            tv.Focusable = true;
            // 全局透明化（v1.0.8 第15项）：面板行焦点同样走统一透明效果层
            var focusDrawable = InkTranslucency.FocusDrawable();
            tv.FocusChange += (_, e) =>
            {
                if (e.HasFocus) tv.Background = focusDrawable;
                else if (tv.Background == focusDrawable) tv.Background = null;
            };
            tv.Click += (_, _) => onClick();
            if (onLongClick != null) tv.LongClick += (_, _) => onLongClick();
            _settingsList!.AddView(tv);
        }

        AddLabel("── 软件设置 ──");
        AddRow($"预检握手：{(_precheckEnabled ? "开" : "关")}（确认键切换）",
            TogglePrecheck, "#F7F5EE");
        AddRow($"后台预热：{(_warmupEnabled ? "开" : "关")}（确认键切换）",
            ToggleWarmup, "#F7F5EE");
        AddRow($"开机自启：{(_autostartEnabled ? "开" : "关")}（确认切换 · 长按跳系统自启管理）",
            ToggleAutostart, "#F7F5EE", OpenAutostartSystemPage);
        AddRow($"显示大小：{DisplaySizeLabel()}（确认键切换）", CycleDisplaySize, "#F7F5EE"); // vc42：大→中→小循环，档位=原尺寸的倍率
        AddRow("崩溃日志（点按查看）", ShowCrashLog, "#C9D6E2");
        AddRow("自诊断（点按查看）", ShowDiagPanel, "#C9D6E2"); // S3（vc38）：五类摘要只读一页+导出
        // 调参档接线（老板 2026-11-12 令）：进阶调优二级页——旋钮全量可调+每项讲清作用+一键还原
        AddRow("进阶调优（点按进入）", ShowAdvancedTuningPanel, "#C9D6E2");
        // B 包第 2 项：砍掉「电视系统」整段入口——系统设置由遥控器设置键长按直达，软件内不再列
        AddLabel("返回键收起 · 长按设置键直达系统设置");

        // v23 第 2 条：重建完成，焦点回原行（确认操作后光标停在当前行，不再让给别的行）
        if (focusedIdx >= 0 && focusedIdx < _settingsList.ChildCount)
        {
            var row = _settingsList.GetChildAt(focusedIdx);
            if (row is not null && row.Focusable)
            {
                row.RequestFocus();
                // vc42④：滚动容器内，光标行滚入可视区，避免焦点落在面板外
                row.RequestRectangleOnScreen(new Rect(0, row.Top, row.Right, row.Bottom), true);
            }
        }
    }

    private void TogglePrecheck()
    {
        _precheckEnabled = !_precheckEnabled;
        _prefs?.Edit()?.PutBoolean("precheck_enabled", _precheckEnabled)?.Apply();
        RefreshSettingsRows();
        FlashInfo(_precheckEnabled ? "预检握手：开" : "预检握手：关");
    }

    /// <summary>A⑥：后台预热开关（关=保活服务退出+调度空转，功能不残废）。</summary>
    private void ToggleWarmup()
    {
        _warmupEnabled = !_warmupEnabled;
        _prefs?.Edit()?.PutBoolean("warmup_enabled", _warmupEnabled)?.Apply();
        try
        {
            if (_warmupEnabled) StartForegroundService(new Intent(this, typeof(WarmupService)));
            else StopService(new Intent(this, typeof(WarmupService)));
        }
        catch (Exception ex) { CrashNet.Record($"[Warmup] 开关切失败（不影响预热调度）：{ex.Message}"); }
        RefreshSettingsRows();
        FlashInfo(_warmupEnabled ? "后台预热：开" : "后台预热：关");
    }

    // vc42 显示大小（老板 21:08 定稿；21:47 令调整梯子）：以 vc42 小档（2/3）为新大档基准，中/小各再减自身 1/3
    // → 大=2/3、中=4/9、小=8/27；面板/字/间距同一系数整体缩放，默认仍「中」
    private static readonly float[] DisplaySizeSteps = { 2f / 3f, 4f / 9f, 8f / 27f };
    private static readonly string[] DisplaySizeLabels = { "大", "中", "小" };

    private static int DisplaySizeIndex(float f)
    {
        for (int i = 0; i < DisplaySizeSteps.Length; i++)
            if (Math.Abs(DisplaySizeSteps[i] - f) < 0.01f) return i;
        return 1; // 未知值回落「中」
    }

    private string DisplaySizeLabel() => DisplaySizeLabels[DisplaySizeIndex(
        GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!.GetFloat("ui_scale", 4f / 9f))];

    /// <summary>vc42 显示大小：大→中→小循环，档位记忆；整界面重建即时生效（正在看的台自动接回）。</summary>
    private void CycleDisplaySize()
    {
        var prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        int next = (DisplaySizeIndex(prefs.GetFloat("ui_scale", 4f / 9f)) + 1) % DisplaySizeSteps.Length;
        prefs.Edit()?.PutFloat("ui_scale", DisplaySizeSteps[next])?.Apply();
        Recreate(); // 重建走 OnCreate 重定标，全套令牌按新档位重算
    }

    /// <summary>v22 第 7 项：跳系统自启管理——三级降级：应用详情页 → 应用列表 → 文字指引，绝不硬撑。</summary>
    private void OpenAutostartSystemPage()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings);
            intent.SetData(global::Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(intent);
            FlashInfo("正在打开系统应用页：请允许「自启动/后台运行」权限");
            CrashNet.Record("[自启] 跳系统应用详情页成功");
            return;
        }
        catch (Exception ex1) { CrashNet.Record($"[自启] 应用详情页跳转失败：{ex1.Message}"); }
        try
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionApplicationSettings));
            FlashInfo("正在打开系统应用列表：找到 TaoLive，允许「自启动/后台运行」");
            CrashNet.Record("[自启] 跳系统应用列表成功（详情页不可用降级）");
            return;
        }
        catch (Exception ex2) { CrashNet.Record($"[自启] 应用列表跳转失败：{ex2.Message}"); }
        FlashInfo("系统未开放跳转：请到 电视设置 → 应用管理 → TaoLive，允许自启动");
        CrashNet.Record("[自启] 系统页均不可达，退文字指引");
    }

    /// <summary>A⑥：开机自启开关（Android 3.1+ 首次自启须先开过一次 App；TCL ROM 以实测为准）。</summary>
    private void ToggleAutostart()
    {
        _autostartEnabled = !_autostartEnabled;
        _prefs?.Edit()?.PutBoolean("autostart_enabled", _autostartEnabled)?.Apply();
        RefreshSettingsRows();
        FlashInfo(_autostartEnabled ? "开机自启：开（首次生效需先开过一次 App）" : "开机自启：关");
    }

    private void ShowCrashLog()
    {
        try
        {
            var path = new Java.IO.File(FilesDir, "taolive_crash.log");
            if (!path.Exists()) { FlashInfo("暂无崩溃日志"); return; } // 空态提示（v1.0.9 第⑩项）
            var text = System.IO.File.ReadAllText(path.AbsolutePath);
            if (text.Length > 16000) text = "…\n" + text[^16000..];
            // B 包第 9 项：崩溃日志窗改全屏水墨面板——深底大字可滚动，确认键一键清理、返回键关闭
            // v22 第 10 项改版（老板 20:37 拍板，整段参照第 11 项退出弹窗做法）：全屏自管——窗底透明+半透明水墨面板，
            // 不再露 ROM 钢蓝灰；「一键清理」=朱砂（危险项，同退出钮口径）、「关闭」=纸白；常驻=水墨深底+次色字；
            // 字体统一 TextPrimary/TextSecondary+可读性阴影；默认焦点落「关闭」（防误清日志）；返回键=关闭。
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int pad = Dp(24); // vc41 自适应体系
            box.SetPadding(pad, pad, pad, pad);
            box.SetBackgroundColor(InkTranslucency.PanelColor(this)); // 与列表页面板同源同号
            var title = new TextView(this) { Text = "崩溃日志（全屏）", TextSize = UiScale.T(1.375f) };
            title.SetTextColor(InkTranslucency.TextPrimary(this));
            InkTranslucency.ApplyReadabilityShadow(title);
            box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = pad });
            var scroll = new ScrollView(this) { };
            var body = new TextView(this) { Text = text, TextSize = UiScale.T(0.9375f) };
            body.SetTextIsSelectable(false);
            body.Focusable = false; // 日志正文不抢焦点，焦点只在两键间转
            body.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(body);
            scroll.AddView(body);
            box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f)); // 权重铺满
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var clean = new TextView(this) { Text = "一键清理", Gravity = GravityFlags.Center }; // TextView 自绘，去 ROM 系统着色
            clean.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
            clean.Click += (_, _) =>
            {
                try { System.IO.File.Delete(path.AbsolutePath); FlashInfo("崩溃日志已清空"); }
                catch { FlashInfo("清理失败，请重试"); }
                CloseCrashPanel();
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
            close.Click += (_, _) => CloseCrashPanel();
            clean.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            clean.NextFocusRightId = close.Id; // 左右键显式互指，不靠焦点搜索
            close.NextFocusLeftId = clean.Id;
            StyleOverlayButton(clean, InkTranslucency.AccentFocusDrawable()); // 一键清理=朱砂（危险项）
            StyleOverlayButton(close, InkTranslucency.FocusDrawable()); // 关闭=纸白
            // 1.0.12 UI①：按钮行固定底栏——固定行高常驻占位，不论日志多少行按钮始终在屏幕底部固定位置
            int btnH = Dp(52);
            btns.AddView(clean, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });
            // 1.0 正式版 UI④（老板拍板）：崩溃日志窗改自管全屏面板——不再套 AlertDialog
            // （根因=对话框浮动容器吃边，全屏配置在真机上仍留边、内容缩半屏）。直接往 _root
            // 顶层铺一层全屏面板，打开时同步收起设置面板（两层页面不叠显）；上下键显式接管滚动。
            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true }; // 吃掉触摸防穿透
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1)); // box 显式铺满，不留 ROM 内边距
            _crashOverlay = overlay;
            _crashScroll = scroll;
            _crashCleanBtn = clean;
            _crashCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel(); // 1.0 UI⑤：打开日志页即收起设置页，两层页面不叠显
            close.RequestFocus(); // 默认焦点落「关闭」——防误清日志，与退出弹窗默认落「取消」同理
        }
        catch { FlashInfo("日志读取失败"); }
    }

    private FrameLayout? _crashOverlay; // 1.0 UI④：自管全屏崩溃日志面板（非 AlertDialog，不再吃边）
    private ScrollView? _crashScroll;   // 日志正文滚动区（上下键显式接管）
    private TextView? _crashCleanBtn;
    private TextView? _crashCloseBtn;

    private FrameLayout? _diagOverlay;   // S3（vc38）：自诊断只读全屏面板（同崩溃日志面板自管打法）
    private ScrollView? _diagScroll;     // 诊断摘要滚动区
    private TextView? _diagExportBtn;    // 导出：完整诊断文件复制到文件管理器可见目录
    private TextView? _diagCloseBtn;

    private FrameLayout? _advOverlay;    // 调参档接线（老板 2026-11-12 令）：进阶调优全屏面板（自诊断同打法）

    /// <summary>自管面板按钮统一描边（自崩溃日志窗提取，诊断窗共用，ui-remote 归一条款）。</summary>
    private void StyleOverlayButton(TextView b, GradientDrawable focusDrawable)
    {
        var idleBg = new ColorDrawable(Color.Argb(96, InkTranslucency.InkR, InkTranslucency.InkG, InkTranslucency.InkB)); // 常驻=水墨深底
        b.Focusable = true;
        b.Clickable = true;
        b.Background = idleBg;
        b.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(b);
        b.FocusChange += (_, ev) =>
        {
            if (ev.HasFocus) { b.Background = focusDrawable; b.SetTextColor(InkTranslucency.TextPrimary(this)); }
            else if (b.Background == focusDrawable) { b.Background = idleBg; b.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary)); }
        };
    }

    /// <summary>关闭自管日志面板并回到设置页原位（打开日志页时设置页已收起，退出时原样回来）。</summary>
    private void CloseCrashPanel()
    {
        if (_crashOverlay is null) return;
        _root.RemoveView(_crashOverlay);
        _crashOverlay.Dispose();
        _crashOverlay = null;
        _crashScroll = null;
        _crashCleanBtn = null;
        _crashCloseBtn = null;
        OpenSettingsPanel();
    }

    /// <summary>
    /// S3 自诊断（老板 2026-11-12 拍板带上）：五类现成数据（版本/时钟/网络/启动取流/健康账）
    /// 只读一页大字展示 + 「导出日志」复制完整诊断文件到文件管理器可见目录。
    /// 不新增采集、不外传、不碰播放链路（方案：S3自诊断日志-最小方案.md）。
    /// </summary>
    private void ShowDiagPanel()
    {
        try
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int pad = Dp(24);
            box.SetPadding(pad, pad, pad, pad);
            box.SetBackgroundColor(InkTranslucency.PanelColor(this));
            var title = new TextView(this) { Text = "自诊断（只读）", TextSize = UiScale.T(1.375f) };
            title.SetTextColor(InkTranslucency.TextPrimary(this));
            InkTranslucency.ApplyReadabilityShadow(title);
            box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = pad });
            var scroll = new ScrollView(this) { };
            var body = new TextView(this) { Text = BuildDiagSnapshot(), TextSize = UiScale.T(0.9375f) };
            body.SetTextIsSelectable(false);
            body.Focusable = false; // 焦点只在两钮间转，正文不抢
            body.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(body);
            scroll.AddView(body);
            box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var export = new TextView(this) { Text = "导出日志", Gravity = GravityFlags.Center };
            export.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
            export.Click += (_, _) =>
            {
                var path = DiagLog.Export(ApplicationContext!, BuildDiagSnapshot());
                CrashNet.Record(path is null ? "[诊断] 导出失败" : $"[诊断] 已导出：{path}");
                FlashInfo(path is null ? "导出失败，请重试" : "已导出到文件管理器可见目录");
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
            close.Click += (_, _) => CloseDiagPanel();
            export.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            export.NextFocusRightId = close.Id;
            close.NextFocusLeftId = export.Id;
            StyleOverlayButton(export, InkTranslucency.FocusDrawable());
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(52); // 按钮行固定底栏（同崩溃日志窗口径）
            btns.AddView(export, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });
            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _diagOverlay = overlay;
            _diagScroll = scroll;
            _diagExportBtn = export;
            _diagCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel(); // 打开诊断页即收起设置页，两层页面不叠显
            close.RequestFocus(); // 默认焦点落「关闭」——防误触，与崩溃日志窗同理
        }
        catch (Exception ex) { CrashNet.Record($"[诊断] 打开失败：{ex.Message}"); FlashInfo("诊断打开失败"); }
    }

    private void CloseDiagPanel()
    {
        if (_diagOverlay is null) return;
        _root.RemoveView(_diagOverlay);
        _diagOverlay.Dispose();
        _diagOverlay = null;
        _diagScroll = null;
        _diagExportBtn = null;
        _diagCloseBtn = null;
        OpenSettingsPanel();
    }

    // ---- 进阶调优面板（调参档接线·老板 2026-11-12 令：旋钮全量进设置页、每项讲清作用、支持一键还原） ----

    /// <summary>旋钮档位（确认键循环）。默认值加 * 标注；改动即存偏好并即时生效（端口项下次播放生效）。</summary>
    private static readonly int[] AdvGateSteps = { 1, 2, 3, 5, 8, 12 };
    private static readonly int[] AdvReprobeMinSteps = { 1, 2, 5, 10, 15 };
    private static readonly int[] AdvCacheSecSteps = { 15, 30, 60, 120, 300 };
    private static readonly int[] AdvRetrySecSteps = { 30, 60, 120, 300, 600 }; // 起步；封顶恒 2 小时
    private static readonly int[] AdvPortSteps = { 47618, 47628, 47638, 47648, 49618 };

    private static int NextStep(int[] steps, int current)
    {
        for (int i = 0; i < steps.Length; i++)
            if (steps[i] > current) return steps[i];
        return steps[^1]; // 到最大档即停（老板 20:27 令：边界限位，不循环回绕）
    }

    private static int PrevStep(int[] steps, int current)
    {
        for (int i = steps.Length - 1; i >= 0; i--)
            if (steps[i] < current) return steps[i];
        return steps[0]; // 到最小档即停（同上，边界限位）
    }

    private void ShowAdvancedTuningPanel()
    {
        try
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int pad = Dp(24);
            box.SetPadding(pad, pad, pad, pad);
            box.SetBackgroundColor(InkTranslucency.PanelColorDeep(this)); // vc41：底色加深一档，不跟画面亮度走（老板 20:23 令）
            var title = new TextView(this) { Text = "进阶调优", TextSize = Shared.UiScale.T(1.5f) };
            title.SetTextColor(InkTranslucency.TextPrimary(this));
            InkTranslucency.ApplyReadabilityShadow(title);
            box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(10) });

            // vc41：内容区套滚动+权重撑开，按钮行固定沉底（照自诊断/崩溃日志窗口径，老板 20:23 拍板「按钮沉底」）
            var scroll = new ScrollView(this) { };
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            scroll.AddView(body);
            box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

            var refreshers = new List<Action>();

            // vc41 加减号交互（老板 20:25-20:27 定稿）：每项「− 当前值 +」——值纯展示不落光标，
            // 光标只在 −/+ 两个焦点位间移（左右切加减、上下跨项、确认键减/加）；
            // 加减真改值且有界：预设最大/最小档，到边界即停不回绕，边界处对应钮灰显失效——彻底避开输入法/无数字键遥控。
            void AddKnob(string desc, int[] steps, Func<string> valueText, Action<int> set, Func<int> cur)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetPadding(Dp(24), Dp(12), Dp(24), Dp(2)); // vc41：紧凑对齐自诊断（上 20→12、缩进统一 24）

                var minus = new TextView(this) { Text = "−", Gravity = GravityFlags.Center };
                var value = new TextView(this) { Gravity = GravityFlags.Start }; // vc45 病根1：值靠左与说明对齐（原居中致上下行错位）
                var plus = new TextView(this) { Text = "+", Gravity = GravityFlags.Center };

                int btn = Dp(40); // 2.5U 方块
                void StyleKnob(TextView b)
                {
                    StyleOverlayButton(b, InkTranslucency.AccentFocusDrawable()); // 选中态加重：朱砂底（vc41 吸收遗留三点）
                    b.TextSize = Shared.UiScale.T(1.25f);
                }
                StyleKnob(minus);
                StyleKnob(plus);
                value.Focusable = false;
                value.TextSize = Shared.UiScale.T(1.25f);
                value.SetTextColor(Color.ParseColor("#F7F5EE"));
                value.SetPadding(Dp(12), 0, Dp(12), 0);
                InkTranslucency.ApplyReadabilityShadow(value);

                minus.Click += (_, _) => { if (cur() > steps[0]) { set(PrevStep(steps, cur())); SaveAdvancedTuning(); Refresh(); } };
                plus.Click += (_, _) => { if (cur() < steps[^1]) { set(NextStep(steps, cur())); SaveAdvancedTuning(); Refresh(); } };
                row.AddView(minus, new LinearLayout.LayoutParams(btn, btn));
                row.AddView(value, new LinearLayout.LayoutParams(0, -2, 1f));
                row.AddView(plus, new LinearLayout.LayoutParams(btn, btn));
                body.AddView(row);

                var sub = new TextView(this) { Text = desc, TextSize = Shared.UiScale.T(0.9375f) };
                sub.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
                InkTranslucency.ApplyReadabilityShadow(sub);
                sub.SetPadding(Dp(24), 0, Dp(24), Dp(8));
                body.AddView(sub);

                void Refresh()
                {
                    value.Text = valueText();
                    minus.Alpha = cur() <= steps[0] ? 0.35f : 1f;   // 边界灰显：到头即停
                    plus.Alpha = cur() >= steps[^1] ? 0.35f : 1f;
                }
                refreshers.Add(Refresh);
                Refresh();
            }

            // 5 个旋钮（每项下挂一行人话作用说明——老板令：讲清楚每一项是什么作用）
            AddKnob("网络解析出毛病时，撑几次失败才启动本地中转自救——调低自救快但易误伤，调高稳但自救慢",
                AdvGateSteps,
                () => $"自愈门槛：连败 {AdvancedTuning.SelfHealFailGate} 次（默认 3）",
                v => AdvancedTuning.Apply(v, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, AdvancedTuning.ProxyPort),
                () => AdvancedTuning.SelfHealFailGate);
            AddKnob("自救起来之后，隔多久偷偷试一次能否回直连——调低回得快但探测勤，调高省流量回得慢",
                AdvReprobeMinSteps,
                () => $"自愈回探：{AdvancedTuning.SelfHealReprobeMs / 60_000} 分钟（默认 5）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, v * 60_000L, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, AdvancedTuning.ProxyPort),
                () => (int)(AdvancedTuning.SelfHealReprobeMs / 60_000));
            AddKnob("频道清单数据在手上留多久不重取——留久省流量，留短换台信息更新鲜",
                AdvCacheSecSteps,
                () => $"清单缓存：{AdvancedTuning.ListCacheSec} 秒（默认 60）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, v, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, AdvancedTuning.ProxyPort),
                () => AdvancedTuning.ListCacheSec);
            AddKnob("被源服务器拒绝后退多远再试——跟风控博弈的分寸：调急了容易挨打，调钝了台恢复慢（封顶恒 2 小时）",
                AdvRetrySecSteps,
                () => $"重试节奏：起步 {AdvancedTuning.RetryBaseMs / 1000} 秒（默认 120）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, v * 1000L, AdvancedTuning.RetryMaxMs, AdvancedTuning.ProxyPort),
                () => (int)(AdvancedTuning.RetryBaseMs / 1000));
            AddKnob("自救中转走的内部通道口——只有跟其他软件撞号才需要改；改动下次播放生效",
                AdvPortSteps,
                () => $"自救端口：{AdvancedTuning.ProxyPort}（默认 47618）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, v),
                () => AdvancedTuning.ProxyPort);

            var tip = new TextView(this)
            {
                Text = "左右键切 − / +，确认键调档；到头即停。\n改坏了随时点「一键还原默认」回出厂值。端口项改动下次播放生效，其余即时生效。",
                TextSize = Shared.UiScale.T(0.9375f),
            };
            tip.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tip);
            tip.SetPadding(Dp(24), Dp(6), Dp(24), Dp(10));
            body.AddView(tip);

            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var reset = new TextView(this) { Text = "一键还原默认", Gravity = GravityFlags.Center };
            reset.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
            reset.Click += (_, _) =>
            {
                AdvancedTuning.ResetToDefaults();
                SaveAdvancedTuning();
                foreach (var rf in refreshers) rf(); // vc41：原位刷新值与灰显，不再整页重建（焦点不丢）
                FlashInfo("进阶调优：已全部还原出厂默认");
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
            close.Click += (_, _) => CloseAdvancedPanel();
            reset.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            reset.NextFocusRightId = close.Id;
            close.NextFocusLeftId = reset.Id;
            StyleOverlayButton(reset, InkTranslucency.AccentFocusDrawable()); // 还原=朱砂（改动性按钮）
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(52); // 3.25U
            btns.AddView(reset, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });

            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _advOverlay = overlay;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus(); // 默认焦点落「关闭」，防误改
        }
        catch (Exception ex) { CrashNet.Record($"[进阶调优] 打开失败：{ex.Message}"); FlashInfo("进阶调优打开失败"); }
    }

    private void CloseAdvancedPanel(bool reopenSettings = true)
    {
        if (_advOverlay is null) return;
        _root.RemoveView(_advOverlay);
        _advOverlay.Dispose();
        _advOverlay = null;
        if (reopenSettings) OpenSettingsPanel();
    }

    private void SaveAdvancedTuning() // 调参档写偏好（跨启动记忆；Apply 即生效）
    {
        _prefs?.Edit()
            ?.PutInt("adv_fail_gate", AdvancedTuning.SelfHealFailGate)
            ?.PutInt("adv_reprobe_min", (int)(AdvancedTuning.SelfHealReprobeMs / 60_000))
            ?.PutInt("adv_cache_sec", AdvancedTuning.ListCacheSec)
            ?.PutInt("adv_retry_base_sec", (int)(AdvancedTuning.RetryBaseMs / 1000))
            ?.PutInt("adv_retry_max_min", (int)(AdvancedTuning.RetryMaxMs / 60_000))
            ?.PutInt("adv_proxy_port", AdvancedTuning.ProxyPort)
            ?.Apply();
    }

    /// <summary>五类摘要组装：全部经现有公开接口/公开成员取数，不落设备标识、不新增采集。</summary>
    private string BuildDiagSnapshot()
    {
        // 版本（PackageInfo 现成读取；不读证书指纹——BLL 层面职责，边界内不动。
        // versionCode 字段 API28 起废弃，兼容 23 读取会出新警（CA1422），摘要只显 versionName）
        string versionLine;
        try
        {
            var pkg = PackageManager!.GetPackageInfo(PackageName!, 0)!;
            var abi = Build.SupportedAbis?.FirstOrDefault() ?? "?";
            versionLine = $"v{pkg.VersionName} · {abi}";
        }
        catch (Exception ex) { versionLine = "读取失败：" + ex.Message; }

        // 时钟：当前墙钟 + 单调已运行时长 + 时区偏移；跳变事件在完整日志里（[账本] 时钟大跳）
        string clockLine;
        try
        {
            var up = TimeSpan.FromMilliseconds(System.Environment.TickCount64);
            clockLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 已运行 {(int)up.TotalHours}h{up.Minutes:D2}m · " +
                        $"时区 UTC{TimeZoneInfo.Local.BaseUtcOffset:hh\\:mm} · 跳变事件见导出日志";
        }
        catch (Exception ex) { clockLine = "读取失败：" + ex.Message; }

        // 网络（复用 v23 修法 A 的就绪判定）
        var networkLine = NetworkReady() ? "就绪（有活动网络且具 Internet 能力）" : "不可用（无活动网络或无 Internet 能力）";

        // 启动/取流：从运行日志尾部统计现成打点
        var tail = DiagLog.ReadTail(System.IO.Path.Combine(FilesDir!.AbsolutePath!, "taolive_crash.log"), 256 * 1024);

        int failCount = 0, crashCount = 0;
        try
        {
            foreach (var line in tail.Split('\n'))
            {
                if (line.Contains("[ConnectFailure]", StringComparison.Ordinal)) failCount++;
                else if (line.Contains("未捕获异常", StringComparison.Ordinal)) crashCount++;
            }
        }
        catch { }
        var startupLine = $"运行日志 {tail.Length / 1024}KB（截尾视图）· 取流失败 {failCount} 条 · 未捕获异常 {crashCount} 条";

        // 健康账（复用预检公开判定，按台聚合快照）
        string healthLine;
        try
        {
            int alive = 0, dead = 0, checking = 0, unknown = 0;
            foreach (var c in _catalog.All)
            {
                switch (_precheck.Of(c))
                {
                    case ChannelHealth.Alive: alive++; break;
                    case ChannelHealth.Dead: dead++; break;
                    case ChannelHealth.Checking: checking++; break;
                    default: unknown++; break;
                }
            }
            healthLine = $"频道 {_catalog.All.Count}：可用 {alive} · 连不上 {dead} · 探测中 {checking} · 未探 {unknown}";
        }
        catch (Exception ex) { healthLine = "健康账读取失败：" + ex.Message; }

        return DiagLog.BuildSnapshot(versionLine, clockLine, networkLine, startupLine, healthLine,
            DiagLog.RecentFailuresFromLog(tail, 10));
    }

    private void ShowInfoBar(string text)
    {
        _infoBar.Text = text;
        _infoBar.Visibility = ViewStates.Visible;
        _ui.RemoveCallbacks(_hideInfo);
        _ui.PostDelayed(_hideInfo, 3000);
    }

    private void FlashInfo(string raw)
    {
        // A 类第2项·信息行单写入口：所有信息行走同一道闸——同文重刷吞、低优先级让位高优先级
        var text = $"{(_current?.Number.ToString() ?? "")}  {raw}".Trim();
        if (_infoHub.Submit(text, InfoLineHub.PriorityOf(text), System.Environment.TickCount64) is null) return;
        ShowInfoBar(text);
    }

    /// <summary>
    /// v22 第 9 项：画面信息短格式——「帧率:x fps&gt;y fps  分辨率:1080P&gt;2K」。
    /// 第二刀（v1.0.2）列表页该行整块摘除：_statsInfo 不再创建、ticker 已停发，此处为空转兜底。
    /// </summary>
    private void RefreshStatsLine()
    {
        if (_statsInfo is null) return;
        var (ofps, cfps, ow, oh, cw, chh) = _host.SnapshotStats();
        bool exhausted = _host.StatsExhausted;
        _statsInfo.Text = $"{FrameStatsLine.FpsSegment(ofps, cfps, exhausted)}  {FrameStatsLine.ResSegment(ow, oh, cw, chh, exhausted)}";
    }

    /// <summary>A 类第13项·播放选源：健康账已验证优先（零新增请求）；无验证时多来源竞速（4 秒预算，退避期跳过，胜者回写）。</summary>
    private async Task<Channel> PickSourceAsync(Channel ch)
    {
        var alive = _precheck.BestAliveSource(ch, _catalog.All.ToList());
        if (alive != null) return alive;
        var variants = _catalog.All.Where(c => !string.IsNullOrEmpty(c.SourceId))
            .GroupBy(c => c.SourceId).Select(g => g.First())
            .Where(c => c.EpgKey == ch.EpgKey || ReferenceEquals(c, ch)).ToList();
        if (variants.Count <= 1) return ch;
        var candidates = variants.Select(c => (EndpointRacer.Candidate)new EndpointRacer.Candidate(
            c.SourceId!, async () => await ProbeChannelAsync(c) ? EndpointRacer.Outcome.Alive : EndpointRacer.Outcome.Dead)).ToList();
        var winner = await EndpointRacer.RaceAsync(candidates, _backoff, System.Environment.TickCount64,
            onLimited: id =>
            {
                _backoff.RecordLimited(id, System.Environment.TickCount64);
                CrashNet.Record($"[退避] {id} 入账：竞速 403，冻结 {TimeSpan.FromMilliseconds(_backoff.RemainingMs(id, System.Environment.TickCount64)).TotalMinutes:F0} 分钟"); // v22 第 4 项
                return Task.CompletedTask;
            });
        var picked = variants.FirstOrDefault(v => v.SourceId == winner);
        var source = picked ?? ch;
        CrashNet.Record($"[竞速] {ch.Name} 无已验证来源，多端点竞速{(winner is null ? "全挂回本台" : $"胜出：{source.SourceId}")}（A⑬）");
        if (winner != null)
        {
            _precheck.ReportSourceResult(source, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // 竞速结果回写健康账
            if (_backoff.IsFrozen(source.SourceId!, System.Environment.TickCount64))
            {
                _backoff.Recover(source.SourceId!);
                CrashNet.Record($"[退避] {source.SourceId} 竞速胜出 200：全额恢复，解除冻结"); // v22 第 4 项
            }
        }
        return source;
    }

    private Action _hideInfo => () => _infoBar.Visibility = ViewStates.Invisible;

    // ---- 起播亮牌（雷二）：延迟亮、点了名、首帧撤 ----

    private void ShowLoading(Channel ch)
    {
        bool wasVisible = _loadingPanel.Visibility == ViewStates.Visible;
        _loadingChannel = ch; // 动画跟随最新频道（重连期间换台不串台名）
        _loadingText.Text = $"{ch.Number}  {ch.Name}\n正在连接";
        _loadingPanel.Visibility = ViewStates.Visible;
        if (!wasVisible) StartLoadingDots(ch); // 已亮：只更新文本不重置动画
    }

    private Channel? _loadingChannel;

    private void HideLoading()
    {
        CancelPendingLoading();
        StopLoadingDots();
        _loadingPanel.Visibility = ViewStates.Gone;
    }

    private void CancelPendingLoading()
    {
        if (_pendingLoading != null) _ui.RemoveCallbacks(_pendingLoading);
        _pendingLoading = null;
    }

    /// <summary>亮牌期间的呼吸点动画：正在连接 → 正在连接· → ·· → ···，让「还没死机」一眼可见。</summary>
    private void StartLoadingDots(Channel ch)
    {
        StopLoadingDots();
        int token = ++_loadingSeq;
        var tick = 0;
        string[] frames = { "正在连接", "正在连接 ·", "正在连接 ··", "正在连接 ···" };
        _loadingAnimRun = () =>
        {
            if (token != _loadingSeq || _loadingPanel.Visibility != ViewStates.Visible) return;
            tick++;
            var cur = _loadingChannel ?? ch; // 动画跟随最新频道
            _loadingText.Text = $"{cur.Number}  {cur.Name}\n{frames[tick % frames.Length]}";
            _ui.PostDelayed(_loadingAnimRun!, 400);
        };
        _ui.PostDelayed(_loadingAnimRun, 400);
    }

    private void StopLoadingDots()
    {
        if (_loadingAnimRun != null) _ui.RemoveCallbacks(_loadingAnimRun);
        _loadingAnimRun = null;
        _loadingSeq++;
    }
    // ---- 频道列表（v1.0.8：树形可折叠 + 宽度随内容）----

    private void ToggleChannelList()
    {
        if (_listOpen) CloseChannelList(); else OpenChannelList();
    }

    // v23 第 1 条：折叠总开关退场——ListGroupKeys/ToggleAllGroups 一并清（列表默认铺开，分组行本就纯展示）。

    private void OpenChannelList()
    {
        bool freshOpen = !_listOpen; // 1.0.12 UI②：仅「关→开」的呼出才重定默认标签（抽屉内切标签/点标签不覆盖用户选择）
        _listOpen = true;
        _listPanel.RemoveAllViews();
        _listPanel.Visibility = ViewStates.Visible; // 呼出列表：盖板必须显式转可见（v1.0.3 漏了这步，真机按菜单无反应）
        SuppressEpgForDrawer(); // v22 第 13 项：抽屉打开先收节目单条（隐藏+停计时），叠字从根上清掉
        // 1.0.12 UI②·默认定位当前台：已收藏→收藏；未收藏→按分类（央视/卫视/地方）；无归属→全部
        if (freshOpen)
            _drawerTab = DrawerSupport.DefaultTab(_current, n => _favorites.IsFavorite(n));

        // B 包第 12 项：抽屉头部——标题 + 台数 + 五标签行（当前标签朱砂底高亮，确认/左右键切换）
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetPadding(Dp(DrawerPad), Dp(16), Dp(DrawerPad), Dp(8)); // vc41 自适应体系：顶部贴顶 40→16（老板 20:20 令），面板本就满高盖板，遮挡范围不变
        var title = new TextView(this) { Text = "TaoLive 频道", TextSize = Shared.UiScale.T(1.5f) };
        title.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(title);
        header.AddView(title);
        var count = new TextView(this) { Text = $"  共 {_catalog.All.Count()} 路", TextSize = Shared.UiScale.T(0.9375f) };
        count.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(count);
        header.AddView(count);
        _listPanel.AddView(header);

        // 第二刀（v1.0.2）：列表页帧率/分辨率行整块摘除——不创建、不刷新、ticker 停发；
        // 后台帧统计逻辑（LiveVideoHost 内）保留，只砍显示。

        // 五分类标签行（第二块，v22 第 9 项换位后随画面信息块之后）；
        // vc41 自适应体系：签行上 24→12（贴顶），签内边距 12→8、签间距 8→6（老板 20:17 拍板）；
        // 首标签 LeftMargin=0（其余 6），标签行最左与页头文字严格对齐
        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabRow.SetPadding(Dp(DrawerPad), Dp(12), Dp(DrawerPad), Dp(8));
        _tabViews = new List<TextView>();
        int tabIndex = 0;
        foreach (DrawerSupport.Tab t in Enum.GetValues(typeof(DrawerSupport.Tab)))
        {
            var tv = new TextView(this) { Text = DrawerSupport.Label(t), TextSize = Shared.UiScale.T(1f) };
            tv.SetSingleLine(true); // UI④折行修根（老板 16:17 拍板）：分类签禁折行，无省略号，宽度由定比面板+降档验算保证完整显示
            tv.SetPadding(Dp(8), Dp(10), Dp(8), Dp(10));
            InkTranslucency.ApplyReadabilityShadow(tv);
            var lpTab = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = tabIndex == 0 ? 0 : Dp(6) };
            tabRow.AddView(tv, lpTab);
            _tabViews.Add(tv);
            tv.Click += (_, _) => { _drawerTab = t; OpenChannelList(); };
            tabIndex++;
        }
        StyleTabs();
        _listPanel.AddView(tabRow);

        // v23 第 1 条：折叠总开关行整行退场——标签行下不再有「全部收起/全部展开」行，列表直接铺频道。

        _listView = new ListView(this) { Divider = null };
        // 全局透明化（v1.0.8 第15项）：焦点效果=低透明度细边框+极淡底色，不复用实色高亮
        _listView.Selector = InkTranslucency.FocusDrawable();
        _listView.ScrollStateChanged += (_, e) =>
        {
            if (e.ScrollState == ScrollState.Idle) TriggerScreenPrecheck(); // v25 修法三：滚动停稳先探屏内
        };
        var rows = BuildRows();

        _adapter = new ChannelAdapter(this, rows, _current, n => _favorites.IsFavorite(n), c => _precheck.Of(c));
        _listView.Adapter = _adapter;
        _listView.ItemClick += (_, e) =>
        {
            var r = rows[e.Position];
            if (r.Channel != null)
            {
                // 连不上的台只能看不能切（v1.0.8，用户拍板）：确认键不发起播放
                if (r.DeadOnly || _precheck.Of(r.Channel) == ChannelHealth.Dead)
                {
                    FlashInfo($"{r.Channel.Name}  暂无信号");
                    return;
                }
                Play(r.Channel);
                return;
            }
            // 分组行（收藏/无信号标题）：v4 抽屉口径纯展示，不再折叠；空态行无点击（不可聚焦）
        };
        _listPanel.AddView(_listView, new LinearLayout.LayoutParams(-1, -1));

        // 宽度随内容（v1.0.8）：按最长行文本估宽，上限半屏、下限 360dp，绝不超屏
        ApplyListWidth(rows);

        // 焦点定位（v1.0.9 第⑪项全局焦点锚定）：锚点优先（⑦分类展开收起/收藏切换均锚原位），
        // 否则排序模式跟被排序的台，否则跟当前频道
        var focusNumber = _sortMode ? _sortChannel?.Number : _current?.Number;
        var curIdx = -1;
        if (_pendingFocusAnchor != null)
        {
            curIdx = rows.FindIndex(r =>
                r.HeaderKey != null ? r.HeaderKey == _pendingFocusAnchor
                : r.Channel != null && "C:" + r.Channel.Number == _pendingFocusAnchor);
        }
        _pendingFocusAnchor = null;
        if (curIdx < 0) curIdx = rows.FindIndex(r => r.Channel?.Number == focusNumber);
        if (curIdx >= 0) _listView.SetSelection(curIdx);
        _listView.RequestFocus();
        TriggerScreenPrecheck(); // v25 修法三：列表呼出即先探屏内（所见先探，未知台不再干等）
    }

    /// <summary>
    /// v25 修法三·屏内先探（UI 摸到哪先探到哪，列表呼出/滚动停稳即触发）：
    /// - 从第一个可见行向下取「预检状态 Unknown」的前 8 台作为立刻批；同台一次只跑一个探测任务（3b 去重）；
    /// - ≥3 秒节流（3b）；跟随预检开关（3a：关=循环与先探同停）；换台/连接中跳过本轮（2a 让路前台）；
    /// - 与稳态循环共享同一让路闸（1a），票账联动同口径（退避期弹回不发起、不入账不记失败）；
    /// - 记账口径与稳态循环一致：200 计正、明确拒绝计负、网络级失败不计负不入账。
    /// </summary>
    private void TriggerScreenPrecheck()
    {
        if (!_precheckEnabled) return; // 3a：跟随预检开关
        if (_foregroundBusy) return;   // 2a：换台/连接中跳过本轮（PauseGate 口径一致）
        if (_adapter is null || _listView is null) return;
        long now = System.Environment.TickCount64;
        if (now - _lastScreenPrecheckMs < 3_000) return; // 3b：≥3 秒节流
        _lastScreenPrecheckMs = now;
        int slots = 8;
        for (int pos = Math.Max(0, _listView.FirstVisiblePosition - 1); pos < _adapter.Count && slots > 0; pos++)
        {
            var ch = _adapter[pos].Channel;
            if (ch is null) continue;
            if (_precheck.Of(ch) != ChannelHealth.Unknown) continue;
            var key = $"{ch.SourceId}:{ch.EpgKey}";
            lock (_probeInFlightLock)
            {
                if (!_probeInFlight.Add(key)) continue; // 同台一次只跑一个探测任务
            }
            slots--;
            _ = Task.Run(async () =>
            {
                try
                {
                    var pace = WarmupPolicy.PaceOf(ch.SourceId);
                    if (!_pace.TryAcquireGate(ch.SourceId, System.Environment.TickCount64, pace.MinGapMs)) return; // 1a：共享让路闸
                    if (_resolver.TicketRemainingCooldownMs(ch) > 0) return; // 2：退避期弹回不发起不入账
                    var verdict = await ProbeChannelVerdictAsync(ch);
                    if (verdict == StreamResolver.ProbeVerdict.Reachable)
                    {
                        _pace.Record(ch.EpgKey, ch.SourceId, true); // 修法六 1b：计正
                        _precheck.ReportSourceResult(ch, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        _backoff.Recover(ch.SourceId); // 探通 200：若源被冻结全额恢复（同一本账）
                    }
                    else if (verdict == StreamResolver.ProbeVerdict.Rejected)
                    {
                        _pace.Record(ch.EpgKey, ch.SourceId, false); // 明确拒绝计负
                        _backoff.RecordLimited(ch.SourceId, System.Environment.TickCount64);
                        _precheck.ReportSourceResult(ch, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                    // Unreachable（网络级失败）：不计负不入账不记状态（统一口径，下轮再探）
                }
                catch { /* 探测失败静默：下轮再探 */ }
                finally
                {
                    lock (_probeInFlightLock) { _probeInFlight.Remove(key); }
                }
            });
        }
    }

    /// <summary>当前焦点行锚点键（⑪全局焦点锚定）：分类行取分类键，频道行取 C:频道号。</summary>
    private string? CurrentFocusKey()
    {
        if (_listView is null || _adapter is null) return null;
        int pos = _listView.SelectedItemPosition;
        if (pos < 0 || pos >= _adapter.Count) return null;
        var r = _adapter[pos];
        if (r.HeaderKey != null) return r.HeaderKey;
        return r.Channel is null ? null : "C:" + r.Channel.Number;
    }

    /// <summary>⑥左右键分类间跳转（v1.0.9 用户 10:04 拍板）：从当前行沿方向找最近分类头，焦点落到分类头上。</summary>
    private void JumpCategory(int dir)
    {
        if (_listView is null || _adapter is null) return;
        int pos = _listView.SelectedItemPosition;
        if (pos < 0) pos = 0;
        for (int p = pos + dir; p >= 0 && p < _adapter.Count; p += dir)
        {
            if (_adapter[p].HeaderKey == null) continue;
            _listView.SetSelection(p);
            _listView.RequestFocus();
            return;
        }
    }

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        // v22 第 8 项：画质增强档位行撤出列表页（设置页保留唯一入口），抽屉只放频道；
        // v22 第 7/15 项：标签口径修正（CGTN/专项并央视、地方台只留真城市台）+ 空态行装配
        var favOrder = _favorites.Numbers.ToList();
        var drawerRows = DrawerSupport.BuildRows(_drawerTab, _catalog.All.ToList(), favOrder,
            c => _precheck.Of(c));

        foreach (var r in drawerRows)
        {
            switch (r)
            {
                case DrawerSupport.DrawerRow.Note n:
                    rows.Add(new Row { Header = n.Text, HeaderKey = "__note__", HeaderDefault = true });
                    break;
                case DrawerSupport.DrawerRow.Group g:
                    rows.Add(new Row { Header = g.Label, HeaderKey = "__group__:" + g.Label, HeaderDefault = true });
                    break;
                case DrawerSupport.DrawerRow.Channel chRow:
                    var ch = chRow.Data;
                    rows.Add(new Row
                    {
                        Channel = ch,
                        DeadOnly = _precheck.Of(ch) == ChannelHealth.Dead,
                    });
                    break;
            }
        }
        return rows;
    }

    /// <summary>五标签行着色：当前标签朱砂底浅字，其余极淡底。</summary>
    private void StyleTabs()
    {
        if (_tabViews is null) return;
        var tabs = (DrawerSupport.Tab[])Enum.GetValues(typeof(DrawerSupport.Tab));
        for (int i = 0; i < _tabViews.Count && i < tabs.Length; i++)
        {
            bool on = tabs[i] == _drawerTab;
            _tabViews[i].SetBackgroundColor(on == true
                ? Color.ParseColor("#8A3B34")
                : Color.Transparent);
            _tabViews[i].SetTextColor(Color.ParseColor(on ? "#F7F5EE" : InkTranslucency.TextSecondary));
        }
    }

    /// <summary>左右键切标签（列表开着时）：左=上一标签，右=下一标签，切换后焦点归位列表。</summary>
    private void SwitchDrawerTab(bool rightward)
    {
        _drawerTab = DrawerSupport.NextTab(_drawerTab, rightward);
        _pendingFocusAnchor = null;
        OpenChannelList();
    }

    /// <summary>预检状态实时刷新（vc38 全表刷新风暴收敛）：预检逐台回报（全表一轮约百次）改为 400ms 合帧，
    /// 帧内合并中间态；状态签名无变化不重绘。焦点与滚动位置由 ListView 保留。</summary>
    private void RefreshListStatuses()
    {
        if (!_listOpen || _listView is null || _adapter is null) return;
        if (System.Threading.Interlocked.Exchange(ref _listRefreshQueued, 1) == 1) return; // 已有合帧在途：本次并入
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            System.Threading.Interlocked.Exchange(ref _listRefreshQueued, 0);
            timer!.Dispose();
            _ui.Post(() =>
            {
                if (!_listOpen || _listView is null || _adapter is null) return;
                var rows = BuildRows();
                var sig = ListStatusSignature(rows);
                if (sig == _lastListSig) return; // 状态无变化：不重绘
                _lastListSig = sig;
                _adapter.ReplaceRows(rows);
                ApplyListWidth(rows);
            });
        }, null, ListRefreshDebounceMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>列表状态签名（vc38）：行序 + 各台健康态的紧凑指纹，签名单调变化才算「有更新」。</summary>
    private string ListStatusSignature(List<Row> rows)
    {
        var sb = new System.Text.StringBuilder(rows.Count * 8);
        foreach (var r in rows)
        {
            if (r.Header != null) { sb.Append(r.HeaderKey).Append('|'); continue; }
            if (r.Channel is null) { sb.Append('~'); continue; }
            sb.Append(r.Channel.EpgKey).Append(':').Append((int)_precheck.Of(r.Channel)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>vc41 自适应体系：面板=屏宽定比 2/3（每次打开一样宽）；降档验算保证一行完整（面板宽永不动）。</summary>
    private void ApplyListWidth(List<Row> rows)
    {
        // vc41 自适应体系（老板 20:17/20:33 拍板）：面板宽=屏宽定比 2/3，每次打开一样宽、运行期位置不变；
        // 旧「按内容自适应+半屏钳制」口径退役（ListMetrics 断言同步改）。
        // 一行完整验算（老板原则）：最坏台名在当前字号档装不下面板内宽就整行降档（1.375U→1U→0.875U），面板宽度永不动。
        int screenW = Resources!.DisplayMetrics!.WidthPixels;
        _listPanel.LayoutParameters = new FrameLayout.LayoutParams(
            Shared.UiScale.PanelWidthPx(screenW), -1, GravityFlags.Left);

        int worstLen = 0;
        foreach (var r in rows)
            if (r.Channel != null) worstLen = Math.Max(worstLen, r.Channel.Name?.Length ?? 0);
        foreach (var mul in RowTextMulSteps)
        {
            _rowTextMul = mul;
            if (ListMetrics.FitsPanel(worstLen, mul)) break;
        }
    }

    /// <summary>频道行主字号档（U 倍率）：默认 1.375U=22，一行装不下逐档降，面板宽不动。</summary>
    private static readonly float[] RowTextMulSteps = { 1.375f, 1f, 0.875f };
    private float _rowTextMul = 1.375f;

    private void CloseChannelList()
    {
        // v23 第 3 条：收抽屉即把焦点从列表行上摘下来，落回播放画面（确认切台后光标不再留在已收起的抽屉里）
        _listPanel.FindFocus()?.ClearFocus();
        _listOpen = false;
        _lastListSig = null; // vc38：收列表清签名，重开列表首帧必刷
        _listPanel.Visibility = ViewStates.Gone;
        _listView = null;
        // v22 第 13 项：关抽屉后重新亮出节目单条（本台拉到过数据才亮），按 7 秒计时收起
        if (_epgHasData) ShowEpgBar();
    }

    private class Row
    {
        public string? Header;         // 分类行显示文本
        public string? HeaderKey;      // 分类行折叠键（频道行 null）
        public bool HeaderDefault;     // 未初始化时的默认展开态
        public Channel? Channel;
        public bool DeadOnly;          // 连不上置底组内台（只能看不能切）
    }

    private class ChannelAdapter : BaseAdapter<Row>
    {
        private readonly MainActivity _owner;
        private readonly Channel? _current;
        private readonly Func<int, bool> _isFav;
        private readonly Func<Channel, ChannelHealth> _health;
        private List<Row> _rows;

        public ChannelAdapter(MainActivity owner, List<Row> rows, Channel? current, Func<int, bool> isFav, Func<Channel, ChannelHealth> health)
        { _owner = owner; _rows = rows; _current = current; _isFav = isFav; _health = health; }

        /// <summary>预检状态实时刷新：换行数据通知重绘（滚动位置尽量保留）。</summary>
        public void ReplaceRows(List<Row> rows)
        {
            _rows = rows;
            NotifyDataSetChanged();
        }

        public override long GetItemId(int position) => position;
        public override Row this[int position] => _rows[position];
        public override int Count => _rows.Count;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var row = _rows[position];
            var ctx = _owner;
            var line = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            line.SetPadding(_owner.Dp(24), _owner.Dp(20), _owner.Dp(24), _owner.Dp(20)); // vc41：左右 1.5U 边距（48→24），上下 1.25U 不变

            void Style(TextView tv, string color, float size)
            {
                tv.SetTextColor(Color.ParseColor(color));
                tv.TextSize = size;
            }

            if (row.Header != null)
            {
                var h = new TextView(ctx) { Text = row.Header };
                bool isNote = row.HeaderKey == "__note__"; // v22 第 15 项：空态行次色平铺，不作分组标题渲染
                Style(h, isNote ? InkTranslucency.TextSecondary : "#A9CFEC", isNote ? Shared.UiScale.T(0.9375f) : Shared.UiScale.T(1.125f));
                InkTranslucency.ApplyReadabilityShadow(h);
                line.AddView(h);
                return line;
            }

            var ch = row.Channel!;
            // B 包第 12 项：行首真实台标（v4 同源内置资源）；无内置台标→首字占位；无信号行不放台标
            bool dead = _health(ch) == ChannelHealth.Dead;
            var logoRes = ChannelLogoMap.ResourceName(ch.Name);
            if (!dead && logoRes != null)
            {
                try
                {
                    var resId = ctx.Resources!.GetIdentifier(logoRes, "drawable", ctx.PackageName);
                    if (resId != 0)
                    {
                        var img = new ImageView(ctx);
                        img.SetAdjustViewBounds(true);
                        img.SetMaxHeight(Shared.UiScale.P(1.75f));
                        img.SetImageResource(resId);
                        var lpLogo = new LinearLayout.LayoutParams(-2, Shared.UiScale.P(1.75f))
                        { RightMargin = _owner.Dp(8), Gravity = GravityFlags.CenterVertical };
                        line.AddView(img, lpLogo);
                    }
                }
                catch { /* 台标渲染失败不碍选台 */ }
            }
            if (!dead && logoRes == null)
            {
                // vc44①（老板 11-13 14:4x 令「尽善尽美+长期稳定免维护」）：无内置台标→水墨文字台标，
                // 运行时动态生成，任意新台/未收录台零资源自动覆盖，无需随台标枚举更新——
                // 瓷釉水墨圆角底+纸色首字+细边描（与 v22 水墨文字台标同风格、与空态行次色一致）。
                var ph = new TextView(ctx) { Text = ch.Name.Substring(0, 1) };
                Style(ph, "#E7E2D6", Shared.UiScale.T(0.875f));
                ph.Gravity = GravityFlags.Center;
                var inkBg = new GradientDrawable();
                inkBg.SetColor(Color.ParseColor("#3A4750"));
                inkBg.SetCornerRadius(Shared.UiScale.P(0.625f));
                inkBg.SetStroke(1, Color.ParseColor("#4E5D68"));
                ph.Background = inkBg;
                var lpPh = new LinearLayout.LayoutParams(Shared.UiScale.P(2.5f), Shared.UiScale.P(1.75f))
                { RightMargin = _owner.Dp(8), Gravity = GravityFlags.CenterVertical };
                line.AddView(ph, lpPh);
            }
            if (dead)
            {
                var spacer = new View(ctx) { };
                var lpSp = new LinearLayout.LayoutParams(0, 1) { RightMargin = _owner.Dp(8) };
                line.AddView(spacer, lpSp); // 无信号行行首不放台标（v4 定稿状态2）
            }

            var num = new TextView(ctx) { Text = $"{ch.Number,3}", Gravity = GravityFlags.Right };
            Style(num, InkTranslucency.TextSecondary, Shared.UiScale.T(1.25f));
            InkTranslucency.ApplyReadabilityShadow(num);
            num.SetPadding(0, 0, _owner.Dp(16), 0); // vc41：数字-星标-台名 56→28（老板 20:17 拍板）
            line.AddView(num);

            var star = new TextView(ctx) { Text = _isFav(ch.Number) ? "★" : " " };
            Style(star, "#F0C563", Shared.UiScale.T(1.25f));
            InkTranslucency.ApplyReadabilityShadow(star);
            star.SetPadding(0, 0, _owner.Dp(12), 0);
            line.AddView(star);

            var name = new TextView(ctx) { Text = ch.Name };
            name.SetSingleLine(true); // UI④折行修根（老板 16:17 拍板）：频道名禁折行、禁省略号，一行完整由定比面板+降档验算保证
            var isCur = _current?.Number == ch.Number;
            Style(name, isCur ? "#FFFFFF" : InkTranslucency.TextSecondary, Shared.UiScale.T(_owner._rowTextMul));
            InkTranslucency.ApplyReadabilityShadow(name);
            if (isCur)
            {
                line.SetBackgroundColor(InkTranslucency.CurrentRowFill(ctx)); // v1.0.9 第③项：当前台整行强调底，一眼认出正在看的台
                name.Typeface = global::Android.Graphics.Typeface.DefaultBold;
            }
            line.AddView(name);

            // 可连状态标识（v1.0.8 预检握手）：绿点可用 · 灰点检测中/未探 · 红叉连不上
            var health = _health(ch);
            var (mark, color) = health switch
            {
                ChannelHealth.Alive => ("●", "#7ED182"),
                ChannelHealth.Dead => ("✕", "#E97F7F"),
                ChannelHealth.Checking => ("●", "#B5C4D0"),
                _ => ("●", "#8894A0"),
            };
            var dot = new TextView(ctx) { Text = mark };
            Style(dot, color, Shared.UiScale.T(1.125f));
            InkTranslucency.ApplyReadabilityShadow(dot);
            dot.SetPadding(_owner.Dp(12), 0, 0, 0); // vc41：点-台名 24→12，与行内收窄同口径
            line.AddView(dot);
            return line;
        }
    }

    // ---- 收藏 ----

    private void ToggleFavoriteOnCurrent()
    {
        if (_current is null) return;
        _favorites.Toggle(_current.Number);
        _prefs?.Edit()?.PutString("favorites", _favorites.Serialize())?.Apply(); // 顺序持久化（v1.0.8 第14项）
        ShowInfoBar(_favorites.IsFavorite(_current.Number)
            ? $"{_current.Number}  {_current.Name}  ★ 已收藏"
            : $"{_current.Number}  {_current.Name}  已取消收藏");
        if (_listOpen) { _pendingFocusAnchor = CurrentFocusKey(); OpenChannelList(); } // 列表开着则刷新（⑪焦点锚原位）
    }

    // ---- 节目单（B 包第 14 项：央视网官方接口，没数据不硬造）----

    /// <summary>换台即刷新：先清空+隐藏旧节目单条（v22 第 1 项：换台先清场，旧条绝不残留），
    /// 后台拉官方节目单，有数据才亮底部节目区并按 7 秒计时收起（v22 第 12 项）；失败/映射外/无数据静默隐藏。</summary>
    private void RefreshEpg(Channel ch)
    {
        _epgClient ??= new CntvEpgClient();
        var seq = _playSeq;
        // v22 第 1 项：换台先清空+隐藏再拉数——旧台节目条在拉取期间绝不亮着
        _epgHasData = false;
        CancelEpgAutoHide();
        if (_epgPanel != null)
        {
            _epgNow!.Text = "";
            _epgNext!.Text = "";
            _epgPanel.Visibility = ViewStates.Gone;
        }
        _epgCts?.Cancel();
        _epgCts?.Dispose();
        _epgCts = new System.Threading.CancellationTokenSource();
        var ui = _ui;
        _ = Task.Run(() =>
        {
            var result = _epgClient.Fetch(ch.EpgKey);
            ui.Post(() =>
            {
                if (seq != _playSeq || _epgPanel is null) return; // 换台后旧节目单作废
                if (result is null)
                {
                    _epgPanel.Visibility = ViewStates.Gone; // 官方无数据不出卡（不硬造）
                    _epgHasData = false;
                    return;
                }
                var (now, next) = result.Value;
                _epgNow!.Text = $"{ch.Name} · {DrawerSupport.Clock(now.Start)} – {DrawerSupport.Clock(now.End)}  {now.Title}"
                    + $"  ·  还剩 {Math.Max(0, (int)Math.Round((now.End - DateTime.Now).TotalMinutes))} 分钟";
                var upcoming = next.Take(2).ToList();
                _epgNext!.Text = upcoming.Count > 0
                    ? string.Join("   ", upcoming.Select(p => $"{DrawerSupport.Clock(p.Start)} {p.Title}"))
                    : "";
                _epgNext.Visibility = upcoming.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
                ShowEpgBar();
            });
        });
    }

    /// <summary>v22 第 12 项：节目单条亮出并按 7 秒无操作计时自动收起（换台刷新即重计）。</summary>
    private void ShowEpgBar()
    {
        if (_epgPanel is null) return;
        _epgHasData = true;
        _epgPanel.Visibility = ViewStates.Visible;
        CancelEpgAutoHide();
        _epgHidePending = () =>
        {
            if (_epgPanel != null && !_listOpen) _epgPanel.Visibility = ViewStates.Gone;
        };
        _ui.PostDelayed(_epgHidePending, EpgAutoHideMs);
    }

    private void CancelEpgAutoHide()
    {
        if (_epgHidePending != null)
        {
            _ui.RemoveCallbacks(_epgHidePending);
            _epgHidePending = null;
        }
    }

    /// <summary>v22 第 13 项：抽屉打开先收节目单条（隐藏+停计时），节目单条压在抽屉下且不叠字。</summary>
    private void SuppressEpgForDrawer()
    {
        CancelEpgAutoHide();
        if (_epgPanel != null) _epgPanel.Visibility = ViewStates.Gone;
    }

    // ---- 返回键（v1.0.8 防错）----

    private void ExitOrCollapseList()
    {
        // 退出确认弹窗开着：返回键=取消，继续播放（v1.0.9）
        if (_exitDialog != null)
        {
            _exitDialog.Dismiss();
            _exitDialog = null;
            return;
        }
        if (_listOpen)
        {
            CloseChannelList();
            // 列表开着回前台时重播被跳过（不打断操作），关列表时补校验：
            // 播放已死则立刻重播，绝不让用户关掉列表后面对黑屏（v1.0.8）
            if (_current != null && !_host.IsAlive)
            {
                _frameEverShown = false;
                Play(_current);
            }
            return;
        }
        // 退出确认（v1.0.9 用户拍板）：弹窗二次确认，确认后真完全关闭（结束进程），
        // 重开走全新冷启动；返回键/取消=继续播放。不再用「再按一次」防错。
        ShowExitConfirmDialog();
    }

    /// <summary>
    /// 退出确认弹窗（v1.0.9 用户拍板）：水墨深色面板，确认键=退出、返回键=取消继续播放。
    /// 确认后：先停播放（音频、取流、看门狗全停）→ FinishAndRemoveTask → 结束进程，
    /// 不留后台声音、不留半死播放内核；重新打开即冷启动（开机默认台/收藏首位照旧）。
    /// v22 第 11 项改版（老板 20:28/20:32 拍板）：全屏自管浮层——窗底透明+半透明水墨面板（与列表页同源，
    /// 不再露 ROM 钢蓝灰）；「退出」=朱砂细边框+朱砂极淡底（强调色 #8A5C3D，与列表页 Accent 同源），
    /// 「取消」=纸白细边框+极淡底；常驻=水墨深底+次色字；字体统一 TextPrimary/TextSecondary+可读性阴影；
    /// 焦点导航、默认焦点落「取消」、返回键=继续看，全不动。
    /// </summary>
    private void ShowExitConfirmDialog()
    {
        if (_exitDialog != null) return; // 弹窗已开着：不重复弹
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(20); // vc41 自适应体系
        box.SetPadding(pad * 2, pad * 2, pad * 2, pad);
        box.SetBackgroundColor(InkTranslucency.PanelColor(this)); // v22 第 11 项：半透明水墨底，与列表页面板同源同号
        box.SetMinimumWidth(Dp(480)); // 居中浮层不因文案缩成窄条

        var msg = new TextView(this) { Text = "确认退出 TaoLive？", TextSize = UiScale.T(1.25f) };
        msg.SetTextColor(InkTranslucency.TextPrimary(this)); // v22 第 11 项：统一 Token，暗屏自动提白
        InkTranslucency.ApplyReadabilityShadow(msg);
        box.AddView(msg);

        // 15:01 老板拍板：退出后后台保活继续跑——弹窗讲清楚，免得用户以为关干净了
        var note = new TextView(this) { Text = "退出后后台保活继续运行，随时回来看", TextSize = UiScale.T(0.8125f) };
        note.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(note);
        var noteLp = new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(8) };
        box.AddView(note, noteLp);

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var lp = new LinearLayout.LayoutParams(0, -2, 1f) { TopMargin = pad, LeftMargin = pad / 2, RightMargin = pad / 2 };
        // v22 第 11 项：按钮改 TextView 自绘——ROM Material 按钮的焦点高亮/系统着色从根上去掉
        var no = new TextView(this) { Text = "取消（继续看）", Gravity = GravityFlags.Center };
        no.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
        no.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; };
        var yes = new TextView(this) { Text = "退出", Gravity = GravityFlags.Center };
        yes.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
        yes.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; ExitAppCompletely(); };
        // v1.0.10 修复④：左右键在「退出」与「取消（继续看）」间切换——显式指路，不靠焦点搜索碰运气
        no.Id = View.GenerateViewId();
        yes.Id = View.GenerateViewId();
        no.NextFocusRightId = yes.Id;
        yes.NextFocusLeftId = no.Id;

        // v22 第 11 项统一按钮样式：聚焦=效果层（退出朱砂/取消纸白）+主色字；常驻=水墨深底+次色字
        var idleBg = new ColorDrawable(Color.Argb(96, InkTranslucency.InkR, InkTranslucency.InkG, InkTranslucency.InkB));
        void StyleDialogButton(TextView b, GradientDrawable focusDrawable)
        {
            b.Focusable = true;
            b.Clickable = true;
            b.Background = idleBg;
            b.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(b);
            b.FocusChange += (_, ev) =>
            {
                if (ev.HasFocus) { b.Background = focusDrawable; b.SetTextColor(InkTranslucency.TextPrimary(this)); }
                else if (b.Background == focusDrawable) { b.Background = idleBg; b.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary)); }
            };
        }
        StyleDialogButton(no, InkTranslucency.FocusDrawable());
        StyleDialogButton(yes, InkTranslucency.AccentFocusDrawable());
        buttons.AddView(no, lp);
        buttons.AddView(yes, lp);
        box.AddView(buttons);

        var dialog = new AlertDialog.Builder(this)
            .SetView(box)
            .SetCancelable(true)
            .Create()!;
        _exitDialog = dialog;
        dialog.Show();
        // v22 第 11 项：全屏自管——窗底透明+内容铺满，露出的是播放画面，与列表页浮在画面上观感一致
        var win = dialog.Window!;
        win.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        win.SetLayout(-1, -1);
        win.SetFlags(WindowManagerFlags.LayoutInScreen, WindowManagerFlags.LayoutInScreen);
        no.RequestFocus(); // v1.0.10 修复④：默认焦点落「取消（继续看）」——防误退更稳；返回键=继续看（OnCancelListener 复位）不变
        dialog.SetOnCancelListener(new DialogActionPeer(() => _exitDialog = null));
        dialog.SetOnDismissListener(new DismissActionPeer(() => _exitDialog = null));
    }

    /// <summary>标准监听器适配（v1.0.9）：把 Android 弹窗回调接成普通 C# 动作，避免匿名监听类散落。</summary>
    private sealed class DialogActionPeer : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnCancelListener
    {
        private readonly Action _act;
        public DialogActionPeer(Action act) { _act = act; }
        public void OnCancel(global::Android.Content.IDialogInterface? dialog) => _act();
    }

    private sealed class DismissActionPeer : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnDismissListener
    {
        private readonly Action _act;
        public DismissActionPeer(Action act) { _act = act; }
        public void OnDismiss(global::Android.Content.IDialogInterface? dialog) => _act();
    }

    /// <summary>退出界面（15:01 老板拍板：退出后后台保活继续跑）：停播放→去任务→进程交给保活服务持有。</summary>
    private void ExitAppCompletely()
    {
        try { _host.Stop(); } catch { /* 已停不碍退出 */ }
        try { _precheckCts?.Cancel(); } catch { }
        FinishAndRemoveTask();
        // 老板 15:01 拍板：退出后后台继续跑——不再结束进程；前台保活服务继续持有进程
        // （预热引擎同进程共享，重开即热启动）。预热开关关闭时无服务持有，进程随后由系统按后台空进程回收，等同真退出。
        if (_warmupEnabled)
        {
            try { StartForegroundService(new Intent(this, typeof(WarmupService))); } // 幂等：已在跑则走 OnStartCommand 续命
            catch (Exception svcEx) { CrashNet.Record($"[Warmup] 退出时保活拉起失败：{svcEx.Message}"); }
        }
    }

    // ---- 遥控键 ----

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        // TCL 设置键厂商码（真机实测 4077）：单击呼出软件设置面板、长按跳系统设置（v1.0.8 用户拍板）。
        // 单击/长按在 KeyUp / OnKeyLongPress 分流，这里只消费按键并复位长按标记。
        if ((int)keyCode == 4077)
        {
            _settingsLongFired = false;
            return true;
        }

        // 收藏排序模式（v1.0.8 第14项）：独立状态机接管键位，退出恢复默认键位
        if (_sortMode)
        {
            HandleSortKey(keyCode);
            return true;
        }

        // 进阶调优面板（调参档接线）：返回/菜单=关闭；确认=点当前行；
        // 方向键必须放行（return false）——系统焦点导航在 Activity 消费之后才走，这里吞掉光标就冻结（真机实锤 1.0.9）；
        // 其余键不穿透，防误触底层键位。
        if (_advOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseAdvancedPanel();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight:
                    return false; // 交还系统走焦点导航（含面板内行间移动与 ScrollView 光标行滚入可视区）
                default:
                    return true; // 其余不穿透
            }
        }

        // S3 自诊断面板（vc38）：键位与崩溃日志面板同款——返回/菜单=关闭；上下=滚动；左右=两钮移焦点；确认=点当前钮
        if (_diagOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseDiagPanel();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown when _diagScroll != null:
                {
                    int dir = keyCode == Keycode.DpadUp ? -1 : 1;
                    if (e is { RepeatCount: 0 })
                        _diagScroll.SmoothScrollBy(0, dir * (int)(_diagScroll.Height * 0.8f)); // 单按滚近一屏
                    else
                        _diagScroll.ScrollBy(0, dir * Dp(56)); // 长按连滚
                    return true;
                }
                case Keycode.DpadLeft or Keycode.DpadRight when _diagExportBtn != null && _diagCloseBtn != null:
                    (CurrentFocus == _diagExportBtn ? _diagCloseBtn : _diagExportBtn).RequestFocus(); // 焦点在两钮间往返
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                default:
                    return true; // 不穿透，防误触底层键位
            }
        }

        // 崩溃日志面板（1.0 UI④自管全屏）：全键接管——返回/菜单=关闭；上下=日志滚动
        // （显式接管：正文无焦点也能滚，长按连滚）；左右=两钮间移焦点；确认=点当前钮；其余键吞掉不穿透。
        if (_crashOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseCrashPanel();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown when _crashScroll != null:
                {
                    int dir = keyCode == Keycode.DpadUp ? -1 : 1;
                    if (e is { RepeatCount: 0 })
                        _crashScroll.SmoothScrollBy(0, dir * (int)(_crashScroll.Height * 0.8f)); // 单按滚近一屏
                    else
                        _crashScroll.ScrollBy(0, dir * Dp(56)); // 长按连滚
                    return true;
                }
                case Keycode.DpadLeft or Keycode.DpadRight when _crashCleanBtn != null && _crashCloseBtn != null:
                    (CurrentFocus == _crashCleanBtn ? _crashCloseBtn : _crashCleanBtn).RequestFocus(); // 焦点在两钮间往返
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                default:
                    return true; // 不穿透，防误触底层键位
            }
        }

        // 软件设置面板开着：返回/菜单收起，其余交给面板焦点导航；左右键不呼列表不翻页
        if (_settingsPanelOpen)
        {
            if (keyCode is Keycode.Back or Keycode.Menu) { CloseSettingsPanel(); return true; }
            if (keyCode is Keycode.DpadLeft or Keycode.DpadRight) return true;
            return base.OnKeyDown(keyCode, e);
        }

        if (_listOpen && _listView != null)
        {
            // B 包第 12 项：左右键=切标签（全部→央视→卫视→地方台→收藏循环）；
            // 上下/确认交给 ListView 焦点导航（上下=选台、确认=播放）；菜单或返回收起
            if (keyCode is Keycode.Menu or Keycode.Back) { ExitOrCollapseList(); return true; }
            if (keyCode is Keycode.DpadLeft) { SwitchDrawerTab(false); return true; }
            if (keyCode is Keycode.DpadRight) { SwitchDrawerTab(true); return true; }
            if (keyCode is Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadCenter or Keycode.Enter)
                return base.OnKeyDown(keyCode, e);
        }
        switch (keyCode)
        {
            case Keycode.DpadUp: Step(-1); return true;
            case Keycode.DpadDown: Step(+1); return true;
            // 左右键（真机实测 21/22）：列表翻页（v1.0.8 用户拍板；音量走遥控独立硬件键，软件不接管）
            case Keycode.DpadLeft: ListNav(+1, e!); return true;
            case Keycode.DpadRight: ListNav(-1, e!); return true;
            // 菜单键：标准菜单 + TCL 等厂商遥控常见的等效键值一并呼出频道列表
            case Keycode.Menu or Keycode.Settings or Keycode.Info: ToggleChannelList(); return true;
            case Keycode.DpadCenter or Keycode.Enter: ToggleFavoriteOnCurrent(); return true;
            case Keycode.Back: ExitOrCollapseList(); return true;
            // 音量透传系统：遥控音量键此前被吞，用户调不了音量（真机反馈）
            case Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute:
                return base.OnKeyDown(keyCode, e);
            default:
                // B 包第 8 项：键码屏显静默化；ui-remote P1 另一半（vc38 落）：未接管键放行给系统，
                // 不再吞——遥控器专有键（输入切换、面板键等）此前被整层吞掉，用户按了没反应。
                // 长按只记首按防刷屏（日志写已是异步）。
                if (e is { RepeatCount: 0 })
                    CrashNet.Record($"[Key] 未接管键值 {((int)keyCode)}");
                return base.OnKeyDown(keyCode, e);
        }
    }

    /// <summary>
    /// 左右键列表导航（v1.0.8 用户拍板）：列表未呼出 → 呼出列表；
    /// 已呼出 → 单按翻一屏（左=向下，右=向上，不足一屏到底/到顶），
    /// 长按（系统键重复）匀速中慢速连续滚动，松手即停。翻屏目标由 ListPaging 纯函数给出。
    /// </summary>
    private void ListNav(int dir, KeyEvent e)
    {
        if (!_listOpen || _listView is null) { OpenChannelList(); return; }
        if (e.RepeatCount == 0)
        {
            int first = _listView.FirstVisiblePosition;
            int visible = Math.Max(1, _listView.LastVisiblePosition - first + 1);
            int target = ListPaging.NextFirstVisible(_listView.Count, first, visible, dir);
            _listView.SmoothScrollToPosition(target);
        }
        else if (e.RepeatCount % 6 == 0)
        {
            // 长按连滚：每 6 个重复事件步进 5% 屏高 ≈ 每秒滚 15%（中慢速，肉眼跟得上）
            _listView.SmoothScrollBy((int)(_listView.Height * 0.05f) * dir, 60);
        }
    }

    /// <summary>设置键 4077 长按：跳电视系统设置（v1.0.8 用户拍板）。标记已触发，KeyUp 不再当单击。</summary>
    public override bool OnKeyLongPress(Keycode keyCode, KeyEvent? e)
    {
        // 收藏排序入口（v1.0.8 第14项）：列表内长按确认键抓起当前收藏台
        if (keyCode is Keycode.DpadCenter or Keycode.Enter)
        {
            if (_listOpen && !_sortMode) { TryEnterSortMode(); return true; }
        }
        if ((int)keyCode == 4077)
        {
            _settingsLongFired = true;
            OpenSystemSettings();
            return true;
        }
        return base.OnKeyLongPress(keyCode, e);
    }

    // ---- 收藏排序模式（v1.0.8 第14项，用户 23:23 拍板）----

    /// <summary>入口：列表内长按确认键且焦点在收藏台上；非收藏台亮牌说明，不硬进。</summary>
    private void TryEnterSortMode()
    {
        if (_listView is null || _adapter is null) return;
        var pos = _listView.SelectedItemPosition;
        if (pos < 0 || pos >= _adapter.Count) return;
        var row = _adapter[pos];
        if (row.Channel is null || !_favorites.IsFavorite(row.Channel.Number))
        {
            FlashInfo("请在收藏组内长按确认键排序");
            return;
        }
        _sortMode = true;
        _sortChannel = row.Channel;
        _sortBackup.Clear();
        _sortBackup.AddRange(_favorites.Numbers);
        FlashInfo("排序中：上下移动，确认放下");
        RefreshSortList();
    }

    /// <summary>排序键位：上下逐位挪、左右按屏快速挪、确认放下保存、返回取消复原、信息键一键置顶。</summary>
    private void HandleSortKey(Keycode keyCode)
    {
        if (_sortChannel is null) { _sortMode = false; return; }
        int num = _sortChannel.Number;
        switch (keyCode)
        {
            case Keycode.DpadUp: _favorites.MoveUp(num); RefreshSortList(); break;
            case Keycode.DpadDown: _favorites.MoveDown(num); RefreshSortList(); break;
            case Keycode.DpadLeft:
            case Keycode.DpadRight:
                int page = VisibleRowCount();
                int dir = keyCode == Keycode.DpadLeft ? +page : -page; // 左=向下按屏挪，右=向上
                _favorites.MoveBy(num, dir);
                RefreshSortList();
                break;
            case Keycode.DpadCenter or Keycode.Enter: // 放下并立即保存（持久化）
                _sortMode = false;
                _prefs?.Edit()?.PutString("favorites", _favorites.Serialize())?.Apply();
                _sortChannel = null;
                FlashInfo("排序已保存");
                RefreshSortList();
                break;
            case Keycode.Back or Keycode.Menu: // 取消复原
                _sortMode = false;
                _favorites.Restore(string.Join(",", _sortBackup));
                _sortChannel = null;
                FlashInfo("已取消排序");
                RefreshSortList();
                break;
            case Keycode.Info: // 一键置顶
                _favorites.MoveToTop(num);
                RefreshSortList();
                break;
        }
    }

    private int VisibleRowCount()
    {
        if (_listView is null) return 4;
        var vis = _listView.LastVisiblePosition - _listView.FirstVisiblePosition + 1;
        return Math.Max(1, vis);
    }

    /// <summary>排序中/排序后重建列表，焦点跟随被排序的台。</summary>
    private void RefreshSortList()
    {
        if (!_listOpen) return;
        OpenChannelList();
    }

    /// <summary>设置键 4077 单击（未长按）：呼出/收起软件设置面板（底部透明面板）。</summary>
    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if ((int)keyCode == 4077)
        {
            if (!_settingsLongFired && _crashOverlay is null && _diagOverlay is null) ToggleSettingsPanel(); // 日志/诊断面板开着：设置键只吞不切换
            return true;
        }
        return base.OnKeyUp(keyCode, e);
    }

    private void Step(int dir)
    {
        var all = _catalog.All.ToList();
        if (all.Count == 0) return;
        var idx = _current is null ? -dir : all.FindIndex(c => c.Number == _current.Number);
        for (var i = 1; i <= all.Count; i++)
        {
            var cand = all[((idx + dir * i) % all.Count + all.Count) % all.Count];
            if (_precheck.Of(cand) == ChannelHealth.Dead) continue; // 连不上：跳过不切
            if (cand.Number == _current?.Number) return;            // 绕一圈只剩当前台：不动
            Play(cand);
            return;
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // B 档：自定义键绑打开道映 → 回上次频道。
        // v1.0.9 起重接播放统一交给 OnResume（singleTask 重开必经 OnResume），
        // 这里不再自行起播，避免双 Play 造成重复停起。
    }

    // v1.0.9 退后台统一策略（真机反馈修复）：
    // 无论载体，退后台一律停播（声音、取流、看门狗全停），回前台重接当前台。
    // 旧纹理路径「静音续播」在真机上出现回前台黑屏有声——播放内核活着但视频面
    // 已被系统收走且未重建，IsAlive 判活通过就永远黑屏。统一停播后回前台必然
    // 重新接线起播，画面必出；代价是退后台不续播，电视场景可接受（第一原则：保画面）。
    protected override void OnPause()
    {
        base.OnPause();
        // 活着退后台 = 载体没崩，视为兼容（用户中途退出不该误判为崩溃）
        if (_carrierTexture && !_frameForCurrentSwitch) CarrierPolicy.MarkOk(this);
        _host.Stop();
        // v1.0.10 第①项：退后台即把源健康账落盘（后台线程，不阻塞界面）
        // v28 读盘优化：先等灌回再落盘——灌回未完成时落盘会把没读完的旧账覆盖成空账
        var store = _healthStore;
        var scores = _scoreStore;
        if (store != null || scores != null)
        {
            var load = _healthLoadTask;
            Task.Run(async () => { if (load != null) await load; store?.FlushNow(); scores?.FlushNow(); });
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!_resumedOnce) { _resumedOnce = true; return; } // 首启：OnCreate 已起播，不重复
        if (_current != null)
        {
            // lifecycle P1：列表开着回前台也统一重接（列表浮层在上、画面在下，不冲突）——
            // 原条件带 !_listOpen 时，列表开着退后台→回前台不重接，收列表又无补播路径=黑屏死路
            _frameEverShown = false; // 复位：重播按冷启动路径立即亮牌
            _ = ResumeReconnectAsync(_current); // v23 修法 A：先探网络就绪再统一重接当前台（走取流缓存秒回），画面必重建
        }
    }

    /// <summary>
    /// v23 修法 A：回前台重接前先探网络就绪——未就绪 500ms×6≈3 秒步进等待（ResumeNetWaitPolicy），
    /// 超时照常 Play 绝不无限等；等待期间用户切台即让位。打点 [ConnectRetry] 真机可验收。
    /// </summary>
    private async Task ResumeReconnectAsync(Channel ch)
    {
        int step = 0;
        while (!NetworkReady() && ResumeNetWaitPolicy.ShouldWait(step))
        {
            if (step == 0) CrashNet.Record("[ConnectRetry] 回前台网络未就绪，步进等待后重接（v23 A）");
            await Task.Delay(ResumeNetWaitPolicy.StepMs);
            if (!ReferenceEquals(_current, ch)) return; // 等待期间用户已切台：让位
            step++;
        }
        if (!ReferenceEquals(_current, ch) || _current is null) return;
        if (!NetworkReady()) CrashNet.Record("[ConnectRetry] 回前台等待约 3 秒网络仍未就绪，照常发起重接（不无限等）");
        Play(ch);
    }

    /// <summary>v23 修法 A：当前默认网络是否就绪（有活动网络且具备 Internet 能力）；探测能力不可用时不挡重接。</summary>
    private bool NetworkReady()
    {
        try
        {
            if (GetSystemService(ConnectivityService) is not global::Android.Net.ConnectivityManager cm) return true;
            var net = cm.ActiveNetwork;
            if (net is null) return false;
            var caps = cm.GetNetworkCapabilities(net);
            return caps?.HasCapability(global::Android.Net.NetCapability.Internet) == true;
        }
        catch { return true; } // 探测本身失败：宁可放行也不卡重接
    }

    private const int TimeJumpClearThresholdMs = 60_000; // vc38: 时钟跳变清账阈值（≤此漂移视为 NTP 微调不动账）

    /// <summary>
    /// v25 修法二 2c/2d + vc38 时钟分治：网络变更与时钟跳变分开处理——
    /// 换 IP（ConnectivityAction）= 真环境变化：退避账与票账冷却立即清零，不限频（连发即环境在抖，清了重来代价小）。
    /// 时钟跳变（ActionTimeChanged）= 仅票账冷却吃墙钟，退避账吃单时钟（TickCount64）不受墙钟跳变污染，永不清；
    /// 且仅 >60s 大跳才清（NTP 微调/时区变更不动账），5 分钟限频被漂移判定取代。
    /// 票据缓存保留验证（失败自然重新入账，退避期不把好环境白冻 2 小时）。
    /// </summary>
    private void RegisterEnvReceiver()
    {
        _envReceiver = new EnvLedgerReceiver(
            onConnectivityChanged: () =>
            {
                try
                {
                    _resolver.ClearCooldowns(); // 票账冷却清零（票据缓存保留验证）
                    _backoff.Clear();           // 源退避账清零（streak 一并归零，新环境从 2 分钟基值重算）
                    CrashNet.Record("[账本] 网络变更：退避账与票据冷却清零（票据缓存保留验证）");
                }
                catch (Exception ex) { CrashNet.Record($"[账本] 网络清账失败：{ex.Message}"); }
            },
            onTimeChanged: () =>
            {
                try
                {
                    var wallNow = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    var tickNow = System.Environment.TickCount64;
                    var lastWall = Volatile.Read(ref _lastTimeWallMs);
                    var lastTick = Volatile.Read(ref _lastTimeTickMs);
                    Volatile.Write(ref _lastTimeWallMs, wallNow);
                    Volatile.Write(ref _lastTimeTickMs, tickNow);
                    if (lastWall < 0 || lastTick < 0) return; // 首次回调只立基准，不清账
                    var drift = Math.Abs(wallNow - (lastWall + (tickNow - lastTick)));
                    if (drift <= TimeJumpClearThresholdMs) return; // NTP 微调：不动账
                    _resolver.ClearCooldowns(); // 票账冷却清零（票据缓存保留验证）
                    CrashNet.Record($"[账本] 时钟大跳 {drift / 1000.0:F0}s：清票据冷却（退避账吃单时钟不清）");
                }
                catch (Exception ex) { CrashNet.Record($"[账本] 时钟清账失败：{ex.Message}"); }
            });
        var filter = new IntentFilter();
        filter.AddAction(global::Android.Net.ConnectivityManager.ConnectivityAction);
        filter.AddAction(Intent.ActionTimeChanged);
        RegisterReceiver(_envReceiver, filter);
    }

    private sealed class EnvLedgerReceiver : BroadcastReceiver
    {
        private readonly Action _onConnectivityChanged;
        private readonly Action _onTimeChanged;
        public EnvLedgerReceiver(Action onConnectivityChanged, Action onTimeChanged)
        {
            _onConnectivityChanged = onConnectivityChanged;
            _onTimeChanged = onTimeChanged;
        }
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == global::Android.Net.ConnectivityManager.ConnectivityAction)
                _onConnectivityChanged();
            else if (intent?.Action == Intent.ActionTimeChanged)
                _onTimeChanged();
        }
    }

    protected override void OnDestroy()
    {
        _precheckCts?.Cancel();
        _precheckCts?.Dispose();
        _precheckCts = null;
        _hotStandby?.Stop(); // v25 修法七 1c：退出即停热备校验流（会话代数作废，不再漏跑）
        if (_envReceiver != null)
        {
            try { UnregisterReceiver(_envReceiver); } catch { /* 已注销不碍 */ }
            _envReceiver = null;
        }
        _epgCts?.Cancel(); // B 包第 14 项：退出停节目单拉取
        _epgCts?.Dispose();
        _epgCts = null;
        _ticketKeeper?.Dispose();
        _ticketKeeper = null;
        _statsTicker?.Dispose(); // ④画面信息行节拍
        _statsTicker = null;
        // v28 读盘优化：退出前先等健康账灌回（启动初期即退出的窗口，毫秒级读盘），再落盘
        _healthLoadTask?.Wait();
        _healthStore?.FlushNow(); // v1.0.10：退出前把源健康账落盘
        SourceProfileStore.Shared.FlushNow(); // 源档案接线：退出前档案落盘（同 source_health.json 家法）
        _host.Dispose();
        base.OnDestroy();
    }
}
