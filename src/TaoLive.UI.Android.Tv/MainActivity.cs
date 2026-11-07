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
    private bool _frameEverShown;      // 本次启动是否出过画面：出过则换台先留旧画面，延迟亮牌
    private bool _frameForCurrentSwitch; // 本轮换台是否已出首帧
    private Action? _pendingLoading;   // 延迟亮牌任务（换台后仍无首帧才亮）
    private Action? _loadingAnimRun;   // 亮牌期间的「正在连接」点动画
    private int _loadingSeq;
    private Handler _ui = new Handler(Looper.MainLooper!);

    // 返回键防错（v1.0.8）：播放态第一次按只提示，2 秒内再按才退后台
    private long _lastBackMs;

    // 收藏排序模式（v1.0.8 第14项，用户 23:23 拍板）：独立状态机，退出恢复默认键位
    private bool _sortMode;
    private Channel? _sortChannel;
    private readonly List<int> _sortBackup = new(); // 返回键取消复原用

    // 软件设置面板（v1.0.8，用户拍板）：底部滑入透明面板；设置键 4077 单击呼出、长按跳系统设置
    private LinearLayout? _settingsPanel;
    private bool _settingsPanelOpen;
    private bool _settingsLongFired;
    private bool _precheckEnabled = true;   // 循环预检开关（设置面板可切，持久化）
    private bool _abi32;                     // 启动时设备档位（画质档重算用）
    private long _totalMem;
    private Android.Content.ISharedPreferences? _prefs;
    private System.Threading.CancellationTokenSource? _precheckCts;

    // 频道列表折叠状态（v1.0.8）：组标题 → 展开？；未初始化时收藏组+当前台所在组自动展开
    private readonly Dictionary<string, bool> _expanded = new();
    private bool _expandedInit;

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
        // - 广西 gxtv 1 路：取票接口 404，待适配。
        _catalog.RegisterSource(new XatvSource());
        _catalog.RegisterSource(new NmtvSource());
        _catalog.Rebuild();
        _resolver = new StreamResolver();
        _host = new LiveVideoHost();
        WireHostEvents();

        // 开机预检握手（v1.0.8）：全台按序轻探（取票/头分片，不建视频传输），限速防判异常。
        // 预检后台进行，不阻塞当前频道播放；状态实时刷新进呼出列表。
        _precheck = new ChannelPrecheck(ProbeChannelAsync, intervalMs: 600);
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
            _ticketKeeper = new System.Threading.Timer(async _ =>
            {
                var cur = _current;
                if (cur is null) return;
                try { await _resolver.ResolvePlayUrlAsync(cur, force: true); }
                catch { /* 续票失败下个周期再试，重连上限兜底 */ }
            }, null, 240_000, 240_000);
        }
        catch (Exception ex)
        {
            // 纹理载体接线当场报错：记录原因，永久回退稳定载体，同一次启动内重建，不闪退
            CrashNet.Record(ex.ToString());
            CarrierPolicy.DisableTexture(this, "启动接线异常: " + ex.Message);
            try
            {
                _host.Dispose();
                _host = new LiveVideoHost();
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
        _host.ReconnectRefresher = async () =>
        {
            if (_current is null) return null;
            var fresh = await _resolver.ResolvePlayUrlAsync(_current, force: true);
            if (fresh is null) return null;
            var opts = new List<(string Key, string Value)>(fresh.Options);
            foreach (var (k, v) in LiveQuality.OptionsFor(_aiTier)) opts.Add((k, v));
            return new RefreshedStream { Url = fresh.Url, Options = opts };
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
            if (_carrierTexture) CarrierPolicy.MarkOk(this); // 纹理载体活到首帧：判兼容
            if (_current != null) _precheck.MarkState(_current, ChannelHealth.Alive); // 播放实测存活
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
        _loadingPanel.SetBackgroundColor(Color.Argb(200, 0x2B, 0x33, 0x3B));
        _loadingText = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = 24,
        };
        _loadingText.SetTextColor(Color.ParseColor("#F7F5EE"));
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
        _infoBar.SetTextColor(Color.ParseColor("#F7F5EE"));
        _infoBar.SetBackgroundColor(Color.Argb(InkTranslucency.InfoAlpha, 0x52, 0x70, 0x8A)); // 透明化基线（v1.0.8 第15项）
        _infoBar.SetPadding(36, 16, 36, 16);
        _infoBar.TextSize = 22;
        _root.AddView(_infoBar, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = 48,
        });

        // 3. 频道列表（菜单键呼出，焦点式；分类树形可折叠+收藏置顶+宽度随内容）
        _listPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listPanel.SetBackgroundColor(InkTranslucency.PanelColor()); // 全局透明化：极淡底，画面仍可见（v1.0.8 第15项）
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

    private void Play(Channel? ch)
    {
        if (ch is null) return;
        int seq = ++_playSeq;
        _current = ch;
        _frameForCurrentSwitch = false;
        CloseChannelList();
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
            var resolved = await resolver.ResolvePlayUrlAsync(ch);
            if (seq != _playSeq) return; // 换台后旧任务作废
            if (resolved is null)
            {
                _ui.Post(() =>
                {
                    if (seq != _playSeq) return;
                    HideLoading();
                    FlashInfo($"{ch.Name}  暂时没接上，先看别的台");
                });
                return;
            }
            // 透传头 + AI 画质滤镜合并为选项列表（v1.0.8）
            var opts = new List<(string Key, string Value)>(resolved.Options);
            foreach (var (k, v) in LiveQuality.OptionsFor(_aiTier)) opts.Add((k, v));
            _ui.Post(() =>
            {
                if (seq != _playSeq) return;
                _host.Play(resolved.Url, opts);
                ShowInfoBar($"{ch.Number}  {ch.Name}");
            });
        });

        PrefetchNeighbors(ch);
    }

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
            Task.Run(async () => { try { await _resolver.ResolvePlayUrlAsync(nb); } catch { /* 预取失败静默 */ } });
        }
    }

    // ---- 循环预检（v1.0.8 用户拍板：频率越高越好，以不被判异常流量为前提）----

    /// <summary>
    /// 常驻循环预检：每轮保态复测（可用台常绿、无信号台留组，探测完才翻转），轮间歇 20 秒；
    /// 单台间隔 600ms + 央视频取票 403 冷却兜底，全程不被判异常流量。关掉开关时空转低耗等待。
    /// </summary>
    private async Task PrecheckLoopAsync(System.Threading.CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_precheckEnabled) await _precheck.RunAsync(_catalog.All, ct, keepKnownState: true);
                await Task.Delay(_precheckEnabled ? 20_000 : 5_000, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* 单轮失败下一轮再来，不影响播放 */ }
        }
    }

    // ---- 软件设置面板（v1.0.8 用户拍板：设置键 4077 单击呼出、长按跳系统设置）----

    /// <summary>长按设置键：直接跳电视系统设置（标准入口）；厂商限制调不起时屏显说明，不硬撑。</summary>
    private void OpenSystemSettings()
    {
        try
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionSettings));
        }
        catch
        {
            FlashInfo("此机型暂不支持跳转系统设置");
        }
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
        var slide = new Android.Views.Animations.TranslateAnimation(0f, 0f, 420f, 0f)
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
        panel.SetBackgroundColor(InkTranslucency.PanelColor()); // 全局透明化：极淡底（v1.0.8 第15项）
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
            tv.SetTextColor(Color.ParseColor("#7C8894"));
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
        AddRow($"画质 · AI 实时档：{DeviceProfile.AiEnhanceTierSetting}（确认键切换）",
            CycleAiTier, "#F7F5EE");
        AddRow($"预检握手：{(_precheckEnabled ? "开" : "关")}（确认键切换）",
            TogglePrecheck, "#F7F5EE");
        AddRow("崩溃日志（点按查看）", ShowCrashLog, "#C9D6E2");
        AddLabel("── 电视系统 ──");
        AddRow("打开电视系统设置", OpenSystemSettings, "#8FB4D4");
        AddLabel("返回键收起 · 长按设置键直达系统设置");
    }

    /// <summary>画质档循环（auto→off→low→high）：立即按新档重起当前台生效，档位持久化。</summary>
    private void CycleAiTier()
    {
        var next = SettingsCycler.NextAiTier(DeviceProfile.AiEnhanceTierSetting);
        DeviceProfile.AiEnhanceTierSetting = next;
        _prefs?.Edit()?.PutString("ai_tier", next)?.Apply();
        _aiTier = LiveQuality.Pick(next, _abi32, _totalMem);
        RefreshSettingsRows();
        FlashInfo($"画质档：{next}" + (_aiTier != LiveQuality.Pick(next, _abi32, _totalMem) ? "（按设备降级）" : ""));
        if (_current != null) Play(_current); // 滤镜随媒体参数生效：重起当前台立即应用
    }

    private void TogglePrecheck()
    {
        _precheckEnabled = !_precheckEnabled;
        _prefs?.Edit()?.PutBoolean("precheck_enabled", _precheckEnabled)?.Apply();
        RefreshSettingsRows();
        FlashInfo(_precheckEnabled ? "预检握手：开" : "预检握手：关");
    }

    private void ShowCrashLog()
    {
        try
        {
            var path = new Java.IO.File(FilesDir, "taolive_crash.log");
            if (!path.Exists()) { FlashInfo("暂无崩溃日志"); return; }
            var text = System.IO.File.ReadAllText(path.AbsolutePath);
            if (text.Length > 4000) text = "…\n" + text[^4000..];
            new AlertDialog.Builder(this)
                .SetTitle("崩溃日志")
                .SetMessage(text)
                .SetPositiveButton("关闭", (s, a) => { })
                .Show();
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

    private static string HeaderText(string title, int count, bool expanded) =>
        $"{(expanded ? "▾" : "▸")} {title} ({count})";

    private void OpenChannelList()
    {
        _listOpen = true;
        _listPanel.RemoveAllViews();
        _listPanel.Visibility = ViewStates.Visible; // 呼出列表：盖板必须显式转可见（v1.0.3 漏了这步，真机按菜单无反应）

        var title = new TextView(this)
        {
            Text = "频道列表（确认键展开/收起分类，返回键收起）",
            TextSize = 20,
        };
        title.SetTextColor(Color.ParseColor("#F7F5EE"));
        title.SetPadding(48, 40, 48, 24);
        _listPanel.AddView(title);

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
            if (r.HeaderKey != null)
            {
                _expanded[r.HeaderKey] = !_expanded.GetValueOrDefault(r.HeaderKey, r.HeaderDefault);
                OpenChannelList(); // 展开收起：重建列表（焦点随当前台/首行归位）
            }
        };
        _listPanel.AddView(_listView, new LinearLayout.LayoutParams(-1, -1));

        // 宽度随内容（v1.0.8）：按最长行文本估宽，上限半屏、下限 360dp，绝不超屏
        ApplyListWidth(rows);

        // 焦点定位：排序模式跟被排序的台，否则跟当前频道
        var focusNumber = _sortMode ? _sortChannel?.Number : _current?.Number;
        var curIdx = rows.FindIndex(r => r.Channel?.Number == focusNumber);
        if (curIdx >= 0) _listView.SetSelection(curIdx);
        _listView.RequestFocus();
    }

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        var favs = _favorites.TopGroup(_catalog.All.ToList());

        // 首次呼出：收藏组 + 当前台所在组自动展开，其余默认收起（硬要求：默认收起）
        if (!_expandedInit)
        {
            if (favs.Count > 0) _expanded["★ 收藏"] = true;
            foreach (var g in _catalog.Groups)
                if (_current != null && g.Channels.Any(c => c.Number == _current.Number))
                    _expanded[g.Title] = true;
            _expandedInit = true;
        }

        if (favs.Count > 0)
        {
            bool favOpen = _expanded.GetValueOrDefault("★ 收藏", true);
            rows.Add(new Row { Header = HeaderText("★ 收藏", favs.Count, favOpen), HeaderKey = "★ 收藏", HeaderDefault = true });
            if (favOpen) rows.AddRange(favs.Select(c => new Row { Channel = c }));
        }
        foreach (var g in _catalog.Groups.Where(g => g.Channels.Count > 0))
        {
            bool open = _expanded.GetValueOrDefault(g.Title, false);
            rows.Add(new Row { Header = HeaderText(g.Title, g.Channels.Count, open), HeaderKey = g.Title, HeaderDefault = false });
            if (open) rows.AddRange(g.Channels.Select(c => new Row { Channel = c }));
        }

        // 确认不可用的台归「无信号」分组（v1.0.8 第11项定名）：置底、默认折叠、只能看不能切；恢复可用自动移回原分类
        var dead = _catalog.All.Where(c => _precheck.Of(c) == ChannelHealth.Dead).ToList();
        if (dead.Count > 0)
        {
            bool dOpen = _expanded.GetValueOrDefault("✕ 无信号", false);
            rows.Add(new Row { Header = HeaderText("无信号", dead.Count, dOpen), HeaderKey = "✕ 无信号", HeaderDefault = false });
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
                Style(h, "#8FB4D4", 18);
                line.AddView(h);
                return line;
            }

            var ch = row.Channel!;
            var num = new TextView(ctx) { Text = $"{ch.Number,3}", Gravity = GravityFlags.Right };
            Style(num, "#C9D6E2", 20);
            num.SetPadding(0, 0, 32, 0);
            line.AddView(num);

            var star = new TextView(ctx) { Text = _isFav(ch.Number) ? "★" : " " };
            Style(star, "#E8B84B", 20);
            star.SetPadding(0, 0, 24, 0);
            line.AddView(star);

            var name = new TextView(ctx) { Text = ch.Name };
            var isCur = _current?.Number == ch.Number;
            Style(name, isCur ? "#FFFFFF" : "#C9D6E2", 22);
            line.AddView(name);

            // 可连状态标识（v1.0.8 预检握手）：绿点可用 · 灰点检测中/未探 · 红叉连不上
            var health = _health(ch);
            var (mark, color) = health switch
            {
                ChannelHealth.Alive => ("●", "#6FBF73"),
                ChannelHealth.Dead => ("✕", "#D96A6A"),
                ChannelHealth.Checking => ("●", "#8A9AA8"),
                _ => ("●", "#5A6672"),
            };
            var dot = new TextView(ctx) { Text = mark };
            Style(dot, color, 18);
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
        if (_listOpen) OpenChannelList(); // 列表开着则刷新
    }

    // ---- 返回键（v1.0.8 防错）----

    private void ExitOrCollapseList()
    {
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
        // 播放态防错：第一次按只屏显提示，2 秒内再按才退后台，超时重新计数
        long now = Environment.TickCount64;
        if (_lastBackMs > 0 && now - _lastBackMs <= 2000)
        {
            _lastBackMs = 0;
            MoveTaskToBack(true); // 退后台不退出进程（开机即看）
            return;
        }
        _lastBackMs = now;
        FlashInfo("再按一次返回键退出");
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
            // 列表开：上下/确认交给 ListView 焦点导航；菜单或返回收起
            if (keyCode is Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadCenter or Keycode.Enter or Keycode.Menu or Keycode.Back)
            {
                if (keyCode == Keycode.Menu || keyCode == Keycode.Back) { ExitOrCollapseList(); return true; }
                return base.OnKeyDown(keyCode, e);
            }
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
            Play(cand);
            return;
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // B 档：自定义键绑打开道映 → 秒开回上次频道。
        // 纹理载体：后台本来就在放且播放实际存活 → 回来原样续，不再重起一遍造成闪动；
        // 播放已死（v1.0.8 黑屏修复：后台播放被系统杀/流断，IsPlaying 可能还在但时钟停走）
        // → 复位首帧状态按冷启动路径重播，绝不黑屏干等。
        if (!_host.IsAlive)
        {
            _frameEverShown = false; // 复位：重播按冷启动路径立即亮牌，不误留黑旧画面
            Play(_current ?? DefaultChannel());
            return;
        }
        if (!_frameEverShown) Play(DefaultChannel());
    }

    // v1.0.7 双载体退后台策略：
    // - 纹理载体：退后台画面照常走（不回收），声音先闭掉，回前台恢复；
    // - 表面载体（稳定档）：退后台主动停播，回前台重新接当前台（v1.0.5 已验证行为）
    protected override void OnPause()
    {
        base.OnPause();
        _lastBackMs = 0; // 防错计数不跨后台
        if (_carrierTexture)
        {
            // 活着退后台 = 载体没崩，视为兼容（用户中途退出不该误判为崩溃）
            if (!_frameForCurrentSwitch) CarrierPolicy.MarkOk(this);
            _host.MuteForBackground();
        }
        else
        {
            _host.Stop();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_carrierTexture)
        {
            _host.UnmuteAfterBackground();
            // v1.0.8 黑屏修复：纹理路径退后台后播放可能已死（系统杀后台播放/流断），
            // 只恢复音量不重播就是永远黑屏；回前台校验播放实际存活，不活则重播当前台
            if (_current != null && !_listOpen && !_host.IsAlive)
            {
                _frameEverShown = false; // 复位：重播按冷启动路径立即亮牌
                Play(_current);
            }
        }
        else if (_current != null && !_listOpen)
        {
            Play(_current); // 表面载体：回前台重接画面（走取流缓存秒回）
        }
    }

    protected override void OnDestroy()
    {
        _precheckCts?.Cancel();
        _precheckCts?.Dispose();
        _precheckCts = null;
        _ticketKeeper?.Dispose();
        _ticketKeeper = null;
        _host.Dispose();
        base.OnDestroy();
    }
}
