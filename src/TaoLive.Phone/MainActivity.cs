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
    private TextView _toast = default!;
    private TextView _emptyState = default!;
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
    // 文案矩阵单点（方案书 v2.4 §25）：失败终态空态文案常量 + 长播无源退级提示账
    private const string EmptyStateText = "信号中断，正在自动重试\n点按画面呼出控制条可换台";
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
    private AlertDialog? _exitDialog;        // 退出确认（v1.0.9 用户拍板，vc22 水墨浮层样式）
    private TextView? _exitNo;
    private TextView? _exitYes;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 持久化设置（vc53 口径）：UI 缩放档 + 后台预热开关 + 进阶调优旋钮，启动装载
        //（缩放档须在 BuildRoot 前就位，全套令牌按档位重算）
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        // 尺寸跟系统（老板 2026-11-27 拍板「跟着系统显示大小做三套，不单独给设置项」）：
        // 文字档 = 中档基准 17sp × 系统字体缩放（系统「标准」=原中档；较大/较小随系统档等比）；
        // 间距档乘密度，系统「显示大小」改密度即自动跟随。改系统档系统会重建 Activity，此处重装即生效。
        UiScale.Factor = 17f / 36f * (Resources!.Configuration?.FontScale ?? 1f);
        // 手机版重定标（2026-11-27 真机整改令）：电视端「长边/1920」直接套手机（长边 2400→1.25）是
        // 「信息条文字巨大盖画面」的根因。改按原型 v13「基准 390px=1:1」：短边 dp 对 390——
        // 文字档与间距档同基准，间距档另乘密度转像素（热区不再跟文字档缩没）。
        var dm = Resources!.DisplayMetrics!;
        float shortEdgeDp = Math.Min(dm.WidthPixels, dm.HeightPixels) / dm.Density;
        UiScale.ScreenScale = shortEdgeDp / 390f;
        UiScale.PxScale = shortEdgeDp / 390f * dm.Density;
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
        BuildRoot();
        SetContentView(_root);
        ApplyImmersive(); // 手机版整改令 2026-11-27：沉浸式全屏（收状态栏/导航栏）
        RegisterPredictiveBack(); // 2026-11-27 真机反馈第7条：接系统预测式返回（去「再次滑动返回」二次确认）
        ShowList();
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
            // 水墨深底盖旧画，空态文案叠其上（BuildRoot z 序：_emptyState 在 _loadingPanel 之上）。
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
                    if (!_onPlayerPage) PlayAt(idx); // 用户已手动进台不抢
                }
                catch (Exception ex) { CrashNet.Record($"[Boot] 开机链路异常照常进默认台：{ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            CrashNet.Record($"[Boot] 开机链路异常照常进默认台：{ex.Message}");
            _main.Post(() => { if (!_onPlayerPage) PlayAt(DefaultChannelIndex()); });
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
        // 切后台音画即停（2026-11-27 真机反馈第8条）：静音冻结出声出画，内核照常灌水位，切回续播
        var cur = _manager?.Current;
        if (cur is not null && cur.State == PlaybackState.Playing)
        {
            try { cur.PausePlayback(); _pausedForBackground = true; CrashNet.Record("[生命周期] 切后台暂停音画（内核照常缓冲）"); }
            catch (Exception ex) { CrashNet.Record($"[生命周期] 切后台暂停失败：{ex.Message}"); }
        }
        StartKeepAlive(); // 老板令 #20：切后台保活（已在跑则幂等跳过）
    }

    private bool _pausedForBackground; // 切后台由本类暂停（区别于用户手动暂停），回前台只恢复这种

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

    private void BuildRoot()
    {
        // 水墨主题底（#65 老板令 2026-11-15：改水墨深底 #2B333B，与窗底/切台遮罩同色——
        // 开机到首帧之间露底不再白屏，跟切台一样黑底+延迟亮牌 Loading，首帧出画自动撤；
        // 原「暖宣浅底不露黑」口径废止：真机白屏几秒实锤比黑更难看）
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.ParseColor("#2B333B"));

        _toastHide = new Java.Lang.Runnable(() =>
            _toast.Visibility = ViewStates.Invisible);

        // 长播无源退级提示（老板 2026-11-17 拍板）：空态满 5 分钟起每分钟如实报已等时长并建议
        // 检查网络；自愈慢轮+重探仍在后台自动复活，提示只是让等待有交代。可见性判定兼停表：
        // 离开空态（HideEmptyState 撤回调）或空态已被复用改写即自然停摆。
        _emptyHintTick = new Java.Lang.Runnable(() =>
        {
            if (_emptyState.Visibility != ViewStates.Visible) return;
            var waitedMin = (int)((System.Environment.TickCount64 - _emptyStateAtMs) / 60_000);
            if (waitedMin >= 5)
                _emptyState.Text = $"{EmptyStateText}\n已 {waitedMin} 分钟未恢复，建议检查网络";
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
        _videoView.LayoutChange += (_, _) => ApplyVideoAspect();
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

        // 失败终态驻留空态（方案书 v77，白屏镜红②）：非瞬时，进失败态显示、离失败态即隐；
        // 样式照 vc53 水墨口径：固定加深水墨底（不跟亮度，失败文案必须醒目）+ 可读性投影
        _emptyState = new TextView(this) { Gravity = GravityFlags.Center };
        _emptyState.SetTextColor(InkTranslucency.TextPrimary(this));
        _emptyState.TextSize = UiScale.Title;
        _emptyState.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));
        InkTranslucency.ApplyReadabilityShadow(_emptyState);
        _emptyState.SetPadding(UiScale.Px(40), UiScale.Px(24), UiScale.Px(40), UiScale.Px(24)); // #15 尺寸令：随档缩放
        _emptyState.Visibility = ViewStates.Invisible;
        _playerPage.AddView(_emptyState, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });

        _root.AddView(_playerPage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 2. 切台信息条（vc53：顶部居中，自动隐藏；亮度自适应水墨底）
        _toast = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Invisible,
        };
        _toast.SetTextColor(InkTranslucency.TextPrimary(this));
        _toast.Background = InkTranslucency.HintBackground(this); // #66 高透自适应水墨皮（亮屏深托字、暗房高透）
        InkTranslucency.ApplyReadabilityShadow(_toast);
        _toast.SetPadding(UiScale.Px(36), UiScale.Px(16), UiScale.Px(36), UiScale.Px(16)); // #15 尺寸令
        _toast.TextSize = UiScale.Item;
        _root.AddView(_toast, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = UiScale.Px(48),
        });

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
        _listPage.SetBackgroundColor(InkTranslucency.PanelSolidColor(this));
        _listPage.Visibility = ViewStates.Gone;
        _root.AddView(_listPage, new FrameLayout.LayoutParams(-1, -1)); // 宽度在 ApplyListWidth 钳制

        // 抽屉页头（vc85 施工包5：横排三段挤一行改上下两排——第一行大标题「TaoLive 频道 共N路」，
        // 第二行小字「实时水位：XX秒 · 画面时刻：XX:XX:XX」；横向一个像素不动，外层宽度仍由 ApplyListWidth 钳制）
        var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
        // 老板令 2026-11-17：列表整体上移让出台标——顶 padding 40→12，页头两行升到台标上方，
        // 台标横条不再与水位小字叠字；横向一个像素不动，宽度仍由 ApplyListWidth 钳制
        header.SetPadding(UiScale.Px(48), UiScale.Px(12), UiScale.Px(48), UiScale.Px(8)); // #15 尺寸令
        var titleRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var title = new TextView(this) { Text = "TaoLive 频道", TextSize = UiScale.Title };
        title.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(title);
        titleRow.AddView(title);
        var count = new TextView(this) { Text = $"  共 {_channels.Count} 路", TextSize = UiScale.Caption };
        count.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(count);
        count.SetSingleLine(true); // 走查令 2026-11-27：台数不竖排换行（定宽下面板外缘走省略号）
        count.Ellipsize = TextUtils.TruncateAt.End;
        titleRow.AddView(count);
        // 关闭 ×（老板 2026-11-27 20:2x 拍板「元素肯定都要」）：竖屏抽屉全屏后无面板外可点，
        // 页头右侧常驻 ✕ 关闭（与设置面板同款），返回键口径不变
        var titleStretch = new Space(this); // 台数与 ✕ 之间弹性撑开，✕ 顶到右缘
        titleRow.AddView(titleStretch, new LinearLayout.LayoutParams(0, 1, 1f));
        var drawerClose = new TextView(this) { Text = "✕", TextSize = UiScale.Title, Gravity = GravityFlags.Center };
        drawerClose.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(drawerClose);
        drawerClose.SetPadding(UiScale.Px(24), 0, 0, 0);
        drawerClose.Click += (_, _) => ShowPlayer();
        titleRow.AddView(drawerClose, new LinearLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.CenterVertical });
        header.AddView(titleRow);
        // 老板令 2026-11-16：共N路后面挂「实时水位」——内核当前攒着还没播的库存（BufferedMs 实时读数），
        // 1 秒独立泵跟刷，抽屉开着数字一直新鲜；vc85 施工包5 同行并入「画面时刻」（现在−水位换算，源头延迟可标定）
        _waterLevelText = new TextView(this) { TextSize = UiScale.Caption };
        _waterLevelText.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_waterLevelText);
        _waterLevelText.SetSingleLine(true); // 走查令 2026-11-27：水位行不竖排换行（宽度由 ApplyListWidth 页头项兜底，极端钳制走省略号）
        _waterLevelText.Ellipsize = TextUtils.TruncateAt.End;
        UpdateWaterLevel();
        header.AddView(_waterLevelText);
        _listPage.AddView(header);

        // 五分类标签行（vc53：页签内容自适应宽度，首页签 LeftMargin=0 与页头文字严格对齐；
        // 当前标签朱砂底高亮，点击/左右键切换）
        _tabViews.Clear();
        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabRow.SetPadding(UiScale.Px(48), UiScale.Px(24), UiScale.Px(48), UiScale.Px(12)); // #15 尺寸令
        int tabIndex = 0;
        foreach (ListTabs.Tab t in Enum.GetValues(typeof(ListTabs.Tab)))
        {
            var tv = new TextView(this) { Text = ListTabs.Label(t), TextSize = UiScale.Item };
            tv.SetSingleLine(true); // vc65 老板令：页签永不换行（「地方台」不再被挤折行），面板下限已并入页签行最小宽
            tv.SetPadding(UiScale.Px(12), UiScale.Px(10), UiScale.Px(12), UiScale.Px(10));
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.Click += (_, _) => SelectTab(t);
            tabRow.AddView(tv, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = tabIndex == 0 ? 0 : UiScale.Px(8) });
            _tabViews.Add(tv);
            tabIndex++;
        }
        StyleTabs();
        _listPage.AddView(tabRow);

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
        filterBox.SetBackgroundResource(0); // 去系统下划线底，走面板同色
        filterBox.SetBackgroundColor(Color.ParseColor("#223A4750")); // 半透明深底条
        filterBox.SetPadding(UiScale.Px(24), UiScale.Px(12), UiScale.Px(24), UiScale.Px(12));
        filterBox.TextChanged += (_, e) =>
        {
            if (_list?.Adapter is ChannelListAdapter adapter)
                adapter.SetFilter(e.Text?.ToString() ?? "");
        };
        _listPage.AddView(filterBox, new LinearLayout.LayoutParams(-1, -2) { LeftMargin = UiScale.Px(48), RightMargin = UiScale.Px(48), TopMargin = UiScale.Px(4), BottomMargin = UiScale.Px(8) });

        // v23 第 1 条：折叠总开关行整行退场——标签行下直接铺频道（vc53 口径）。
        _list = new ListView(this) { Divider = null };
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

    /// <summary>vc53 口径：退出确认弹窗开着=取消继续播放；列表开着=先收抽屉回画面（不强制起播，
    /// 当前台死流才补播，绝不让用户关掉列表面对黑屏）；都收着才弹退出确认。</summary>
    private void ExitOrCollapseList()
    {
        if (_exitDialog != null)
        {
            _exitDialog.Dismiss();
            _exitDialog = null;
            return;
        }
        if (_listOpen)
        {
            var alive = _manager.Current is { State: PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Reconnecting };
            ShowPlayer();
            if (!alive) PlayAt(_currentIndex);
            return;
        }
        ConfirmExit();
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

        // 退出确认弹窗开着时按键全接管：左右键在「取消/退出」间显式切换
        //（焦点切换不依赖系统焦点搜索，真机实测不可靠）；确认键=按当前焦点执行；返回键=取消继续播放（vc53 v1.0.9 口径）
        if (_exitDialog != null)
        {
            switch (keyCode)
            {
                case Keycode.Back:
                    _exitDialog?.Dismiss(); // vc53 v1.0.9 口径：返回键=取消弹窗，继续播放
                    _exitDialog = null;
                    return true;
                case Keycode.DpadLeft:
                    _exitNo?.RequestFocus();
                    return true;
                case Keycode.DpadRight:
                    _exitYes?.RequestFocus();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    if (_exitDialog.Window?.CurrentFocus is TextView focused) focused.PerformClick();
                    else _exitNo?.PerformClick(); // 焦点异常兜底：落「取消」绝不误退
                    return true;
                default:
                    return true; // 其余按键弹窗期间不穿透，防止误触列表/换台
            }
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
            // 铁律：任何时刻最多一个层——设置面板开着才允许收起；列表/弹窗/其他面板开着时设置键不生效
            if (_settingsPanelOpen) ToggleSettingsPanel();
            else if (_exitDialog == null && !_listOpen && _advOverlay == null && _licenseOverlay == null)
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

    /// <summary>退出确认弹窗（v1.0.9 用户拍板；vc22 水墨浮层样式整套搬运）：窗底透明+半透明水墨面板，
    /// 「退出」=朱砂细边框+朱砂极淡底，「取消」=纸白细边框+极淡底；焦点导航、默认焦点落「取消」、
    /// 返回键=第二次返回直接退出（vc53 v1.0.12 口径）。</summary>
    private void ConfirmExit()
    {
        if (_exitDialog != null) return; // 弹窗已开着：不重复弹
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧（退出弹窗也是窗口）
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        // 走查令 2026-11-27（D3 根治）：#71 重定基把 density 又乘进 Px()（2026-11-27 整改令后 PxScale 已含密度），
        // 弹窗间距被放大 ~2.6 倍（pad 80px≈30dp），按钮内宽被挤到装不下「取消（继续看）」竖排三行——去掉重复乘数，入参即 390dp 基准 dp 值
        int pad = UiScale.Px(11);
        box.SetPadding(pad * 2, pad * 2, pad * 2, pad);
        box.SetBackgroundColor(InkTranslucency.PanelColor(this)); // 半透明水墨底，与抽屉面板同源同号
        box.SetMinimumWidth(UiScale.Px(267)); // 居中浮层不因文案缩成窄条

        var msg = new TextView(this) { Text = "确认退出 TaoLive？", TextSize = UiScale.Title };
        msg.SetTextColor(InkTranslucency.TextPrimary(this)); // 统一 Token，暗屏自动提白
        InkTranslucency.ApplyReadabilityShadow(msg);
        box.AddView(msg);

        var note = new TextView(this) { Text = "退出后开机自启仍可用，随时回来看", TextSize = UiScale.Caption };
        note.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(note);
        box.AddView(note, new LinearLayout.LayoutParams(-2, -2) { TopMargin = UiScale.Px(4) }); // #71 重定基：旧观感 8÷1.8

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var lp = new LinearLayout.LayoutParams(0, -2, 1f) { TopMargin = pad, LeftMargin = pad / 2, RightMargin = pad / 2 };
        // vc22 第 11 项：按钮改 TextView 自绘——ROM Material 按钮的焦点高亮/系统着色从根上去掉
        var no = new TextView(this) { Text = "取消（继续看）", Gravity = GravityFlags.Center, TextSize = UiScale.Item }; // #68 语义字号令：弹窗按钮此前漏设字号吃系统默认
        no.SetPadding(pad / 2, UiScale.Px(8), pad / 2, UiScale.Px(8)); // #71 重定基：旧观感 14÷1.8
        no.SetSingleLine(true); // 走查令 2026-11-27：按钮文案单行（盒最小宽按按钮实测宽兜底，见下）
        no.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; };
        var yes = new TextView(this) { Text = "退出", Gravity = GravityFlags.Center, TextSize = UiScale.Item };
        yes.SetPadding(pad / 2, UiScale.Px(8), pad / 2, UiScale.Px(8)); // #71 重定基：旧观感 14÷1.8
        yes.SetSingleLine(true);
        yes.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; Finish(); };
        // 走查令 2026-11-27：盒最小宽并入按钮行实需——两钮权重均分等宽，按较宽文案算：
        // 盒宽 ≥ 2×较宽文案 + 8×pad（盒左右距 4×pad + 每钮内外距合计 4×pad），「取消（继续看）」不再被挤换行
        float btnRowNeed = 8f * pad + 2f * Math.Max(no.Paint!.MeasureText(no.Text!.ToString()), yes.Paint!.MeasureText(yes.Text!.ToString()));
        box.SetMinimumWidth(Math.Max(UiScale.Px(267), (int)btnRowNeed));
        // v1.0.10 修复④：左右键在「退出」与「取消（继续看）」间切换——显式指路，不靠焦点搜索碰运气
        no.Id = View.GenerateViewId();
        yes.Id = View.GenerateViewId();
        no.NextFocusRightId = yes.Id;
        yes.NextFocusLeftId = no.Id;
        _exitNo = no;
        _exitYes = yes;

        // vc22 第 11 项统一按钮样式：聚焦=效果层（退出朱砂/取消纸白）+主色字；常驻=水墨深底+次色字
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

        // 绑定契约 SetView/SetCancelable/Create 为可空返回：改语句式构造，窗口可空即放弃弹窗（实际不发生）
        var builder = new AlertDialog.Builder(this);
        builder.SetView(box);
        builder.SetCancelable(true);
        var dialog = builder.Create();
        if (dialog is null) return;
        _exitDialog = dialog;
        dialog.Show();
        // vc22 第 11 项：全屏自管——窗底透明+内容铺满，露出的是播放画面，与抽屉浮在画面上观感一致
        var win = dialog.Window!;
        win.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        win.SetLayout(-1, -1);
        win.SetFlags(WindowManagerFlags.LayoutInScreen, WindowManagerFlags.LayoutInScreen);
        no.RequestFocus(); // 默认焦点落「取消（继续看）」——防误退更稳
        dialog.SetOnCancelListener(new DialogActionPeer(() => { _exitDialog = null; _exitNo = null; _exitYes = null; }));
        dialog.SetOnDismissListener(new DismissActionPeer(() => { _exitDialog = null; _exitNo = null; _exitYes = null; }));
    }

    /// <summary>标准监听器适配（vc53 v1.0.9）：把 Android 弹窗回调接成普通 C# 动作，避免匿名监听类散落。</summary>
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

    /// <summary>订阅新收录频道并入频道列表（老板 2026-11-26 令「最好还能有新收录」）：
    /// 目录外主流台（省级卫视/CCTV/CGTN，IsMainstreamNewChannel 口径，订阅库已限额）接在表尾，编号顺延不变号；
    /// 列表已建则原适配器换表重建行（ItemClick 接线不动）。返回本次并入的新频道数。</summary>
    private int MergeSubscriptionChannels(bool silent)
    {
        var extra = SubscriptionSourceStore.Shared.GetNewChannels(_channels.Select(c => c.Name));
        if (extra.Count == 0) return 0;
        var merged = new List<Channel>(_channels);
        int number = merged.Max(c => c.Number);
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

    /// <summary>列表宽度定宽（老板 2026-11-27 20:2x 拍板）：竖屏固定全屏、横屏固定最大宽 480dp，不随内容/页签/收藏变。
    /// 沿革：vc65 恒定估宽（全表最长行兜底）；走查令 2026-11-27（D1/D2 根治）改行装配实式估宽+上限 0.98 屏——
    /// 间距重定标后行内固定件（台标/编号/收藏星/状态点/边距）实宽≈0.68 屏，贴内容估宽必然顶满上限，等效近全屏；
    /// 老板拍板不再「贴内容长」：竖屏全屏（信息一行看全），横屏定宽 480dp（页签行实需≈455dp 内收，长台名单行省略号）。
    /// 转屏在 OnConfigurationChanged 重算（定宽不依赖内容，转屏即定）。</summary>
    private void ApplyListWidth(IReadOnlyList<Channel> channels)
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        int width = landscape
            ? Math.Min(UiScale.Px(480), Resources.DisplayMetrics!.WidthPixels) // 横屏定宽 480dp，窄屏钳到屏宽
            : Resources.DisplayMetrics!.WidthPixels; // 竖屏固定全屏
        _listPage.LayoutParameters = new FrameLayout.LayoutParams(width, FrameLayout.LayoutParams.MatchParent, GravityFlags.Left);
    }

    /// <summary>列表页顶部安全区避让（2026-11-27 真机反馈：竖屏文字避开挖孔）：页头/页签/筛选框整体
    /// 下移到挖孔与状态栏之下，至少留 12dp 呼吸边。每次呼出重算（转屏/分屏后 inset 会变）。</summary>
    private void ApplyListTopInset()
    {
        int top = UiScale.Px(12);
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window!.DecorView!.RootWindowInsets is { } insets)
        {
            int cutout = insets.GetInsets(WindowInsets.Type.DisplayCutout()).Top;
            int status = insets.GetInsets(WindowInsets.Type.StatusBars()).Top;
            top = Math.Max(top, Math.Max(cutout, status));
        }
        _listPage.SetPadding(0, top, 0, 0);
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
            RequestedOrientation = ScreenOrientation.SensorPortrait; // 横屏退全屏回竖屏
            ShowToast("竖屏");
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

    /// <summary>信息条（vc53 InfoBar 口径）：顶部居中，自动隐藏；文案即给即显。
    /// #66：每次亮条重取自适应水墨皮——开关灯/调亮度后底色随之变化，不必等重建界面。</summary>
    private void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Background = InkTranslucency.HintBackground(this);
        _toast.Visibility = ViewStates.Visible;
        _main.RemoveCallbacks(_toastHide);
        _main.PostDelayed(_toastHide, 2200);
    }

    /// <summary>失败终态驻留空态（v77）：显示至离开失败态；样式照 vc53 水墨面板口径。
    /// 文案矩阵（方案书 v2.4 §25）单点：空态文案=常量，退级提示按等待时长叠加，不另开文案源。</summary>
    private void ShowEmptyState(string text = EmptyStateText)
    {
        _emptyState.Text = text;
        _emptyState.Visibility = ViewStates.Visible;
        // 长播无源退级提示（老板 2026-11-17 拍板，归文案项一并落）：空态满 5 分钟起如实报已等
        // 时长并建议检查网络——自愈慢轮+重探在后台自动复活，不用用户盯屏干等；离开空态即停表撤字。
        _emptyStateAtMs = System.Environment.TickCount64;
        _main.PostDelayed(_emptyHintTick, 30_000);
    }

    private void HideEmptyState()
    {
        if (_emptyState.Visibility != ViewStates.Visible) return;
        _emptyState.Visibility = ViewStates.Invisible;
        _main.RemoveCallbacks(_emptyHintTick);
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
                    rows.Add(new Row { Header = "收藏夹是空的，对着频道按确认可收藏", IsNote = true }); // v22 第 6/15 项
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

            // v22 第 6/15 项：装配完没有任何频道行 → 空态提示
            if (!rows.Any(r => r.Channel != null))
                rows.Add(new Row { Header = "该分类暂无频道", IsNote = true });
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
            public TextView Name = null!;
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
            var density = ctx.Resources!.DisplayMetrics!.Density;
            var h = new RowHolder { Line = new LinearLayout(ctx) { Orientation = Orientation.Horizontal } };
            h.Line.SetPadding(UiScale.Px(48), UiScale.Px(20), UiScale.Px(48), UiScale.Px(20)); // #15 尺寸令：行高随档

            h.Header = new TextView(ctx);
            h.Header.Ellipsize = TextUtils.TruncateAt.End; // vc65：面板钳到半屏等极端场景走省略号，不折行
            InkTranslucency.ApplyReadabilityShadow(h.Header);
            h.Line.AddView(h.Header);

            h.Logo = new ImageView(ctx);
            // #61 尺寸令补漏：台标高度与首字占位块同框（40×28 基准×档位系数），ImageView 默认 FitCenter 框内等比不变形
            h.Line.AddView(h.Logo, new LinearLayout.LayoutParams((int)(40 * density * UiScale.Scale), (int)(28 * density * UiScale.Scale))
            { RightMargin = UiScale.Px(16), Gravity = GravityFlags.CenterVertical });

            h.Placeholder = new TextView(ctx);
            Style(h.Placeholder, InkTranslucency.TextSecondary, UiScale.Item);
            h.Placeholder.Gravity = GravityFlags.Center;
            h.Placeholder.SetBackgroundColor(Color.ParseColor("#3A4750"));
            h.Line.AddView(h.Placeholder, new LinearLayout.LayoutParams((int)(40 * density * UiScale.Scale), (int)(28 * density * UiScale.Scale)) // #15 尺寸令
            { RightMargin = UiScale.Px(16), Gravity = GravityFlags.CenterVertical });

            h.Spacer = new View(ctx);
            h.Line.AddView(h.Spacer, new LinearLayout.LayoutParams(0, 1) { RightMargin = UiScale.Px(16) }); // 无信号行行首不放台标（v4 定稿状态2）

            h.Num = new TextView(ctx) { Gravity = GravityFlags.Right };
            Style(h.Num, InkTranslucency.TextSecondary, UiScale.Item);
            InkTranslucency.ApplyReadabilityShadow(h.Num);
            h.Num.SetPadding(0, 0, UiScale.Px(32), 0);
            h.Line.AddView(h.Num);

            h.Star = new TextView(ctx);
            Style(h.Star, "#F0C563", UiScale.Item);
            InkTranslucency.ApplyReadabilityShadow(h.Star);
            h.Star.SetPadding(0, 0, UiScale.Px(24), 0);
            h.Line.AddView(h.Star);

            h.Name = new TextView(ctx);
            h.Name.Ellipsize = TextUtils.TruncateAt.End; // vc65：半屏钳制兜底，宁省略不折行
            InkTranslucency.ApplyReadabilityShadow(h.Name);
            h.Line.AddView(h.Name);

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

            h.Dot = new TextView(ctx);
            h.Dot.SetPadding(UiScale.Px(24), 0, 0, 0);
            InkTranslucency.ApplyReadabilityShadow(h.Dot);
            h.Line.AddView(h.Dot);
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
                h.Name.Visibility = ViewStates.Gone;
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
            h.Line.SetBackgroundColor(isCur
                ? InkTranslucency.CurrentRowFill(ctx) // v1.0.9 第③项：当前台整行强调底
                : Color.Transparent);
            h.Name.Visibility = ViewStates.Visible;

            // 路源标（老板 2026-11-27 20:2x 拍板「元素肯定都要」）：显示该台源路数；0 路（异常态）不显
            var srcCount = ch.SourceEndpoints.Count;
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
