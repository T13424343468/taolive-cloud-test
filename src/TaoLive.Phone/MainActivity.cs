using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using TaoLive.Catalog;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Players;
using TaoLive.Session;

namespace TaoLive.Phone;

/// <summary>
/// 电视端主入口（2.0 W5 接线 + vc53 界面整套搬运 2026-11-15 老板真机令）：
/// 界面视觉与交互照 vc53——全屏视频垫底、半透明水墨抽屉盖画面、亮度自适应、
/// 真实台标列表（无台标=首字占位）、五分类页签、底部节目单、顶部信息条、起播亮牌、水墨退出弹窗。
/// 底层保留 2.0 事件/会话架构（PlaybackSessionManager + ExoPlayer 内核 + TextureView 挂面），
/// 即「vc53 的皮，2.0 的骨」。操作逻辑：菜单键呼/收抽屉、上下换台、左右切页签、确认收藏、频道号直达。
/// </summary>
[Activity(Label = "TaoLive", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public partial class MainActivity : Activity
{
    private IServiceProvider _services = default!;
    private PlaybackSessionManager _manager = default!;
    private IReadOnlyList<Channel> _channels = ChannelTable.Build(); // 订阅新收录频道并入后整体替换（表尾追加，编号顺延不变号）

    private FrameLayout _root = default!;
    private LinearLayout _listPage = default!;
    private FrameLayout? _scrim; // 手机版整改令 2026-11-27：浮层遮罩（点外关闭）
    private ListView _list = default!;
    private FrameLayout _playerPage = default!;
    private readonly List<TextView> _tabViews = new();
    private ListTabs.Tab _tab = ListTabs.Tab.All;
    private long _healthLastAtMs;
    private readonly SwitchDebouncer _debouncer = new();
    private TextureView _videoView = default!; // 视频载体（真机反馈批#6 修）：TextureView 在视图树内可被盖，SurfaceView 独立窗口层被盖即整层不渲染
    private Surface? _textureSurface; // 自建 Surface（SurfaceTexture 就绪后创建，生命周期归壳层）
    private LinearLayout _toast = default!;      // 提示胶囊（v28 老板令：灵动岛式，胶囊包住摄像头孔、孔上无字、文字两侧）
    private TextView _toastLeft = default!;       // 胶囊左段文字（主文案）
    private TextView _toastRight = default!;      // 胶囊右段文字（副文案/图标位，默认空）
    private View _toastGap = default!;            // 胶囊中段空档（宽度=挖孔宽，孔上不画字）
    private View _toastBalL = default!;           // vc121 包孔扶正：左侧平衡占位（右段更宽时补差值，默认 0 宽）
    private View _toastBalR = default!;           // vc121 包孔扶正：右侧平衡占位（左段更宽时补差值，默认 0 宽）
    private int _cutoutHoleW;                     // 挖孔宽（px，0=无挖孔信息走兜底居中）
    private int _cutoutHoleCenterX;               // 挖孔中心 X（px）
    private int _pillGeoRetries;                  // vc133：挖孔几何未就绪时的等帧重试计数（首颗胶囊 insets 未挂上踩空）
    private int _cutoutHoleCenterY;               // 挖孔中心 Y（px）
    // vc124 老板令（真机「胶囊从来没包住孔」实锤）：空态胶囊重构成系统胶囊同款三段式——
    // 左状态行 + 中段孔位空档 + 右可点链接行，孔落在胶囊正中、内容分两侧（系统状态栏胶囊口径）
    private LinearLayout _emptyPill = default!;
    private TextView _emptyLeft = default!;       // 空态胶囊左段：状态行
    private TextView _emptyRight = default!;      // 空态胶囊右段：可点链接行（立即重试/打开列表）
    private View _emptyGap = default!;            // 空态胶囊中段空档（宽度=挖孔宽）
    private View _emptyBalL = default!;           // 空态胶囊左侧平衡占位
    private View _emptyBalR = default!;           // 空态胶囊右侧平衡占位
    private readonly TaoLive.Session.NetGuard _netGuard = new();
    private readonly Handler _main = new(Looper.MainLooper!);
    private bool _onPlayerPage;
    private bool _listOpen; // vc53 抽屉口径：抽屉开着=列表键位，画面在面板下继续
    private int _currentIndex;
    private string _digits = "";
    private Java.Lang.Runnable? _digitCommit;
    private bool _watchdogRunning;
    private bool _watchdogStopped;
    private long _lastVideoFrameAtMs;   // #75 音画同出兜底：最近一次视频帧单调时刻（0=尚未出过帧）
    private int _stallRebuildCount;     // #75：同源快速重建连续计数（有帧即清账，2 次封顶切线路）
    private long? _failoverAtMs; // 换源黑屏埋点账（黑屏镜黄④）：换源起点 → Playing 出画恢复时长落运行台账
    private Java.Lang.Runnable _toastHide = default!;
    // 文案矩阵单点（方案书 v2.4 §25 + vc113 原型对照令）：失败终态三件套文案——
    // 发生了什么（信号中断，正在自动重试·第 X/6 次）+ 为什么（已自动换线尝试）+ 能怎么办（等 / 立即重试 / 打开频道列表）；
    // 禁裸错误码；长播无源退级提示按等待时长叠加（SetEmptyPillText 单点装配，含可点链接）。
    private const string EmptyStateLead = "信号中断，正在自动重试";
    private Java.Lang.Runnable _emptyHintTick = default!; // 退级提示节拍（ShowEmptyState 起、HideEmptyState 撤）
    private long _emptyStateAtMs;               // 空态起点（单调钟，分钟级即可）
    private readonly CntvEpgClient _epg = new();
    private HashSet<string> _favorites = new();
    // 收藏账跨线程快照（vc84 首轮交叉审查修）：PrecheckQueue 在调度泵线程读收藏，
    // HashSet 非线程安全——主线程变更后重发布不可变快照，泵线程只读快照引用，不碰工作集。
    private volatile HashSet<string> _favoritesView = new();
    private readonly FavoriteStore _favStore = new(); // 收藏顺序（vc53 排序模式口径）
    private bool _sortMode;      // 收藏排序状态机（v1.0.8 第14项）：列表内长按确认进入，退出恢复默认键位
    private Channel? _sortChannel;
    private readonly List<int> _sortBackup = new();
    private static bool _precheckEnabled = true; // vc105 老板令：以 93 为基线——预检握手+预热入口接回（vc101 拆除令被「以93为基线」覆盖），读盘持久化恢复；

    /// <summary>开机全表备货避让节拍（老板批 2026-11-16 第③条·避让）：先让预检探出默认台/收藏台首批绿点，备货再上路。</summary>
    private const int BootWarmAllGraceMs = 3 * 60 * 1000;
    private const int BootWarmLastGraceMs = 2 * 1000; // 刀四：上次台开机预热延后（先让首帧/首批绿点落，共车道让路）
    // #15 改静态：进程级 once-block 里的换台预取闭包读它，不能钉在首个壳上
    private ChannelPrecheck _precheck = default!;
    private QualityHealthStore _qualityBook = default!; // 实测账（vc85 两本账之二）：排序合成与微卡/盲区记账同源
    private TicketResolver _resolver = default!;
    private WatchTimeLedger _watchLedger = default!;         // 观看时长账本（刀三）：进程级实例，每壳接回
    private long? _watchSinceMs;                             // 在看起算点（TickCount64）：Playing 起算，封账即记
    private string? _watchEpgKey;                            // 在看台 EpgKey（账本一格一台）
    private System.Threading.CancellationTokenSource? _precheckCts;

    // ---- 真机反馈批#15：显示大小换档原地换皮 —— 服务图/会话/内核进程级常驻，Recreate 不再断流 ----
    // 换档旧路径 Recreate() 整个 Activity 推倒重建，播放会话跟着销毁重建、重新取流（真机实锤「切尺寸重连」）。
    // 改造：服务图只建一次（进程级），会话管理器/内核跨 Recreate 存活；新 Activity 只重挂界面与新 Surface，
    // 内核回调统一经静态壳引用（Shell）路由到当前实例。当前台、热备、健康账全都不动，画面不闪不断。
    private static IServiceProvider? _processServices;
    private static TicketResolver? _processResolver;
    private static WatchTimeLedger? _processWatchLedger; // 观看时长账本（刀三）：进程级一本账，跨 Recreate 存活
    private static int _prefetchSeq;                        // 邻台预取代数：换台即换代（进程级：预取任务可能跨 Recreate 存活）
    private static Channel? _prefetchPending;               // 邻台预取待办台：换台即换代换台，Work 执行时读最新（快速连切只取最后一代）
    internal static MainActivity? Shell;                    // 进程级壳引用：内核/会话回调统一指向当前实例
    private static EventHandler<FailoverEventArgs>? _failoverRelay; // 换源回调转接器：进程级唯一（幂等 -=/+=），换壳不双收
    private static EventHandler? _slowGraceRelay; // 慢连宽限回调转接器（vc86 F7）：宽限窗挂「正在换信号」提示，换壳不双收
    private static EventHandler<SourceEndpoint>? _weakFillRelay; // 灌速探针转接器（vc89）：弱线起播期后台焐热备线票，换壳不双收
    // vc86 C 预检对齐（老板令 F4 拍板）：5s→8s——内核连接超时 12s/读超时 15s 已放宽，预检先行超时
    // 会把「慢而通」的源探成死票错标；8s 与死循环方案 F4 预检超时对齐，慢源不再被预检抢杀
    private static readonly HttpClient PrecheckHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    // ---- vc82 统一调度器（方案三，老板拍板单开工程）：后台各路活全部登记排班，自跑循环退役 ----
    // 排班归调度器（谁先谁后、轮次串行），车道纪律由各路活内部逐请求自过（绝不并发）——两层职责正交互为保险。
    private static TaoLive.Scheduler.BackgroundScheduler? _scheduler;

    // ---- vc53 界面搬运：亮牌 / 底部节目单 / 水墨退出弹窗 ----
    private FrameLayout _loadingPanel = default!;
    private TextView _loadingText = default!;
    private int _loadingSeq;                 // 亮牌代数：换台即换代，旧牌绝不再亮
    private bool _veilAwaitingReveal;        // 换台遮罩待真实出画（真机反馈批#11 续修：Ready≠出画）
    private int _veilSeq;                    // 遮罩代数：迟到的撤牌只撤自己那一代（快速连切防串）
    private Java.Lang.Runnable? _veilCountdown; // 真机反馈批#16：出画倒计时句柄（换台/出画即作废）
    private string? _veilChannelName = "";   // #16：倒计时文案台名（#73 修：失败终态清空可空）
    private long _switchAtMs;                // #16：本台切换起点（出画耗时账起点）
    private static double RevealEtaMs = 3000; // #16：出画耗时指数均值（进程级自校准，倒计时基准）
    private TextView? _waterLevelText;       // 老板令 2026-11-16：列表页头「实时水位:XX秒」——当前会话内核攒着还没播的量
    private int _epgSeq;                     // 节目单代数：换台即作废旧拉取
    // vc112 老板拍板：退出确认弹窗整删（v1.0.9 防误退口径退役）——返回=停播攒流退后台，回前台即播

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 持久化设置（vc53 口径）：UI 缩放档 + 后台预热开关 + 进阶调优旋钮，启动装载
        //（缩放档须在 BuildRoot 前就位，全套令牌按档位重算）
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        // 尺寸跟系统（老板 2026-11-27 拍板「跟着系统显示大小做三套，不单独给设置项」）：
        // vc135 双跟随令（2026-11-28 老板拍板「全落」）：字体滑条 5 档归并三档——相对首启 fontScale
        // 比值 ≤1.05→小(1.0)、≤1.2→中(+10%)、>1.2→大(+20%)（步距「中10% 高20」）；
        // 首启 fontScale 拍板存档（ui_fontscale_ref，仿 ui_density_ref），首启所见=小档基准
        // （老板系统字体/显示大小均停最小档，新版装上所见不变）。文字 sp 渲染层自乘的 fontScale
        // 在 T()/Scale 里归一除掉，滑条只切档不重复放大；间距/图标/盒子跟同一套 TierRatio，
        // 文字与间距永远同比例。字体变更走 Activity 重建（清单无 configChanges），OnCreate 重读重算。
        // 显示大小维持现状：密度等比跟随（SystemScale=当前密度/首启密度）。
        UiScale.Factor = 17f / 36f;
        // vc117 全元素比例换算令（2026-11-28 老板拍板）：所有界面元素统一按屏幕比例换算——
        // 分辨率比例项=短边px/(390×首启密度)；系统显示大小跟随项=当前密度/首启密度。
        // 首启密度拍板存档（ui_density_ref）：首启时的系统档=「中」基准（老板「目前这个作为中」口径），
        // 此后系统改显示大小（密度变）→ 界面等比跟随；换设备/分辨率 → 比例项跟随短边。
        var dm = Resources!.DisplayMetrics!;
        float densRef = _prefs.GetFloat("ui_density_ref", 0f);
        if (densRef <= 0f)
        {
            densRef = dm.Density; // 首启拍板：当前系统档=「中」
            _prefs.Edit()!.PutFloat("ui_density_ref", densRef)!.Apply();
        }
        UiScale.DensityRef = densRef;
        UiScale.SystemScale = dm.Density / densRef;
        UiScale.ScreenScale = Math.Min(dm.WidthPixels, dm.HeightPixels) / (390f * densRef);
        UiScale.PxScale = UiScale.ScreenScale * UiScale.SystemScale; // 令17：间距/线框盒/图标与文字同一套比例
        // vc135 双跟随：首启 fontScale 拍板存档（ui_fontscale_ref），首启所见=小档基准；
        // Factor=中档基准 17/36 × 首启 fontScale，滑条切档走 TierRatio（见 UiScale.UpdateFontScale）。
        float fsRef = _prefs.GetFloat("ui_fontscale_ref", 0f);
        float fsNow = Resources!.Configuration!.FontScale;
        if (fsRef <= 0f)
        {
            fsRef = fsNow > 0f ? fsNow : 1f; // 首启拍板：当前所见=小档基准
            _prefs.Edit()!.PutFloat("ui_fontscale_ref", fsRef)!.Apply();
        }
        UiScale.FontScaleRef = fsRef;
        UiScale.Factor = 17f / 36f * fsRef;
        UiScale.UpdateFontScale(fsNow);
        // 收口批④开关合流：预热与预检共车道只留预检开关，预热（开机全量预热+换台邻台预取+开机默认台焐热）
        // 一律随 _precheckEnabled 同起同停——两个开关管同一条车道必出一个死开关（方案书收口批④原文）。
        // vc105 老板令：以 93 为基线——读盘复活恢复（默认开），vc101 硬关口径作废。
        _precheckEnabled = _prefs.GetBoolean("precheck_enabled", true);
        // 内置订阅源库（老板 2026-11-26 拍板「只做内置订阅」）：载入落盘档案；开关默认关，开了才拉合集
        SubscriptionSourceStore.Shared.Load(FilesDir!.AbsolutePath);
        SubscriptionSourceStore.Reporter = msg => Android.Util.Log.Info("TaoLive", msg);
        _subEnabled = _prefs.GetBoolean("sub_enabled", false);
        // 启动错峰（老板令「拉源避开播放」）：开机先让预热/断点回看跑稳，90s 后再拉源；届时若在播照旧挂起
        if (_subEnabled)
            new Handler(Looper.MainLooper!).PostDelayed(
                () => { if (_subEnabled) KickSubscriptionRefresh(startup: true); }, SubStartupDelayMs);
        AdvancedTuning.Apply(
            _prefs.GetInt("adv_fail_gate", AdvancedTuning.DefaultFailGate),
            _prefs.GetInt("adv_reprobe_min", 5) * 60_000L,
            _prefs.GetInt("adv_cache_sec", AdvancedTuning.DefaultListCacheSec),
            _prefs.GetInt("adv_retry_base_sec", 120) * 1000L,
            _prefs.GetInt("adv_retry_max_min", 120) * 60_000L,
            _prefs.GetInt("adv_prefetch_sec", AdvancedTuning.DefaultPrefetchWindowSec), // #67 库存档旋钮
            _prefs.GetInt("adv_live_lag_sec", AdvancedTuning.DefaultLiveLagSec), // 落后目标旋钮（老板令 2026-11-16）
            _prefs.GetInt("adv_failover_cap", AdvancedTuning.DefaultFailoverCapPerMin)); // 防快切闸第 7 旋钮（老板 2026-11-17 拍板接通）
        CrashNet.Install(); // 未捕获异常先落日志再放行（崩溃日志页可查可清）

        // 真机反馈批#15：进程级壳引用先立（内核/会话回调经它路由到当前实例）
        Shell = this;
        _resolver = _processResolver ??= new TicketResolver(); // 取票缓存进程级共享，Recreate 不丢
        // 观看时长账本（刀三）：进程级一本账跨 Recreate 存活；挂账即载入历史账（损坏按零账，画像不挡播放）
        _watchLedger = _processWatchLedger ??= new WatchTimeLedger(FilesDir!.AbsolutePath);
        _watchLedger.Load();
        var resolver = _resolver;
        // vc69 坏票不自焚：会话层报 HTTP 状态错（坏票）时作废该端点票据缓存，原地换新票重试一次
        TaoLive.Session.PlaybackSession.TicketCacheInvalidator = (channelId, endpoint) =>
            resolver.Invalidate(channelId, endpoint.Url);
        // vc87 票据热轮换（内核级，老板拍板「三方案一次到位」）：播放内核 4xx 即刻按失败 URL 反查
        // 票据缓存换新票同偏移重开，不等会话层自救链——坏票 6 秒循环病灶的另一刀。静态委托进程级单份，
        // 取票缓存进程级共享（_processResolver），once-block 接线幂等（重复赋值同值）
        TaoLive.Players.RacingDataSourceFactory.TicketReissuer = url => resolver.ReissueAsync(url);

        // 服务图进程级只建一次：会话管理器/内核跨 Recreate 存活（#15 原地换皮的核心——
        // 换档旧路径 Recreate 把播放会话连带销毁，重新取流=真机「切尺寸重连」）
        if (_processServices is null)
        {
        // 每会话独享内核（Current+Standby 双实例热切的资源前提）；
        // 内核持 ApplicationContext（随进程存活），不持 Activity——换壳后内核照常解码渲染
        IPlayer KernelFactory()
        {
            var player = new ExoPlayerPlayer(ApplicationContext!); // 随进程存活的应用上下文，不持 Activity——换壳后内核照常解码渲染
            player.WithStreamResolver(endpoint =>
                resolver.ResolveAsync(endpoint, player.CurrentChannelKey ?? "", player.CurrentChannelName ?? ""));
            WireFramePulse(player); // 画面平稳埋点（方案书 v76/v20：帧回调 10Hz → 滑窗 P95 断言）
            WireVeilReveal(player); // 换台遮罩真实出画撤除（真机反馈批#11 续修 2026-11-15）
            WireVideoFault(player); // vc66 审查修正：内核视频故障信号→只出声守卫快速递进（根因上屏）
            return player;
        }

        // 换台遮罩真实出画撤除（真机反馈批#11 续修 2026-11-15）：内核 Ready 即撤遮罩会露旧台冻结帧
        // （TextureView 停帧保留最后渲染内容，新流首帧渲染前那几秒「一直挂着上个台画面」实锤）。
        // 改挂待撤旗：本台第一帧真渲染的帧节拍才准撤——键位对账：只有「当前会话本台」的帧节拍
        // 有资格（热备攒流/自救尾帧不掺）；渲染线程回调落主线程执行，遮罩代数防快速连切串代。
        // #15：经 Shell 路由——内核跨 Recreate 存活，帧节拍必须打到当前壳，不钉死建内核时的那个壳
        void WireVeilReveal(ExoPlayerPlayer player)
        {
            // vc72 审查清零 P1：帧回调在渲染线程，VeilFrameTick 写遮罩代/出画倒计时账与主线程
            // 遮罩倒计时并发——过 _main 归主线程（L162 注释「回调落主线程」自本修起为真）。
            player.OnFrameTick += (_, _) => _main.Post(() => Shell?.VeilFrameTick(player));
        }

        // vc66 审查修正（老板审批方案第三刀「根因上屏」）：内核视频解码器报错即触发 OnVideoFault——
        // 当前会话本内核处于 Playing 时，把零帧账直接判过期（回拨 60 秒），守卫 3 秒泵下一拍即走
        // 重建→切线路→强制换源递进，不再硬等 20 秒窗。只动账不动状态机：递进动作仍由守卫统一执行，
        // 与内核 PlaybackException→Failed→failover 天然竞速但方向一致（谁先救回都行，账重复判过期幂等）。
        // 信号回调在内核播放线程：过 _main 回主线程，与守卫泵读写零帧账不并发。
        void WireVideoFault(ExoPlayerPlayer player)
        {
            player.OnVideoFault += (_, detail) => _main.Post(() =>
            {
                if (_manager.Current is not { } c
                    || !ReferenceEquals(c.Kernel, player)
                    || c.State != PlaybackState.Playing)
                {
                    return; // 非当前会话本内核（热备攒流），或已不在播：账归守卫原口径管，不掺和
                }
                CrashNet.Record($"[只出声兜底] 内核视频故障：{detail}——零帧账判过期，守卫下一拍递进");
                _lastVideoFrameAtMs = System.Environment.TickCount64 - 60_000;
                c.NotifyVideoFault(detail); // vc85 两增强①⑥：坏帧信号进健康线（实测账降权+链路裕度负项）
            });
        }

        // 画面平稳无感量化埋点（v20）：帧回调进滑窗，每 10 秒取数落 [画面平稳] 日志行；
        // P95≤250ms 且连续 3 窗超阈即写「疑似卡顿」——真机验收按此行核，不靠人工观感。
        // 内核随会话换新即重挂，闭包生命周期随内核，无跨会话账。
        void WireFramePulse(IPlayer player)
        {
            var pulse = new FramePulse();
            DateTimeOffset lastReport = DateTimeOffset.UtcNow;
            DateTimeOffset? startupFirstTick = null; // 起播流畅台账：本内核首个出帧时刻
            bool startupStallSeen = false;           // 起播稳定窗内卡顿疑点
            bool startupReported = false;            // 台账只记一次
            player.OnFrameTick += (_, e) =>
            {
                // vc72 审查清零 P1：帧回调在内核渲染线程（IPlayer 契约），零帧账/平稳账/起播账同时被
                // 主线程守卫泵读写——整处理器体过 _main 归主线程（与 WireVideoFault 同款），
                // List/Queue 并发读写即断；注释「渲染线程回调落主线程执行」自本修起为真（原注释与契约矛盾）。
                _main.Post(() =>
                {
                // vc66：帧账只认当前会话的内核——热备攒流哑面解码的帧不替当前台续零帧账（只出声兜底不失明）
                if (_manager.Current is not { } c || !ReferenceEquals(c.Kernel, player)) return;
                _lastVideoFrameAtMs = System.Environment.TickCount64; // #75 音画同出兜底：视频帧活着，账随帧走
                pulse.Record(e.AtUtc);
                // 起播流畅台账（真机反馈批#12 统计令 2026-11-15，统计不能少）：首帧出画到平稳的时长落
                // 运行台账，修复效果（门槛 12 秒+预取让路）真机验收按此行核。平稳判据=首帧满 20 秒且
                // 当前无疑点游程；窗内有疑点如实带出，不美化。
                if (startupFirstTick is { } t0 && !startupReported)
                {
                    if (pulse.StallSuspicion) startupStallSeen = true;
                    if (e.AtUtc - t0 >= TimeSpan.FromSeconds(20) && !pulse.StallSuspicion)
                    {
                        startupReported = true;
                        CrashNet.Record(startupStallSeen
                            ? $"[起播流畅] 首帧到平稳 {(e.AtUtc - t0).TotalSeconds:F1}s（起播窗含卡顿疑点）"
                            : $"[起播流畅] 首帧到平稳 {(e.AtUtc - t0).TotalSeconds:F1}s（无卡顿疑点）");
                    }
                }
                else if (startupFirstTick is null)
                {
                    startupFirstTick = e.AtUtc;
                }
                if (e.AtUtc - lastReport < TimeSpan.FromSeconds(10)) return;
                lastReport = e.AtUtc;
                var p95 = pulse.WindowP95Ms();
                // 真机反馈批#10 打点（2026-11-15）：出画中 10 秒零视频帧=「白屏只出声」排查锚点，
                // 音视频齐 Ready 才 Playing，此态停帧指向视频解码/渲染层（ExoPlayer 无软解兜底，与 vc53 VLC 本质差异）
                if (p95 is not { } ms)
                {
                    CrashNet.Record("[视频停滞] 出画中 10 秒无视频帧（白屏只出声排查锚点）");
                    c.NotifyVideoStall(); // vc87 停滞信号接入：记账归会话预防泵，窗内满 2 次预防换线
                    return;
                }
                // vc70 慢帧流接线：平稳疑点交会话记账（画面平稳优先令：壳层只埋点，攒满判线归预防泵）——
                // 台账三张定案「画面不停地抖」=慢帧流坏线，既有自救全不触发，靠平稳账单独定罪换源
                if (pulse.StallSuspicion) c.NotifySmoothnessSuspect();
                // 台账行带台名（vc70）：此前[画面平稳]行不带台名，真机账对不上是哪台的账
                CrashNet.Record(pulse.StallSuspicion
                    ? $"[画面平稳] {c.CurrentChannel?.Name ?? c.CurrentChannel?.Id ?? "?"} 疑似卡顿：连续 3 窗帧间隔 P95 {ms}ms 超阈 {FramePulse.P95ThresholdMs}ms"
                    : $"[画面平稳] {c.CurrentChannel?.Name ?? c.CurrentChannel?.Id ?? "?"} 帧间隔 P95 {ms}ms（阈 {FramePulse.P95ThresholdMs}ms）");
                });
            };
        }

        // 换台邻台预取（vc53 任务8）：取票落 ChannelWarmer 既有 5 分钟缓存（归一纪律，不建第二本账），
        // DNS 预解析失败静默（取流链路自带兜底）；全程过 BackgroundLane 单车道 Standard 档让路播放
        var prefetcher = new ChannelPrefetcher(
            async ch =>
            {
                try { return await new ChannelWarmer(resolver).WarmChannelAsync(ch, throughLane: false) ? new ResolvedStream { Channel = ch } : null; } // 邻台预取外层已持车道执行权，预热内不再排队（嵌套等自己=死锁）
                catch { return null; }
            },
            async host => { try { await System.Net.Dns.GetHostAddressesAsync(host); } catch { /* 预解析失败不挡预取 */ } });

        // #15：换台预取闭包是进程级一次性的，频道表用静态快照（ChannelTable.Build 静态数据无状态），
        // 不引用实例 _channels——否则闭包把第一个壳连整个视图树钉死到进程结束（内存泄漏）
        var channels = ChannelTable.Build();
        // vc82 收编②：邻台预取自起 Task.Run 圈外飞退役，登记进统一调度器（常规档一次性活，跑完即退役）。
        // 15 秒起播稳定窗+换代校验留在 Work 内（外层不占车道，内层逐邻台自过车道）；再换台先重登记重置一次性旗标再触发
        var neighborPrefetchJob = new TaoLive.Scheduler.BackgroundScheduler.Job
        {
            Name = "neighbor-prefetch",
            Interval = TimeSpan.Zero,
            OneShot = true,
            Tier = TaoLive.Scheduler.BackgroundScheduler.ScheduleTier.Standard,
            Work = async _ =>
            {
                try
                {
                    var target = _prefetchPending;
                    int seq = System.Threading.Volatile.Read(ref _prefetchSeq);
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    if (seq != System.Threading.Volatile.Read(ref _prefetchSeq)) return; // 已换台：旧代预取作废
                    if (target is not null)
                    {
                        await prefetcher.PrefetchNeighborsAsync(target, channels);
                        // 刀三（老板 2026-11-25 九件套定稿）：预取圈扩到「最近看过的台」——直接消费观看时长
                        // 账本快照（进程级一本账，读静态引用不钉壳）；邻台已覆盖者自动跳过
                        if (_processWatchLedger is { } ledger)
                            await prefetcher.PrefetchRecentAsync(target, channels, ledger.Snapshot());
                    }
                }
                catch { /* 预取失败不影响播放 */ }
            },
        };
        _services = _processServices = new ServiceCollection()
            .AddSingleton<IClock, global::TaoLive.Session.SystemClock>()
            .AddSingleton<IMainThreadDispatcher>(new AndroidMainThreadDispatcher(_main))
            .AddTaoLiveSession(KernelFactory, ch =>
            {
                // 换台即触发邻台预取（火车道让路：首帧未落地铁道自动门控，画面平稳优先）；
                // 真机反馈批#12 修 2026-11-15：预取原在首帧落地即排队上路——出画后内核正要抢带宽把
                // 水位从起播线填满，预取插队即抢网，起播期卡顿实锤之一。改延后 15 秒（起播稳定窗）
                // 再上路，换代作废旧延迟（快速连切只取最后一代）；预热随预检开关同起同停（收口批④开关合流）
                // vc82 收编②：这里只换代+触发，排班全归调度器
                if (_precheckEnabled)
                {
                    System.Threading.Volatile.Write(ref _prefetchPending, ch);
                    System.Threading.Interlocked.Increment(ref _prefetchSeq);
                    _scheduler?.Register(neighborPrefetchJob); // 重置一次性旗标（跑完即退役，再换台先重登记）
                    _scheduler?.TriggerNow("neighbor-prefetch");
                }
            })
            .BuildServiceProvider();
        _manager = _services.GetRequiredService<PlaybackSessionManager>();
        // vc82 统一调度器接线：执行口子=直调（调度器只管排班先后——档位升序、同档最久未跑优先、轮次串行）；
        // 车道纪律在每路活内部逐请求自过（预检探活/预热取票/邻台预取全如此），执行口子绝不再包一层——
        // 整轮过闸=整轮占住全局并发1（播放链救助全堵死），且内部再进车道=嵌套等自己死锁。
        // 调度器进程级一个脑子，各路活 Register 登记，StopPrecheckLoop 等负责退役旧路
        _scheduler = new TaoLive.Scheduler.BackgroundScheduler((tier, work, ct) => work(ct));
        _ = Task.Run(() => _scheduler!.RunAsync(CancellationToken.None)); // 进程级常驻：随进程生死
        // 内核回调异常出口（闪退镜黄⑤）：矩阵外跳转不掀回调链，落运行台账
        TaoLive.Session.PlaybackSession.KernelCallbackFailed += (_, msg) => CrashNet.Record($"[KernelCallback] {msg}");
        // 埋点补充（真机反馈批 2026-11-15）：会话/管理器/取票/预热/内核各层台账出口统一接 CrashNet——
        // 层向纪律由静态委托承接（Session/Catalog/Players 不反向依赖 Tv），未接线的层打点此前只进 logcat。
        TaoLive.Session.SessionTrace.Sink = m => CrashNet.Record(m);
        TaoLive.Catalog.TicketResolver.Reporter = m => CrashNet.Record(m);
        TaoLive.Catalog.ChannelWarmer.Reporter = m => CrashNet.Record(m);
        TaoLive.Players.ExoPlayerPlayer.Reporter = m => CrashNet.Record(m);
        TaoLive.Session.ChannelPrecheck.Reporter = m => CrashNet.Record(m); // 预检既有 Reporter 此前未接线，一并接通
        // 备源耗尽提示（一期口径：全源不可达时明确提示，不静默装死；OK 键重试）
        // 只收当前会话事件（热备攒流/换台竞速不掺内部，画面不受热备起播影响——方案书 v5 口径）
        // #15：进程级只订阅一次，回调经静态壳路由到当前实例——每个壳重复订阅会让事件翻倍收
        _manager.SessionStateChanged += (_, e) => Shell?.HandleSessionStateChanged(e);
        } // ← #15 once-block 闭合：以下为每壳（per-OnCreate）必跑段

        // 服务图已就位（once-block 首建、跨壳复用）：本壳字段接回进程级实例
        _services = _processServices!; // once-block 已跑过：进程级必非空
        _manager = _services.GetRequiredService<PlaybackSessionManager>();

        // —— per-OnCreate 段：引用本壳回调/账本/偏好的接线，每次壳重建都要重挂（闭包持 this 的全在这） ——

        // 收口批①：备源队列吃内置表（换源触发时才遍历注册表，不进预检常驻轮）+ 活性按健康账轻排序
        // （同台活源在前、账新者在前——只动同台内排序不改台间顺序，v28 轻排序条款）；
        // 回主源迟滞判据=主源预检轮次连续命中 OK ≥3 次（复用预检结果，不另起独立探测）+ 稳定 5 分钟（管理器内判）
        _manager.FallbackSourceProvider = ch => FallbackQueueSource.FromRegistry(ch, FallbackHealthHintOf);
        _manager.PrimaryAliveConfirmed = ch =>
            _precheck.SourceHealthOf(ch) is { State: ChannelHealth.Alive, ConsecutiveOks: >= PlaybackSessionManager.ReturnToPrimaryMinConsecutiveOks };
        // 收口批③：会话 LRU 按内存分档（memoryClass≤128MB 低配档 4、高配 8；定标后只调常量）；取不到按低配保守
        var activityManager = (ActivityManager?)GetSystemService(ActivityService);
        _manager.RetainedCapacity = PlaybackSessionManager.CapacityFor(activityManager?.MemoryClass ?? 0);
        _favorites = GetSharedPreferences("favorites", FileCreationMode.Private)!
            .GetStringSet("ids", new List<string>())!.ToHashSet();
        _favoritesView = new HashSet<string>(_favorites); // 发布快照（跨线程读者只读此份）
        // 收藏顺序（vc53 v1.0.8 第14项：顺序跨启动记忆；逗号编号串，坏段忽略）
        _favStore.Restore(GetSharedPreferences("favorites", FileCreationMode.Private)!.GetString("order", null));
        // 开机预热（W8：取票口预取进 5 分钟缓存，换台零等待；退避账挂账不烧请求）
        // 预热随预检开关同起同停（收口批④开关合流，不再单设开关）；#15：每壳重跑无妨——取票缓存进程级共享，焐热幂等
        if (_precheckEnabled)
        {
            // vc82 收编①：开机全表备货登记进统一调度器（常规档一次性）——不再自起 Task.Run 圈外飞
            _scheduler?.Register(new TaoLive.Scheduler.BackgroundScheduler.Job
            {
                Name = "boot-warm-all",
                Interval = TimeSpan.FromHours(1),
                OneShot = true,
                Tier = TaoLive.Scheduler.BackgroundScheduler.ScheduleTier.Standard,
                Work = async _ => { await new ChannelWarmer(resolver).WarmAllAsync(ChannelTable.Build()).ConfigureAwait(false); },
            });
            // 老板批 2026-11-16 第③条·避让（后半场）：备货延后 3 分钟再上路——备货取票与预检探活
            // 共用全局单车道，且调度器整轮串行（先跑的整轮占泵），开机即跑会把默认台/收藏台的
            // 首批绿点整个拖后。先让预检探出第一批绿点，备货随后补；取票缓存进程级共享，晚补无感。
            // 延后经 TriggerNow 实现（不占调度泵：未触发前调度器照常跑预检）；#15 每壳重跑注册/触发
            // 幂等（同名覆盖、TriggerNow 空操作），壳重建只重置计时，备货本就幂等。
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(BootWarmAllGraceMs).ConfigureAwait(false);
                    _scheduler?.TriggerNow("boot-warm-all");
                }
                catch { /* 触发失败不挡启动：壳重建重登再补 */ }
            });
            // 刀四（老板 2026-11-25 九件套定稿）：冷启动同口径——上次看的台开机即预热（DNS 预解析+取票预取），
            // 用户回台起播少付一次取票时延；boot-warm-all（3 分钟全表备货）保留不动，本任务只焐热一台。
            // #15 每壳重跑注册/触发幂等（同名覆盖、TriggerNow 空操作）；预热失败静默（进台走既有起播链路兜底）
            _scheduler?.Register(new TaoLive.Scheduler.BackgroundScheduler.Job
            {
                Name = "boot-warm-last",
                Interval = TimeSpan.FromHours(1),
                OneShot = true,
                Tier = TaoLive.Scheduler.BackgroundScheduler.ScheduleTier.Standard,
                Work = async _ =>
                {
                    try
                    {
                        var lastId = _prefs.GetString("last_channel", null);
                        if (string.IsNullOrEmpty(lastId)) return;
                        var last = ChannelTable.Build().FirstOrDefault(c => string.Equals(c.Id, lastId, StringComparison.Ordinal));
                        if (last is null) return;
                        // DNS 预解析（首条流域名，IP 字面量免解析；失败静默——取流链路自带系统解析兜底），过车道让路播放
                        var stream = last.Sources.FirstOrDefault()?.Url ?? last.EntryUrl;
                        if (Uri.TryCreate(stream, UriKind.Absolute, out var u)
                            && u.HostNameType != UriHostNameType.IPv4 && u.HostNameType != UriHostNameType.IPv6
                            && u.Host.Length > 0)
                        {
                            await BackgroundLane.RunAsync(async () =>
                            {
                                try { await System.Net.Dns.GetHostAddressesAsync(u.Host).ConfigureAwait(false); }
                                catch { /* 预解析失败不挡预热 */ }
                            }).ConfigureAwait(false);
                        }
                        await new ChannelWarmer(resolver).WarmChannelAsync(last).ConfigureAwait(false); // 预取票（内部自过车道）
                    }
                    catch { /* 上次台预热失败不挡启动 */ }
                },
            });
            // 短延迟（2 秒级）再触发：预热与起播共车道，先让首帧/首批绿点落，预热随后补
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(BootWarmLastGraceMs).ConfigureAwait(false);
                    _scheduler?.TriggerNow("boot-warm-last");
                }
                catch { /* 触发失败不挡启动：壳重建重登再补 */ }
            });
        }

        // 开机预检握手（vc53 v1.0.8 一比一，常驻后台轮）：全台按序轻探——Web 取票口换票成功=服务器可达
        // （顺手焐热取票缓存，换台零等待）；直连源只取几 KB 头数据验证，不建视频传输。600ms/台限速
        // 防判异常流量；探测结果落健康账（持久化，跨启动可比）。开关关=循环同停，不留死旋钮。
        // #15：预检器随壳重建（重探一轮可接受），健康账落盘进程级共享，跨壳账不丢
        _precheck = new ChannelPrecheck(ProbeChannelAsync, 600);
        try { _precheck.AttachHealthStore(new SourceHealthStore(FilesDir!.AbsolutePath)); }
        catch { /* 账本初始化失败按账空跑，预检不挡启动 */ }
        // 实测账（vc85 蓝本 v2.1 健康账两本之二）：微卡上账/盲区标记/实测成功，管「好不好」；
        // 与探活账（管「通不通」）在排序里合成，管理器与会话记账共用同一实例
        _qualityBook = new QualityHealthStore();
        _manager.QualityBook = _qualityBook;
        // vc85 双向无缝切换四件套（蓝本 v2.5）：仲裁器三原语/偷拉管道/链路健康线/握手竞速，
        // 接上即软着陆+对齐回主源+逃生口生效；偷拉管道池粮做「主源有粮」实测凭据
        _manager.Arbiter = new SwitchArbiter();
        _manager.HealthLine = new LinkHealthLine();
        _manager.SegmentLoader = new HttpSegmentLoader();
        // 刀一（老板 2026-11-25 九件套定稿）：竞速与取票合成一次——Web 取票口端点由探针直接换票探通
        // （票落进程级 5 分钟缓存，赢家起播取票零等待），直连端点照旧握手赛马
        _manager.RaceProber = new TicketFusedProber(new HttpHandshakeProber(),
            (ch, ep) => resolver.ResolveAsync(ep, ch.Id, ch.Name));
        _manager.MainDispatcher = action => _main.Post(action);
        // #74 源队列健康排序（2026-11-15 老板令）+ 健康度排序升级（蓝本 v2.1）：合成两本账打分——
        // 探活账 Dead 沉底（原口径保留）；实测账微卡降权、盲区（天然贴边）降权、最近实测成功提权；
        // 全空账=保持既有序（本台优先、备援吃 FromRegistry 健康轻排序），不猜。稳定序兜底。
        _manager.QueueOrderer = (ch, endpoints) =>
        {
            bool ownDead = _precheck.SourceHealthOf(ch) is { State: ChannelHealth.Dead };
            var ownUrls = new HashSet<string>(ch.Sources.Select(s => s.Url), StringComparer.Ordinal);
            return endpoints
                .Select((e, i) => (e, i, own: ownUrls.Contains(e.Url) ? 1 : 0))
                .OrderBy(t =>
                {
                    long score = QualityHealthStore.SortScore(
                        _qualityBook.Peek(ch.EpgKey, ch.SourceId, t.e.Url), probeDead: false);
                    if (ownDead && t.own == 1) score += 10_000; // 本台源死账：整组沉底（原 #74 口径并入）
                    return score;
                })
                .ThenBy(t => t.i)    // 稳定序：同分保既有纪律序
                .Select(t => t.e);
        };
        if (_precheckEnabled) StartPrecheckLoop();
        _precheck.StateChanged += OnPrecheckHealthChanged; // ④列表健康点：读账刷新展示（5 秒节流）

        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        // vc113 第④单：允许绘制延伸进挖孔区（shortEdges）——提示胶囊要包住摄像头孔，不申请则孔区被系统留白、胶囊包不上
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            Window!.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        BuildRoot();
        SetContentView(_root);
        ApplyImmersive(); // 手机版整改令 2026-11-27：沉浸式全屏（收状态栏/导航栏）
        RegisterPredictiveBack(); // 2026-11-27 真机反馈第7条：接系统预测式返回（去「再次滑动返回」二次确认）
        // vc124 老板令（2026-11-28「进软件第一屏要么默认不开列表页」）：首屏直接进播放画面，
        // 开机链路照常自动进默认台；列表页只在用户主动呼出时出现
        ShowPlayer();
        MergeSubscriptionChannels(silent: true); // 启动并入落盘档案里的订阅新收录频道（_precheck/_list 此刻均已就绪）

        // #15 原地换皮：会话跨壳存活（换显示尺寸 Recreate 到此）——回播放页对准当前台即可，
        // 不重进不重取流（重进=「切尺寸重连」实锤复发）；进程首启无会话才走开机链路（#17：收藏第一个）
        if (_manager.Current is { } live)
        {
            var liveCh = live.CurrentChannel;
            int idx = liveCh is null ? -1 : IndexOfChannel(liveCh, _channels);
            _currentIndex = idx >= 0 ? idx : 0;
            ShowPlayer(); // 只重挂界面与新 Surface，播放不断流
        }
        else
        {
            _ = EnterByBootAsync();
        }
    }

    /// <summary>观看时长记账（刀三，账本口径=画面真实可见期间）：出画起算，同台续账不重开；
    /// 封账在换台/失败终态/退壳。纯本地零网络，账本自带节流落盘。</summary>
    private void WatchArm(Channel ch)
    {
        if (_watchEpgKey == ch.EpgKey && _watchSinceMs is not null) return; // 同台续账（重灌回 Playing 不重开）
        WatchSeal();
        _watchEpgKey = ch.EpgKey;
        _watchSinceMs = System.Environment.TickCount64;
    }

    /// <summary>封账：把当前观看段记进账本（非正段不入账，账本自拒脏数据）。</summary>
    private void WatchSeal()
    {
        if (_watchSinceMs is { } since && _watchEpgKey is { } epg)
        {
            var delta = System.Environment.TickCount64 - since;
            if (delta > 0) _watchLedger.Record(epg, delta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        _watchSinceMs = null;
        _watchEpgKey = null;
    }

    /// <summary>
    /// failover 转接器摘旧挂新防双收，事件体读本壳界面状态（遮罩/空态/健康账回写）。
    /// </summary>
    private void HandleSessionStateChanged(SessionStateEventArgs e)
    {
        // 观看时长记账（刀三）：前台会话出画=起算（首帧后真实观看口径），封账在换台/失败终态/退壳
        if (e.Session == _manager.Current && e.State == PlaybackState.Playing && e.Session.CurrentChannel is { } playingCh)
        {
            WatchArm(playingCh);
        }
        else if (e.Session == _manager.Current && e.State == PlaybackState.Failed)
        {
            WatchSeal(); // 失败终态：观看段封账（账本口径=真实观看时长，空态不计）；vc96 第 6 轮：限前台会话——热备/让位会话失败不封前台账（事件对全部会话派发，原口径热备一失败就把前台观看段封掉，且前台保持 Playing 不再触发 Playing 重挂账，后续观看时长整段丢失）
        }
        // 逐次换源打点：所有会话（含热备）均落运行日志（自诊断/崩溃日志统计口径）
        // #15：转接器进程级唯一（静态字段首次赋值后恒等），对每个会话 -=/+= 幂等——换壳不双收
        _failoverRelay ??= (s, e2) => Shell?.OnSessionFailover(s, e2);
        e.Session.Failover -= _failoverRelay;
        e.Session.Failover += _failoverRelay;
        // vc86 F7：慢连宽限窗亮「正在换信号」——宽限期如实告知，不让用户误判黑屏死机（仅前台会话亮）
        _slowGraceRelay ??= (s, _) => Shell?.OnSessionSlowGrace(s);
        e.Session.SlowGraceStarted -= _slowGraceRelay;
        e.Session.SlowGraceStarted += _slowGraceRelay;
        // vc89 灌速探针：弱线起播期后台焐热备线票（不切线不动画面，干库秒切零取票等待）
        _weakFillRelay ??= (s, ep) => Shell?.OnSessionWeakFill(s, ep);
        e.Session.WeakFillDetected -= _weakFillRelay;
        e.Session.WeakFillDetected += _weakFillRelay;
        if (e.Session != _manager.Current) return;
        if (e.State == PlaybackState.Failed)
        {
            // 失败打点接真实事件：全线路不可达落运行日志
            CrashNet.Record($"[ConnectFailure] {e.Session.CurrentChannel?.Name ?? "未知频道"} 全线路不可达");
            // 播放实测回写（真机反馈批#4 修 2026-11-15）：真实断流写健康账——
            // 此前 ReportSourceResult 生产零接线，播放正常的台健康账永远不涨（圆点全灰实锤之一）；
            // 时间戳=Unix 毫秒（真机日志镜修 2026-11-15：CheckedAtMs 契约为跨启动可比的 Unix 时基，
            // 误传单调钟会把账本时间戳写花，健康提示排序与落盘合并全错账）
            if (e.Session.CurrentChannel is { } failCh)
                _precheck.ReportSourceResult(failCh, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), playback: true);
            // 失败终态驻留空态（方案书 v77，白屏镜红②）：重连耗尽转空态文案，不再用瞬时 Toast；
            // 键位引导按树内真实键位：OK 重试（退避账管节奏）· 菜单键选台（呼列表）。
            // 真机反馈批#11 修 2026-11-15：失败终态此前只撤遮罩露旧台冻结帧（TextureView 停帧后
            // 保留最后渲染内容），「新台没切过来一直挂着上个台画面」实锤——终态改铺全屏不透明
            // 水墨深底盖旧画，空态文案叠其上（BuildRoot z 序：_emptyPill 在 _loadingPanel 之上）。
            // _loadingSeq 作废亮牌延迟（原 HideLoading 语义保留，只不清遮罩底）。
            _loadingSeq++;
            _veilChannelName = null; // #73 修：失败终态清台名——ShowSwitchVeil 不再带「正在连接」字样，空态文案接管
            ShowSwitchVeil();
            _veilAwaitingReveal = false; // 失败终态遮罩由空态文案管，不归帧节拍撤
            ShowEmptyState();
        }
        else if (e.State == PlaybackState.Buffering)
        {
            HideEmptyState();
            _lastVideoFrameAtMs = System.Environment.TickCount64; // #75：进过渡即续账，恢复 Playing 后给出帧宽限
            // vc66 修（老板真机反馈「画一直黑几秒后全线路不可达」）：此处不再清零重建计数——
            // 只出声守卫的同源重建走的就是 Buffering 过渡，清了账就永远到不了「2 次封顶切线路」，原地打转烧队列。
            // 计数只在用户真实换台（SwitchToCurrent）与帧恢复（守卫内）归零。
            // 断流自救（FailoverCount>0）零提示（方案书「热切断流自救全程零提示」，四镜·黑屏镜 11-15 修）：
            // 不铺遮罩不弹 OSD，画面保持最后一帧直到新流就绪；换台过渡（FailoverCount=0）才走 SwitchRevealPolicy。
            // 真机反馈批#2/#8 修 2026-11-15：铺遮罩=立即盖上不透明水墨深底（vc53 L588-605 口径），
            // 不再藏画面——SurfaceView/TextureView 让出即露根布局暖白底，就是「换台白屏」本身。
            if (e.Session.FailoverCount == 0 && SwitchRevealPolicy.ShouldVeilOnSwitch(_textureSurface is not null))
            {
                ShowSwitchVeil();
                ShowToast(TaoLive.Session.SwitchHud.Connecting);
            }
        }
        else if (e.State == PlaybackState.Playing)
        {
            HideEmptyState();
            // 真机反馈批#16 修 2026-11-15：此前此处 HideLoading()——Playing=内核 Ready 但首帧未真渲染，
            // 提示先于画面消失（老板令：文字要亮到真出画）。改由 VeilFrameTick 真出画一并撤字撤底。
            // 真机反馈批#11 续修 2026-11-15：Playing=内核 Ready（音视频齐），但新流首帧尚未真渲染，
            // 此刻撤遮罩会露旧台冻结帧几秒（老板真机实锤「台已切过来但画面一直挂着上个台」）。
            // 遮罩改挂待撤旗：本台第一帧真出画（OnFrameTick）才撤（WireVeilReveal）。
            // vc66 修（老板真机反馈「过一会音出来画还是黑」）：10 秒安全网掀盖取消——零帧掀盖就是露黑屏本身；
            // 改挂 30 秒「帧活兜底网」：只有帧账活着（帧节拍在跳）但揭幕判定失灵的极端边角才掀盖，
            // 真零帧期间遮罩保持 Loading，只出声守卫递进（重建→切线路→换源）直至真出画或失败终态接管。
            _veilAwaitingReveal = true;
            int revealSeq = _veilSeq;
            _main.PostDelayed(() =>
            {
                if (!_veilAwaitingReveal || revealSeq != _veilSeq) return;
                bool framesAlive = _lastVideoFrameAtMs != 0
                    && System.Environment.TickCount64 - _lastVideoFrameAtMs < 5_000;
                if (!framesAlive) return; // 真零帧：遮罩不掀，守卫递进中，失败终态/真出画接管
                _veilAwaitingReveal = false;
                HideSwitchVeil();
            }, 30_000);
            // 换源黑屏埋点（黑屏镜黄④ 11-15）：换源起点到出画恢复的时长落运行台账（含 ≤500ms 快切段）
            if (_failoverAtMs is { } from)
            {
                _failoverAtMs = null;
                CrashNet.Record($"[换源黑屏] 恢复出画 {System.Environment.TickCount64 - from}ms（{e.Session.CurrentChannel?.Name ?? ""}）");
            }
            // 断点台落盘（方案书 v24，黑屏镜黄⑤ 11-15）：每次换台成功即同步写档，恢复读最近成功台
            if (e.Session.CurrentChannel is { } playingChannel)
            {
                _prefs.Edit()!.PutString("last_channel", playingChannel.Id)!.Apply();
                // 播放实测回写（真机反馈批#4 修 2026-11-15）：首帧真出画=强证据直接点亮圆点——
                // 此前播放正常的台健康账零回写，圆点只能等预检慢慢滚（真机「看过的台也不绿」实锤）；
                // 时间戳=Unix 毫秒（真机日志镜修：时基契约同上）
                _precheck.ReportSourceResult(playingChannel, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), playback: true);
            }
            // 首帧撤遮罩由 HideSwitchVeil 承担（真机反馈批#2 修），载体不再显隐切换
        }
        else
        {
            HideEmptyState();
            // vc89 换信号提示（老板拍板「一次平稳」③）：干库进入重灌=画面必黑——如实告知「正在换信号」，
            // 不再纯黑十来秒。仅前台会话且存货见底时亮条（软着陆对齐切/热备攒流不掺；有存货的瞬时抖动
            // 画面未黑，不弹防打扰）。
            if (e.Session.IsForeground && e.Session.BufferedStockMs == 0)
            {
                ShowToast(TaoLive.Session.SwitchHud.Connecting);
            }
        }
    }

    /// <summary>帧节拍遮罩撤除（真机反馈批#11 续修；#15 起内核跨壳存活，帧节拍经 Shell 打到当前壳）：
    /// 只有「当前会话本台」的真渲染帧有资格撤——热备攒流/自救尾帧不掺；遮罩代数防快速连切串代。
    /// 真机反馈批#16：真出画耗时进进程级指数均值，下一次「约还需X秒出画」倒计时拿它当基准。</summary>
    private void VeilFrameTick(ExoPlayerPlayer player)
    {
        if (!_veilAwaitingReveal) return;
        if (!string.Equals(player.CurrentChannelKey, _manager.Current?.CurrentChannel?.Id, StringComparison.Ordinal)) return;
        _veilAwaitingReveal = false;
        var elapsedMs = System.Environment.TickCount64 - _switchAtMs;
        if (elapsedMs is > 200 and < 60_000)
            RevealEtaMs = RevealEtaMs * 0.7 + elapsedMs * 0.3; // 指数均值自校准（首启基线 3 秒，逐次逼近真值）
        int seq = _veilSeq;
        _main.Post(() => { if (seq == _veilSeq) HideSwitchVeil(); });
    }



    /// <summary>
    /// 开机链路（方案书收口批②⑤⑥）：等网就绪再进默认台——轻量 TCP 连通探测（非 Wi-Fi 状态位，防假在线），
    /// 初值 15 秒，超时照常进；预检先焐热取票缓存，默认台随后起播（顺序定死）；预热超时/失败照常进，起播走既有重试。
    /// </summary>
    private async Task EnterByBootAsync()
    {
        try
        {
            ShowToast("等待网络…");
            await NetReadiness.WaitAsync(report: m => CrashNet.Record(m)); // ≤15 秒，超时照常放行
            _main.Post(async () =>
            {
                try
                {
                    var idx = DefaultChannelIndex();
                    ShowToast($"正在打开 {_channels[idx].Name}");
                    try
                    {
                        // 默认台焐热：老板令 2026-11-15「每次开机后台自动优先预热默认频道」——
                        // 移出预检开关条件，每次启动链路无条件焐热（全量预热/邻台预取仍随预检开关同起同停）；
                        // 出画前提示即上方「正在打开 X」信息条，已有不必另造。
                        var warm = new ChannelWarmer(_resolver).WarmChannelAsync(_channels[idx]);
                        await Task.WhenAny(warm, Task.Delay(15_000)); // 预热超时照常进默认台
                    }
                    catch (Exception ex) { CrashNet.Record($"[Boot] 预热未完成照常进默认台：{ex.Message}"); }
                    if (_manager.Current is null) PlayAt(idx); // vc124 首屏改播放页：会话仍空才进默认台（用户已手动进台不抢）
                }
                catch (Exception ex) { CrashNet.Record($"[Boot] 开机链路异常照常进默认台：{ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            CrashNet.Record($"[Boot] 开机链路异常照常进默认台：{ex.Message}");
            _main.Post(() => { if (_manager.Current is null) PlayAt(DefaultChannelIndex()); });
        }
    }

    /// <summary>默认台来源（老板令 2026-11-15 #17）：开机进收藏第一个（按频道表顺序取最先收藏的台）；
    /// 无收藏=全表第一个。断点台口径废止。</summary>
    private int DefaultChannelIndex() => DefaultChannelIndex(_favorites);

    /// <summary>带收藏集合入参：供泵线程快照口径复用（主线程读者用工作集重载即可）。</summary>
    private int DefaultChannelIndex(HashSet<string> favorites)
    {
        for (int i = 0; i < _channels.Count; i++)
            if (favorites.Contains(_channels[i].Id)) return i;
        return 0; // 无收藏=列表首位
    }

    // 生命周期打点（埋点补充 2026-11-15）：电视 ROM 杀后台/回前台黑屏排查锚点——
    // 此前 OnResume/OnPause 完全无账，真机「按主页再回来黑了」查无第一手证据。
    protected override void OnResume()
    {
        base.OnResume();
        CrashNet.Record($"[生命周期] OnResume 回前台（{_manager?.Current?.CurrentChannel?.Name ?? "无会话"}）");
        PlaybackSession.BackgroundHold = false; // vc117 后台出声根治：回前台先撤冻结旗，自愈/换源恢复
        _isBackgrounded = false; // vc117：泵与静默重连同闸回前台撤
        StartKeepAlive(); // 老板令 #20：会话期长驻（起停相撞会触发系统违规闪退，v7 走查实锤），只在退出时撤
        // 切后台音画即停（2026-11-27 真机反馈第8条）：只恢复自己暂停的会话——用户手动暂停的不抢
        if (_pausedForBackground)
        {
            _pausedForBackground = false;
            try { _manager?.Current?.ResumePlayback(); CrashNet.Record("[生命周期] 回前台恢复播放（切后台曾暂停）"); }
            catch (Exception ex) { CrashNet.Record($"[生命周期] 回前台恢复失败：{ex.Message}"); }
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        CrashNet.Record($"[生命周期] OnPause 失去前台（{_manager?.Current?.CurrentChannel?.Name ?? "无会话"}）");
        PlaybackSession.BackgroundHold = true; // vc117 后台出声根治：后台冻结期自愈/换源不发起，回前台撤
        _isBackgrounded = true; // vc117：泵与静默重连同闸置位
        // 切后台音画即停（2026-11-27 真机反馈第8条）：静音冻结出声出画，内核照常灌水位，切回续播。
        // vc112 口径放宽：Buffering（攒流期）也冻结——防「返回=退后台」后门开在后台自动出声；回前台 OnResume 统一续播
        var cur = _manager?.Current;
        if (cur is not null && (cur.State == PlaybackState.Playing || cur.State == PlaybackState.Buffering))
        {
            try { cur.PausePlayback(); _pausedForBackground = true; CrashNet.Record("[生命周期] 切后台暂停音画（内核照常缓冲，vc112 含攒流期）"); }
            catch (Exception ex) { CrashNet.Record($"[生命周期] 切后台暂停失败：{ex.Message}"); }
        }
        StartKeepAlive(); // 老板令 #20：切后台保活（已在跑则幂等跳过）
    }

    private bool _pausedForBackground; // 切后台由本类暂停（区别于用户手动暂停），回前台只恢复这种
    private bool _isBackgrounded; // vc117 后台出声根治：后台冻结旗（OnPause 置/OnResume 撤），泵与静默重连同闸

    // —— 后台保活（老板令 2026-11-27 #20「切后台要保活+满水位+切回续播」）——————
    // 前台服务只负责持有进程优先级：播放内核与看门狗泵在 Activity 里照常运转，
    // 后台继续拉流灌水位；服务不碰播放器，切回时画面从水位接续点原样续播。
    // 长驻口径：播放期一直跑，只在 OnDestroy 撤——OnPause 起/OnResume 撤的短命模式
    // 存在起停竞态（StopService 先于服务报到 → ForegroundServiceDidNotStartInTime 闪退）。

    private bool _keepAliveOn; // 保活服务在跑（防重复起/撤）

    private void StartKeepAlive()
    {
        if (_keepAliveOn || _manager?.Current?.CurrentChannel is null) return; // 没在播不起
        var intent = new Intent(this, typeof(KeepAliveService));
        intent.PutExtra(KeepAliveService.ExtraChannel, _manager.Current.CurrentChannel!.Name);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent);
            else StartService(intent);
            _keepAliveOn = true;
            CrashNet.Record("[生命周期] 后台保活服务起（前台服务持有进程，水位照灌）");
        }
        catch (Exception ex)
        {
            _keepAliveOn = false;
            CrashNet.Record($"[生命周期] 后台保活服务起失败（不挡播放）：{ex.GetType().Name}");
        }
    }

    private void StopKeepAlive()
    {
        if (!_keepAliveOn) return;
        try { StopService(new Intent(this, typeof(KeepAliveService))); } catch { }
        _keepAliveOn = false;
        CrashNet.Record("[生命周期] 后台保活服务撤（回前台）");
    }

    /// <summary>
    /// 内存压力缩编钩子（老板令 #25：低配机不等系统来杀，先自瘦身）：系统内存告警
    /// （RunningLow 及更重）即清让位会话释放内核，当前台/热备不动——
    /// 旧台缓存主动让路，防低配档被 ROM 直接整进程杀掉（预防于被杀之前）。
    /// </summary>
    public override void OnTrimMemory([GeneratedEnum] Android.Content.TrimMemory trimLevel)
    {
        base.OnTrimMemory(trimLevel);
        if (trimLevel >= Android.Content.TrimMemory.RunningLow)
        {
            var shrunk = _manager?.ShrinkForMemoryPressure() ?? 0;
            if (shrunk > 0)
            {
                CrashNet.Record($"[生命周期] OnTrimMemory({trimLevel}) 内存告警，缩编清让位会话 {shrunk} 个");
            }
        }
    }

    protected override void OnDestroy()
    {
        // 生命周期收尾（闪退镜黄⑦⑧⑨ 11-15）：停看门狗泵、清数字提交回调、停预检循环
        StopKeepAlive(); // 老板令 #20：退出应用保活服务必撤，不留常驻通知
        WatchSeal(); // 观看时长记账（刀三）：退壳封账
        _watchLedger.FlushNow(); // 立落（退出关键点口径）：有脏必落盘，防进程先死文件没写完
        _watchdogStopped = true;
        _main.RemoveCallbacks(_digitCommit!);
        _digitCommit?.Dispose();
        _main.RemoveCallbacks(_emptyHintTick); // R1 审查红项修（闪退镜黄⑦同族）：Recreate 后旧壳退级提示节拍不得自续
        StopPrecheckLoop();
        StopVeilCountdown(); // #16：换壳必停倒计时（新壳起自己的）
        if (IsFinishing)
        {
            // 真机退场才让位会话（停内核解码）；#15 换尺寸 Recreate（IsFinishing=false）会话跨壳存活，绝不打断播放
            // vc68 退出令：全表清场——只让位 Current+Standby 时，vc67 下 Failed 漏停的孤儿内核
            // （会话表内其余会话）没人停，退软件后音频照放；渲染必须结束口径，退场一个内核不留。
            _manager.Shutdown();
            Shell = null;
        }
        base.OnDestroy();
    }

    // —— 视图（vc53 BuildUi 皮：视频垫底 + 半透明水墨抽屉 + 信息条 + 节目单 + 亮牌） ———

    /// <summary>vc116⑦ 统一靠边对齐边距（dp）：列表页头/水位行/筛选框统一 24dp 贴边（vc119 老板令：左右统一设置页口径），行内容经 ListView 16dp 内距对齐同一边线。</summary>
    private const int ListEdgePad = (int)UiScale.EdgePadDp;

    private void BuildRoot()
    {
        // 水墨主题底（#65 老板令 2026-11-15：改水墨深底 #2B333B，与窗底/切台遮罩同色——
        // 开机到首帧之间露底不再白屏，跟切台一样黑底+延迟亮牌 Loading，首帧出画自动撤；
        // 原「暖宣浅底不露黑」口径废止：真机白屏几秒实锤比黑更难看）
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.ParseColor("#2B333B"));

        _toastHide = new Java.Lang.Runnable(() =>
        {
            _toast.Visibility = ViewStates.Invisible;
            // vc124 老板令：提示胶囊收起后，被让位的空态胶囊原位回场（失败态仍在且无浮层在位）；
            // 浮层在位则记账交 UpdateEmptyPillSuppression 收浮层时回场，不在此硬亮
            if (_emptyPillDeferredByToast)
            {
                _emptyPillDeferredByToast = false;
                bool windowOpen = _listOpen || _settingsPanelOpen
                    || _diagOverlay is not null || _statsOverlay is not null
                    || _advOverlay is not null || _advGearOverlay is not null || _licenseOverlay is not null;
                if (windowOpen) _emptyPillSuppressed = true;
                else if (_manager.Current?.State == PlaybackState.Failed) _emptyPill.Visibility = ViewStates.Visible;
            }
        });

        // 长播无源退级提示（老板 2026-11-17 拍板）：空态满 5 分钟起每分钟如实报已等时长并建议
        // 检查网络；自愈慢轮+重探仍在后台自动复活，提示只是让等待有交代。可见性判定兼停表：
        // 离开空态（HideEmptyState 撤回调）或空态已被复用改写即自然停摆。
        _emptyHintTick = new Java.Lang.Runnable(() =>
        {
            if (_emptyPill.Visibility != ViewStates.Visible) return;
            var waitedMin = (int)((System.Environment.TickCount64 - _emptyStateAtMs) / 60_000);
            if (waitedMin >= 5)
                SetEmptyPillText($"已 {waitedMin} 分钟未恢复，建议检查网络");
            _main.PostDelayed(_emptyHintTick, 60_000);
        });

        // 1. 全屏视频层（vc53：垫底；抽屉/信息条/节目单全盖其上）
        // 载体改 TextureView（真机反馈批#6 修 2026-11-15）：SurfaceView 独立窗口层被列表盖住即整层
        // 不渲染（列表开着换台画面黑的根因，vc53 LiveTextureView 优先同口径）；Surface 经
        // SurfaceTexture 自建，挂面仍走会话契约 AttachSurface(IntPtr of Surface)。
        _playerPage = new FrameLayout(this);
        // 老板令 2026-11-27（截图令）：画面四边留边全黑——原透出水墨深底 #2B333B 发灰，
        // 观感像「没铺满」；播放页垫纯黑，与视频黑边融为一体（根布局水墨底只管开机露底，不动）
        _playerPage.SetBackgroundColor(Color.Black);
        _videoView = new TextureView(this);
        _playerPage.AddView(_videoView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        // 手机版整改令 2026-11-27：画面等比适配——视频区按 16:9 contain 居中（竖屏上下留边、横屏左右留边），
        // 根治「竖屏黑边巨大/横屏上下被裁」（电视端 MatchParent 直铺在手机屏上两头不讨好的根因）
        _videoView.LayoutChange += (_, _) => { ApplyVideoAspect(); PositionHud(); }; // vc120：画面区域变化即重算亮度条黑区落位
        // 走查令 2026-11-27（D4 根治）：转屏瞬间 OnConfigurationChanged 里播放页尺寸还是旧值，算出的竖屏口径被原样带回横屏
        //（v4 走查实锤：横屏视频带 1079×607 = 竖屏 16:9 尺寸）——挂播放页布局变化，尺寸真落地后再重算
        _playerPage.LayoutChange += (_, _) => ApplyVideoAspect();
        _videoView.SurfaceTextureListener = new TextureHook(this);

        // 1.5 起播亮牌（雷二·延迟亮）+ 换台遮罩（真机反馈批#2 修：底固定不透明，盖住 GPU 残留旧画面）：
        // 换台后 0.8 秒内没出首帧才亮字，亮了带台名，首帧落地才撤（vc53 LoadingPanel 同款水墨深底）
        _loadingPanel = new FrameLayout(this);
        _loadingPanel.SetBackgroundColor(InkTranslucency.SwitchVeilColor());
        _loadingText = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = UiScale.Title,
        };
        _loadingText.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_loadingText);
        _loadingPanel.AddView(_loadingText, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Center,
        });
        _loadingPanel.Visibility = ViewStates.Gone;
        _playerPage.AddView(_loadingPanel, new FrameLayout.LayoutParams(-1, -1));

        // 失败终态驻留空态（方案书 v77，白屏镜红②）：非瞬时，进失败态显示、离失败态即隐。
        // vc126 老板令（2026-11-28「他就不用胶囊了吧 它按照屏幕中间的样式去显示」）：胶囊皮退场，
        // 改屏幕正中纯文字样式（与「正在连接×××」加载文案同位同款）——状态行居中 + 可点链接行居中在下。
        _emptyPill = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _emptyPill.SetGravity(GravityFlags.Center);
        _emptyLeft = new TextView(this) { TextSize = UiScale.Title, Gravity = GravityFlags.Center }; // 与加载文案同字号
        _emptyLeft.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_emptyLeft);
        _emptyLeft.SetMaxLines(2); // 退级提示附加行进第二行，常态单行
        _emptyLeft.Ellipsize = TextUtils.TruncateAt.End;
        _emptyPill.AddView(_emptyLeft, new LinearLayout.LayoutParams(-2, -2));
        _emptyGap = new View(this); // vc126 居中样式无孔位空档，占位保留零尺寸（兼容既有引用）
        _emptyPill.AddView(_emptyGap, new LinearLayout.LayoutParams(0, 0));
        _emptyRight = new TextView(this) { TextSize = UiScale.Item, Gravity = GravityFlags.Center };
        _emptyRight.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_emptyRight);
        _emptyRight.SetSingleLine(true);
        _emptyRight.MovementMethod = Android.Text.Method.LinkMovementMethod.Instance; // 可点链接（立即重试/打开列表）
        _emptyRight.SetHighlightColor(Color.Transparent); // 链接按压不糊底（绑定 HighlightColor 只读，走 Setter）
        _emptyPill.AddView(_emptyRight, new LinearLayout.LayoutParams(-2, -2) { TopMargin = UiScale.Px(12) });
        _emptyBalL = new View(this); // 平衡占位退场，保留零尺寸字段兼容既有引用
        _emptyPill.AddView(_emptyBalL, 0, new LinearLayout.LayoutParams(0, 0));
        _emptyBalR = new View(this);
        _emptyPill.AddView(_emptyBalR, new LinearLayout.LayoutParams(0, 0));
        _emptyPill.Visibility = ViewStates.Invisible;
        _playerPage.AddView(_emptyPill, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center }); // 与加载文案同位：屏幕正中

        _root.AddView(_playerPage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 2. 切台信息条（vc53：顶部居中，自动隐藏）——vc113 第④单改胶囊：结构见 BuildToastPill，挂载移到列表页之上（最顶层）

        // 3. 底部节目单条已拆（2026-11-27 真机反馈第10条：手机端只留控制条节目名行，节目信息走 RefreshControlBarInfo）

        // 4. 频道抽屉（vc53：水墨面板盖画面，宽度随内容、左贴、上下全高）
        // 手机版整改令 2026-11-27：抽屉底改近实色水墨（原 PanelColor 透明度 76 在亮台标上叠字不可读）；
        // 抽屉/设置面板下加全屏遮罩层——点遮罩空白处即关（老板令 2026-11-27：所有浮层点外关闭）
        _scrim = new FrameLayout(this);
        _scrim.SetBackgroundColor(Color.Argb(96, 0, 0, 0));
        _scrim.Visibility = ViewStates.Gone;
        _scrim.Click += (_, _) =>
        {
            if (_settingsPanelOpen) CloseSettingsPanel();
            else if (_listOpen) ShowPlayer();
            UpdateScrim();
        };
        _root.AddView(_scrim, new FrameLayout.LayoutParams(-1, -1));

        _listPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listPage.SetBackgroundColor(InkTranslucency.PanelColor(this)); // vc111 老板令：全页面统一电视端水墨透光（原近实色 232 闷画面）
        _listPage.Visibility = ViewStates.Gone;
        _root.AddView(_listPage, new FrameLayout.LayoutParams(-1, -1)); // 宽度在 ApplyListWidth 钳制

        // 5. 提示胶囊（vc113 第④单，v28 老板令定稿：灵动岛式——胶囊包住摄像头孔、孔上无字、文字两侧；
        // 挂载在列表页之上=根布局最顶层，治「Toast 垫在列表页下透字叠字」根因）
        BuildToastPill();
        _root.AddView(_toast, new FrameLayout.LayoutParams(-2, -2)
        { Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal, TopMargin = UiScale.Px(24) });

        // 抽屉页头（vc113 原型对照令 02 节）：标题行=「频道列表」+⟳ 刷新+✕ 关闭（原型无台数，随原型口径）；
        // 页面顺序=标题行→页签→水位行→筛选框→列表（v31 原型 topbar→tabs→water-line→search-row）
        var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
        // 老板令 2026-11-17：列表整体上移让出台标——顶 padding 40→12，页头升到台标上方，
        // 台标横条不再与水位小字叠字；横向一个像素不动，宽度仍由 ApplyListWidth 钳制
        // vc116⑦ 统一靠边对齐：页头 padding 顶 20/左右 16/底 16（顶部加高+与水位行/筛选框同边线）
        header.SetPadding(UiScale.Px(ListEdgePad), UiScale.Px(20), UiScale.Px(ListEdgePad), UiScale.Px(16));
        var titleRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var title = new TextView(this) { Text = "频道列表", TextSize = UiScale.Title };
        title.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(title);
        titleRow.AddView(title);
        // 关闭 ×（老板 2026-11-27 20:2x 拍板「元素肯定都要」）：竖屏抽屉全屏后无面板外可点，
        // 页头右侧常驻 ✕ 关闭（与设置面板同款），返回键口径不变
        var titleStretch = new Space(this); // 标题与右侧钮之间弹性撑开，钮顶到右缘
        titleRow.AddView(titleStretch, new LinearLayout.LayoutParams(0, 1, 1f));
        // vc116⑧ 页头钮统一皮：⟳ 刷新/✕ 关闭改定尺寸线框钮——定尺寸、图标居中、OutlineBox 描边、
        // 垂直居中、间距 8dp（旧纯文字加 padding 撑热区退场）。
        // vc119 老板令（2026-11-28「改小不是改这么小」）：字形钮（⟳✕ 字符跟字体度量，真机缩成一点沉底）
        // 整体退场，改 BarIconView 几何自绘（Refresh/Close），与控制条图标同风格同盒径 44dp，字形病根根除。
        Android.Graphics.Drawables.Drawable HeaderBtnBg() =>
            InkTranslucency.OutlineBox(this, 10, InkTranslucency.FocusStrokeAlpha);
        var listRefresh = new BarIconView(this) { Kind = BarIconView.IconKind.Refresh, Clickable = true, BoxTextSize = UiScale.Title }; // vc120 肉眼对齐令：跟「频道列表」标题渲染高
        listRefresh.Background = new Android.Graphics.Drawables.InsetDrawable(HeaderBtnBg(), UiScale.Px(UiScale.BtnTouchPadDp)); // vc120：背景内缩对齐可见盒
        listRefresh.Click += (_, _) => RefreshChannelList();
        titleRow.AddView(listRefresh, new LinearLayout.LayoutParams(-2, -2)
        { Gravity = GravityFlags.CenterVertical, LeftMargin = UiScale.Px(UiScale.BtnGapDp) });
        var drawerClose = new BarIconView(this) { Kind = BarIconView.IconKind.Close, Clickable = true, BoxTextSize = UiScale.Title }; // vc120：同上
        drawerClose.Background = new Android.Graphics.Drawables.InsetDrawable(HeaderBtnBg(), UiScale.Px(UiScale.BtnTouchPadDp));
        drawerClose.Click += (_, _) => ShowPlayer();
        titleRow.AddView(drawerClose, new LinearLayout.LayoutParams(-2, -2)
        { Gravity = GravityFlags.CenterVertical, LeftMargin = UiScale.Px(UiScale.BtnGapDp) });
        header.AddView(titleRow);
        _listPage.AddView(header);

        // 五分类标签行（vc53：页签内容自适应宽度，首页签 LeftMargin=0 与页头文字严格对齐；
        // 当前标签朱砂底高亮，点击/左右键切换）
        // vc111 老板令（系统显示大小三档都要一页放全）：页签行宽随屏等比（字号与间距同比例），
        // 原实需≈116% 屏宽三档全裁「地方台」——收边距到≈93%屏宽，再布局后实测兜底（FitTabsOnePage）
        _tabViews.Clear();
        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabRow.SetPadding(UiScale.Px(ListEdgePad), UiScale.Px(24), UiScale.Px(ListEdgePad), UiScale.Px(12)); // #15 尺寸令；vc119 左右统一 24dp 边线
        int tabIndex = 0;
        foreach (ListTabs.Tab t in Enum.GetValues(typeof(ListTabs.Tab)))
        {
            var tv = new TextView(this) { Text = ListTabs.Label(t), TextSize = UiScale.Item };
            tv.SetSingleLine(true); // vc65 老板令：页签永不换行（「地方台」不再被挤折行），面板下限已并入页签行最小宽
            tv.SetPadding(UiScale.Px(10), UiScale.Px(10), UiScale.Px(10), UiScale.Px(10)); // vc111 左右 12→10
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.Click += (_, _) => SelectTab(t);
            tabRow.AddView(tv, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = tabIndex == 0 ? 0 : UiScale.Px(6) }); // vc111 8→6
            _tabViews.Add(tv);
            tabIndex++;
        }
        StyleTabs();
        _listPage.AddView(tabRow);
        tabRow.Post(() => FitTabsOnePage(tabRow)); // vc111：布局后实测，超屏自动再收（兜系统字体放大等 ROM 差异）

        // 老板令 2026-11-16：页头挂「实时水位」——内核当前攒着还没播的库存（BufferedMs 实时读数），
        // 1 秒独立泵跟刷，抽屉开着数字一直新鲜；vc85 施工包5 同行并入「画面时刻」（现在−水位换算，源头延迟可标定）；
        // vc113 原型对照令：水位行在页签行之后、筛选框之前（v31 原型 topbar→tabs→water-line→search-row）
        _waterLevelText = new TextView(this) { TextSize = UiScale.Caption };
        _waterLevelText.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_waterLevelText);
        _waterLevelText.SetSingleLine(true); // 走查令 2026-11-27：水位行不竖排换行（宽度由 ApplyListWidth 页头项兜底，极端钳制走省略号）
        _waterLevelText.Ellipsize = TextUtils.TruncateAt.End;
        UpdateWaterLevel();
        _listPage.AddView(_waterLevelText, new LinearLayout.LayoutParams(-1, -2)
        { LeftMargin = UiScale.Px(ListEdgePad), RightMargin = UiScale.Px(ListEdgePad) }); // vc116⑦ 统一靠边

        // 筛选输入框（老板 2026-11-27 20:2x 拍板「元素肯定都要」；v11 设计稿口径）：数字=频道号前缀、文字=台名包含，
        // 系统输入法；空串恢复全量。过滤只动行装配，不碰频道表与编号语义
        var filterBox = new EditText(this)
        {
            Hint = "筛选：数字=频道号 · 文字=台名",
            TextSize = UiScale.Caption,
        };
        filterBox.SetSingleLine(true);
        filterBox.SetTextColor(InkTranslucency.TextPrimary(this));
        filterBox.SetHintTextColor(Color.ParseColor("#8894A0"));
        filterBox.SetBackgroundResource(0); // 去系统下划线底
        filterBox.Background = InkTranslucency.OutlineBox(this, 10, InkTranslucency.FocusStrokeAlpha); // vc116⑨：全透明+圆角线框（界面元素统一皮）
        filterBox.SetPadding(UiScale.Px(8), UiScale.Px(12), UiScale.Px(8), UiScale.Px(12)); // vc117 令6：内距 8dp 呼吸边（框边 24dp 与全页边线对齐，vc119）
        filterBox.Gravity = GravityFlags.CenterVertical; // 加高后文字垂直居中
        // vc120 老板令（2026-11-28 真机截图拍板「高度对齐+间距放宽」）：筛选框最低高度=列表行高——
        // 行高=上下 12dp 内距+台名行（Item）+副标题行（Caption），同字体度量实测取线高，档位/字体缩放自动跟
        int LineHeight(float size)
        {
            var tv = new TextView(this) { TextSize = size, Text = "测" };
            tv.SetSingleLine(true);
            tv.Measure(Android.Views.View.MeasureSpec.MakeMeasureSpec(0, Android.Views.MeasureSpecMode.Unspecified), Android.Views.View.MeasureSpec.MakeMeasureSpec(0, Android.Views.MeasureSpecMode.Unspecified));
            return tv.MeasuredHeight;
        }
        filterBox.SetMinHeight(UiScale.Px(12) * 2 + LineHeight(UiScale.Item) + LineHeight(UiScale.Caption));
        filterBox.TextChanged += (_, e) =>
        {
            if (_list?.Adapter is ChannelListAdapter adapter)
                adapter.SetFilter(e.Text?.ToString() ?? "");
        };
        _listPage.AddView(filterBox, new LinearLayout.LayoutParams(-1, -2)
        { LeftMargin = UiScale.Px(ListEdgePad), RightMargin = UiScale.Px(ListEdgePad), TopMargin = UiScale.Px(12), BottomMargin = UiScale.Px(12) }); // vc120 老板令：上下间距 4/8→12/12 放宽（左右仍 24dp 统一边线）

        // v23 第 1 条：折叠总开关行整行退场——标签行下直接铺频道（vc53 口径）。
        _list = new ListView(this) { Divider = null };
        // vc124 老板令（2026-11-28 真机「滚动条压住小圆点」）：列表滚动条贴右缘画，正好横穿行尾状态点列——
        // 滚动条整体退场（拇指透明），行尾圆点不再被压；滚动位置反馈由行内容自身可见性兜底
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            _list.VerticalScrollbarThumbDrawable = new Android.Graphics.Drawables.ColorDrawable(Color.Transparent);
        // vc119 老板令：列表左右 16dp 内距+不裁内距——选中态描边不被屏缘裁，行内容（+行内 8dp）对齐 24dp 统一边线
        // vc121 同类排查：底部内距=圆角让位——滚到底最后一行能滚出系统圆角弧区，不贴底被削
        _list.SetPadding(UiScale.Px(16), 0, UiScale.Px(16), BottomCornerClearPx());
        _list.SetClipToPadding(false);
        // 全局透明化（v1.0.8 第15项）：焦点效果=低透明度细边框+极淡底色，不复用实色高亮
        _list.Selector = InkTranslucency.FocusDrawable();
        _list.Adapter = new ChannelListAdapter(this, _channels, _favorites, ch => _precheck.MarkOf(ch), () => _channels[_currentIndex].Id, favoriteOrder: _favStore.Numbers);
        _list.ItemClick += (_, e) =>
        {
            // 行→频道→列表索引（收藏组行也指向同一频道，编号直达语义不变）
            var channel = ((ChannelListAdapter)_list.Adapter!).ChannelAt(e.Position);
            if (channel is null) return;
            // 连不上的台只能看不能切（v1.0.8，用户拍板）：确认键不发起播放
            if (_precheck.MarkOf(channel) == ChannelHealth.Dead)
            {
                ShowToast($"{channel.Name}  暂无信号");
                return;
            }
            PlayAt(_channels.ToList().FindIndex(c => c.Id == channel.Id));
        };
        // vc124 老板令（2026-11-28「左右滑动不能切换分类吗」）：列表横滑切页签——横向位移累计过
        // 60dp 且横向主导即切上一/下一页签（循环）；触摸监听只旁路手势识别不拦截事件，
        // 行点击、纵向滚动、筛选框输入一概不受影响
        var tabSwipe = new GestureDetector(this, new TabSwipeGesture(this));
        _list.SetOnTouchListener(new TabSwipeTouchListener(tabSwipe));
        _listPage.AddView(_list, new LinearLayout.LayoutParams(-1, 0, 1f));

        // 手机版触屏层（界面岗 2026-11-27）：手势+控制条+方向锁，遥控键位保留兼容
        BuildTouchLayer();
        ApplyOrientationLock(); // 方向锁启动装载（锁=钉当前方向，解锁=随传感器）
    }

    /// <summary>频道抽屉呼出（vc53 口径）：半透明面板盖画面，播放不中断（不摘输出面，画面在面板下继续）。</summary>
    private void ShowList()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧（抽屉与设置/弹窗/子页面板互斥）
        _listOpen = true;
        BackgroundLane.PlayingGapOverrideMs = 5000; // 老板令 2026-11-16 让路间隔：抽屉开着正盯绿点，压到 5 秒快变绿
        BackgroundLane.SetDrawerOpen(true); // vc86 放行口径边界③：抽屉开着单开一档，不受水位稳全停（仍受首帧门控+5 秒间隔）
        _playerPage.Visibility = ViewStates.Visible;
        _listPage.Visibility = ViewStates.Visible;
        _onPlayerPage = false;
        UpdateScrim(); // 手机版整改令：抽屉开着亮遮罩（点外关闭）
        ApplyListTopInset(); // 2026-11-27 真机反馈：列表页文字避开挖孔/状态栏（顶部安全区留白）
        UpdateWaterLevel(); // 老板令 2026-11-16：呼出即刷实时水位，不等下一拍
        if (_list.Adapter is ChannelListAdapter adapter)
        {
            adapter.Refresh(_favorites, _tab);
            ApplyListWidth(_channels); // vc65：恒定估宽（全表+页签行兜底），打开宽度不再随页签/收藏漂移
        }
        // 焦点默认落当前台行（方案书收口批②）：呼出即定位到正在看的台
        var current = _channels[_currentIndex];
        var pos = (_list.Adapter as ChannelListAdapter)?.PositionOf(current.Id) ?? -1;
        if (pos >= 0) _list.SetSelection(pos);
        _list.RequestFocus();
    }

    /// <summary>列表页头 ⟳ 刷新（vc113 对照原型 02 节）：重拉订阅、重绘列表、刷新当前台节目单。</summary>
    private void RefreshChannelList()
    {
        if (_subEnabled) KickSubscriptionRefresh(startup: false);
        (_list.Adapter as ChannelListAdapter)?.Refresh(_favorites, _tab);
        if (_channels.Count > 0 && _currentIndex >= 0 && _currentIndex < _channels.Count) RefreshEpg(_channels[_currentIndex]);
        ShowToast("已刷新");
    }

    /// <summary>抽屉收起回播放（vc53 口径）：画面本就没停，撤面板+亮回节目单条即可。</summary>
    private void ShowPlayer()
    {
        _listOpen = false;
        BackgroundLane.PlayingGapOverrideMs = 0; // 抽屉收起回默认 10 秒：没人盯绿点，画面平稳优先
        BackgroundLane.SetDrawerOpen(false); // vc86：抽屉档随收起撤销，水位稳闸恢复全口径
        _listPage.Visibility = ViewStates.Gone;
        _playerPage.Visibility = ViewStates.Visible;
        _onPlayerPage = true;
        UpdateScrim(); // 手机版整改令：抽屉收起撤遮罩
        EnsureWatchdog();
        EnsureWaterPump(); // vc85 水位跟手令：1 秒独立秒泵（抽屉开着才刷数字）
        // 播放页已就绪而 Surface 就绪（返回再进场景）：补挂输出面
        if (_textureSurface is { } surface)
            _manager.Current?.AttachSurface(surface.Handle);
    }

    /// <summary>vc112 口径：列表开着=先收抽屉回画面（不强制起播，当前台死流才补播，绝不让用户
    /// 关掉列表面对黑屏）；收着=停播攒流退后台（退出确认弹窗已整删，回前台自动续播）。</summary>
    private void ExitOrCollapseList()
    {
        if (_listOpen)
        {
            var alive = _manager.Current is { State: PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Reconnecting };
            ShowPlayer();
            if (!alive) PlayAt(_currentIndex);
            return;
        }
        // vc112 老板拍板：返回=停播攒流退后台（询问框去除）——OnPause 统一冻结音画、内核照常灌水位，回前台即播
        ShowToast("已退后台，回来自动续播");
        MoveTaskToBack(true);
    }

    /// <summary>vc53 口径：播放页左右键=列表翻页/呼出——列表没开就呼出，开了整页翻，长按连滚。</summary>
    private void ListNav(int dir)
    {
        if (!_listOpen) { ShowList(); return; }
        if (dir == 0) return;
        int first = _list.FirstVisiblePosition;
        int visible = Math.Max(1, _list.LastVisiblePosition - first + 1);
        int target = Math.Max(0, Math.Min(_list.Count - 1, first + dir * visible));
        _list.SmoothScrollToPosition(target);
    }

    // —— 播放接线 ————————————————————————————————————————————

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        _currentIndex = index;
        SwitchToCurrent();
        ShowPlayer();
    }

    /// <summary>预检轻探（vc53 口径）：Web 取票口换票成功即服务器可达（结果落 5 分钟缓存）；直连源只取头数据验证，不建视频传输。
    /// 探测结果回灌断网监测（方案书 v82，白屏镜红①）：成功=网络可达；全源失败=一次失败回灌；
    /// 恢复沿触发当前台静默自动重连。</summary>
    private async Task<bool> ProbeChannelAsync(Channel ch)
    {
        var ok = await ProbeChannelCoreAsync(ch).ConfigureAwait(false);
        if (ok)
        {
            if (_netGuard.OnSuccess()) HandleNetRecovered();
        }
        else
        {
            _netGuard.OnFailure();
        }
        return ok;
    }

    private async Task<bool> ProbeChannelCoreAsync(Channel ch)
    {
        // 热备保温（老板 2026-11-25 九件套定稿·Top4-B）：探活首通后不急着收工，把同台备援取票口的票
        // 顺手焐热（ResolveAsync 自落 5 分钟缓存）——真断流切备援时票已在手，省一次完整换票来回。
        // 封顶 2 张备援票：够断流切换零等待，不把探活轮变成取票风暴；缓存命中轮零网络（5 分钟内复用）。
        var ok = false;
        var warmed = 0;
        foreach (var ep in ch.Sources)
        {
            if (!ep.IsValid) continue;
            try
            {
                if (ep.Kind == SourceKind.Web)
                {
                    var url = await _resolver.ResolveAsync(ep, ch.Id, ch.Name).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(url))
                    {
                        if (!ok) { ok = true; continue; } // 首通：继续走剩余备援焐票
                        if (++warmed >= 2) break;         // 备援票封顶，焐够即收
                        continue;
                    }
                    // 探活失败明细（埋点补充 2026-11-15）：此前单源失败静默落下一源，全断才判死——补失败源与原因明细
                    if (!ok) CrashNet.Record($"[探活] {ch.Name} 取票口失败（{HostOf(ep)}，返回空）");
                }
                else
                {
                    if (ok) break; // 已有通路：直连无票可焐，不再多连（保温只对取票口端点）
                    using var req = new HttpRequestMessage(HttpMethod.Get, ep.Url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 8191);
                    using var res = await PrecheckHttp.SendAsync(req).ConfigureAwait(false);
                    if ((int)res.StatusCode < 400)
                    {
                        // vc86 老板令·广告源剔除（央六慢连跳广告源实锤）：内容校验——读分片首字节，
                        // 首个非空白字节是 '<' 即 HTML 广告壳（真源是 #EXTM3U/0x47 二进制），判死落下一源
                        var head = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        byte first = 0;
                        foreach (var b in head)
                        {
                            if (!char.IsWhiteSpace((char)b)) { first = b; break; }
                        }
                        if (first == (byte)'<')
                        {
                            CrashNet.Record($"[探活] {ch.Name} 直连探活命中 HTML 广告壳（{HostOf(ep)}），判死");
                        }
                        else
                        {
                            ok = true; // 首通：继续走剩余取票口备援焐票
                            continue;
                        }
                    }
                    CrashNet.Record($"[探活] {ch.Name} 直连探活 HTTP {(int)res.StatusCode}（{HostOf(ep)}）");
                }
            }
            catch (Exception ex)
            {
                if (!ok) CrashNet.Record($"[探活] {ch.Name} 源探活异常（{HostOf(ep)}）：{ex.Message}"); // 单源失败落下一源；全断才判死
            }
        }
        return ok;
    }

    /// <summary>台账端点主机（埋点补充 2026-11-15）：URL 原文太长不进台账，只记主机；解析失败记原串截尾。</summary>
    private static string HostOf(SourceEndpoint ep)
    {
        try { return new Uri(ep.Url, UriKind.Absolute).Host; }
        catch { return ep.Url.Length > 48 ? ep.Url[..48] : ep.Url; }
    }

    /// <summary>断网恢复沿（方案书 v82）：网络恢复自动重连失败终态的当前台，静默自愈不打断用户操作；
    /// 退避账窗内不抢跑（既有重试节奏不动），回主线程泵再动会话。</summary>
    private void HandleNetRecovered()
    {
        _main.Post(() =>
        {
            // vc117 后台出声根治令：后台冻结期静默重连不发起（重连=重播内核=后台出声后门），回前台再续
            if (_isBackgrounded) return;
            var cur = _manager.Current;
            if (cur is null || cur.State != PlaybackState.Failed) return;
            if (!_manager.TryManualRetry(cur.CurrentChannel?.Id ?? string.Empty, out _)) return;
            PlayAt(_currentIndex); // 静默重连：无 Toast，起播走既有状态机
        });
    }

    /// <summary>常驻循环预检（vc53 口径）：保态复测列表不闪烁；轮间歇 20 秒；单轮失败下一轮再来。
    /// vc82 收编③：自跑 while 循环退役，登记进统一调度器（慢探档、20 秒一轮、轮次串行）——
    /// 探活仍逐台自过全局单车道（ChannelPrecheck 内部口径不变），取消账（CTS）保留：停预检即在途轮收场。</summary>
    private void StartPrecheckLoop()
    {
        StopPrecheckLoop();
        _precheckCts = new System.Threading.CancellationTokenSource();
        var ct = _precheckCts.Token;
        _scheduler?.Register(new TaoLive.Scheduler.BackgroundScheduler.Job
        {
            Name = "precheck-round",
            Interval = TimeSpan.FromSeconds(20),
            Tier = TaoLive.Scheduler.BackgroundScheduler.ScheduleTier.Scout,
            Work = async _ =>
            {
                try { await _precheck.RunAsync(PrecheckQueue(), ct, keepKnownState: true).ConfigureAwait(false); }
                catch (System.OperationCanceledException) { /* 停预检：在途轮收场 */ }
                catch { /* 单轮失败下轮再来 */ }
            },
        });
    }

    /// <summary>预检轮次序（老板批 2026-11-16 第③条：体检顺序重排）——默认台与收藏台排最前、其余原序垫后：
    /// 装上后第一批绿点先落在用户马上要看的台上，不再陪全表慢慢排队。同台变体由 RunAsync 按首现位置归组，
    /// 健康账按 SourceId:EpgKey 记账，归并重复变体不重复计账。
    /// 收藏走快照 _favoritesView（vc84 首轮交叉审查修）：本方法跑在调度泵线程，
    /// HashSet 工作集只许主线程摸——这里只读快照，不碰工作集。</summary>
    private List<Channel> PrecheckQueue()
    {
        var rest = new List<Channel>(_channels.Count);
        var favorites = new List<Channel>();
        Channel? def = null;
        var fav = _favoritesView; // 取一次快照引用，整轮口径一致
        int defIdx = DefaultChannelIndex(fav);
        for (int i = 0; i < _channels.Count; i++)
        {
            var ch = _channels[i];
            if (i == defIdx) def = ch; // 默认台（收藏第一个/列表首位）置顶
            else if (fav.Contains(ch.Id)) favorites.Add(ch); // 收藏台次之
            else rest.Add(ch);
        }
        var ordered = new List<Channel>(_channels.Count);
        if (def is { } d) ordered.Add(d);
        ordered.AddRange(favorites);
        ordered.AddRange(rest);
        return ordered;
    }

    private void StopPrecheckLoop()
    {
        _precheckCts?.Cancel();
        _precheckCts = null;
        _scheduler?.Unregister("precheck-round"); // 收编③：登记路一并退役（调度器跑完当前轮自然收口）
    }

    /// <summary>备援候选健康提示（收口批①轻排序只读账，不写账）：
    /// 候选 URL 对上台目录内来源变体（同址匹配），取健康账结论——活源带核验时间（账新者在前，越旧权重越低）；
    /// 无账/未探返回 null（保持注册表纪律序）。只动同台内排序，不改台间顺序（v28 条款）。</summary>
    private FallbackQueueSource.FallbackHealthHint? FallbackHealthHintOf(SourceEndpoint endpoint)
    {
        long bestAt = 0;
        var best = ChannelHealth.Unknown;
        foreach (var ch in _channels)
        {
            if (ch.Unavailable) continue;
            var hasUrl = false;
            foreach (var s in ch.Sources)
            {
                if (string.Equals(s.Url, endpoint.Url, StringComparison.Ordinal)) { hasUrl = true; break; }
            }
            if (!hasUrl) continue;
            var entry = _precheck.SourceHealthOf(ch);
            if (entry is null) continue;
            if (entry.CheckedAtMs > bestAt) { bestAt = entry.CheckedAtMs; best = entry.State; }
        }
        return best == ChannelHealth.Unknown ? null : new FallbackQueueSource.FallbackHealthHint(best == ChannelHealth.Alive, bestAt);
    }

    private void SwitchToCurrent()
    {
        var ch = _channels[_currentIndex];
        WatchSeal(); // 观看时长记账（刀三）：换台即封上一段账（账本口径=真实观看时长）
        _lastVideoFrameAtMs = System.Environment.TickCount64; // #75：换台即续账，防旧台陈帧误触只出声兜底
        _stallRebuildCount = 0;
        // 频道上下文由内核 Play 自取（每会话独享内核，双实例不共用上下文）
        if (_manager.Current is { } cur && cur.CurrentChannel?.Id == ch.Id)
        {
            cur.ChangeChannel(ch); // 同频道原地重起播（切回口径）
        }
        else
        {
            _manager.SwitchTo(ch);
        }
        ShowLoading(ch);    // 亮牌（雷二·延迟亮）：0.8 秒没出首帧才亮，首帧落地即撤
        ShowToast(ch.Name); // 信息条：台名顶条闪现（vc53 口径；节目单走底部节目条）
        RefreshEpg(ch);     // 底部节目单：换台先清场再拉，有数据才亮
    }

    /// <summary>亮牌（雷二·延迟亮）：换台后 0.8 秒内没出首帧才亮，明确反馈「没死机」；
    /// 真机反馈批#16：文字亮到本台第一帧真出画才撤，期间带「约还需X秒出画」倒计时。</summary>
    private void ShowLoading(Channel ch)
    {
        int seq = ++_loadingSeq;
        _veilChannelName = ch.Name;                       // #16：倒计时文案台名
        _switchAtMs = System.Environment.TickCount64;     // #16：出画耗时账起点（进指数均值）
        _loadingText.Text = $"正在连接 {ch.Name}";
        _main.PostDelayed(() =>
        {
            if (seq != _loadingSeq) return; // 已换台/已出画：旧牌作废
            if (_manager.Current is { } s && s.State is PlaybackState.Buffering or PlaybackState.Idle or PlaybackState.Reconnecting)
            {
                ShowSwitchVeil(); // 文字带底亮（真机反馈批#2 修：遮罩底即时不透明水墨深底）
                _loadingText.Visibility = ViewStates.Visible;
                StartVeilCountdown(ch.Name); // #16：倒计时起跑，每秒刷新到真出画
            }
        }, 800);
    }

    /// <summary>换台遮罩（vc53 铺干净底色口径，真机反馈批#2/#8 修）：换台过渡即铺不透明水墨深底盖旧画面，
    /// 新流首帧到齐才撤。#73 Loading 全程在场修（2026-11-15 老板令）：内核反复上报缓冲会多次走到这里——
    /// 此前每次重铺都把字样藏掉、停掉倒计时，老板实锤「全程全黑屏没有 Loading」。改：字样跟遮罩同寿命，
    /// 有台名即亮字并重启倒计时（亮到真出画一并撤）；失败终态由调用方先清台名（空态接管）。</summary>
    private void ShowSwitchVeil()
    {
        _veilSeq++; // 遮罩换代：旧代迟到的撤牌/安全网全部作废（快速连切防串）
        StopVeilCountdown();
        if (_veilChannelName is { } name)
        {
            _loadingText.Text = $"正在连接 {name}";
            _loadingText.Visibility = ViewStates.Visible;
            StartVeilCountdown(name);
        }
        else
        {
            _loadingText.Visibility = ViewStates.Gone;
        }
        _loadingPanel.Visibility = ViewStates.Visible;
    }

    private void HideSwitchVeil()
    {
        StopVeilCountdown(); // #16：出画/兜底即停倒计时（真出画=VeilFrameTick 走这里，字与底一并撤）
        _loadingPanel.Visibility = ViewStates.Gone;
    }

    /// <summary>出画倒计时（真机反馈批#16）：亮牌文字不因内核 Ready 提前消失，亮到本台第一帧真出画；
    /// 「约还需X秒」按进程级出画耗时指数均值倒着算，每秒刷新，下限 1 秒——不吹精确，给得住预期。</summary>
    private void StartVeilCountdown(string channelName)
    {
        StopVeilCountdown();
        var runnable = new Java.Lang.Runnable(() =>
        {
            if (_loadingPanel.Visibility != ViewStates.Visible) return; // 遮罩已撤：倒计时随亡
            if (_veilAwaitingReveal)
            {
                // #73 修：等待出画才刷倒计时；未到 Playing 前只续拍（字样保持「正在连接 X」不闪断）
                var remainMs = RevealEtaMs - (System.Environment.TickCount64 - _switchAtMs);
                int remain = Math.Max(1, (int)Math.Round(remainMs / 1000.0));
                _loadingText.Text = $"正在连接 {channelName}\n约还需 {remain} 秒出画";
            }
            _main.PostDelayed(_veilCountdown!, 1000);
        });
        _veilCountdown = runnable;
        _main.PostDelayed(runnable, 1000);
    }

    private void StopVeilCountdown()
    {
        if (_veilCountdown is null) return;
        _main.RemoveCallbacks(_veilCountdown); // 先摘再释放（闪退镜黄⑨同款）：窗口内换台不提前触发
        _veilCountdown.Dispose();
        _veilCountdown = null;
    }

    // —— 节目信息（2026-11-27 真机反馈第10条：底部节目单条已拆，只留控制条节目名行）———

    /// <summary>换台即刷新：后台拉官方节目单，只喂控制条节目名行（RefreshControlBarInfo）；
    /// 失败/映射外/无数据静默降级为只显画面时刻（vc53 RefreshEpg 同口径）。</summary>
    private void RefreshEpg(Channel ch)
    {
        int seq = ++_epgSeq;
        _epgNowTitle = null;
        _epgNextText = null;
        RefreshControlBarInfo();
        _ = Task.Run(() =>
        {
            (CntvEpgClient.Entry Now, List<CntvEpgClient.Entry> Next)? result = null;
            try { result = _epg.Fetch(ch.EpgKey); }
            catch { /* 官方接口失败按无数据，不硬造 */ }
            _main.Post(() =>
            {
                if (seq != _epgSeq) return; // 换台后旧节目单作废
                _epgNowTitle = result?.Now.Title;
                // vc113 对照原型（v24–v26 节目单口径）：接下来两条「HH:mm 节目名」，缺表降级不显
                // vc129 老板令（真机「换行断点切进节目名，生命树第8集被拆生/命树」实锤）：条目间改
                // 显式换行——每条节目独占一行，换行只落在条目边界，不再靠自然换行拆开节目名
                _epgNextText = result is { } r && r.Next.Count > 0
                    ? string.Join("\n", r.Next.Take(2).Select(e => $"{e.Start:HH:mm} {e.Title}"))
                    : null;
                RefreshControlBarInfo();
            });
        });
    }

    private void AttachSurfaceIfShown()
    {
        if (_onPlayerPage && _textureSurface is { } surface)
            _manager.Current?.AttachSurface(surface.Handle);
    }

    // —— 视频载体挂面（TextureView，真机反馈批#6 修） ————————————————

    // —— 手机版整改批（2026-11-27 真机令）：沉浸式/遮罩/画面等比 ————————————
    // 老板真机四图+口头反馈 19 条问题清单（projects/1967083/mobile-build/build/真机问题清单-手机版v1.md）。

    /// <summary>沉浸式全屏：收状态栏/导航栏，手势呼出（API 30+ InsetsController；低版本退 legacy 口径）。</summary>
    private void ApplyImmersive()
    {
        var win = Window;
        if (win is null) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var ctl = win.InsetsController;
            if (ctl is not null)
            {
                ctl.Hide(WindowInsets.Type.SystemBars());
                ctl.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else
        {
            // legacy：SystemUiVisibility 已弃用但 minSdk 24 路径仍需
            var decor = win.DecorView;
            decor.SystemUiFlags = SystemUiFlags.HideNavigation | SystemUiFlags.ImmersiveSticky
                | SystemUiFlags.Fullscreen | SystemUiFlags.LayoutHideNavigation
                | SystemUiFlags.LayoutFullscreen;
        }
    }

    /// <summary>遮罩显收单点：抽屉或设置面板任一开着即亮（点遮罩空白处关闭，见 _scrim.Click）。</summary>
    private void UpdateScrim()
    {
        if (_scrim is null) return;
        bool need = _listOpen || _settingsPanelOpen;
        _scrim.Visibility = need ? ViewStates.Visible : ViewStates.Gone;
        UpdateEmptyPillSuppression(); // vc122 审查修（轻微）：有浮层在位时空态胶囊让位，不再透底压面板标题
    }

    /// <summary>空态胶囊让位（vc122 审查修）：面板/抽屉/子页是透底水墨，空态胶囊垫在下面会透出来
    /// 压住面板标题——任一浮层在位即隐形让位，全收后若仍在失败态再按原口径亮相。</summary>
    private bool _emptyPillSuppressed;
    private bool _emptyPillDeferredByToast; // vc124：空态胶囊为提示胶囊让位中（提示胶囊收起后回场）
    private void UpdateEmptyPillSuppression()
    {
        bool windowOpen = _listOpen || _settingsPanelOpen
            || _diagOverlay is not null || _statsOverlay is not null
            || _advOverlay is not null || _advGearOverlay is not null || _licenseOverlay is not null;
        if (windowOpen)
        {
            if (_emptyPill.Visibility == ViewStates.Visible)
            {
                _emptyPill.Visibility = ViewStates.Invisible;
                _emptyPillSuppressed = true;
            }
        }
        else if (_emptyPillSuppressed)
        {
            _emptyPillSuppressed = false;
            if (_manager.Current?.State == PlaybackState.Failed) ShowEmptyState();
        }
    }

    /// <summary>画面等比拉满（2026-11-27 真机反馈第5条）：竖屏满宽（高 16:9 上下黑边）、横屏满高（宽 16:9 外溢裁切）。
    /// 直播流主流 16:9，非 16:9 源按 16:9 框适配（真机验收后按需接内核视频尺寸回调）。</summary>
    private void ApplyVideoAspect()
    {
        if (_videoView is null || _playerPage is null) return;
        int w = _playerPage.Width, h = _playerPage.Height;
        if (w <= 0 || h <= 0) return;
        const double target = 16.0 / 9.0;
        double vw, vh;
        if (w >= h) { vh = h; vw = h * target; }   // 横屏：满高，宽按 16:9 外溢（左右裁切）
        else { vw = w; vh = w / target; }          // 竖屏：满宽，高按 16:9（上下黑边）
        _videoView.LayoutParameters = new FrameLayout.LayoutParams((int)vw, (int)vh)
        { Gravity = GravityFlags.Center };
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            ApplyImmersive(); // 焦点回归（弹窗/切后台回来）重收系统栏
            ApplyVideoAspect();
        }
    }

    /// <summary>转屏不重启（v16 口径）：尺寸/方向变化时重排画面等比 + 重建控制条（竖横布局不同）。
    /// 走查令 2026-11-27：回调瞬间布局未完成（读到旧尺寸），布局落地后再兜底重算一次（D4）。</summary>
    public override void OnConfigurationChanged(Android.Content.Res.Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig!);
        ApplyVideoAspect();
        RebuildControlBar();
        ApplyListWidth(_channels); // 定宽令 2026-11-27：竖屏全屏/横屏 480dp，转屏即定（不依赖内容）
        if (_listOpen) ApplyListTopInset(); // vc124：转屏横竖胶囊挂法不同，列表开着即按新方向重算顶距
        PositionHud(); // vc110：指示条落位随转屏重算（竖屏顶部黑区/横屏右侧黑区）
        _main.Post(ApplyVideoAspect);
    }

    /// <summary>SurfaceTexture 就绪：自建 Surface 并补挂当前会话（首帧创建/回前台重建同路径）。</summary>
    private void OnTextureAvailable(SurfaceTexture texture)
    {
        _textureSurface?.Release();
        _textureSurface = new Surface(texture);
        AttachSurfaceIfShown();
    }

    /// <summary>SurfaceTexture 销毁：摘会话面并释放自建 Surface；返回 true 纹理随销毁释放。</summary>
    private bool OnTextureDestroyed()
    {
        _manager.Current?.DetachSurface();
        _textureSurface?.Release();
        _textureSurface = null;
        return true;
    }

    private sealed class TextureHook(MainActivity owner) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
            => owner.OnTextureAvailable(surface);

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => owner.OnTextureDestroyed();

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }

    // —— 看门狗泵（会话层 Tick：缓冲超时/卡流自救） ——————————————————

    /// <summary>#75 音画同出兜底（2026-11-15 老板令，泵 3 秒节拍内调；vc66 重写治「画一直黑几秒后全线路不可达」）：
    /// 「音出画不出」=内核 READY 态视频断供/慢热。vc66 三修：
    /// ①零帧判定窗随真机出画耗时自校准（RevealEtaMs 指数均值×2，下限 20 秒）——慢热盒子不被死窗掐死重起，
    ///   画面在路上就被拦下重起是「画永远出不来」的第一根因（真机实锤：以前无守卫等得久但等得到）；
    /// ②遮罩挂起期（出画倒计时中）同样值守——音频先出视频死时立刻走递进，不再干等安全网掀盖露黑；
    /// ③计数只认用户换台与帧恢复归零（Buffering 过渡清账缺陷已修），2 次重建→切线路→线路走完强制进换源队列，
    ///   备源耗尽即全线路不可达终态（空态文案接管），全程递进不原地打转。</summary>
    private void PumpAudioOnlyGuard()
    {
        // vc117 后台出声根治令（2026-11-28 老板拍板）：后台冻结期只出声兜底不发起——
        // 它的三条出路（同源重建/切线/换源）全都会重播内核，后台发起=后台出声后门。
        if (_isBackgrounded) return;
        if (_manager.Current is not { } cur
            || cur.State != PlaybackState.Playing
            || cur.CurrentChannel is not { } ch)
        {
            return;
        }
        // vc66 ①：判定窗=20 秒与「2×真机出画耗时均值」取大——老版本基线（12 秒水位+解码）出画就在 13-16 秒起，
        // 窗口必须给慢热让够路；真死的快速通道走内核视频故障信号（OnVideoFault→账直接判过期），不靠窗硬等
        var zeroWindowMs = (long)Math.Max(20_000, RevealEtaMs * 2);
        if (_lastVideoFrameAtMs != 0 && System.Environment.TickCount64 - _lastVideoFrameAtMs < zeroWindowMs)
        {
            _stallRebuildCount = 0; // 有帧即清账：画面健康，重建计数归零
            return;
        }
        if (_lastVideoFrameAtMs == 0) return; // 尚未出过帧：起播极早期，过窗后再判（起播期看门狗同窗兜底）
        _lastVideoFrameAtMs = System.Environment.TickCount64; // 介入即重置：3 秒泵不连发
        if (_stallRebuildCount < 2)
        {
            _stallRebuildCount++;
            CrashNet.Record($"[只出声兜底] 零视频帧满 {zeroWindowMs}ms，同源重建第 {_stallRebuildCount} 次（{ch.Name}）");
            cur.ChangeChannel(ch); // 同源快速重建：FailoverCount 归零，先试原路
        }
        else if (cur.CurrentLineIndex + 1 < cur.LineCount)
        {
            CrashNet.Record($"[只出声兜底] 同源重建 2 次未救回，切线路 {cur.CurrentLineIndex + 2}/{cur.LineCount}（{ch.Name}）");
            _stallRebuildCount = 0;
            cur.SwitchLine(cur.CurrentLineIndex + 1);
        }
        else
        {
            // vc66 ③：线路走完仍零帧不再静默挂 Playing——强制转换源队列，备源耗尽即不可达终态（带原因上屏）
            CrashNet.Record($"[只出声兜底] 线路走完仍零帧，强制换源队列（{ch.Name}）");
            _stallRebuildCount = 0;
            cur.ForceFailover("audio-only zero-frame");
        }
    }

    private string _lastWaterText = ""; // 上一拍页头第二行文案（水位+画面时刻）：读数不可靠期间保持显示，不闪归零（vc85 水位跟手令）
    private string? _epgNowTitle;       // 当前节目名：null=官方缺表降级（控制条只显画面时刻）
    private string? _epgNextText;       // 接下来两条「HH:mm 节目名 · …」（vc113 对照原型 v24–v26 节目单口径）

    /// <summary>当前画面水位读数单点：无会话按 0 计；读数不可靠（重接/换票/准备期）返回 null，各显示点自行保持上一拍。</summary>
    private long? BufferedMsNow()
    {
        var session = _manager.Current;
        if (session is null) return 0;
        return session.Kernel.BufferedMsKnown ? session.Kernel.BufferedMs : (long?)null;
    }

    /// <summary>画面绝对时刻单点：现在−水位换算（源头延迟可标定），HH:mm:ss。</summary>
    private static string PictureClock(long bufferedMs) => DateTime.Now.AddMilliseconds(-bufferedMs).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>列表页头第二行（vc85 施工包5）：「实时水位：XX秒 · 画面时刻：XX:XX:XX」。
    /// 实时水位=当前会话内核攒着还没播的量（BufferedMs 实时读数），无会话按 0 秒计；
    /// 画面时刻=现在−水位换算的绝对时刻（源头延迟可标定），同一拍一并跟刷，两读数同源不错位。
    /// 读数不可靠（重接/换票/准备期）保持上一拍显示，只有真没货才显 0。</summary>
    private void UpdateWaterLevel()
    {
        if (_waterLevelText is null) return;
        long? bufferedMs = BufferedMsNow();
        if (bufferedMs is null)
        {
            if (_lastWaterText.Length > 0) _waterLevelText.Text = _lastWaterText; // 读不出≠没货：保持上一拍
            return;
        }
        _lastWaterText = HeadlineLine(bufferedMs.Value);
        _waterLevelText.Text = _lastWaterText;
    }

    /// <summary>页头第二行文案单点：实时水位（整秒）+ 画面时刻（现在−水位，HH:mm:ss）。</summary>
    private static string HeadlineLine(long bufferedMs) =>
        $"实时水位：{(int)Math.Round(bufferedMs / 1000.0)}秒 · 画面时刻：{PictureClock(bufferedMs)}";

    /// <summary>控制条节目名行跟刷（2026-11-27 真机反馈第10条：底部节目单条已拆，节目名+画面时刻走控制条）。</summary>
    private void UpdateEpgClock() => RefreshControlBarInfo();

    private bool _waterPumpRunning;
    private long? _stableSinceMs; // 水位连续高于 15 秒线的起算点（TickCount64 口径）

    /// <summary>水位稳判据（老板放行口径 2026-11-17 定稿）：连续 15 秒 &gt; 15 秒线——线与窗都是 15 秒，一把尺。</summary>
    private const long WaterStableLineMs = 15_000;
    private const long WaterStableWindowMs = 15_000;

    /// <summary>水位稳驱动（vc86 放行口径，秒泵每拍驱动）：会话不在播=开机/空闲/失败档放行（边界①）并清账；
    /// 在播但读数不可靠=判稳窗冻结（边界④：不推进不清零）；读数连续 15 秒&gt;15 秒线才判稳，掉线即清账。
    /// 判稳期间偷拉主源由 StealthPipeline 让路闸同尺暂停（F3 修：防 15s 线振荡活锁）。</summary>
    private void UpdateWaterStable()
    {
        var session = _manager.Current;
        if (session is null || session.State != PlaybackState.Playing)
        {
            _stableSinceMs = null;
            BackgroundLane.SetWaterStable(true); // 无画面在播：后台不憋，开机档放行
            return;
        }
        long? bufferedMs = BufferedMsNow();
        if (bufferedMs is null) return; // 判稳窗冻结：读数不可靠，不推进不清零
        if (bufferedMs.Value > WaterStableLineMs)
        {
            _stableSinceMs ??= System.Environment.TickCount64;
            if (System.Environment.TickCount64 - _stableSinceMs.Value >= WaterStableWindowMs)
                BackgroundLane.SetWaterStable(true); // 满窗判稳：放行
        }
        else
        {
            _stableSinceMs = null;
            BackgroundLane.SetWaterStable(false); // 未稳：后台全停（抽屉档另行单开）
        }
    }

    /// <summary>水位独立秒泵（vc85 水位跟手令，老板拍板「每秒都能看到最新变化」）：
    /// 从 3 秒自救泵摘出，1 秒一拍跟刷水位；只在抽屉开着时干活，收抽屉空转一拍即歇。
    /// vc86：同一拍驱动水位稳闸（放行口径），UI 与车道读同一把尺。
    /// 停泵跟自救泵同一旗（OnDestroy 后不泄漏）。</summary>
    private void EnsureWaterPump()
    {
        if (_waterPumpRunning) return;
        _waterPumpRunning = true;
        PumpWater();
        void PumpWater()
        {
            _main.PostDelayed(() =>
            {
                if (_watchdogStopped) return; // OnDestroy 后停泵：不泄漏 Activity
                UpdateWaterStable(); // vc86：每拍驱动放行闸（在 UI 刷数之前，车道先知稳不稳）
                if (_listOpen) UpdateWaterLevel();
                else UpdateEpgClock(); // 控制条节目名行秒级跟刷（2026-11-27 第10条：底部节目单条已拆）
                PumpWater();
            }, 1000);
        }
    }

    private void EnsureWatchdog()
    {
        if (_watchdogRunning) return;
        _watchdogRunning = true;
        _watchdogStopped = false;
        Pump();
        void Pump()
        {
            _main.PostDelayed(() =>
            {
                if (_watchdogStopped) return; // OnDestroy 后停泵（闪退镜黄⑦）：不泄漏 Activity
                // 抽屉开着画面也在播：自救泵跟会话走，不跟面板键位走（vc53 画面常驻口径）
                if (_manager.Current is not null) _manager.Tick();
                PumpAudioOnlyGuard(); // #75 音画同出兜底：播放中 5 秒零视频帧即介入（内核 READY 态看门狗覆盖不到）
                Pump();
            }, 3000);
        }
    }

    // —— 遥控器键位（vc53 交互口径 + 2.0 频道号直达：菜单呼/收抽屉、上下换台、左右切页签、确认收藏） ——

    public override bool OnKeyDown([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // TCL 设置键厂商码（真机实测 4077）：单击呼出软件设置面板（老板令 2026-11-15 #18：长按直达系统设置已删）。
        // 单击在 KeyUp 分流，这里只消费按键。
        if ((int)keyCode == 4077)
        {
            return true;
        }

        // vc112：退出确认弹窗已整删（返回=停播攒流退后台），无弹窗按键接管

        // vc117 令12：档位弹层与底层页互斥——弹层开着时底层进阶调优页已收，须独立接住：
        // 返回/菜单=收弹层回进阶调优页；其余键不穿透，防误触底层键位
        if (_advGearOverlay != null)
        {
            if (keyCode is Keycode.Back or Keycode.Menu)
            {
                CloseAdvGearSheetOnly();
                ShowAdvancedTuningPanel();
            }
            return true;
        }

        // 进阶调优面板：返回/菜单=关闭；确认=点当前行；
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
                    return false; // 交还系统走焦点导航（含面板内行间移动与光标行滚入可视区）
                default:
                    return true; // 其余不穿透
            }
        }

        // 自诊断/崩溃日志独立弹层已撤（v26 原型重做：并进设置页诊断卡片三页签，键位走设置面板分支）

        // 关于面板（v47 开源合规）：键位同自诊断——上下滚许可文本、确认点「关闭」、其余吞掉；单钮无左右移焦
        if (_licenseOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseLicensePanel();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown when _licenseScroll != null:
                {
                    int dir = keyCode == Keycode.DpadUp ? -1 : 1;
                    if (e is { RepeatCount: 0 })
                        _licenseScroll.SmoothScrollBy(0, dir * (int)(_licenseScroll.Height * 0.8f));
                    else
                        _licenseScroll.ScrollBy(0, dir * Dp(56));
                    return true;
                }
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                default:
                    return true; // 不穿透，防误触底层键位
            }
        }

        // vc113：诊断日志/运行统计子页开着——返回回设置页（原型 04/05 节「系统返回返回设置页」口径）
        if (_diagOverlay is not null) { CloseDiagPanel(); return true; }
        if (_statsOverlay is not null) { CloseStatsPanel(); return true; }

        // 设置面板开着：返回/菜单收起；#61 真机实锤——面板改流式一行多项后系统焦点搜索失灵，
        // 方向键改显式焦点导航（SettingsFocusMove），确认键显式点当前行；不再依赖系统焦点导航
        if (_settingsPanelOpen)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseSettingsPanel();
                    return true;
                case Keycode.DpadLeft:
                    SettingsFocusMove(-1, 0);
                    return true;
                case Keycode.DpadRight:
                    SettingsFocusMove(1, 0);
                    return true;
                case Keycode.DpadUp:
                    SettingsFocusMove(0, -1);
                    return true;
                case Keycode.DpadDown:
                    SettingsFocusMove(0, 1);
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                default:
                    return true; // 其余键不穿透，防误触底层键位
            }
        }

        // 收藏排序模式（v1.0.8 第14项）：独立状态机接管键位，退出恢复默认键位
        if (_sortMode)
        {
            HandleSortKey(keyCode);
            return true;
        }

        // 频道抽屉开着（vc53 抽屉键位；画面在面板下继续）：返回=退出确认、菜单=收抽屉、左右切页签、
        // 上下交列表焦点导航、数字键直达（2.0 保留）
        if (_listOpen)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Escape or Keycode.Menu:
                    ExitOrCollapseList(); // vc53 口径：菜单/返回=先收抽屉回画面，收完再按返回才弹退出确认
                    return true;
                case Keycode.DpadLeft or Keycode.DpadRight:
                    SelectTab(ListTabs.NextTab(_tab, keyCode == Keycode.DpadRight)); // 左右键切标签（vc53 抽屉口径）
                    return true;
                case >= Keycode.Num0 and <= Keycode.Num9:
                    FeedDigit((int)keyCode - (int)Keycode.Num0); // 频道号直达（2.0 保留：抽屉内也可直按）
                    return true;
            }
            return base.OnKeyDown(keyCode, e); // 上下键/确认交 ListView 原生焦点导航与点击
        }

        // 播放页（抽屉收起）
        switch (keyCode)
        {
            case Keycode.DpadUp:
                DebouncedSwitch(Neighbor(-1));
                return true;
            case Keycode.DpadDown:
                DebouncedSwitch(Neighbor(+1));
                return true;
            case Keycode.DpadCenter or Keycode.Enter:
                // 备源耗尽（Failed）时 OK=原地重试，接退避账（收口批③）：指数退避管重试节奏
                if (_manager.Current?.State == PlaybackState.Failed)
                {
                    if (_manager.TryManualRetry(_manager.Current.CurrentChannel?.Id ?? string.Empty, out var waitMs))
                        PlayAt(_currentIndex);
                    else
                        ShowToast(SwitchHud.RetryBackoff((int)Math.Ceiling(waitMs / 1000.0)));
                }
                else
                {
                    ToggleFavoriteOnCurrent(); // vc53：确认键收藏当前台
                }
                return true;
            case Keycode.DpadLeft:
                ListNav(+1); // vc53 口径：列表没开就呼出到对应位置，开了就翻页
                return true;
            case Keycode.DpadRight:
                ListNav(-1);
                return true;
            case Keycode.Menu or Keycode.Settings or Keycode.Info:
                ShowList(); // 菜单键呼抽屉（vc53：呼出同步无延迟）
                return true;
            case Keycode.Back or Keycode.Escape:
                ExitOrCollapseList(); // vc53 口径：播放页返回=退出确认弹窗（返回管退出、菜单管呼列表）
                return true;
            case >= Keycode.Num0 and <= Keycode.Num9:
                FeedDigit((int)keyCode - (int)Keycode.Num0); // 频道号直达（W5 保留）
                return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // 设置键单击在 KeyUp 分流（KeyDown 只消费键值；长按直达系统设置已删，真机反馈批#18）
        if ((int)keyCode == 4077)
        {
            // 铁律：任何时刻最多一个层——设置面板开着才允许收起；列表/其他面板开着时设置键不生效
            if (_settingsPanelOpen) ToggleSettingsPanel();
            else if (!_listOpen && _advOverlay == null && _advGearOverlay == null && _licenseOverlay == null)
                ToggleSettingsPanel();
            return true;
        }
        return base.OnKeyUp(keyCode, e);
    }

    public override bool OnKeyLongPress([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // 老板令 2026-11-15 #18：设置键长按直达系统设置已删（整键位移除，单击呼设置不受影响）
        if (keyCode is Keycode.DpadCenter or Keycode.Enter)
        {
            if (!_onPlayerPage)
            {
                // 列表内长按确认=进入收藏排序（v1.0.8 第14项 vc53 口径；非收藏台亮牌说明不硬进）
                TryEnterSortMode();
                return true;
            }
            // 播放页长按 OK = 切下一线路（W8 换线路入口，环形轮转）
            if (_manager.Current is { LineCount: > 1 } session)
            {
                var next = (session.CurrentLineIndex + 1) % session.LineCount;
                session.SwitchLine(next);
                ShowToast(TaoLive.Session.SwitchHud.LineSwitching(next));
            }
            return true;
        }
        return base.OnKeyLongPress(keyCode, e);
    }


    // —— 收藏 ——

    /// <summary>收藏当前台（vc53 确认键收藏口径）：播放页按确认即收藏/取消，信息条给结果。
    /// 收藏账与顺序账同步写（11-15 通读修）：取消收藏同撤顺序账，重收藏不因顺序账残留把台序弄丢。</summary>
    private void ToggleFavoriteOnCurrent()
    {
        var ch = _channels[_currentIndex];
        SyncFavorite(ch);
        PersistFavorites();
        ShowToast(_favorites.Contains(ch.Id)
            ? $"{IndexOfChannel(ch, _channels)}  {ch.Name}  ★ 已收藏"
            : $"{IndexOfChannel(ch, _channels)}  {ch.Name}  已取消收藏");
        if (_listOpen && _list.Adapter is ChannelListAdapter adapter) adapter.Refresh(_favorites, _tab);
    }

    /// <summary>收藏账与顺序账一次写齐（11-15 通读修）：此前只有收藏动作写顺序账，取消收藏残留旧序号，
    /// 再收藏时 Toggle 反把顺序账条目顶掉——排序入口（按顺序账认台）对该台失效。两账同源同步。</summary>
    private void SyncFavorite(Channel ch)
    {
        bool nowFav;
        if (!_favorites.Add(ch.Id))
        {
            _favorites.Remove(ch.Id);
            nowFav = false;
        }
        else nowFav = true;
        _favoritesView = new HashSet<string>(_favorites); // 变更后重发布快照（跨线程读者只读此份）
        if (_favStore.IsFavorite(ch.Number) != nowFav) _favStore.Toggle(ch.Number);
    }

    private static int IndexOfChannel(Channel target, IReadOnlyList<Channel> list)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == target.Id) return i;
        return -1;
    }

    private void ToggleFavoriteSelected()
    {
        var pos = _list.SelectedItemPosition;
        if (pos < 0) return;
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        var channel = adapter.ChannelAt(pos);
        if (channel is null) return;
        SyncFavorite(channel); // 两账同步（11-15 通读修，同 ToggleFavoriteOnCurrent 口径）
        PersistFavorites();
        adapter.Refresh(_favorites, _tab, _favStore.Numbers);
    }

    /// <summary>收藏状态与顺序一并持久化（StringSet 管「是否收藏」；order 串管顺序，vc53 口径）。</summary>
    private void PersistFavorites()
    {
        GetSharedPreferences("favorites", FileCreationMode.Private)!.Edit()!
            .PutStringSet("ids", _favorites)!
            .PutString("order", _favStore.Serialize())!
            .Apply();
    }

    // —— 收藏排序模式（vc53 v1.0.8 第14项 逐成员对照基线原文） ——

    /// <summary>入口：列表内长按确认键且焦点在收藏台上；非收藏台亮牌说明，不硬进。</summary>
    private void TryEnterSortMode()
    {
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        var pos = _list.SelectedItemPosition;
        if (pos < 0 || pos >= adapter.Count) return;
        var row = adapter.ChannelAt(pos);
        if (row is null || !_favorites.Contains(row.Id)) // 排序入口认收藏账（11-15 通读修）：顺序账只管次序，收藏与否以收藏账为准
        {
            ShowToast("请在收藏组内长按确认键排序");
            return;
        }
        _sortMode = true;
        _sortChannel = row;
        _sortBackup.Clear();
        _sortBackup.AddRange(_favStore.Numbers);
        ShowToast("排序中：上下移动，确认放下");
        RefreshSortList();
    }

    /// <summary>排序键位（vc53 原文口径）：上下逐位挪、左右按屏快速挪、确认放下保存、返回取消复原、信息键一键置顶。</summary>
    private void HandleSortKey(Keycode keyCode)
    {
        if (_sortChannel is null) { _sortMode = false; return; }
        int num = _sortChannel.Number;
        switch (keyCode)
        {
            case Keycode.DpadUp: _favStore.MoveUp(num); RefreshSortList(); break;
            case Keycode.DpadDown: _favStore.MoveDown(num); RefreshSortList(); break;
            case Keycode.DpadLeft:
            case Keycode.DpadRight:
            {
                int page = Math.Max(1, _list.LastVisiblePosition - _list.FirstVisiblePosition + 1);
                int dir = keyCode == Keycode.DpadLeft ? +page : -page; // 左=向下按屏挪，右=向上（vc53 原文）
                _favStore.MoveBy(num, dir);
                RefreshSortList();
                break;
            }
            case Keycode.DpadCenter or Keycode.Enter: // 放下并立即保存（持久化）
                _sortMode = false;
                PersistFavorites();
                _sortChannel = null;
                ShowToast("排序已保存");
                RefreshSortList();
                break;
            case Keycode.Back or Keycode.Menu: // 取消复原
                _sortMode = false;
                _favStore.Restore(string.Join(",", _sortBackup));
                _sortChannel = null;
                ShowToast("已取消排序");
                RefreshSortList();
                break;
            case Keycode.Info: // 一键置顶
                _favStore.MoveToTop(num);
                RefreshSortList();
                break;
        }
    }

    /// <summary>排序中/排序后重建列表（焦点跟随被排序的台，vc53 RefreshSortList 口径）。</summary>
    private void RefreshSortList()
    {
        if (!_listOpen) return;
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        adapter.Refresh(_favorites, _tab, _favStore.Numbers);
        // 焦点跟随被排序的台，不落到底层
        var pos = PositionOfChannel(_sortChannel);
        if (pos >= 0) _list.SetSelection(pos);
    }

    private int PositionOfChannel(Channel? channel)
    {
        if (channel is null || _list.Adapter is not ChannelListAdapter adapter) return -1;
        for (var i = 0; i < adapter.Count; i++)
            if (adapter.ChannelAt(i)?.Id == channel.Id) return i;
        return -1;
    }

    private int Neighbor(int delta)
    {
        var i = (_currentIndex + delta) % _channels.Count;
        return i < 0 ? i + _channels.Count : i;
    }

    // —— 五标签切换（vc53 抽屉口径：当前标签朱砂底高亮） ——

    private void SelectTab(ListTabs.Tab tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        if (_list.Adapter is ChannelListAdapter adapter) adapter.Refresh(_favorites, tab);
        StyleTabs();
    }

    /// <summary>页签行着色（vc53 StyleTabs 同款）：当前标签朱砂底浅字，其余极淡底。</summary>
    private void StyleTabs()
    {
        for (int i = 0; i < _tabViews.Count; i++)
        {
            var active = (ListTabs.Tab)i == _tab;
            _tabViews[i].SetBackgroundColor(active
                ? Color.ParseColor("#8A3B34") // vc53 StyleTabs：朱砂底
                : Color.Transparent);
            _tabViews[i].SetTextColor(Color.ParseColor(active ? "#F7F5EE" : InkTranslucency.TextSecondary));
        }
    }

    /// <summary>列表横滑切页签（vc124 老板令）：next=true 左滑到下一页签，false 右滑到上一页签，首尾循环。</summary>
    private void SwipeSwitchTab(bool next)
    {
        var tabs = (ListTabs.Tab[])Enum.GetValues(typeof(ListTabs.Tab));
        int idx = Array.IndexOf(tabs, _tab);
        if (idx < 0 || tabs.Length == 0) return;
        int n = tabs.Length;
        SelectTab(tabs[((idx + (next ? 1 : n - 1)) % n + n) % n]);
    }

    /// <summary>列表横滑手势（vc124 老板令）：横向位移自按下点累计过 60dp 且横向主导即触发一次切签；
    /// 一次手势只切一档，纵向滚动（|dx|&lt;|dy|）全程让位，不碰列表滚动与行点击。</summary>
    private sealed class TabSwipeTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly GestureDetector _detector;
        public TabSwipeTouchListener(GestureDetector detector) { _detector = detector; }
        public bool OnTouch(View? v, MotionEvent? e) { if (e is not null) _detector.OnTouchEvent(e); return false; } // 恒 false：事件照常交给列表
    }

    private sealed class TabSwipeGesture : GestureDetector.SimpleOnGestureListener
    {
        private readonly MainActivity _act;
        private bool _locked;
        public TabSwipeGesture(MainActivity act) { _act = act; }
        public override bool OnDown(MotionEvent e) { _locked = false; return false; }
        public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
        {
            if (_locked || e1 is null) return false;
            float dx = e2.GetX() - e1.GetX(), dy = e2.GetY() - e1.GetY();
            if (Math.Abs(dx) < Math.Abs(dy)) return false; // 纵向主导：让位列表滚动
            if (Math.Abs(dx) >= UiScale.Px(60))
            {
                _locked = true;
                _act.SwipeSwitchTab(dx < 0); // 左滑=下一页签，右滑=上一页签
            }
            return false;
        }
    }

    /// <summary>订阅新收录频道并入频道列表（老板 2026-11-26 令「最好还能有新收录」）：
    /// 目录外主流台（省级卫视/CCTV/CGTN，IsMainstreamNewChannel 口径，订阅库已限额）接在表尾，编号顺延不变号；
    /// 列表已建则原适配器换表重建行（ItemClick 接线不动）。返回本次并入的新频道数。</summary>
    private int MergeSubscriptionChannels(bool silent)
    {
        var extra = SubscriptionSourceStore.Shared.GetNewChannels(_channels.Select(c => c.Name));
        if (extra.Count == 0) return 0;
        var merged = new List<Channel>(_channels);
        // vc122 审查修（P3）：频道表为空（订阅先于内置表就绪并入）时 Max() 对空表抛异常——空表从 0 起编
        int number = merged.Count > 0 ? merged.Max(c => c.Number) : 0;
        foreach (var (name, entries) in extra)
        {
            number++;
            merged.Add(new Channel($"sub_{name}", name, "自有源", entries.Select(e => new SourceEndpoint(
                e.Url,
                Uri.TryCreate(e.Url, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.IPv4
                    ? SourceKind.PureIp : SourceKind.Cdn, 1)).ToList())
            { UserAgent = entries[0].Ua, Number = number });
        }
        _channels = merged;
        if (_list?.Adapter is ChannelListAdapter adapter) adapter.ReplaceChannels(_channels);
        ApplyListWidth(_channels);
        if (!silent) ShowToast($"源订阅：新收录频道 {extra.Count} 个");
        return extra.Count;
    }

    /// <summary>列表宽度定宽（v27 老板令定稿，对齐真机 vc113 拍板）：竖屏固定全屏、横屏 45% 屏宽，
    /// vc116⑩ 老板令：横屏列表改左侧贴边出现（原右侧贴边退场），不随内容/页签/收藏变。
    /// 沿革：vc65 恒定估宽→走查令改实式估宽+上限 0.98 屏→定宽 480dp（真机被钳到满屏，2026-11-27 三单③根因）→45% 比例宽。
    /// 转屏在 OnConfigurationChanged 重算（定宽不依赖内容，转屏即定）。</summary>
    private void ApplyListWidth(IReadOnlyList<Channel> channels)
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        int screenW = Resources.DisplayMetrics!.WidthPixels;
        int width = landscape
            ? Math.Max((int)(screenW * 0.45f), 1) // 横屏 45% 屏宽（原型 .landscape .overlay-list{width:45%}）
            : screenW; // 竖屏固定全屏
        _listPage.LayoutParameters = new FrameLayout.LayoutParams(width, FrameLayout.LayoutParams.MatchParent,
            GravityFlags.Left); // vc116⑩：横竖屏统一左侧贴边
    }

    /// <summary>vc111 页签一页放全兜底（老板令：系统显示大小三档都要一屏显全）：布局后实测总宽，
    /// 超屏先收页签左右内边距到 4dp，仍超再按 0.92 缩字号（最多五步）。字号与间距本就随屏等比，
    /// 常态三档显示大小比例相同必然放得下；此兜底只防 ROM 连动放大系统字体等比例外情况。</summary>
    private float _tabFontScale = 1f;

    private void FitTabsOnePage(LinearLayout row)
    {
        try
        {
            if (row.Parent is not View parent || parent.Width <= 0) return;
            if (row.MeasuredWidth <= parent.Width) return;
            bool shrunkPad = false;
            foreach (var tv in _tabViews)
            {
                if (tv.PaddingLeft > UiScale.Px(4))
                {
                    tv.SetPadding(UiScale.Px(4), UiScale.Px(10), UiScale.Px(4), UiScale.Px(10));
                    shrunkPad = true;
                }
            }
            if (shrunkPad) { row.Post(() => FitTabsOnePage(row)); return; }
            if (_tabFontScale <= 0.6f) return; // 极端兜底上限：再缩伤可读性，到此为止
            _tabFontScale *= 0.92f;
            foreach (var tv in _tabViews)
                tv.SetTextSize(ComplexUnitType.Sp, UiScale.Item * _tabFontScale);
            row.Post(() => FitTabsOnePage(row));
        }
        catch { /* 兜底失败不挡列表主链路 */ }
    }

    /// <summary>列表页顶部安全区避让（vc113 第④单修订：不再按挖孔/状态栏整页下顶，只避让提示胶囊显示区——
    /// 胶囊包孔，页内元素让到胶囊底缘之下即可，至少留 12dp 呼吸边）。每次呼出重算（转屏/分屏后 inset 会变）。</summary>
    private void ApplyListTopInset()
    {
        ReadCutoutGeometry(); // vc117 老板令：避让前先取准孔位，不拿旧几何算胶囊底缘
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        int top = UiScale.Px(12);
        bool insetsReady = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window!.DecorView!.RootWindowInsets is { } insets)
        {
            insetsReady = true;
            int cutout = insets.GetInsets(WindowInsets.Type.DisplayCutout()).Top;
            int status = insets.GetInsets(WindowInsets.Type.StatusBars()).Top;
            if (landscape)
            {
                // vc124 老板令（2026-11-28 真机「横屏列表页跑哪儿去了」）：横屏胶囊挂左上角（令6），
                // 不包顶孔——孔在左侧中部，孔心纵坐标≈半屏高，按竖屏包孔口径算顶距会把页头压到屏幕中间。
                // 横屏只让安全区顶距（挖孔/状态栏上沿+8dp 呼吸边），提示胶囊短暂盖页头左上与播放页同口径
                top = Math.Max(top, Math.Max(cutout, status) + UiScale.Px(8));
            }
            else
            {
                // 竖屏：胶囊显示区底缘=孔底 + 半个胶囊高（胶囊垂直居中包孔）+ 呼吸边；无孔信息退回状态栏口径。
                // vc117 令13（2026-11-28 老板拍板）：无孔兜底再吃胶囊自身高度（顶距 24+半高），状态胶囊
                // 不再压进页头 ⟳/✕ 按钮——此前 status=0 时页头只让 20dp，胶囊正好叠上按钮。
                int pillBottom = _cutoutHoleW > 0
                    ? Math.Max(cutout, _cutoutHoleCenterY + UiScale.Px(24))
                    : Math.Max(Math.Max(cutout, status), UiScale.Px(24) + UiScale.Px(28));
                top = Math.Max(top, pillBottom + UiScale.Px(8));
            }
        }
        if (!insetsReady)
        {
            // vc124 老板令（2026-11-28 真机「首屏标题贴边被盖」）：首帧 RootWindowInsets 未就绪
            // 不再只让 12dp 裸贴顶边——走无孔兜底口径（胶囊区 24+28dp+8dp 呼吸边），与重开位置一致；
            // 并挂一次性附型回调：窗口附型 inset 晚到即按真值回正，首屏/重开不再两副面孔
            top = UiScale.Px(24) + UiScale.Px(28) + UiScale.Px(8);
            if (!_listTopInsetAttachHooked)
            {
                _listTopInsetAttachHooked = true;
                _listPage.AddOnAttachStateChangeListener(new ListTopInsetReinset(this));
            }
        }
        // vc127 老板令（2026-11-28 真机「横屏列表页左侧边距还是不够，标题被覆盖到了」）：横屏抽屉贴左，
        // 挖孔在左缘——整页左内距补挖孔左安全区+8dp 呼吸边，「频道列表」标题/页签/列表全让开；
        // 竖屏孔在顶部，左距不动。转屏/重开即重算，与顶距同一口径。
        int leftSafe = 0;
        if (landscape && OperatingSystem.IsAndroidVersionAtLeast(30) && Window!.DecorView!.RootWindowInsets is { } insLeft)
            leftSafe = Math.Max(0, insLeft.GetInsets(WindowInsets.Type.DisplayCutout()).Left + UiScale.Px(8));
        _listPage.SetPadding(leftSafe, top, 0, 0);
    }

    /// <summary>vc124：列表页附型后重算顶部避让（首帧 inset 未就绪的回正钩子，一次性）。</summary>
    private bool _listTopInsetAttachHooked;

    private sealed class ListTopInsetReinset : Java.Lang.Object, View.IOnAttachStateChangeListener
    {
        private readonly MainActivity _act;
        public ListTopInsetReinset(MainActivity act) { _act = act; }
        public void OnViewAttachedToWindow(View v) => _act.ApplyListTopInset();
        public void OnViewDetachedFromWindow(View v) { }
    }

    /// <summary>vc116⑬ 子页面顶部安全区避让：挖孔/状态栏高度 + 8dp 呼吸边，与列表页同口径——
    /// 设置面板/诊断/统计/进阶调优/关于页统一调用，子页面标题不再顶到挖孔里。</summary>
    private int SafeTopInset(int basePx)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window!.DecorView!.RootWindowInsets is { } insets)
        {
            int cutout = insets.GetInsets(WindowInsets.Type.DisplayCutout()).Top;
            int status = insets.GetInsets(WindowInsets.Type.StatusBars()).Top;
            return Math.Max(basePx, Math.Max(cutout, status) + UiScale.Px(8));
        }
        return basePx;
    }

    /// <summary>vc119 丝滑过渡令：底部系统圆角半径（px）——API 31+ 实时读四角几何取底部两角最大值；
    /// 拿不到走 24dp 兜底。供贴底面板背景自长底部圆角用（内容不再整体上移避让）。</summary>
    internal int BottomCornerRadiusPx()
    {
        int fallback = UiScale.Px(24);
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return fallback;
        try
        {
            var insets = Window!.DecorView!.RootWindowInsets;
            if (insets is null) return fallback;
            int maxR = 0;
            foreach (var pos in new[] { Android.Views.RoundedCornerPosition.BottomLeft, Android.Views.RoundedCornerPosition.BottomRight })
            {
                var rc = insets.GetRoundedCorner((int)pos);
                if (rc is { } c) maxR = Math.Max(maxR, c.Radius);
            }
            return maxR > 0 ? maxR : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>vc121 同类排查收口（老板 2026-11-28 05:45 令「同类问题全部进清单」）：贴底面板底部内距的
    /// 圆角让位（px）——同控制条口径：让位=底部圆角半径−16dp，半径≤16dp 时为 0。
    /// vc124 老板令（2026-11-28 真机「横屏关闭按钮下面留白太多」「边距都统一」）：让位封顶 24dp——
    /// 系统圆角半径是物理值不随横屏变小，横屏面板矮、叠加后留白过大；封顶后横竖屏同口径，
    /// 关闭行居中且两端有 24dp+ 边距，38dp 底距远在弧区之上，不会被削角。
    /// 设置/诊断/统计/关于/许可/进阶调优/列表页统一走此口径，底部关闭行不再落进系统圆角弧区被削角。</summary>
    internal int BottomCornerClearPx() => BottomCornerSafePx(); // vc126 老板令（真机三处「按钮被圆角盖住」实锤）：24dp 封顶小于真机圆角弧区，退场——直读系统圆角真值+8dp 间距，拿不到走 32dp 大值兜底（宁多让不压钮）

    /// <summary>vc117 令19/20（2026-11-28 老板拍板）：底部圆角安全边（px）——API 31+ 实时读系统圆角几何
    /// （四角半径+圆心坐标），取底部两角半径最大值 + 8dp 间距；拿不到走 24dp 兜底。
    /// 令20 全局口径：所有贴底界面（控制条/列表页/设置面板/子页面）一开始就避让圆角区并留间距，
    /// 不是哪个页面被裁了再单独补。转屏后 insets 变化由调用点重算。</summary>
    internal int BottomCornerSafePx()
    {
        int fallback = UiScale.Px(32); // vc126 老板令：兜底从 24dp 提到 32dp——真机实锤 24dp 挡不住高圆角机型，宁多让不压钮
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return fallback;
        try
        {
            var insets = Window!.DecorView!.RootWindowInsets;
            if (insets is null) return fallback;
            int maxR = 0;
            foreach (var pos in new[] { Android.Views.RoundedCornerPosition.BottomLeft, Android.Views.RoundedCornerPosition.BottomRight })
            {
                var rc = insets.GetRoundedCorner((int)pos);
                if (rc is { } c) maxR = Math.Max(maxR, c.Radius);
            }
            return maxR > 0 ? maxR + UiScale.Px(8) : fallback;
        }
        catch { return fallback; }
    }

    // —— 预测式返回（2026-11-27 真机反馈第7条：去「再次滑动返回」二次确认，滑一次即走） ———

    private Java.Lang.Object? _backCallback; // 持引用防 GC 回收（Java 侧注册的是代理对象）

    /// <summary>API 33+ 注册系统返回回调：横屏=退全屏回竖屏，竖屏=退出确认（与返回键同路）；
    /// 旧系统走 OnKeyDown 原路，行为一致。</summary>
    private void RegisterPredictiveBack()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        var callback = new BackInvokerCallback(HandleSystemBack);
        _backCallback = callback;
        OnBackInvokedDispatcher!.RegisterOnBackInvokedCallback(
            Android.Window.IOnBackInvokedDispatcher.PriorityDefault, callback);
    }

    private void HandleSystemBack()
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        if (landscape)
        {
            if (_orientationLocked) return; // vc136 修（老板 15:45 报「锁定没起作用」）：锁定时返回键不退竖屏——锁优先于返回转向，翻向即破锁
            RequestedOrientation = ScreenOrientation.SensorPortrait; // 横屏退全屏回竖屏（vc116⑭ 去提示弹条）
        }
        else
        {
            ExitOrCollapseList(); // 竖屏：列表开着先收，播放页弹退出确认
        }
    }

    private sealed class BackInvokerCallback(Action onBack) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => onBack();
    }

    /// <summary>健康点刷新节流（方案书收口批②：更新节流 5 秒）。只读账刷新展示，不写账。
    /// 真机反馈批#4 打点（2026-11-15）：状态沿逐条落运行日志（不节流），圆点转绿口径按日志对账。</summary>
    private void OnPrecheckHealthChanged(string epgKey, ChannelHealth state)
    {
        CrashNet.Record($"[健康点] {epgKey} → {state}");
        if (_onPlayerPage) return; // 抽屉未开不见列表
        var now = System.Environment.TickCount64;
        if (now - _healthLastAtMs < 5000) return;
        _healthLastAtMs = now;
        _main.Post(() =>
        {
            if (_list.Adapter is not ChannelListAdapter adapter) return;
            adapter.Refresh(_favorites, _tab);
            ApplyListWidth(_channels); // vc65：同上恒定口径（健康账刷新不再带动宽度漂移）
        });
    }

    /// <summary>换台去抖入口（W8 SwitchDebouncer 口径）：窗口内连按取末次按键，停稳才真切。</summary>
    private void DebouncedSwitch(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        var ch = _channels[index];
        // 换台竞速：去抖窗口内即备热备（攒流到起播门槛线，停稳时热切接管不露黑）
        _ = _manager.PrepareStandby(ch);
        _debouncer.Tap(System.Environment.TickCount64, ch.Id);
        ShowToast(ch.Name);
        _main.PostDelayed(() =>
        {
            if (!_debouncer.HasTarget || !_debouncer.IsDue(System.Environment.TickCount64)) return;
            var key = _debouncer.Target;
            _debouncer.Clear();
            var idx = _channels.ToList().FindIndex(c => c.Id == key);
            if (idx >= 0) PlayAt(idx);
        }, SwitchDebouncer.WindowMs + 20);
    }

    private void FeedDigit(int d)
    {
        if (_digits.Length >= 3) return; // 位数封顶（闪退镜红① 11-15）：连按不溢出，编号三位内直达
        _digits = (_digits + d.ToString()).TrimStart('0');
        ShowToast(string.IsNullOrEmpty(_digits) ? "0" : _digits); // 盲测修：首位按 0 不再出空信息条
        _main.RemoveCallbacks(_digitCommit!); // 先摘旧回调再换新（闪退镜黄⑨）：已 Dispose 的 runnable 不再执行，窗口内连按不提前换台
        _digitCommit?.Dispose();
        _digitCommit = new Java.Lang.Runnable(() =>
        {
            if (string.IsNullOrEmpty(_digits)) return;
            var number = int.Parse(_digits);
            _digits = "";
            // 编号=列表顺序（一期口径：编号永久直达）
            if (number >= 1 && number <= _channels.Count) PlayAt(number - 1);
            else ShowToast($"无此频道 {number}");
        });
        _main.PostDelayed(_digitCommit, 1200);
    }

    /// <summary>提示胶囊（vc113 第④单，v28 老板令定稿：灵动岛式）——胶囊包住摄像头孔、孔上不画字、
    /// 文字显示在孔两侧；主文案走左段，右段留副文案位。无挖孔信息时退化为顶部居中胶囊。</summary>
    private void BuildToastPill()
    {
        _toast = new LinearLayout(this) { Orientation = Orientation.Horizontal }; // Gravity 只读，SetGravity 落后置
        _toast.SetGravity(GravityFlags.CenterVertical);
        _toast.Background = InkTranslucency.PillBackground(this); // vc116：全透明+胶囊描边（界面元素统一皮）
        _toast.SetPadding(UiScale.Dp(16), UiScale.Dp(8), UiScale.Dp(16), UiScale.Dp(8)); // vc127 老板令：左内边距偏窄根因=Px 口径出裸像素（实测≈7dp），改物理 dp 直读 16dp
        _toast.Visibility = ViewStates.Invisible;

        _toastLeft = new TextView(this) { TextSize = UiScale.Item };
        _toastLeft.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_toastLeft);
        _toastLeft.SetMaxLines(2); // vc116⑫：胶囊最多两行自适应（单行截断退场），超长仍走省略号
        _toastLeft.Ellipsize = TextUtils.TruncateAt.End;
        _toast.AddView(_toastLeft, new LinearLayout.LayoutParams(-2, -2));

        _toastGap = new View(this); // 中段空档=挖孔宽（孔上无字），无挖孔时宽 0
        _toast.AddView(_toastGap, new LinearLayout.LayoutParams(0, 1));

        _toastRight = new TextView(this) { TextSize = UiScale.Caption, Visibility = ViewStates.Gone };
        _toastRight.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_toastRight);
        _toastRight.SetMaxLines(2); // vc126 老板令：绕孔排布后右段也吃两行（长文案加高变两行）
        _toastRight.Ellipsize = TextUtils.TruncateAt.End;
        _toast.AddView(_toastRight, new LinearLayout.LayoutParams(-2, -2));

        // vc121 包孔扶正（老板 05:3x 真机截图实锤：右段无字时孔不在胶囊正中、压在文字下面）：
        // 左右各挂一个平衡占位（默认 0 宽）——竖屏包孔分支按左右两段文字宽度差补占位，
        // 孔两侧总宽相等 → 孔永远落在胶囊正中、孔上无字；横屏/无孔分支占位归零不影响。
        // vc127 老板令（「孔还是不在胶囊正中间」）：左占位原挂在右段之后=补错边——右段比左段宽时
        // 差值补在右端，孔反而左偏半个差值。改插到最左（下标 0），左右各归各位，孔心必居中。
        _toastBalL = new View(this);
        _toast.AddView(_toastBalL, 0, new LinearLayout.LayoutParams(0, 1));
        _toastBalR = new View(this);
        _toast.AddView(_toastBalR, new LinearLayout.LayoutParams(0, 1));
    }

    /// <summary>孔区几何统一读取（vc128 老板令「孔区提出来全处复用」）：找准孔中心点与直径，
    /// 孔区范围随之确定——胶囊包孔、绕孔排布、横屏指示条避让全走同一份口径，不再各写各的。
    /// 按当前屏幕方向取数：系统报孔位矩形用真值；不报的机型按「孔带内距+状态栏高度≈孔径」推。</summary>
    private (int Cx, int Cy, int Diameter) ReadHoleRect()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return (0, 0, 0);
        try
        {
            var cutout = Window!.DecorView!.RootWindowInsets?.DisplayCutout;
            var rect = cutout?.BoundingRects?.FirstOrDefault();
            if (rect is { } r && r.Width() > 0)
                return (r.CenterX(), r.CenterY(), r.Width());
            if (cutout is { } c)
            {
                bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
                if (landscape)
                {
                    // 横屏：孔落左缘垂直居中——X=左缘孔带一半，Y=屏高/2，直径≈左缘孔带宽
                    if (c.SafeInsetLeft > 0)
                        return (c.SafeInsetLeft / 2, Resources.DisplayMetrics!.HeightPixels / 2, c.SafeInsetLeft);
                }
                else if (c.SafeInsetTop > 0)
                {
                    // 竖屏（vc124~vc128 口径）：X=屏宽/2；直径按状态栏高度推（挖孔机状态栏高度≈孔径，
                    // 含孔上下留白），孔心=孔带底缘−半径；状态栏读不到退 28dp。直径不超孔带高度。
                    int holeD = StatusBarHeightPx();
                    if (holeD <= 0) holeD = UiScale.Dp(28);
                    holeD = Math.Min(holeD, c.SafeInsetTop);
                    return (Resources.DisplayMetrics!.WidthPixels / 2,
                            Math.Max(c.SafeInsetTop - holeD / 2, UiScale.Dp(14)), holeD);
                }
            }
        }
        catch { }
        return (0, 0, 0);
    }

    /// <summary>挖孔几何读取（vc113 第④单）：统一走 ReadHoleRect，供胶囊包孔与列表页避让。</summary>
    private void ReadCutoutGeometry()
    {
        var (cx, cy, d) = ReadHoleRect();
        _cutoutHoleCenterX = cx;
        _cutoutHoleCenterY = cy;
        _cutoutHoleW = d;
    }

    /// <summary>状态栏高度（物理 px，vc128 孔径推算用）：读系统 status_bar_height 尺寸；
    /// 读不到返回 0，由调用方走兜底口径。</summary>
    private int StatusBarHeightPx()
    {
        try
        {
            int id = Resources!.GetIdentifier("status_bar_height", "dimen", "android");
            if (id > 0) return Resources.GetDimensionPixelSize(id);
        }
        catch { }
        return 0;
    }

    /// <summary>胶囊包孔定位（v28 定稿：胶囊包住摄像头孔）：孔中心对齐胶囊中心，孔宽=中段空档；
    /// 无挖孔信息走顶部居中兜底。每次亮条重算（转屏/分屏后孔位会变）。
    /// vc117 令6（2026-11-28 老板拍板）：横屏改挂左侧挖孔区——文字靠左、往下换行（竖排两行），
    /// 不再横着盖画面；竖屏维持包孔口径。令14：亮胶囊前按当前开着的页面避让——列表/设置面板
    /// 已把内容顶到胶囊区之下（ApplyListTopInset/SafeTopInset），播放页横屏左上无元素，互不遮挡。
    /// vc117 老板令（2026-11-28）：避让定位不许闪烁——onPositioned 在终位落定后回调
    /// （竖屏包孔分支在 Post 对齐后回调，其余分支定位即终位同步回调），亮相时机由调用方掌握。</summary>
    private void PositionToastPill(Action? onPositioned = null)
    {
        ReadCutoutGeometry();
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        var lp = _toast.LayoutParameters as FrameLayout.LayoutParams;
        if (lp is null) { onPositioned?.Invoke(); return; }
        if (landscape)
        {
            // vc131 老板令（14:06/14:07）：横屏=竖放胶囊（竖长条）包孔——孔在左缘垂直居中，胶囊竖排：
            // 上段文字→孔位空档→下段文字，文字从上往下换行，孔永远在胶囊竖向正中；原左上角挂放（未包孔）退场。
            _toast.Orientation = Orientation.Vertical;
            _toast.SetGravity(GravityFlags.Top | GravityFlags.Left);
            // vc132 老板令（14:29「不用那么宽 圆角弧度也不用那么大」）：竖放胶囊收窄内距（16/8→10/6），
            // 最小宽从孔带全宽收窄 12dp（孔带含系统留白，实际孔径更小，仍完整包住孔圆）
            _toast.SetPadding(UiScale.Dp(10), UiScale.Dp(6), UiScale.Dp(10), UiScale.Dp(6));
            _toastGap.LayoutParameters = new LinearLayout.LayoutParams(1, Math.Max(1, _cutoutHoleW)); // 竖排中段空档高=孔径（孔上无字）
            ((LinearLayout.LayoutParams)_toastBalL.LayoutParameters!).Width = 0; // 竖排占位走高度，宽度归零
            ((LinearLayout.LayoutParams)_toastBalR.LayoutParameters!).Width = 0;
            _toast.SetMinimumHeight(Math.Max(UiScale.Dp(44), _cutoutHoleW + UiScale.Dp(8))); // 包孔高度容差（vc126 同源口径转 90°）
            _toast.SetMinimumWidth(Math.Max(UiScale.Dp(24), _cutoutHoleW - UiScale.Dp(12))); // vc132：收窄 12dp，左弧不再贴屏缘
            _toastLeft.SetMaxLines(6); // 松上限：真实容量由 LayoutToastTextAroundHole 按几何分行钳制
            _toastRight.SetMaxLines(6);
            // 文字限宽：竖长条口径——不出半屏、不超 100dp
            int segW = Math.Max(UiScale.Px(60), Math.Min((int)(Resources.DisplayMetrics!.WidthPixels / 2f - UiScale.Dp(24)), UiScale.Dp(100)));
            _toastLeft.SetMaxWidth(segW);
            _toastRight.SetMaxWidth(segW);
            lp.Gravity = GravityFlags.Top | GravityFlags.Left;
            lp.LeftMargin = 0;
            lp.TopMargin = 0;
            _toast.Post(() =>
            {
                _toast.Measure(
                    Android.Views.View.MeasureSpec.MakeMeasureSpec(Resources.DisplayMetrics!.WidthPixels, Android.Views.MeasureSpecMode.AtMost),
                    Android.Views.View.MeasureSpec.MakeMeasureSpec(0, Android.Views.MeasureSpecMode.Unspecified));
                int topW = _toastLeft.Visibility == ViewStates.Gone ? 0 : _toastLeft.Width;
                int botW = _toastRight.Visibility == ViewStates.Gone ? 0 : _toastRight.Width;
                int topH = _toastLeft.Visibility == ViewStates.Gone ? 0 : _toastLeft.Height;
                int botH = _toastRight.Visibility == ViewStates.Gone ? 0 : _toastRight.Height;
                if (topH <= 0 && botH <= 0) { onPositioned?.Invoke(); return; }
                // 竖向平衡占位（vc121 同源口径转 90°）：上下两段文字高度差补占位——孔两侧总高相等，
                // 孔永远落在胶囊竖向正中、孔上无字；占位改动后不赌布局帧，按已知量解析算终尺寸。
                int diff = topH - botH;
                ((LinearLayout.LayoutParams)_toastBalR.LayoutParameters!).Height = diff > 0 ? diff : 0;
                ((LinearLayout.LayoutParams)_toastBalL.LayoutParameters!).Height = diff < 0 ? -diff : 0;
                _toastBalL.RequestLayout();
                _toastBalR.RequestLayout();
                int pw = _toast.PaddingLeft + _toast.PaddingRight + Math.Max(topW, botW);
                int ph = _toast.PaddingTop + _toast.PaddingBottom + Math.Max(topH, botH) * 2 + Math.Max(1, _cutoutHoleW);
                ph = Math.Max(ph, _toast.MinimumHeight);
                // vc133 老板令（15:07 拍板方案A）：横屏撤「向下拉高」补足——横屏孔心在屏竖向正中，
                // minPh=2×(Cy−8dp) 会把胶囊拉到近全屏高（真机实锤 583px≈96%），文字挤顶、空档脱离
                // 真孔、下段盖控制条。恢复内容高+孔居竖向正中（vc131 口径），胶囊只包住孔和文字。
                lp.Height = ViewGroup.LayoutParams.WrapContent;
                _toast.LayoutParameters = lp;
                // vc133 老板令（15:07 拍板方案B项）：左缘钳 8dp——孔贴左缘机型 tx=max(0,…) 恒 0，
                // 左弧贴屏缘被裁（真机实锤 x=0）；钳 8dp 后左弧完整，孔心右偏 8dp 仍完整包住
                //（胶囊内距 10dp/侧 > 8dp 偏移，孔不会出胶囊）。
                float tx = Math.Max(UiScale.Dp(8), _cutoutHoleCenterX - pw / 2f);
                _toast.TranslationX = tx;
                float ty = Math.Max(UiScale.Dp(8), _cutoutHoleCenterY - ph / 2f); // 尽量孔居竖向正中；孔贴顶机型钳 8dp
                _toast.TranslationY = ty;
                onPositioned?.Invoke(); // 终位落定再亮相，杜绝先现身后跳位
            });
        }
        else if (_cutoutHoleCenterY > 0) // vc124：兜底几何（无孔位矩形时按安全区推孔心）同样走包孔分支
        {
            _toast.Orientation = Orientation.Horizontal; // 竖屏回横排包孔
            _toast.SetGravity(GravityFlags.CenterVertical);
            _toastLeft.SetMaxLines(2);
            _toastGap.LayoutParameters = new LinearLayout.LayoutParams(_cutoutHoleW, 1);
            // vc126 老板令「胶囊必须完整包住孔」：兜底孔心有估计误差——胶囊高度给足容差
            // （≥44dp，或孔宽+8dp），孔心即使偏几个 dp 孔仍被完整包住；文字限宽不出孔位。
            // vc127：容差改物理 dp 直读（Px 口径出裸像素，44 实为≈16dp，包不住高孔带）。
            _toast.SetMinimumHeight(Math.Max(UiScale.Dp(44), _cutoutHoleW + UiScale.Dp(8)));
            _toast.SetMinimumWidth(0); // vc131：解除横屏竖排的最小宽钳制，防互切残留
            // vc132 老板令（14:33 断点定案）：竖屏文字限宽改定数口径——左段每行≤3字、右段恒 2 字/行，
            // 右缘=孔心+2字宽+内距定数，不进状态栏；容量不足由两行兜底。
            // vc126/128/131 的 statusSafeW 动态钳宽口径退场。
            float cw = Math.Max(1f, _toastLeft.Paint!.MeasureText("字"));
            int availL = Math.Max(UiScale.Px(12), (int)(_cutoutHoleCenterX - UiScale.Dp(24) - _cutoutHoleW / 2f));
            int availR = Math.Max(0, (int)(Resources.DisplayMetrics!.WidthPixels - UiScale.Dp(90)
                - _cutoutHoleCenterX - UiScale.Dp(16) - _cutoutHoleW / 2f));
            _toastLeft.SetMaxWidth(Math.Min((int)(cw * 3 + UiScale.Px(2)), availL));
            _toastRight.SetMaxWidth(Math.Min((int)(cw * 2 + UiScale.Px(2)), availR));
            lp.Gravity = GravityFlags.Top; // 左上起摆，TranslationX 精确对孔心
            lp.LeftMargin = 0;
            lp.TopMargin = 0;
            _toast.Post(() =>
            {
                // vc128 老板令（「上方不能被覆盖」）：高度解析取值——先强制量一轮再取数。首建时序
                // _toast.Height 可能读到旧帧 0/旧值，顶弧被推出屏顶的根因；实测、旧值与最小高取大，
                // 不赌布局帧。ty 钳制保顶弧完整（压回孔带后 ty≥0 恒成立）。
                _toast.Measure(
                    Android.Views.View.MeasureSpec.MakeMeasureSpec(Resources.DisplayMetrics!.WidthPixels, Android.Views.MeasureSpecMode.AtMost),
                    Android.Views.View.MeasureSpec.MakeMeasureSpec(0, Android.Views.MeasureSpecMode.Unspecified));
                int ph = Math.Max(Math.Max(_toast.Height, _toast.MeasuredHeight), _toast.MinimumHeight);
                if (ph <= 0) { onPositioned?.Invoke(); return; }
                // vc121 包孔扶正（老板 05:3x 真机截图实锤）：右段无字时孔不在胶囊正中、压在文字下面——
                // 几何根因：孔（中段空档）跟在左段文字后，胶囊对孔心居中后孔落在文字下方（偏移=textW/2）。
                // 改法：按左右两段文字宽度差补平衡占位（左宽>右宽右侧补差值，反之左侧补），
                // 孔两侧总宽相等 → 孔永远居中、孔上无字。
                int leftW = _toastLeft.Visibility == ViewStates.Gone ? 0 : _toastLeft.Width;
                int rightW = _toastRight.Visibility == ViewStates.Gone ? 0 : _toastRight.Width;
                int diff = leftW - rightW;
                ((LinearLayout.LayoutParams)_toastBalR.LayoutParameters!).Width = diff > 0 ? diff : 0;
                ((LinearLayout.LayoutParams)_toastBalL.LayoutParameters!).Width = diff < 0 ? -diff : 0;
                ((LinearLayout.LayoutParams)_toastBalR.LayoutParameters!).Height = 0; // vc131：竖排占位残留清零（横竖互切）
                ((LinearLayout.LayoutParams)_toastBalL.LayoutParameters!).Height = 0;
                _toastBalL.RequestLayout();
                _toastBalR.RequestLayout();
                // vc127 老板令（「孔还是不在胶囊正中间」+真机截图实锤）：平衡占位改动后 _toast.Width
                // 仍是旧一帧的值，TranslationX 拿旧宽对中=胶囊被占位撑宽后整体右偏半个差值
                // （截图量得孔偏左≈66px，与新旧宽差 117px 的一半吻合）。改按已知量解析算终宽：
                // 左右内距 + 左右占位 + 左右文字 + 孔宽，不再赌布局时机，一步对中。
                // vc120 老板令（2026-11-28「所有胶囊必须包裹住孔 并保证孔在最中间」）：包孔居中 + 安全钳制
                // vc132 老板令（14:32「向下拉高」）：高度不足孔正中所需时拉高补足（孔居正中），
                // 超出时向下加高不裁字；vc126/131 的「宁裁字不偏心」钳制造场
                int minPh = Math.Max(1, (_cutoutHoleCenterY - UiScale.Dp(8)) * 2);
                if (ph < minPh) { ph = minPh; lp.Height = ph; }
                else lp.Height = ViewGroup.LayoutParams.WrapContent; // 解除上一帧钳制，防陈旧高度裁字
                _toast.LayoutParameters = lp;
                int pw = _toast.PaddingLeft + _toast.PaddingRight + Math.Max(leftW, rightW) * 2 + _cutoutHoleW;
                _toast.TranslationX = _cutoutHoleCenterX - pw / 2f;
                float ty = Math.Max(UiScale.Dp(8), _cutoutHoleCenterY - ph / 2f); // 尽量孔居正中；孔贴顶机型钳 8dp 向下拉高（vc132）
                _toast.TranslationY = ty;
                onPositioned?.Invoke(); // 终位落定（含兜底原位）再亮相，杜绝先现身后跳位
            });
        }
        else
        {
            // vc133 老板令（15:06 拍板方案A）：首颗胶囊在 OnCreate 连接流程亮起时 DecorView 未完成
            // 首次布局，挖孔几何读到 (0,0,0)——直接落兜底位=顶部居中，位置不对（真机实锤仅首次错）。
            // 改为等帧重读几何，拿到孔位即转包孔定位亮相；仍拿不到才落兜底位。
            // vc135 老板令（15:20「首启动胶囊还是有问题」）：vc133 的 3 帧重试在冷启时全部跑在
            // 首次 traversal 之前——Post 队列在视图挂载后立即出队，几何仍是 (0,0,0)，3 次用尽
            // 照样落兜底（真机复现）。改两处：①兜底门槛改「窗口布局完成」（DecorView.IsLaidOut）——
            // 布局没完成前只等不落，无孔机型首帧布局完立即兜底不多等；②重试上限 3→30（16ms 步进
            // ≈0.5s），几何一到即转包孔，首颗亮相即在终位。
            bool decorReady = Window!.DecorView!.IsLaidOut;
            if (!decorReady && _pillGeoRetries < 30)
            {
                _pillGeoRetries++;
                _toast.PostDelayed(() =>
                {
                    if (_cutoutHoleCenterY > 0) _pillGeoRetries = 0;
                    PositionToastPill(onPositioned); // 重读几何再判：就绪转包孔，未就绪继续等，布局完仍无孔落兜底
                }, 16);
                return; // 兜底 lp 不落，防闪现兜底位
            }
            _pillGeoRetries = 0;
            _toast.Orientation = Orientation.Horizontal;
            _toast.SetGravity(GravityFlags.CenterVertical);
            _toastLeft.SetMaxLines(2);
            _toastGap.LayoutParameters = new LinearLayout.LayoutParams(0, 1);
            ((LinearLayout.LayoutParams)_toastBalL.LayoutParameters!).Width = 0; // 非包孔分支占位归零
            ((LinearLayout.LayoutParams)_toastBalR.LayoutParameters!).Width = 0;
            ((LinearLayout.LayoutParams)_toastBalL.LayoutParameters!).Height = 0; // vc131：竖排占位残留清零（横竖互切）
            ((LinearLayout.LayoutParams)_toastBalR.LayoutParameters!).Height = 0;
            _toast.SetMinimumWidth(0); // vc131：解除包孔分支最小尺寸残留
            _toast.SetMinimumHeight(0);
            lp.Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal;
            lp.LeftMargin = 0;
            lp.TopMargin = SafeTopInset(0); // vc120 老板令：兜底分支不再贴死顶边，给安全区顶距
            _toast.TranslationX = 0;
            _toast.TranslationY = 0;
            onPositioned?.Invoke();
        }
        _toast.LayoutParameters = lp;
    }

    /// <summary>vc126 老板令（2026-11-28 口述定稿）：提示文案绕孔排布——孔占中间一位，文字从左到右流。
    /// vc132 老板令（14:33 断点定案）：竖屏文案封顶 10 字，右段恒 2 字/行（len≤8 右 2 字、9-10 右 4 字两行），
    /// 左段每行≤3字限宽；横屏竖排绕孔与无孔兜底维持原口径。</summary>
    private void LayoutToastTextAroundHole(string text)
    {
        ReadCutoutGeometry();
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        if (landscape)
        {
            // vc131 老板令（14:06/14:07）：横屏竖排绕孔——文字从上往下流：上段放满再进下段（孔占中间一位）；
            // 未满一段容量时按「行数差最小、再按宽度差最小」（vc130 同源）找断点，上下贴孔、孔上无字。
            _toastLeft.TextSize = UiScale.Item;
            if (_cutoutHoleCenterY <= 0)
            {
                _toastLeft.Text = text; // 无孔兜底：整段进上段
                _toastRight.Visibility = ViewStates.Gone;
                return;
            }
            float cwLand = Math.Max(1f, _toastLeft.Paint!.MeasureText("字"));
            int segW = Math.Max(UiScale.Px(60), Math.Min((int)(Resources.DisplayMetrics!.WidthPixels / 2f - UiScale.Dp(24)), UiScale.Dp(100)));
            float lineH = _toastLeft.Paint!.TextSize * 1.4f; // 行高（含行距保守估计）
            int screenH = Resources.DisplayMetrics!.HeightPixels;
            int capTopLines = Math.Max(1, (int)((_cutoutHoleCenterY - UiScale.Dp(16)) / lineH));
            int capBotLines = Math.Max(0, (int)((screenH - _cutoutHoleCenterY - UiScale.Dp(16)) / lineH));
            int cpl = Math.Max(1, (int)(segW / cwLand));
            int capTop = cpl * capTopLines;
            int capBot = cpl * Math.Max(1, capBotLines);
            string topText, botText;
            if (text.Length <= capTop + capBot)
            {
                int lo = Math.Max(0, text.Length - capBot), hi = Math.Min(capTop, text.Length);
                int nTop = lo, bestLines = int.MaxValue;
                long bestDiff = long.MaxValue;
                for (int n = lo; n <= hi; n++)
                {
                    int lt = (n + cpl - 1) / cpl, lb = (text.Length - n + cpl - 1) / cpl;
                    int ld = Math.Abs(lt - lb);
                    long wd = Math.Abs((long)_toastLeft.Paint!.MeasureText(text[..n]) - (long)_toastLeft.Paint!.MeasureText(text[n..]));
                    if (ld < bestLines || (ld == bestLines && wd < bestDiff)) { bestLines = ld; bestDiff = wd; nTop = n; }
                }
                topText = text[..nTop];
                botText = text[nTop..];
            }
            else
            {
                topText = text[..capTop]; // 超长：上段放满，余量进下段（MaxLines+省略兜底）
                botText = text[capTop..];
            }
            _toastLeft.SetMaxLines(capTopLines);
            _toastRight.SetMaxLines(Math.Max(1, capBotLines));
            _toastLeft.Text = topText;
            if (string.IsNullOrEmpty(botText)) { _toastRight.Visibility = ViewStates.Gone; _toastRight.Text = ""; }
            else { _toastRight.Text = botText; _toastRight.Visibility = ViewStates.Visible; }
            return;
        }
        if (_cutoutHoleCenterY <= 0)
        {
            _toastLeft.TextSize = UiScale.Item; // vc128：缩字号只作用于竖屏包孔长文案分支，他分支恢复标准档
            _toastLeft.Text = text;
            _toastRight.Visibility = ViewStates.Gone;
            return;
        }
        // vc132 老板令（14:33 断点定案）：文案封顶 10 字；右段恒 2 字/行——len≤8 右 2 字单行、
        // len 9-10 右 4 字两行；左段每行≤3字限宽（限宽定数在 PositionToastPill）。
        // vc127/130「像素宽差最小」断点循环与 >8 长文案缩字号分支退场。
        _toastLeft.TextSize = UiScale.Item;
        if (text.Length > 10) text = text[..10];
        int len = text.Length;
        string leftText, rightText;
        if (len <= 2)
        {
            leftText = text; // 一两个字全放左段，右侧留空
            rightText = "";
        }
        else
        {
            int rc = len <= 8 ? 2 : 4; // 右段字数：≤8 字右 2 字单行；9-10 字右 4 字两行
            leftText = text[..(len - rc)];
            rightText = text[(len - rc)..];
        }
        _toastLeft.Text = leftText;
        if (string.IsNullOrEmpty(rightText)) { _toastRight.Visibility = ViewStates.Gone; _toastRight.Text = ""; }
        else { _toastRight.Text = rightText; _toastRight.Visibility = ViewStates.Visible; }
    }

    /// <summary>信息条（vc53 InfoBar 口径）：顶部居中，自动隐藏；文案即给即显。
    /// vc113 第④单改胶囊：主文案进左段，胶囊包孔重定位；#66 每次亮条重取自适应水墨皮。</summary>
    private void ShowToast(string text)
    {
        LayoutToastTextAroundHole(text);
        // vc132 老板令（14:29「圆角弧度也不用那么大」）：横屏竖放胶囊圆角收小 14dp，竖屏维持全胶囊形
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        _toast.Background = InkTranslucency.PillBackground(this, landscape ? 14f : 999f);
        // vc132 老板令（14:37 总口径）：亮度条与提示胶囊互斥——提示胶囊亮相即收亮度条，不同屏
        if (_hudBright is { } hb && hb.Visibility == ViewStates.Visible)
        {
            hb.Visibility = ViewStates.Gone;
            if (_hudBrightHide is { } h) _main.RemoveCallbacks(h);
        }
        // vc124 老板令（2026-11-28 真机「两个胶囊重叠了」）：空态胶囊与提示胶囊都按包孔定位，同屏必叠——
        // 提示胶囊亮相前空态胶囊临时让位（隐形不撤账），提示胶囊收起后失败态仍在即原位回场。
        if (_emptyPill.Visibility == ViewStates.Visible)
        {
            _emptyPill.Visibility = ViewStates.Invisible;
            _emptyPillDeferredByToast = true;
        }
        // vc117 老板令（2026-11-28）：避让定位不许闪烁——要么不出现，出现就在终位。
        // 先隐形定位，竖屏包孔在 Post 对齐后、其余定位即终位，落定才亮相。
        _toast.Visibility = ViewStates.Invisible;
        PositionToastPill(() => _toast.Visibility = ViewStates.Visible);
        _main.RemoveCallbacks(_toastHide);
        _main.PostDelayed(_toastHide, 2200);
    }

    /// <summary>失败终态驻留空态（v77）：显示至离开失败态；vc113 原型对照令（v31 retry-tag）——
    /// 三件套文案+可点链接，警示橙框横幅（样式见 BuildRoot 装配点）。</summary>
    private void ShowEmptyState()
    {
        // vc126 老板令（2026-11-28「决不允许出现两个胶囊」）：同一时刻只许一颗——空态亮相先顶掉
        // 在亮的提示胶囊（新的顶旧的），并清让位回场账防陈旧恢复；反向（提示顶空态）ShowToast 已有。
        if (_toast.Visibility == ViewStates.Visible)
        {
            _main.RemoveCallbacks(_toastHide);
            _toast.Visibility = ViewStates.Invisible;
            _emptyPillDeferredByToast = false;
        }
        HideControlBar(); // vc115 A1：失败横幅与控制条互斥，横幅出场先收控制条
        SetEmptyPillText();
        // 长播无源退级提示（老板 2026-11-17 拍板）：空态满 5 分钟起如实报已等时长并建议检查网络；
        // vc122 审查修（P2）：记账+起拍提到亮相前，防分支提前 return 跳过。
        _emptyStateAtMs = System.Environment.TickCount64;
        _main.RemoveCallbacks(_emptyHintTick); // 重复亮相不叠拍
        _main.PostDelayed(_emptyHintTick, 30_000);
        // vc126 老板令（2026-11-28「他就不用胶囊了吧 它按照屏幕中间的样式去显示」）：空态退掉胶囊皮，
        // 改屏幕正中纯文字样式（与「正在连接×××」加载文案同位同款）——无包孔定位、无平衡占位，直接亮相。
        if (_emptyPill.LayoutParameters is FrameLayout.LayoutParams lp)
        {
            lp.Gravity = GravityFlags.Center;
            lp.LeftMargin = 0;
            lp.TopMargin = 0;
            _emptyPill.LayoutParameters = lp;
        }
        _emptyPill.TranslationX = 0;
        _emptyPill.TranslationY = 0;
        _emptyPill.Visibility = ViewStates.Visible;
        UpdateEmptyPillSuppression(); // vc122 审查修：亮相后若浮层在位即让位（失败态可能在面板开着时到来）
    }

    /// <summary>空态胶囊文案装配单点（vc124 三段式重构）：左段=状态行（信号中断 · 自动重试中（X/6），
    /// extra=退级提示附加行进第二行），右段=可点链接行（立即重试 · 打开列表）。</summary>
    private void SetEmptyPillText(string? extra = null)
    {
        var session = _manager.Current;
        int attempts = session?.SelfHealAttempts ?? 0;
        _emptyLeft.Text = $"信号中断 · 自动重试中（{attempts}/{PlaybackSession.SelfHealMaxAttempts}）"
            + (string.IsNullOrEmpty(extra) ? "" : $"\n{extra}");
        var right = new SpannableStringBuilder();
        AppendEmptyLink(right, "立即重试", RetryFromEmptyState);
        right.Append(" · ");
        AppendEmptyLink(right, "打开列表", () => { HideEmptyState(); ShowList(); });
        _emptyRight.TextFormatted = right;
        _emptyRight.Visibility = ViewStates.Visible;
    }

    /// <summary>空态链接段装配（原型 azure 蓝 #5E9EDE，无下划线；点击走既有重试/呼列表路径）。</summary>
    private static void AppendEmptyLink(SpannableStringBuilder ssb, string label, Action onClick)
    {
        int start = ssb.Length();
        ssb.Append(label);
        ssb.SetSpan(new EmptyLinkSpan(onClick), start, ssb.Length(), SpanTypes.ExclusiveExclusive);
    }

    /// <summary>空态「立即重试」（接退避账，与 OK 键重试同路：TryManualRetry 成功即原地重放，
    /// 退避窗内给节奏提示不硬抢）。</summary>
    private void RetryFromEmptyState()
    {
        long waitMs = 0; // 短路未进 TryManualRetry 时退避账为 0，提示按 0 秒口径
        if (_manager.Current?.State == PlaybackState.Failed
            && _manager.TryManualRetry(_manager.Current.CurrentChannel?.Id ?? string.Empty, out waitMs))
        {
            PlayAt(_currentIndex);
        }
        else
        {
            ShowToast(SwitchHud.RetryBackoff((int)Math.Ceiling(waitMs / 1000.0)));
        }
    }

    /// <summary>空态可点链接段（原型 azure 蓝，无下划线；匿名子类持动作不持视图）。</summary>
    private sealed class EmptyLinkSpan : Android.Text.Style.ClickableSpan
    {
        private readonly Action _onClick;
        public EmptyLinkSpan(Action onClick) => _onClick = onClick;
        public override void OnClick(View widget) => _onClick();
        public override void UpdateDrawState(Android.Text.TextPaint paint)
        {
            paint.Color = Color.ParseColor("#5E9EDE"); // 原型 --azure：强调/链接
            paint.UnderlineText = false;
        }
    }

    private void HideEmptyState()
    {
        if (_emptyPill.Visibility != ViewStates.Visible) { _emptyPillSuppressed = false; return; }
        _emptyPill.Visibility = ViewStates.Invisible;
        _main.RemoveCallbacks(_emptyHintTick);
        _emptyPillSuppressed = false; // vc122 审查修：状态离开失败态即清让位账，防陈旧恢复
    }

    // —— 列表适配器（vc53 ChannelAdapter 皮整套搬运）：
    //      真实台标（无台标=首字占位）+ 编号 + 星标 + 台名 + 健康点；当前台整行强调底；
    //      分组行/空态行 vc53 同款着色。 ——

    private sealed class Row
    {
        public string? Header;      // 分组行显示文本（频道行 null）
        public bool IsNote;         // 空态行（次色平铺，不作分组标题渲染）
        public Channel? Channel;    // 频道行归属台
        public bool DeadOnly;       // 连不上置底组内台（只能看不能切）
    }

    private sealed class ChannelListAdapter : BaseAdapter
    {
        private Context _ctx;
        private IReadOnlyList<Channel> _channels;
        private readonly Func<Channel, ChannelHealth> _markOf; // ④健康点：只读账（预检 MarkOf 三态）
        private readonly Func<string?> _currentId;
        private HashSet<string> _favorites;
        private ListTabs.Tab _tab;
        private string _filter = ""; // 筛选令（老板 2026-11-27 20:2x 拍板）：数字=频道号前缀、文字=台名包含
        private List<Row> _rows;
        private IReadOnlyList<int> _favOrder; // 收藏顺序（vc53 排序口径：收藏页签按此顺序铺开）

        public ChannelListAdapter(Context ctx, IReadOnlyList<Channel> channels, HashSet<string> favorites,
            Func<Channel, ChannelHealth> markOf, Func<string?> currentId, ListTabs.Tab tab = ListTabs.Tab.All,
            IReadOnlyList<int>? favoriteOrder = null)
        {
            _ctx = ctx;
            _channels = channels;
            _favorites = favorites;
            _markOf = markOf;
            _currentId = currentId;
            _tab = tab;
            _favOrder = favoriteOrder ?? Array.Empty<int>();
            _rows = BuildRows(channels, favorites, tab, markOf, _favOrder, _filter);
        }

        /// <summary>当前装配的行（宽度随内容重算用）。</summary>
        public IReadOnlyList<Row> Rows => _rows;

        /// <summary>行装配（vc53 DrawerSupport.BuildRows 口径一比一）：
        /// 收藏标签=收藏顺次铺开（按 favoriteOrder，未入册的按频道表序缀后）+空态行；其余标签=收藏成组置顶，无信号分组收口（全部标签），
        /// 装配完无任何频道行补空态行（不白屏）。
        /// 筛选令（老板 2026-11-27 20:2x 拍板）：纯数字=频道号前缀；含非数字=台名包含（不区分大小写）；空=不过滤。</summary>
        private static List<Row> BuildRows(IReadOnlyList<Channel> channels, HashSet<string> favorites,
            ListTabs.Tab tab, Func<Channel, ChannelHealth> markOf, IReadOnlyList<int> favOrder, string filter = "")
        {
            var rows = new List<Row>();
            // 收藏顺序铺开（vc53 排序口径）：order 在前的在前，未入 order 的收藏台按频道表序缀后
            var favOrdered = new List<Channel>();
            foreach (var n in favOrder)
            {
                var c = channels.FirstOrDefault(x => x.Number == n);
                if (c != null && favorites.Contains(c.Id) && favOrdered.All(x => x.Id != c.Id)) favOrdered.Add(c);
            }
            foreach (var c in channels)
                if (favorites.Contains(c.Id) && favOrdered.All(x => x.Id != c.Id)) favOrdered.Add(c);

            IEnumerable<Channel> baseList = tab switch
            {
                // vc53 v22 第 7 项（老板拍板）：CGTN 并回「央视」；专项随拍板并入央视口径；
                // 「地方台」只放真城市台（City/自有源），不再兜杂烩
                ListTabs.Tab.Cctv => channels.Where(c => c.Category is ChannelCategory.Cctv or ChannelCategory.Cgtn or ChannelCategory.Special),
                ListTabs.Tab.Satellite => channels.Where(c => c.Category == ChannelCategory.Provincial),
                ListTabs.Tab.Local => channels.Where(c => c.Category is ChannelCategory.City or ChannelCategory.UserSource),
                _ => channels,
            };
            IEnumerable<Channel> filtered = baseList.Where(c => !c.Unavailable);
            // 筛选令：纯数字=频道号前缀；含非数字=台名包含（不区分大小写）；空=不过滤
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                filtered = f.All(char.IsDigit)
                    ? filtered.Where(c => c.Number.ToString().StartsWith(f, StringComparison.Ordinal))
                    : filtered.Where(c => c.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            }
            var baseL = filtered.ToList();

            if (tab == ListTabs.Tab.Favorite)
            {
                foreach (var c in favOrdered) rows.Add(new Row { Channel = c });
                if (favOrdered.Count == 0)
                    rows.Add(new Row { Header = "收藏夹是空的，长按频道可收藏", IsNote = true }); // vc113 原型对照令（02 节空态规格）
                return rows;
            }

            // 收藏成组置顶（按收藏顺序结果）
            if (baseL.Any(c => favorites.Contains(c.Id)))
            {
                rows.Add(new Row { Header = "★ 收藏" });
                foreach (var c in baseL.Where(c => favorites.Contains(c.Id))) rows.Add(new Row { Channel = c });
            }
            foreach (var c in baseL.Where(c => !favorites.Contains(c.Id)))
                rows.Add(new Row { Channel = c });

            // 全部标签收口：无信号分组置底
            if (tab == ListTabs.Tab.All)
            {
                var off = baseL.Where(c => markOf(c) == ChannelHealth.Dead).ToList();
                if (off.Count > 0)
                {
                    rows.Add(new Row { Header = "── 无信号分组 ──" });
                    foreach (var c in off) rows.Add(new Row { Channel = c, DeadOnly = true });
                }
            }

            // v22 第 6/15 项 + vc113 原型对照令（02 节空态规格）：装配完没有任何频道行 → 空态提示；
            // 整表无频道=「还没有可看的频道 / 点右上角 ⟳ 重新获取」（两行）；筛选无命中=「没有匹配」；分类无频道=「该分类暂无频道」
            if (!rows.Any(r => r.Channel != null))
            {
                if (channels.Count == 0)
                {
                    rows.Add(new Row { Header = "还没有可看的频道", IsNote = true });
                    rows.Add(new Row { Header = "点右上角 ⟳ 重新获取", IsNote = true });
                }
                else if (!string.IsNullOrWhiteSpace(filter))
                {
                    rows.Add(new Row { Header = "没有匹配", IsNote = true });
                }
                else
                {
                    rows.Add(new Row { Header = "该分类暂无频道", IsNote = true });
                }
            }
            return rows;
        }

        /// <summary>行对应的频道；分组头行返回空。</summary>
        public Channel? ChannelAt(int position) =>
            position >= 0 && position < _rows.Count ? _rows[position].Channel : null;

        /// <summary>频道所在行位置（呼出列表焦点落当前台行用）；不在表内返回 -1。</summary>
        public int PositionOf(string channelId)
        {
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].Channel?.Id == channelId) return i;
            return -1;
        }

        /// <summary>收藏/标签/健康账变化后重建行（保持选中位置不跳）；favoriteOrder 传排序后的收藏顺序。</summary>
        public void Refresh(HashSet<string> favorites, ListTabs.Tab? tab = null, IReadOnlyList<int>? favoriteOrder = null)
        {
            _favorites = favorites;
            _tab = tab ?? _tab;
            if (favoriteOrder != null) _favOrder = favoriteOrder;
            _rows = BuildRows(_channels, favorites, _tab, _markOf, _favOrder, _filter);
            NotifyDataSetChanged();
        }

        /// <summary>筛选令变化（老板 2026-11-27 20:2x 拍板）：重建行装配；空串恢复全量。</summary>
        public void SetFilter(string filter)
        {
            if (_filter == filter) return;
            _filter = filter;
            _rows = BuildRows(_channels, _favorites, _tab, _markOf, _favOrder, _filter);
            NotifyDataSetChanged();
        }

        /// <summary>订阅新收录频道并入后整体换表（订阅并入批）：重建行并刷新，ItemClick 等接线保持原适配器实例不动。</summary>
        public void ReplaceChannels(IReadOnlyList<Channel> channels)
        {
            _channels = channels;
            _rows = BuildRows(channels, _favorites, _tab, _markOf, _favOrder, _filter);
            NotifyDataSetChanged();
        }

        public override int Count => _rows.Count;
        public override Java.Lang.Object GetItem(int position) => new Java.Lang.String(position.ToString());
        public override long GetItemId(int position) => position;

        /// <summary>行视图复用件（vc73 审查清零 P2）：行视图一次建齐挂 Tag，绑定只改文案/颜色/可见性/背景——
        /// 旧实现每次 GetView 都 RemoveAllViews 并 new 全套子视图，低配 TV 快速滚动 GC 压力大、掉帧。
        /// header 行与频道行同一张布局，按行型切可见性（GONE 不占位），视觉与旧逐项一致。
        /// 派生 Java.Lang.Object：View.Tag 收 Java 对象，普通 CLR 对象挂 Tag 会炸。</summary>
        private sealed class RowHolder : Java.Lang.Object
        {
            public LinearLayout Line = null!;
            public TextView Header = null!;
            public ImageView Logo = null!;
            public TextView Placeholder = null!;
            public View Spacer = null!;
            public TextView Num = null!;
            public TextView Star = null!;
            public LinearLayout Meta = null!; // 台名+副标题两行块（vc113 对照原型 02 节 chan-meta）
            public TextView Name = null!;
            public TextView Sub = null!;  // 行副标题：当前台「正在看」/其余「直播频道」
            public TextView Pill = null!; // 路源标（老板 2026-11-27 20:2x 拍板「元素肯定都要」）：每台显示源路数
            public TextView Dot = null!;
        }

        private void Style(TextView tv, string color, float size)
        {
            tv.SetTextColor(Color.ParseColor(color));
            tv.TextSize = size;
            tv.SetSingleLine(true); // vc65 老板令：列表行永不换行——行内文本一律单行，超宽走省略号不折行
        }

        private RowHolder BuildRowHolder()
        {
            var ctx = _ctx;
            var h = new RowHolder { Line = new LinearLayout(ctx) { Orientation = Orientation.Horizontal } };
            // vc116⑦：行内左右 8dp（叠加 ListView 8dp 内距=内容对齐 16dp 边线）、垂直 20→12 降行高
            h.Line.SetPadding(UiScale.Px(8), UiScale.Px(12), UiScale.Px(8), UiScale.Px(12));

            h.Header = new TextView(ctx);
            h.Header.Ellipsize = TextUtils.TruncateAt.End; // vc65：面板钳到半屏等极端场景走省略号，不折行
            InkTranslucency.ApplyReadabilityShadow(h.Header);
            h.Line.AddView(h.Header);

            h.Logo = new ImageView(ctx);
            // #61 尺寸令补漏：台标高度与首字占位块同框（40×28 基准），vc120 去写死令：改统一缩放口径 Px()（跟档自动走）
            h.Line.AddView(h.Logo, new LinearLayout.LayoutParams(UiScale.Px(40), UiScale.Px(28))
            { RightMargin = UiScale.Px(16), Gravity = GravityFlags.CenterVertical });

            h.Placeholder = new TextView(ctx);
            Style(h.Placeholder, InkTranslucency.TextSecondary, UiScale.Item);
            h.Placeholder.Gravity = GravityFlags.Center;
            h.Placeholder.SetBackgroundColor(Color.ParseColor("#3A4750"));
            h.Line.AddView(h.Placeholder, new LinearLayout.LayoutParams(UiScale.Px(40), UiScale.Px(28)) // #15 尺寸令；vc120 统一口径
            { RightMargin = UiScale.Px(16), Gravity = GravityFlags.CenterVertical });

            h.Spacer = new View(ctx);
            h.Line.AddView(h.Spacer, new LinearLayout.LayoutParams(0, 1) { RightMargin = UiScale.Px(16) }); // 无信号行行首不放台标（v4 定稿状态2）

            h.Num = new TextView(ctx) { Gravity = GravityFlags.Right };
            Style(h.Num, InkTranslucency.TextSecondary, UiScale.Item);
            InkTranslucency.ApplyReadabilityShadow(h.Num);
            h.Num.SetPadding(0, 0, UiScale.Px(32), 0);
            // vc120 老板令（2026-11-28「列表页的行元素也没有对齐」）：编号跟台名块同一垂直居中基准
            h.Line.AddView(h.Num, new LinearLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.CenterVertical });

            h.Star = new TextView(ctx) { Gravity = GravityFlags.CenterVertical }; // vc117 令6：收藏点垂直居中
            Style(h.Star, "#F0C563", UiScale.Item);
            InkTranslucency.ApplyReadabilityShadow(h.Star);
            h.Star.SetPadding(0, 0, UiScale.Px(24), 0);
            h.Line.AddView(h.Star, new LinearLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.CenterVertical }); // vc120：布局重力修正（原误挂视图重力仍顶对齐）

            // 台名+副标题两行块（vc113 对照原型 02 节 chan-meta：chan-name 上、chan-sub 下）
            h.Meta = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            h.Name = new TextView(ctx);
            h.Name.Ellipsize = TextUtils.TruncateAt.End; // vc65：半屏钳制兜底，宁省略不折行
            InkTranslucency.ApplyReadabilityShadow(h.Name);
            h.Meta.AddView(h.Name, new LinearLayout.LayoutParams(-2, -2));
            h.Sub = new TextView(ctx);
            Style(h.Sub, InkTranslucency.TextSecondary, UiScale.Caption);
            InkTranslucency.ApplyReadabilityShadow(h.Sub);
            h.Meta.AddView(h.Sub, new LinearLayout.LayoutParams(-2, -2));
            h.Line.AddView(h.Meta, new LinearLayout.LayoutParams(0, -2, 1f) { Gravity = GravityFlags.CenterVertical }); // vc115 D1：weight=1 占余宽，修 vc113 宽 0 不可见

            // 路源标（老板 2026-11-27 20:2x 拍板「元素肯定都要」；v26 olist 口径）：圆角小胶囊显示源路数，
            // 紧跟台名，状态点之前；半透明浅底+次色字，不抢台名主视觉
            h.Pill = new TextView(ctx) { Gravity = GravityFlags.Center };
            Style(h.Pill, InkTranslucency.TextSecondary, UiScale.Caption);
            var pillBg = new Android.Graphics.Drawables.GradientDrawable();
            pillBg.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
            pillBg.SetCornerRadius(UiScale.Px(10));
            pillBg.SetColor(Color.ParseColor("#33D8E2EC")); // 纸色 20% 透明底
            h.Pill.Background = pillBg;
            h.Pill.SetPadding(UiScale.Px(12), UiScale.Px(2), UiScale.Px(12), UiScale.Px(2));
            var pillLp = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = UiScale.Px(12), Gravity = GravityFlags.CenterVertical };
            h.Line.AddView(h.Pill, pillLp);

            h.Dot = new TextView(ctx) { Gravity = GravityFlags.CenterVertical }; // vc117 令6：状态点垂直居中
            h.Dot.SetPadding(UiScale.Px(24), 0, 0, 0);
            InkTranslucency.ApplyReadabilityShadow(h.Dot);
            h.Line.AddView(h.Dot, new LinearLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.CenterVertical }); // vc120：布局重力修正（原误挂视图重力仍顶对齐）
            return h;
        }

        private void BindRow(RowHolder h, Row row)
        {
            var ctx = _ctx;
            if (row.Header != null)
            {
                // 分组/空态行：只有标题（vc53 v22 第 15 项：空态行次色平铺，不作分组标题渲染）
                bool isNote = row.IsNote;
                Style(h.Header, isNote ? InkTranslucency.TextSecondary : "#A9CFEC", UiScale.Caption);
                h.Header.Text = row.Header;
                h.Header.Visibility = ViewStates.Visible;
                h.Logo.Visibility = ViewStates.Gone;
                h.Placeholder.Visibility = ViewStates.Gone;
                h.Spacer.Visibility = ViewStates.Gone;
                h.Num.Visibility = ViewStates.Gone;
                h.Star.Visibility = ViewStates.Gone;
                h.Meta.Visibility = ViewStates.Gone;
                h.Pill.Visibility = ViewStates.Gone;
                h.Dot.Visibility = ViewStates.Gone;
                h.Line.SetBackgroundColor(Color.Transparent);
                return;
            }

            var ch = row.Channel!;
            // vc75 审查清零 P1：ViewHolder 复用回置——频道行分支此前不收分组标题，
            // header 行复用进频道行时残留上一行标题叠字（旧实现逐行重建无此病，vc73 重构引入）。
            h.Header.Visibility = ViewStates.Gone;
            // B 包第 12 项：行首真实台标（vc53 同源内置资源）；无内置台标→首字占位；无信号行不放台标
            var logoRes = ChannelLogoMap.ResourceName(ch.Name);
            bool logoResolved = false;
            if (!row.DeadOnly && logoRes != null)
            {
                try
                {
                    var resId = ctx.Resources!.GetIdentifier(logoRes, "drawable", ctx.PackageName);
                    if (resId != 0)
                    {
                        h.Logo.SetImageResource(resId);
                        logoResolved = true;
                    }
                }
                catch { /* 台标渲染失败不碍选台 */ }
            }
            h.Logo.Visibility = logoResolved ? ViewStates.Visible : ViewStates.Gone;
            // 占位条件与旧口径一致：logoRes 无资源名才用首字占位（resId 查不到时旧行为首不放任何元素）
            h.Placeholder.Visibility = !row.DeadOnly && logoRes == null ? ViewStates.Visible : ViewStates.Gone;
            if (h.Placeholder.Visibility == ViewStates.Visible) h.Placeholder.Text = ch.Name.Substring(0, 1);
            h.Spacer.Visibility = row.DeadOnly ? ViewStates.Visible : ViewStates.Gone;

            h.Num.Text = $"{IndexInTable(ch),3}";
            h.Num.Visibility = ViewStates.Visible;
            h.Star.Text = _favorites.Contains(ch.Id) ? "★" : " ";
            h.Star.Visibility = ViewStates.Visible;

            var isCur = _currentId() == ch.Id;
            h.Name.Text = ch.Name;
            Style(h.Name, isCur ? "#FFFFFF" : InkTranslucency.TextSecondary, UiScale.Item);
            h.Name.Typeface = isCur ? Typeface.DefaultBold! : Typeface.Default!; // 复用件必须显式回置，防上一行粗体残留
            // vc115 D2：当前台改 deep #3D6E9E 描边（原型 .chan-row.current），去棕色 Accent 填充。
            // vc117 令6：描边内收与行内容同边线；vc121 老板拍板方案A（2026-11-28 真机「框线盖到台标」）：
            // 描边盒内缩改 4dp——vc124 老板令（2026-11-28 真机「框线还是压台标」实锤：低缩放档 4dp≈2px，
            // 描边与台标视觉压线）内缩 4dp 退场，改 0 内缩——描边画在列表 16dp 内距边线上，
            // 与台标（24dp 边线起笔）拉开 8dp 实距，行内容位置一概不动
            if (isCur)
            {
                var curBg = new Android.Graphics.Drawables.GradientDrawable();
                curBg.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
                curBg.SetColor(Color.Transparent);
                curBg.SetStroke(UiScale.Px(1), Color.ParseColor("#3D6E9E"));
                curBg.SetCornerRadius(UiScale.Px(12));
                h.Line.Background = curBg;
            }
            else
            {
                h.Line.SetBackgroundColor(Color.Transparent);
            }
            h.Meta.Visibility = ViewStates.Visible;
            // 行副标题（vc113 对照原型 02 节 chan-sub）：当前台「正在看」、其余「直播频道」
            h.Sub.Text = isCur ? "正在看" : "直播频道";
            Style(h.Sub, isCur ? "#F0C563" : InkTranslucency.TextSecondary, UiScale.Caption);

            // 路源标（老板 2026-11-27 20:2x 拍板「元素肯定都要」；vc121 实数令 2026-11-28 05:4x 拍板）：
            // 显示该台实际可用源路数=主源+备援注册表候选+订阅同台源（与播放备援队列 FallbackQueueSource 同口径，
            // 不再只数主源恒显 1 路）；0 路（异常态）不显
            var srcCount = ch.SourceEndpoints.Count + FallbackQueueSource.FromRegistry(ch).Count;
            h.Pill.Text = $"{srcCount}路";
            h.Pill.Visibility = srcCount > 0 ? ViewStates.Visible : ViewStates.Gone;

            // 可连状态标识（v1.0.8 预检握手）：绿点可用 · 灰点检测中/未探 · 红叉连不上；
            // 未探态提亮 #8894A0→#A9B7C5（走查令 2026-11-27：深底对比度 4.4:1→6.6:1，CCTV4/CCTV7 状态点看不清）
            var (mark, color) = _markOf(ch) switch
            {
                ChannelHealth.Alive => ("●", "#7ED182"),
                ChannelHealth.Dead => ("✕", "#E97F7F"),
                ChannelHealth.Checking => ("●", "#B5C4D0"),
                _ => ("●", "#A9B7C5"),
            };
            h.Dot.Text = mark;
            Style(h.Dot, color, UiScale.Caption);
            h.Dot.Visibility = ViewStates.Visible;
        }

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var row = _rows[position];
            var holder = convertView?.Tag as RowHolder;
            if (holder is null)
            {
                holder = BuildRowHolder(); // 首次建齐整行，此后只绑定
                convertView = holder.Line;
                convertView.Tag = holder;
            }
            BindRow(holder, row);
            return holder.Line;
        }

        /// <summary>编号=整表顺序（一期口径：编号永久直达，FeedDigit 同款语义）。</summary>
        private int IndexInTable(Channel c)
        {
            for (var i = 0; i < _channels.Count; i++)
                if (_channels[i].Id == c.Id) return i + 1;
            return 0;
        }
    }
}
