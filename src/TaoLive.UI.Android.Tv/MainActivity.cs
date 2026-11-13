using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
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
    Label = "道映",
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
    private int _playSeq;
    private Channel? _current;
    private AiLiveTier _aiTier;         // AI 画质档位（启动时按设备解析）
    private bool _carrierTexture;      // 本次会话是否纹理载体（v1.0.7 启动自检决定）
    private bool _frameForCurrentSwitch; // 本轮换台是否已出首帧
    private Action? _pendingLoading;   // 延迟亮牌任务（换台后仍无首帧才亮）
    private Action? _loadingAnimRun;   // 亮牌期间的「正在连接」点动画
    private int _loadingSeq;
    private Handler _ui = new Handler(Looper.MainLooper!);

    // ---- 双实例无感切换（v1.0.10【六】【九】A）：新流亮了再换，档位变化绝不重起当前台 ----
    private readonly HotSwapCoordinator _hotSwap = new();
    private HotSwapSession? _swapSession;                    // 未决切换会话
    private AiLiveTier? _pendingTier;                        // 待提交的新档位（提交成功才落 _aiTier）
    private global::Android.Views.View? _mainVideoView;              // 主画面载体（提交时被第二实例载体顶替）
    private global::Android.Views.View? _pendingSwapCarrier;         // 第二实例载体（已入视图树，暂不可见）
    private Action? _swapTimeoutRun;                         // 切换超时兜底任务
    private IReadOnlyList<QualityVariant> _currentVariants = Array.Empty<QualityVariant>(); // 当前台多档变体（空=单档）
    private int _currentVariantHeight;                       // 当前在播变体的分辨率高
    private string? _lastResolvedUrl;                        // 最近一次成功取流的地址（档位切换沿用）
    private SmartGovernor? _governor;                        // ⑨A自动档实时调节（本台会话）
    // fix25（老板拍板）：帧率稳档观察——升档后 60 秒观察窗，稳不住自动退回原档；升档门槛加严防拉锯
    private readonly FrameRateKeeper _keeper = new();
    private System.Threading.Timer? _governorTicker;         // ⑨A巡检（5 秒节拍，口径同 SmartGovernor）
    private int _governorTickInFlight;                       // fix50：巡检节拍互斥——节拍改投线程池后不再经 UI 串行化，用在途标志防重入

    // 退出确认（v1.0.9 用户拍板）：播放态按返回弹窗二次确认；确认后真完全关闭，
    // 重开是全新冷启动。弹窗开着时返回键=第二次返回，直接退出（v1.0.12 老板拍板）。
    private AlertDialog? _exitDialog;
    // v1.0.12（老板反馈）：弹窗按钮显式持引用——焦点切换由按键接管，不靠系统焦点搜索碰运气
    private Button? _exitNo;
    private Button? _exitYes;
    private bool _resumedOnce;  // 首启 OnResume 与回前台重接的分流（v1.0.9）

    // 收藏排序模式（v1.0.8 第14项，用户 23:23 拍板）：独立状态机，退出恢复默认键位
    private bool _sortMode;
    private Channel? _sortChannel;
    private readonly List<int> _sortBackup = new(); // 返回键取消复原用

    // 软件设置面板（v1.0.8，用户拍板）：底部滑入透明面板；设置键 4077 单击呼出、长按跳系统设置
    private LinearLayout? _settingsPanel;
    private bool _settingsPanelOpen;
    private bool _settingsLongFired;
    private bool _precheckEnabled = true;   // 循环预检开关（设置面板可切，持久化）
    // v1.0.16 抢流与网络优化（v3.1 冻结稿）：B 换台探测竞速（默认开）、D 并行抢首包（默认关，设置页开关）
    private bool _rushEnabled = true;
    private bool _firstPacketEnabled = false;
    private bool _firstPacketPending;        // 抢首包陪跑会话在途（提交/放弃时清）
    private readonly RacePreconnector _racer = new();
    private Action? _firstPacketCutRun;      // 陪跑 3 秒即断的收口回调
    // fix31 长窗兜底断路（冻结稿 v2）：10 分钟窗口三类兜底事件累计过频→90 秒观察期→连 3 轮仍全败按「全源不可达」收口
    private readonly FallbackLoopBreaker _loopBreaker = new();
    // v1.0.20 B 终局出口：自动跳台一圈封顶——绕满一圈无一成功即停跳亮「信号源暂时不可用」，绝不兜圈死循环
    private readonly AutoHopGuard _autoHop = new();
    private Action? _observeUnpauseRun;      // 观察期 90 秒到期恢复自动链路的回调（换台即撤）
    private bool _loopClosed;                // 断路收口只报一次（换台复位）
    private System.Threading.Timer? _resourceTicker; // fix31 ③：每 5 分钟资源观测日志（内存/缓冲水位留底）
    // 老板 2026-11-08 拍板（连接优先）：开机即连/切台即连第一位——没连上之前冷启动（预检）暂停，连成之后才开始；
    // 切台必停、切完后启。true=允许预检循环跑，false=暂停等待
    private volatile bool _precheckGate;
    private bool _abi32;                     // 启动时设备档位（画质档重算用）
    private long _totalMem;
    private global::Android.Content.ISharedPreferences? _prefs;
    private System.Threading.CancellationTokenSource? _precheckCts;

    // 频道列表折叠状态（v1.0.8）：组标题 → 展开？；未初始化时收藏组+当前台所在组自动展开
    private readonly Dictionary<string, bool> _expanded = new();
    private bool _expandedInit;

    // v1.0.9 第④项：列表旁操作提示去掉，改为实时画面信息行
    // v1.0.12（老板拍板）：动态信息拆三行独占（画质最上、依次分辨率、帧率），行高锁死提前占位
    private TextView? _qualityLine;
    private TextView? _resLine;
    private TextView? _fpsLine;
    private System.Threading.Timer? _statsTicker;
    // fix24：热切冷却拒受日志限流（最多一次/分钟，退避期间不刷屏）
    private long _lastCooldownLogMs = long.MinValue / 2;
    // v1.0.9 第⑦/⑪项：重建列表的焦点锚点（分类键或 C:频道号），锚定原位置不跳头
    private string? _pendingFocusAnchor;
    // v1.0.9 第⑬项：本台会话实际取播的来源变体（健康表优先取最近验证可用来源）
    private Channel? _playSource;
    // fix23：重连最近一次取用的链路——再次重连时若同链路复断，先作废再换下一候选
    private Channel? _lastRefreshSource;
    // v1.0.10 第①项：源健康账本地存储（应用私有目录，跨启动保留已验证源）
    private SourceHealthStore? _healthStore;
    // v1.0.20 批1（方案 A/E）：播放实测首帧延迟记账——Play 起记起点，首个首帧顺路入健康账（EMA）
    private long _playStartTicks;
    private bool _awaitFirstFrame;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        TaoPalette.Select(0); // 水墨默认

        // 全局兜底：未捕获异常先落日志再放行，崩溃原因可查可报，不再无声闪退（v1.0.7）
        Java.Lang.Thread.DefaultUncaughtExceptionHandler =
            new UncaughtCrashPeer(Java.Lang.Thread.DefaultUncaughtExceptionHandler);

        // BLL 日志通道接入现有崩溃日志：业务层吞点日志与 UI 同账（v1.0.10【八】口径收口）
        BllLog.SetSink(new DelegateBllLogSink(CrashNet.Record));

        // 低配设备档位：32 位芯片或内存不足 3GB 软解带不动 4K/8K，超高清台直接不进节目单（雷一：带不动就别点）
        _abi32 = Build.SupportedAbis?.Any(a => a == "armeabi-v7a") == true;
        var am = (ActivityManager)GetSystemService(ActivityService)!;
        var mem = new ActivityManager.MemoryInfo();
        am.GetMemoryInfo(mem);
        _totalMem = mem.TotalMem;
        DeviceProfile.HideUhd = _abi32 || mem.TotalMem < 3L * 1024 * 1024 * 1024;

        // 持久化设置（v1.0.8）：AI 画质档位与预检开关跨启动记忆
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        DeviceProfile.AiEnhanceTierSetting = _prefs.GetString("ai_tier", "auto") ?? "auto";
        _precheckEnabled = _prefs.GetBoolean("precheck_enabled", true);
        _rushEnabled = _prefs.GetBoolean("rush_enabled", true);              // v1.0.16 方案 B
        _firstPacketEnabled = _prefs.GetBoolean("firstpacket_enabled", false); // v1.0.16 方案 D

        // AI 画质实时档（v1.0.8）：auto 按内存分档，绝不在带不动的设备硬开
        _aiTier = LiveQuality.Pick(DeviceProfile.AiEnhanceTierSetting, _abi32, _totalMem);

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
        _catalog.Rebuild();
        StreamResolver.MemAvailProvider = () => // H 内存档自适应：池并发按可用内存档（BLL 纯逻辑判定）
        {
            var hMem = new ActivityManager.MemoryInfo();
            ((ActivityManager)GetSystemService(ActivityService)!).GetMemoryInfo(hMem);
            return hMem.AvailMem;
        };
        _resolver = new StreamResolver();
        // G2 跨源兜底链接线（冻结稿 v3.1，老板追加令+修正令）：同名台全部官方源进链参选，
        // 链序=健康评分降序（央视频不再唯一支柱）；链内不排用户自有源（修正令），执行走既有备援口径
        _resolver.ChainProvider = ch => _catalog.All
            .Where(c => !c.Unavailable && SameChannel(c, ch) && c.SourceId != ch.SourceId)
            .ToList();
        _resolver.ChainScore = c => SourceScorer.Score(_precheck.SourceHealthOf(c), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        CheckStartupSelfClean(); // v1.0.20 F：上次未正常退出（崩溃/被杀）→ 清取流缓存等可疑状态
        _host = new LiveVideoHost { TotalMemBytes = _totalMem }; // ⑩内存水位自适应口径注入
        WireHostEvents();

        // 开机预检握手（v1.0.8）：全台按序轻探（取票/头分片，不建视频传输），限速防判异常。
        // 预检后台进行，不阻塞当前频道播放；状态实时刷新进呼出列表。
        // v1.0.13（老板 15:42 拍板）：预热在后台跑可以不用那么快——探测节拍 600→1000ms 让路前台切台，
        // 探活顺路预取播放清单入缓存（能压的都要压），换台读缓存不现场等网络
        _precheck = new ChannelPrecheck(ProbeChannelAsync, intervalMs: 1000);
        // 老板 2026-11-08 拍板（连接优先 + 续接循环）：每个台节点前等闸——连接优先时冷启动就地挂起，
        // 连成后从暂停点接着往下探（不从头重转）；首连成功（首帧）前闸不开，冷启动一步都不跑
        _precheck.PauseGate = async ct =>
        {
            while (!_precheckGate && !ct.IsCancellationRequested)
                await Task.Delay(1000, ct);
        };
        // v1.0.10 第①项：源健康账落盘本地——重启后已验证源直接可用，换台不用从零再探
        // fix52（B 路）：健康账读盘+反序列化移出主线程（冷启动不等磁盘，条目增长不再拖慢开机）——
        // 挂账保持同步（只设引用，探活写入不落空）；就绪前健康表为空兜底：换台走预检重探，语义等价；
        // 健康账为并发字典，Load 的 TryAdd 不覆盖会话内新条目（既有口径），与探活并发安全
        _healthStore = new SourceHealthStore(FilesDir!.AbsolutePath);
        _precheck.AttachHealthStore(_healthStore);
        var bootStore = _healthStore;
        Task.Run(() =>
        {
            try { bootStore.Load(); }
            catch (Exception ex) { CrashNet.Record("[Boot] 源健康账后台加载异常（空表兜底，交回预检重探）：" + ex.Message); }
        });
        // v1.0.13 缓存分层：档位目录落盘（24 小时）——重启后各台档位免重抓，换台零网络等待
        VariantTableStore.Shared = new VariantTableStore(FilesDir.AbsolutePath);
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
            // 开机即看 + 老板 2026-11-08 拍板（连接优先）：起播前等网络就绪再连——没连上之前冷启动暂停，连成之后才开始；
            // 等网络期间亮「正在连接网络」呼吸点，不黑屏干等（老板补充拍板）
            ShowBootLoading();
            _ = Task.Run(async () =>
            {
                await WaitNetworkReadyAsync(10_000);
                _ui.Post(() =>
                {
                    // 修正②（推演）：等网络期间用户可能已抢先选台——有台在播就只撤亮牌，不拿默认台顶掉用户的选择
                    if (_current is null) Play(DefaultChannel());
                });
            });
            // 循环预检（v1.0.8 用户拍板）：常驻后台轮，保态复测列表不闪烁；
            // v1.0.13 节拍 1000ms 让路前台切台：83 路 ≈ 83 秒/轮 + 轮间歇 20 秒 ≈ 1.5~2 分钟一轮（限速防判异常流量）；
            // 票据静默续期：每 4 分钟为当前台换新票
            _precheckCts = new System.Threading.CancellationTokenSource();
            _ = Task.Run(() => PrecheckLoopAsync(_precheckCts.Token));
            // v1.0.9 第④项：画面信息行实时刷新（2 秒节拍，列表开着才写字）
            _statsTicker = new System.Threading.Timer(_ => _ui.Post(() =>
            {
                if (_qualityLine is null) return;
                var (ofps, cfps, ow, oh, cw, chh) = _host.SnapshotStats();
                // v1.0.10 修复⑤口径沿用：到限显「--」；v1.0.12 拆三行只换字不动结构
                bool exhausted = _host.StatsExhausted;
                // v1.0.20 ⑪画质档如实：显示实际生效档（SmartGovernor 降级/<2GB 强降透出），如实不吃安慰剂；
                // 同行右侧追加内存段（ActivityManager.MemoryInfo，缺失省略不挡播放）
                string memText = "";
                try
                {
                    if (GetSystemService(global::Android.Content.Context.ActivityService) is global::Android.App.ActivityManager am)
                    {
                        var mi = new global::Android.App.ActivityManager.MemoryInfo();
                        am.GetMemoryInfo(mi);
                        memText = FrameStatsLine.MemorySegment(mi.TotalMem, mi.AvailMem);
                    }
                }
                catch { memText = ""; }
                string settingName = LiveQuality.DisplayName(DeviceProfile.AiEnhanceTierSetting);
                string effectiveName = LiveQuality.TierName(_governor?.Effective ?? _aiTier);
                _qualityLine.Text = FrameStatsLine.QualityLineEx(settingName, effectiveName, memText);
                _resLine!.Text = FrameStatsLine.ResLine(ow, oh, cw, chh, exhausted);
                _fpsLine!.Text = FrameStatsLine.FpsLine(ofps, cfps, exhausted);
            }), null, 2000, 2000);
            _ticketKeeper = new System.Threading.Timer(async _ =>
            {
                var cur = _current;
                if (cur is null) return;
                try { await _resolver.ResolvePlayUrlAsync(cur, force: true); }
                catch (Exception ex) { CrashNet.Record($"[TicketKeeper] 续票失败下个周期再试：{ex.Message}"); }
            }, null, 240_000, 240_000);
            // ⑨A自动档巡检（v1.0.10 双实例落地）：5 秒节拍喂实测，档位变化走双实例无感切换
            // fix50（老板 2026-11-13 拍板「同类问题不用问、加快速度」）：节拍改投线程池线程——
            // 判活/帧率快照/自动档判定的原生重活整体离开 UI 线程；要动界面的动作经 _ui.Post 回 UI
            _governor = new SmartGovernor(_aiTier);
            _governorTicker = new System.Threading.Timer(_ => TickGovernor(), null, 5000, 5000);
            // fix31 ③：资源观测日志——每 5 分钟一条内存/缓冲水位（载体回退重建不重复挂表）
            if (_resourceTicker == null)
                _resourceTicker = new System.Threading.Timer(LogResourceWatermark, null, 300_000, 300_000);
        }
        catch (Exception ex)
        {
            // 纹理载体接线当场报错：记录原因，永久回退稳定载体，同一次启动内重建，不闪退
            CrashNet.Record(ex.ToString());
            CarrierPolicy.DisableTexture(this, "启动接线异常: " + ex.Message);
            try
            {
                try { _host.Dispose(); } catch { }
                _host = new LiveVideoHost { TotalMemBytes = _totalMem };
                WireHostEvents();
                _carrierTexture = false;
                BuildUi(false);
                // fix31 ③：载体回退重建路径同样挂上资源观测日志（防重复挂表）
                if (_resourceTicker == null)
                    _resourceTicker = new System.Threading.Timer(LogResourceWatermark, null, 300_000, 300_000);
                Play(DefaultChannel());
                Toast.MakeText(this, $"画面载体已自动回退稳定模式（{ex.Message}）", ToastLength.Long)!.Show();
            }
            catch (Exception fatal)
            {
                CrashNet.Record("兜底回退仍失败: " + fatal);
                throw;
            }
        }
    }

    /// <summary>播放宿主事件接线（载体重建时随宿主重挂）。</summary>
    private void WireHostEvents()
    {
        // 重连前重新取票（v1.0.8）：换台代数守卫在宿主内，这里只管拿新鲜入口
        // ⑬联动：优先取本台会话实际使用的来源变体（健康表最近验证可用者）
        // fix23（老板思路）：重连循环每次只走新鲜链路——上一条链路又断了就立即作废
        //（缓存清除 + 健康表判坏），换健康表里下一个候选来源重新取票；作废的来源由
        // 预检循环周期复测，探活通过自动恢复候选资格。绝不对同一条失败链路反复重试。
        _host.ReconnectRefresher = async () =>
        {
            var cur = _current;
            if (cur is null) return null;
            // fix31：重连换候选先过长窗断路——观察期/收口不再换候选（宿主沿用原链路保持画面），
            // 触达上限进观察期（一次性提示），连 3 轮观察仍全败按「全源不可达」收口；正常放行走现行链路
            var verdict = _loopBreaker.OnFallbackEvent(System.Environment.TickCount64);
            if (verdict == FallbackLoopBreaker.Step.ObserveEnter) { _ui.Post(EnterObserve); return null; }
            if (verdict == FallbackLoopBreaker.Step.ObserveHold) return null;
            if (verdict == FallbackLoopBreaker.Step.Exhausted) { _ui.Post(() => CloseLoopAsNoSignal(cur)); return null; }
            if (_lastRefreshSource is { } last)
            {
                _resolver.Invalidate(last);
                _precheck.ReportSourceResult(last, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                CrashNet.Record($"[Reconnect] 链路复失败作废：来源={last.SourceId}（{last.Name}）——重连改走下一候选");
            }
            _lastRefreshSource = null;
            // 候选来源：健康表里最活的来源（刚判坏的自动落选）；无备选时回退本台原始来源
            var source = _precheck.BestAliveSource(cur, _catalog.All.ToList()) ?? cur;
            if (!ReferenceEquals(source, _playSource) && source.SourceId != _playSource?.SourceId)
                CrashNet.Record($"[Reconnect] 换源重连：{_playSource?.SourceId ?? "无"} → {source.SourceId}（{cur.Name}）");
            _playSource = source;
            var fresh = await _resolver.ResolveWithChainAsync(source, force: true); // G2：重连沿健康链换源
            if (fresh is null)
            {
                CrashNet.Record($"[Reconnect] 候选取流全失败（{cur.Name}）——播放器重试余量耗尽后如实收口");
                return null;
            }
            _lastRefreshSource = fresh.Channel;
            var opts = new List<(string Key, string Value)>(fresh.Options);
            foreach (var (k, v) in LiveQuality.OptionsFor(_aiTier)) opts.Add((k, v));
            return new RefreshedStream { Url = fresh.Url, Options = opts };
        };
        // ⑫联动：降清晰度=AI 增强降一档（软解负载减轻）；无档可降时自救并入静默重取流
        _host.HasLowerQuality = () => DeviceProfile.AiEnhanceTierSetting switch
        {
            "high" => true,   // 高→低
            "low" => true,    // 低→关
            _ => _aiTier > AiLiveTier.Off, // auto 按实设备档判
        };
        _host.LowerQualityRequested += () =>
        {
            if (DeviceProfile.AiEnhanceTierSetting == "high") DeviceProfile.AiEnhanceTierSetting = "low";
            else if (DeviceProfile.AiEnhanceTierSetting == "low") DeviceProfile.AiEnhanceTierSetting = "off";
            else if (_aiTier > AiLiveTier.Off) _aiTier--;
            _prefs?.Edit()?.PutString("ai_tier", DeviceProfile.AiEnhanceTierSetting)?.Apply();
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
                // v1.0.13（老板拍板）：换台静默窗内的重连提示合并——连切不再连环刷「信号中断 2 秒后重连」，
                // 窗口过后按最终状态照常提示；真正的信号中断（非换台造成）不受影响
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _switchQuietUntil) FlashInfo(text);
            }
            else
            {
                FlashInfo(text);
            }
        });
        _host.FrameShown += () => _ui.Post(() =>
        {
            _frameForCurrentSwitch = true;
            _precheckGate = true;              // 老板 2026-11-08 拍板（连接优先）：连成之后冷启动才开始
            _autoHop.OnSuccess();              // v1.0.20 B：首帧成功即清终局账，自动跳台恢复正常额度
            if (_carrierTexture) CarrierPolicy.MarkOk(this); // 纹理载体活到首帧：判兼容
            if (_current != null) _precheck.MarkState(_current, ChannelHealth.Alive); // 播放实测存活
            _videoContainer?.SetBackgroundColor(Color.Transparent); // ⑦新流首帧到齐：撤底色亮画面
            // fix31：观察期/收口期间画面真的回来了——解除暂停并随台清账（成功即免）。
            // 不清会让健康流一直被锁在静默里（后续卡死无人自救）；清账后再次风暴重新计数，口径不失真
            if (_host.RescuePaused)
            {
                _host.RescuePaused = false;
                ClearObserveUnpause();
                if (_loopBreaker.IsBlocking(System.Environment.TickCount64)) _loopBreaker.OnChannelChanged();
                _loopClosed = false;
                CrashNet.Record("[FallbackLoop] 画面恢复（首帧落地），解除暂停并随台清账");
            }
            // ⑬健康表回写：播放实测成功覆盖预检结论（取播来源变体优先；Unix 毫秒，落盘可比）
            // 批1 A/E：首帧顺路记延迟（Play→首帧全程，EMA 入账）；仅起播窗口记，
            // 稳播期换档/热切帧不污染延迟账（_awaitFirstFrame 记一次即收）
            if (_playSource != null)
            {
                int? delay = _awaitFirstFrame
                    ? (int)Math.Clamp(System.Environment.TickCount64 - _playStartTicks, 0L, int.MaxValue)
                    : null;
                _awaitFirstFrame = false;
                _precheck.ReportSourceResult(_playSource, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), delay);
            }
            HideLoading();
            _ = UpgradeAfterFirstFrameAsync(); // 【六】先亮后升：最快档已亮，后台无感升最高档（双实例）
        });

        // 双实例无感切换（【六】【九】A）：新流亮了再换——提交前旧画面原样保留，绝不黑屏闪卡
        _host.SwapReady += () => _ui.Post(CommitHotSwap);
        _host.SwapFailed += () => _ui.Post(() => AbortHotSwapUi("第二实例起流失败（连接被拒或流不可用），留当前档，热切进入冷却"));

        // fix29（首帧兜底）：8 秒无首帧阶梯走尽（重取流→降清晰度→重启内核）仍黑屏——
        // CCTV-7 类盲区：预检全通、内核无声不报、锁死在连接态。这里收口：判死本台来源、
        // 放开冷启动探活、强制走「判死跳台」，绝不无限卡在黑屏。
        // fix31：判死跳台前先过长窗断路——10 分钟窗口兜底过频进观察期（停判死跳台，保持画面，
        // 换台即退），连 3 轮观察仍全败按「全源不可达」收口；正常放行走现行链路，绝不把台连死。
        _host.FirstFrameExhausted += () => _ui.Post(() =>
        {
            var cur = _current;
            if (cur is null || _frameForCurrentSwitch) return; // 已连上/换台了：作废
            var verdict = _loopBreaker.OnFallbackEvent(System.Environment.TickCount64);
            if (verdict == FallbackLoopBreaker.Step.ObserveEnter) { EnterObserve(); return; }
            if (verdict == FallbackLoopBreaker.Step.ObserveHold) { CrashNet.Record("[FallbackLoop] 观察期内：首帧兜底判死跳台暂停，保持当前画面"); return; }
            if (verdict == FallbackLoopBreaker.Step.Exhausted) { CloseLoopAsNoSignal(cur); return; }
            CrashNet.Record($"[FirstFrame] 首帧等待耗尽（{cur.Name}）：判死本台来源，放开探活自动跳台");
            if (_playSource != null) _precheck.ReportSourceResult(_playSource, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _precheckGate = true;
            OnCurrentDeadByPrecheck(cur.EpgKey, force: true);
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
            _mainVideoView = videoView;
            _host.Attach(videoView);
        }
        else
        {
            var videoView = new LiveSurfaceView(this) { LayoutParameters = new FrameLayout.LayoutParams(-1, -1) };
            _videoContainer.AddView(videoView);
            _mainVideoView = videoView;
            _host.Attach(videoView);
        }
        _root.AddView(_videoContainer);

        // 1.5 起播亮牌（雷二·延迟亮）：换台后 0.8 秒内没出首帧才亮，亮了带台名和呼吸点，
        // 明确反馈「没死机」，首帧落地才撤
        _loadingPanel = new FrameLayout(this);
        _loadingPanel.SetBackgroundColor(InkTranslucency.LoadingColor(this)); // v1.0.9 第③项：底色随亮度自适应
        _loadingText = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = 24,
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
        _infoBar.SetPadding(36, 16, 36, 16);
        _infoBar.TextSize = 22;
        _root.AddView(_infoBar, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = 48,
        });

        // 3. 频道列表（菜单键呼出，焦点式；分类树形可折叠+收藏置顶+宽度随内容）
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
    /// fix29：force=true 供首帧兜底强制跳台——无首帧时宿主 IsAlive 可能为真，不能挡住跳台。
    /// v1.0.20 B 终局出口：距上次成功累计绕满一圈仍无一成功 → 停跳，亮「信号源暂时不可用」，
    /// 不再兜圈死循环（真机日志实锤的央1→央4 兜圈本体）。
    /// </summary>
    private void OnCurrentDeadByPrecheck(string epgKey, bool force = false)
    {
        if (_current is null || _current.EpgKey != epgKey) return;
        if (!force && (_frameForCurrentSwitch || _host.IsAlive)) return; // 用户在看：看门狗接管，不打扰
        var all = _catalog.All;
        if (all.Count == 0) return;
        var list = all.ToList();
        var idx = list.FindIndex(c => c.EpgKey == epgKey);
        for (int i = 1; i <= list.Count; i++)
        {
            var cand = list[((idx + i) % list.Count + list.Count) % list.Count];
            if (_precheck.Of(cand) == ChannelHealth.Dead) continue;
            if (cand.EpgKey == epgKey) break; // 绕一圈只剩自己：全死
            if (!_autoHop.RecordHop(list.Count))
            {
                FlashInfo("信号源暂时不可用"); // v1.0.20 B：一圈封顶，终局如实提示
                CrashNet.Record($"[AutoHop] 绕满一圈无一成功（起点 {epgKey}），停跳亮终局提示");
                return;
            }
            FlashInfo($"{_current.Name} 无信号，切到 {cand.Name}");
            Play(cand, autoHop: true);
            return;
        }
        FlashInfo("无信号");
    }

    /// <summary>
    /// 起播。autoHop=true 表示本次 Play 来自自动跳台链路（OnCurrentDeadByPrecheck）——
    /// v1.0.20 B：该链路在跳前已记账，这里不清终局账；用户手动选台（默认 false）即清账重新给额度。
    /// </summary>
    private void Play(Channel? ch, bool autoHop = false)
    {
        if (ch is null) return;
        if (!autoHop) _autoHop.OnSuccess(); // v1.0.20 B：用户手动选台即清终局账
        // v1.0.13 去抖联动：非连切路径起播（列表/数字键/无信号自动跳台）时作废待发的连切任务
        if (_pendingSwitch is { } ps) { _ui.RemoveCallbacks(ps); _pendingSwitch = null; _debouncer.Clear(); }
        // 修正①（推演）：重连轮数不再在这里清零——它跟着播放任务记账，真正换台才重起（见 ScheduleConnectRetry）；
        // 否则重连自己也走 Play，3 轮永远数不满，真死台逃逸失效
        // ⑬播放选源（v1.0.9）：同频道多来源优先取最近验证可用的来源变体（来源级健康表），
        // 无已验证来源回到本台本身；任一来源通=频道可用，全挂才进无信号组。
        var source = _precheck.BestAliveSource(ch, _catalog.All.ToList()) ?? ch;
        _playSource = source;
        // 批1 A/E（冻结稿）：播放实测天然经过一次 HTTP 往返，顺路记时长——起点在起播请求前，首个首帧落地即入账
        _playStartTicks = System.Environment.TickCount64;
        _awaitFirstFrame = true;
        _precheck.MarkUsed(source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // v1.0.16 方案 A：最近使用入账（评分衰减基准）
        // fix31：真正换台时长窗断路随台清账（计数清零、退出观察期/收口态、恢复自动链路）；
        // 重连同台不清——失败循环总量照旧累计，断路口径不被重连重置绕过
        if (_current is { } prevCh && prevCh.EpgKey != ch.EpgKey)
        {
            _loopBreaker.OnChannelChanged();
            _loopClosed = false;
            _host.RescuePaused = false;
            ClearObserveUnpause();
        }
        int seq = ++_playSeq;
        _current = ch;
        _lastRefreshSource = null; // fix23：换台清重连链路状态，不带旧台的作废记录
        _frameForCurrentSwitch = false;
        // 老板 15:38 拍板（v1.0.13）：切换立刻停止上一个流——连接只管当前台，不保留旧流，
        // 绝不让旧台连接与当前台抢资源；同时静默换台期内的重连提示（只报最终状态）
        try { _host.Stop(); } catch (Exception ex) { CrashNet.Record($"[Switch] 停旧流异常照常起播：{ex.Message}"); }
        _switchQuietUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
        // 【六】【九】A：换台即收口未决切换（旧台的第二实例作废，新台从冷启动路径正常起播），
        // 自动档会话随台重建——旧台的档位调节节奏不带入新台
        AbortHotSwapUi("换台");
        _hotSwap.OnChannelChanged();
        // 老板 2026-11-08 拍板（连接优先）：切台必停、切完后启——切台期间冷启动暂停，新台连成（首帧）后恢复
        _precheckGate = false;
        _keeper.Reset(); // fix25：换台清稳档观察窗（新台新算）
        _currentVariants = Array.Empty<QualityVariant>();
        _currentVariantHeight = 0;
        _governor = new SmartGovernor(_aiTier);
        CloseChannelList();
        // v1.0.10 修复⑦：撤旧画面的同时铺干净底色，GPU 残留旧缓冲绝不外露（花屏竖条）；
        // 新流首帧到齐才撤底色亮画面（FrameShown 处清回透明）
        _videoContainer?.SetBackgroundColor(Color.ParseColor(TaoPalette.Current.Bg));
        // 老板 15:38 拍板：旧流已立即停，无旧画面可留——换台即亮「正在连接」，统一台号口径
        CancelPendingLoading();
        ShowLoading(ch);
        ShowInfoBar($"{ch.Number}  {ch.Name}  连接中…");

        var resolver = _resolver;
        Task.Run(async () =>
        {
            ResolvedStream? resolved = null;
            // v1.0.16 方案 B（换台探测竞速，默认开）：同频道候选按评分前 2~3 路并行轻探（控制层短连接，
            // 不算播放连接），1.5s 总窗口择先胜出者直取；全败/作废/被拒一律退回下面现行取流链路，绝不把台连死
            if (_rushEnabled)
            {
                var raced = await RaceSwitchSourceAsync(source, ch, seq).ConfigureAwait(false);
                if (raced is not null && seq == _playSeq)
                {
                    source = raced;
                    _playSource = source;
                    _precheck.MarkUsed(source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    CrashNet.Record($"[Race] 竞速胜出直取：{source.SourceId}（{ch.Name}）");
                }
            }
            try
            {
                // v1.0.10 第①项：优先直连健康账里已验证的来源；取不到流时若换过源，
                // 现场回退本台原始来源现取新流兜底——缓存只加速，绝不把台连死
                // fix28（老板拍板「开机即连/切台即连第一位」）：首连没接上先快速短重试——2 秒间隔再试 2 次
                //（网络刚通/取票抖动最常见，网络级失败已不进 30 秒冷却）；服务器明确拒绝仍走冷却慢路
                for (int attempt = 1; ; attempt++)
                {
                    resolved = await resolver.ResolveWithChainAsync(source); // G2：主源失败沿健康链下家，单链自动退现行备援
                    if (resolved is not null || attempt >= 3) break;
                    if (seq != _playSeq) return; // 换台后旧任务作废
                    await Task.Delay(2000);
                    resolver.Invalidate(source); // 清缓存表重试（冷却不动作已在票据侧区分：网络级失败不进冷却）
                }
                if (seq != _playSeq) return; // 换台后旧任务作废
                if (resolved is null && !ReferenceEquals(source, ch))
                    resolved = await resolver.ResolvePlayUrlAsync(ch, force: true);
            }
            catch (Exception ex)
            {
                // ⑫失败矩阵实接：逐类归因落日志 + 对应提示（不笼统「连接失败」）
                var kind = LiveVideoHost.FailureKind(ex);
                CrashNet.Record($"[ConnectFailure][{kind}] 取流异常 {ch.Name}（源={ch.SourceId}）：{ex.Message}");
                resolved = null;
                _ui.Post(() => { if (seq == _playSeq) FlashInfo(ConnectHint(kind)); });
            }
            if (seq != _playSeq) return;
            if (resolved is null)
            {
                _precheck.ReportSourceResult(source, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // ⑬：取流失败回写健康表
                // fix28（老板拍板「连接优先」）：冷却到期主动重连——不再干等下一轮冷启动轮询，冷却一过自动重连当前台
                ScheduleConnectRetry(seq, ch);
                _ui.Post(() =>
                {
                    if (seq != _playSeq) return;
                    HideLoading();
                    FlashInfo($"{ch.Name}  暂时没接上，稍后自动重连");
                });
                return;
            }
            // 透传头 + AI 画质滤镜合并为选项列表（v1.0.8）
            var opts = new List<(string Key, string Value)>(resolved.Options);
            foreach (var (k, v) in LiveQuality.OptionsFor(_aiTier)) opts.Add((k, v));
            // 【六】换台提速·先亮后升：多档变体发现（机制照建——单档/无数据/拉取失败一律空表按单档播），
            // 多档可用则先连最快出画面的档位（带宽最低先亮），亮后 UpgradeAfterFirstFrameAsync 无感升最高档
            // fix21（真机日志）：发现请求与主播放同一套头（频道 Referer/UA），否则央视频系 CDN 对无头清单 403
            // v1.0.13 fix27（真机日志）：仅频道静态头仍被央视频 CDN 拒（403/417）——发现请求再带上主播放
            // 实际使用的完整选项（令牌/画质键值），逐字同源，缺什么补什么
            // fix31 ②：403 冷却期跳过多档发现请求——票据侧正被服务器拒绝（冷却中），同源 CDN 的
            // 发现请求只会再吃一次拒绝，白跑还拖慢起播；冷却期照常单档起播，冷却过后自然恢复
            var inYspCooldown = _resolver.YspInCooldown(source);
            var variants = inYspCooldown
                ? Array.Empty<QualityVariant>()
                : await VariantDiscovery.DiscoverAsync(resolved.Url, resolved.Channel.Referer, resolved.Channel.UserAgent, resolved.Options);
            if (inYspCooldown) CrashNet.Record($"[FastStart] 403 冷却期跳过多档发现（{ch.Name}），单档起播");
            if (seq != _playSeq) return; // 变体发现期间换台：作废
            if (variants.Count > 1)
            {
                var first = FastStartPlanner.First(variants.Select(v => (v.HeightPx, v.BandwidthBps)).ToList());
                if (first is { } f)
                {
                    CrashNet.Record($"[FastStart] 多档先亮：{variants.Count} 档可用，先连 {f.Height}P（{f.Bandwidth}bps）出画面，亮后无感升最高档");
                    var pick = variants.First(v => v.HeightPx == f.Height && v.BandwidthBps == f.Bandwidth);
                    resolved = new ResolvedStream { Channel = resolved.Channel, Url = pick.Uri, Options = resolved.Options };
                    _currentVariantHeight = f.Height;
                }
            }
            if (seq != _playSeq) return;
            _currentVariants = variants;
            _lastResolvedUrl = resolved.Url;
            _ui.Post(() =>
            {
                if (seq != _playSeq) return;
                // fix29 恶意加固（最后一道闸）：快档变体可能带出 file/ftp 等恶意子地址——
                // 起播前再校验一次，非法地址按取流失败走重连记账，绝不交给播放内核
                if (!TaoLive.BLL.StreamUrlGuard.IsValid(resolved.Url))
                {
                    try { CrashNet.Record($"[恶意地址] 起播前闸口拦截（{ch.Name}）：变体地址非合法 http(s)，按取流失败处理"); } catch { }
                    _precheck.ReportSourceResult(source, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    ScheduleConnectRetry(seq, ch);
                    _ui.Post(() =>
                    {
                        if (seq != _playSeq) return;
                        HideLoading();
                        FlashInfo($"{ch.Name}  暂时没接上，稍后自动重连");
                    });
                    return;
                }
                _host.Play(resolved.Url, opts);
                // v1.0.20 ⑪②起播日志增记实际档位与全部画质滤镜参数：真机日志可定死「没开还是空转」
                try
                {
                    var qOpts = LiveQuality.OptionsFor(_aiTier);
                    CrashNet.Record(qOpts.Count == 0
                        ? $"[画质] 起播（{ch.Name}）：档位 关（无滤镜参数，原画直出）"
                        : $"[画质] 起播（{ch.Name}）：档位 {LiveQuality.TierName(_aiTier)}，滤镜参数 {string.Join("; ", qOpts.Select(o => $"{o.Key}={o.Value}"))}");
                }
                catch { }
                ShowInfoBar($"{ch.Number}  {ch.Name}");
                // v1.0.16 方案 D（并行抢首包，默认关）：主实例刚起播，陪跑第二实例后台连同频道次优源，
                // 3 秒窗口内谁先亮谁顶上（复用 HotSwap 第二实例 Prepare/Commit/Abort）；被拒退避回竞速
                if (_firstPacketEnabled && !_frameForCurrentSwitch && seq == _playSeq)
                    Task.Run(async () =>
                    {
                        try
                        {
                            var alt = CompanionSourceFor(source, ch);
                            if (alt is null) return;
                            var altResolved = await resolver.ResolvePlayUrlAsync(alt).ConfigureAwait(false);
                            if (altResolved is null || !TaoLive.BLL.StreamUrlGuard.IsValid(altResolved.Url) || seq != _playSeq) return;
                            _ui.Post(() =>
                            {
                                if (seq != _playSeq || _frameForCurrentSwitch) return;
                                BeginFirstPacketCompanion(altResolved.Url, opts);
                            });
                        }
                        catch (Exception ex) { CrashNet.Record($"[FirstPacket] 陪跑取流失败不拖累主链路：{ex.Message}"); }
                    });
            });
        });

        PrefetchNeighbors(ch);
    }

    // ---- 连接优先（老板 2026-11-08 拍板）：网络就绪先行、失败快速重试、冷却到期主动重连 ----

    // ---- v1.0.16 抢流与网络优化（v3.1 冻结稿）：B 换台竞速 / D 并行抢首包 ----

    /// <summary>同频道归并（与 ChannelPrecheck 口径一致：台名去空格一致视为同一频道）。</summary>
    private static bool SameChannel(Channel a, Channel b) => string.Equals(
        a.Name.Replace(" ", "").Replace("　", ""),
        b.Name.Replace(" ", "").Replace("　", ""), StringComparison.Ordinal);

    /// <summary>
    /// 换台探测竞速（方案 B）：候选=同频道全部可用来源变体，按健康账评分取前 2~3 路；
    /// 单路 1.2s 超时、总窗 1.5s、同源间隔 ≥30s；被拒（403/429）回写健康账走现行冷却；
    /// 竞速任务挂播放代数，换台即作废；全败返回 null 退回现行取流链路。
    /// </summary>
    private async Task<Channel?> RaceSwitchSourceAsync(Channel source, Channel ch, int seq)
    {
        try
        {
            var variants = _catalog.All.Where(c => !c.Unavailable && SameChannel(c, ch)).ToList();
            if (variants.Count <= 1) return null; // 单源无可竞速：直接走现行链路
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var candidates = _racer.Pick(variants, c => SourceScorer.Score(_precheck.SourceHealthOf(c), now), now);
            if (candidates.Count == 0) return null;
            // 冻结稿场景⑫：竞速并发按内存三档收缩（充裕 3 路 / 紧张、极低 2 路），低配设备内存占用不升
            var raceAm = (ActivityManager)GetSystemService(ActivityService)!;
            var raceMem = new ActivityManager.MemoryInfo();
            raceAm.GetMemoryInfo(raceMem);
            candidates = RaceConcurrencyPolicy.Shrink(
                candidates,
                c => SourceScorer.Score(_precheck.SourceHealthOf(c), now),
                RaceConcurrencyPolicy.Cap(raceMem.AvailMem));
            if (candidates.Count == 0) return null;
            return await _racer.RunAsync(candidates,
                c => _resolver.ProbeReachableDetailedAsync(c.StreamUrl, c.Referer, c.UserAgent),
                _ => seq == _playSeq, // 换台作废
                rejected => _precheck.ReportSourceResult(rejected, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) // 被拒进现行冷却
                .ConfigureAwait(false);
        }
        catch (Exception ex) { CrashNet.Record($"[Race] 竞速异常退回现行链路：{ex.Message}"); return null; }
    }

    /// <summary>抢首包陪跑候选（方案 D）：同频道次优来源变体（排除当前胜出源，按评分取最高者）。</summary>
    private Channel? CompanionSourceFor(Channel source, Channel ch) =>
        _catalog.All
            .Where(c => !c.Unavailable && SameChannel(c, ch) && !ReferenceEquals(c, source))
            .OrderByDescending(c => SourceScorer.Score(_precheck.SourceHealthOf(c), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ?? double.MinValue)
            .FirstOrDefault();

    /// <summary>
    /// 发起抢首包陪跑（方案 D）：复用双实例「新流亮了再换」机制（BeginHotSwap 既有口径），
    /// 受理后 3 秒窗口：陪跑先亮即提交顶上（CommitHotSwap），到点未亮即断陪跑（留主画面）。
    /// </summary>
    private void BeginFirstPacketCompanion(string url, List<(string Key, string Value)> options)
    {
        if (!_firstPacketEnabled || _firstPacketPending) return;
        if (_host.IsSwapPending || _hotSwap.HasPending) return;
        var before = _hotSwap.Active;
        BeginHotSwap(url, options, targetHeight: 0, reason: "抢首包");
        var session = _hotSwap.Active;
        if (session is null || ReferenceEquals(session, before)) return; // 未受理（冷却/未决）：不陪跑
        _firstPacketPending = true;
        CrashNet.Record("[FirstPacket] 陪跑第二实例受理（3 秒窗口内谁先亮谁顶上）");
        _firstPacketCutRun = () => _ui.Post(() =>
        {
            _firstPacketCutRun = null;
            if (!_firstPacketPending) return;
            if (_host.IsSwapPending) // 陪跑还没亮：3 秒即断，留主画面
            {
                CrashNet.Record("[FirstPacket] 陪跑 3 秒未亮即断，留主画面");
                AbortHotSwapUi("抢首包陪跑超时即断");
            }
        });
        _ui.PostDelayed(_firstPacketCutRun, FirstPacketPolicy.CompanionWindowMs);
    }

    /// <summary>撤陪跑 3 秒收口回调（提交/放弃/换台时调用，不残留）。</summary>
    private void ClearFirstPacketCut()
    {
        if (_firstPacketCutRun != null)
        {
            _ui.RemoveCallbacks(_firstPacketCutRun);
            _firstPacketCutRun = null;
        }
    }


    /// <summary>
    /// fix28：起播前等网络就绪——冷启动头几秒网络未必就绪，先连必败还白吃 30 秒冷却；
    /// 等就绪再连（最长 10 秒，超时不拦，连接重试链路兜底）。只在开机首连前用。
    /// </summary>
    private async Task WaitNetworkReadyAsync(int timeoutMs)
    {
        try
        {
            if (GetSystemService(global::Android.Content.Context.ConnectivityService) is not global::Android.Net.ConnectivityManager cm) return;
            long start = System.Environment.TickCount64;
            while (System.Environment.TickCount64 - start < timeoutMs)
            {
                var net = cm.ActiveNetwork;
                if (net != null)
                {
                    var caps = cm.GetNetworkCapabilities(net);
                    bool up = caps is { } c && c.HasCapability(global::Android.Net.NetCapability.Internet)
                        && c.HasCapability(global::Android.Net.NetCapability.Validated); // 现行口径：Validated 能力即网络可用（过时 API 已移除）
                    if (up) { CrashNet.Record("[Boot] 网络就绪，开始开机即连"); return; }
                }
                await Task.Delay(500);
            }
            CrashNet.Record($"[Boot] 等网络就绪超时 {timeoutMs}ms——照常起播（连接重试链路兜底）");
        }
        catch (Exception ex) { CrashNet.Record($"[Boot] 网络就绪检查异常照常起播：{ex.Message}"); }
    }

    /// <summary>
    /// fix28：首连最终失败后，冷却一过（约 32 秒）主动重连当前台——连接优先，不干等冷启动轮询。
    /// 换台或已连上即作废（代数守卫）；重连再失败由本方法再排下一次，用户随时可切台。
    /// fix29 修正①：轮数改用 ConnectRetryLedger 按频道记账——原按播放任务序号记账，
    /// 重连本身走 Play 换新任务序号，轮数永远数不到 3，「3 轮放开探活」实际不生效。
    /// 快失败约 3 轮 96 秒放开探活；慢失败（取流单次最长 10 秒叠加）最坏 2~3 分半——
    /// 宁可慢跳台也要把连接优先做足（老板 2026-11-08 定）。
    /// </summary>
    private void ScheduleConnectRetry(int failSeq, Channel ch)
    {
        // fix29 修正①：同频道累计轮数，换频道（真正换台）自动清零——3 轮仍败=疑似真死台
        var round = _connectLedger.OnFail(ch.EpgKey);
        if (_connectLedger.ShouldReleaseGate)
        {
            // 冲突收口（老板拍板「连接优先」vs 真死台自动跳台）：重连 3 轮仍败，视为本台已凉、
            // 连接等待窗属「冷」——放开冷启动探活，判死即自动跳下一个有信号的台；
            // 台若活了，探活顺路暖票，下一轮重连直接成（重连本身仍走 Play，届时照常关闸连）
            _precheckGate = true;
            CrashNet.Record("[ConnectRetry] 重连 3 轮仍失败，放开冷启动探活（判死自动跳台）");
        }
        System.Threading.Timer? retry = null;
        retry = new System.Threading.Timer(_ => _ui.Post(() =>
        {
            retry?.Dispose();
            if (failSeq != _playSeq || _frameForCurrentSwitch) return; // 换台了/已连上：作废
            // fix31：链路短重试先过长窗断路——观察期/收口不再自动重连（保持画面，按键照常），
            // 触达上限进观察期（一次性提示），连 3 轮观察仍全败按「全源不可达」收口；正常放行走现行重连
            var verdict = _loopBreaker.OnFallbackEvent(System.Environment.TickCount64);
            if (verdict == FallbackLoopBreaker.Step.ObserveEnter) { EnterObserve(); return; }
            if (verdict == FallbackLoopBreaker.Step.ObserveHold) { CrashNet.Record("[FallbackLoop] 观察期内：链路短重试暂停，保持当前画面"); return; }
            if (verdict == FallbackLoopBreaker.Step.Exhausted) { CloseLoopAsNoSignal(ch); return; }
            CrashNet.Record($"[ConnectRetry] 首连失败冷却到期，主动重连 {ch.Name}（第 {round} 轮后）");
            Play(ch);
        }), null, 32_000, System.Threading.Timeout.Infinite);
    }

    // ---- fix31 长窗兜底断路（冻结稿 v2）：观察期与收口，与 RescueBreaker 两级并存 ----

    /// <summary>观察期一次性提示：每次进入观察期提示一次，不刷屏。</summary>
    private void ShowObserveNotice()
    {
        try { Toast.MakeText(this, "来源暂时受限，稍后自动重试", ToastLength.Long)!.Show(); } catch { }
    }

    /// <summary>
    /// 进入 90 秒静默观察期：停自动兜底与自动跳台（宿主自救/静默重取流/断流重连全静默），
    /// 保持当前画面，按键全可用；期满自动恢复自动链路——源若仍坏，事件继续入账走下一轮或收口。
    /// </summary>
    private void EnterObserve()
    {
        CrashNet.Record($"[FallbackLoop] 长窗断路：10 分钟窗口兜底事件达 {FallbackLoopBreaker.MaxEvents} 次，进入 90 秒观察期（停自动兜底/跳台，按键照常）");
        _host.RescuePaused = true;
        ShowObserveNotice();
        ClearObserveUnpause();
        _observeUnpauseRun = () => _ui.Post(() =>
        {
            _observeUnpauseRun = null;
            _host.RescuePaused = false; // 观察期满恢复自动链路（换台已在 Play 内即时放行）
            CrashNet.Record("[FallbackLoop] 观察期满，恢复自动兜底与自动跳台");
        });
        _ui.PostDelayed(_observeUnpauseRun, FallbackLoopBreaker.ObserveMs);
    }

    /// <summary>撤观察期到期回调（换台/收口时调用，不残留）。</summary>
    private void ClearObserveUnpause()
    {
        if (_observeUnpauseRun != null)
        {
            _ui.RemoveCallbacks(_observeUnpauseRun);
            _observeUnpauseRun = null;
        }
    }

    /// <summary>
    /// 断路收口（现行「全源不可达」口径）：连 3 轮观察仍全败，停自动兜底、亮无信号——
    /// 绝不无限空转；换台即恢复（Play 内清账放行）。只报一次，防刷屏。
    /// </summary>
    private void CloseLoopAsNoSignal(Channel ch)
    {
        if (_loopClosed || _current?.EpgKey != ch.EpgKey) return;
        _loopClosed = true;
        _host.RescuePaused = true;
        ClearObserveUnpause();
        HideLoading();
        FlashInfo($"{ch.Name}  无信号");
        CrashNet.Record($"[FallbackLoop] 断路收口：{ch.Name} 连续 {FallbackLoopBreaker.MaxObserveRounds} 轮观察仍全败，按「全源不可达」收口（换台即恢复）");
    }

    /// <summary>fix31 ③：每 5 分钟资源观测日志（托管堆/系统内存/播放缓冲水位，为长跑稳定性排查留底）。</summary>
    private void LogResourceWatermark(object? _)
    {
        try
        {
            long managed = GC.GetTotalMemory(false);
            string sys = "n/a";
            if (GetSystemService(global::Android.Content.Context.ActivityService) is global::Android.App.ActivityManager am)
            {
                var mi = new global::Android.App.ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                sys = $"{mi.AvailMem / 1048576}MB 可用 / {mi.TotalMem / 1048576}MB 总量";
            }
            CrashNet.Record($"[Resources] 5 分钟水位：托管堆 {managed / 1024}KB，系统内存 {sys}，播放缓冲 {_host.LastBufferWatermark ?? "暂无"}");
        }
        catch { }
    }

    // ---- 双实例无感切换（v1.0.10【六】【九】A）：新流亮了再换，全程不黑不闪不卡 ----

    /// <summary>
    /// 【六】先亮后升：最快档画面已亮，后台无感升最高档（双实例新流亮了再换）。
    /// 已是最高档/单档不折腾；升级失败或网络撑不住留当前档，绝不为升档搞挂画面。
    /// </summary>
    private async Task UpgradeAfterFirstFrameAsync()
    {
        try
        {
            var variants = _currentVariants;
            if (variants.Count <= 1) return; // 单档流：如实单档，不硬造档位
            var target = FastStartPlanner.UpgradeTarget(
                variants.Select(v => (v.HeightPx, v.BandwidthBps)).ToList(), _currentVariantHeight);
            if (target is null) return; // 已在最高档：不折腾
            var top = variants.First(v => v.HeightPx == target.Value.Height);
            CrashNet.Record($"[FastStart] 画面已亮，后台无感升级 → {top.HeightPx}P（双实例，当前画面原样保留）");
            BeginHotSwap(top.Uri, LiveQuality.OptionsFor(_aiTier), top.HeightPx, "⑥先亮后升");
        }
        catch (Exception ex)
        {
            CrashNet.Record("[FastStart] 先亮后升检查异常（保持当前档）：" + ex.Message);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 【九】A/手动切档统一入口：绝不重起当前台（Play(_current) 路径退役）。
    /// 同档不折腾；可热应用优先热切（现渲染器滤镜随媒体参数生效，走双实例）；失败留当前档。
    /// </summary>
    private void ApplyTierChange(AiLiveTier newTier, string reason)
    {
        if (newTier == _aiTier) return; // 同档不折腾
        var mode = HotSwitchPolicy.Decide(sameTarget: false, canHotApply: false); // 现 LibVLC 滤镜需新 Media → 双实例
        var url = _host.CurrentUrl;
        if (mode == SwapMode.SwapOnReady && !string.IsNullOrEmpty(url))
        {
            _pendingTier = newTier;
            // fix50（B 路）：热切整链投后台口径——巡检/先亮后升的判定已在后台线程，第二载体创建
            // 与视图树操作必须回 UI 线程（Handler.Post 既有口径）；BeginSwap 的原生重活由宿主内部
            // 投内核线程（fix30 同源口径），UI 只保留触发与结果回调。此处同时修掉「先亮后升」后台
            // 直调 BeginHotSwap 触碰视图树的潜在违例。
            _ui.Post(() => BeginHotSwap(url, LiveQuality.OptionsFor(newTier), 0, reason));
        }
        else
        {
            // 无当前流可切（未起播等）：档位直接落账，下次起播按新档
            _aiTier = newTier;
            _governor = new SmartGovernor(newTier);
        }
    }

    /// <summary>发起一次双实例切换（会话受理→第二载体入视图树→宿主后台连新流）。受理失败=留在当前档，不重试到崩。</summary>
    private void BeginHotSwap(string url, IReadOnlyList<(string Key, string Value)> options, int targetHeight, string reason)
    {
        if (_current is null || _host.IsSwapPending || _hotSwap.HasPending) return;
        // v1.0.20 C 拉黑期禁第二实例：整源观察期内热切只会再撞一次拒绝，禁起第二实例（403 不进热切）
        if (_playSource != null && _resolver.Blackout.IsBlackedOut(_playSource.SourceId))
        {
            var blkNow = System.Environment.TickCount64;
            if (blkNow - _lastCooldownLogMs >= 60_000)
            {
                _lastCooldownLogMs = blkNow;
                CrashNet.Record($"[HotSwap] 源观察期内热切禁用（{_playSource.SourceId}，{reason}），保持当前画面");
            }
            return;
        }
        var session = _hotSwap.Begin(_playSeq, targetHeight, reason, System.Environment.TickCount64);
        if (session is null)
        {
            // fix24：冷却拒受只记一次/分钟——失败退避期间不再 5 秒一循环刷日志
            var now = System.Environment.TickCount64;
            if (now - _lastCooldownLogMs >= 60_000)
            {
                _lastCooldownLogMs = now;
                CrashNet.Record($"[HotSwap] 切换冷却未受理，保持当前档（{reason}）"); // 防横跳：反复升降档不闪卡
            }
            return;
        }
        var carrier = CreateSwapCarrier();
        if (carrier is null) return; // 载体创建失败：不切换，当前画面原样
        if (!_host.BeginSwap(carrier, url, options))
        {
            AbortHotSwapUi("第二实例受理失败，留当前档");
            return;
        }
        _swapSession = session;
        // 老板 2026-11-08 拍板（连接优先）：第二实例连接期间冷启动让路，提交或放弃后恢复
        _precheckGate = false;
        // 超时兜底：新流迟迟不亮判升级失败（留当前档，画面全程原样），绝不黑屏干等
        _swapTimeoutRun = () => _ui.Post(OnSwapTimeoutCheck);
        _ui.PostDelayed(_swapTimeoutRun, HotSwapCoordinator.PrepareTimeoutMs + 500);
    }

    /// <summary>第二实例新流已亮：顶上成主画面（无黑无闪），档位落账。</summary>
    private void CommitHotSwap()
    {
        if (_swapSession is null || !_host.IsSwapPending) return;
        if (!_swapSession.MarkReady()) return; // 非未决：忽略（提交只此一次，防重复提交）
        var newCarrier = _pendingSwapCarrier;
        var oldCarrier = _mainVideoView;
        _host.CommitSwap();
        if (newCarrier != null) newCarrier.Visibility = ViewStates.Visible;
        if (oldCarrier != null && !ReferenceEquals(oldCarrier, newCarrier))
            _videoContainer.RemoveView(oldCarrier); // 旧载体撤出视图树，第二载体成唯一画面
        _mainVideoView = newCarrier ?? oldCarrier;
        _pendingSwapCarrier = null;
        ClearSwapTimeout();
        if (_pendingTier is { } t)
        {
            var fromTier = _aiTier;
            _aiTier = t;
            _governor = new SmartGovernor(t); // 自动档会话以新档为基准续走
            _pendingTier = null;
            _keeper.OnUpgradeCommitted(fromTier, System.Environment.TickCount64); // fix25：升档提交即开稳档观察窗
        }
        _currentVariantHeight = _swapSession.TargetHeight > 0 ? _swapSession.TargetHeight : _currentVariantHeight;
        _hotSwap.OnCommitted(_swapSession, _playSeq);
        _hotSwap.ResetFailures(); // fix24：升级成功清失败账
        _swapSession = null;
        _precheckGate = true; // 老板拍板（连接优先）：第二实例已连成，冷启动恢复
        var wasFirstPacket = _firstPacketPending;
        _firstPacketPending = false;
        ClearFirstPacketCut();
        if (wasFirstPacket) FlashInfo("抢首包：陪跑先亮，直接顶上"); // v1.0.16 方案 D
        else FlashInfo($"画质：{LiveQuality.DisplayName(DeviceProfile.AiEnhanceTierSetting)}");
    }

    /// <summary>放弃切换：第二实例撤出视图树与播放内核，当前画面从头到尾原样。</summary>
    private void AbortHotSwapUi(string reason)
    {
        ClearSwapTimeout();
        var wasFirstPacket = _firstPacketPending;
        _firstPacketPending = false;
        ClearFirstPacketCut();
        if (_pendingSwapCarrier != null)
        {
            try { _videoContainer.RemoveView(_pendingSwapCarrier); } catch { }
            _pendingSwapCarrier = null;
        }
        _host.AbortSwap(reason);
        _swapSession?.MarkAborted();
        _swapSession = null;
        _pendingTier = null;
        // 修正③（推演）：只有当前台出过画面（热切建立在已连成的基础上）才恢复冷启动；
        // 首连未成时保持关闸——否则开机头几秒在设置面板改档，热切失败会把冷启动提前放开
        if (_frameForCurrentSwitch) _precheckGate = true;
        if (_governor is { } g && reason != "换台" && reason != "停止播放")
        {
            _hotSwap.MarkFailed(System.Environment.TickCount64); // fix24：失败入退避账，不 5 秒一循环空耗
            _governor = new SmartGovernor(g.BaseTier); // 自动档进度重置：以当前实际档重新计平稳周期
        }
        if (wasFirstPacket)
        {
            // v1.0.16 方案 D：陪跑被拒/超时退避回竞速——不占用升档退避账（失败账随台清）
            _hotSwap.ResetFailures();
            CrashNet.Record("[FirstPacket] 陪跑退避回竞速");
        }
    }

    private void OnSwapTimeoutCheck()
    {
        _swapTimeoutRun = null;
        if (_hotSwap.ExpireIfDue(System.Environment.TickCount64))
            AbortHotSwapUi("新流迟迟不亮（升级超时），留当前档");
    }

    private void ClearSwapTimeout()
    {
        if (_swapTimeoutRun != null)
        {
            _ui.RemoveCallbacks(_swapTimeoutRun);
            _swapTimeoutRun = null;
        }
    }

    /// <summary>第二实例载体：与主载体同型（纹理/表面），入视图树垫底、暂不可见——新流在背后连，首帧到齐才亮。</summary>
    private IVideoCarrierView? CreateSwapCarrier()
    {
        try
        {
            global::Android.Views.View view = _carrierTexture
                ? new LiveTextureView(this) { LayoutParameters = new FrameLayout.LayoutParams(-1, -1) }
                : new LiveSurfaceView(this) { LayoutParameters = new FrameLayout.LayoutParams(-1, -1) };
            view.Visibility = ViewStates.Invisible;
            _videoContainer.AddView(view, 0); // 垫底：亮牌等浮层不受影响；提交时旧载体移出后自然亮出
            _pendingSwapCarrier = view;
            return view is IVideoCarrierView c ? c : null;
        }
        catch (Exception ex)
        {
            CrashNet.Record("[HotSwap] 第二载体创建失败（留当前档）：" + ex.Message);
            return null;
        }
    }

    /// <summary>【九】A自动档巡检（5 秒节拍，口径同 SmartGovernor）：档位变化走双实例，绝不重起当前台。</summary>
    private void TickGovernor()
    {
        // fix50（B 路）：本方法整体跑在线程池线程（定时器直调，不再投 UI）——判活、帧率快照、
        // 卡顿记账等原生/重活全部离开 UI 线程（与已定谳④同型病根的触发入口整链后撤）。
        // 需要动界面的动作（提示条、换档触发）经 _ui.Post 回 UI 线程，UI 只保留触发与结果回调。
        // 在途互斥：旧口径靠 UI 线程串行化节拍，改后台后用在途标志保持「同一时刻至多一拍」。
        if (System.Threading.Interlocked.Exchange(ref _governorTickInFlight, 1) == 1) return; // 上一拍未完：跳过本轮，不排队堆积
        try
        {
            var cur = _current;
            // 批1 A（冻结稿「巡检每 5 秒已喂帧统计，顺路回写」）：卡顿率顺路入健康账——
            // 口径：首帧已出后播放时钟停滞（IsLive）才算卡顿；起播连接期不算（那段是延迟与失败口径，不污染卡顿账）。
            // 观测先于自动档守卫：热切换/换台挂起期间照记，不漏窗口；零额外请求
            bool stalled = cur is not null && _frameForCurrentSwitch && !_host.IsAlive;
            if (cur is not null && _playSource != null)
                _precheck.ReportStallObserved(_playSource, stalled);
            if (cur is null || _host.IsSwapPending || _hotSwap.HasPending) return;
            var g = _governor;
            if (g is null) return;
            // fix25：升档观察窗——稳不住（帧率跌破 85% 或停滞）自动退回原档，判定即收口只退一次
            if (_keeper.Watching)
            {
                var (kOfps, kCfps, _, _, _, _) = _host.SnapshotStats();
                if (_keeper.Report(System.Environment.TickCount64, kOfps, kCfps, stalled))
                {
                    var from = _keeper.FromTier;
                    _ui.Post(() =>
                    {
                        FlashInfo("升档后帧率撑不住，退回上一档");
                        ApplyTierChange(from, "稳档回退");
                    });
                    return;
                }
            }
            if (g.Tick(droppedRate: 0, stalled)) // 丢弃帧计数快照未含：升档路径按无掉帧推进，降档由停滞触发
            {
                // fix25：升档门槛加严——实测帧率撑不住原始帧率（或停滞）本轮不升，平稳周期重新计，防来回拉锯
                if (g.Effective > _aiTier)
                {
                    var (ofps, cfps, _, _, _, _) = _host.SnapshotStats();
                    if (!FrameRateKeeper.UpgradeAllowed(ofps, cfps, stalled))
                    {
                        _governor = new SmartGovernor(_aiTier);
                        return;
                    }
                }
                // fix50：ApplyTierChange 内部自行把视图树操作回投 UI（见该方法注释），此处直接调用
                ApplyTierChange(g.Effective, "⑨A自动档");
            }
        }
        catch (Exception ex)
        {
            // 线程池线程无 UI 异常兜底：巡检异常必须落日志，下个节拍续走，绝不吞掉
            CrashNet.Record("[Governor] 后台巡检异常（下个节拍续走）：" + ex.Message);
        }
        finally
        {
            System.Threading.Volatile.Write(ref _governorTickInFlight, 0);
        }
    }

    /// <summary>⑫逐类提示（ConnectFailure 口径）：不笼统「连接失败」，每类给对应说法。</summary>
    private static string ConnectHint(string kind) => kind switch
    {
        nameof(ConnectFailureKind.DnsFailure) => "网络解析不了该台，正在自动换备用来源",
        nameof(ConnectFailureKind.ConnectTimeout) => "电视没联网或网络不稳，请检查网络",
        nameof(ConnectFailureKind.RateLimited) => "取流被限流，稍后自动重试",
        nameof(ConnectFailureKind.TimeSkew) => "电视时间不对，请校准时间后重试",
        nameof(ConnectFailureKind.Rejected) => "该台暂时被拒绝访问，正在自动换来源",
        nameof(ConnectFailureKind.DecoderSurface) => "播放器内部错误，正在自动重试",
        _ => "暂时连不上，正在自动重试",
    };

    /// <summary>预检轻探（v1.0.8）：换票成功即服务器可达；直连源只取几 KB 头数据验证，不建视频传输。</summary>
    private async Task<bool> ProbeChannelAsync(Channel ch)
    {
        var r = await _resolver.ResolvePlayUrlAsync(ch);
        if (r is null) return false;
        // v1.0.13 预取到底（老板拍板「能压的都要压」）：探活顺路把播放清单抓回缓存，
        // 后台低优先执行不抢前台资源——换台时变体发现/起流直接读缓存
        if (!string.IsNullOrEmpty(r.Url))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await StreamResolver.FetchPlaylistTextAsync(r.Url, ch.Referer, ch.UserAgent);
                    if (!string.IsNullOrEmpty(text))
                        PlaylistPrefetch.Put(r.Url, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                catch (Exception ex) { BllLog.Warn("PlaylistPrefetch", $"清单预取失败 url={BllLog.MaskUrl(r.Url)}", ex); }
            });
        }
        if (!string.Equals(r.Url, ch.StreamUrl, StringComparison.Ordinal))
            return true; // 换票成功=取票服务器可达且票有效
        return await _resolver.ProbeReachableAsync(r.Url, ch.Referer, ch.UserAgent);
    }

    /// <summary>预连接：把相邻两路的可播地址提前换好放进缓存，换台零等待。</summary>
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
            Task.Run(async () => { try { await _resolver.ResolvePlayUrlAsync(nb); } catch (Exception ex) { CrashNet.Record($"[Prefetch] 邻台预取失败：{ex.Message}"); } });
        }
    }

    // ---- 循环预检（v1.0.8 用户拍板：频率越高越好，以不被判异常流量为前提）----

    /// <summary>
    /// 常驻循环预检：每轮保态复测（可用台常绿、无信号台留组，探测完才翻转），轮间歇 20 秒；
    /// 单台间隔 1000ms（v1.0.13 让路前台切台）+ 央视频取票 403 冷却兜底，全程不被判异常流量。关掉开关时空转低耗等待。
    /// </summary>
    private async Task PrecheckLoopAsync(System.Threading.CancellationToken ct)
    {
        // v1.0.13（老板 15:42 拍板）：预热后台跑，让路前台切台——整循环最低线程优先
        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Lowest;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 老板 2026-11-08 拍板：连接优先 + 续接循环——暂停/恢复由每台节点前的闸（PauseGate）接管，
                // 此处只管整轮节奏；连接优先时段循环就地挂起，冷了从断点续探
                if (_precheckEnabled) await _precheck.RunAsync(_catalog.All, ct, keepKnownState: true);
                await Task.Delay(_precheckEnabled ? 20_000 : 5_000, ct);
            }
            catch (global::System.OperationCanceledException) { return; }
            catch (Exception ex) { CrashNet.Record($"[Precheck] 预检单轮失败下一轮再来：{ex.Message}"); }
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
            catch (Exception ex) { CrashNet.Record($"[SystemSettings] 设置跳转调不动降级下一级：{ex.Message}"); }
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
        panel.SetPadding(40, 28, 40, 36);
        panel.SetMinimumWidth((int)(360 * Resources!.DisplayMetrics!.Density));
        panel.Visibility = ViewStates.Gone;
        _root.AddView(panel, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
        });
        return panel;
    }

    private void RefreshSettingsRows()
    {
        if (_settingsPanel is null) return;
        _settingsPanel.RemoveAllViews();

        void AddLabel(string text)
        {
            var tv = new TextView(this) { Text = text, TextSize = 15 };
            tv.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.SetPadding(24, 18, 24, 6);
            _settingsPanel!.AddView(tv);
        }
        void AddRow(string text, Action onClick, string color)
        {
            var tv = new TextView(this) { Text = text, TextSize = 19 };
            tv.SetTextColor(Color.ParseColor(color));
            tv.SetPadding(24, 24, 24, 24);
            tv.Focusable = true;
            // 全局透明化（v1.0.8 第15项）：面板行焦点同样走统一透明效果层
            var focusDrawable = InkTranslucency.FocusDrawable();
            tv.FocusChange += (_, e) =>
            {
                if (e.HasFocus) tv.Background = focusDrawable;
                else if (tv.Background == focusDrawable) tv.Background = null;
            };
            tv.Click += (_, _) => onClick();
            _settingsPanel!.AddView(tv);
        }

        AddLabel("── 软件设置 ──");
        AddRow($"画质 · {LiveQuality.DisplayName(DeviceProfile.AiEnhanceTierSetting)}（确认键切换）",
            CycleAiTier, "#F7F5EE");
        AddRow($"预检握手：{(_precheckEnabled ? "开" : "关")}（确认键切换）",
            TogglePrecheck, "#F7F5EE");
        AddRow($"换台竞速：{(_rushEnabled ? "开" : "关")}（确认键切换）",        // v1.0.16 方案 B
            ToggleRush, "#F7F5EE");
        AddRow($"并行抢首包：{(_firstPacketEnabled ? "开" : "关")}（确认键切换）", // v1.0.16 方案 D
            ToggleFirstPacket, "#F7F5EE");
        AddRow("崩溃日志（点按查看）", ShowCrashLog, "#C9D6E2");
        AddLabel("── 电视系统 ──");
        AddRow("打开电视系统设置", OpenSystemSettings, "#8FB4D4");
        AddLabel("返回键收起 · 长按设置键直达系统设置");
    }

    /// <summary>画质档循环（自动→低→高→自动，v1.0.10 全中文）：档位持久化，生效走双实例无感切换（绝不重起当前台）。</summary>
    private void CycleAiTier()
    {
        var next = SettingsCycler.NextAiTier(DeviceProfile.AiEnhanceTierSetting);
        DeviceProfile.AiEnhanceTierSetting = next;
        _prefs?.Edit()?.PutString("ai_tier", next)?.Apply();
        RefreshSettingsRows();
        var picked = LiveQuality.Pick(next, _abi32, _totalMem);
        FlashInfo($"画质：{LiveQuality.DisplayName(next)}" + (picked != LiveQuality.Pick("high", _abi32, _totalMem) && next == "high" ? "（本机按性能降档）" : ""));
        // 【九】A：按新设置解出实设备档，双实例「新流亮了再换」生效——失败留当前档，下次换台按新档起播
        ApplyTierChange(picked, "手动切档");
    }

    private void TogglePrecheck()
    {
        _precheckEnabled = !_precheckEnabled;
        _prefs?.Edit()?.PutBoolean("precheck_enabled", _precheckEnabled)?.Apply();
        RefreshSettingsRows();
        FlashInfo(_precheckEnabled ? "预检握手：开" : "预检握手：关");
    }

    /// <summary>换台竞速开关（v1.0.16 方案 B，默认开）：关掉即全走现行选源链路。</summary>
    private void ToggleRush()
    {
        _rushEnabled = !_rushEnabled;
        _prefs?.Edit()?.PutBoolean("rush_enabled", _rushEnabled)?.Apply();
        RefreshSettingsRows();
        FlashInfo(_rushEnabled ? "换台竞速：开" : "换台竞速：关");
    }

    /// <summary>并行抢首包开关（v1.0.16 方案 D，默认关）：实验能力，开起后换台并行双实例抢首包。</summary>
    private void ToggleFirstPacket()
    {
        _firstPacketEnabled = !_firstPacketEnabled;
        _prefs?.Edit()?.PutBoolean("firstpacket_enabled", _firstPacketEnabled)?.Apply();
        RefreshSettingsRows();
        FlashInfo(_firstPacketEnabled ? "并行抢首包：开" : "并行抢首包：关");
    }

    private void ShowCrashLog()
    {
        try
        {
            var path = new Java.IO.File(FilesDir, "taolive_crash.log");
            if (!path.Exists()) { FlashInfo("暂无崩溃日志"); return; } // 空态提示（v1.0.9 第⑩项）
            var text = System.IO.File.ReadAllText(path.AbsolutePath);
            if (text.Length > 4000) text = "…\n" + text[^4000..];
            var builder = new AlertDialog.Builder(this); // 构建器逐条调用：绑定返回可空不续点
            builder.SetTitle("崩溃日志");
            builder.SetMessage(text);
            builder.SetPositiveButton("关闭", (s, a) => { });
            // ⑩一键清理（v1.0.9 用户拍板）：确认键清空，清完空态提示
            builder.SetNeutralButton("一键清理", (s, a) =>
            {
                try
                {
                    System.IO.File.Delete(path.AbsolutePath);
                    FlashInfo("崩溃日志已清空");
                }
                catch { FlashInfo("清理失败，请重试"); }
            });
            builder.Show();
        }
        catch { FlashInfo("日志读取失败"); }
    }

    private void ShowInfoBar(string text)
    {
        _infoBar.Text = text;
        _infoBar.Visibility = ViewStates.Visible;
        _ui.RemoveCallbacks(_hideInfo);
        _ui.PostDelayed(_hideInfo, 3000);
    }

    private void FlashInfo(string text) => ShowInfoBar($"{(_current?.Number.ToString() ?? "")}  {text}".Trim());

    private Action _hideInfo => () => _infoBar.Visibility = ViewStates.Invisible;

    // ---- 起播亮牌（雷二）：延迟亮、点了名、首帧撤 ----

    private void ShowLoading(Channel ch)
    {
        bool wasVisible = _loadingPanel.Visibility == ViewStates.Visible;
        _loadingChannel = ch; // 动画跟随最新频道（重连期间换台不串台名）
        _loadingText.Text = $"{ch.Number}  {ch.Name}\n正在连接";
        _loadingPanel.Visibility = ViewStates.Visible;
        if (_bootLoading)
        {
            // 网络就绪亮牌交接：停掉「正在连接网络」动画，换台名版动画（否则旧动画会顶掉新文本）
            _bootLoading = false;
            StopLoadingDots();
            StartLoadingDots(ch);
        }
        else if (!wasVisible) StartLoadingDots(ch); // 已亮：只更新文本不重置动画
    }

    private Channel? _loadingChannel;
    private bool _bootLoading; // 网络就绪等待亮牌（fix28）：交接与撤销的归属标记
    private readonly TaoLive.BLL.ConnectRetryLedger _connectLedger = new(); // fix29 修正①：重连轮数按频道记账（换频道清零，3 轮放闸跳台）

    /// <summary>fix28（老板拍板）：起播前等网络就绪也给亮牌——「正在连接网络」呼吸点，不黑屏干等。</summary>
    private void ShowBootLoading()
    {
        StopLoadingDots();
        _bootLoading = true;
        _loadingText.Text = "正在连接网络";
        _loadingPanel.Visibility = ViewStates.Visible;
        int token = ++_loadingSeq;
        var tick = 0;
        string[] frames = { "正在连接网络", "正在连接网络 ·", "正在连接网络 ··", "正在连接网络 ···" };
        _loadingAnimRun = () =>
        {
            if (token != _loadingSeq || _loadingPanel.Visibility != ViewStates.Visible) return;
            tick++;
            _loadingText.Text = frames[tick % frames.Length];
            _ui.PostDelayed(_loadingAnimRun!, 400);
        };
        _ui.PostDelayed(_loadingAnimRun, 400);
    }

    private void HideLoading()
    {
        CancelPendingLoading();
        StopLoadingDots();
        _bootLoading = false;
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

    private static string HeaderText(string title, bool expanded) =>
        $"{(expanded ? "▾" : "▸")} {title}";

    /// <summary>⑪全部可收放分组键：收藏组（有收藏时）+ 非空分类 + 无信号组（有判死台时）。</summary>
    private List<string> ListGroupKeys()
    {
        var keys = new List<string>();
        if (_favorites.TopGroup(_catalog.All.ToList()).Count > 0) keys.Add("★ 收藏");
        keys.AddRange(_catalog.Groups.Where(g => g.Channels.Count > 0).Select(g => g.Title));
        if (_catalog.All.Any(c => _precheck.Of(c) == ChannelHealth.Dead)) keys.Add("✕ 无信号");
        return keys;
    }

    /// <summary>⑪一键切换：全开→全部收起；有任何折叠→全部展开（含无信号组），重建列表。</summary>
    private void ToggleAllGroups()
    {
        var keys = ListGroupKeys();
        int collapsed = keys.Count(k => !_expanded.GetValueOrDefault(k, true));
        bool expandAll = collapsed > 0; // 全收/部分折叠都一键展开
        foreach (var k in keys) _expanded[k] = expandAll;
        _pendingFocusAnchor = null;
        OpenChannelList();
    }

    private void OpenChannelList()
    {
        _listOpen = true;
        _listPanel.RemoveAllViews();
        _listPanel.Visibility = ViewStates.Visible; // 呼出列表：盖板必须显式转可见（v1.0.3 漏了这步，真机按菜单无反应）

        // v1.0.12（老板拍板）：动态信息与标题各不干扰——固定三行独占、行高锁死、提前占位
        //（画质最上、依次分辨率、帧率），「检测中」/「--」占位，只换字不动结构
        TextView MakeStatLine(string placeholder)
        {
            var tv = new TextView(this)
            {
                Text = placeholder,
                TextSize = 16,
            };
            tv.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.SetSingleLine(true);
            tv.SetPadding(48, 8, 48, 8);
            int locked = (int)(36 * Resources!.DisplayMetrics!.Density); // 行高锁死：占位与实测同高，刷新不跳
            tv.SetMinimumHeight(locked);
            tv.SetHeight(locked);
            return tv;
        }
        _qualityLine = MakeStatLine("画质 检测中");
        _resLine = MakeStatLine("分辨率 检测中");
        _fpsLine = MakeStatLine("帧率 检测中");
        _listPanel.AddView(_qualityLine);
        _listPanel.AddView(_resLine);
        _listPanel.AddView(_fpsLine);

        _listView = new ListView(this) { Divider = null };
        // 全局透明化（v1.0.8 第15项）：焦点效果=低透明度细边框+极淡底色，不复用实色高亮
        _listView.Selector = InkTranslucency.FocusDrawable();
        var rows = BuildRows();

        _adapter = new ChannelAdapter(this, rows, _current, n => _favorites.IsFavorite(n), c => _precheck.Of(c));
        _listView.Adapter = _adapter;
        _listView.ItemClick += (_, e) =>
        {
            var r = rows[e.Position];
            if (r.FoldToggle)
            {
                ToggleAllGroups();
                return;
            }
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
            if (r.HeaderKey != null)
            {
                _expanded[r.HeaderKey] = !_expanded.GetValueOrDefault(r.HeaderKey, r.HeaderDefault);
                // v1.0.9 第⑦项：展开/收起后光标锚定当前分类位置，不跳列表头
                _pendingFocusAnchor = r.HeaderKey;
                OpenChannelList(); // 展开收起：重建列表（焦点按锚点归位）
            }
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
        var favs = _favorites.TopGroup(_catalog.All.ToList());

        // v1.0.9 用户拍板：打开列表所有分类默认全展开，一屏可见；
        // 手动折叠仍支持（确认键切换），折叠状态由 _expanded 记录。
        if (!_expandedInit)
        {
            if (favs.Count > 0) _expanded["★ 收藏"] = true;
            foreach (var g in _catalog.Groups)
                _expanded[g.Title] = true;
            _expandedInit = true;
        }

        // v1.0.12（老板拍板）：总开关行挪进列表第一项（所有分类之前）——全开显「全部收起」、
        // 有任何折叠显「全部展开」；无信号组同样参与收放
        int collapsedNow = ListGroupKeys().Count(k => !_expanded.GetValueOrDefault(k, true));
        rows.Add(new Row { FoldToggle = true, Header = ListFoldController.ToggleLabel(collapsedNow) });

        if (favs.Count > 0)
        {
            bool favOpen = _expanded.GetValueOrDefault("★ 收藏", true);
            rows.Add(new Row { Header = HeaderText(ListHeaderStats.Format("★ 收藏", favs.Count, favs.Count, true), favOpen), HeaderKey = "★ 收藏", HeaderDefault = true });
            if (favOpen) rows.AddRange(favs.Select(c => new Row { Channel = c }));
        }
        foreach (var g in _catalog.Groups.Where(g => g.Channels.Count > 0))
        {
            bool open = _expanded.GetValueOrDefault(g.Title, false);
            // ⑧分类统计加可连数（v1.0.9）：口径与⑬统一——可连=至少一个来源可达的频道数；
            // 预检未出结果先显总数，后台静默刷新不断焦点（沿用 StateChanged 实时重建）。
            int total = g.Channels.Count;
            int connectable = g.Channels.Count(c => _precheck.Of(c) != ChannelHealth.Dead);
            bool known = g.Channels.Any(c => _precheck.Of(c) != ChannelHealth.Unknown);
            rows.Add(new Row
            {
                Header = HeaderText(ListHeaderStats.Format(g.Title, total, connectable, known), open),
                HeaderKey = g.Title,
                HeaderDefault = false,
            });
            if (open) rows.AddRange(g.Channels.Select(c => new Row { Channel = c }));
        }

        // 「无信号」分组（v1.0.8 第11项定名）：置底、默认展开（v1.0.9 用户拍板全展开）、只能看不能切；恢复可用自动移回原分类
        var dead = _catalog.All.Where(c => _precheck.Of(c) == ChannelHealth.Dead).ToList();
        if (dead.Count > 0)
        {
            bool dOpen = _expanded.GetValueOrDefault("✕ 无信号", true);
            rows.Add(new Row { Header = HeaderText(ListHeaderStats.Format("无信号", dead.Count, 0, true), dOpen), HeaderKey = "✕ 无信号", HeaderDefault = true });
            if (dOpen) rows.AddRange(dead.Select(c => new Row { Channel = c, DeadOnly = true }));
        }
        return rows;
    }

    /// <summary>预检状态实时刷新（列表开着时重建行；焦点与滚动位置由 ListView 保留）。</summary>
    private void RefreshListStatuses()
    {
        if (!_listOpen || _listView is null || _adapter is null) return;
        var rows = BuildRows();
        _adapter.ReplaceRows(rows);
        ApplyListWidth(rows);
    }

    /// <summary>列表宽度随内容（越小越好）：行宽估算含状态标识，经 ListMetrics 钳制（下限 360dp，上限半屏）。</summary>
    private void ApplyListWidth(List<Row> rows)
    {
        float density = Resources!.DisplayMetrics!.Density;
        float Estimate(string s, float sizeDp)
        {
            float px = sizeDp * density;
            float w = 0;
            foreach (var ch in s) w += ch > 127 ? px : px * 0.55f;
            return w;
        }
        float max = 0;
        foreach (var r in rows)
        {
            if (r.Header != null)
                max = Math.Max(max, Estimate(r.Header, 20));
            else if (r.Channel != null)
            {
                // 行宽 = 左右 padding(48×2) + 号(3字符) + 间距(32) + 星(1字符) + 间距(24) + 台名 + 状态标识
                var w = 96 + Estimate(new string('0', 3), 20) + 32
                        + Estimate("★", 20) + 24 + Estimate(r.Channel.Name, 22)
                        + Estimate("●", 20) + 24;
                max = Math.Max(max, w);
            }
        }
        int width = ListMetrics.ComputeWidthPx(max - 96 * density, density, Resources.DisplayMetrics.WidthPixels);
        _listPanel.LayoutParameters = new FrameLayout.LayoutParams(width, -1, GravityFlags.Left);
    }

    private void CloseChannelList()
    {
        _listOpen = false;
        _listPanel.Visibility = ViewStates.Gone;
        _listView = null;
    }

    private class Row
    {
        public string? Header;         // 分类行显示文本
        public string? HeaderKey;      // 分类行折叠键（频道行 null）
        public bool HeaderDefault;     // 未初始化时的默认展开态
        public Channel? Channel;
        public bool DeadOnly;          // 连不上置底组内台（只能看不能切）
        public bool FoldToggle;        // v1.0.12：总开关行（列表第一项，一键全部收起/展开）
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
            line.SetPadding(48, 20, 48, 20);

            void Style(TextView tv, string color, float size)
            {
                tv.SetTextColor(Color.ParseColor(color));
                tv.TextSize = size;
            }

            if (row.FoldToggle)
            {
                // v1.0.12：总开关行（列表第一项），样式同原顶部独立行
                var toggle = new TextView(ctx) { Text = row.Header };
                toggle.SetTextColor(InkTranslucency.TextPrimary(ctx));
                toggle.TextSize = 20;
                InkTranslucency.ApplyReadabilityShadow(toggle);
                line.AddView(toggle);
                return line;
            }

            if (row.Header != null)
            {
                var h = new TextView(ctx) { Text = row.Header };
                Style(h, "#A9CFEC", 18);
                InkTranslucency.ApplyReadabilityShadow(h);
                line.AddView(h);
                return line;
            }

            var ch = row.Channel!;
            var num = new TextView(ctx) { Text = $"{ch.Number,3}", Gravity = GravityFlags.Right };
            Style(num, InkTranslucency.TextSecondary, 20);
            InkTranslucency.ApplyReadabilityShadow(num);
            num.SetPadding(0, 0, 32, 0);
            line.AddView(num);

            var star = new TextView(ctx) { Text = _isFav(ch.Number) ? "★" : " " };
            Style(star, "#F0C563", 20);
            InkTranslucency.ApplyReadabilityShadow(star);
            star.SetPadding(0, 0, 24, 0);
            line.AddView(star);

            var name = new TextView(ctx) { Text = ch.Name };
            var isCur = _current?.Number == ch.Number;
            Style(name, isCur ? "#FFFFFF" : InkTranslucency.TextSecondary, 22);
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
            Style(dot, color, 18);
            InkTranslucency.ApplyReadabilityShadow(dot);
            dot.SetPadding(24, 0, 0, 0);
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

    // ---- 返回键（v1.0.8 防错）----

    private void ExitOrCollapseList()
    {
        // v1.0.12：弹窗开着的返回键已在 OnKeyDown 弹窗分支接管（=直接退出），到不了这里
        if (_listOpen)
        {
            CloseChannelList();
            // 列表开着回前台时重播被跳过（不打断操作），关列表时补校验：
            // 播放已死则立刻重播，绝不让用户关掉列表后面对黑屏（v1.0.8）
            if (_current != null && !_host.IsAlive)
            {
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
    /// </summary>
    private void ShowExitConfirmDialog()
    {
        if (_exitDialog != null) return; // 弹窗已开着：不重复弹
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = (int)(20 * Resources!.DisplayMetrics!.Density);
        box.SetPadding(pad * 2, pad * 2, pad * 2, pad);
        box.SetBackgroundColor(Color.ParseColor("#2B333B")); // 水墨深底

        var msg = new TextView(this) { Text = "确认退出道映？\n再按返回=直接退出 · 确认键=选中的按钮", TextSize = 20 };
        msg.SetTextColor(Color.ParseColor("#E8E2D6"));
        box.AddView(msg);

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var lp = new LinearLayout.LayoutParams(0, -2, 1f) { TopMargin = pad };
        var no = new Button(this) { Text = "取消（继续看）" };
        no.SetBackgroundColor(Color.ParseColor("#3A4750"));
        no.SetTextColor(Color.ParseColor("#E8E2D6"));
        no.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; };
        var yes = new Button(this) { Text = "退出" };
        yes.SetBackgroundColor(Color.ParseColor("#8A3B34"));
        yes.SetTextColor(Color.ParseColor("#F2EDE4"));
        yes.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; ExitAppCompletely(); };
        // v1.0.12（老板反馈）：焦点态可见化——默认底色无焦点反馈，遥控上分不清焦点在哪
        void StyleFocus(Button b, string normal, string focused)
        {
            b.SetBackgroundColor(Color.ParseColor(normal));
            b.FocusChange += (_, e) =>
                b.SetBackgroundColor(Color.ParseColor(e.HasFocus ? focused : normal));
        }
        StyleFocus(no, "#3A4750", "#5E7482");
        StyleFocus(yes, "#8A3B34", "#C25A50");
        // v1.0.10 修复④：左右键在「退出」与「取消（继续看）」间切换——显式指路（双保险，OnKeyDown 已接管）
        no.Id = View.GenerateViewId();
        yes.Id = View.GenerateViewId();
        no.NextFocusRightId = yes.Id;
        yes.NextFocusLeftId = no.Id;
        _exitNo = no;
        _exitYes = yes;
        buttons.AddView(no, lp);
        buttons.AddView(yes, lp);
        box.AddView(buttons);

        var dialogBuilder = new AlertDialog.Builder(this); // 构建器逐条调用：绑定返回可空不续点
        dialogBuilder.SetView(box);
        dialogBuilder.SetCancelable(true);
        var dialog = dialogBuilder.Create()!;
        _exitDialog = dialog;
        dialog.Show();
        no.RequestFocus(); // v1.0.10 修复④：默认焦点落「取消（继续看）」——防误退更稳；返回键=直接退出（v1.0.12 老板拍板）
        dialog.SetOnCancelListener(new DialogActionPeer(() => { _exitDialog = null; _exitNo = null; _exitYes = null; }));
        dialog.SetOnDismissListener(new DismissActionPeer(() => { _exitDialog = null; _exitNo = null; _exitYes = null; }));
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

    /// <summary>
    /// 真完全关闭（v1.0.9）：停播放→去任务→结束进程；重开必为冷启动。
    /// v1.0.13 fix26 退出铁律（老板 2026-11-08 15:22 拍板：任何时候都要能退出）——
    /// 窗口先消失（退出观感即时）；停流挪后台线程、最多陪 2 秒（v1.0.20 F 定稿上限），卡死也绝不吊住退出；
    /// 超时照退（JavaSystem.Exit 硬杀），绝不白屏卡死只能重启电视（老板 22:45 症状）。
    /// </summary>
    private void ExitAppCompletely()
    {
        try { FinishAndRemoveTask(); } catch { /* 先让窗口消失，退出即时可见 */ }
        try
        {
            var stopTask = Task.Run(() => { try { _host.Stop(); } catch { /* 已停不碍退出 */ } });
            stopTask.Wait(2000); // v1.0.20 F：等内核最多 2 秒；超时照退，退出路径绝不等待绝不阻塞
        }
        catch { }
        try { _precheckCts?.Cancel(); } catch { }
        try { MarkCleanExit(); } catch { } // v1.0.20 F：正常退出落干净标记，下次启动免自清理
        Java.Lang.JavaSystem.Exit(0); // 结束进程：重开即全新冷启动，不留后台音
    }

    /// <summary>v1.0.20 F 启动自清理：读上次退出标记——正常退出清标记放行；异常退出（崩溃/被杀）清取流缓存、
    /// 票据、拉黑账本、换路账本等可疑状态（与自愈8 ClearCache 同口径），落日志可查。</summary>
    private void CheckStartupSelfClean()
    {
        try
        {
            var p = _prefs ?? GetSharedPreferences("carrier", FileCreationMode.Private)!;
            bool clean = p.GetBoolean("clean_exit", false);
            if (clean) { p.Edit()?.PutBoolean("clean_exit", false)?.Apply(); return; }
            CrashNet.Record("[SelfClean] 上次未正常退出（崩溃/被杀）：清取流缓存与票据等可疑状态");
            _resolver.ClearCache();
        }
        catch (Exception ex) { CrashNet.Record("[SelfClean] 自清理检查异常（不影响启动）：" + ex.Message); }
    }

    private void MarkCleanExit()
    {
        var p = _prefs ?? GetSharedPreferences("carrier", FileCreationMode.Private)!;
        p.Edit()?.PutBoolean("clean_exit", true)?.Apply();
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

        // v1.0.12（老板反馈）：退出确认弹窗开着时按键全接管——左右键在「取消/退出」间显式切换
        //（焦点切换不再依赖系统焦点搜索，真机实测不可靠）；确认键=按当前焦点执行；
        // 返回键=第二次返回，直接退出（老板拍板：连续两次返回直接退出）
        if (_exitDialog != null)
        {
            switch (keyCode)
            {
                case Keycode.Back:
                    ExitAppCompletely();
                    return true;
                case Keycode.DpadLeft:
                    _exitNo?.RequestFocus();
                    return true;
                case Keycode.DpadRight:
                    _exitYes?.RequestFocus();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    if (_exitDialog.Window?.CurrentFocus is Button focused) focused.PerformClick();
                    else _exitNo?.PerformClick(); // 焦点异常兜底：落「取消」绝不误退
                    return true;
                default:
                    return true; // 其余按键弹窗期间不穿透，防止误触列表/换台
            }
        }

        // 收藏排序模式（v1.0.8 第14项）：独立状态机接管键位，退出恢复默认键位
        if (_sortMode)
        {
            HandleSortKey(keyCode);
            return true;
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
            // 列表开：⑥左右键=分类间跳转（用户 10:04 拍板，去掉与上下键重合的翻页/拖动逻辑）；
            // 上下/确认交给 ListView 焦点导航（上下=选台、确认=播放）；菜单或返回收起
            if (keyCode is Keycode.Menu or Keycode.Back) { ExitOrCollapseList(); return true; }
            if (keyCode is Keycode.DpadLeft or Keycode.DpadRight)
            {
                JumpCategory(keyCode == Keycode.DpadLeft ? -1 : +1);
                return true;
            }
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
            default:
                // 诊断：未接管的按键屏显键码，用于摸清各家遥控器的真实键值
                FlashInfo($"按键 {((int)keyCode)}");
                return true;
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
            if (!_settingsLongFired) ToggleSettingsPanel();
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
            // v1.0.13 去抖（老板 15:38 拍板）：连按不真切——按键即时统一台号显示，
            // 停稳 0.4 秒才对最终目标台起流；中间台不发真实连接，不打断、不误触发重连
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _debouncer.Tap(now, cand.EpgKey);
            ShowInfoBar($"{cand.Number}  {cand.Name}  连接中…");
            if (_pendingSwitch is { } old) _ui.RemoveCallbacks(old); // 上一发待切作废，只留最新目标
            Action pending = () =>
            {
                if (_debouncer.Target != cand.EpgKey) return;           // 又按了别的台：让位最新目标
                _debouncer.Clear();
                _pendingSwitch = null;
                Play(cand);
            };
            _pendingSwitch = pending;
            _ui.PostDelayed(pending, SwitchDebouncer.WindowMs);
            return;
        }
    }

    private readonly SwitchDebouncer _debouncer = new();
    private Action? _pendingSwitch;
    private long _switchQuietUntil; // 换台静默窗截止时刻：窗内重连提示合并只报最终状态

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
        var store = _healthStore;
        if (store != null) Task.Run(store.FlushNow);
        // v1.0.13 缓存分层：档位目录随退后台落盘，重启免重抓
        var vt = VariantTableStore.Shared;
        if (vt != null) Task.Run(vt.FlushNow);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!_resumedOnce) { _resumedOnce = true; return; } // 首启：OnCreate 已起播，不重复
        if (_current != null && !_listOpen)
        {
            Play(_current);          // 统一重接当前台（走取流缓存秒回），画面必重建
        }
    }

    protected override void OnDestroy()
    {
        _precheckCts?.Cancel();
        _precheckCts?.Dispose();
        _precheckCts = null;
        _ticketKeeper?.Dispose();
        _ticketKeeper = null;
        _governorTicker?.Dispose(); // ⑨A自动档巡检随宿主释放
        _governorTicker = null;
        _resourceTicker?.Dispose(); // fix31 ③：资源观测日志随宿主释放（复检建议2，防定时器泄漏）
        _resourceTicker = null;
        _statsTicker?.Dispose(); // ④画面信息行节拍
        _statsTicker = null;
        // fix51（B 路）：退出落盘移出主线程（对齐 OnPause 的 Task.Run 口径），但进程即将终止——
        // 后台写盘必须有界等待完成再放行（2 秒上限），防「进程先死文件没写完」丢健康账，不得丢档
        var exitStore = _healthStore;
        if (exitStore != null)
        {
            try { Task.Run(exitStore.FlushNow).Wait(2000); }
            catch (Exception ex) { CrashNet.Record("[Exit] 源健康账退出落盘等待异常：" + ex.Message); }
        }
        _host.Dispose();
        base.OnDestroy();
    }
}
