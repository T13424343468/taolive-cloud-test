using Android.Content;
using Android.Graphics;
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
/// - 亮度/音量手势：竖横屏均按左右半区——左半区上下滑=调亮度（持久记忆）、右半区上下滑=调音量，悬浮指示跟手；
/// - 控制条按原型 v19–v26 重构：三行（台名读数/节目单/按钮两端分组），横屏紧凑两行；
///   按钮单色文本图标（去 emoji 彩色呈现）；补音量按钮（老板令：一次 ±10）；
/// - 方向锁持久化（taolive_prefs:orientation_lock）不变；屏幕亮度记忆（taolive_prefs:screen_brightness）。
/// 底层零改动：换台走既有 PlayAt/SwitchToCurrent，收藏走既有 ToggleFavoriteOnCurrent，设置走既有 OpenSettingsPanel。
/// </summary>
public partial class MainActivity
{
    private LinearLayout? _controlBar;
    private TextView? _lockBtn;
    private TextView? _cbTitle;    // 控制条行1：台名
    private TextView? _cbReadout;  // 控制条行1：水位·画面时刻
    private TextView? _cbProgram;  // 控制条行2：节目单（横屏并入行1）
    private bool _controlBarShown;
    private Java.Lang.Runnable? _controlBarHide;
    private GestureDetector? _gesture;
    private bool _orientationLocked; // 方向锁（持久化）：锁=钉当前方向，解锁=随传感器

    private FrameLayout? _hud;                 // 亮度/音量悬浮指示
    private TextView? _hudText;
    private Java.Lang.Runnable? _hudHide;
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

        // 手势挂视频载体：单击/滑动/长按全在这层（列表抽屉/面板开着时被盖住，天然不抢）
        _videoView.Touch += (_, e) =>
        {
            e.Handled = _gesture!.OnTouchEvent(e.Event!);
        };

