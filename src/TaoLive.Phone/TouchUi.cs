using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using TaoLive.Foundation;

namespace TaoLive.Phone;

/// <summary>
/// 手机版触屏交互层（2026-11-27 真机整改令，19 条问题清单落点）：
/// - 单击画面=控制条显收（3 秒自动收）；长按=收藏/取消当前台（口径不变）；
/// - 切台手势纠正（老板令「左上右下」）：横屏左滑=上一台、右滑=下一台；竖屏同构左右滑
///   （上下滑让位亮度/音量手势——老板令「参考普通播放器做法」）；支持慢拖（OnScroll 累计）不只认快滑；
/// - 亮度/音量手势：竖横屏均按左右半区——左半区上下滑=调亮度、右半区上下滑=调音量；指示=自绘竖条+单色图标跟手（2026-11-27 老板令「滑动还是要、样式也还是要有」）；控制条音量按钮走系统音量条（同日老板令「直接用手机的」）；
///   vc117 老板令「出画不提亮」：应用不再主动改亮度——启动/出画保持当前亮度原样，仅手势调亮时从当前实际亮度起步（原「持久记忆+启动强制套用」整组撤除）。
/// - 控制条按原型 v19–v26 重构：三行（台名读数/节目单/按钮两端分组），vc116 统一信息分块令：横竖屏同构三行；
///   按钮单色文本图标（去 emoji 彩色呈现）；补音量按钮（老板令：一次 ±10）；
/// - 方向锁持久化（taolive_prefs:orientation_lock）不变。
/// 底层零改动：换台走既有 PlayAt/SwitchToCurrent，收藏走既有 ToggleFavoriteOnCurrent，设置走既有 OpenSettingsPanel。
/// </summary>
public partial class MainActivity
{
    private LinearLayout? _controlBar;
    private LockIconView? _lockBtn;
    private TextView? _cbTitle;    // 控制条行1：台名
    private TextView? _cbMini;     // 控制条行1：分辨率 mini（老板令 2026-11-28「帧率可以不显示」：只显分辨率）
    private TextView? _cbNowName;  // 控制条行2：节目名（占余宽省略，vc115 拆段保读数）
    private TextView? _cbNowTime;  // 控制条行2：「· 画面时刻：HH:mm:ss」（完整不截，vc115）
    private TextView? _cbAuxWater; // 控制条行3：「实时水位：X秒」（完整不截，vc115）
    private TextView? _cbAuxNext;  // 控制条行3：「· 接下来：…」（占余宽省略，vc115）
    private bool _controlBarShown;
    private Java.Lang.Runnable? _controlBarHide;
    private GestureDetector? _gesture;
    private bool _orientationLocked; // 方向锁（持久化）：锁=钉当前方向，解锁=随传感器
    // vc112 老板令「图标统一+调大」：六枚图标（◀ ▶ ☰ ⚙ 全屏 锁）统一字号——T(1.25)≈21sp（原 Item≈19sp），
    // 自绘图标（全屏/锁）OnDraw 按 ScaledDensity 换算同尺寸绘制，热区 44dp 不变。
    private static float IconGlyph => UiScale.T(1.25f);

    private FrameLayout? _hud;                 // 亮度/音量手势悬浮指示（竖条+图标，普通播放器样式，2026-11-27 老板令）
    private HudIconView? _hudIcon;
    private View? _hudFill;                    // 竖条填充（高度=音量/亮度比例）
    private Java.Lang.Runnable? _hudHide;
    private const int HudTrackDp = 120;        // 竖条轨道高（dp，390 基准；v31 老板令「参考系统窄条」定稿 5×120）
    private const int HudIconDp = 16;          // 指示图标边长（v31 原型口径：太阳 SVG 16×16）
    private const int HudTrackWidthDp = 5;     // 竖条轨道宽（v31 老板令：系统滚动条式细条 5×120，不占画面）
    private AudioManager? _audio;
    private float _brightness = 0.5f;          // 0.05–1 手势工作值：起点=当前实际亮度（vc117 老板令「出画不提亮」，不再持久记忆）

    /// <summary>触屏层装配（BuildRoot 末尾调用）：手势挂视频面，控制条挂播放页。</summary>
    private void BuildTouchLayer()
    {
        _orientationLocked = _prefs.GetBoolean("orientation_lock", false);
        _brightness = ReadCurrentBrightness(); // vc117 老板令「出画不提亮」：启动不套用任何亮度，当前是什么就保持什么
        _audio = (AudioManager?)GetSystemService(AudioService);

        _gesture = new GestureDetector(this, new PhoneGestureListener(this));

        // vc110 老板令「整屏高度都是触控区」：手势挂播放页整层（视频+黑边全收），
        // 控制条/抽屉/面板开着时各自消费自己的触摸，天然不抢
        _playerPage.Touch += (_, e) =>
        {
            e.Handled = _gesture!.OnTouchEvent(e.Event!);
        };

        BuildHud();
        BuildControlBar();
    }

    // —— 亮度/音量（手势自绘竖条；音量按钮走系统音量条，2026-11-27 老板令） ————

