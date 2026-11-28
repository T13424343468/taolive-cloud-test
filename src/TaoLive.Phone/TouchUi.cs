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
    private TextView? _cbNowName;  // 控制条行2：节目单整块（当前+接下来合并，vc131 老板令）
    private TextView? _cbNowTime;  // 控制条行2：「· 画面时刻：HH:mm:ss」（完整不截，vc115）
    private TextView? _cbAuxWater; // 控制条行3：「实时水位：X秒」（完整不截，vc115）
    private LinearLayout? _cbBtnLeft;  // vc124：按钮行左组（圆角让位亮条时重算）
    private LinearLayout? _cbBtnRight; // vc124：按钮行右组（同上）
    private bool _controlBarShown;
    private Java.Lang.Runnable? _controlBarHide;
    private GestureDetector? _gesture;
    private bool _orientationLocked; // 方向锁（持久化）：锁=钉当前方向，解锁=随传感器
    // vc112 老板令「图标统一+调大」：六枚图标（◀ ▶ ☰ ⚙ 全屏 锁）统一字号——T(1.25)≈21sp（原 Item≈19sp），
    // 自绘图标（全屏/锁）OnDraw 按 ScaledDensity 换算同尺寸绘制，热区 44dp 不变。
    private static float IconGlyph => UiScale.T(1.25f);

    private FrameLayout? _hudBright;           // 亮度指示条（vc121 老板令：竖横屏永远亮左、音右，各在黑区）
    private FrameLayout? _hudVol;              // 音量指示条
    private HudIconView? _hudBrightIcon;
    private HudIconView? _hudVolIcon;
    private View? _hudBrightFill;              // 竖条填充（高度=音量/亮度比例）
    private View? _hudVolFill;
    private FrameLayout? _hudBrightTrack;
    private FrameLayout? _hudVolTrack;
    private Java.Lang.Runnable? _hudBrightHide;
    private Java.Lang.Runnable? _hudVolHide;
    private const int HudTrackDp = 160;        // 竖条轨道高（vc121 老板令「整体元素太小」：120→160）
    private const int HudIconDp = 24;          // 指示图标边长（vc121：16→24）
    private const int HudTrackWidthDp = 10;    // 竖条轨道宽（vc121：5→10，可拖不硌手）
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
        _hudBright = BuildHudPanel(HudIconView.IconKind.Sun);
        _hudVol = BuildHudPanel(HudIconView.IconKind.Speaker);
        PositionHud();
        _playerPage.AddView(_hudBright); // vc116①：不传新 lp——PositionHud 已落 LayoutParameters，再挂带 Gravity 的 lp 会整体覆盖落位
        _playerPage.AddView(_hudVol);
    }

    /// <summary>构建单根指示条面板（图标在上、竖条在下）；条面板整块可拖直接调值（vc121 老板令「划不动」整改）。</summary>
    private FrameLayout BuildHudPanel(HudIconView.IconKind kind)
    {
        var panel = new FrameLayout(this);
        // vc124 老板令（2026-11-28 真机「黑底还加方块底干啥」）：面板底色退场——播放页本就是黑底，
        // 方块底是画蛇添足；只留图标+竖条悬浮，触摸消费不变
        panel.Clickable = true; // 消费触摸，不透给切台手势

        // 竖排：图标在上、竖条在下（普通播放器通行样式，无文字）
        var col = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        col.SetGravity(GravityFlags.Center);
        col.SetPadding(UiScale.Px(28), UiScale.Px(20), UiScale.Px(28), UiScale.Px(20));

        var icon = new HudIconView(this);
        icon.SetKind(kind);
        col.AddView(icon, new LinearLayout.LayoutParams(UiScale.Px(HudIconDp), UiScale.Px(HudIconDp)) { Gravity = GravityFlags.CenterHorizontal });

        var gap = new View(this);
        col.AddView(gap, new LinearLayout.LayoutParams(1, UiScale.Px(12)));

        var track = new FrameLayout(this);
        var trackBg = new GradientDrawable();
        // vc124 老板令（2026-11-28 真机「亮度条在哪儿」）：轨道 70 透明度在黑底上几乎隐形——
        // 提到 150，黑底上清晰见轨；填充仍用亮白，比例一眼可读
        int trackArgb = new Color(Color.ParseColor(InkTranslucency.TextSecondary)).ToArgb();
        trackBg.SetColor(Color.Argb(150, (trackArgb >> 16) & 0xFF, (trackArgb >> 8) & 0xFF, trackArgb & 0xFF));
        trackBg.SetCornerRadius(UiScale.Px(5));
        track.Background = trackBg;
        track.ClipToOutline = true; // vc121：裁掉 fill 溢出轨道圆角弧线的 1-2px

        var fillBg = new GradientDrawable();
        fillBg.SetColor(InkTranslucency.TextPrimary(this));
        fillBg.SetCornerRadius(UiScale.Px(5));
        var fill = new View(this) { Background = fillBg };
        track.AddView(fill, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiScale.Px(3)) { Gravity = GravityFlags.Bottom });

        col.AddView(track, new LinearLayout.LayoutParams(UiScale.Px(HudTrackWidthDp), UiScale.Px(HudTrackDp)));
        panel.AddView(col);

        // vc121 老板令「亮度划不动」：手指按住条面板上下拖，纵向位置直接定值（绝对映射），不再只靠暗手势
        int trackH = UiScale.Px(HudTrackDp);
        track.Touch += (_, e) =>
        {
            if (e.Event is null) return;
            var act = e.Event.ActionMasked;
            if (act == MotionEventActions.Down || act == MotionEventActions.Move)
            {
                float f = Math.Clamp(1f - e.Event.GetY() / trackH, 0f, 1f);
                ApplyHudValue(kind, f);
                e.Handled = true;
            }
            else if (act == MotionEventActions.Up || act == MotionEventActions.Cancel)
            {
                e.Handled = true;
            }
        };

        if (kind == HudIconView.IconKind.Sun) { _hudBrightIcon = icon; _hudBrightFill = fill; _hudBrightTrack = track; }
        else { _hudVolIcon = icon; _hudVolFill = fill; _hudVolTrack = track; }
        panel.Visibility = ViewStates.Gone;
        return panel;
    }

    /// <summary>拖条设值：亮度走窗口亮度，音量走媒体流；各自亮条跟手。</summary>
    private void ApplyHudValue(HudIconView.IconKind kind, float f)
    {
        if (kind == HudIconView.IconKind.Sun) SetBrightness(f);
        else if (_audio is { } audio)
        {
            int max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
            int idx = Math.Clamp((int)Math.Round(f * max), 0, max);
            audio.SetStreamVolume(Android.Media.Stream.Music, idx, (VolumeNotificationFlags)0);
            ShowHudBar(kind, max <= 0 ? 0 : (float)idx / max);
        }
    }

    /// <summary>悬浮指示条落位（vc129 老板令定稿：竖屏亮左/音右在上半区黑区；横屏两条都垂直居中、
    /// 各贴画面左右缘外侧留 8dp，对称落位）。转屏重算。</summary>
    internal void PositionHud()
    {
        if (_playerPage is null) return;
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        PositionHudBar(_hudBright, landscape, left: true);
        PositionHudBar(_hudVol, landscape, left: false);
    }

    private void PositionHudBar(FrameLayout? bar, bool landscape, bool left)
    {
        if (bar is null) return;
        var lp = bar.LayoutParameters as FrameLayout.LayoutParams;
        if (lp is null)
        {
            lp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            bar.LayoutParameters = lp;
        }
        int barW = bar.Width, barH = bar.Height;
        if (landscape)
        {
            // vc129 老板令（2026-11-28「上下都居中 左右贴画面缘往外留边距」拍板）：横屏两条对称落位——
            // 都垂直居中；亮度条贴画面左缘外侧、音量条贴画面右缘外侧，各留 8dp。不再按黑区居中、
            // 不再避孔（vc128 顶对齐避孔口径退场；真实孔径若切条真机见再调）。
            int margin = UiScale.Px(8);
            int effW = barW > 0 ? barW : UiScale.Px(28 + HudTrackWidthDp + 28); // 面板宽=左右padding+轨道宽
            int videoLeft = _videoView is { } vv && vv.Width > 0 ? vv.Left : 0;
            int screenW = Resources!.DisplayMetrics!.WidthPixels;
            int videoRight = _videoView is { } vv2 && vv2.Width > 0 ? vv2.Right : screenW;
            lp.TopMargin = 0; lp.LeftMargin = 0; lp.RightMargin = 0;
            if (left)
            {
                // 条右缘=画面左缘−8dp；画面未布局兜底贴屏左 8dp
                lp.Gravity = GravityFlags.CenterVertical | GravityFlags.Left;
                lp.LeftMargin = Math.Max(videoLeft - margin - effW, UiScale.Px(8));
            }
            else
            {
                // 条左缘=画面右缘+8dp；黑区窄于条兜底贴屏右 8dp
                lp.Gravity = GravityFlags.CenterVertical | GravityFlags.Right;
                int rightGap = screenW - videoRight;
                lp.RightMargin = rightGap > margin + effW ? rightGap - margin - effW : UiScale.Px(8);
            }
        }
        else
        {
            // 竖屏：条落上半区黑区垂直居中——黑区=屏顶到画面上沿；黑区太窄兜底 SafeTop+8dp
            lp.Gravity = GravityFlags.Top | (left ? GravityFlags.Left : GravityFlags.Right);
            if (left) { lp.LeftMargin = UiScale.Px(12); lp.RightMargin = 0; }
            else { lp.RightMargin = UiScale.Px(12); lp.LeftMargin = 0; }
            int videoTop = _videoView is { } vv3 && vv3.Height > 0 ? vv3.Top : 0;
            int safeTop = SafeTopInset(0);
            if (videoTop > 0)
            {
                int margin = (videoTop - barH) / 2;
                lp.TopMargin = Math.Max(margin, safeTop + UiScale.Px(8));
            }
            else
            {
                lp.TopMargin = safeTop + UiScale.Px(8); // 画面未布局兜底，LayoutChange 后重算
            }
        }
    }

    /// <summary>竖条悬浮指示：kind 选条（亮左/音右），fraction 0–1 定填充高；700ms 无操作自动收。</summary>
    private void ShowHudBar(HudIconView.IconKind kind, float fraction)
    {
        bool isSun = kind == HudIconView.IconKind.Sun;
        var panel = isSun ? _hudBright : _hudVol;
        var icon = isSun ? _hudBrightIcon : _hudVolIcon;
        var fill = isSun ? _hudBrightFill : _hudVolFill;
        if (panel is null || icon is null || fill is null) return;
        // vc132 老板令（14:37 总口径）：亮度/音量条与提示胶囊互斥——指示条亮相即收胶囊，不同屏
        if (_toast is { } t && t.Visibility == ViewStates.Visible)
        {
            t.Visibility = ViewStates.Gone;
            _main.RemoveCallbacks(_toastHide);
        }
        icon.SetKind(kind);
        float f = Math.Clamp(fraction, 0f, 1f);
        int fillH = Math.Max((int)(UiScale.Px(HudTrackDp) * f), UiScale.Px(3)); // 0 也留可见头
        if (fill.LayoutParameters is FrameLayout.LayoutParams lp)
        {
            // vc128 老板令（真机「音量声音有变化 自绘元素没变化」实锤）：原地改 lp.Height 不触发
            // 布局，填充高度不动——改完必须 RequestLayout 才上屏。
            lp.Height = fillH;
            fill.RequestLayout();
        }
        else fill.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, fillH) { Gravity = GravityFlags.Bottom };
        panel.Visibility = ViewStates.Visible;
        var oldHide = isSun ? _hudBrightHide : _hudVolHide;
        if (oldHide is { }) _main.RemoveCallbacks(oldHide);
        var newHide = new Java.Lang.Runnable(() => panel.Visibility = ViewStates.Gone);
        if (isSun) _hudBrightHide = newHide; else _hudVolHide = newHide;
        _main.PostDelayed(newHide, 700);
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
        // vc128 老板令（真机「亮度调节现在没生效」实锤）：只改 Attributes 字段不回写，窗口不重排
        // 亮度就不落——改完必须 Window.Attributes = attr 触发 WindowManager 重排才生效。
        try
        {
            if (Window?.Attributes is { } attr)
            {
                attr.ScreenBrightness = _brightness;
                Window.Attributes = attr;
            }
        }
        catch { }
        ShowHudBar(HudIconView.IconKind.Sun, _brightness); // 亮度 0.05–1 直接作比例
    }

    // —— 控制条（原型 v19–v26：三行结构/两端分组/单色图标 + 音量按钮） ———

    private void BuildControlBar()
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        _controlBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        // vc119 老板令（2026-11-28「避开圆角不是整体上移，要丝滑过渡」）：控制条贴回屏幕底，
        // 底边距回归与左右一致的正常口径；圆角过渡改由背景自身长出底部圆角（半径=系统圆角），
        // 弧线贴合屏幕角落，不再整条硬抬一大截留空白带。
        // vc127 老板令（2026-11-28 真机截图「文字太贴边了 能不能有点间距」）：内距改物理 dp 直读——
        // 原 Px(16) 出来是裸像素 ≈6dp 物理宽，文字几乎贴屏幕边；16dp 物理宽才有呼吸感。
        // vc132 老板令（2026-11-28 14:25 拍板A）：按钮上下等距——底部内距 16dp 收 0，
        // 上（文字→按钮盒=热区缓冲 10dp）下（按钮盒→屏缘=热区缓冲 10dp+0）对齐；横竖屏同口径。
        // vc133 老板令（15:02 拍板「左右9 底6」）：左右内距 16→9dp 按钮再靠边，底部内距 0→6dp 按钮整体上抬。
        _controlBar.SetPadding(UiScale.Dp(9), UiScale.Dp(10), UiScale.Dp(9), UiScale.Dp(6));
        _controlBar.Background = InkTranslucency.PanelBgWithBottomCorner(this, BottomCornerRadiusPx());

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

        // 行1：频道名（占余宽、超长省略）+ 右侧读数区——vc128 老板令（2026-11-28 真机截图定稿）：
        // 「第一行左侧放频道名 右侧画面时刻 实时水位 显示完整」——读数段全部挂行1右侧，完整不截断
        // vc131 老板令（「操作栏文字统一小字号」）：行1 全部回 Caption 小字号，Item 大字混用退场
        var infoRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        infoRow.SetGravity(GravityFlags.CenterVertical);
        _cbTitle = NewBarText(UiScale.Caption, InkTranslucency.TextPrimary(this), bold: true);
        infoRow.AddView(_cbTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _cbNowTime = NewBarText(UiScale.Caption, InkTranslucency.TextPrimary(this), bold: true);
        _cbNowTime.SetPadding(UiScale.Dp(8), 0, 0, 0);
        infoRow.AddView(_cbNowTime);
        // vc132 老板令（14:39 样式对调）：实时水位改主色加粗（=画面时刻样式）
        _cbAuxWater = NewBarText(UiScale.Caption, InkTranslucency.TextPrimary(this), bold: true);
        _cbAuxWater.SetPadding(UiScale.Dp(8), 0, 0, 0);
        infoRow.AddView(_cbAuxWater);
        _controlBar.AddView(infoRow);

        // 行2（vc131 老板令）：第二行起的中间整块区域都放节目单信息——当前+接下来合并为单块整行，
        // 统一小字号（Caption）、左对齐自然换行不设上限；原「节目名 Item 大字 + 接下来 Caption 小字」
        // 两段混排、视觉居中吊挂的口径退场（真机「第8集」「14:57 生命树第9集」居中吊着实锤）。
        var nowRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        // vc132 老板令（14:39 样式对调）：节目单改次要色不加粗（=水位旧样式）
        _cbNowName = NewBarText(UiScale.Caption, Color.ParseColor(InkTranslucency.TextSecondary));
        _cbNowName.SetSingleLine(false); // 覆盖 NewBarText 单行口径：节目单整块完整可换行
        _cbNowName.Ellipsize = null!;
        _cbNowName.Gravity = GravityFlags.Top | GravityFlags.Left; // 左对齐换行
        nowRow.AddView(_cbNowName, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _controlBar.AddView(nowRow);

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
            // vc120 肉眼对齐令：可见盒=文字渲染高，视图含透明外扩热区——背景 InsetDrawable 内缩同口径，线框画在可见盒上
            b.Background = new Android.Graphics.Drawables.InsetDrawable(BarButtonBackground(), UiScale.Px(UiScale.BtnTouchPadDp));
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
        flipBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { LeftMargin = UiScale.Px(8) }; // vc120 肉眼对齐令：盒径自量（跟台名渲染高），定尺寸盒退场
        flipBtn.Background = new Android.Graphics.Drawables.InsetDrawable(BarButtonBackground(), UiScale.Px(UiScale.BtnTouchPadDp)); // vc120：背景内缩对齐可见盒
        flipBtn.Click += (_, _) =>
        {
            bool land = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
            RequestedOrientation = land ? ScreenOrientation.SensorPortrait : ScreenOrientation.SensorLandscape;
            // vc116⑤：去提示弹条——转屏结果画面自见，不再弹字
        };
        right.AddView(flipBtn);

        // 锁定按钮（vc110 老板令：文字「锁/已锁」换图标——解锁=开锁、锁定横屏=横屏锁、锁定竖屏=竖屏锁，自绘单色同风格）
        var lockBtn = new LockIconView(this) { Clickable = true };
        lockBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { LeftMargin = UiScale.Px(8) }; // vc120 肉眼对齐令：盒径自量（跟台名渲染高），定尺寸盒退场
        lockBtn.Background = new Android.Graphics.Drawables.InsetDrawable(BarButtonBackground(), UiScale.Px(UiScale.BtnTouchPadDp)); // vc120：背景内缩对齐可见盒
        lockBtn.Click += (_, _) => ToggleOrientationLock();
        _lockBtn = lockBtn;
        right.AddView(lockBtn);

        // vc110：音量按钮删除后竖屏 6 钮单行放得下，两行拆分口径退场
        // vc121 老板拍板方案A（2026-11-28 真机「按钮被圆角覆盖」）：仅按钮行两端让出圆角弧区——
        // 让位=底部圆角半径-条内距 16dp（半径≤16dp 时为 0），文字三行位置不动，弧线照旧贴屏幕角
        // vc124 老板令（2026-11-28 真机「有时缩有时不缩」实锤）：开机首建时 RootWindowInsets 圆角几何
        // 尚未就绪，读到兜底值=不缩；转屏重建才读到真值。改两端组挂字段，亮条时重算（见 ApplyControlBarCornerClear）
        _cbBtnLeft = left;
        _cbBtnRight = right;
        // vc132 老板令（14:25「按钮整体再往左右各靠一点」）：圆角让位收窄 8dp（钳 0），按钮更贴两缘
        int cornerClear = Math.Max(0, BottomCornerClearPx() - UiScale.Px(8)); // vc124：统一走封顶口径（横屏留白收口），与设置/列表页同源
        btnRow.AddView(left, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = cornerClear });
        var space = new View(this);
        btnRow.AddView(space, new LinearLayout.LayoutParams(0, 1, 1f)); // 中间留白
        btnRow.AddView(right, new LinearLayout.LayoutParams(-2, -2) { RightMargin = cornerClear });
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
        _cbNowName = null;
        _cbNowTime = null;
        _cbAuxWater = null;
        _cbBtnLeft = null;
        _cbBtnRight = null;
        BuildControlBar();
        if (shown) ShowControlBar();
    }

    /// <summary>控制条信息刷新（vc113 原型对照令）：台名/分辨率·帧率 mini/节目名·画面时刻/水位·接下来
    /// （换台与显条时同步；mini 无读数降级隐藏）。</summary>
    private void RefreshControlBarInfo()
    {
        if (_cbTitle is null || _cbNowName is null || _cbNowTime is null || _cbAuxWater is null) return;
        var ch = _channels.Count > 0 && _currentIndex >= 0 && _currentIndex < _channels.Count
            ? _channels[_currentIndex] : null;
        long buffered = BufferedMsNow() ?? 0;
        // v31 原型口径（vc115 拆段保读数）：节目名与「画面时刻」分段——节目名超长省略，读数段完整；
        // 「实时水位：X秒」完整；缺表降级只显画面时刻（v13 老板令）
        string clock = PictureClock(buffered);
        bool hasNow = _epgNowTitle is { } t && t.Length > 0;
        bool hasNext = _epgNextText is { } n && n.Length > 0;
        _cbTitle.Text = ch is null ? "TaoLive" : ch.Name;
        // vc131 老板令：节目单整块——当前+接下来合并单块文本，左对齐换行（原两段分挂退场）
        string epg = hasNow ? _epgNowTitle! : "";
        if (hasNext) epg = (epg.Length > 0 ? epg + " " : "") + $"· 接下来：{_epgNextText}";
        _cbNowName.Text = epg;
        _cbNowName.Visibility = hasNow || hasNext ? ViewStates.Visible : ViewStates.Gone;
        _cbNowTime.Text = hasNow ? $"· 画面时刻：{clock}" : $"画面时刻：{clock}";
        _cbAuxWater.Text = $"实时水位：{buffered / 1000}秒";
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

    /// <summary>控制条显收：单击画面切换，3 秒自动收；抽屉/面板开着不出。
    /// vc132 老板令（14:27）：空态横幅互斥退场——任何时候点屏幕都唤起操作栏，横幅在下一层被盖住。</summary>
    private void ToggleControlBar()
    {
        if (_listOpen || (_settingsPanelOpen)) return; // 面板优先：面板开着控制条不出
        if (_controlBarShown) HideControlBar();
        else ShowControlBar();
    }

    private void ShowControlBar()
    {
        if (_controlBar is null) return;
        ApplyControlBarCornerClear(); // vc124 老板令：亮条即重算圆角让位（首建时 insets 未就绪的兜底根治）
        RefreshControlBarInfo();
        _controlBar.Visibility = ViewStates.Visible;
        _controlBarShown = true;
        ScheduleControlBarHide();
    }

    /// <summary>vc124 老板令（2026-11-28 真机「有时往里缩有时不缩」）：控制条按钮行两端圆角让位重算——
    /// 首建时 RootWindowInsets 圆角几何未就绪读到兜底值，转屏重建才读真值，故「切一次横屏回来就好了」。
    /// 亮条时即读即算 + Post 一拍补算（insets 晚到场景），两端组边距与条底圆角背景一并刷新。</summary>
    private void ApplyControlBarCornerClear()
    {
        if (_controlBar is null) return;
        int clear = Math.Max(0, BottomCornerClearPx() - UiScale.Px(8)); // vc124：统一走封顶口径（横屏留白收口）；vc132：收窄 8dp 靠拢两缘
        if (_cbBtnLeft is { } l && l.LayoutParameters is LinearLayout.LayoutParams ll) ll.LeftMargin = clear;
        if (_cbBtnRight is { } r && r.LayoutParameters is LinearLayout.LayoutParams rl) rl.RightMargin = clear;
        _controlBar.Background = InkTranslucency.PanelBgWithBottomCorner(this, BottomCornerRadiusPx());
        _main.Post(() =>
        {
            if (_controlBar is null) return;
            int clear2 = Math.Max(0, BottomCornerClearPx() - UiScale.Px(8)); // vc124：统一走封顶口径；vc132：收窄 8dp 靠拢两缘
            if (_cbBtnLeft is { } l2 && l2.LayoutParameters is LinearLayout.LayoutParams ll2) ll2.LeftMargin = clear2;
            if (_cbBtnRight is { } r2 && r2.LayoutParameters is LinearLayout.LayoutParams rl2) rl2.RightMargin = clear2;
            _controlBar.Background = InkTranslucency.PanelBgWithBottomCorner(this, BottomCornerRadiusPx());
        });
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
    /// 老板令「左上右下」；慢拖 OnScroll 累计触发，不只认快滑）；上下滑分区=亮度/音量/切台——
    /// 竖屏三块（vc121 老板令：左 1/3 亮度、右 1/3 音量、中间 1/3 上下滑切台，上=下一台），
    /// 横屏不分块维持左右半区（vc121 老板令：横屏左右滑切台，不用分三块）；行程按整屏高；
    /// 边缘手势（vc110 老板令）：左边缘右滑=呼出列表页（横竖屏一致）、右边缘左滑=横屏退全屏/竖屏出退出确认；
    /// 长按=收藏/取消当前台。滑动阈值 96dp。</summary>
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
                    float xf = _startX / host.Resources!.DisplayMetrics!.WidthPixels;
                    bool portrait = host.Resources!.Configuration!.Orientation != Android.Content.Res.Orientation.Landscape;
                    if (portrait && xf >= 1f / 3f && xf <= 2f / 3f)
                    {
                        // vc121 老板令：竖屏中间 1/3 上下滑=切台（上=下一台、下=上一台），
                        // 门槛沿用横向 96dp 防误触；一次滑动只切一台
                        if (ady > Dp(SwipeMinDp))
                        {
                            _fired = true;
                            host.RunOnUiThread(() => host.SwitchChannel(dy < 0 ? 1 : -1));
                        }
                        _lastY = e2.GetY();
                        return true;
                    }
                    // vc121 老板令：竖屏三块——左 1/3 亮度、右 1/3 音量；横屏不分块，维持左右半区（左右滑切台）
                    _vMode = portrait ? (xf < 1f / 3f ? 1 : 2)
                                      : (_startX < host.Resources!.DisplayMetrics!.WidthPixels / 2f ? 1 : 2);
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
        internal enum IconKind { Prev, Next, List, Gear, Refresh, Close, Doc, Folder, Trash }
        private IconKind _kind = IconKind.List;
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };

        public BarIconView(Context context) : base(context) => _stroke.SetStyle(Paint.Style.Stroke);

        internal IconKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        /// <summary>vc120 肉眼对齐令：可见盒高=旁边文字实际渲染高（控制条跟台名 Item、列表页头跟标题 Title），
        /// 视图整体外扩 BtnTouchPadDp 透明热区；字号档位/系统缩放自动跟，不写死。</summary>
        internal float BoxTextSize { get; set; } = UiScale.Item;

        // vc120：盒径不再写死 44dp——按 BoxTextSize 实测线高取可见盒，视图含透明外扩热区
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int box = UiScale.TextLineHeight(Context!, BoxTextSize);
            int side = box + UiScale.Px(UiScale.BtnTouchPadDp) * 2;
            SetMeasuredDimension(side, side);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            int pad = UiScale.Px(UiScale.BtnTouchPadDp);
            float boxSide = Math.Min(Width, Height) - pad * 2f; // 可见盒（背景经 InsetDrawable 内缩同口径）
            float side = boxSide * 0.58f; // 图标占可见盒 58%，几何正中间
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
                case IconKind.Refresh: // ⟳：270° 圆弧+箭头（刷新，vc119 页头钮自绘化）
                {
                    float cx = w / 2f, cy = h / 2f, r = w * 0.30f;
                    canvas.DrawArc(new RectF(cx - r, cy - r, cx + r, cy + r), -60f, 270f, false, _stroke);
                    // 箭头：弧终点（右上）指顺时针切线方向
                    var tri = new float[]
                    {
                        cx + r * 1.05f, cy - r * 0.55f,
                        cx + r * 0.30f, cy - r * 0.72f,
                        cx + r * 0.62f, cy - r * 1.28f,
                    };
                    var fill = new Paint(PaintFlags.AntiAlias) { Color = _stroke.Color };
                    canvas.DrawPath(ToPath(tri), fill);
                    break;
                }
                case IconKind.Close: // ✕：两根对角线（关闭）
                {
                    float m = w * 0.22f;
                    canvas.DrawLine(m, m, w - m, h - m, _stroke);
                    canvas.DrawLine(w - m, m, m, h - m, _stroke);
                    break;
                }
                case IconKind.Doc: // 📄→自绘：纸张+折角+两行字（打开源文件）
                {
                    float l = w * 0.24f, t = h * 0.16f, r2 = w * 0.76f, b = h * 0.84f;
                    float fold = w * 0.18f;
                    var paper = new Android.Graphics.Path();
                    paper.MoveTo(l, t);
                    paper.LineTo(r2 - fold, t);
                    paper.LineTo(r2, t + fold);
                    paper.LineTo(r2, b);
                    paper.LineTo(l, b);
                    paper.Close();
                    canvas.DrawPath(paper, _stroke);
                    canvas.DrawLine(r2 - fold, t, r2 - fold, t + fold, _stroke);
                    canvas.DrawLine(l + w * 0.12f, h * 0.48f, r2 - w * 0.12f, h * 0.48f, _stroke);
                    canvas.DrawLine(l + w * 0.12f, h * 0.62f, r2 - w * 0.12f, h * 0.62f, _stroke);
                    break;
                }
                case IconKind.Folder: // 📁→自绘：文件夹（打开目录）
                {
                    float l = w * 0.16f, t = h * 0.26f, r2 = w * 0.84f, b = h * 0.76f;
                    float tabW = w * 0.26f, tabH = h * 0.12f;
                    var fd = new Android.Graphics.Path();
                    fd.MoveTo(l, t + tabH);
                    fd.LineTo(l + tabW, t + tabH);
                    fd.LineTo(l + tabW + tabH * 0.6f, t);
                    fd.LineTo(r2, t);
                    fd.LineTo(r2, b);
                    fd.LineTo(l, b);
                    fd.Close();
                    canvas.DrawPath(fd, _stroke);
                    break;
                }
                case IconKind.Trash: // 🗑→自绘：桶盖+桶身两道（清空）
                {
                    float l = w * 0.24f, r2 = w * 0.76f;
                    float lidY = h * 0.28f, bottom = h * 0.82f;
                    canvas.DrawLine(w * 0.16f, lidY, w * 0.84f, lidY, _stroke); // 盖沿
                    canvas.DrawLine(w * 0.5f, h * 0.16f, w * 0.5f, lidY, _stroke); // 提手
                    var bodyP = new Android.Graphics.Path();
                    bodyP.MoveTo(l + w * 0.04f, lidY);
                    bodyP.LineTo(l + w * 0.10f, bottom);
                    bodyP.LineTo(r2 - w * 0.10f, bottom);
                    bodyP.LineTo(r2 - w * 0.04f, lidY);
                    canvas.DrawPath(bodyP, _stroke);
                    canvas.DrawLine(w * 0.40f, h * 0.42f, w * 0.42f, h * 0.68f, _stroke);
                    canvas.DrawLine(w * 0.60f, h * 0.42f, w * 0.58f, h * 0.68f, _stroke);
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
        // vc120 肉眼对齐令：盒高=台名实际渲染高（Item 线高），视图含透明外扩热区，不写死。
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int box = UiScale.TextLineHeight(Context!, UiScale.Item);
            int side = box + UiScale.Px(UiScale.BtnTouchPadDp) * 2;
            SetMeasuredDimension(side, side);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            int pad = UiScale.Px(UiScale.BtnTouchPadDp);
            float boxSide = Math.Min(Width, Height) - pad * 2f; // 可见盒
            // 图标占可见盒 58%，居中平移后按 side 见方绘制——几何正中间
            float side = boxSide * 0.58f;
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
        // vc120 肉眼对齐令：盒高=台名实际渲染高（Item 线高），视图含透明外扩热区，不写死。
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int box = UiScale.TextLineHeight(Context!, UiScale.Item);
            int side = box + UiScale.Px(UiScale.BtnTouchPadDp) * 2;
            SetMeasuredDimension(side, side);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            int pad = UiScale.Px(UiScale.BtnTouchPadDp);
            float boxSide = Math.Min(Width, Height) - pad * 2f; // 可见盒
            // 图标占可见盒 58%，居中平移后按 side 见方绘制；锁体线条风不变
            float side = boxSide * 0.58f;
            canvas.Save();
            canvas.Translate((Width - side) / 2f, (Height - side) / 2f);
            DrawIcon(canvas, side);
            canvas.Restore();
        }

        private void DrawIcon(Canvas canvas, float side)
        {
            var context = Context!;
            _stroke.Color = InkTranslucency.TextPrimary(context);
            _stroke.StrokeWidth = side * 0.075f;
            // vc127 锁图标三绘（老板令 2026-11-28「照 🔓🔒 那样画，只是把带颜色的换成线条」）：
            // emoji 几何直出——锁体=偏下圆角横矩形，锁梁=正圆半拱+双腿插入锁体上沿；
            // 解锁=整梁绕左腿根外旋 38°，左腿原地踏住锁体、右腿抬起悬空，开口一目了然。
            // vc130 老板令（真机「锁定态像茶杯」实锤）：三态照 emoji 重排——去方向小屏指示条
            // （贴锁体右沿读成杯把）、锁梁加宽近锁体同宽（半径 0.165→0.23，横跨≈0.85 锁体宽）、
            // 锁体收小（0.60×0.44→0.54×0.42），比例对齐 🔒；锁定横竖屏同画 🔒。
            bool open = _kind == IconKind.Unlocked;
            float bw = side * 0.54f, bh = side * 0.42f;
            float r = side * 0.23f;
            float bx = (side - bw) / 2f;
            float top = (side - (bh + r)) / 2f; // 锁体+拱高整体垂直居中
            float by = top + r;                 // 锁体上沿
            float scx = side / 2f;
            float leg = side * 0.05f; // 双腿插入锁体的深度（emoji 同款：梁腿没入锁体）
            canvas.DrawRoundRect(bx, by, bx + bw, by + bh, side * 0.12f, side * 0.12f, _stroke);
            void DrawShackle()
            {
                canvas.DrawArc(scx - r, by - r, scx + r, by + r, 180f, 180f, false, _stroke);
                canvas.DrawLine(scx - r, by, scx - r, by + leg, _stroke);
                canvas.DrawLine(scx + r, by, scx + r, by + leg, _stroke);
            }
            if (open)
            {
                canvas.Save();
                canvas.Rotate(-38f, scx - r, by); // 绕左腿根外旋：右腿扬起悬空=开
                DrawShackle();
                canvas.Restore();
            }
            else
            {
                DrawShackle();
            }
        }
    }
}
