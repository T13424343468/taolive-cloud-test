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
/// - 亮度/音量手势：竖横屏均按左右半区——左半区上下滑=调亮度（持久记忆）、右半区上下滑=调音量；指示=自绘竖条+单色图标跟手（2026-11-27 老板令「滑动还是要、样式也还是要有」）；控制条音量按钮走系统音量条（同日老板令「直接用手机的」）；
/// - 控制条按原型 v19–v26 重构：三行（台名读数/节目单/按钮两端分组），横屏紧凑两行；
///   按钮单色文本图标（去 emoji 彩色呈现）；补音量按钮（老板令：一次 ±10）；
/// - 方向锁持久化（taolive_prefs:orientation_lock）不变；屏幕亮度记忆（taolive_prefs:screen_brightness）。
/// 底层零改动：换台走既有 PlayAt/SwitchToCurrent，收藏走既有 ToggleFavoriteOnCurrent，设置走既有 OpenSettingsPanel。
/// </summary>
public partial class MainActivity
{
    private LinearLayout? _controlBar;
    private LockIconView? _lockBtn;
    private TextView? _cbTitle;    // 控制条行1：台名
    private TextView? _cbReadout;  // 控制条行1：水位·画面时刻
    private TextView? _cbProgram;  // 控制条行2：节目单（横屏并入行1）
    private bool _controlBarShown;
    private Java.Lang.Runnable? _controlBarHide;
    private GestureDetector? _gesture;
    private bool _orientationLocked; // 方向锁（持久化）：锁=钉当前方向，解锁=随传感器

    private FrameLayout? _hud;                 // 亮度/音量手势悬浮指示（竖条+图标，普通播放器样式，2026-11-27 老板令）
    private HudIconView? _hudIcon;
    private View? _hudFill;                    // 竖条填充（高度=音量/亮度比例）
    private Java.Lang.Runnable? _hudHide;
    private const int HudTrackDp = 110;        // 竖条轨道高（dp，390 基准；vc110 老板令「悬浮指示条缩小」160→110）
    private const int HudIconDp = 24;          // 指示图标边长（vc110 缩小 30→24）
    private const int HudTrackWidthDp = 8;     // 竖条轨道宽（vc110 缩小 10→8）
    private AudioManager? _audio;
    private float _brightness = 0.5f;          // 0.05–1，持久记忆
    private Java.Lang.Runnable? _brightnessSave; // 亮度落盘防抖