    private void BuildHud()
    {
        _hud = new FrameLayout(this);
        _hud.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));

        // 竖排：图标在上、竖条在下（普通播放器通行样式，无文字）
        var col = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        col.SetGravity(GravityFlags.Center);
        col.SetPadding(UiScale.Px(28), UiScale.Px(20), UiScale.Px(28), UiScale.Px(20));

        _hudIcon = new HudIconView(this);
        col.AddView(_hudIcon, new LinearLayout.LayoutParams(UiScale.Px(HudIconDp), UiScale.Px(HudIconDp)) { Gravity = GravityFlags.CenterHorizontal });

        var gap = new View(this);
        col.AddView(gap, new LinearLayout.LayoutParams(1, UiScale.Px(12)));

        var track = new FrameLayout(this);
        var trackBg = new GradientDrawable();
        int trackArgb = new Color(Color.ParseColor(InkTranslucency.TextSecondary)).ToArgb();
        trackBg.SetColor(Color.Argb(70, (trackArgb >> 16) & 0xFF, (trackArgb >> 8) & 0xFF, trackArgb & 0xFF));
        trackBg.SetCornerRadius(UiScale.Px(5));
        track.Background = trackBg;

        var fillBg = new GradientDrawable();
        fillBg.SetColor(InkTranslucency.TextPrimary(this));
        fillBg.SetCornerRadius(UiScale.Px(5));
        _hudFill = new View(this) { Background = fillBg };
        track.AddView(_hudFill, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiScale.Px(3)) { Gravity = GravityFlags.Bottom });

        col.AddView(track, new LinearLayout.LayoutParams(UiScale.Px(HudTrackWidthDp), UiScale.Px(HudTrackDp)));
        _hud.AddView(col);
        _hud.Visibility = ViewStates.Gone;
        PositionHud();
        _playerPage.AddView(_hud); // vc116①：不传新 lp——PositionHud 已落 LayoutParameters，再挂带 Gravity 的 lp 会整体覆盖落位
    }

    /// <summary>悬浮指示条落位（v28 老板令定稿④「落位」）：竖屏=亮度左上（顶部黑边内），
    /// 横屏=亮度左黑边垂直居中（画面满高后左右留黑边）。音量走系统音量条（⑤定稿），自绘条只服务亮度。转屏重算。</summary>
    internal void PositionHud()
    {
        if (_hud is null || _playerPage is null) return;
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        var lp = _hud.LayoutParameters as FrameLayout.LayoutParams;
        if (lp is null)
        {
            lp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            _hud.LayoutParameters = lp;
        }
        if (landscape)
        {
            lp.Gravity = GravityFlags.CenterVertical | GravityFlags.Left; // 亮度左黑边垂直居中
            lp.LeftMargin = UiScale.Px(10); // 原型 .landscape .ind-bri{left:10px}
            lp.RightMargin = 0;
            lp.TopMargin = 0;
        }
        else
        {
            lp.Gravity = GravityFlags.Top | GravityFlags.Left; // 竖屏亮度左上
            lp.LeftMargin = UiScale.Px(12); // 原型 .ind-bri{top:56px;left:12px}（56px 含状态栏，挂播放页内由黑边区承接）
            lp.TopMargin = UiScale.Px(24);
            lp.RightMargin = 0;
        }
    }

    /// <summary>竖条悬浮指示：kind 选图标，fraction 0–1 定填充高；700ms 无操作自动收。</summary>
    private void ShowHudBar(HudIconView.IconKind kind, float fraction)
    {
        if (_hud is null || _hudIcon is null || _hudFill is null) return;
        _hudIcon.SetKind(kind);
        float f = Math.Clamp(fraction, 0f, 1f);
        int fillH = Math.Max((int)(UiScale.Px(HudTrackDp) * f), UiScale.Px(3)); // 0 也留可见头
        if (_hudFill.LayoutParameters is FrameLayout.LayoutParams lp) lp.Height = fillH;
        else _hudFill.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, fillH) { Gravity = GravityFlags.Bottom };
        _hud.Visibility = ViewStates.Visible;
        if (_hudHide is { } old) _main.RemoveCallbacks(old);
        _hudHide = new Java.Lang.Runnable(() => _hud!.Visibility = ViewStates.Gone);
        _main.PostDelayed(_hudHide, 700);
    }

    // —— 亮度/音量（手势与按钮共用单点） ————————————————————

    /// <summary>读当前实际亮度（vc117 老板令「出画不提亮」）：窗口有覆写用窗口值，否则取系统亮度，兜底 0.5；只读不改。</summary>
    private float ReadCurrentBrightness()
    {
        try
        {
            if (Window?.Attributes is { } attr && attr.ScreenBrightness >= 0f)
                return Math.Clamp(attr.ScreenBrightness, 0.05f, 1f);
        }
        catch { }
        try
        {
            int v = global::Android.Provider.Settings.System.GetInt(ContentResolver!,
                global::Android.Provider.Settings.System.ScreenBrightness, 128);
            return Math.Clamp(v / 255f, 0.05f, 1f);
        }
        catch { }
        return 0.5f;
    }

    private void SetBrightness(float value)
    {
        _brightness = Math.Clamp(value, 0.05f, 1f);
        try { if (Window?.Attributes is { } attr) attr.ScreenBrightness = _brightness; } catch { }
        ShowHudBar(HudIconView.IconKind.Sun, _brightness); // 亮度 0.05–1 直接作比例
    }

    // —— 控制条（原型 v19–v26：三行结构/两端分组/单色图标 + 音量按钮） ———

    private void BuildControlBar()
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        _controlBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        _controlBar.SetBackgroundColor(InkTranslucency.PanelColor(this)); // vc111 老板令：全页面统一电视端水墨透光（原近实色 232 闷画面）
        // vc117 令2①+8+19/20（2026-11-28 老板拍板）：控制条贴底，底边距吃圆角安全边（圆角半径+8dp，
        // 拿不到走 24dp 兜底）——大圆角屏不再裁掉按钮，一开始就避让不留角部裁切。
        _controlBar.SetPadding(UiScale.Px(16), UiScale.Px(8), UiScale.Px(16), Math.Max(UiScale.Px(10), BottomCornerSafePx()));

        // vc116③ 统一信息分块令：横竖屏统一三行结构（行1 台名+清晰度 mini / 行2 节目名+画面时刻 /
        // 行3 水位+接下来），信息分块不再横屏特判；全部单行不换行、读数段完整不截断。
        TextView NewBarText(float size, Color color, bool bold = false)
        {
            var tv = new TextView(this) { TextSize = size, Ellipsize = TextUtils.TruncateAt.End };
            tv.SetSingleLine(true);
            tv.SetTextColor(color);
            if (bold) tv.Typeface = Typeface.DefaultBold!;
            InkTranslucency.ApplyReadabilityShadow(tv);
            return tv;
        }

        // 行1：台名（占余宽、超长省略）+ 分辨率 mini（右对齐小字，无数据隐藏；老板令 2026-11-28 帧率不显示）
        var infoRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        infoRow.SetGravity(GravityFlags.CenterVertical);
        _cbTitle = NewBarText(UiScale.Item, InkTranslucency.TextPrimary(this), bold: true);
        infoRow.AddView(_cbTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _cbMini = NewBarText(UiScale.Caption, Color.ParseColor(InkTranslucency.TextSecondary));
        _cbMini.Gravity = GravityFlags.End;
        _cbMini.SetPadding(UiScale.Px(10), 0, 0, 0); // vc115 分段令：mini 与节目名之间留间距
        infoRow.AddView(_cbMini);
        _controlBar.AddView(infoRow);

        // 行2（v31 原型 epg-bar；vc115 拆段保读数）：节目名（占余宽省略）+「· 画面时刻：HH:mm:ss」（完整不截）
        var nowRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _cbNowName = NewBarText(UiScale.Item, InkTranslucency.TextPrimary(this), bold: true);
        nowRow.AddView(_cbNowName, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _cbNowTime = NewBarText(UiScale.Item, InkTranslucency.TextPrimary(this), bold: true);
        _cbNowTime.SetPadding(UiScale.Px(8), 0, 0, 0);
        nowRow.AddView(_cbNowTime);
        _controlBar.AddView(nowRow);

        // 行3：「实时水位：X秒」（完整）+ 接下来（占余宽省略）——读数段永不被节目名挤掉
        var auxRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _cbAuxWater = NewBarText(UiScale.Caption, Color.ParseColor(InkTranslucency.TextSecondary));
        _cbAuxWater.SetPadding(0, 0, UiScale.Px(8), 0);
        auxRow.AddView(_cbAuxWater);
        _cbAuxNext = NewBarText(UiScale.Caption, Color.ParseColor(InkTranslucency.TextSecondary));
        auxRow.AddView(_cbAuxNext, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _controlBar.AddView(auxRow);

        // 按钮行：两端分组（v21）——左组切台、右组功能；中间留白撑开
        // vc110 老板令：音量加减按钮全删（横竖屏都删，音量走手势/系统键）
        var btnRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        btnRow.SetGravity(GravityFlags.CenterVertical);
        var left = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        var right = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };

        // vc113（老板真机三单②+v29 六行四态图例）：六枚图标全改自绘——◀▶☰⚙ 字符图标跟字体度量、
        // 自绘按几何中心，两套混排中心线差 3.6px、字面高 13~22px 参差。现统一走自绘视图（44dp 热区、
        // IconGlyph 字号绘制、几何居中），并按原型 v20/v29 补半透明胶囊底与按压态（默认 12% 白底、按压 26%）。
        View AddIcon(LinearLayout into, BarIconView.IconKind kind, EventHandler click)
        {
            var b = new BarIconView(this) { Kind = kind, Clickable = true };
            b.Background = BarButtonBackground(); // 令16：36dp 定尺寸盒自带热区，旧文字钮 padding 退场
            b.Click += click;
            // vc116④：相邻按钮统一 8dp 间距（首钮贴组首不留头距）
            into.AddView(b, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = into.ChildCount > 0 ? UiScale.Px(8) : 0 });
            return b;
        }

        AddIcon(left, BarIconView.IconKind.Prev, (_, _) => SwitchChannel(-1));
        AddIcon(left, BarIconView.IconKind.Next, (_, _) => SwitchChannel(1));

        AddIcon(right, BarIconView.IconKind.List, (_, _) => { HideControlBar(); ShowList(); });
        AddIcon(right, BarIconView.IconKind.Gear, (_, _) => { HideControlBar(); OpenSettingsPanel(); });

        // 翻转按钮（vc110 老板令：⟳ 换两枚图标——竖屏显「进全屏」、横屏显「返回竖屏」，自绘单色同风格）
        var flipBtn = new FullscreenIconView(this)
        {
            Kind = landscape ? FullscreenIconView.IconKind.ExitFullscreen : FullscreenIconView.IconKind.EnterFullscreen,
            Clickable = true,
        };
        flipBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, UiScale.Px(36))
        { LeftMargin = UiScale.Px(8) }; // 令16：36dp 定尺寸盒（原 44dp），图标居中稍大
        flipBtn.Background = BarButtonBackground(); // vc116②：线框皮，按压态强描边
        flipBtn.Click += (_, _) =>
        {
            bool land = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
            RequestedOrientation = land ? ScreenOrientation.SensorPortrait : ScreenOrientation.SensorLandscape;
            // vc116⑤：去提示弹条——转屏结果画面自见，不再弹字
        };
        right.AddView(flipBtn);

        // 锁定按钮（vc110 老板令：文字「锁/已锁」换图标——解锁=开锁、锁定横屏=横屏锁、锁定竖屏=竖屏锁，自绘单色同风格）
        var lockBtn = new LockIconView(this) { Clickable = true };
        lockBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, UiScale.Px(36))
        { LeftMargin = UiScale.Px(8) }; // 令16：36dp 定尺寸盒（原 44dp），图标居中稍大
        lockBtn.Background = BarButtonBackground(); // vc116②：线框皮，按压态强描边
        lockBtn.Click += (_, _) => ToggleOrientationLock();
        _lockBtn = lockBtn;
        right.AddView(lockBtn);

        // vc110：音量按钮删除后竖屏 6 钮单行放得下，两行拆分口径退场
        btnRow.AddView(left);
        var space = new View(this);
        btnRow.AddView(space, new LinearLayout.LayoutParams(0, 1, 1f)); // 中间留白
        btnRow.AddView(right);
        _controlBar.AddView(btnRow);

        RefreshLockButton();
        _controlBar.Visibility = ViewStates.Gone;
        _playerPage.AddView(_controlBar, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Bottom });
    }

    /// <summary>转屏重建控制条（vc116③ 横竖屏已同构三行，重建只为翻转钮图标跟方向换态）。</summary>
    private void RebuildControlBar()
    {
        if (_controlBar is null) return;
        bool shown = _controlBarShown;
        if (_controlBar.Parent is ViewGroup p) p.RemoveView(_controlBar);
        _controlBar = null;
        _lockBtn = null;
        _cbTitle = null;
        _cbMini = null;
        _cbNowName = null;
        _cbNowTime = null;
        _cbAuxWater = null;
        _cbAuxNext = null;
        BuildControlBar();
        if (shown) ShowControlBar();
    }

    /// <summary>控制条信息刷新（vc113 原型对照令）：台名/分辨率·帧率 mini/节目名·画面时刻/水位·接下来
    /// （换台与显条时同步；mini 无读数降级隐藏）。</summary>
    private void RefreshControlBarInfo()
    {
        if (_cbTitle is null || _cbNowName is null || _cbNowTime is null || _cbAuxWater is null || _cbAuxNext is null) return;
        var ch = _channels.Count > 0 && _currentIndex >= 0 && _currentIndex < _channels.Count
            ? _channels[_currentIndex] : null;
        long buffered = BufferedMsNow() ?? 0;
        // v31 原型口径（vc115 拆段保读数）：节目名与「画面时刻」分段——节目名超长省略，读数段完整；
        // 「实时水位：X秒」完整，「接下来」段超长省略；缺表降级只显画面时刻（v13 老板令）
        string clock = PictureClock(buffered);
        bool hasNow = _epgNowTitle is { } t && t.Length > 0;
        bool hasNext = _epgNextText is { } n && n.Length > 0;
        _cbTitle.Text = ch is null ? "TaoLive" : ch.Name;
        _cbNowName.Text = hasNow ? _epgNowTitle! : "";
        _cbNowName.Visibility = hasNow ? ViewStates.Visible : ViewStates.Gone;
        _cbNowTime.Text = hasNow ? $"· 画面时刻：{clock}" : $"画面时刻：{clock}";
        _cbAuxWater.Text = $"实时水位：{buffered / 1000}秒";
        _cbAuxNext.Text = hasNext ? $"· 接下来：{_epgNextText}" : "";
        _cbAuxNext.Visibility = hasNext ? ViewStates.Visible : ViewStates.Gone;
        RefreshControlBarMini();
    }

    /// <summary>分辨率 mini（老板令 2026-11-28「帧率可以不显示」：只显分辨率）：内核 OnVideoSizeChanged 实读，
    /// 无读数（未就绪/音频流）降级隐藏不占位。</summary>
    private void RefreshControlBarMini()
    {
        if (_cbMini is null) return;
        var kernel = _manager.Current?.Kernel;
        int? h = kernel?.VideoHeight;
        if (h is { } hh && hh > 0)
        {
            _cbMini.Text = $"{hh}";
            _cbMini.Visibility = ViewStates.Visible;
        }
        else
        {
            _cbMini.Visibility = ViewStates.Gone;
        }
    }

    /// <summary>换台（触屏滑动手势口径）：±1 环绕，走既有 PlayAt 链路（预检死台拦截、防抖全继承）。</summary>
    private void SwitchChannel(int dir)
    {
        if (_channels.Count == 0) return;
        int next = ((_currentIndex + dir) % _channels.Count + _channels.Count) % _channels.Count;
        PlayAt(next);
    }

    /// <summary>方向锁：锁=钉当前方向（横=SensorLandscape 竖=SensorPortrait），解锁=FullUser 随传感器；
    /// 写偏好跨启动记忆。vc116⑤：去提示弹条——锁态由图标三态自见，不再弹字。</summary>
    private void ToggleOrientationLock()
    {
        _orientationLocked = !_orientationLocked;
        var editor = _prefs.Edit();
        editor?.PutBoolean("orientation_lock", _orientationLocked);
        editor?.Apply();
        ApplyOrientationLock();
        RefreshLockButton();
    }

    private void ApplyOrientationLock()
    {
        if (_orientationLocked)
        {
            bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
            RequestedOrientation = landscape ? ScreenOrientation.SensorLandscape : ScreenOrientation.SensorPortrait;
        }
        else
        {
            RequestedOrientation = ScreenOrientation.FullUser;
        }
    }

    private void RefreshLockButton()
    {
        if (_lockBtn is { } b)
        {
            if (!_orientationLocked) b.Kind = LockIconView.IconKind.Unlocked;
            else
            {
                bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
                b.Kind = landscape ? LockIconView.IconKind.LockLandscape : LockIconView.IconKind.LockPortrait;
            }
        }
    }

    /// <summary>控制条显收：单击画面切换，3 秒自动收；抽屉/面板开着不出；空态横幅可见时不出（vc115 A1 互斥）。</summary>
    private void ToggleControlBar()
    {
        if (_listOpen || (_settingsPanelOpen)) return; // 面板优先：面板开着控制条不出
        if (_emptyState is not null && _emptyState.Visibility == ViewStates.Visible) return; // vc115 A1：失败横幅优先
        if (_controlBarShown) HideControlBar();
        else ShowControlBar();
    }

    private void ShowControlBar()
    {
        if (_controlBar is null) return;
        if (_emptyState is not null && _emptyState.Visibility == ViewStates.Visible) return; // vc115 A1：失败横幅优先
        RefreshControlBarInfo();
        _controlBar.Visibility = ViewStates.Visible;
        _controlBarShown = true;
        ScheduleControlBarHide();
    }

    private void HideControlBar()
    {
        if (_controlBar is null) return;
        _controlBar.Visibility = ViewStates.Gone;
        _controlBarShown = false;
        if (_controlBarHide is { } h) _main.RemoveCallbacks(h);
    }

    private void ScheduleControlBarHide()
    {
        if (_controlBarHide is { } old) _main.RemoveCallbacks(old);
        _controlBarHide = new Java.Lang.Runnable(() =>
        {
            if (_controlBarShown && !_listOpen && !_settingsPanelOpen) HideControlBar();
        });
        _main.PostDelayed(_controlBarHide, 3000);
    }

    /// <summary>触屏手势监听（2026-11-27 整改令 + vc110 老板令）：单击=控制条；左右滑=切台（左=上一台、右=下一台，
    /// 老板令「左上右下」；慢拖 OnScroll 累计触发，不只认快滑）；上下滑按半区=亮度（左）/音量（右），
    /// 行程按整屏高（vc110 老板令「行程=整屏高」）；边缘手势（vc110 老板令）：左边缘右滑=呼出列表页（横竖屏一致）、
    /// 右边缘左滑=横屏退全屏/竖屏出退出确认；长按=收藏/取消当前台。滑动阈值 96dp。</summary>
    private sealed class PhoneGestureListener(MainActivity host) : GestureDetector.SimpleOnGestureListener
    {
        private const float SwipeMinDp = 96f;  // 横向切台门槛（只管切台）
        private const float VertEntryDp = 8f;  // 纵向入模门槛（v28 定稿①②：去 96dp 死区，微动即变、滑 1 得 1）
        private const float EdgeDp = 28f; // 边缘判定带宽：起点落在左右边缘此带宽内走边缘手势，不切台

        private float _startX, _startY, _lastY;
        private bool _fired;        // 本次手势已触发切台（一次滑动只切一台）
        private int _vMode;         // 0=未定 1=亮度 2=音量
        private int _volStart;      // vc117 令10：手势入模时的起点音量档（绝对映射锚点）
        private int _volLast = -1;  // vc117 令10：上一拍已设档（同档不重复设值/亮条）

        private float Dp(float px) => px / host.Resources!.DisplayMetrics!.Density;

        public override bool OnDown(MotionEvent? e)
        {
            if (e is not null)
            {
                _startX = e.GetX();
                _startY = e.GetY();
                _lastY = e.GetY();
            }
            _fired = false;
            _vMode = 0;
            _volLast = -1; // vc117 令10：新手势重置上一拍档位
            return true; // 接收后续手势事件
        }

        public override bool OnSingleTapConfirmed(MotionEvent? e)
        {
            host.ToggleControlBar();
            return true;
        }

        public override void OnLongPress(MotionEvent? e)
        {
            host.RunOnUiThread(() =>
            {
                host.ToggleFavoriteOnCurrent();
                host.ScheduleControlBarHide(); // 长按收藏后控制条节奏重计
            });
        }

        /// <summary>横向滑动分流：边缘手势优先（vc110），其余=切台。命中返回 true 已消费。</summary>
        private bool HandleHorizontal(float dx, float dy)
        {
            if (!(Dp(Math.Abs(dx)) > SwipeMinDp && Math.Abs(dx) > Math.Abs(dy) * 1.2f)) return false;
            float w = host.Resources!.DisplayMetrics!.WidthPixels;
            bool fromLeft = _startX <= Dp(EdgeDp) && dx > 0;   // 左边缘右滑
            bool fromRight = _startX >= w - Dp(EdgeDp) && dx < 0; // 右边缘左滑
            if (!fromLeft && !fromRight) return false;
            _fired = true;
            host.RunOnUiThread(() =>
            {
                if (fromLeft) { host.HideControlBar(); host.ShowList(); }
                else if (host.Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape)
                {
                    host.RequestedOrientation = ScreenOrientation.SensorPortrait; // 横屏：退全屏回竖屏（vc116⑤ 去提示弹条）
                }
                else host.ExitOrCollapseList(); // vc112：退出确认弹窗已整删，竖屏滑回=与返回键同口径（收抽屉/退后台续播）
            });
            return true;
        }

        public override bool OnScroll(MotionEvent? e1, MotionEvent? e2, float distanceX, float distanceY)
        {
            if (e1 is null || e2 is null || _fired) return true;
            float dx = e2.GetX() - _startX;
            float dy = e2.GetY() - _startY;

            if (_vMode == 0)
            {
                if (HandleHorizontal(dx, dy)) return true;
                float adx = Math.Abs(dx), ady = Math.Abs(dy);
                if (adx > Dp(SwipeMinDp) && adx > ady * 1.2f)
                {
                    _fired = true;
                    host.RunOnUiThread(() => host.SwitchChannel(dx < 0 ? -1 : 1)); // 左=上一台，右=下一台
                    return true;
                }
                if (ady > Dp(VertEntryDp) && ady > adx * 1.2f)
                {
                    bool leftHalf = _startX < host.Resources!.DisplayMetrics!.WidthPixels / 2f;
                    _vMode = leftHalf ? 1 : 2; // 左半区亮度、右半区音量
                    if (_vMode == 1) host.RunOnUiThread(() => host._brightness = host.ReadCurrentBrightness()); // vc117：入模即同步当前实际亮度，起手不跳变
                    if (_vMode == 2 && host._audio is { } a)
                        _volStart = a.GetStreamVolume(Android.Media.Stream.Music); // vc117 令10：入模记起点音量，绝对映射不跳变
                    _lastY = _startY; // v28 定稿②：入模即从起点全额结算，起手行程不吞（滑 1 得 1）
                }
            }

            if (_vMode == 1)
            {
                float delta = (_lastY - e2.GetY()) / host.Resources!.DisplayMetrics!.HeightPixels; // 行程=整屏高
                host.RunOnUiThread(() => host.SetBrightness(host._brightness + delta)); // 指示条即亮实时跟手（SetBrightness 内）
            }
            else if (_vMode == 2)
            {
                if (host._audio is { } audio)
                {
                    // vc117 令10（2026-11-28 老板拍板）：音量改跟手绝对映射——手指位置直接定音量档
                    // （起点档 + 行程比例×满档），对齐系统音量滑动手感，不跳变；累计步进口径退场。
                    int max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
                    float travel = (_startY - e2.GetY()) / host.Resources!.DisplayMetrics!.HeightPixels;
                    int idx = Math.Clamp((int)Math.Round(_volStart + travel * max), 0, max);
                    if (idx != _volLast)
                    {
                        _volLast = idx;
                        // vc117 令9：音量与亮度同一套自绘竖条 HUD（同位置同样式，图标区分太阳/喇叭），
                        // 系统音量条退场（ShowUi 旗改 0 静默设值）
                        audio.SetStreamVolume(Android.Media.Stream.Music, idx, (VolumeNotificationFlags)0);
                        host.RunOnUiThread(() => host.ShowHudBar(HudIconView.IconKind.Speaker, max <= 0 ? 0 : (float)idx / max));
                    }
                }
            }
            _lastY = e2.GetY();
            return true;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent? e2, float velocityX, float velocityY)
        {
            if (e1 is null || e2 is null || _fired) return true;
            float dx = e2.GetX() - _startX;
            float dy = e2.GetY() - _startY;
            if (HandleHorizontal(dx, dy)) return true;
            if (Dp(Math.Abs(dx)) < SwipeMinDp || Math.Abs(dx) < Math.Abs(dy)) return true;
            _fired = true;
            host.RunOnUiThread(() => host.SwitchChannel(dx < 0 ? -1 : 1)); // 左=上一台，右=下一台
            return true;
        }
    }

    /// <summary>悬浮指示图标：单色自绘（喇叭/太阳），对齐 v22 去 emoji 口径（2026-11-27 老板令「样式也还是要有」）。</summary>
    private sealed class HudIconView : View
    {
        internal enum IconKind { Speaker, Sun }
        private IconKind _kind = IconKind.Speaker;
        private readonly Paint _fill = new(PaintFlags.AntiAlias) { Color = Color.White };
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { Color = Color.White, StrokeCap = Paint.Cap.Round };

        public HudIconView(Context context) : base(context)
        {
            _fill.SetStyle(Paint.Style.Fill);
            _stroke.SetStyle(Paint.Style.Stroke);
        }

        internal void SetKind(IconKind kind)
        {
            if (_kind == kind) return;
            _kind = kind;
            Invalidate();
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            var color = InkTranslucency.TextPrimary(Context);
            _fill.Color = color;
            _stroke.Color = color;
            _stroke.StrokeWidth = Math.Max(Width, Height) * 0.09f;
            float w = Width, h = Height;
            if (_kind == IconKind.Speaker)
            {
                // 喇叭：小矩形音仓 + 外扩梯形号筒 + 一道声波弧
                float bw = w * 0.26f, bh = h * 0.36f, cx = w * 0.30f, cy = h * 0.5f;
                canvas.DrawRect(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2, _fill);
                var horn = new Android.Graphics.Path();
                horn.MoveTo(cx + bw / 2, cy - bh / 2);
                horn.LineTo(w * 0.66f, h * 0.10f);
                horn.LineTo(w * 0.66f, h * 0.90f);
                horn.LineTo(cx + bw / 2, cy + bh / 2);
                horn.Close();
                canvas.DrawPath(horn, _fill);
                canvas.DrawArc(w * 0.60f, h * 0.26f, w * 1.02f, h * 0.74f, -48f, 96f, false, _stroke);
            }
            else
            {
                // 太阳：圆心 + 八向光线
                canvas.DrawCircle(w / 2f, h / 2f, h * 0.22f, _fill);
                for (int i = 0; i < 8; i++)
                {
                    canvas.Rotate(45f, w / 2f, h / 2f);
                    canvas.DrawLine(w / 2f, h * 0.04f, w / 2f, h * 0.16f, _stroke);
                }
            }
        }
    }

    /// <summary>控制条按钮统一线框皮（vc116② 界面元素统一皮）：全透明填充+圆角方形描边——
    /// 默认描边 45% 白、按压 95% 白，圆角 10dp；旧半透明胶囊实底退场。</summary>
    private static Android.Graphics.Drawables.Drawable BarButtonBackground()
    {
        Android.Graphics.Drawables.GradientDrawable NewBg(int strokeAlpha)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
            d.SetCornerRadius(UiScale.Px(10)); // 圆角方形线框
            d.SetColor(Color.Transparent); // 中间全透明
            d.SetStroke(1, Color.Argb(strokeAlpha, 255, 255, 255));
            return d;
        }
        var ss = new Android.Graphics.Drawables.StateListDrawable();
        ss.AddState(new int[] { Android.Resource.Attribute.StatePressed }, NewBg(242)); // 按压：95% 白强描边
        ss.AddState(new int[] { }, NewBg(115)); // 默认：45% 白描边
        return ss;
    }

    /// <summary>控制条自绘图标（vc113 老板令三单②：六枚全改自绘）：◀▶☰⚙ 四枚由字符改几何绘制，
    /// 与全屏/锁图标同风格（单色描边、IconGlyph 字号、44dp 热区、几何居中），根除字体度量与几何中心两套混排的错位。</summary>
    private sealed class BarIconView : View
    {
        internal enum IconKind { Prev, Next, List, Gear }
        private IconKind _kind = IconKind.List;
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };

        public BarIconView(Context context) : base(context) => _stroke.SetStyle(Paint.Style.Stroke);

        internal IconKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        // vc117 令16（2026-11-28 老板拍板）：线框盒统一 36dp 稍小（原 44dp），图标占盒 58% 稍大、几何正中间。
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int side = UiScale.Px(36);
            SetMeasuredDimension(side, side);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            float side = Math.Min(Width, Height) * 0.58f; // 令16：图标占盒 58%，几何正中间
            canvas.Save();
            canvas.Translate((Width - side) / 2f, (Height - side) / 2f);
            DrawIcon(canvas, side);
            canvas.Restore();
        }

        private void DrawIcon(Canvas canvas, float side)
        {
            var context = Context!;
            _stroke.Color = InkTranslucency.TextPrimary(context);
            _stroke.StrokeWidth = side * 0.085f;
            float w = side, h = side;
            switch (_kind)
            {
                case IconKind.Prev: // ⏮：竖线+左三角（上一台）
                {
                    float barX = w * 0.30f;
                    canvas.DrawLine(barX, h * 0.22f, barX, h * 0.78f, _stroke);
                    var tri = new float[] { w * 0.72f, h * 0.26f, w * 0.40f, h * 0.5f, w * 0.72f, h * 0.74f };
                    var fill = new Paint(PaintFlags.AntiAlias) { Color = _stroke.Color };
                    canvas.DrawPath(ToPath(tri), fill);
                    break;
                }
                case IconKind.Next: // ⏭：右三角+竖线（下一台）
                {
                    float barX = w * 0.70f;
                    canvas.DrawLine(barX, h * 0.22f, barX, h * 0.78f, _stroke);
                    var tri = new float[] { w * 0.28f, h * 0.26f, w * 0.60f, h * 0.5f, w * 0.28f, h * 0.74f };
                    var fill = new Paint(PaintFlags.AntiAlias) { Color = _stroke.Color };
                    canvas.DrawPath(ToPath(tri), fill);
                    break;
                }
                case IconKind.List: // ☰：三横线
                {
                    float left = w * 0.20f, right = w * 0.80f;
                    canvas.DrawLine(left, h * 0.28f, right, h * 0.28f, _stroke);
                    canvas.DrawLine(left, h * 0.50f, right, h * 0.50f, _stroke);
                    canvas.DrawLine(left, h * 0.72f, right, h * 0.72f, _stroke);
                    break;
                }
                case IconKind.Gear: // ⚙：外圈齿+中孔（简化：圆环+八向短齿+中心圆）
                {
                    float cx = w / 2f, cy = h / 2f, r = w * 0.30f;
                    canvas.DrawCircle(cx, cy, r, _stroke);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        float x1 = cx + (float)(Math.Cos(a) * r);
                        float y1 = cy + (float)(Math.Sin(a) * r);
                        float x2 = cx + (float)(Math.Cos(a) * r * 1.32f);
                        float y2 = cy + (float)(Math.Sin(a) * r * 1.32f);
                        canvas.DrawLine(x1, y1, x2, y2, _stroke);
                    }
                    _stroke.StrokeWidth = side * 0.07f;
                    canvas.DrawCircle(cx, cy, r * 0.38f, _stroke);
                    break;
                }
            }
        }

        private static Android.Graphics.Path ToPath(float[] pts)
        {
            var p = new Android.Graphics.Path();
            p.MoveTo(pts[0], pts[1]);
            for (int i = 2; i < pts.Length; i += 2) p.LineTo(pts[i], pts[i + 1]);
            return p;
        }
    }

    private sealed class FullscreenIconView : View
    {
        internal enum IconKind { EnterFullscreen, ExitFullscreen }
        private IconKind _kind = IconKind.EnterFullscreen;
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };

        public FullscreenIconView(Context context) : base(context) => _stroke.SetStyle(Paint.Style.Stroke);

        internal IconKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        // vc111 真机修（老板截图第1条）：自绘视图不量尺寸会被系统撑满整行剩余宽度——
        // 图标拉成巨大白块、锁定钮挤出屏外。量死见方（与文字钮同热区），OnDraw 按方图绘制自然居中。
        // vc117 令16：线框盒统一 36dp 稍小（原 44dp），图标占盒 58% 稍大、几何正中间。
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int side = UiScale.Px(36);
            SetMeasuredDimension(side, side);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            // 令16：图标占盒 58%（原 IconGlyph 字号≈48%），居中平移后按 side 见方绘制——几何正中间
            float side = Math.Min(Width, Height) * 0.58f;
            canvas.Save();
            canvas.Translate((Width - side) / 2f, (Height - side) / 2f);
            DrawIcon(canvas, side);
            canvas.Restore();
        }

        private void DrawIcon(Canvas canvas, float side)
        {
            var context = Context!;
            _stroke.Color = InkTranslucency.TextPrimary(context);
            _stroke.StrokeWidth = side * 0.08f;
            float w = side, h = side;
            float inset = w * 0.14f, len = w * 0.22f;
            // 四角括号
            canvas.DrawLines(new float[]
            {
                inset, inset + len, inset, inset,
                inset, inset, inset + len, inset,
                w - inset - len, inset, w - inset, inset,
                w - inset, inset, w - inset, inset + len,
                w - inset, h - inset - len, w - inset, h - inset,
                w - inset, h - inset, w - inset - len, h - inset,
                inset + len, h - inset, inset, h - inset,
                inset, h - inset, inset, h - inset - len,
            }, _stroke);
            // 中央箭头：进全屏朝右上外扩、返回竖屏朝左下内收
            float cx = w / 2f, cy = h / 2f, a = w * 0.15f;
            if (_kind == IconKind.EnterFullscreen)
            {
                canvas.DrawLines(new float[]
                {
                    cx - a, cy + a, cx + a, cy - a,
                    cx + a, cy - a, cx + a * 0.4f, cy - a,
                    cx + a, cy - a, cx + a, cy - a * 0.4f,
                }, _stroke);
            }
            else
            {
                canvas.DrawLines(new float[]
                {
                    cx + a, cy - a, cx - a, cy + a,
                    cx - a, cy + a, cx - a * 0.4f, cy + a,
                    cx - a, cy + a, cx - a, cy + a * 0.4f,
                }, _stroke);
            }
        }
    }

    /// <summary>锁定按钮图标（vc110 老板令）：解锁=开锁（锁梁右腿翘起）、锁定横屏=横屏锁（锁+横条小屏）、
    /// 锁定竖屏=竖屏锁（锁+竖条小屏），单色自绘，与 ☰ ⚙ 同风格同热区。</summary>
    private sealed class LockIconView : View
    {
        internal enum IconKind { Unlocked, LockLandscape, LockPortrait }
        private IconKind _kind = IconKind.Unlocked;
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round }; // vc112 锁体改线条风，填充画笔退场

        public LockIconView(Context context) : base(context) => _stroke.SetStyle(Paint.Style.Stroke);

        internal IconKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        // vc111 真机修（老板截图第1条）：同 FullscreenIconView——量死见方，防默认测量撑满整行。
        // vc117 令16：线框盒统一 36dp 稍小（原 44dp），图标占盒 58% 稍大、几何正中间。
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int side = UiScale.Px(36);
            SetMeasuredDimension(side, side);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            // 令16：图标占盒 58%（原 IconGlyph 字号≈48%），居中平移后按 side 见方绘制；锁体线条风不变
            float side = Math.Min(Width, Height) * 0.58f;
            canvas.Save();
            canvas.Translate((Width - side) / 2f, (Height - side) / 2f);
            DrawIcon(canvas, side);
            canvas.Restore();
        }

        private void DrawIcon(Canvas canvas, float side)
        {
            var context = Context!;
            var color = InkTranslucency.TextPrimary(context);
            _stroke.Color = color;
            _stroke.StrokeWidth = side * 0.09f;
            float w = side, h = side;
            // 锁体：圆角矩形描边（vc116⑥ 放大到约 56%×42% side 治偏小；线条风不变）
            float bw = w * 0.56f, bh = h * 0.42f;
            float bx = w * 0.42f - bw / 2, by = h * 0.50f;
            canvas.DrawRoundRect(bx, by, bx + bw, by + bh, bw * 0.2f, bw * 0.2f, _stroke);
            // 锁梁：拱形。vc116⑥ 解锁态=锁梁整体上提 lift=r*0.5——左腿长扣到锁体上沿、右腿短悬空不进体，
            // 根治旧画法右腿上翘线与拱弧叠成「0」形散块；锁定态双腿扣到体上
            float r = bw * 0.30f;
            float scx = bx + bw / 2, scy = by;
            if (_kind == IconKind.Unlocked)
            {
                float lift = r * 0.5f; // 锁梁整体上提
                float ay = scy - lift; // 抬升后的梁基线（拱弧水平径所在高度）
                canvas.DrawArc(scx - r, ay - r * 1.7f, scx + r, ay + r * 0.3f, 180f, 180f, false, _stroke);
                canvas.DrawLine(scx - r, ay - r * 0.7f, scx - r, scy, _stroke); // 左腿长：扣到锁体上沿
                canvas.DrawLine(scx + r, ay - r * 0.7f, scx + r, ay - r * 0.2f, _stroke); // 右腿短：悬空不进体=开
            }
            else
            {
                canvas.DrawArc(scx - r, scy - r * 1.7f, scx + r, scy + r * 0.3f, 180f, 180f, false, _stroke);
                canvas.DrawLine(scx - r, scy - r * 0.7f, scx - r, scy, _stroke);
                canvas.DrawLine(scx + r, scy - r * 0.7f, scx + r, scy, _stroke);
            }
            // 方向指示：锁定时锁体右侧的小屏（横=宽扁、竖=窄高）——vc112 同改描边线条风
            if (_kind == IconKind.LockLandscape)
                canvas.DrawRoundRect(w * 0.76f, h * 0.44f, w * 0.94f, h * 0.62f, 2f, 2f, _stroke);
            else if (_kind == IconKind.LockPortrait)
                canvas.DrawRoundRect(w * 0.80f, h * 0.36f, w * 0.94f, h * 0.64f, 2f, 2f, _stroke);
        }
    }
}