        BuildHud();
        BuildControlBar();
    }

    // —— 悬浮指示（亮度/音量/音量按钮共用） ————————————————————

    private void BuildHud()
    {
        _hud = new FrameLayout(this);
        _hud.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));
        _hudText = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = UiScale.Item,
        };
        _hudText.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_hudText);
        int hpad = UiScale.Px(28), vpad = UiScale.Px(14);
        _hudText.SetPadding(hpad, vpad, hpad, vpad);
        _hud.AddView(_hudText);
        _hud.Visibility = ViewStates.Gone;
        _playerPage.AddView(_hud, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });
    }

    private void ShowHud(string text)
    {
        if (_hud is null || _hudText is null) return;
        _hudText.Text = text;
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
        ShowHud($"亮度 {_brightness * 100f:F0}%");
        if (_brightnessSave is { } old) _main.RemoveCallbacks(old);
        _brightnessSave = new Java.Lang.Runnable(() =>
        {
            var ed = _prefs.Edit();
            ed?.PutFloat("screen_brightness", _brightness);
            ed?.Apply();
        });
        _main.PostDelayed(_brightnessSave, 600);
    }

    private void StepVolume(int dir)
    {
        if (_audio is null) return;
        int max = _audio.GetStreamMaxVolume(Android.Media.Stream.Music);
        int step = Math.Max(1, max / 10); // 老板令：一次 ±10（按 0–100 口径折算刻度）
        int idx = Math.Clamp(_audio.GetStreamVolume(Android.Media.Stream.Music) + dir * step, 0, max);
        _audio.SetStreamVolume(Android.Media.Stream.Music, idx, 0); // 0=不出系统音量条，用自绘 HUD
        ShowHud($"音量 {idx * 100 / max}%");
    }

    // —— 控制条（原型 v19–v26：三行结构/两端分组/单色图标 + 音量按钮） ———

    private void BuildControlBar()
    {
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        _controlBar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        _controlBar.SetBackgroundColor(InkTranslucency.PanelSolidColor(this));
        _controlBar.SetPadding(UiScale.Px(16), UiScale.Px(8), UiScale.Px(16), UiScale.Px(10));

        // 信息行：台名+读数（竖屏行1）；横屏与节目单同行横排（v25 紧凑口径）
        var infoRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _cbTitle = new TextView(this) { TextSize = UiScale.Item };
        _cbTitle.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_cbTitle);
        infoRow.AddView(_cbTitle);
        _cbReadout = new TextView(this) { TextSize = UiScale.Caption };
        _cbReadout.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_cbReadout);
        infoRow.AddView(_cbReadout);
        _controlBar.AddView(infoRow);

        if (!landscape)
        {
            _cbProgram = new TextView(this) { TextSize = UiScale.Caption, Ellipsize = TextUtils.TruncateAt.End };
            _cbProgram.SetSingleLine(true);
            _cbProgram.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(_cbProgram);
            _controlBar.AddView(_cbProgram);
        }
        else
        {
            _cbProgram = null; // 横屏节目单并入信息行（v25），由 RefreshControlBarInfo 拼接
        }

        // 按钮行：两端分组（v21）——左组切台、右组功能+音量；中间留白撑开
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
        AddBtn(right, "⟳", (_, _) =>
        {
            bool land = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
            RequestedOrientation = land ? ScreenOrientation.SensorPortrait : ScreenOrientation.SensorLandscape;
            ShowToast(land ? "竖屏" : "横屏");
        });
        var lockBtn = new TextView(this) { Gravity = GravityFlags.Center, TextSize = UiScale.Item };
        lockBtn.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(lockBtn);
        lockBtn.SetPadding(UiScale.Px(18), UiScale.Px(12), UiScale.Px(18), UiScale.Px(12));
        lockBtn.Click += (_, _) => ToggleOrientationLock();
        _lockBtn = lockBtn;
        right.AddView(lockBtn);
        AddBtn(right, "音量−", (_, _) => StepVolume(-1));
        AddBtn(right, "音量+", (_, _) => StepVolume(1));

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
        string readout = $"  实时水位：{buffered / 1000}秒 · 画面时刻：{PictureClock(buffered)}";
        bool landscape = Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape;
        string program = _epgNowTitle is { } t ? t : "节目单暂无数据";
        if (landscape)
        {
            _cbTitle.Text = ch is null ? "TaoLive" : ch.Name;
            _cbReadout.Text = $"  {readout}  {program}";
        }
        else
        {
            _cbTitle.Text = (ch is null ? "TaoLive" : ch.Name) + readout;
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
    /// 写偏好跨启动记忆。</summary>
    private void ToggleOrientationLock()
    {
        _orientationLocked = !_orientationLocked;
        var editor = _prefs.Edit();
        editor?.PutBoolean("orientation_lock", _orientationLocked);
        editor?.Apply();
        ApplyOrientationLock();
        RefreshLockButton();
        ShowToast(_orientationLocked ? "方向已锁定" : "方向已解锁");
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
        if (_lockBtn is { } b) b.Text = _orientationLocked ? "已锁" : "锁";
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

    /// <summary>触屏手势监听（2026-11-27 整改令）：单击=控制条；左右滑=切台（左=上一台、右=下一台，
    /// 老板令「左上右下」；慢拖 OnScroll 累计触发，不只认快滑）；上下滑按半区=亮度（左）/音量（右）；
    /// 长按=收藏/取消当前台。滑动阈值 96dp。</summary>
    private sealed class PhoneGestureListener(MainActivity host) : GestureDetector.SimpleOnGestureListener
    {
        private const float SwipeMinDp = 96f;

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

        public override bool OnScroll(MotionEvent? e1, MotionEvent? e2, float distanceX, float distanceY)
        {
            if (e1 is null || e2 is null || _fired) return true;
            float dx = e2.GetX() - _startX;
            float dy = e2.GetY() - _startY;

            if (_vMode == 0)
            {
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
                float delta = (_lastY - e2.GetY()) / (host.Resources!.DisplayMetrics!.HeightPixels * 0.6f); // 上滑增
                host.RunOnUiThread(() => host.SetBrightness(host._brightness + delta));
            }
            else if (_vMode == 2)
            {
                if (host._audio is { } audio)
                {
                    int max = audio.GetStreamMaxVolume(Android.Media.Stream.Music);
                    float delta = (_lastY - e2.GetY()) / (host.Resources!.DisplayMetrics!.HeightPixels * 0.6f) * max;
                    _volAccum += delta;
                    if (Math.Abs(_volAccum) >= 1f)
                    {
                        int step = (int)_volAccum;
                        _volAccum -= step;
                        int idx = Math.Clamp(audio.GetStreamVolume(Android.Media.Stream.Music) + step, 0, max);
                        audio.SetStreamVolume(Android.Media.Stream.Music, idx, 0);
                        int idxFinal = idx;
                        host.RunOnUiThread(() => host.ShowHud($"音量 {idxFinal * 100 / max}%"));
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
            if (Dp(Math.Abs(dx)) < SwipeMinDp || Math.Abs(dx) < Math.Abs(dy)) return true;
            _fired = true;
            host.RunOnUiThread(() => host.SwitchChannel(dx < 0 ? -1 : 1)); // 左=上一台，右=下一台
            return true;
        }
    }
}
