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
    // B 包第 12 项：列表页改五标签抽屉（全部/央视/卫视/地方台/收藏），左右键切标签
    private DrawerSupport.Tab _drawerTab = DrawerSupport.Tab.All;
    private List<TextView>? _tabViews;
    private int _drawerAiTierRowIndex = -1; // 抽屉行装配后：画质档位行位置（确认键切档用）
    // B 包第 14 项：底部节目单（央视网官方接口，没数据不硬造）
    private LinearLayout? _epgPanel;
    private TextView? _epgNow;
    private TextView? _epgNext;
    private CntvEpgClient? _epgClient;
    private System.Threading.CancellationTokenSource? _epgCts;
    private int _playSeq;
    private Channel? _current;
    private AiLiveTier _aiTier;         // AI 画质档位（启动时按设备解析）
    private SmartGovernor? _governor;   // A⑩：自动档会话临时档调节器（手动档=天花板，降档不写盘）
    private InfoLineHub _infoHub = new(); // A②：信息行单写入口（防重刷总闸+优先级让位）
    private System.Threading.Timer? _governorTicker; // A⑩：5 秒巡检喂实测统计
    private bool _carrierTexture;      // 本次会话是否纹理载体（v1.0.7 启动自检决定）
    private bool _frameEverShown;      // 本次启动是否出过画面：出过则换台先留旧画面，延迟亮牌
    private bool _frameForCurrentSwitch; // 本轮换台是否已出首帧
    private Action? _pendingLoading;   // 延迟亮牌任务（换台后仍无首帧才亮）
    private Action? _loadingAnimRun;   // 亮牌期间的「正在连接」点动画
    private int _loadingSeq;
    private Handler _ui = new Handler(Looper.MainLooper!);

    // 退出确认（v1.0.9 用户拍板）：播放态按返回弹窗二次确认；确认后真完全关闭，
    // 重开是全新冷启动。弹窗开着时返回键=取消继续播放。
    private AlertDialog? _exitDialog;
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
    private bool _warmupEnabled = true;     // A⑥：后台预热开关（细案 §二设置页，持久化）
    private bool _autostartEnabled = false; // A⑥：开机自启开关（默认关，ROM 授权实测为准）
    private bool _abi32;                     // 启动时设备档位（画质档重算用）
    private long _totalMem;
    private global::Android.Content.ISharedPreferences? _prefs;
    private System.Threading.CancellationTokenSource? _precheckCts;

    // 频道列表折叠状态（v1.0.8）：组标题 → 展开？；未初始化时收藏组+当前台所在组自动展开
    private readonly Dictionary<string, bool> _expanded = new();
    private bool _expandedInit;

    // v1.0.9 第④项：列表旁操作提示去掉，改为实时画面信息行（原/现帧率/分辨率）
    private TextView? _statsLine;
    private TextView? _foldToggle; // ⑪列表页总开关行（一键展开/折叠）
    private System.Threading.Timer? _statsTicker;
    // v1.0.9 第⑦/⑪项：重建列表的焦点锚点（分类键或 C:频道号），锚定原位置不跳头
    private string? _pendingFocusAnchor;
    // v1.0.9 第⑬项：本台会话实际取播的来源变体（健康表优先取最近验证可用来源）
    private Channel? _playSource;
    // v1.0.10 第①项：源健康账本地存储（应用私有目录，跨启动保留已验证源）
    private SourceHealthStore? _healthStore;
    private readonly SourceBackoffLedger _backoff = new(); // A⑬/A⑥：同一本 403 退避账（竞速与预热共用）
    private readonly Dictionary<string, long> _nextProbeAtMs = new(); // A⑥：每台下次到期时刻（TickCount64）
    private volatile bool _foregroundBusy; // A⑥ PauseGate：前台换台/连接期间预热让路

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        TaoPalette.Select(0); // 水墨默认

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

        // 持久化设置（v1.0.8）：AI 画质档位与预检开关跨启动记忆
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        DeviceProfile.AiEnhanceTierSetting = _prefs.GetString("ai_tier", "auto") ?? "auto";
        _precheckEnabled = _prefs.GetBoolean("precheck_enabled", true);
        _warmupEnabled = _prefs.GetBoolean("warmup_enabled", true);       // A⑥：后台预热（默认开）
        _autostartEnabled = _prefs.GetBoolean("autostart_enabled", false); // A⑥：开机自启（默认关）

        // AI 画质实时档（v1.0.8）：auto 按内存分档，绝不在带不动的设备硬开
        _aiTier = LiveQuality.Pick(DeviceProfile.AiEnhanceTierSetting, _abi32, _totalMem);
        _governor = new SmartGovernor(_aiTier); // A⑩：基准档=静态分档，运行中按实测升降（会话临时档）

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
        _resolver = new StreamResolver();
        _host = new LiveVideoHost { TotalMemBytes = _totalMem }; // ⑩内存水位自适应口径注入
        WireHostEvents();

        // 开机预检握手（v1.0.8）：全台按序轻探（取票/头分片，不建视频传输），限速防判异常。
        // 预检后台进行，不阻塞当前频道播放；状态实时刷新进呼出列表。
        _precheck = new ChannelPrecheck(ProbeChannelAsync, intervalMs: 600);
        // v1.0.10 第①项：源健康账落盘本地——重启后已验证源直接可用，换台不用从零再探
        // A 类第 7 项：央视频 guid 设备身份持久化，与源健康账同目录落盘
        DeviceIdentity.Attach(FilesDir!.AbsolutePath);
        // A 类第6项：预热落盘层（/files/warmup/，与 source_health.json 并列）——长效源起播零网络探测
        _resolver.WarmupDisk = new WarmupStore(System.IO.Path.Combine(FilesDir!.AbsolutePath, "warmup"));
        _resolver.WarmupDisk.Load();
        // A 类第6项：App 打开即拉起后台预热服务（同进程共享同一预热引擎，绝不起第二份）
        try
        {
            var svc = new Intent(this, typeof(WarmupService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O && _warmupEnabled) StartForegroundService(svc);
            else if (_warmupEnabled) StartService(svc);
        }
        catch (Exception svcEx) { CrashNet.Record($"[Warmup] 保活服务拉起失败（功能不残废）：{svcEx.Message}"); }
        _healthStore = new SourceHealthStore(FilesDir!.AbsolutePath);
        _healthStore.Load();
        _precheck.AttachHealthStore(_healthStore);
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
            // 循环预检（v1.0.8 用户拍板）：常驻后台轮，保态复测列表不闪烁；
            // 全列表 83 路 × 600ms ≈ 50 秒/轮 + 轮间歇 20 秒 ≈ 1~1.5 分钟一轮（限速防判异常流量）；
            // 票据静默续期：每 4 分钟为当前台换新票
            _precheckCts = new System.Threading.CancellationTokenSource();
            _ = Task.Run(() => PrecheckLoopAsync(_precheckCts.Token));
            // v1.0.9 第④项：画面信息行实时刷新（2 秒节拍，列表开着才写字）
            _statsTicker = new System.Threading.Timer(_ => _ui.Post(() =>
            {
                if (_statsLine is null) return;
                var (ofps, cfps, ow, oh, cw, chh) = _host.SnapshotStats();
                // v1.0.10 修复⑤：起播后重试解析，到限显「--」；并显 AI 档位（口径同【二】）
                // A 类第5项：信息行显实际档（在跑的档，不是选的档；A⑩临时档变化可见）
                _statsLine.Text = FrameStatsLine.FormatEx(ofps, cfps, ow, oh, cw, chh,
                    LiveQuality.TierName(EffectiveTier), _host.StatsExhausted);
            }), null, 2000, 2000);
            // A 类第10项：5 秒巡检喂实测——水位偏低提前降档防卡；降档=会话内临时档，平稳 10 分钟自动回位
            _governorTicker = new System.Threading.Timer(_ => _ui.Post(() =>
            {
                if (_governor is null || _current is null || !_frameEverShown) return;
                var before = _governor.Effective;
                var (fps, dropped, stalled) = _host.SnapshotGovernorStats();
                if (!_governor.Tick(dropped, stalled, System.Environment.TickCount64)) return;
                OnAutoTierChanged(before, stalled);
            }), null, 5000, 5000);
            _ticketKeeper = new System.Threading.Timer(async _ =>
            {
                var cur = _current;
                if (cur is null) return;
                try { await _resolver.ResolvePlayUrlAsync(cur, force: true); }
                catch (Exception ex) { CrashNet.Record($"[TicketKeeper] 续票失败下个周期再试：{ex.Message}"); }
            }, null, 240_000, 240_000);
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
        _host.ReconnectRefresher = async () =>
        {
            var src = _playSource ?? _current;
            if (src is null) return null;
            var fresh = await _resolver.ResolvePlayUrlAsync(src, force: true);
            if (fresh is null) return null;
            var opts = new List<(string Key, string Value)>(fresh.Options);
            foreach (var (k, v) in LiveQuality.OptionsFor(EffectiveTier)) opts.Add((k, v)); // A⑩：带实际生效档
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
            var from = EffectiveTier;
            if (DeviceProfile.AiEnhanceTierSetting == "high") DeviceProfile.AiEnhanceTierSetting = "low";
            else if (DeviceProfile.AiEnhanceTierSetting == "low") DeviceProfile.AiEnhanceTierSetting = "off";
            else if (_governor != null && _governor.Effective > AiLiveTier.Off)
                _governor.Tick(SmartGovernor.DropRateThreshold + 1, true, System.Environment.TickCount64); // A⑩：会话内立即降一档，不写持久化
            if (DeviceProfile.AiEnhanceTierSetting != "auto")
            {
                _aiTier = LiveQuality.Pick(DeviceProfile.AiEnhanceTierSetting, _abi32, _totalMem);
                _prefs?.Edit()?.PutString("ai_tier", DeviceProfile.AiEnhanceTierSetting)?.Apply(); // 仅显式档自救才落盘
            }
            _governor = new SmartGovernor(_aiTier); // 基准档随新设置重开
            // A 类第5项·降档打点：旧档→新档+触发原因，自救不再静默
            CrashNet.Record($"[画质] 自救降档：{LiveQuality.TierName(from)}→{LiveQuality.TierName(EffectiveTier)}（卡顿自救，⑫②级）");
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
            // ⑬健康表回写：播放实测成功覆盖预检结论（取播来源变体优先；Unix 毫秒，落盘可比）
            if (_playSource != null) _precheck.ReportSourceResult(_playSource, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

        // 3. 频道列表（菜单键呼出，焦点式；B 包第 12 项改五标签抽屉：全部/央视/卫视/地方台/收藏）
        _listPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listPanel.SetBackgroundColor(InkTranslucency.PanelColor(this)); // v1.0.9 第③项：底色加深并随亮度自适应
        _listPanel.Visibility = ViewStates.Gone;
        _root.AddView(_listPanel, new FrameLayout.LayoutParams(-1, -1));

        // 4. 底部节目单（B 包第 14 项）：官方接口有数据才显示（没数据不硬造）
        _epgPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _epgPanel.SetBackgroundColor(InkTranslucency.PanelColor(this));
        _epgPanel.SetPadding(36, 18, 36, 22);
        _epgPanel.Visibility = ViewStates.Gone;
        _epgNow = new TextView(this) { TextSize = 18 };
        _epgNow.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_epgNow);
        _epgNext = new TextView(this) { TextSize = 15 };
        _epgNext.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_epgNext);
        _epgPanel.AddView(_epgNow);
        _epgPanel.AddView(_epgNext);
        _root.AddView(_epgPanel, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.Left,
        });

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

    private void Play(Channel? ch)
    {
        if (ch is null) return;
        _foregroundBusy = true; // A⑥ PauseGate：换台/连接期间预热让路
        int seq = ++_playSeq;
        _current = ch;
        _frameForCurrentSwitch = false;
        CloseChannelList();
        // v1.0.10 修复⑦：撤旧画面的同时铺干净底色，GPU 残留旧缓冲绝不外露（花屏竖条）；
        // 新流首帧到齐才撤底色亮画面（FrameShown 处清回透明）
        _videoContainer?.SetBackgroundColor(Color.ParseColor(TaoPalette.Current.Bg));
        // 雷二·延迟亮牌：出过画面的情况下先让旧画面亮着（纹理载体保留最后一帧），
        // 0.8 秒内新画面出来了用户无感；没出来才亮「正在连接」，且亮着就不撤直到首帧
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
                _ui.Post(() =>
                {
                    if (seq != _playSeq) return;
                    _foregroundBusy = false; // A⑥ PauseGate：取流失败也让路（重试由分级自救承担）
                    HideLoading();
                    FlashInfo($"{ch.Name}  暂时没接上，先看别的台");
                });
                return;
            }
            // 透传头 + AI 画质滤镜合并为选项列表（v1.0.8）
            var opts = new List<(string Key, string Value)>(resolved.Options);
            foreach (var (k, v) in LiveQuality.OptionsFor(EffectiveTier)) opts.Add((k, v)); // A⑩：带实际生效档
            // A 类第5项·起播打点：档位/设置值/滤镜参数可对账（AI 生效性核查 §4 判据）
            var filters = string.Join(";", opts.Where(o => QualityFilterKeysForLog.Contains(o.Key)).Select(o => $"{o.Key}={o.Value}"));
            CrashNet.Record($"[画质] 起播（{ch.Name}）：档位 {LiveQuality.TierName(EffectiveTier)}（设置值 {LiveQuality.DisplayName(DeviceProfile.AiEnhanceTierSetting)}），滤镜 {filters}");
            _ui.Post(() =>
            {
                if (seq != _playSeq) return;
                _host.Play(resolved.Url, opts);
                ShowInfoBar($"{ch.Number}  {ch.Name}");
                RefreshEpg(ch); // B 包第 14 项：进台成功即拉官方节目单（换台即刷新，无数据不显示）
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
        nameof(ConnectFailureKind.RateLimited) => "取流被限流，约 1 分钟后自动重试，先看别的台也行",
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
                await Task.Delay(5_000, ct);
            }
            catch (global::System.OperationCanceledException) { return; }
            catch (Exception ex) { CrashNet.Record($"[Precheck] 预检单轮失败下一轮再来：{ex.Message}"); }
        }
    }

    /// <summary>A⑥单轮：只探「已到期且未退避且前台空闲」的台；首见台登记节拍不立即探（读盘起播口径）。</summary>
    private async Task RunDueProbesAsync(System.Threading.CancellationToken ct)
    {
        long now = System.Environment.TickCount64;
        if (_foregroundBusy) return; // PauseGate：前台连接优先，预热就地挂起，下轮再看（不消耗到期时间）
        foreach (var ch in _catalog.All)
        {
            if (ct.IsCancellationRequested) return;
            if (_foregroundBusy) return;
            var key = $"{ch.SourceId}:{ch.EpgKey}";
            if (!_nextProbeAtMs.TryGetValue(key, out var due))
            {
                _nextProbeAtMs[key] = now + WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs;
                continue; // 首见只登记，不立即探
            }
            if (now < due) continue;
            if (_backoff.IsFrozen(ch.SourceId, now))
            {
                _nextProbeAtMs[key] = now + 60_000; // 退避期：挂起一个短周期后重看（细案：绝不绕闸）
                continue;
            }
            _nextProbeAtMs[key] = now + WarmupPolicy.PaceOf(ch.SourceId).RefreshIntervalMs;
            try
            {
                var ok = await ProbeChannelAsync(ch).ConfigureAwait(false);
                _precheck.ReportSourceResult(ch, ok, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (!ok) _backoff.RecordLimited(ch.SourceId, System.Environment.TickCount64); // 探测 403/失败入同一本退避账
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
        // B 包第 4 项：画质行一行紧凑式（档位与操作提示合一，不再双行展开）
        AddRow($"画质增强 · {LiveQuality.DisplayName(DeviceProfile.AiEnhanceTierSetting)} · 确认切换",
            CycleAiTier, "#F7F5EE");
        AddRow($"预检握手：{(_precheckEnabled ? "开" : "关")}（确认键切换）",
            TogglePrecheck, "#F7F5EE");
        AddRow($"后台预热：{(_warmupEnabled ? "开" : "关")}（确认键切换）",
            ToggleWarmup, "#F7F5EE");
        AddRow($"开机自启：{(_autostartEnabled ? "开" : "关")}（确认键切换，装后须先开一次 App）",
            ToggleAutostart, "#F7F5EE");
        AddRow("崩溃日志（点按查看）", ShowCrashLog, "#C9D6E2");
        // B 包第 2 项：砍掉「电视系统」整段入口——系统设置由遥控器设置键长按直达，软件内不再列
        AddLabel("返回键收起 · 长按设置键直达系统设置");
    }

    /// <summary>画质档循环（自动→低→高→自动，v1.0.10 全中文）：立即按新档重起当前台生效，档位持久化。</summary>
    private void CycleAiTier()
    {
        var next = SettingsCycler.NextAiTier(DeviceProfile.AiEnhanceTierSetting);
        DeviceProfile.AiEnhanceTierSetting = next;
        _prefs?.Edit()?.PutString("ai_tier", next)?.Apply();
        _aiTier = LiveQuality.Pick(next, _abi32, _totalMem);
        _governor = new SmartGovernor(_aiTier); // A⑩：基准档随设置重开，旧会话临时档作废
        RefreshSettingsRows();
        FlashInfo($"画质：{LiveQuality.DisplayName(next)}" + (_aiTier != LiveQuality.Pick(next, _abi32, _totalMem) ? "（本机按性能降档）" : ""));
        if (_current != null) Play(_current); // 滤镜随媒体参数生效：重起当前台立即应用
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
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetBackgroundColor(Color.ParseColor("#2B333B"));
            int pad = (int)(24 * Resources!.DisplayMetrics!.Density);
            box.SetPadding(pad, pad, pad, pad);
            var title = new TextView(this) { Text = "崩溃日志（全屏）", TextSize = 22 };
            title.SetTextColor(Color.ParseColor("#F2EDE4"));
            box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = pad });
            var scroll = new ScrollView(this) { };
            var body = new TextView(this) { Text = text, TextSize = 15 };
            body.SetTextIsSelectable(false);
            body.SetTextColor(Color.ParseColor("#D8E2EC"));
            InkTranslucency.ApplyReadabilityShadow(body);
            scroll.AddView(body);
            box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var clean = new Button(this) { Text = "一键清理" };
            clean.SetBackgroundColor(Color.ParseColor("#3A4750"));
            clean.SetTextColor(Color.ParseColor("#E8E2D6"));
            clean.Click += (_, _) =>
            {
                try { System.IO.File.Delete(path.AbsolutePath); FlashInfo("崩溃日志已清空"); }
                catch { FlashInfo("清理失败，请重试"); }
                _crashDialog?.Dismiss();
            };
            var close = new Button(this) { Text = "关闭" };
            close.SetBackgroundColor(Color.ParseColor("#8A3B34"));
            close.SetTextColor(Color.ParseColor("#F2EDE4"));
            close.Click += (_, _) => _crashDialog?.Dismiss();
            btns.AddView(clean, new LinearLayout.LayoutParams(0, -2, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, -2, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });
            var dlg = new AlertDialog.Builder(this)
                .SetView(box)
                .SetCancelable(true)
                .Create()!;
            _crashDialog = dlg;
            dlg.Show();
            dlg.Window!.SetLayout(-1, -1); // 全屏
            close.RequestFocus();
            dlg.SetOnDismissListener(new DismissActionPeer(() => _crashDialog = null));
        }
        catch { FlashInfo("日志读取失败"); }
    }

    private AlertDialog? _crashDialog; // B 包第 9 项：全屏崩溃日志窗

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

    /// <summary>A⑩：实际生效档（自动档临时调节含在内；手动档=天花板）。</summary>
    private AiLiveTier EffectiveTier => _governor?.Effective ?? _aiTier;

    /// <summary>A⑩/A⑤：自动档变化后统一动作——重挂滤镜、调档打点、信息行（档位变化优先级，走单写入口）。</summary>
    private void OnAutoTierChanged(AiLiveTier before, bool stalled)
    {
        var now = EffectiveTier;
        CrashNet.Record($"[画质] 自动调档：{LiveQuality.TierName(before)}→{LiveQuality.TierName(now)}（{(stalled ? "播放停滞" : "掉帧超阈")}，会话临时档，平稳 10 分钟回位）");
        _host.ApplyQualityTier(LiveQuality.OptionsFor(now));
        FlashInfo(now < before
            ? $"画质已自动调为「{LiveQuality.TierName(now)}」（流畅优先，平稳后自动回位）"
            : $"画质已自动回位「{LiveQuality.TierName(now)}」");
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
            onLimited: id => { _backoff.RecordLimited(id, System.Environment.TickCount64); return Task.CompletedTask; });
        var picked = variants.FirstOrDefault(v => v.SourceId == winner);
        var source = picked ?? ch;
        CrashNet.Record($"[竞速] {ch.Name} 无已验证来源，多端点竞速{(winner is null ? "全挂回本台" : $"胜出：{source.SourceId}")}（A⑬）");
        if (winner != null) _precheck.ReportSourceResult(source, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // 竞速结果回写健康账
        return source;
    }

    /// <summary>画质滤镜键集合（A⑤起播打点摘选用）。</summary>
    private static readonly string[] QualityFilterKeysForLog = { "video-filter", "sharpen-amplitude", "contrast", "saturation", "gamma" };

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

        // B 包第 12 项：抽屉头部——标题 + 台数 + 五标签行（当前标签朱砂底高亮，确认/左右键切换）
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetPadding(48, 40, 48, 8);
        var title = new TextView(this) { Text = "道映·频道", TextSize = 24 };
        title.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(title);
        header.AddView(title);
        var count = new TextView(this) { Text = $"  共 {_catalog.All.Count()} 路", TextSize = 15 };
        count.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(count);
        header.AddView(count);
        _listPanel.AddView(header);

        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabRow.SetPadding(48, 8, 48, 12);
        _tabViews = new List<TextView>();
        foreach (DrawerSupport.Tab t in Enum.GetValues(typeof(DrawerSupport.Tab)))
        {
            var tv = new TextView(this) { Text = DrawerSupport.Label(t), TextSize = 16 };
            tv.SetPadding(24, 10, 24, 10);
            InkTranslucency.ApplyReadabilityShadow(tv);
            var lpTab = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = 12 };
            tabRow.AddView(tv, lpTab);
            _tabViews.Add(tv);
            tv.Click += (_, _) => { _drawerTab = t; OpenChannelList(); };
        }
        StyleTabs();
        _listPanel.AddView(tabRow);

        // v1.0.9 第④项（用户拍板）：操作提示全部去掉，列表旁改为实时画面信息行
        //（原帧率/现帧率/原分辨率/现分辨率，2 秒节拍刷新；测量缺失显示「检测中」）
        _statsLine = new TextView(this)
        {
            Text = "画面信息检测中",
            TextSize = 18,
        };
        _statsLine.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_statsLine);
        _statsLine.SetPadding(48, 8, 48, 20);
        _listPanel.AddView(_statsLine);

        // v1.0.10 修复⑪：顶部总开关行——全开显「全部收起」、有任何折叠显「全部展开」，一键切换；
        // 无信号组同样参与收放；打开列表默认习惯不变（总开关只管手动触发）
        int collapsedNow = ListGroupKeys().Count(k => !_expanded.GetValueOrDefault(k, true));
        _foldToggle = new TextView(this)
        {
            Text = ListFoldController.ToggleLabel(collapsedNow),
            TextSize = 20,
        };
        _foldToggle.SetTextColor(InkTranslucency.TextPrimary(this));
        _foldToggle.SetPadding(48, 12, 48, 12);
        _foldToggle.Focusable = true;
        var foldFocus = InkTranslucency.FocusDrawable();
        _foldToggle.FocusChange += (_, e) =>
        {
            if (e.HasFocus) _foldToggle!.Background = foldFocus;
            else if (_foldToggle.Background == foldFocus) _foldToggle.Background = null;
        };
        _foldToggle.Click += (_, _) => ToggleAllGroups();
        _listPanel.AddView(_foldToggle);

        _listView = new ListView(this) { Divider = null };
        // 全局透明化（v1.0.8 第15项）：焦点效果=低透明度细边框+极淡底色，不复用实色高亮
        _listView.Selector = InkTranslucency.FocusDrawable();
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
            // B 包第 12 项：画质档位行确认键循环切档（关→低→高→关，不收抽屉）
            if (r.HeaderKey == "__aitier__") { CycleAiTier(); OpenChannelList(); return; }
            // 分组行（收藏/无信号标题）：v4 抽屉口径纯展示，不再折叠
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
        // B 包第 12 项：五标签抽屉行装配（v4 drawerRows 口径移植，纯函数可测）。
        // 首行=画质增强档位行（确认键循环 关→低→高→关）；AI 档位文案取实时档。
        var favOrder = _favorites.Numbers.ToList();
        var drawerRows = DrawerSupport.BuildRows(_drawerTab, _catalog.All.ToList(), favOrder,
            c => _precheck.Of(c), AiTierRowLabel());
        _drawerAiTierRowIndex = drawerRows.FindIndex(r => r is DrawerSupport.DrawerRow.AiTier);

        foreach (var r in drawerRows)
        {
            switch (r)
            {
                case DrawerSupport.DrawerRow.AiTier ai:
                    rows.Add(new Row { Header = ai.Label, HeaderKey = "__aitier__", HeaderDefault = true });
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

    /// <summary>画质增强档位行文案（确认键循环 关→低→高→关，实时档口径）。</summary>
    private string AiTierRowLabel()
    {
        var setting = DeviceProfile.AiEnhanceTierSetting;
        return setting switch
        {
            "high" => "画质增强：高（确认切换）",
            "low" => "画质增强：低（确认切换）",
            _ => _aiTier == AiLiveTier.Off ? "画质增强：关（确认切换）" : "画质增强：低（确认切换）",
        };
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

            if (row.Header != null)
            {
                var h = new TextView(ctx) { Text = row.Header };
                Style(h, "#A9CFEC", 18);
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
                        img.SetMaxHeight((int)(28 * ctx.Resources.DisplayMetrics.Density));
                        img.SetImageResource(resId);
                        var lpLogo = new LinearLayout.LayoutParams(-2, (int)(28 * ctx.Resources.DisplayMetrics.Density))
                        { RightMargin = 16, Gravity = GravityFlags.CenterVertical };
                        line.AddView(img, lpLogo);
                    }
                }
                catch { /* 台标渲染失败不碍选台 */ }
            }
            if (!dead && logoRes == null)
            {
                var ph = new TextView(ctx) { Text = ch.Name.Substring(0, 1) };
                Style(ph, InkTranslucency.TextSecondary, 14);
                ph.Gravity = GravityFlags.Center;
                ph.SetBackgroundColor(Color.ParseColor("#3A4750"));
                var lpPh = new LinearLayout.LayoutParams((int)(40 * ctx.Resources.DisplayMetrics.Density), (int)(28 * ctx.Resources.DisplayMetrics.Density))
                { RightMargin = 16, Gravity = GravityFlags.CenterVertical };
                line.AddView(ph, lpPh);
            }
            if (dead)
            {
                var spacer = new View(ctx) { };
                var lpSp = new LinearLayout.LayoutParams(0, 1) { RightMargin = 16 };
                line.AddView(spacer, lpSp); // 无信号行行首不放台标（v4 定稿状态2）
            }

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

    // ---- 节目单（B 包第 14 项：央视网官方接口，没数据不硬造）----

    /// <summary>换台即刷新：后台拉官方节目单，有数据才亮底部节目区；失败/映射外/无数据静默隐藏。</summary>
    private void RefreshEpg(Channel ch)
    {
        _epgClient ??= new CntvEpgClient();
        var seq = _playSeq;
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
                _epgPanel.Visibility = ViewStates.Visible;
            });
        });
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
    /// </summary>
    private void ShowExitConfirmDialog()
    {
        if (_exitDialog != null) return; // 弹窗已开着：不重复弹
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = (int)(20 * Resources!.DisplayMetrics!.Density);
        box.SetPadding(pad * 2, pad * 2, pad * 2, pad);
        box.SetBackgroundColor(Color.ParseColor("#2B333B")); // 水墨深底

        var msg = new TextView(this) { Text = "确认退出道映？", TextSize = 20 };
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
        // v1.0.10 修复④：左右键在「退出」与「取消（继续看）」间切换——显式指路，不靠焦点搜索碰运气
        // B 包第 11 项：按钮焦点可见化——聚焦按钮朱砂底+浅字，失焦水墨底+次色，绝不靠默认裸按钮
        no.Id = View.GenerateViewId();
        yes.Id = View.GenerateViewId();
        no.NextFocusRightId = yes.Id;
        yes.NextFocusLeftId = no.Id;
        void StyleExitButton(Button b, string focusBg, string focusFg, string idleBg, string idleFg)
        {
            b.SetBackgroundColor(Color.ParseColor(idleBg));
            b.SetTextColor(Color.ParseColor(idleFg));
            b.FocusChange += (_, ev) =>
            {
                if (ev.HasFocus) { b.SetBackgroundColor(Color.ParseColor(focusBg)); b.SetTextColor(Color.ParseColor(focusFg)); }
                else { b.SetBackgroundColor(Color.ParseColor(idleBg)); b.SetTextColor(Color.ParseColor(idleFg)); }
            };
        }
        StyleExitButton(no, "#8A3B34", "#F7F5EE", "#3A4750", "#D8E2EC");
        StyleExitButton(yes, "#8A3B34", "#F7F5EE", "#8A3B34", "#F2EDE4");
        buttons.AddView(no, lp);
        buttons.AddView(yes, lp);
        box.AddView(buttons);

        var dialog = new AlertDialog.Builder(this)
            .SetView(box)
            .SetCancelable(true)
            .Create()!;
        _exitDialog = dialog;
        dialog.Show();
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

    /// <summary>真完全关闭（v1.0.9）：停播放→去任务→结束进程；重开必为冷启动。</summary>
    private void ExitAppCompletely()
    {
        try { _host.Stop(); } catch { /* 已停不碍退出 */ }
        try { _precheckCts?.Cancel(); } catch { }
        FinishAndRemoveTask();
        Java.Lang.JavaSystem.Exit(0); // 结束进程：重开即全新冷启动，不留后台音
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
            default:
                // B 包第 8 项：键码屏显静默化——未接管的按键不再屏显键码，只落日志排查
                CrashNet.Record($"[Key] 未接管键值 {((int)keyCode)}");
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
        var store = _healthStore;
        if (store != null) Task.Run(store.FlushNow);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!_resumedOnce) { _resumedOnce = true; return; } // 首启：OnCreate 已起播，不重复
        if (_current != null && !_listOpen)
        {
            _frameEverShown = false; // 复位：重播按冷启动路径立即亮牌
            Play(_current);          // 统一重接当前台（走取流缓存秒回），画面必重建
        }
    }

    protected override void OnDestroy()
    {
        _precheckCts?.Cancel();
        _precheckCts?.Dispose();
        _precheckCts = null;
        _epgCts?.Cancel(); // B 包第 14 项：退出停节目单拉取
        _epgCts?.Dispose();
        _epgCts = null;
        _ticketKeeper?.Dispose();
        _ticketKeeper = null;
        _statsTicker?.Dispose(); // ④画面信息行节拍
        _statsTicker = null;
        _governorTicker?.Dispose(); // A⑩调速巡检节拍
        _governorTicker = null;
        _healthStore?.FlushNow(); // v1.0.10：退出前把源健康账落盘
        _host.Dispose();
        base.OnDestroy();
    }
}