    /// <summary>触屏层装配（BuildRoot 末尾调用）：手势挂视频面，控制条挂播放页。</summary>
    private void BuildTouchLayer()
    {
        _orientationLocked = _prefs.GetBoolean("orientation_lock", false);
        _brightness = Math.Clamp(_prefs.GetFloat("screen_brightness", 0.5f), 0.05f, 1f);
        try { if (Window?.Attributes is { } attr) attr.ScreenBrightness = _brightness; } catch { }
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
        _playerPage.AddView(_hud, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });
    }

    /// <summary>悬浮指示条落位（vc110 老板令「移到黑区显示，不压画面」）：竖屏=顶部黑区居中，
    /// 横屏=右侧黑区竖向居中（画面满高后左右留黑边）。转屏重算。</summary>
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
            lp.Gravity = GravityFlags.CenterVertical | GravityFlags.Right;
            lp.RightMargin = UiScale.Px(16);
        }
        else
        {
            lp.Gravity = GravityFlags.CenterHorizontal | GravityFlags.Top;
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

    private void SetBrightness(float value)
    {
        _brightness = Math.Clamp(value, 0.05f, 1f);
        try { if (Window?.Attributes is { } attr) attr.ScreenBrightness = _brightness; } catch { }
        ShowHudBar(HudIconView.IconKind.Sun, _brightness); // 亮度 0.05–1 直接作比例
        if (_brightnessSave is { } old) _main.RemoveCallbacks(old);
        _brightnessSave = new Java.Lang.Runnable(() =>
        {
            var ed = _prefs.Edit();
            ed?.PutFloat("screen_brightness", _brightness);
            ed?.Apply();
        });
        _main.PostDelayed(_brightnessSave, 600);
    }

    // —— 控制条（原型 v19–v26：三行结构/两端分组/单色图标 + 音量按钮） ———

    private void BuildControlBar()
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        _controlBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        _controlBar.SetBackgroundColor(InkTranslucency.PanelSolidColor(this));
        _controlBar.SetPadding(UiScale.Px(16), UiScale.Px(8), UiScale.Px(16), UiScale.Px(10));

        // 信息行：台名+读数同行（v26 原型口径：台名左、读数右，均单行——2026-11-27 走查修：竖屏原把读数拼进台名
        // 导致整行换行顶高，违 v13 单行令）；横屏与节目单同行横排（v25 紧凑口径）
        var infoRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _cbTitle = new TextView(this) { TextSize = UiScale.Item, Ellipsize = TextUtils.TruncateAt.End };
        _cbTitle.SetSingleLine(true);
        _cbTitle.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_cbTitle);
        infoRow.AddView(_cbTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)); // 台名占余宽、超长省略
        _cbReadout = new TextView(this) { TextSize = UiScale.Caption, Ellipsize = TextUtils.TruncateAt.End };
        _cbReadout.SetSingleLine(true);
        _cbReadout.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_cbReadout);
        infoRow.AddView(_cbReadout);
        _controlBar.AddView(infoRow);

        if (!landscape)
        {
            _cbProgram = new TextView(this)
            {
                TextSize = UiScale.Caption,
                Ellipsize = TextUtils.TruncateAt.End,
                Gravity = GravityFlags.End, // vc110 老板令：节目名右对齐，右缘与「实时水位」读数对齐（不再像台名硬换行）
            };
            _cbProgram.SetSingleLine(true);
            _cbProgram.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(_cbProgram);
            _controlBar.AddView(_cbProgram);
        }
        else
        {
            _cbProgram = null; // 横屏节目单并入信息行（v25），由 RefreshControlBarInfo 拼接
        }

        // 按钮行：两端分组（v21）——左组切台、右组功能；中间留白撑开
        // vc110 老板令：音量加减按钮全删（横竖屏都删，音量走手势/系统键）
        var btnRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        btnRow.SetGravity(GravityFlags.CenterVertical);
        var left = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        var right = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };

        void AddBtn(LinearLayout into, string text, EventHandler click)
        {
            var b = new TextView(this)
            {
                Text = text,
                Gravity = GravityFlags.Center,
                TextSize = UiScale.Item,
            };
            b.SetHeight(UiScale.Px(44)); // 定高按钮：◀▶ 符号字形走回退字体度量偏大，不定高会把整行顶高、字形下沉错位（2026-11-27 走查实锤）
            b.SetTextColor(InkTranslucency.TextPrimary(this)); // 单色文本图标（v22：去彩色 emoji 呈现）
            InkTranslucency.ApplyReadabilityShadow(b);
            b.SetPadding(UiScale.Px(18), UiScale.Px(12), UiScale.Px(18), UiScale.Px(12)); // 手机热区：间距档实值
            b.Click += click;
            into.AddView(b);
        }

        AddBtn(left, "◀", (_, _) => SwitchChannel(-1));
        AddBtn(left, "▶", (_, _) => SwitchChannel(1));

        AddBtn(right, "☰", (_, _) => { HideControlBar(); ShowList(); });
        AddBtn(right, "⚙\uFE0E", (_, _) => { HideControlBar(); OpenSettingsPanel(); });

        // 翻转按钮（vc110 老板令：⟳ 换两枚图标——竖屏显「进全屏」、横屏显「返回竖屏」，自绘单色同风格）
        var flipBtn = new FullscreenIconView(this)
        {
            Kind = landscape ? FullscreenIconView.IconKind.ExitFullscreen : FullscreenIconView.IconKind.EnterFullscreen,
        };
        flipBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, UiScale.Px(44)); // 定高按钮：与文字钮同热区
        flipBtn.SetPadding(UiScale.Px(18), UiScale.Px(12), UiScale.Px(18), UiScale.Px(12));
        flipBtn.Click += (_, _) =>
        {
            bool land = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
            RequestedOrientation = land ? ScreenOrientation.SensorPortrait : ScreenOrientation.SensorLandscape;
            ShowToast(land ? "竖屏" : "横屏");
        };
        right.AddView(flipBtn);

        // 锁定按钮（vc110 老板令：文字「锁/已锁」换图标——解锁=开锁、锁定横屏=横屏锁、锁定竖屏=竖屏锁，自绘单色同风格）
        var lockBtn = new LockIconView(this);
        lockBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, UiScale.Px(44)); // 定高按钮：与文字钮同热区
        lockBtn.SetPadding(UiScale.Px(18), UiScale.Px(12), UiScale.Px(18), UiScale.Px(12));
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

    /// <summary>转屏重建控制条（竖屏三行/横屏两行布局不同，v25/v26 口径）。</summary>
    private void RebuildControlBar()
    {
        if (_controlBar is null) return;
        bool shown = _controlBarShown;
        if (_controlBar.Parent is ViewGroup p) p.RemoveView(_controlBar);
        _controlBar = null;
        _lockBtn = null;
        _cbTitle = null;
        _cbReadout = null;
        _cbProgram = null;
        BuildControlBar();
        if (shown) ShowControlBar();
    }

    /// <summary>控制条信息刷新：台名/水位读数/节目单（换台与显条时同步）。</summary>
    private void RefreshControlBarInfo()
    {
        if (_cbTitle is null || _cbReadout is null) return;
        var ch = _channels.Count > 0 && _currentIndex >= 0 && _currentIndex < _channels.Count
            ? _channels[_currentIndex] : null;
        long buffered = BufferedMsNow() ?? 0;
        string readout = $"实时水位：{buffered / 1000}秒 · 画面时刻：{PictureClock(buffered)}";
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        string program = _epgNowTitle is { } t ? t : "节目单暂无数据";
        if (landscape)
        {
            _cbTitle.Text = ch is null ? "TaoLive" : ch.Name;
            _cbReadout.Text = $"  {readout}  {program}";
        }
        else
        {
            // 竖屏：台名/读数分属两个 TextView（信息行单行、台名超长省略，v26 原型口径）
            _cbTitle.Text = ch is null ? "TaoLive" : ch.Name;
            _cbReadout.Text = "  " + readout;
            _cbProgram?.Text = program;
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
    /// 写偏好跨启动记忆。vc110：提示与图标配套三态（解锁/锁横屏/锁竖屏）。</summary>
    private void ToggleOrientationLock()
    {
        _orientationLocked = !_orientationLocked;
        var editor = _prefs.Edit();
        editor?.PutBoolean("orientation_lock", _orientationLocked);
        editor?.Apply();
        ApplyOrientationLock();
        RefreshLockButton();
        if (!_orientationLocked) ShowToast("方向已解锁");
        else
        {
            bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
            ShowToast(landscape ? "已锁定横屏" : "已锁定竖屏");
        }
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

    /// <summary>控制条显收：单击画面切换，3 秒自动收；抽屉/面板开着不出。</summary>
    private void ToggleControlBar()
    {
        if (_listOpen || (_settingsPanelOpen)) return; // 面板优先：面板开着控制条不出
        if (_controlBarShown) HideControlBar();
        else ShowControlBar();
    }

    private void ShowControlBar()
    {
        if (_controlBar is null) return;
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
        private const float SwipeMinDp = 96f;
        private const float EdgeDp = 28f; // 边缘判定带宽：起点落在左右边缘此带宽内走边缘手势，不切台

        private float _startX, _startY, _lastY;
        private bool _fired;        // 本次手势已触发切台（一次滑动只切一台）
        private int _vMode;         // 0=未定 1=亮度 2=音量
        private float _volAccum;    // 音量连续调整累计（刻度）

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
            _volAccum = 0f;
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
                    host.RequestedOrientation = ScreenOrientation.SensorPortrait; // 横屏：退全屏回竖屏
                    host.ShowToast("竖屏");
                }
                else host.ConfirmExit(); // 竖屏：出退出确认（再确认才退）
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
                if (Dp(Math.Abs(dx)) > SwipeMinDp && Math.Abs(dx) > Math.Abs(dy) * 1.2f)
                {
                    _fired = true;
                    host.RunOnUiThread(() => host.SwitchChannel(dx < 0 ? -1 : 1)); // 左=上一台，右=下一台
                    return true;
                }
                if (Dp(Math.Abs(dy)) > SwipeMinDp && Math.Abs(dy) > Math.Abs(dx) * 1.2f)
                {
                    bool leftHalf = _startX < host.Resources!.DisplayMetrics!.WidthPixels / 2f;
                    _vMode = leftHalf ? 1 : 2; // 左半区亮度、右半区音量
                }
            }

            if (_vMode == 1)
            {
                float delta = (_lastY - e2.GetY()) / host.Resources!.DisplayMetrics!.HeightPixels; // vc110：行程=整屏高
                host.RunOnUiThread(() => host.SetBrightness(host._brightness + delta));
            }
            else if (_vMode == 2)
            {
                if (host._audio is { } audio)
                {
                    int max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
                    float delta = (_lastY - e2.GetY()) / host.Resources!.DisplayMetrics!.HeightPixels * max; // vc110：行程=整屏高
                    _volAccum += delta;
                    if (Math.Abs(_volAccum) >= 1f)
                    {
                        int step = (int)_volAccum;
                        _volAccum -= step;
                        int idx = Math.Clamp(audio.GetStreamVolume(Android.Media.Stream.Music) + step, 0, max);
                        // 手势调音量：自绘竖条跟手（不出系统条，避免侧边条与手势指示打架）；按钮路径才走系统条
                        audio.SetStreamVolume(Android.Media.Stream.Music, idx, 0);
                        int idxFinal = idx;
                        host.RunOnUiThread(() => host.ShowHudBar(HudIconView.IconKind.Speaker, idxFinal / (float)max));
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

    /// <summary>翻转按钮图标（vc110 老板令）：竖屏显「进全屏」（四角外扩+外向箭头）、横屏显「返回竖屏」
    /// （四角内收+内向箭头），单色自绘，与 ☰ ⚙ 同风格同热区。</summary>
    private sealed class FullscreenIconView : View
    {
        internal enum IconKind { EnterFullscreen, ExitFullscreen }
        private IconKind _kind = IconKind.EnterFullscreen;
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };

        public FullscreenIconView(Context context) : base(context) => _stroke.SetStyle(Paint.Style.Stroke);

        internal IconKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            _stroke.Color = InkTranslucency.TextPrimary(Context);
            _stroke.StrokeWidth = Math.Max(Width, Height) * 0.08f;
            float w = Width, h = Height;
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
        private readonly Paint _fill = new(PaintFlags.AntiAlias);
        private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round };

        public LockIconView(Context context) : base(context) => _stroke.SetStyle(Paint.Style.Stroke);

        internal IconKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas is null || Context is null) return;
            var color = InkTranslucency.TextPrimary(Context);
            _fill.Color = color;
            _stroke.Color = color;
            _stroke.StrokeWidth = Math.Max(Width, Height) * 0.09f;
            float w = Width, h = Height;
            // 锁体：圆角矩形（居中偏下）
            float bw = w * 0.42f, bh = h * 0.32f;
            float bx = w * 0.42f - bw / 2, by = h * 0.54f;
            canvas.DrawRoundRect(bx, by, bx + bw, by + bh, bw * 0.2f, bw * 0.2f, _fill);
            // 锁梁：拱形；解锁=右腿翘起离体，锁定=双腿扣到体上
            float r = bw * 0.30f;
            float scx = bx + bw / 2, scy = by;
            canvas.DrawArc(scx - r, scy - r * 1.7f, scx + r, scy + r * 0.3f, 180f, 180f, false, _stroke);
            canvas.DrawLine(scx - r, scy - r * 0.7f, scx - r, scy, _stroke);
            if (_kind == IconKind.Unlocked)
                canvas.DrawLine(scx + r, scy - r * 0.7f, scx + r, scy - r * 1.3f, _stroke); // 右腿翘起=开
            else
                canvas.DrawLine(scx + r, scy - r * 0.7f, scx + r, scy, _stroke);
            // 方向指示：锁定时锁体右侧的小屏（横=宽扁、竖=窄高）
            if (_kind == IconKind.LockLandscape)
                canvas.DrawRoundRect(w * 0.76f, h * 0.44f, w * 0.94f, h * 0.62f, 2f, 2f, _fill);
            else if (_kind == IconKind.LockPortrait)
                canvas.DrawRoundRect(w * 0.80f, h * 0.36f, w * 0.94f, h * 0.64f, 2f, 2f, _fill);
        }
    }
}
