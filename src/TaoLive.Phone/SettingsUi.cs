using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;

namespace TaoLive.Phone;

/// <summary>
/// 软件设置面板 + 三子页（崩溃日志全屏 / 自诊断 / 进阶调优）+ 显示大小档 + 三级跳。
/// UI 布局与文案照 vc53（eef464b MainActivity 对应段）；2.0 只接真实接线面：
/// 预检握手已接真（常驻循环预检，MainActivity.StartPrecheckLoop）；
/// 开机自启已接真（方案书收口批②：BootReceiver+开关行，BootReceiver.OnReceive 读同一份偏好）；
/// 后台预热开关真实控制开机全量预热与换台邻台预取。
/// </summary>
public partial class MainActivity
{
    // ---- 设置面板（真机反馈批#19：底部全宽流式——面板占满全宽贴底，行按内容从左往右一行摆多项，
    //      说明收进底部帮助行，不留大面积空白；内容高度封顶滚动；间距/字号全部随显示尺寸档缩放） ----
    private LinearLayout? _settingsPanel;
    private bool _settingsPanelOpen;
    private FlowLayout? _settingsFlow;
    private ScrollView? _settingsScroll;

    // ---- 全屏子页（自管面板：非 AlertDialog——对话框浮动容器吃边，真机全屏仍缩半屏） ----
    private FrameLayout? _crashOverlay;
    private ScrollView? _crashScroll;
    private TextView? _crashCleanBtn, _crashCloseBtn;
    private FrameLayout? _diagOverlay;
    private ScrollView? _diagScroll;
    private TextView? _diagExportBtn, _diagCloseBtn;
    private FrameLayout? _advOverlay;
    private FrameLayout? _licenseOverlay;
    private ScrollView? _licenseScroll;
    private TextView? _licenseCloseBtn;

    private ISharedPreferences _prefs = default!;

    /// <summary>密度换算（#15 尺寸令：所有页面所有元素跟档位走，间距不豁免；真机反馈批#70 口径归一：
    /// 乘 UiScale.Scale 与字体侧同账——此前只乘 Factor 漏了 BaseSize/SpecBase×ScreenScale，
    /// 间距/按钮高度相对字体整体缩水 1.8 倍，装不下字就盖字、旋钮色块对不齐符号）。</summary>
    private int Dp(float v) => (int)(v * Resources!.DisplayMetrics!.Density * UiScale.Scale + 0.5f);

    // —— 崩溃/失败打点（接真实事件：逐次换源） ——

    private void OnSessionFailover(object? sender, FailoverEventArgs e)
    {
        CrashNet.Record($"[换源] {e.From.Url} → {e.To.Url}（{e.Reason}）· 第 {e.Attempt} 次");
        // 换源黑屏埋点起点（黑屏镜黄④ 11-15）：仅当前台记账，热备攒流不掺
        if (sender is PlaybackSession s && ReferenceEquals(s, _manager.Current))
        {
            _failoverAtMs = System.Environment.TickCount64;
        }
    }

    /// <summary>慢连宽限提示（vc86 F7 老板令）：宽限窗挂「正在换信号」OSD——用户知道在原地等慢源，不是死机；
    /// 仅前台会话亮条，热备/让位会话不弹（文案单点 SwitchHud.Connecting，自动消隐 ≤2 秒语义同致）。</summary>
    private void OnSessionSlowGrace(object? sender)
    {
        if (sender is PlaybackSession s && ReferenceEquals(s, _manager.Current))
            ShowToast(TaoLive.Session.SwitchHud.Connecting);
    }

