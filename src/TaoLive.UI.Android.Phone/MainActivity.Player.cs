using System;
using System.Linq;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using AllPlayer.BLL;
using AllPlayer.Model;
using AllPlayer.UI.Android.Shared;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>TaoPlay 手机版 — 播放页 (partial): 手势 / 控制条 / 锁定 / 画中画 / 倍速 / 投屏 / 字幕音轨。</summary>
    public partial class MainActivity
    {
        // v3.22.0 方案A: 画面载体抽象化——默认自写纹理视图 (退后台不回收画面层),
        // 低配机型自检不过自动永久回退官方表面视图 (路径与既有真机验证过的稳定版一致)
        private IVideoCarrierView _carrier = null!;

        // ================= 界面构建 =================

        private void BuildPlayer()
        {
            _playerPanel = new FrameLayout(this);
            // v3.22.0 方案A: 默认纹理载体——SurfaceView 退后台整层回收, 回前台重建时序在真机
            // 出现画面缩成贴底横条; TextureView 是普通视图层, 退后台不销毁, 回前台画面原样保留。
            // 纹理启动没活到首帧的机型, 下次启动起由 CarrierPolicy 自动永久回退表面载体。
            _carrier = CarrierPolicy.ResolveStartupCarrier(this)
                ? (IVideoCarrierView)new AllPlayerTextureView(this)
                : new VideoCarrierAdapter(new LibVLCSharp.Platforms.Android.VideoView(this));
            _playerPanel.AddView(_carrier.AsView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // v3.23.1 (真机反馈打开画面抖动): 纹理载体首帧挂上后、内核报出视频真实尺寸前,
            // 会外露「贴底横条→过裁放大→跳回适配」的中间态——开场整层黑场过渡,
            // 尺寸定版后同帧揭幕, 打开即稳定适配画面 (进度条等控制条也一并布局定型前不画)
            // v3.25.2 黑屏首帧·本地: 黑场底上叠首帧缩略图 (列表预热解出的才亮),
            // 本地文件解码初始化的固有耗时里黑场变成画面, 远程流无缩略图维持纯黑
            _openingCover = new FrameLayout(this);
            _openingCover.SetBackgroundColor(Color.Black);
            _openingThumb = new ImageView(this);
            _openingThumb.SetScaleType(ImageView.ScaleType.FitCenter);
            _openingThumb.Visibility = ViewStates.Gone;
            _openingCover.AddView(_openingThumb, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            // v3.25.8 原画级封面层: 叠在小图上, 解好原画尺寸首帧后淡入 (列表小图只做即时垫底)
            _openingThumbHd = new ImageView(this);
            _openingThumbHd.SetScaleType(ImageView.ScaleType.FitCenter);
            _openingThumbHd.Visibility = ViewStates.Gone;
            _openingCover.AddView(_openingThumbHd, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _playerPanel.AddView(_openingCover, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // 手势层 (v3.9 修「控制条按钮点了没反应」): 紧贴视频之上、控制条/顶栏之下 ——
            // 以前它盖在顶栏和底栏上面, 触摸先被它整层吃掉, 暂停/主页/锁定/更多全收不到点击;
            // 现在控制 UI 永远在手势层之上, 按钮优先拿到触摸, 视频区域才走手势。
            var gestureLayer = new View(this);
            var detector = new GestureDetector(this, new PlayerGestures(this));
            gestureLayer.Touch += (s, e) =>
            {
                detector.OnTouchEvent(e.Event!);
                if (e.Event!.Action == MotionEventActions.Up)
                {
                    FinishGesture();
                    EndRateBoost();
                    ScheduleHide();
                }
                e.Handled = true;   // 这一层不与任何按钮重叠, 完整消费手势序列, 单击呼出才稳定
            };
            _playerPanel.AddView(gestureLayer, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // 居中手势提示: 实底圆角胶囊 (深底浅字), 视频再暗也看得清
            _gestureHint = new TextView(this) { TextSize = 18, Visibility = ViewStates.Gone };
            _gestureHint.SetTextColor(TaoTheme.Bg);
            var hintBg = new GradientDrawable();
            hintBg.SetCornerRadius(TaoUi.Dp(this, 24));
            // v3.23.2 (真机反馈): 底色改 70% 透明度——亮画面不糊、暗画面看得清; 文字与圆角不动
            var hintColor = TaoTheme.TextMain;
            hintBg.SetColor(new Color(hintColor.R, hintColor.G, hintColor.B, (int)(hintColor.A * 0.7f)));
            _gestureHint.Background = hintBg;
            _gestureHint.SetPadding(TaoUi.Dp(this, 28), TaoUi.Dp(this, 14), TaoUi.Dp(this, 28), TaoUi.Dp(this, 14));
            _playerPanel.AddView(_gestureHint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));

            // v3.13 顶部控制行 (对照参考图, 竖横屏同布局):
            // 左上 返回+文件名; 右上 投屏+锁定+更多 —— 统一 40dp 正圆玻璃钮
            var topBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            topBar.SetGravity(GravityFlags.CenterVertical);
            // v3.22.5 (老板口径): 顶栏内边距走 TopBarPolicy——
            // 上=状态栏/挖孔安全高度+间隙, 下=间隙 → 按钮离遮罩底边的距离与安全区以下到按钮的距离一致,
            // 上下留白对称、按钮组在遮罩内垂直居中, 视觉舒适不挤不贴
            // v3.25.18: 安全垫按横竖屏分算并可在转屏后重算 (修横屏按钮被等高裁切)
            ApplyTopBarPadding(topBar);
            _backBtn = TaoUi.MakeGlassBtn(this, IconKind.Back, 18, () => { SaveTsIfRecording(); _host.Stop(); ShowPlayer(false); });
            topBar.AddView(_backBtn, TaoUi.Dp(this, 40), TaoUi.Dp(this, 40));
            _title = new TextView(this) { Text = "", TextSize = 15 };
            // v3.25.25 跑马灯: 文件名超长改区域内循环滚动, 不再单行截断 (Selected 常置免焦点, 永久滚动)
            _title.SetSingleLine(true);
            _title.Ellipsize = TextUtils.TruncateAt.Marquee;
            _title.SetMarqueeRepeatLimit(-1);
            _title.Selected = true;
            _title.Gravity = GravityFlags.CenterVertical;   // v3.25.17: 文字格内垂直居中, 绝对位置不变, 与上移后的按钮圆心对齐
            _title.SetTextColor(new global::Android.Graphics.Color(255, 255, 255));   // v3.25.22 可读性修: 文件名同底栏画质状态小字口径——恒亮白+半透明黑描影, 浅色主题下压在画面上不再隐形
            _title.SetShadowLayer(4f, 0f, 1f, global::Android.Graphics.Color.Argb(153, 0, 0, 0));
            _title.SetPadding(TaoUi.Dp(this, 10), 0, TaoUi.Dp(this, 10), 0);
            // 标题行高 40dp 垂直居中: 文字正对两侧钮的中心线
            // v3.25.20 大字体适配: 固定 40dp 在 1.3 倍字体下会截字, 改最小高 40dp + 自然撑开
            _title.SetMinHeight(TaoUi.Dp(this, 40));
            topBar.AddView(_title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { Gravity = GravityFlags.CenterVertical });
            // v3.22.5 (老板拍板): 右组顺序 投屏 | 锁定 | 更多 —— 锁定居中, 更多保持最右
            _castBtn = TaoUi.MakeGlassBtn(this, IconKind.Cast, 18, ShowCastDialog);   // v3.13 投屏升级为顶栏直达
            topBar.AddView(_castBtn, TaoUi.Dp(this, 40), TaoUi.Dp(this, 40));

            // v3.20.5 状态文字迁至底栏中间区, 顶栏恢复单行; 状态条随控制条整体显隐, 无独立底栏
            topBar.SetBackgroundColor(Scrim());
            _playerTopBar = topBar;
            _playerPanel.AddView(topBar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

            // v3.22.4 (老板拍板): 锁定键只在菜单呼出时出现——移入顶栏, 与菜单同进同退, 不再常驻画面右侧
            // v3.22.5: 插到投屏与更多之间 (锁定居中)
            _lockBtn = TaoUi.MakeGlassBtn(this, IconKind.Lock, 18, ToggleLock);
            topBar.AddView(_lockBtn, new LinearLayout.LayoutParams(TaoUi.Dp(this, 40), TaoUi.Dp(this, 40)) { LeftMargin = TaoUi.Dp(this, 6) });

            _playerMoreBtn = TaoUi.MakeGlassBtn(this, IconKind.More, 18, ShowMoreMenu);
            topBar.AddView(_playerMoreBtn, new LinearLayout.LayoutParams(TaoUi.Dp(this, 40), TaoUi.Dp(this, 40)) { LeftMargin = TaoUi.Dp(this, 6) });

            // v3.22.4 中央播放浮钮: 暂停态亮在画面正中, 播放态消失 (按钮只反映真实状态)
            _centerPlayBtn = TaoUi.MakeGlassBtn(this, IconKind.Play, 26, TogglePauseBtn);
            _centerPlayBtn.Visibility = ViewStates.Gone;
            _playerPanel.AddView(_centerPlayBtn, new FrameLayout.LayoutParams(TaoUi.Dp(this, 64), TaoUi.Dp(this, 64), GravityFlags.Center));

            // 锁定后的解锁按钮: v3.22.4 按老板全盘设计改中央——锁定时点屏幕只在画面正中短暂亮解锁键, 2.5 秒无操作自动消失
            _unlockBtn = TaoUi.MakeGlassBtn(this, IconKind.LockOpen, 18, ToggleLock);
            _unlockBtn.Visibility = ViewStates.Gone;

            // 底部控制条
            var controls = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _controls = controls;
            controls.SetBackgroundColor(Scrim(160));
            ApplyControlsPadding(controls);   // v3.25.19: 底部安全垫按横竖屏分算, 三键导航机型不再贴死

            _playerStatus = new TextView(this) { TextSize = 13, Visibility = ViewStates.Gone };
            _playerStatus.SetTextColor(TaoTheme.TextSub);
            controls.AddView(_playerStatus, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            _seek = new SeekBar(this);
            _seek.Max = 1000;
            _seek.StartTrackingTouch += (s, e) =>
            {
                _seekDragging = true;
                // 拖动进度期间冻结自动隐藏, 防止拖到一半控制条收起把手势打断 (横屏全屏时最明显)
                if (_hideRun != null) _uiHandler.RemoveCallbacks(_hideRun);
            };
            _seek.StopTrackingTouch += (s, e) =>
            {
                _seekDragging = false;
                var len = _host.Player?.Length ?? 0;
                if (len > 0)
                {
                    var target = _seek.Progress * len / 1000;
                    _host.SeekTo(target);
                    OnSeekChunkAware(_host.CurrentPath, target);   // v3.17: seek 感知——落增强区原地见效果，未增强区增强链照常追
                }
                if (_seekPreview != null) _seekPreview.Visibility = ViewStates.Gone;   // v3.18.6: 松手收预览
                ScheduleHide();
            };
            // v3.18.6 拖动中: 实时显示拖到的时间 + 就近抽帧小预览 (200ms 节流, 秒级缓存)
            _seek.ProgressChanged += (s, e) =>
            {
                if (!e.FromUser) return;
                var len = _host.Player?.Length ?? 0;
                if (len <= 0) return;
                UpdateSeekPreview(_seek.Progress * len / 1000);
            };
            // v3.13 进度行 (对照参考图): 当前时间 — 进度条 — 总时长, 全屏钮收在行尾
            var seekRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            seekRow.SetGravity(GravityFlags.CenterVertical);
            _timeCur = new TextView(this) { Text = "00:00", TextSize = 12 };
            _timeCur.SetTextColor(TaoTheme.TextSub);
            seekRow.AddView(_timeCur, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            seekRow.AddView(_seek, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
            { LeftMargin = TaoUi.Dp(this, 6), RightMargin = TaoUi.Dp(this, 6) });
            _timeDur = new TextView(this) { Text = "00:00", TextSize = 12 };
            _timeDur.SetTextColor(TaoTheme.TextSub);
            seekRow.AddView(_timeDur, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            _fullscreenBtn = TaoUi.MakeGlassBtn(this, _fullscreen ? IconKind.FullscreenExit : IconKind.Fullscreen, 18, ToggleFullscreen);
            seekRow.AddView(_fullscreenBtn, new LinearLayout.LayoutParams(TaoUi.Dp(this, 40), TaoUi.Dp(this, 40)) { LeftMargin = TaoUi.Dp(this, 6) });
            _fullscreenBtn.Visibility = AppSettings.Current.AutoRotate ? ViewStates.Gone : ViewStates.Visible; // v3.12 自动旋转开=全屏钮收起
            controls.AddView(seekRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // v3.13 底行 (对照参考图): 上一集/播放暂停/下一集 靠左, 倍速 靠右 —— 统一 40dp 正圆钮, 整块可点
            var bottomRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bottomRow.SetGravity(GravityFlags.CenterVertical);
            void Mk(IconKind kind, Action click)
            {
                var b = TaoUi.MakeGlassBtn(this, kind, 18, click);
                var lp = new LinearLayout.LayoutParams(TaoUi.Dp(this, 40), TaoUi.Dp(this, 40));
                lp.LeftMargin = TaoUi.Dp(this, 6);
                lp.RightMargin = TaoUi.Dp(this, 6);
                bottomRow.AddView(b, lp);
            }
            Mk(IconKind.Prev, () => _host.PlayPrevious());
            _pauseBtn = TaoUi.MakeGlassBtn(this, IconKind.Pause, 20, TogglePauseBtn);
            bottomRow.AddView(_pauseBtn, new LinearLayout.LayoutParams(TaoUi.Dp(this, 40), TaoUi.Dp(this, 40)) { LeftMargin = TaoUi.Dp(this, 10), RightMargin = TaoUi.Dp(this, 10) });
            Mk(IconKind.Next, () => _host.PlayNext());
            // v3.20.7 状态字不在画面中间晃, 紧挨左侧三个按钮靠左; 上下两行小字垂直居中
            var midStatus = new LinearLayout(this) { Orientation = Orientation.Vertical };
            midStatus.SetGravity(GravityFlags.CenterVertical | GravityFlags.Left);
            // v3.25.9 可读性修: 状态小字弃用主题主字色——水墨/天灰主题下是深墨字, 压在视频画面上看不清;
            // 播放页叠层恒用亮白 + 半透明黑描影, 任何主题、任何亮度视频都清晰
            _aiStatusFps = new TextView(this) { Text = "", TextSize = 11, Gravity = GravityFlags.Left };
            _aiStatusFps.SetMaxLines(1);
            _aiStatusFps.SetTextColor(new global::Android.Graphics.Color(255, 255, 255));
            _aiStatusFps.SetShadowLayer(4f, 0f, 1f, global::Android.Graphics.Color.Argb(153, 0, 0, 0));
            midStatus.AddView(_aiStatusFps, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _aiStatusRes = new TextView(this) { Text = "", TextSize = 11, Gravity = GravityFlags.Left };
            _aiStatusRes.SetMaxLines(1);
            _aiStatusRes.SetTextColor(new global::Android.Graphics.Color(255, 255, 255));
            _aiStatusRes.SetShadowLayer(4f, 0f, 1f, global::Android.Graphics.Color.Argb(153, 0, 0, 0));
            midStatus.AddView(_aiStatusRes, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            // v3.25.23 智能字幕状态行: 下载进度/识别中/已挂载如实上屏, 样式同上两行 (亮白+描影)
            _smartStatus = new TextView(this) { Text = "", TextSize = 11, Gravity = GravityFlags.Left };
            _smartStatus.SetMaxLines(1);
            _smartStatus.SetTextColor(new global::Android.Graphics.Color(255, 255, 255));
            _smartStatus.SetShadowLayer(4f, 0f, 1f, global::Android.Graphics.Color.Argb(153, 0, 0, 0));
            midStatus.AddView(_smartStatus, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            bottomRow.AddView(midStatus, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f) { LeftMargin = TaoUi.Dp(this, 6), RightMargin = TaoUi.Dp(this, 6) });
            _speedBtn = new TextView(this) { Text = SpeedLabel(), TextSize = 15, Gravity = GravityFlags.Center };
            // v3.25.18 可读性修: 倍速钮同状态小字口径——恒亮白+半透明黑描影, 亮色画面上不再隐形 (真机横屏反馈)
            _speedBtn.SetTextColor(new global::Android.Graphics.Color(255, 255, 255));
            _speedBtn.SetShadowLayer(4f, 0f, 1f, global::Android.Graphics.Color.Argb(153, 0, 0, 0));
            _speedBtn.SetPadding(TaoUi.Dp(this, 10), 0, TaoUi.Dp(this, 10), 0);
            _speedBtn.Click += (s, e) => ShowSpeedDialog();
            bottomRow.AddView(_speedBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            // v3.25.20 大字体适配: 固定 40dp 高在 1.3 倍字体下会截字, 改最小高 40dp + 自然撑开
            _speedBtn.SetMinHeight(TaoUi.Dp(this, 40));

            controls.AddView(bottomRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(this, 4) });

            // v3.25.17: 横屏顶栏与底栏逐像素等高——底栏尺寸一变(状态小字显隐/转屏/进出场)即重算顶栏高度;
            // 竖屏恢复自然高度。转屏落定时 OnConfigurationChanged 也主动重算一次, 双保险。
            controls.LayoutChange += (s, e) => SyncTopBarHeight();

            _playerPanel.AddView(controls, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));

            // v3.18.6 拖动进度预览浮层: 控制条之上、解锁钮之下, 拖动中才可见
            BuildSeekPreview();
            _playerPanel.AddView(_seekPreview!, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.CenterHorizontal));
            if (!_seekPreviewWired)
            {
                _seekPreviewWired = true;
                _host.CurrentPathChanged += _ => ReleaseSeekPreviewResources();   // 换片: 清缓存 + 重绑取帧器
            }
            // v3.25.1: 换片即探内容帧率 (探到后重申帧率声明, 画面挂稳前后双保险)
            if (!_fpsProbeWired)
            {
                _fpsProbeWired = true;
                _host.CurrentPathChanged += path =>
                {
                    _contentFps = ProbeContentFps(path);
                    if (_contentFps > 0) ApplyDisplayBoost();
                };
            }

            // 解锁按钮在控制条之后挂载 (z 最高): 锁定时控制条/顶栏都隐藏, 只有它能点
            // v3.22.4 解锁钮改画面正中央 (老板全盘设计: 锁定后点屏幕短暂亮起, 2.5 秒自动消失)
            _playerPanel.AddView(_unlockBtn, new FrameLayout.LayoutParams(TaoUi.Dp(this, 56), TaoUi.Dp(this, 56), GravityFlags.Center));

            _root.AddView(_playerPanel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _playerPanel.Visibility = ViewStates.Gone;

            // 播放内核只在首次构建时初始化; 主题原位重绘时只重挂画面出口 (v3.13)
            if (_hostInit) _host.AttachView(_carrier);
            else
            {
                _host.Init(_carrier);
                _hostInit = true;
                // v3.22.1 (0004 复验修正): 首帧落地清标订阅只在首建挂一次 (防主题重绘重复订阅, P2-8)。
                // 清标与 PlayPath 每次打标配对——「每次起播都自检」: 首帧清掉, 进程死在首帧前则下次启动回退
                _host.TimeUpdated += (_, _) => { MarkCarrierOk(); TryRevealOpening(); };
            }

            // v3.17.0 用户定（2026-11-07）：播放页强行用满设备最高刷新率 + 走系统视频优化通道
            //（画面挂稳前后各应用一次, 双保险都拿到 Surface）
            ApplyDisplayBoost();
            _carrier.AsView.PostDelayed(new Java.Lang.Runnable(ApplyDisplayBoost), 1500);

            // 缓存(边播边存)完成 -> 自动无损转成 MP4, 各种播放器/剪辑工具直接能用 (只挂一次)
            if (!_recordWired)
            {
                _recordWired = true;
                _host.RecordedFileCompleted += ts => TsRemuxer.RemuxToMp4Async(ts, (ok, path, err) => RunOnUiThread(() =>
                {
                    var name = System.IO.Path.GetFileName(path);
                    if (ok) TaoUi.Toast(this, $"缓存已转成 MP4: {name}", long_: true);
                    else TaoUi.Toast(this, $"转 MP4 没成 ({err}), 先保留原始 TS 文件, 可以重试", long_: true);
                }));
            }

            // 恢复上次记忆的倍速
            var savedRate = (float)AppSettings.Current.PlaybackRate;
            if (savedRate > 0 && Math.Abs(savedRate - 1f) > 0.01f)
            {
                _currentRate = savedRate;
                _host.SetRate(savedRate);
                _speedBtn!.Text = SpeedLabel();   // v3.13 倍速钮同步显示记忆倍率
            }
        }

        // ================= v3.18.6 拖动进度条预览 =================

        /// <summary>构建拖动预览浮层: 预览图 + 时间, 深色圆角, 默认隐藏</summary>
        private void BuildSeekPreview()
        {
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetGravity(GravityFlags.Center);
            var bg = new GradientDrawable();
            bg.SetCornerRadius(TaoUi.Dp(this, 10));
            bg.SetColor(Color.Argb(215, 0, 0, 0));
            panel.Background = bg;
            panel.SetPadding(TaoUi.Dp(this, 5), TaoUi.Dp(this, 5), TaoUi.Dp(this, 5), TaoUi.Dp(this, 5));
            panel.Visibility = ViewStates.Gone;

            var img = new ImageView(this);
            img.SetScaleType(ImageView.ScaleType.FitXy);
            panel.AddView(img, new LinearLayout.LayoutParams(TaoUi.Dp(this, 160), TaoUi.Dp(this, 90)));

            var time = new TextView(this) { TextSize = 13, Gravity = GravityFlags.Center };
            time.SetTextColor(Color.White);
            panel.AddView(time, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(this, 2) });

            _seekPreview = panel;
            _seekPrevImg = img;
            _seekPrevTime = time;
        }

        /// <summary>拖动中: 更新时间文本, 秒级缓存命中即贴图; 未命中按 200ms 节流后台抽帧。
        /// magnet:/网络流/路径失效只显时间不出图。</summary>
        private void UpdateSeekPreview(long ms)
        {
            if (_seekPreview == null || _seekPrevImg == null || _seekPrevTime == null) return;
            _seekPrevTime.Text = FormatMs(ms);
            if (_seekPreview.Visibility != ViewStates.Visible)
            {
                var lp = (FrameLayout.LayoutParams)_seekPreview.LayoutParameters!;
                lp.BottomMargin = (_controls?.Height ?? 0) + TaoUi.Dp(this, 10);   // 悬在控制条上方
                _seekPreview.Visibility = ViewStates.Visible;
            }

            var path = _host.CurrentPath;
            // v3.18.7: 本地文件 + 本地流服务(magnet 播放走 http://127.0.0.1)都可取帧预览; 其他外链只显时间
            if (string.IsNullOrEmpty(path)) { _seekPrevImg.Visibility = ViewStates.Gone; return; }
            var isLocal = path[0] == '/';
            var isLocalStream = path.StartsWith("http://127.0.0.1");
            if (!isLocal && !isLocalStream)
            {
                _seekPrevImg.Visibility = ViewStates.Gone;
                return;
            }
            _seekPrevImg.Visibility = ViewStates.Visible;

            var sec = (int)(ms / 1000);
            lock (_seekFrames)
            {
                if (_seekFrames.TryGetValue(sec, out var hit))
                {
                    if (hit != null) _seekPrevImg.SetImageBitmap(hit);
                    else _seekPrevImg.SetImageBitmap(null);   // 负缓存: 该秒取不出帧, 时间照显
                    return;
                }
            }
            var now = System.Environment.TickCount64;
            if (now - _seekLastFetchMs < 200) return;   // 抽帧节流
            _seekLastFetchMs = now;
            var imgRef = _seekPrevImg;
            System.Threading.Tasks.Task.Run(() =>
            {
                var bmp = GetSeekFrame(path, sec);
                lock (_seekFrames)
                {
                    if (_seekFrames.Count >= 120) _seekFrames.Clear();   // 封顶防涨内存
                    _seekFrames[sec] = bmp;
                }
                RunOnUiThread(() =>
                {
                    // 只在仍在拖动且没换片时回贴, 防旧帧盖住新位置
                    if (_seekDragging && imgRef.IsAttachedToWindow && _host.CurrentPath == path && bmp != null)
                        imgRef.SetImageBitmap(bmp);
                });
            });
        }

        /// <summary>就近取帧: API 27+ 按目标尺寸缩放取 (ClosestSync 寻址最快), 低版本整帧取</summary>
        private Bitmap? GetSeekFrame(string path, int sec)
        {
            try
            {
                lock (_seekRetrLock)
                {
                    if (_seekRetr == null || _seekRetrPath != path)
                    {
                        try { _seekRetr?.Release(); } catch { /* 释放失败换新实例即可 */ }
                        _seekRetr = new global::Android.Media.MediaMetadataRetriever();
                        _seekRetr.SetDataSource(path);
                        _seekRetrPath = path;
                    }
                    long us = sec * 1000000L;
                    if (OperatingSystem.IsAndroidVersionAtLeast(27))
                        return _seekRetr.GetScaledFrameAtTime(us, global::Android.Media.Option.ClosestSync, TaoUi.Dp(this, 160), TaoUi.Dp(this, 90));
                    return _seekRetr.GetFrameAtTime(us, global::Android.Media.Option.ClosestSync);
                }
            }
            catch
            {
                return null;   // 取帧失败走负缓存, 时间照显
            }
        }

        /// <summary>换片/退出时清预览缓存并释放取帧器 (线程安全)</summary>
        internal void ReleaseSeekPreviewResources()
        {
            lock (_seekFrames) { _seekFrames.Clear(); }
            lock (_seekRetrLock)
            {
                try { _seekRetr?.Release(); } catch { /* 兜底: 置空即弃 */ }
                _seekRetr = null;
                _seekRetrPath = null;
            }
        }

        /// <summary>播放页压暗底 (视频上的浮层), 随主题微调保持字色可辨</summary>
        private static Color Scrim(int alpha = 120) => Color.Argb(alpha, 0, 0, 0);

        // ================= 播放页切换 =================

        /// <summary>v3.12 旋转模式统一出口: 全屏必横屏; 非全屏按「自动旋转」设置, 开=陀螺仪接管(全屏钮收起), 关=竖屏持稳(全屏钮手动切)
        /// v3.25.20: 分屏模式下跳过强制方向——系统会忽略或触发兼容黑边, 方向交还给分屏自己管。</summary>
        private void ApplyRotationMode()
        {
            if (!InSplitScreen)
                RequestedOrientation = _fullscreen
                    ? ScreenOrientation.SensorLandscape
                    : (AppSettings.Current.AutoRotate ? ScreenOrientation.FullSensor : ScreenOrientation.Portrait);
            if (_fullscreenBtn != null)
                _fullscreenBtn.Visibility = AppSettings.Current.AutoRotate ? ViewStates.Gone : ViewStates.Visible;
            ApplySystemBars();   // v3.25.17: 系统栏收放并入转屏统一出口, 自动转与手动全屏同口径
        }

        /// <summary>v3.25.20: 是否处于分屏/多窗口模式 (系统方向强制会失效或打架, 一律跳过)。</summary>
        private bool InSplitScreen =>
            OperatingSystem.IsAndroidVersionAtLeast(24) && IsInMultiWindowMode;

        /// <summary>v3.25.20: 进出分屏时按当前模式重算方向, 出分屏恢复强制方向、进分屏交还系统。</summary>
        public override void OnMultiWindowModeChanged(bool isInMultiWindowMode, global::Android.Content.Res.Configuration? newConfig)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) base.OnMultiWindowModeChanged(isInMultiWindowMode, newConfig);
            if (_inPlayer) ApplyRotationMode();
        }

        /// <summary>v3.25.18 顶栏安全垫按横竖屏分算: 横屏播放状态栏已收净、挖孔在侧边, 顶部安全垫归零——
        /// 否则设备上报的大安全垫叠加等高压顶后, 内容区放不下 40dp 圆钮, 按钮被上下裁成一条 (118 真机红线反馈);
        /// 竖屏沿用状态栏/挖孔取大口径。转屏落定后重算一次, 建顶栏时也走这里。
        /// v3.25.19: 横屏左右内边距计入侧边挖孔, 返回钮不再有压开孔风险。</summary>
        private void ApplyTopBarPadding(LinearLayout topBar)
        {
            var land = (int)Resources!.Configuration!.Orientation == (int)global::Android.Content.Res.Orientation.Landscape;
            int safeTop = land ? 0 : Model.TopBarPolicy.SafeTopPx(TaoUi.StatusBarHeight(this), TaoUi.TopCutoutInset(this));
            var (padTop, padBottom) = Model.TopBarPolicy.Padding(safeTop, TaoUi.Dp(this, 12));
            padTop -= TaoUi.Dp(this, 10);   // v3.25.17: 按钮整排上移10dp, 圆心对齐文字中心; 遮罩底线随行上移, 按钮下留白12dp不变
            int side = land ? Math.Max(TaoUi.Dp(this, 12), TaoUi.SideCutoutInset(this)) : TaoUi.Dp(this, 12);   // v3.25.19: 横屏左右避让挖孔
            topBar.SetPadding(side, padTop, side, padBottom);
        }

        /// <summary>v3.25.19 底部控制条安全垫按横竖屏分算: 竖屏垫导航栏高度, 三键导航机型倍速钮/进度条不再贴死;
        /// 横屏全屏系统栏已收净, 维持原 12dp。转屏落定后重算, 建控制条时也走这里。</summary>
        private void ApplyControlsPadding(LinearLayout controls)
        {
            var land = (int)Resources!.Configuration!.Orientation == (int)global::Android.Content.Res.Orientation.Landscape;
            int padBottom = TaoUi.Dp(this, 12) + (land ? 0 : TaoUi.NavBarHeight(this));
            controls.SetPadding(TaoUi.Dp(this, 20), TaoUi.Dp(this, 8), TaoUi.Dp(this, 20), padBottom);
        }

        /// <summary>v3.25.17 横屏顶栏与底栏逐像素等高: 底栏量高, 顶栏重建布局参数跟随(必走重排, 避开旧版等高同步的高度滞后); 竖屏恢复自然高度。</summary>
        private void SyncTopBarHeight()
        {
            if (_playerTopBar == null || _controls == null) return;
            var land = (int)Resources!.Configuration!.Orientation == (int)global::Android.Content.Res.Orientation.Landscape;
            var lp = _playerTopBar.LayoutParameters;
            if (land)
            {
                if (lp == null || lp.Height != _controls.Height)
                    _playerTopBar.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, _controls.Height, GravityFlags.Top);
            }
            else if (lp == null || lp.Height != ViewGroup.LayoutParams.WrapContent)
            {
                _playerTopBar.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top);
            }
        }

        /// <summary>v3.25.17 系统栏统一出口: 播放中横屏(自动转与手动全屏同口径)一律收净状态栏/导航栏, 下拉临时呼出; 竖屏或回主页还原常驻。</summary>
        private void ApplySystemBars()
        {
            if (!_inPlayer) { RestoreSystemBars(); return; }
            var land = (int)Resources!.Configuration!.Orientation == (int)global::Android.Content.Res.Orientation.Landscape;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var ctl = Window!.InsetsController;
                if (ctl == null) return;
                ctl.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                if (land) ctl.Hide(WindowInsets.Type.SystemBars());
                else ctl.Show(WindowInsets.Type.SystemBars());
            }
            else if (land)
            {
                Window!.DecorView!.SystemUiFlags = SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutStable;
            }
            else
            {
                RestoreSystemBars();
            }
        }

        private void RestoreSystemBars()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                Window!.InsetsController?.Show(WindowInsets.Type.SystemBars());
            else
                Window!.DecorView!.SystemUiFlags = (SystemUiFlags)0;
        }

        /// <summary>v3.25.17: 转屏落定后按横竖屏收/还系统栏——自动旋转与手动全屏效果完全一致。</summary>
        public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            ApplySystemBars();
            if (_playerTopBar != null) ApplyTopBarPadding(_playerTopBar);   // v3.25.18: 转屏重算顶栏安全垫, 修横屏按钮被裁
            if (_controls != null) ApplyControlsPadding(_controls);         // v3.25.19: 转屏重算底栏安全垫
            _controls?.Post(() => SyncTopBarHeight());   // v3.25.19: 延到下一帧布局后再量高, 修折叠屏/转屏瞬间读到旧高度
        }

        // v3.25.8 修「本地首帧预览串片」: 起播时序是先亮播放页再换片, ShowPlayer(true) 当下
        // _host.CurrentPath 还停留在上一条视频, 封面按它取缓存就取到上一次打开的视频;
        // 改为起播入口把本次要播的路径带进来, 优先按 openingPath 取图/设标题, 没传才退回 CurrentPath
        internal void ShowPlayer(bool show, string? openingPath = null)
        {
            _inPlayer = show;
            _playerPanel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _homePanel.Visibility = show ? ViewStates.Gone : ViewStates.Visible;
            // v3.16: 播放时接管息屏策略保持常亮, 离开播放页交还系统
            try
            {
                if (show) Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
                else Window!.ClearFlags(WindowManagerFlags.KeepScreenOn);
            }
            catch { }
            if (show)
            {
                _homeHint.Visibility = ViewStates.Gone;
                ApplyRotationMode();
                var cur = openingPath ?? _host.CurrentPath;
                // v3.25.23 智能字幕: 开关开着就对本次要播的片起跑 (缓存秒挂/进行中幂等)
                _smartSub?.Kick(cur);
                _title.Text = cur == null ? "" : System.IO.Path.GetFileName(cur);
                _title.Selected = true;   // v3.25.25 跑马灯: 换片后重置选中态, 超长文件名重新起滚
                // v3.23.1 开场黑场过渡: 进播放页即挂黑场, 控制条布局定型前不画;
                // 视频尺寸定版 (或兜底 4 秒) 后同帧揭幕, 中间态画面与错位控件都不外露
                _openingRevealed = false;
                // v3.25.2 起播封面挡黑: 本地文件且列表已解出首帧缩略图, 黑场期间先垫画面; 远程流维持黑场
                if (_openingThumb != null)
                {
                    var tb = cur != null && cur.StartsWith("/") && _thumbs.TryGetValue(cur, out var t) ? t : null;
                    _openingThumb.SetImageBitmap(tb);
                    _openingThumb.Visibility = tb == null ? ViewStates.Gone : ViewStates.Visible;
                }
                // v3.25.8 每次起播复位封面状态: 揭幕淡出位/高清层归零, 归属路径记本次, 后台解原画级封面
                if (_openingCover != null) _openingCover.Alpha = 1f;
                if (_openingThumbHd != null) { _openingThumbHd.Visibility = ViewStates.Gone; _openingThumbHd.SetImageBitmap(null); }
                _openingThumbPath = cur;
                if (cur != null && cur.StartsWith("/")) PrimeOpeningThumbHd(cur);
                // v3.25.8 封面帧起播: 起播前把预览取帧时间点预置给内核, 无续播时从预览那一帧直接开播
                _host.PrimeCoverStart(cur != null && cur.StartsWith("/") ? GetCoverStartMs(cur) : 0);
                if (_openingCover != null) _openingCover.Visibility = ViewStates.Visible;
                if (_controls != null) _controls.Visibility = ViewStates.Gone;
                if (_openingSafety != null) { _uiHandler.RemoveCallbacks(_openingSafety); _openingSafety.Dispose(); _openingSafety = null; }
                _openingSafety = new Java.Lang.Runnable(RevealOpening);
                _uiHandler.PostDelayed(_openingSafety, 4000);
                // v3.11 修「有声音没画面」: 面板刚显示出来, 等布局落定后把画面出口重挂一次
                _carrier.AsView.Post(() => _host.ReattachVideoView());
                // v3.20.6 增强状态小字定时刷新: 增强跑出规格/档位变化自动上屏 (竖横屏同一套控制条都显示)
                StartAiStatusTicker();
            }
            else
            {
                _aiStatusTicker = false;   // 离开播放页停掉状态字刷新
                // v3.23.1: 收掉开场黑场的兜底揭幕, 黑场随面板退场
                if (_openingSafety != null) { _uiHandler.RemoveCallbacks(_openingSafety); _openingSafety.Dispose(); _openingSafety = null; }
                if (_openingCover != null) _openingCover.Visibility = ViewStates.Gone;
                // 回主页固定竖屏 (修复: 全屏横屏播放时点主页, 主页也横着显示)
                // v3.25.20: 分屏下不强制, 方向交还系统
                if (!InSplitScreen) RequestedOrientation = ScreenOrientation.Portrait;
                RestoreSystemBars();   // v3.25.17: 回主页还原系统栏常驻
                if (_locked) { _locked = false; if (_unlockBtn != null) _unlockBtn.Visibility = ViewStates.Gone; }
                // 离开播放页必须收尾: 存进度 + 停播 (任何路径回列表都不能留着声音)
                SaveTsIfRecording();
                _host.Stop();
                StartScan(); // 回主页刷新一遍
            }
        }

        // ================= 画中画 =================

        internal void EnterPipMode()
        {
            try
            {
                _controls.Visibility = ViewStates.Gone;
                _playerTopBar.Visibility = ViewStates.Gone;
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    var builder = new PictureInPictureParams.Builder();
                    try
                    {
                        // v3.25.19: 按片源宽高比设画中画窗口, 修非 16:9 片源上下留黑边
                        var p = _host.Player;
                        uint w = 0, h = 0;
                        p?.Size(0, ref w, ref h);
                        if (w > 0 && h > 0) builder.SetAspectRatio(new global::Android.Util.Rational((int)w, (int)h));
                    }
                    catch { }   // 尺寸没定版就走系统默认, 不挡进画中画
                    EnterPictureInPictureMode(builder.Build()!);
                }
            }
            catch (Exception ex) { TaoUi.Toast(this, "这台系统不支持画中画: " + ex.Message); }
        }

        public override void OnPictureInPictureModeChanged(bool isInPip, global::Android.Content.Res.Configuration? newConfig)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) base.OnPictureInPictureModeChanged(isInPip, newConfig);
            if (!isInPip && _controls != null)
            {
                _controls.Visibility = ViewStates.Visible;
                _playerTopBar.Visibility = ViewStates.Visible;
                UpdateAiStatusText();   // v3.20.3 顶栏唤出即刷新增强状态
            }
        }

        // ================= 全屏 =================

        internal void ToggleFullscreen()
        {
            _fullscreen = !_fullscreen;
            // 全屏 = 锁定横屏; 退出全屏 = 按自动旋转设置交还 (开=陀螺仪, 关=竖屏)
            ApplyRotationMode();
            var decor = Window!.DecorView!;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                // 30+：系统栏交给 Insets 控制器——全屏=隐藏+下拉临时呼出，退出=常驻显示
                var ctl = Window!.InsetsController;
                if (ctl != null)
                {
                    ctl.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    if (_fullscreen) ctl.Hide(WindowInsets.Type.SystemBars());
                    else ctl.Show(WindowInsets.Type.SystemBars());
                }
            }
            else
            {
                // 30 以下：经典沉浸式旗标（该旗标绑定自 30 起才标记废弃，旧系统按官方口径继续用）
                if (_fullscreen)
                    decor.SystemUiFlags = SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutStable;
                else
                    decor.SystemUiFlags = (SystemUiFlags)0;
            }
            if (_fullscreenBtn is FrameLayout fb) TaoUi.SetGlassIcon(fb, _fullscreen ? IconKind.FullscreenExit : IconKind.Fullscreen);
            TaoUi.Toast(this, _fullscreen ? "已进入全屏" : "已退出全屏");
        }

        // ================= 播放锁定 =================

        /// <summary>锁定 (v3.22.4 老板全盘设计): 收干净全部控件进纯看片态, 锁定保留过切后台;
        /// 解锁键只在点屏幕时短暂亮 2.5 秒; 手势/呼出全失效; 返回键需两次确认防误退。</summary>
        internal void ToggleLock()
        {
            _locked = !_locked;
            if (_locked)
            {
                // v3.25.20: 分屏下不锁方向, 系统会忽略还可能打架
                if (!InSplitScreen) RequestedOrientation = ScreenOrientation.Locked;
                _controls.Visibility = ViewStates.Gone;
                _playerTopBar.Visibility = ViewStates.Gone;   // 锁定键在顶栏内, 随栏一起收
                _gestureHint.Visibility = ViewStates.Gone;
                _centerPlayBtn!.Visibility = ViewStates.Gone;
                if (_seekPreview != null) _seekPreview.Visibility = ViewStates.Gone;
                _unlockBtn!.Visibility = ViewStates.Gone;
                TaoUi.Toast(this, "已锁定");
            }
            else
            {
                ApplyRotationMode(); // 解锁后按自动旋转设置恢复方向
                _unlockBtn!.Visibility = ViewStates.Gone;
                _controls.Visibility = ViewStates.Visible;
                _playerTopBar.Visibility = ViewStates.Visible;
                UpdatePlaybackUi();   // 解锁恢复画面控制与真实播放状态一致
                UpdateAiStatusText();
                ScheduleHide();
                TaoUi.Toast(this, "已解锁");
            }
        }

        /// <summary>锁定态点屏幕: 只在画面正中短暂亮解锁键 (v3.22.4), 2.5 秒无操作自动消失防手滑;
        /// 非锁定态走正常菜单呼出。</summary>
        private void FlashUnlockKey()
        {
            _uiHandler.RemoveCallbacks(_hideUnlockRun!);
            _unlockBtn!.Visibility = ViewStates.Visible;
            _uiHandler.PostDelayed(_hideUnlockRun = new Java.Lang.Runnable(() =>
            {
                if (_locked) _unlockBtn!.Visibility = ViewStates.Gone;
            }), 2500);
        }

        // ================= 手势 =================

        internal void GestureDown(MotionEvent e)
        {
            if (_locked) return;
            _gLastX = e.GetX(); _gLastY = e.GetY();
            _gMode = 0; _gSeekMs = 0;
        }

        internal void GestureScroll(MotionEvent? e1, MotionEvent? e2, float dx, float dy)
        {
            if (_locked || e1 == null || e2 == null) return;
            var w = Math.Max(_playerPanel.Width, 1);
            var h = Math.Max(_playerPanel.Height, 1);
            var totalDx = e2.GetX() - _gLastX;
            var totalDy = e2.GetY() - _gLastY;
            if (_gMode == 0)
                _gMode = Math.Abs(totalDx) > Math.Abs(totalDy) ? 1 : (e1.GetX() < w / 2f ? 2 : 3);

            if (_gMode == 1)          // 水平滑动: 快进/快退 (v3.25.14 老板定档: 横屏一屏宽=10分钟, 竖屏=5分钟)
            {
                var land = (int)Resources!.Configuration!.Orientation == (int)global::Android.Content.Res.Orientation.Landscape;
                float span = land ? 600000f : 300000f;
                _gSeekMs = (long)(totalDx / w * span);
                ShowHint((_gSeekMs >= 0 ? "快进 " : "快退 ") + FormatMs(Math.Abs(_gSeekMs)));
            }
            else if (_gMode == 2)     // 左侧上下滑: 亮度
            {
                var attr = Window!.Attributes!;
                float cur = attr.ScreenBrightness < 0 ? 0.5f : attr.ScreenBrightness;
                var nb = Math.Clamp(cur - totalDy / h, 0.01f, 1f);
                attr.ScreenBrightness = nb;
                Window.Attributes = attr;
                ShowHint($"亮度 {(int)(nb * 100)}%");
            }
            else                      // 右侧上下滑: 音量
            {
                var am = (global::Android.Media.AudioManager)GetSystemService(AudioService)!;
                int max = am.GetStreamMaxVolume(global::Android.Media.Stream.Music);
                int cur = am.GetStreamVolume(global::Android.Media.Stream.Music);
                int step = (int)(-totalDy / h * max);
                int target = Math.Clamp(cur + step, 0, max);
                if (target != cur) am.SetStreamVolume(global::Android.Media.Stream.Music, target, global::Android.Media.VolumeNotificationFlags.ShowUi);
                ShowHint($"音量 {target * 100 / max}%");
            }
        }

        internal void FinishGesture()
        {
            if (_gMode == 1 && _gSeekMs != 0)
            {
                var target = Math.Max(0, _lastPosMs + _gSeekMs);
                _host.SeekTo(target);
                OnSeekChunkAware(_host.CurrentPath, target);   // v3.17: 手势快进同样走 seek 感知
            }
            _gMode = 0;
        }

        // ================= 物理音量键 =================

        // v3.18.2: 物理音量键不再接管, 完全交给系统默认流程——步进与系统自带播放器同款
        // (每按一下±1格, 总格数以机型为准, 常见 15 格), 提示浮层也由系统出, 应用内不再自绘提示。

        // ================= 画面载体自检 (v3.22.0) =================

        /// <summary>纹理载体首帧落地即清自检标记 (0004 复验修正: 去单次守卫, 支持每次起播自检);
        /// 是否写盘由 CarrierPolicy.MarkOk 按 attempt==1 把关, TimeUpdated 每帧进来不落盘。</summary>
        private void MarkCarrierOk()
        {
            if (_carrier is AllPlayerTextureView) CarrierPolicy.MarkOk(this);
        }

        // ================= 开场黑场过渡 (v3.23.1) =================

        /// <summary>进度回执驱动: 视频尺寸还没定版就等下一条回执, 定版后揭幕 (全程幂等)。</summary>
        private void TryRevealOpening()
        {
            if (_openingRevealed) return;
            RunOnUiThread(() =>
            {
                if (_openingRevealed || _openingCover is not { Visibility: ViewStates.Visible }) return;
                try
                {
                    var p = _host.Player;
                    if (p == null) return;
                    uint w = 0, h = 0;
                    p.Size(0, ref w, ref h);
                    if (w == 0 || h == 0) return;   // 尺寸没定版, 下一条进度回执再试
                }
                catch { }
                RevealOpening();
            });
        }

        /// <summary>揭幕: 收兜底定时, 黑场退场, 控制条随画面同帧亮出 (锁定态不亮, 锁定流程自行接管)。
        /// v3.25.8 封面→真实画面改淡出过渡 (220ms), 不再硬切 —— 消除「模糊突然变清晰」的突兀感。</summary>
        private void RevealOpening()
        {
            if (_openingRevealed) return;
            _openingRevealed = true;
            if (_openingSafety != null) { _uiHandler.RemoveCallbacks(_openingSafety); _openingSafety.Dispose(); _openingSafety = null; }
            if (_openingCover != null)
            {
                var cover = _openingCover;
                try
                {
                    cover.Animate()!.Alpha(0f).SetDuration(220)!.WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        cover.Visibility = ViewStates.Gone;
                        cover.Alpha = 1f;                      // 复位淡出位, 下次起播不吞帧
                        _openingThumbHd?.SetImageBitmap(null); // 封面层放掉, 位图交回缓存
                        if (_openingThumbHd != null) _openingThumbHd.Visibility = ViewStates.Gone;
                    }));
                }
                catch { cover.Visibility = ViewStates.Gone; }  // 动画不可用兜底: 退回原硬切
            }
            if (_controls != null && !_locked) _controls.Visibility = ViewStates.Visible;
            ScheduleHide();
        }

        // ================= 开场封面原画化 (v3.25.8) =================

        /// <summary>磁盘高清档键: 与列表小图同目录不同后缀, 互不覆盖。</summary>
        private static string ThumbHdDiskKey(string path) => ThumbKey(path, ThumbLen(path)) + "_hd";

        /// <summary>原画级首帧封面: 列表小图先垫底, 后台解屏幕尺寸的高清帧 (磁盘缓存高清档),
        /// 就绪后淡入换上 —— 过渡期间清晰度渐升, 不再小图硬糊到揭幕。只服务当前要播的片, 换片回图直接弃。</summary>
        internal async void PrimeOpeningThumbHd(string path)
        {
            try
            {
                var bmp = await System.Threading.Tasks.Task.Run(() => TryLoadThumbDiskHd(path));
                if (bmp == null)
                {
                    bmp = await System.Threading.Tasks.Task.Run(() => DecodeThumbHdCore(path));
                    if (bmp != null) SaveThumbDiskHd(path, bmp);
                }
                if (bmp == null) return;
                RunOnUiThread(() =>
                {
                    // 换片了 / 已揭幕 / 封面层已退场: 旧图不再上屏
                    if (_openingRevealed || _openingThumbHd == null || _openingCover == null
                        || _openingCover.Visibility != ViewStates.Visible
                        || _openingThumbPath != path) return;
                    _openingThumbHd.SetImageBitmap(bmp);
                    _openingThumbHd.Alpha = 0f;
                    _openingThumbHd.Visibility = ViewStates.Visible;
                    _openingThumbHd.Animate()!.Alpha(1f).SetDuration(180);
                });
            }
            catch { }
        }

        /// <summary>按屏幕尺寸解首帧 (黑帧免入与列表小图同一套): 逐时间点取平均亮度达标的高清帧。</summary>
        private Bitmap? DecodeThumbHdCore(string path)
        {
            try
            {
                using var retr = new global::Android.Media.MediaMetadataRetriever();
                retr.SetDataSource(path);
                int vw = int.TryParse(retr.ExtractMetadata((int)global::Android.Media.MetadataKey.VideoWidth), out var w) ? w : 0;
                int vh = int.TryParse(retr.ExtractMetadata((int)global::Android.Media.MetadataKey.VideoHeight), out var h) ? h : 0;
                var dm = Resources!.DisplayMetrics!;
                int targetW = Math.Max(dm.WidthPixels, 1), targetH = Math.Max(dm.HeightPixels, 1);
                double cover = (vw > 0 && vh > 0) ? Math.Max((double)targetW / vw, (double)targetH / vh) : 1;
                int wantW = vw > 0 ? Math.Max(1, (int)Math.Round(vw * cover)) : targetW;
                int wantH = vh > 0 ? Math.Max(1, (int)Math.Round(vh * cover)) : targetH;

                int durUs = 0;
                if (int.TryParse(retr.ExtractMetadata((int)global::Android.Media.MetadataKey.Duration), out var durMs) && durMs > 0)
                    durUs = durMs * 1000;
                Bitmap? best = null; double bestY = -1; long chosenUs = -1;
                foreach (var atUs in ThumbStamps(durUs))
                {
                    Bitmap? f = null;
                    if (OperatingSystem.IsAndroidVersionAtLeast(27) && vw > 0 && vh > 0)
                        f = retr.GetScaledFrameAtTime(atUs, global::Android.Media.Option.ClosestSync, wantW, wantH);
                    f ??= retr.GetFrameAtTime(atUs, global::Android.Media.Option.ClosestSync);
                    if (f == null) continue;
                    double y = ThumbMeanLuma(f);
                    if (y > bestY) { best?.Recycle(); best = f; bestY = y; chosenUs = atUs; }
                    else f.Recycle();
                    if (y >= ThumbLumaMin) break;
                }
                if (best != null && chosenUs >= 0) RememberCoverStamp(path, chosenUs);   // v3.25.8 封面帧起播: 同步记账
                return best;
            }
            catch { return null; }
        }

        private Bitmap? TryLoadThumbDiskHd(string path)
        {
            try
            {
                var f = System.IO.Path.Combine(ThumbDir, ThumbHdDiskKey(path) + ".jpg");
                if (!System.IO.File.Exists(f)) return null;
                var bytes = System.IO.File.ReadAllBytes(f);
                return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            }
            catch { return null; }
        }

        private void SaveThumbDiskHd(string path, Bitmap bmp)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ThumbDir);
                using var fs = System.IO.File.Create(System.IO.Path.Combine(ThumbDir, ThumbHdDiskKey(path) + ".jpg"));
                bmp.Compress(Bitmap.CompressFormat.Jpeg!, 90, fs);
                PruneThumbDisk();
            }
            catch { }
        }

        /// <summary>长按倍速: 按住 3x 快进, 松手恢复原速</summary>
        internal void StartRateBoost()
        {
            if (_locked || _boosting || !_host.IsPlaying) return;
            _boosting = true;
            _boostRate = _currentRate;
            _host.SetRate(3f);
            ShowHint("3x 快进中 ▶▶▶", sticky: true);
        }

        internal void EndRateBoost()
        {
            if (!_boosting) return;
            _boosting = false;
            _host.SetRate(_boostRate);
            ShowHint("恢复 " + _boostRate + "x");
        }

        internal void TogglePauseByGesture()
        {
            if (_locked) return;
            if (_host.IsPlaying) { _host.Pause(); _controls.Visibility = ViewStates.Visible; _playerTopBar.Visibility = ViewStates.Visible; UpdateAiStatusText(); ScheduleHide(); }
            else _host.Play();
            UpdatePlaybackUi();   // v3.22.4: 手势双击后按钮同步真实状态
        }

        internal void ToggleControls()
        {
            if (_locked) { FlashUnlockKey(); return; } // v3.22.4: 锁定态点屏幕只短暂亮解锁键, 菜单不呼出
            var v = _controls.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            _controls.Visibility = v;
            _playerTopBar.Visibility = v;
            if (v == ViewStates.Visible) ScheduleHide();
            else if (_hideRun != null) _uiHandler.RemoveCallbacks(_hideRun);
        }

        /// <summary>按钮只反映真实状态 (v3.22.4 老板口径): 任何原因的播放/暂停变化都走这里统一刷——
        /// 播放中=底部暂停键+中间无浮层; 暂停=底部播放键+画面正中亮播放钮。</summary>
        internal void UpdatePlaybackUi()
        {
            bool playing = _host.IsPlaying;
            TaoUi.SetGlassIcon(_pauseBtn!, playing ? IconKind.Pause : IconKind.Play);
            // v3.22.6: 中央浮钮判定收敛到统一规则 (播放中绝不亮; 暂停+在播放器+未锁才亮)
            _centerPlayBtn!.Visibility = Model.PlaybackUiPolicy.CenterPlayVisible(playing, _locked, _inPlayer)
                ? ViewStates.Visible : ViewStates.Gone;
        }

        private void ScheduleHide()
        {
            if (_hideRun != null) _uiHandler.RemoveCallbacks(_hideRun);
            _hideRun = new Java.Lang.Runnable(() =>
            {
                _controls.Visibility = ViewStates.Gone;
                _playerTopBar.Visibility = ViewStates.Gone;
                // v3.23.2: 提示条已自管消失, 这里不再代收
            });
            _uiHandler.PostDelayed(_hideRun, 3500);
        }

        // v3.23.2 (真机反馈): 提示条自管消失——不再寄生控制条的 3.5 秒收起任务
        // (那个任务会被点屏幕收控制条/拖进度条中途摘走, 摘掉后提示条永久滞留);
        // 默认 1.2 秒自动收, 连续刷新重置计时(滑动中不闪断), 长按倍速用 sticky 按住不收。
        private void ShowHint(string text, bool sticky = false, int hideAfterMs = 1200)
        {
            _gestureHint.Text = text;
            _gestureHint.Visibility = ViewStates.Visible;
            if (_hintHideRun != null) { _uiHandler.RemoveCallbacks(_hintHideRun); _hintHideRun.Dispose(); _hintHideRun = null; }
            if (!sticky)
            {
                _hintHideRun = new Java.Lang.Runnable(() => _gestureHint.Visibility = ViewStates.Gone);
                _uiHandler.PostDelayed(_hintHideRun, hideAfterMs);
            }
        }

        private class PlayerGestures : Java.Lang.Object, GestureDetector.IOnGestureListener, GestureDetector.IOnDoubleTapListener
        {
            private readonly MainActivity _a;
            public PlayerGestures(MainActivity a) { _a = a; }
            public bool OnDown(MotionEvent e) { _a.GestureDown(e); return true; }
            public bool OnFling(MotionEvent? e1, MotionEvent? e2, float vx, float vy) => false;
            public void OnLongPress(MotionEvent e) { _a.StartRateBoost(); }
            public bool OnScroll(MotionEvent? e1, MotionEvent? e2, float dx, float dy) { _a.GestureScroll(e1, e2, dx, dy); return true; }
            public bool OnSingleTapUp(MotionEvent e) => true;
            public void OnShowPress(MotionEvent e) { }
            public bool OnSingleTapConfirmed(MotionEvent e) { _a.ToggleControls(); return true; }
            public bool OnDoubleTap(MotionEvent e) { _a.TogglePauseByGesture(); return true; }
            public bool OnDoubleTapEvent(MotionEvent e) => false;
        }

        // ================= 对话框: 倍速 / 更多 / 比例 / 轨道 / 投屏 =================

        private void SaveRate()
        {
            AppSettings.Current.PlaybackRate = _currentRate;
            SaveSettingsSafe();
        }

        /// <summary>倍速: 卡片选项列表</summary>
        internal void ShowSpeedDialog()
        {
            float[] rates = { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 3.0f };
            var labels = new string[rates.Length];
            for (int i = 0; i < rates.Length; i++)
                labels[i] = (Math.Abs(rates[i] - _currentRate) < 0.01f ? "●  " : "○  ")
                    + (rates[i] == 1.0f ? "1.0x (正常)" : rates[i] + "x");
            TaoPopup.ShowListDialog(this, "倍速", labels.Select((t, i) => (t, (Action)(() =>
            {
                _currentRate = rates[i];
                _host.SetRate(_currentRate);
                SaveRate();
                _speedBtn!.Text = SpeedLabel();   // v3.13 倍速钮同步显示实际倍率
                TaoUi.Toast(this, $"倍速: {_currentRate}x (已记住)");
            }))).ToArray());
        }

        /// <summary>播放暂停圆钮: 播放/暂停切换并同步图标; v3.22.4 状态统一走 UpdatePlaybackUi</summary>
        internal void TogglePauseBtn()
        {
            if (_host.IsPlaying) _host.Pause();
            else _host.Play();
            UpdatePlaybackUi();
        }

        /// <summary>更多菜单: 低频播放选项集中在这里, 控制条保持清爽; 卡片在更多钮上方右对齐唤起。
        /// v3.20.3: 效果预览卡片移除——改为顶栏透明文字状态 (用户反馈卡片占地方且看不懂)。</summary>
        internal void ShowMoreMenu()
        {
            string[] items =
            {
                $"循环播放: {(_host.LoopEnabled ? "开" : "关")}",
                $"自动旋转: {(AppSettings.Current.AutoRotate ? "开" : "关")}",
                $"画质增强（实验）: {(AppSettings.Current.AiAutoGear ? "智能自动" : new[] { "关", "低档", "中档", "高档" }[Math.Clamp(AppSettings.Current.AiEnhanceTier, 0, 3)])}",
                $"画面比例: {new[] { "跟随片源", "16:9", "4:3" }[_aspectIdx]}",
                "字幕轨道",
                "音轨",
                "画中画",
                $"智能字幕: {(AppSettings.Current.SmartSubtitleEnabled ? "开" : "关")}",
                "实时增强基准",
            };
            TaoPopup.ShowMenu(this, _playerMoreBtn!, items.Select(t => (t, (Action)(() =>
            {
                if (t.StartsWith("循环播放")) { _host.LoopEnabled = !_host.LoopEnabled; TaoUi.Toast(this, _host.LoopEnabled ? "循环播放: 开" : "循环播放: 关"); }
                else if (t.StartsWith("自动旋转")) { AppSettings.Current.AutoRotate = !AppSettings.Current.AutoRotate; SaveSettingsSafe(); ApplyRotationMode(); TaoUi.Toast(this, AppSettings.Current.AutoRotate ? "自动旋转: 开 (陀螺仪接管, 全屏钮收起)" : "自动旋转: 关 (竖屏持稳, 全屏钮手动切)"); }
                else if (t.StartsWith("画质增强")) { ShowAiEnhanceDialog(); }
                else if (t.StartsWith("画面比例")) ShowAspectDialog();
                else if (t == "字幕轨道") ShowTrackDialog(true);
                else if (t == "音轨") ShowTrackDialog(false);
                else if (t.StartsWith("智能字幕"))
                {
                    // v3.25.23 智能字幕开关: 开启且在播立即起跑 (无模型先下载, 进度上屏); 关闭即停
                    AppSettings.Current.SmartSubtitleEnabled = !AppSettings.Current.SmartSubtitleEnabled;
                    SaveSettingsSafe();
                    if (AppSettings.Current.SmartSubtitleEnabled) _smartSub?.Kick();
                    else _smartSub?.Cancel();
                    TaoUi.Toast(this, AppSettings.Current.SmartSubtitleEnabled ? "智能字幕: 开 (首次使用自动下载识别模型)" : "智能字幕: 关");
                }
                else if (t == "实时增强基准") RunRealtimeBenchEntry();
                else EnterPipMode();
            }))).ToArray(), belowLeft: true, title: "更多");   // v3.13 投屏/倍速升级为专用按钮, 不再占菜单
        }

        /// <summary>底栏中间状态小字 (v3.20.6): 每个增强不跑的断点如实亮出来——装模型中/磁力流/格式不支持/
        /// 处理中未出规格/规格已出; 文案走 AiStatusText 纯逻辑。竖横屏同一套控制条, 都会显示。
        /// v3.21.1: 接入协调器任务状态——排队/处理中/暂停/失败如实上屏, 换片后不再误显示为待机。</summary>
        internal void UpdateAiStatusText()
        {
            if (_aiStatusFps == null || _aiStatusRes == null) return;
            var s = AppSettings.Current;
            string? cur = _host.CurrentPath;
            bool isMp4 = false, magnet = false;
            try { isMp4 = cur != null && AiSegmentRunner.IsMp4Container(cur); } catch { }
            try { magnet = cur != null && (cur.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || IsMagnetActive(cur)); } catch { }

            // v3.21.1: 查当前播放视频在协调器里的任务状态 (整条链路); 分片任务另看 _chunkMan
            var taskState = AiEnhanceState.Idle;
            var progress = (Done: 0, Total: 0);
            var coord = _coordinator;
            if (coord != null && cur != null)
            {
                string curNorm;
                try { curNorm = Normalize(cur); } catch { curNorm = cur ?? ""; }
                var coordCur = coord.CurrentPath;
                if (coordCur != null && string.Equals(Normalize(coordCur), curNorm, StringComparison.OrdinalIgnoreCase))
                {
                    taskState = coord.State;
                    progress = coord.Progress;
                }
                else if (coord.IsQueued(cur))
                {
                    taskState = AiEnhanceState.Queued;
                }
            }
            else if (_chunkMan != null && _chunkSrcPath != null && cur != null &&
                     string.Equals(Normalize(_chunkSrcPath), Normalize(cur), StringComparison.OrdinalIgnoreCase) &&
                     _chunkQ != null && _chunkQ.Count > 0)
            {
                // 分段续存链路: 有待算段即在处理中 (规格未出时用户才需要这条提示)
                taskState = AiEnhanceState.Running;
            }

            var lines = AiStatusText.Compose(
                s.AiEnhanceTier, s.AiAutoGear,
                AiSegmentRunner.LatestSpecValid ? AiSegmentRunner.LatestSpec : default,   // v3.20.9: 换片后旧片规格不作数
                kernelReady: _coordinator != null,
                magnetActive: magnet, isMp4: isMp4,
                taskState: taskState, progress: progress,
                screenMaxHz: DeviceSpecProbe.MaxHz);   // v3.25.9: 输出不封顶, 超出屏幕能力时标注「屏幕可显示≤N」
            _aiStatusFps.Text = lines.Fps;
            _aiStatusRes.Text = lines.Res;
        }

        private bool _aiStatusTicker;   // 控制条可见时定时刷新状态字 (增强跑出规格/档位变化都能自动上屏)

        private void StartAiStatusTicker()
        {
            if (_aiStatusTicker) return;
            _aiStatusTicker = true;
            RunAiStatusTick();
        }

        private void RunAiStatusTick()
        {
            if (!_aiStatusTicker) return;
            try
            {
                if (_inPlayer && _controls != null && _controls.Visibility == ViewStates.Visible) UpdateAiStatusText();
            }
            catch { }
            _carrier?.AsView.PostDelayed(new Java.Lang.Runnable(RunAiStatusTick), 2000);
        }


        /// <summary>倍速钮文案: 正常速显示「倍速」, 非常速显示实际倍率</summary>
        private string SpeedLabel() => Math.Abs(_currentRate - 1f) < 0.01f ? "倍速" : $"{_currentRate:0.##}x";

        /// <summary>v3.25.1: 探视频内容真实帧率 (本地文件走 MediaExtractor; 探不到返回 0, 调用方跳过声明)</summary>
        private static float ProbeContentFps(string? path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            try
            {
                using var ex = new global::Android.Media.MediaExtractor();
                ex.SetDataSource(path);
                for (int i = 0; i < ex.TrackCount; i++)
                {
                    var f = ex.GetTrackFormat(i);
                    var mime = f.GetString(global::Android.Media.MediaFormat.KeyMime);
                    if (mime != null && mime.StartsWith("video/"))
                    {
                        float fps = 0;
                        try { fps = f.GetFloat(global::Android.Media.MediaFormat.KeyFrameRate); } catch { }
                        return fps > 0 && fps <= 480 ? fps : 0;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>v3.17.0 用户定（2026-11-07）：播放页强行用满设备支持的最高帧率 + 走系统视频优化通道。
        /// ① preferredDisplayModeId 锁最高刷新显示模式；② Surface.setFrameRate(FIXED_SOURCE)
        /// 向系统声明视频内容（厂商视频画质增强/MEMC 类能力由此介入）；③ 持续性能模式避免长播降频。
        /// 三步各自兜底，任何一步失败不影响播放。
        /// </summary>
        private void ApplyDisplayBoost()
        {
            try
            {
                var attrs = Window!.Attributes!;
                float hz = 0;
                try
                {
                    // 反射取 getSupportedModes()（绑定差异，编译期不依赖；逐元素反射取刷新率与模式 id）
                    var display = WindowManager?.DefaultDisplay;
                    var modesObj = display?.Class?.GetMethod("getSupportedModes")?.Invoke(display, null);
                    if (modesObj != null)
                    {
                        int n = global::Java.Lang.Reflect.Array.GetLength(modesObj);
                        float bestHz = 0; int bestId = 0;
                        for (int i = 0; i < n; i++)
                        {
                            var m = global::Java.Lang.Reflect.Array.Get(modesObj, i);
                            if (m == null) continue;   // 反射取元素可能为空：跳过该模式
                            try
                            {
                                var getHz = m.Class?.GetMethod("getRefreshRate");
                                var getId = m.Class?.GetMethod("getModeId");
                                if (getHz != null && getId != null)
                                {
                                    float r = (float)(global::Java.Lang.Float)getHz.Invoke(m, null)!;
                                    int id = (int)(global::Java.Lang.Integer)getId.Invoke(m, null)!;
                                    if (r > bestHz) { bestHz = r; bestId = id; }
                                }
                            }
                            catch { }
                        }
                        if (bestHz > 0)
                        {
                            hz = bestHz;
                            attrs.PreferredDisplayModeId = bestId;
                        }
                    }
                }
                catch { }
                Window.Attributes = attrs;          // 触发 relayout 生效
                if (OperatingSystem.IsAndroidVersionAtLeast(24)) Window.SetSustainedPerformanceMode(true);
                // v3.22.7: 色彩按设备能力启用+安全回退——探得广色域就开 (ColorModeWideGamut), 探得 HDR 如实记录;
                // 任何一步失败不影响播放, 生效档位落日志
                try
                {
                    var disp = WindowManager?.DefaultDisplay;
                    // 反射探测 (绑定差异): HDR 类型 + 广色域
                    bool hdr = false, wide = false;
                    try
                    {
                        var hdrObj = disp?.GetType().GetMethod("getHdrCapabilities")?.Invoke(disp, null);
                        if (hdrObj != null)
                        {
                            var types = hdrObj.GetType().GetMethod("getSupportedHdrTypes")?.Invoke(hdrObj, null) as int[];
                            hdr = types != null && types.Length > 0;
                        }
                    }
                    catch { }
                    wide = IsWideColorGamutSupported(disp);
                    // 广色域: Activity.setColorMode(1=wideGamut, API 26+), 反射兼容绑定差异
                    if (wide && (int)global::Android.OS.Build.VERSION.SdkInt >= 26)
                    {
                        var setColorMode = GetType().GetMethod("setColorMode", new[] { typeof(int) });
                        setColorMode?.Invoke(this, new object[] { 1 });
                    }
                    string dolby = Model.DeviceCapabilityPolicy.DolbyVisionFallback(DolbyVisionSupported(), hdr);
                    string line = Model.DeviceCapabilityPolicy.CapabilityLine(hz,
                        Model.DeviceCapabilityPolicy.ColorMode(hdr, wide, contentHdr: true), dolby);
                    AppLog.W("设备能力 " + line);
                }
                catch (Exception capEx) { AppLog.W("设备能力探测降级: " + capEx.GetType().Name); }
                // 系统视频优化通道：setFrameRate(FIXED_SOURCE=1) 向系统声明"视频内容帧率" (API 30+)
                // v3.25.1: 语义修正——旧写法把屏幕最高刷新率 (165) 当内容帧率声明, 系统按 165Hz 内容处理反而可能降档;
                // 现在只声明探测到的真实内容帧率, 探不到就跳过 (显示模式锁定不受影响)
                if (OperatingSystem.IsAndroidVersionAtLeast(30) && _contentFps > 0)
                {
                    var surface = _carrier?.HolderSurface;
                    if (surface != null)
                    {
                        surface.SetFrameRate(_contentFps, 1);
                        AppLog.W($"帧率: 显示模式锁 {hz:0.##}Hz, 内容声明 {_contentFps:0.##}fps (FIXED_SOURCE)");
                    }
                }
                else
                {
                    AppLog.W($"帧率: 显示模式锁 {hz:0.##}Hz, 内容帧率未探到 ({_contentFps:0.##}), 跳过 setFrameRate 声明");
                }
            }
            catch { }
        }
        /// <summary>v3.22.7: 设备广色域支持探测 (Display.isWideColorGamut, API 26+, 反射兼容绑定差异)。</summary>
        private static bool IsWideColorGamutSupported(object? display)
        {
            if (display == null || (int)global::Android.OS.Build.VERSION.SdkInt < 26) return false;
            try
            {
                var m = display.GetType().GetMethod("isWideColorGamut") ?? display.GetType().GetMethod("IsWideColorGamut");
                return m != null && (bool)m.Invoke(display, Array.Empty<object>())!;
            }
            catch { return false; }
        }

        /// <summary>v3.22.7: 杜比视界解码支持探测 (MediaCodecList 里找 dolby 解码器, 反射兼容绑定差异)。</summary>
        private static bool DolbyVisionSupported()
        {
            try
            {
                var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.AllCodecs);
                var getInfos = list.GetType().GetMethod("GetCodecInfos") ?? list.GetType().GetMethod("getCodecInfos");
                if (getInfos?.Invoke(list, null) is global::Android.Media.MediaCodecInfo[] infos)
                    foreach (var info in infos)
                        if (info.Name?.Contains("dolby", StringComparison.OrdinalIgnoreCase) == true) return true;
            }
            catch { }
            return false;
        }

        /// <summary>画面比例选择: 原片宽高比异常时手动纠正; 卡片列表</summary>
        private void ShowAspectDialog()
        {
            var names = new[] { "跟随片源", "16:9", "4:3" };
            var labels = names.Select((n, i) => (i == _aspectIdx ? "●  " : "○  ") + n).ToArray();
            TaoPopup.ShowListDialog(this, "画面比例", labels.Select((t, i) => (t, (Action)(() =>
            {
                _aspectIdx = i;
                _host.SetAspectRatio(i == 1 ? "16:9" : i == 2 ? "4:3" : null);
            }))).ToArray());
        }

        internal void ShowTrackDialog(bool subtitle)
        {
            var count = subtitle ? _host.SpuTrackCount : _host.AudioTrackCount;
            if (count <= 0) { TaoUi.Toast(this, subtitle ? "这个视频没有字幕轨道" : "这个视频没有多音轨"); return; }
            var labels = new string[count + 1];
            labels[0] = "关闭";
            for (int i = 0; i < count; i++) labels[i + 1] = $"轨道 {i}";
            TaoPopup.ShowListDialog(this, subtitle ? "字幕轨道" : "音轨", labels.Select((t, i) => (t, (Action)(() =>
            {
                if (subtitle) _host.SpuTrack = i - 1;
                else _host.AudioTrack = i - 1;
            }))).ToArray());
        }

        // ===== 投屏 =====

        private CastDevice? _lastCastDevice;
        private LanFileServer? _lanServer;
        private global::Android.Net.Wifi.WifiManager.MulticastLock? _mcastLock;

        /// <summary>组播需要 WiFi 组播锁, 否则部分路由器下搜不到电视</summary>
        private void AcquireMulticastLock()
        {
            try
            {
                if (_mcastLock != null) return;
                var wifi = (global::Android.Net.Wifi.WifiManager)GetSystemService(WifiService)!;
                var lck = wifi.CreateMulticastLock("taoplay-cast");
                if (lck == null) return;
                lck.SetReferenceCounted(false);
                lck.Acquire();
                _mcastLock = lck;
            }
            catch { }
        }

        /// <summary>投屏: 搜设备 -> 选一台 -> 把当前播放地址原样递给电视 (4K 等高码率不经过手机, 画质无损)</summary>
        private async void ShowCastDialog()
        {
            var path = _host.CurrentPath;
            if (string.IsNullOrEmpty(path))
            {
                TaoUi.Toast(this, "先播放一个视频再投屏");
                return;
            }
            TaoUi.Toast(this, "搜索电视/盒子里… (需与手机同一 Wi-Fi)");
            AcquireMulticastLock();
            var devices = await CastService.DiscoverAsync();
            if (devices.Count == 0)
            {
                TaoPopup.ShowMessage(this, "没有找到可投屏设备",
                    "请检查这两点:\n1. 电视支持投屏 (DLNA), 且已在设置里打开「DLNA/多屏互动」;\n2. 电视和手机连的是同一个 Wi-Fi。\n弄好后点「更多 → 投屏」重试。",
                    new (string, Action?, bool)[] { ("知道了", null, false) });
                return;
            }
            TaoPopup.ShowListDialog(this, "投屏到",
                devices.Select((x, i) => (x.Name, (Action)(() => CastTo(devices[i])))).ToArray());
        }

        private async void CastTo(CastDevice device)
        {
            var path = _host.CurrentPath;
            if (string.IsNullOrEmpty(path)) return;
            string url;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 磁力/m3u8 边下边播走的是手机本机地址, 电视够不着: 换成局域网代理地址给电视
                var lanUrl = _host.MagnetLanStreamUrl ?? _host.HlsLanStreamUrl;
                if ((path.Contains("127.0.0.1") || path.Contains("localhost")) && lanUrl != null)
                    url = lanUrl;
                else
                    url = path; // 网络地址原样直传
            }
            else
            {
                // 本地文件: 临时开个局域网服务给电视拉流
                try
                {
                    _lanServer?.Dispose();
                    _lanServer = new LanFileServer(path);
                    _lanServer.Start();
                    url = _lanServer.LanUrl;
                }
                catch (Exception ex)
                {
                    TaoUi.Toast(this, "投屏没成: " + ex.Message);
                    return;
                }
            }
            var ok = await CastService.CastAsync(device, url, System.IO.Path.GetFileName(path));
            if (ok)
            {
                _lastCastDevice = device;
                StartCastWatch(device);   // v3.23 (P2-2): 投屏成功后开始看守电视端状态
                _host.Pause(); // 手机端暂停, 电视端接管
                RunOnUiThread(() => TaoUi.Toast(this, $"已投屏到 {device.Name}, 电视上正在播放", long_: true));
            }
            else
            {
                TaoUi.Toast(this, $"投屏到 {device.Name} 没成功, 可以重启电视的投屏功能再试一次", long_: true);
            }
        }
    }
}