    /// <summary>灌速探针预热（vc89 老板拍板「一次平稳」）：弱线起播期后台焐热下一条备线的票——
    /// 走既有 TicketResolver 取票链路落缓存，干库秒切时零取票等待。不切线不动画面；
    /// 仅前台会话发车，后台线程跑（探针信号在会话泵线程），失败照常走既有自救不碍事。</summary>
    private void OnSessionWeakFill(object? sender, SourceEndpoint endpoint)
    {
        if (sender is not PlaybackSession s || !ReferenceEquals(s, _manager.Current)) return;
        if (s.CurrentChannel is not { } ch || !endpoint.IsValid) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _resolver.ResolveAsync(endpoint, ch.Id, ch.Name).ConfigureAwait(false);
                CrashNet.Record($"[预热] {ch.Name} 备线票焐热完成（灌速探针）");
            }
            catch (Exception ex)
            {
                CrashNet.Record($"[预热] {ch.Name} 备线票焐热未成照常走既有自救：{ex.Message}");
            }
        });
    }

    // —— ⑨「去系统设置」三级跳（v1.0.9 拍板口径）：对口设置页 → 系统设置总入口 → 文字指引 ——

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
            catch (Exception ex) { CrashNet.Record($"[设置跳转] 打开系统设置失败降级下一级：{ex.Message}"); } // 第11轮 P3修：原标签「[MemoryLevel] 内存分级调不动…」系复制粘贴残留，跳系统设置失败被记成内存分级账，台账误导诊断
        }
        ShowToast("请在手机系统设置中调整该权限"); // 手机版口径：无遥控器，指引系统设置
    }

    // —— 软件设置面板（设置键 4077 单击呼出；#18 老板令 2026-11-15：长按直达系统设置已删，保留三级跳给自启行用） ——

    private void ToggleSettingsPanel()
    {
        if (_settingsPanelOpen) CloseSettingsPanel(); else OpenSettingsPanel();
    }

    // —— 窗口唯一令（#64 老板令 2026-11-15）：打开任何窗口前先关掉其它所有窗口——
    //    不允许重叠，当前操作的窗口永远第一顺位且唯一。覆盖：频道抽屉/设置面板/退出确认弹窗/
    //    运行统计·崩溃日志/自诊断/进阶调优/开源许可。纯关闭不触发「返回设置页」收口。 ——

    /// <summary>统一闸：开新窗口前先关掉其它全部窗口（幂等，重复调无害）。</summary>
    private void EnforceSingleWindow()
    {
        if (_listOpen) ShowPlayer();      // 收频道抽屉回播放页（纯收起，画面本就没停）
        CloseSettingsPanel();             // 收设置面板
        if (_exitDialog is not null)      // 收退出确认弹窗
        {
            _exitDialog.Dismiss();
            _exitDialog = null;
            _exitNo = null;
            _exitYes = null;
        }
        CloseCrashOverlayOnly();
        CloseDiagOverlayOnly();
        CloseLicenseOverlayOnly();
        CloseAdvancedOverlayOnly();
    }

    private void OpenSettingsPanel()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧
        _settingsPanel ??= BuildSettingsPanel();
        _settingsPanelOpen = true;
        RefreshSettingsRows();
        _settingsPanel.Visibility = ViewStates.Visible;
        UpdateScrim(); // 手机版整改令：设置面板开着亮遮罩（点外关闭）
        // 从底部滑入（透明底，尽量少遮挡画面）
        var slide = new TranslateAnimation(0f, 0f, 420f, 0f) { Duration = 220 };
        _settingsPanel.StartAnimation(slide);
        // 焦点必须看得见：弹出后下一帧把焦点直接放到面板第一个可选行
        //（容器 RequestFocus 落空时焦点会留在底下的层里——真机「设置页光标选不中」根因）
        _settingsPanel.Post(() =>
        {
            if (_settingsFlow is null) return;
            for (int i = 0; i < _settingsFlow.ChildCount; i++)
                if (_settingsFlow.GetChildAt(i) is View row && row.Focusable) { row.RequestFocus(); break; }
        });
    }

    private void CloseSettingsPanel()
    {
        _settingsPanelOpen = false;
        if (_settingsPanel != null) _settingsPanel.Visibility = ViewStates.Gone;
        UpdateScrim(); // 手机版整改令：面板收起撤遮罩
    }

    /// <summary>
    /// 设置面板（真机反馈批#19，老板令「底部全宽流式、一行摆多项、说明收进帮助行、不留大面积留白」）：
    /// 面板全宽贴底；行区用 FlowLayout 按内容从左往右摆，一行放得下几项摆几项、放不下才换行；
    /// 键位说明收成底部常驻帮助行；内容多时行区封顶滚动。字号走 #68 语义字号令（Title/Item/Caption），间距走随档缩放的 Dp。
    /// </summary>
    private LinearLayout BuildSettingsPanel()
    {
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetBackgroundColor(InkTranslucency.PanelSolidColor(this)); // 手机版整改令：近实色水墨底，亮画面上不叠字
        panel.SetPadding(Dp(20), Dp(10), Dp(20), Dp(12));
        panel.Visibility = ViewStates.Gone;

        // 行区 ScrollView：行多时整体可滚，面板高度不超出屏幕（滚动容器不截焦点）
        var maxHeight = (int)(Resources!.DisplayMetrics!.HeightPixels * 0.9f);
        _settingsScroll = new MaxHeightScrollView(this, maxHeight);
        _settingsFlow = new FlowLayout(this, Dp(4), Dp(2));
        _settingsScroll.AddView(_settingsFlow, new FrameLayout.LayoutParams(-1, -2));

        // #19：说明收进底部帮助行（原每行下挂的操作提示统一收到这一行，全宽常驻不抢眼）
        var help = new TextView(this) { Text = "点按切换 · 点 ✕ 或返回键收起", TextSize = UiScale.Caption };
        help.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(help);
        help.SetPadding(Dp(14), Dp(8), Dp(14), Dp(4));

        // 手机版整改令 2026-11-27：设置改全屏页式（原贴底浮层是电视端口径）——标题行+整页行列表
        var headRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        headRow.SetGravity(GravityFlags.CenterVertical);
        var headTitle = new TextView(this) { Text = "设置", TextSize = UiScale.Title };
        headTitle.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(headTitle);
        headRow.AddView(headTitle);
        var closeBtn = new TextView(this) { Text = "✕", TextSize = UiScale.Item };
        closeBtn.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(closeBtn);
        closeBtn.SetPadding(Dp(20), Dp(14), Dp(20), Dp(14));
        closeBtn.Click += (_, _) => CloseSettingsPanel();
        headRow.AddView(new View(this), new LinearLayout.LayoutParams(0, 1) { Weight = 1f }); // 中间留白
        headRow.AddView(closeBtn);
        panel.AddView(headRow, new FrameLayout.LayoutParams(-1, -2));

        panel.AddView(_settingsScroll, new LinearLayout.LayoutParams(-1, 0, 1f));
        panel.AddView(help, new FrameLayout.LayoutParams(-1, -2));
        _root.AddView(panel, new FrameLayout.LayoutParams(-1, -1)); // 全屏页
        return panel;
    }

    /// <summary>
    /// 流式布局（真机反馈批#19）：子项按内容从左往右排，一行放不下才换行——设置行一行摆多项。
    /// 量测/摆放均按子项实测宽高（行内自带的 padding 即项间距），面板全宽时天然铺满不留空白。
    /// </summary>
    private sealed class FlowLayout(Context c, int hGap, int vGap) : ViewGroup(c)
    {
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int maxW = Math.Max(0, MeasureSpec.GetSize(widthMeasureSpec) - PaddingLeft - PaddingRight);
            int x = 0, y = 0, lineH = 0;
            for (int i = 0; i < ChildCount; i++)
            {
                var child = GetChildAt(i)!;
                MeasureChild(child, widthMeasureSpec, heightMeasureSpec);
                int cw = child.MeasuredWidth, ch = child.MeasuredHeight;
                if (x > 0 && x + cw > maxW) { x = 0; y += lineH + vGap; lineH = 0; } // 换行
                x += cw + hGap;
                lineH = Math.Max(lineH, ch);
            }
            SetMeasuredDimension(
                ResolveSize(MeasureSpec.GetSize(widthMeasureSpec), widthMeasureSpec),
                ResolveSize(y + lineH + PaddingTop + PaddingBottom, heightMeasureSpec));
        }

        protected override void OnLayout(bool changed, int l, int t, int r, int b)
        {
            int maxW = r - l - PaddingLeft - PaddingRight;
            int x = PaddingLeft, y = PaddingTop, lineH = 0;
            for (int i = 0; i < ChildCount; i++)
            {
                var child = GetChildAt(i)!;
                int cw = child.MeasuredWidth, ch = child.MeasuredHeight;
                if (x - PaddingLeft > 0 && x + cw - PaddingLeft > maxW) { x = PaddingLeft; y += lineH + vGap; lineH = 0; }
                child.Layout(x, y, x + cw, y + ch);
                x += cw + hGap;
                lineH = Math.Max(lineH, ch);
            }
        }
    }

    /// <summary>内容高度封顶的 ScrollView——行多则滚动；滚动容器不截焦点（否则上下键变成滚动面板）。</summary>
    private sealed class MaxHeightScrollView : ScrollView
    {
        private readonly int _maxHeight;
        public MaxHeightScrollView(Context c, int maxH) : base(c)
        {
            _maxHeight = maxH;
            HorizontalScrollBarEnabled = false;
            VerticalScrollBarEnabled = false;
            Focusable = false;
        }
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            base.OnMeasure(widthMeasureSpec,
                MeasureSpec.MakeMeasureSpec(_maxHeight, MeasureSpecMode.AtMost));
        }
    }

    private void RefreshSettingsRows()
    {
        if (_settingsPanel is null || _settingsFlow is null) return;
        // 重建前记录焦点行（只记可聚焦行；#61 焦点实修：显式导航只走 _settingsRows，不含标签行）
        var focusedNow = CurrentFocus as TextView;
        int focusedIdx = focusedNow is null ? -1 : _settingsRows.IndexOf(focusedNow);
        _settingsFlow.RemoveAllViews();
        _settingsRows.Clear();

        // #19 流式行：紧凑内边距（项间距自带），操作提示收进底部帮助行不再每行下挂
        void AddRow(string text, Action onClick, string color, Action? onLongClick = null)
        {
            var tv = new TextView(this) { Text = text, TextSize = UiScale.Item };
            tv.SetTextColor(Color.ParseColor(color));
            tv.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14)); // 手机热区：行高加大
            tv.Focusable = true;
            var focusDrawable = InkTranslucency.FocusDrawable();
            tv.FocusChange += (_, e) =>
            {
                if (e.HasFocus) tv.Background = focusDrawable;
                else if (tv.Background == focusDrawable) tv.Background = null;
            };
            tv.Click += (_, _) => onClick();
            // 手机版整改令 2026-11-27：触屏按压反馈（电视端只有焦点高亮，触屏按下无态）
            tv.Touch += (_, e) =>
            {
                if (e.Event is null) return;
                if (e.Event.Action == MotionEventActions.Down) tv.Background = focusDrawable;
                else if (e.Event.Action is MotionEventActions.Up or MotionEventActions.Cancel && !tv.HasFocus)
                    tv.Background = null;
            };
            if (onLongClick is not null) tv.LongClick += (_, _) => onLongClick();
            _settingsFlow!.AddView(tv);
            _settingsRows.Add(tv); // #61 显式焦点导航：真机实锤系统焦点搜索在自实现流式容器上不可靠
        }

        // ⑤开机自启（方案书收口批②：BootReceiver+开关行）：安装默认开启（照 vc53 v22 第 15 项），与 BootReceiver 同读一份偏好
        AddRow($"开机自启：{(_prefs.GetBoolean("autostart_enabled", true) ? "开" : "关")}（长按跳自启管理）",
            ToggleAutoStart, "#F7F5EE", OpenAutoStartSettings);
        // ⑥开机默认台设置行已删（老板令 2026-11-15：一百多台循环点选没法用；启动选台回归 vc53 口径=断点台回看，见 MainActivity.DefaultChannelIndex）
        // 预检握手（vc53 v1.0.8 一比一）：真实控制常驻循环预检（关=循环同停）；
        // 收口批④开关合流：预热与预检两个开关管同一条车道必出一个死开关——只留本开关，
        // 预热（开机全量预热+换台邻台预取）随预检同起同停，不再单设开关。
        // vc105 老板令：以 93 为基线——入口接回（vc101 撤除令被「以93为基线」覆盖）。
        AddRow($"预检握手：{(_precheckEnabled ? "开" : "关")}（预热同起同停）", TogglePrecheck, "#F7F5EE");
        // 内置订阅（老板 2026-11-26 拍板「只做内置订阅」）：开关默认关；长按=立即刷新合集
        AddRow($"源订阅：{(_subEnabled ? "开" : "关")}（自动收录公开源）", ToggleSubscription, "#F7F5EE", RefreshSubscriptionNow);
        AddRow($"显示大小：{DisplaySizeLabel()}", CycleDisplaySize, "#F7F5EE");
        AddRow("运行统计", ShowRunStatsPanel, "#C9D6E2");
        AddRow("崩溃日志", ShowCrashLog, "#C9D6E2");
        AddRow("自诊断", ShowDiagPanel, "#C9D6E2");
        AddRow("进阶调优", ShowAdvancedTuningPanel, "#C9D6E2");
        AddRow("关于 · 开源许可", ShowLicensePanel, "#C9D6E2");

        // 重建完成，焦点回原行（#61：走显式导航，含滚入可视）
        if (focusedIdx >= 0) FocusSettingsRow(focusedIdx);
    }

    // —— 设置面板显式焦点导航（#61 真机实锤：设置面板改流式一行多项后，系统焦点搜索在自实现
    //    FlowLayout 上切不动焦点，且左右键原被键位分支吞掉——照退出弹窗先例改显式接管） ——

    /// <summary>当前面板可聚焦行（添加序=流式视觉序：从左到右、从上到下）。</summary>
    private readonly List<TextView> _settingsRows = new();

    /// <summary>焦点落到指定行并滚入可视区。</summary>
    private void FocusSettingsRow(int idx)
    {
        if (idx < 0 || idx >= _settingsRows.Count) return;
        var row = _settingsRows[idx];
        row.RequestFocus();
        _settingsScroll?.Post(() => _settingsScroll.SmoothScrollTo(0, Math.Max(0, row.Top - row.Height)));
    }

    /// <summary>方向键移焦点：左右=同序列前/后一项；上下=按视觉行分组找相邻行中水平最近的项。
    /// 封顶/到底时停在原行不环绕（面板行少，环绕易晕）。总是消费按键返回 true。</summary>
    private bool SettingsFocusMove(int dx, int dy)
    {
        if (_settingsRows.Count == 0) return true;
        var focusedNow = CurrentFocus as TextView;
        int idx = focusedNow is null ? -1 : _settingsRows.IndexOf(focusedNow);
        if (idx < 0) { FocusSettingsRow(0); return true; }
        if (dx != 0)
        {
            FocusSettingsRow(Math.Min(_settingsRows.Count - 1, Math.Max(0, idx + dx)));
            return true;
        }
        // 上下：按 Top 分组（组距超半行高即换行），在相邻行组里找 Left 最接近的项
        var cur = _settingsRows[idx];
        float curLeft = cur.Left;
        var groups = new List<List<int>>();
        foreach (var (i, r) in _settingsRows.Select((r, i) => (i, r)))
        {
            if (groups.Count == 0 || Math.Abs(r.Top - _settingsRows[groups[^1][0]].Top) > r.Height * 0.5f)
                groups.Add(new List<int>());
            groups[^1].Add(i);
        }
        int gi = groups.FindIndex(g => g.Contains(idx));
        int gj = Math.Min(groups.Count - 1, Math.Max(0, gi + (dy > 0 ? 1 : -1)));
        if (gj != gi)
            FocusSettingsRow(groups[gj].OrderBy(t => Math.Abs(_settingsRows[t].Left - curLeft)).First());
        return true;
    }

    /// <summary>⑤开机自启开关（方案书收口批②）：与 BootReceiver 同读一份偏好（默认开）。</summary>
    private void ToggleAutoStart()
    {
        var next = !_prefs.GetBoolean("autostart_enabled", true);
        _prefs.Edit()?.PutBoolean("autostart_enabled", next)?.Apply();
        RefreshSettingsRows();
        ShowToast(next ? "开机自启：开" : "开机自启：关");
    }

    /// <summary>长按自启行跳系统自启管理（照 vc53 三级降级口径，遥控器放行入口）。</summary>
    private void OpenAutoStartSettings() => OpenSystemSettings();

    /// <summary>预检握手开关（vc53 v1.0.8 一比一；vc105 老板令以 93 为基线接回）：切换并持久化，
    /// 开=常驻循环预检+预热（共车道同起同停）随启停。</summary>
    private void TogglePrecheck()
    {
        _precheckEnabled = !_precheckEnabled;
        _prefs.Edit()?.PutBoolean("precheck_enabled", _precheckEnabled)?.Apply();
        if (_precheckEnabled) StartPrecheckLoop();
        else StopPrecheckLoop();
        RefreshSettingsRows();
        ShowToast(_precheckEnabled ? "预检握手：开" : "预检握手：关");
    }

    // —— 内置订阅源（老板 2026-11-26 拍板「只做内置订阅」）：拉公开免费合集→按台名匹配→查重→试活→入库垫底 ——
    // 老板令 2026-11-26「拉源避开播放，不能影响播放」：播放/热备在跑一律暂缓，挂起待停播自动补跑；
    // 试活并发≤2、单次取样≤64KB，拉源流量绝不与播放抢道。

    private static bool _subEnabled = false; // 源订阅开关（默认关；与偏好 sub_enabled 同步）
    private static bool _subRefreshPending = false; // 播放中挂起的刷新（停播后自动补跑）
    private static bool _subStartupPendingRun; // 启动延迟的首跑标记（补跑时静默不弹提示）
    private const long SubBusyRecheckMs = 30_000; // 挂起补跑轮询节拍
    private const long SubStartupDelayMs = 90_000; // 启动错峰：开机先让预热/回看跑稳，再拉源

    private void ToggleSubscription()
    {
        _subEnabled = !_subEnabled;
        _prefs.Edit()?.PutBoolean("sub_enabled", _subEnabled)?.Apply();
        if (_subEnabled) KickSubscriptionRefresh(startup: false);
        RefreshSettingsRows();
        ShowToast(_subEnabled ? "源订阅：开" : "源订阅：关");
    }

    private void RefreshSubscriptionNow()
    {
        if (!_subEnabled) { ShowToast("先开启源订阅"); return; }
        KickSubscriptionRefresh(startup: false);
    }

    /// <summary>播放/热备是否在跑（拉源避让判据：任一在跑即让道）。</summary>
    private bool IsPlaybackBusy() => _manager?.Current is not null || _manager?.Standby is not null;

    /// <summary>后台拉合集并试活入库；播放中挂起待停播补跑（startup 静默不弹提示）。线程池跑，不占主线程。
    /// vc96 第 12 轮 P3 修：入口补源订阅开关闸——挂起补跑回调（ScheduleSubRecheck）只查挂起账与播放占用，
    /// 用户关掉开关后停播仍会续跑拉源并入频道（违背「关=不拉源」语义）；入口统一拦截并清挂起账。</summary>
    private void KickSubscriptionRefresh(bool startup)
    {
        if (!_subEnabled) { _subRefreshPending = false; return; } // vc96 第 12 轮 P3 修：关=不拉源，挂起账一并清
        if (IsPlaybackBusy())
        {
            _subRefreshPending = true;
            _subStartupPendingRun = startup;
            ScheduleSubRecheck();
            if (!startup) ShowToast("播放中暂缓拉源，停止播放后自动续跑");
            return;
        }
        _subRefreshPending = false;
        var names = _channels.Select(c => c.Name).ToList();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var added = SubscriptionSourceStore.Shared
                    .RefreshAsync(names, TaoLive.Catalog.TicketResolver.ClientUserAgent,
                        abortWhenBusy: () => (Shell?.IsPlaybackBusy() ?? true) || !MainActivity._subEnabled).GetAwaiter().GetResult(); // vc96 第 12 轮 P3 修：拉源途中关开关即中止（在途轮也守开关语义）；vc96 出包闸修：static 成员经类名访问（Shell?. 实例引用在 Release 档 CS0176）
                RunOnUiThread(() =>
                {
                    if (!_subEnabled) return; // vc96 第 12 轮 P3 修：拉源途中被关（abort 优雅返回带部分结果），并入前再守一道，关=不并入不弹提示
                    // 订阅新收录频道并入列表（目录外主流台，表尾追加）；并入数>0 才提示（startup 静默不弹）
                    var newChannels = MergeSubscriptionChannels(silent: startup);
                    if (!startup && newChannels > 0) ShowToast($"源订阅：新收录频道 {newChannels} 个");
                    else if (!startup && added > 0) ShowToast($"源订阅：新收录 {added} 路");
                });
                // 刷新途中播放开始了（拉源中途让道）：挂起待停播补跑，保证收录完整（开关已关则不挂，vc96 第 12 轮 P3 修）
                if (_subEnabled && IsPlaybackBusy() && !_subRefreshPending)
                {
                    _subRefreshPending = true;
                    _subStartupPendingRun = startup;
                    ScheduleSubRecheck();
                }
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => { if (!startup) ShowToast($"源订阅刷新失败：{ex.Message}"); });
            }
        });
    }

    /// <summary>挂起补跑轮询：每 30s 查一次，停播即补跑；仍播继续挂。
    /// vc93 审查清零 P2：复用单一主线程 Handler——此前每轮 new Handler 链式自续，
    /// 壳销毁后仅靠布尔守卫停链，消息队列里残留挂起的回调解引用旧壳（轻微泄漏面）。
    /// 单例 Handler 挂主 Looper，链式自续只排队不新建；停链仍由 _subRefreshPending 守卫（口径不变）。</summary>
    private static Handler? _subRecheckHandler;
    private void ScheduleSubRecheck()
    {
        _subRecheckHandler ??= new Handler(Looper.MainLooper!);
        _subRecheckHandler.PostDelayed(() =>
        {
            if (!_subRefreshPending) return;
            if (!IsPlaybackBusy()) KickSubscriptionRefresh(startup: _subStartupPendingRun);
            else ScheduleSubRecheck();
        }, SubBusyRecheckMs);
    }

    // —— 显示大小（真机反馈批 #61 尺寸回抬令 2026-11-15 19:3x：中档定「肉眼最舒适」=基准 20sp，
    // 三档整体重定标：大=2/3(24sp)、中=5/9(20sp)、小=1/3(12sp)，默认中；覆盖 #14 的 ×2/3 压缩口径） ——

    private static readonly float[] DisplaySizeSteps = { 21f / 36f, 17f / 36f, 13f / 36f }; // #77 档位重定：大21/中17/小13（换算实值÷基准36）
    private static readonly string[] DisplaySizeLabels = { "大", "中", "小" };

    private static int DisplaySizeIndex(float f)
    {
        for (int i = 0; i < DisplaySizeSteps.Length; i++)
            if (Math.Abs(DisplaySizeSteps[i] - f) < 0.01f) return i;
        return 1; // 未知值回落「中」
    }

    private string DisplaySizeLabel() => DisplaySizeLabels[DisplaySizeIndex(
        _prefs.GetFloat("ui_scale", 17f / 36f))];

    /// <summary>大→中→小循环，档位记忆；界面重建即时生效——真机反馈批#15 原地换皮：会话跨 Recreate 存活，
    /// 正在看的台不断流不重连。</summary>
    private void CycleDisplaySize()
    {
        int next = (DisplaySizeIndex(_prefs.GetFloat("ui_scale", 17f / 36f)) + 1) % DisplaySizeSteps.Length;
        _prefs.Edit()?.PutFloat("ui_scale", DisplaySizeSteps[next])?.Apply();
        Recreate(); // 重建走 OnCreate 重定标，全套令牌按新档位重算
    }

    // —— 崩溃日志全屏面板（自管：窗底透明+半透明水墨面板，不露 ROM 钢蓝灰） ——

    /// <summary>按尾偏移读日志尾部（fix53 口径），主线程 IO 有界。</summary>
    private static string ReadLogTail(string filePath, int maxBytes) => DiagLog.ReadTail(filePath, maxBytes);

    /// <summary>运行统计聚合（真机反馈批#12 统计令 2026-11-15）：台账原文太长不好读，聚合成关键数字。
    /// 只扫近期台账尾 1MB（崩溃日志 1MB×2 代轮转封顶，覆盖近期）；各标记行按前缀计数，末次值带出。
    /// 纯解析无副作用，行格式与打点侧（MainActivity 埋点/取票/连接失败）逐一对齐，不猜行文。</summary>
    internal static string BuildRunStats(string log)
    {
        int startupOk = 0, startupStall = 0; string lastStartup = "";
        int smoothOk = 0, smoothStall = 0;
        int videoIdle = 0, failover = 0; string lastFailover = "";
        int connFail = 0, playerErr = 0, ticketOk = 0, ticketFail = 0, uncaught = 0;
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Contains("[起播流畅]")) { startupOk++; lastStartup = line[(line.IndexOf("[起播流畅]", StringComparison.Ordinal) + 6)..].Trim(); }
            else if (line.Contains("[画面平稳]")) { if (line.Contains("疑似卡顿")) smoothStall++; else smoothOk++; }
            else if (line.Contains("[视频停滞]")) videoIdle++;
            else if (line.Contains("[换源黑屏]")) { failover++; lastFailover = line[(line.IndexOf("[换源黑屏]", StringComparison.Ordinal) + 6)..].Trim(); }
            else if (line.Contains("(player error", StringComparison.Ordinal)) playerErr++;
            else if (line.Contains("[ConnectFailure]")) connFail++;
            else if (line.Contains("[取票]")) { if (line.Contains("成功")) ticketOk++; else ticketFail++; }
            else if (line.Contains("未捕获异常")) uncaught++;
        }
        return
            "—— 播放流畅（近期台账）——\n" +
            $"起播到平稳：{startupOk} 次（{lastStartup}）\n" +
            $"起播窗卡顿：{startupStall} 次 · 平稳轮询疑点：{smoothStall} 轮\n" +
            $"视频停滞（白屏只出声排查锚点）：{videoIdle} 次\n" +
            $"换源恢复出画：{failover} 次（{lastFailover}）\n\n" +
            "—— 稳定性 ——\n" +
            $"全线路不可达：{connFail} 次 · 换源播放器报错：{playerErr} 次\n" +
            $"取票：成功 {ticketOk} · 失败 {ticketFail}\n" +
            $"未捕获异常：{uncaught} 次\n\n" +
            "统计范围：近期 1MB 台账（轮转两代封顶）";
    }

    /// <summary>运行统计面板：只读聚合，无清理键防误操作；关闭键即回设置页。读盘解析走后台（#23）。</summary>
    private void ShowRunStatsPanel()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧
        try
        {
            var path = new Java.IO.File(FilesDir, "taolive_crash.log");
            if (!path.Exists()) { ShowToast("暂无运行台账"); return; }
            var (box, pad) = BuildOverlayPanel("运行统计（近期）", "统计中…", out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseCrashPanel();
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(60); // #70 口径归一后实值直出：52dp 装不下 22sp 文字+28dp 内边距，抬到 60dp 保字完整
            btns.AddView(close, new LinearLayout.LayoutParams(-1, btnH));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });

            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true }; // 吃掉触摸防穿透
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _crashOverlay = overlay;
            _crashScroll = scroll;
            _crashCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus();
            // 读盘+解析后台走（#23）：1MB 尾扫不占主线程，读前先判文件还在（可能被一键清理删掉）
            LoadOverlayBodyAsync(scroll, () =>
                BuildRunStats(System.IO.File.Exists(path.AbsolutePath!) ? ReadLogTail(path.AbsolutePath!, 1024 * 1024) : "（暂无运行台账）"),
                "统计中…", () => ReferenceEquals(_crashOverlay, overlay));
        }
        catch { ShowToast("统计读取失败"); }
    }


    private void ShowCrashLog()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧
        try
        {
            var path = new Java.IO.File(FilesDir, "taolive_crash.log");
            if (!path.Exists()) { ShowToast("暂无崩溃日志"); return; } // 空态提示
            var (box, pad) = BuildOverlayPanel($"崩溃日志（全屏）· {CrashNet.VersionTag()}", "读取中…", out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var clean = new TextView(this) { Text = "一键清理", Gravity = GravityFlags.Center };
            clean.SetPadding(pad, Dp(14), pad, Dp(14));
            clean.Click += (_, _) =>
            {
                try { System.IO.File.Delete(path.AbsolutePath!); ShowToast("崩溃日志已清空"); }
                catch { ShowToast("清理失败，请重试"); }
                CloseCrashPanel();
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseCrashPanel();
            clean.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            clean.NextFocusRightId = close.Id; // 左右键显式互指，不靠焦点搜索
            close.NextFocusLeftId = clean.Id;
            StyleOverlayButton(clean, InkTranslucency.AccentFocusDrawable()); // 一键清理=朱砂（危险项）
            StyleOverlayButton(close, InkTranslucency.FocusDrawable()); // 关闭=纸白
            int btnH = Dp(60); // 按钮行固定底栏，不论日志多少行按钮始终在屏幕底部；#70 口径归一后实值直出，抬到 60dp 保字完整
            btns.AddView(clean, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });

            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true }; // 吃掉触摸防穿透
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _crashOverlay = overlay;
            _crashScroll = scroll;
            _crashCleanBtn = clean;
            _crashCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel(); // 打开日志页即收起设置页，两层页面不叠显
            close.RequestFocus(); // 默认焦点落「关闭」——防误清日志
            // 读盘后台走（#23）：64KB 尾扫+截断不占主线程
            LoadOverlayBodyAsync(scroll, () =>
            {
                var text = ReadLogTail(path.AbsolutePath!, 64 * 1024);
                if (text.Length > 16000) text = "…\n" + text[^16000..];
                return text;
            }, "读取中…", () => ReferenceEquals(_crashOverlay, overlay));
        }
        catch { ShowToast("日志读取失败"); }
    }

    private void CloseCrashPanel()
    {
        CloseCrashOverlayOnly();
        OpenSettingsPanel();
    }

    /// <summary>#64 窗口唯一令：崩溃/运行统计面板纯关闭（不回设置页），供统一闸复用。</summary>
    private void CloseCrashOverlayOnly()
    {
        if (_crashOverlay is null) return;
        _root.RemoveView(_crashOverlay);
        _crashOverlay.Dispose();
        _crashOverlay = null;
        _crashScroll = null;
        _crashCleanBtn = null;
        _crashCloseBtn = null;
    }

    /// <summary>子页面板骨架：标题 + 权重铺满的滚动正文 + 统一内边距。按钮行由各页自挂（键位各不同）。</summary>
    private (LinearLayout Box, int Pad) BuildOverlayPanel(string title, string body, out ScrollView scroll)
    {
        int pad = Dp(24);
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(pad, pad, pad, pad);
        box.SetBackgroundColor(InkTranslucency.PanelColor(this));
        var titleView = new TextView(this) { Text = title, TextSize = UiScale.Title };
        titleView.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(titleView);
        box.AddView(titleView, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = pad });
        scroll = new ScrollView(this);
        var bodyView = new TextView(this) { Text = body, TextSize = UiScale.Caption };
        bodyView.SetTextIsSelectable(false);
        bodyView.Focusable = false; // 正文不抢焦点，焦点只在按钮间转
        bodyView.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(bodyView);
        scroll.AddView(bodyView);
        box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f)); // 权重铺满
        return (box, pad);
    }

    /// <summary>
    /// 日志正文后台装载（真机反馈批#23，老板令「一定不能卡线程」）：点开先出占位文案，
    /// 读盘+解析全在后台线程，主线程只收结果刷新——运行统计/崩溃日志/自诊断三页共用。
    /// 面板先开先关（读盘期间关闭）：回帖前校验面板还开着，关了就丢弃结果不碰已释放视图。
    /// </summary>
    private void LoadOverlayBodyAsync(ScrollView scroll, Func<string> build, string placeholder, Func<bool> panelOpen)
    {
        if (scroll.GetChildAt(0) is TextView bodyView) bodyView.Text = placeholder;
        _ = Task.Run(() =>
        {
            string text;
            try { text = build(); }
            catch (Exception ex) { text = "读取失败：" + ex.Message; }
            _main.Post(() =>
            {
                try { if (panelOpen() && scroll.GetChildAt(0) is TextView tv) tv.Text = text; }
                catch { /* 面板已关：结果丢弃 */ }
            });
        });
    }

    /// <summary>自管面板按钮统一描边（崩溃日志/自诊断/进阶调优共用，ui-remote 归一条款）。</summary>
    private void StyleOverlayButton(TextView b, GradientDrawable focusDrawable)
    {
        var idleBg = new ColorDrawable(Color.Argb(96, InkTranslucency.InkR, InkTranslucency.InkG, InkTranslucency.InkB));
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

    // —— 自诊断（S3 口径）：五类现成数据只读一页 + 导出 ——

    private void ShowDiagPanel()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧
        try
        {
            var (box, pad) = BuildOverlayPanel("自诊断（只读）", "诊断中…", out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var export = new TextView(this) { Text = "导出日志", Gravity = GravityFlags.Center };
            export.SetPadding(pad, Dp(14), pad, Dp(14));
            export.Click += (_, _) =>
            {
                // 导出快照后台组装（#23 同条款：快照含 256KB 日志尾扫，不占主线程）
                _ = Task.Run(() =>
                {
                    var path = DiagLog.Export(ApplicationContext!, BuildDiagSnapshot());
                    CrashNet.Record(path is null ? "[诊断] 导出失败" : $"[诊断] 已导出：{path}");
                    _main.Post(() => ShowToast(path is null ? "导出失败，请重试" : "已导出到文件管理器可见目录"));
                });
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseDiagPanel();
            export.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            export.NextFocusRightId = close.Id;
            close.NextFocusLeftId = export.Id;
            StyleOverlayButton(export, InkTranslucency.FocusDrawable());
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(60); // #70 口径归一后实值直出：52dp 装不下 22sp 文字+28dp 内边距，抬到 60dp 保字完整
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
            CloseSettingsPanel();
            close.RequestFocus();
            // 快照组装后台走（#23）：内存探测+256KB 日志尾扫+逐行统计不占主线程
            LoadOverlayBodyAsync(scroll, BuildDiagSnapshot, "诊断中…", () => ReferenceEquals(_diagOverlay, overlay));
        }
        catch (Exception ex) { CrashNet.Record($"[诊断] 打开失败：{ex.Message}"); ShowToast("诊断打开失败"); }
    }

    private void CloseDiagPanel()
    {
        CloseDiagOverlayOnly();
        OpenSettingsPanel();
    }

    /// <summary>#64 窗口唯一令：自诊断面板纯关闭（不回设置页），供统一闸复用。</summary>
    private void CloseDiagOverlayOnly()
    {
        if (_diagOverlay is null) return;
        _root.RemoveView(_diagOverlay);
        _diagOverlay.Dispose();
        _diagOverlay = null;
        _diagScroll = null;
        _diagExportBtn = null;
        _diagCloseBtn = null;
    }

    // —— 关于面板（v47 开源合规）：Apache-2.0 许可文本只读一页，来源随包 Assets/NOTICE.txt ——

    private void ShowLicensePanel()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧
        try
        {
            string body;
            try
            {
                // 许可文本随包走 Assets（NET Android 默认打包 Assets/ 目录），读不到按降级文案不挡面板
                using var stream = Assets!.Open("NOTICE.txt");
                using var reader = new StreamReader(stream);
                body = reader.ReadToEnd();
            }
            catch
            {
                body = "TaoLive · Apache License 2.0\n\n许可文本缺失（NOTICE.txt 未随包），完整条款见项目仓库 LICENSE 文件。";
            }
            var (box, pad) = BuildOverlayPanel("关于：TaoLive", body, out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseLicensePanel();
            close.Id = View.GenerateViewId();
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(60); // #70 口径归一后实值直出：52dp 装不下 22sp 文字+28dp 内边距，抬到 60dp 保字完整
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });
            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _licenseOverlay = overlay;
            _licenseScroll = scroll;
            _licenseCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus();
        }
        catch (Exception ex) { CrashNet.Record($"[关于] 打开失败：{ex.Message}"); ShowToast("关于打开失败"); }
    }

    private void CloseLicensePanel()
    {
        CloseLicenseOverlayOnly();
        OpenSettingsPanel();
    }

    /// <summary>#64 窗口唯一令：许可面板纯关闭（不回设置页），供统一闸复用。</summary>
    private void CloseLicenseOverlayOnly()
    {
        if (_licenseOverlay is null) return;
        _root.RemoveView(_licenseOverlay);
        _licenseOverlay.Dispose();
        _licenseOverlay = null;
        _licenseScroll = null;
        _licenseCloseBtn = null;
    }

    /// <summary>五类摘要组装：全部经现有公开接口/公开成员取数，不落设备标识、不新增采集。</summary>
    private string BuildDiagSnapshot()
    {
        // 版本：PackageInfo 现成读取；摘要只显 VersionName（versionCode 字段 API28 起废弃，不碰旧读法）
        string versionLine;
        try
        {
            // GetPackageInfo(string,int) 旧重载：全部支持档（minSdk 24）可用，只读 VersionName 不碰 versionCode
#pragma warning disable CA1422, CS0612, CS0619
            var pkg = PackageManager!.GetPackageInfo(PackageName!, 0)!;
#pragma warning restore CA1422, CS0612, CS0619
            var abi = Build.SupportedAbis is { Count: > 0 } abis ? abis[0] : "?";
            versionLine = $"版本：v{pkg.VersionName} · {abi}";
        }
        catch (Exception ex) { versionLine = "版本：读取失败 " + ex.Message; }

        // 时钟：当前墙钟 + 应用进程已运行时长 + 时区偏移
        // 真机反馈批#13 修（2026-11-15 真机照片归因）：原用 Environment.TickCount64（机器开机时长），
        // 盒子 2.5 天没重启就被报成「已运行 61h02m」实锤；改按进程起始时刻算应用真实运行时长。
        // vc74 P3①：Process 句柄改 using 显式释放（原一次性表达式未 Dispose，句柄滞留到 GC；
        // 诊断页每次打开多一个泄漏点，低配 TV 上 GC 慢，句柄堆积放大）。
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        var up = DateTime.Now - proc.StartTime;
        var clockLine = $"时钟：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 已运行 {(int)up.TotalHours}h{up.Minutes:D2}m · " +
                        $"时区 UTC{TimeZoneInfo.Local.BaseUtcOffset:hh\\:mm}";

        // 网络：活动网络现成判定
        string networkLine;
        try
        {
            var cm = GetSystemService(ConnectivityService) as Android.Net.ConnectivityManager;
            networkLine = $"网络：{(cm?.ActiveNetwork != null ? "就绪" : "不可用（无活动网络）")}";
        }
        catch (Exception ex) { networkLine = "网络：读取失败 " + ex.Message; }

        // 播放：当前会话真实状态（频道/状态/换源账/线路位）
        var sessionLine = _manager.Current is { } s
            ? $"播放：{s.CurrentChannel?.Name ?? "-"} · 状态 {s.State} · 换源 {s.FailoverCount} 次 · 线路 {s.CurrentLineIndex + 1}/{s.LineCount}"
            : "播放：当前无会话";

        // 内核与水位：ExoPlayer（Media3）+ 按内存自适应的深水保满水位线
        string kernelLine;
        try
        {
            var am = (ActivityManager?)GetSystemService(ActivityService);
            var mi = new ActivityManager.MemoryInfo();
            am?.GetMemoryInfo(mi);
            kernelLine = $"内核：ExoPlayer（Media3）· 缓冲水位 {BufferPolicy.MaxWatermarkSeconds(mi.TotalMem)} 秒 · 总内存 {mi.TotalMem / (1024L * 1024L * 1024L)}GB";
        }
        catch (Exception ex) { kernelLine = "内核：读取失败 " + ex.Message; }

        // 运行日志：从日志尾部统计现成打点（无日志文件按空账计，不挡诊断页）
        string tail = "";
        try { tail = DiagLog.ReadTail(System.IO.Path.Combine(FilesDir!.AbsolutePath!, "taolive_crash.log"), 256 * 1024); }
        catch { }
        int failCount = 0, crashCount = 0;
        foreach (var line in tail.Split('\n'))
        {
            // 真机反馈批#13 修（2026-11-15 真机照片归因）：取流失败原只数 [ConnectFailure]，
            // 换源时内核报的播放器错误（BAD_HTTP_STATUS 等 [换源]·player error 行）一条不数，账偏小实锤。
            // 补数 player error 行：换源失败也是取流失败，口径与崩溃日志页逐行对齐。
            if (line.Contains("[ConnectFailure]", StringComparison.Ordinal) || line.Contains("(player error", StringComparison.Ordinal)) failCount++;
            else if (line.Contains("未捕获异常", StringComparison.Ordinal)) crashCount++;
        }
        var startupLine = $"运行日志 {LogSizeLabel(tail.Length)}（截尾视图）· 取流失败 {failCount} 条 · 未捕获异常 {crashCount} 条";

        return DiagLog.BuildSnapshot(versionLine, clockLine, networkLine, sessionLine, kernelLine, startupLine,
            DiagLog.RecentFailuresFromLog(tail, 10));
    }

    /// <summary>
    /// 运行日志大小显示口径（真机反馈批 2026-11-15 修）：原整除 1024，日志不足 1KB 显示「0KB」，
    /// 与崩溃日志页「有内容」看似矛盾（实为同一文件同一账，纯显示口径）；改 KB 向上取整，空账明示「暂无日志」。
    /// </summary>
    private static string LogSizeLabel(int chars) =>
        chars == 0 ? "0KB（暂无日志）" : $"{Math.Max(1, (int)Math.Ceiling(chars / 1024.0))}KB";

    // —— 进阶调优面板：旋钮全量可调 + 每项讲清作用 + 一键还原 ——

    /// <summary>旋钮档位（确认键循环）。改动即存偏好并即时生效（方案书收口令：ProxyPort 假旋钮已删）。</summary>
    private static readonly int[] AdvGateSteps = { 1, 2, 3, 5, 8, 12 };
    private static readonly int[] AdvReprobeMinSteps = { 1, 2, 5, 10, 15 };
    private static readonly int[] AdvCacheSecSteps = { 15, 30, 60, 120, 300 };
    private static readonly int[] AdvWindowSecSteps = { 30, 45, 60 }; // #67 库存档三档位（老板令 2026-11-15）
    private static readonly int[] AdvLagSecSteps = { 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 }; // 落后目标 5 秒一档（老板令 2026-11-16）
    private static readonly int[] AdvRetrySecSteps = { 30, 60, 120, 300, 600 }; // 起步；封顶 16 分钟（AdvancedTuning.DefaultRetryMaxMs 口径）
    private static readonly int[] AdvFailoverCapSteps = { 1, 2, 3, 4, 5, 6, 8, 10 }; // 防快切闸：下限 1 次/分钟全局钳死（蓝图 v2.5），上限 10

    private static int NextStep(int[] steps, int current)
    {
        for (int i = 0; i < steps.Length; i++)
            if (steps[i] > current) return steps[i];
        return steps[^1]; // 到最大档即停（边界限位，不回绕）
    }

    private static int PrevStep(int[] steps, int current)
    {
        for (int i = steps.Length - 1; i >= 0; i--)
            if (steps[i] < current) return steps[i];
        return steps[0]; // 到最小档即停
    }

    private void ShowAdvancedTuningPanel()
    {
        EnforceSingleWindow(); // #64 窗口唯一令：开新先关旧
        try
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int padAdv = Dp(24);
            box.SetPadding(padAdv, padAdv, padAdv, padAdv);
            box.SetBackgroundColor(InkTranslucency.PanelColorDeep(this)); // 底色加深一档，不跟画面亮度走
            var title = new TextView(this) { Text = "进阶调优", TextSize = UiScale.Title };
            title.SetTextColor(InkTranslucency.TextPrimary(this));
            InkTranslucency.ApplyReadabilityShadow(title);
            box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(10) });

            var scroll = new ScrollView(this);
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            scroll.AddView(body);
            box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

            var refreshers = new List<Action>();

            // 加减号交互：每项「− 当前值 +」——值纯展示不落光标，光标只在 −/+ 两个焦点位间移；
            // 加减真改值且有界：预设最大/最小档，到边界即停不回绕，边界处对应钮灰显。
            void AddKnob(string desc, int[] steps, Func<string> valueText, Action<int> set, Func<int> cur)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetPadding(Dp(24), Dp(12), Dp(24), Dp(2));

                var minus = new TextView(this) { Text = "−", Gravity = GravityFlags.Center };
                var value = new TextView(this) { Gravity = GravityFlags.Start }; // 值靠左与说明对齐
                var plus = new TextView(this) { Text = "+", Gravity = GravityFlags.Center };

                int btn = Dp(40);
                void StyleKnob(TextView b)
                {
                    StyleOverlayButton(b, InkTranslucency.AccentFocusDrawable()); // 选中态加重：朱砂底
                    b.TextSize = UiScale.Item;
                }
                StyleKnob(minus);
                StyleKnob(plus);
                value.Focusable = false;
                value.TextSize = UiScale.Item;
                value.SetTextColor(Color.ParseColor("#F7F5EE"));
                value.SetPadding(Dp(12), 0, Dp(12), 0);
                InkTranslucency.ApplyReadabilityShadow(value);

                minus.Click += (_, _) => { if (cur() > steps[0]) { set(PrevStep(steps, cur())); SaveAdvancedTuning(); Refresh(); } };
                plus.Click += (_, _) => { if (cur() < steps[^1]) { set(NextStep(steps, cur())); SaveAdvancedTuning(); Refresh(); } };
                row.AddView(minus, new LinearLayout.LayoutParams(btn, btn));
                row.AddView(value, new LinearLayout.LayoutParams(0, -2, 1f));
                row.AddView(plus, new LinearLayout.LayoutParams(btn, btn));
                body.AddView(row);

                var sub = new TextView(this) { Text = desc, TextSize = UiScale.Caption };
                sub.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
                InkTranslucency.ApplyReadabilityShadow(sub);
                sub.SetPadding(Dp(24), 0, Dp(24), Dp(8));
                body.AddView(sub);

                void Refresh()
                {
                    value.Text = valueText();
                    minus.Alpha = cur() <= steps[0] ? 0.35f : 1f; // 边界灰显：到头即停
                    plus.Alpha = cur() >= steps[^1] ? 0.35f : 1f;
                }
                refreshers.Add(Refresh);
                Refresh();
            }

            // 7 个旋钮（每项下挂一行人话作用说明）：写入 AdvancedTuning 即时生效（播放链读门面）
            AddKnob("网络解析出毛病时，撑几次失败才启动自救判别——调低自救快但易误伤，调高稳但自救慢",
                AdvGateSteps,
                () => $"自愈门槛：连败 {AdvancedTuning.SelfHealFailGate} 次（默认 3）",
                v => AdvancedTuning.Apply(v, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs),
                () => AdvancedTuning.SelfHealFailGate);
            AddKnob("自救起来之后，隔多久偷偷试一次能否回直连——调低回得快但探测勤，调高省流量回得慢",
                AdvReprobeMinSteps,
                () => $"自愈回探：{AdvancedTuning.SelfHealReprobeMs / 60_000} 分钟（默认 5）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, v * 60_000L, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs),
                () => (int)(AdvancedTuning.SelfHealReprobeMs / 60_000));
            AddKnob("频道清单数据在手上留多久不重取——留久省流量，留短换台信息更新鲜",
                AdvCacheSecSteps,
                () => $"清单缓存：{AdvancedTuning.ListCacheSec} 秒（默认 60）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, v, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs),
                () => AdvancedTuning.ListCacheSec);
            AddKnob("被源服务器拒绝后退多远再试——跟风控博弈的分寸：调急了容易挨打，调钝了台恢复慢（封顶 16 分钟）",
                AdvRetrySecSteps,
                () => $"重试节奏：起步 {AdvancedTuning.RetryBaseMs / 1000} 秒（默认 120）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, v * 1000L, AdvancedTuning.RetryMaxMs),
                () => (int)(AdvancedTuning.RetryBaseMs / 1000));
            AddKnob("切片预取的库存时长：出画后后台往深里攒多久的切片——攒深卡不住但直播延迟感重，攒浅跟得紧但抗抖差；改档后新开的台生效（默认 60）",
                AdvWindowSecSteps,
                () => $"预取库存：{AdvancedTuning.PrefetchWindowSec} 秒",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, v),
                () => AdvancedTuning.PrefetchWindowSec);
            AddKnob("起播和追赶落在直播边缘后面多远——落后越深手上的旧货越多越抗卡，但看到的画面越旧；源拿不出这么深就按源的实际深度；0=贴边看最新。改档后新开的台生效（默认 60）",
                AdvLagSecSteps,
                () => $"播放落后：{AdvancedTuning.LiveLagSec} 秒",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, AdvancedTuning.PrefetchWindowSec, v),
                () => AdvancedTuning.LiveLagSec);
            AddKnob("一分钟内自动换源的次数上限——到顶冻结转慢恢复，防连环快切烧线：调低更稳但坏线恢复慢，调高坏线切得快；下限保底 1 次/分钟调不灭（默认 3）",
                AdvFailoverCapSteps,
                () => $"防快切闸：{AdvancedTuning.FailoverCapPerMin} 次/分钟",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs, AdvancedTuning.PrefetchWindowSec, AdvancedTuning.LiveLagSec, v),
                () => AdvancedTuning.FailoverCapPerMin);

            var tip = new TextView(this)
            {
                // vc105 老板令：文案放一行（原写死 \n 拆两行，改一行自然排版）
                Text = "点按 − / + 调档；到头即停。改坏了随时点「一键还原默认」回出厂值，全部即时生效。",
                TextSize = UiScale.Caption,
            };
            tip.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tip);
            tip.SetPadding(Dp(24), Dp(6), Dp(24), Dp(10));
            body.AddView(tip);

            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var reset = new TextView(this) { Text = "一键还原默认", Gravity = GravityFlags.Center };
            reset.SetPadding(padAdv / 2, Dp(14), padAdv / 2, Dp(14));
            reset.Click += (_, _) =>
            {
                AdvancedTuning.ResetToDefaults();
                SaveAdvancedTuning();
                foreach (var rf in refreshers) rf(); // 原位刷新值与灰显，不整页重建（焦点不丢）
                ShowToast("进阶调优：已全部还原出厂默认");
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(padAdv / 2, Dp(14), padAdv / 2, Dp(14));
            close.Click += (_, _) => CloseAdvancedPanel();
            reset.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            reset.NextFocusRightId = close.Id;
            close.NextFocusLeftId = reset.Id;
            StyleOverlayButton(reset, InkTranslucency.AccentFocusDrawable()); // 还原=朱砂（改动性按钮）
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(60); // #70 口径归一后实值直出：52dp 装不下 22sp 文字+28dp 内边距，抬到 60dp 保字完整
            btns.AddView(reset, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = padAdv });

            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _advOverlay = overlay;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus(); // 默认焦点落「关闭」，防误改
        }
        catch (Exception ex) { CrashNet.Record($"[进阶调优] 打开失败：{ex.Message}"); ShowToast("进阶调优打开失败"); }
    }

    private void CloseAdvancedPanel()
    {
        CloseAdvancedOverlayOnly();
        OpenSettingsPanel();
    }

    /// <summary>#64 窗口唯一令：进阶调优面板纯关闭（不回设置页），供统一闸复用。</summary>
    private void CloseAdvancedOverlayOnly()
    {
        if (_advOverlay is null) return;
        _root.RemoveView(_advOverlay);
        _advOverlay.Dispose();
        _advOverlay = null;
    }

    private void SaveAdvancedTuning() // 旋钮写偏好（跨启动记忆；Apply 即生效）
    {
        _prefs.Edit()
            ?.PutInt("adv_fail_gate", AdvancedTuning.SelfHealFailGate)
            ?.PutInt("adv_reprobe_min", (int)(AdvancedTuning.SelfHealReprobeMs / 60_000))
            ?.PutInt("adv_cache_sec", AdvancedTuning.ListCacheSec)
            ?.PutInt("adv_retry_base_sec", (int)(AdvancedTuning.RetryBaseMs / 1000))
            ?.PutInt("adv_retry_max_min", (int)(AdvancedTuning.RetryMaxMs / 60_000))
            ?.PutInt("adv_prefetch_sec", AdvancedTuning.PrefetchWindowSec) // #67 库存档旋钮
            ?.PutInt("adv_live_lag_sec", AdvancedTuning.LiveLagSec) // 落后目标旋钮（老板令 2026-11-16）
            ?.PutInt("adv_failover_cap", AdvancedTuning.FailoverCapPerMin) // 防快切闸第 7 旋钮（老板 2026-11-17 拍板接通）
            ?.Apply();
    }
}
