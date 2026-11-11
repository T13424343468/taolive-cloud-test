using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using AllPlayer.BLL;
using AllPlayer.Model;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>道映手机版 — 主页 (partial): 品牌头 / 状态 / 搜索 / 磁力条 / 视频列表。</summary>
    public partial class MainActivity
    {
        // ---- 主页控件 ----
        private EditText _searchInput = null!;

        /// <summary>建根布局与主页; 播放页与浮层由另外两个 partial 补齐。</summary>
        private void BuildHome()
        {
            _root = new FrameLayout(this) { Background = new ColorDrawable(TaoTheme.Bg) };

            _homePanel = new LinearLayout(this) { Orientation = Orientation.Vertical, Background = new ColorDrawable(TaoTheme.Bg) };
            _homePanel.SetPadding(TaoUi.Dp(this, 20), Math.Max(TaoUi.StatusBarHeight(this), TaoUi.TopCutoutInset(this)) + TaoUi.Dp(this, 8), TaoUi.Dp(this, 20), 0);   // v3.25.19: 顶部取状态栏与挖孔大者, 挖孔高于状态栏的机型品牌行不再顶进开孔区
            _root.AddView(_homePanel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // ---- 品牌行: 品牌图标 + 软件名 (纯展示) + 右上两钮 (主题/设置, v3.10 其余功能收进设置钮的菜单卡片) ----
            var titleRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            titleRow.SetGravity(GravityFlags.CenterVertical);

            // 品牌钮: 27dp 图标方块套在 48dp 命中区里, 纯展示不可点
            var brandHit = new FrameLayout(this) { Clickable = false };
            var brand = new TextView(this) { Text = "▶", TextSize = 13, Gravity = GravityFlags.Center };
            brand.SetTextColor(TaoTheme.Bg);
            var brandBg = new GradientDrawable();
            brandBg.SetCornerRadius(TaoUi.Dp(this, 14));   // v3.5 ColorOS 圆角方块: 更圆润
            brandBg.SetColor(TaoTheme.Accent);
            brand.Background = brandBg;
            brandHit.AddView(brand, new FrameLayout.LayoutParams(TaoUi.Dp(this, 27), TaoUi.Dp(this, 27), GravityFlags.Center));
            titleRow.AddView(brandHit, TaoUi.Dp(this, 48), TaoUi.Dp(this, 48));

            var title = new TextView(this) { Text = "道映", TextSize = 22, Typeface = Typeface.DefaultBold };
            title.SetTextColor(TaoTheme.TextMain);
            // v3.25.25 跑马灯: 与播放页标题同口径, 超长文字区域内循环滚动不截断
            title.SetSingleLine(true);
            title.Ellipsize = TextUtils.TruncateAt.Marquee;
            title.SetMarqueeRepeatLimit(-1);
            title.Selected = true;
            title.SetPadding(TaoUi.Dp(this, 8), TaoUi.Dp(this, 12), TaoUi.Dp(this, 6), TaoUi.Dp(this, 12));
            titleRow.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            // 右上两钮 (v3.10): 主题 / 设置, 菜单收进设置钮
            AddHeaderIcon(titleRow, IconKind.Theme, CycleTheme, ref _themeBtn);
            AddHeaderIcon(titleRow, IconKind.Gear, ShowHomeMenu, ref _settingsBtn);
            _homePanel.AddView(titleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // ---- r3 直播入口: 一行老电视风入口, 点开直播频道列表 ----
            var liveEntry = new TextView(this) { Text = "📺 直播频道", TextSize = 16, Typeface = Typeface.DefaultBold };
            liveEntry.SetTextColor(TaoTheme.Accent);
            liveEntry.SetPadding(0, TaoUi.Dp(this, 12), 0, TaoUi.Dp(this, 2));
            liveEntry.Click += (s, e) => ShowLivePanel();
            _homePanel.AddView(liveEntry, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // ---- 状态行: 点状态行可重试授权/重扫 ----
            _status = new TextView(this) { Text = "正在准备…", TextSize = 13 };
            _status.SetTextColor(TaoTheme.TextSub);
            _status.SetPadding(0, TaoUi.Dp(this, 8), 0, TaoUi.Dp(this, 4));
            _status.Click += (s, e) => { if (!MediaPermissionGranted) RequestStorageAccess(); else StartScan(); };
            _homePanel.AddView(_status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // ---- 搜索框: 实时过滤媒体库 ----
            _searchInput = TaoUi.MakeInput(this, "搜索视频名称…");
            _searchInput.TextChanged += (s, e) =>
            {
                _filterKw = e.Text?.ToString() ?? "";
                RebuildFiltered();
            };
            _homePanel.AddView(_searchInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(this, 14) });

            // ---- 磁力条 (v3.10): 粘上磁力后自动检测可用性, 可用即进列表, 点开即播 ----
            // v3.24: 同时收 m3u8 地址, 同一列表同一边播边存
            var magnetInput = TaoUi.MakeInput(this, "粘贴磁力链接 (magnet:…) 或 m3u8 地址, 停一秒自动检测");
            _magnetStatus = new TextView(this) { TextSize = 13 };
            _magnetStatus.SetTextColor(TaoTheme.TextSub);
            magnetInput.TextChanged += (s, e) =>
            {
                if (_magnetDebounce != null) _uiHandler.RemoveCallbacks(_magnetDebounce);   // 连续粘贴只认最后一次
                var raw = e.Text?.ToString();
                var t = (raw ?? "").Trim();
                bool isMagnet = t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
                bool isHls = HlsStreamService.IsHlsUrl(t);
                if ((!isMagnet && !isHls) || t.Length < (isHls ? 12 : 30)) return;
                var uri = t;
                _magnetDebounce = new Java.Lang.Runnable(() => StartMagnetAuto(uri));
                _uiHandler.PostDelayed(_magnetDebounce, 1000);
            };

            _homePanel.AddView(magnetInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(this, 14) });
            _homePanel.AddView(_magnetStatus, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(this, 6) });

            // ---- 视频列表 ----
            _adapter = new VideoAdapter(this);
            _list = new ListView(this)
            {
                Adapter = _adapter,
                Divider = new ColorDrawable(TaoTheme.Stroke),
                DividerHeight = 0,   // v3.3 卡片化: 行与行之间用卡片留白分隔, 不再画细线
            };
            // 滚动条与列表线互不干扰: 滚动条画到右侧留白槽里 (v3.12 加宽到 14dp), 不再压住卡片
            _list.ScrollBarStyle = ScrollbarStyles.OutsideOverlay;
            _list.SetPadding(0, 0, TaoUi.Dp(this, 14), 0);
            // v3.7 缩略图提速: 手指拖动/惯性快滑时暂停解码, 停稳再把屏幕里的行补上 —— 滚动不再被解码拖卡
            _list.ScrollStateChanged += (s, e) =>
            {
                _thumbPaused = e.ScrollState != global::Android.Widget.ScrollState.Idle;
                if (!_thumbPaused) RefreshVisibleThumbs();
            };
            // 行点击在 HomeRow 里处理, 列表不再接管单击 (v3.25.22 长按菜单退场)
            _homePanel.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 1f));
        }

        /// <summary>浮层: 主页提示条 / 下载管理面板 (盖在最上层)。</summary>
        private void BuildOverlayPanels()
        {
            // 主页浮动提示条 (snackbar 风格: 挂根布局底部, 不挤进排版流)
            _homeHint = new TextView(this)
            {
                TextSize = 14,
                Visibility = ViewStates.Gone,
                Gravity = GravityFlags.Center,
            };
            _homeHint.SetTextColor(TaoTheme.Bg);
            var hintBg = new GradientDrawable();
            hintBg.SetCornerRadius(TaoUi.Dp(this, 22));
            hintBg.SetColor(TaoTheme.TextMain);
            _homeHint.Background = hintBg;
            _homeHint.SetPadding(TaoUi.Dp(this, 22), TaoUi.Dp(this, 10), TaoUi.Dp(this, 22), TaoUi.Dp(this, 10));
            _homeHint.Elevation = 6;
            _homeHint.Click += (s, e) => _homeHint.Visibility = ViewStates.Gone;   // 提示条点一下就收走
            _root.AddView(_homeHint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.CenterHorizontal)
            { BottomMargin = TaoUi.Dp(this, 90) });

            // 下载管理面板: 盖在最上层 (v3.13 主题原位重绘时复用既有下载服务, 不重复建档/断任务)
            if (_downloads == null)
            {
                var downloadsFolder = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
                _downloads = new DownloadService(
                    System.IO.Path.Combine(GetExternalFilesDir(null)!.AbsolutePath, "downloads.json"),
                    System.IO.Path.Combine(downloadsFolder, "道映"));
                _downloads.Load();
                _downloads.Changed += () => RunOnUiThread(UpdateDownloadLocks);   // v3.25.20: 有任务在下载时持有保持锁, 灭屏不再停滞
            }
            _dlPanel = new DownloadsPanel(this, _downloads);
            _dlPanel.Visibility = ViewStates.Gone;
            _root.AddView(_dlPanel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }

        /// <summary>主页提示: 默认显示 3 秒后自动隐藏, 连续提示时重新计时</summary>
        internal void ShowHomeHint(string? text = null, long ms = 3000)
        {
            if (text != null) _homeHint.Text = text;
            _homeHint.Visibility = ViewStates.Visible;
            if (_homeHintRun != null) _uiHandler.RemoveCallbacks(_homeHintRun);
            _homeHintRun = new Java.Lang.Runnable(() => _homeHint.Visibility = ViewStates.Gone);
            _uiHandler.PostDelayed(_homeHintRun, ms);
        }

        // ================= 右上角图标钮与开关 =================

        /// <summary>当前排列模式对应的图标: 图文=四宫格, 紧凑=三条线 (现在是哪种显示哪种)</summary>
        internal IconKind CurrentSortIcon() => _galleryMode ? IconKind.Grid : IconKind.Sort;

        /// <summary>右上角图标钮 (v3.10 回到大尺寸): 图标 27dp / 命中 48dp, 只剩主题/设置两枚; 随主题取色</summary>
        private void AddHeaderIcon(LinearLayout row, IconKind kind, Action click, ref IconView? slot)
        {
            var btn = TaoUi.MakeIcon(this, kind, 27, TaoTheme.TextMain);
            int pad = TaoUi.Dp(this, (48 - 27) / 2f);
            btn.SetPadding(pad, pad, pad, pad);   // 图标画在 27dp 方寸里, 命中区仍是整格 48dp
            btn.Click += (s, e) => click();
            slot = btn;
            row.AddView(btn, TaoUi.Dp(this, 48), TaoUi.Dp(this, 48));
        }

        /// <summary>最近播放优先开关 (v3.9 从设置上收右上角): 点一下切换, 列表立刻重排</summary>
        private void ToggleRecentFirst()
        {
            var s = AppSettings.Current;
            s.RecentFirst = !s.RecentFirst;
            SaveSettingsSafe();
            RebuildFiltered();
            TaoUi.Toast(this, s.RecentFirst ? "最近播过的排在前面" : "已恢复默认排序");
        }

        /// <summary>排列切换: 图文/紧凑两种列表样式 (v3.10 起入口在设置钮的菜单卡片)</summary>
        private void ToggleGalleryMode()
        {
            _galleryMode = !_galleryMode;
            AppSettings.Current.GalleryMode = _galleryMode;   // v3.18.2 落盘, 重启后保持
            SaveSettingsSafe();
            _adapter.NotifyDataSetChanged();
            TaoUi.Toast(this, _galleryMode ? "图文排列" : "紧凑排列");
        }

        /// <summary>主题切换: 点一下换下一象, 落盘后原位重绘 (v3.13 只重画样式, 不再 Recreate 动列表数据)</summary>
        private void CycleTheme()
        {
            var s = AppSettings.Current;
            string[] themeNames = { "水墨", "天灰", "墨夜" };
            var next = (s.TaoThemeId + 1) % 3;
            s.TaoThemeId = next;
            s.ThemeMode = themeNames[next];
            SaveSettingsSafe();
            TaoTheme.Apply(next);
            ReapplyTheme();
            TaoUi.Toast(this, $"已换{themeNames[next]}象");
        }

        /// <summary>边播边存开关 (v3.10 入口收进设置钮的菜单卡片): 切换后用提示说清现状</summary>
        private void ToggleAutoSave()
        {
            AppSettings.Current.AutoSaveMagnet = !AppSettings.Current.AutoSaveMagnet;
            SaveSettingsSafe();
            TaoUi.Toast(this, AppSettings.Current.AutoSaveMagnet ? "边播边存已打开: 播磁力时顺带存进下载目录" : "边播边存已关闭: 播完不保留文件");
        }

        /// <summary>断线自动重连开关 (v3.23 P0-1): 网络流卡住或中断时自动按退避重试</summary>
        private void ToggleStreamAutoRetry()
        {
            AppSettings.Current.StreamAutoRetry = !AppSettings.Current.StreamAutoRetry;
            SaveSettingsSafe();
            ShowHomeMenu();   // 菜单原地刷新, 开关状态如实
            TaoUi.Toast(this, AppSettings.Current.StreamAutoRetry
                ? "断线自动重连已开: 网络流中断时自动重试"
                : "断线自动重连已关: 中断即停, 不再自动重试");
        }

        /// <summary>流访问令牌开关 (v3.23 P0-2): 本机流地址带会话令牌, 防同网络他人随意拉流</summary>
        private void ToggleStreamToken()
        {
            AppSettings.Current.StreamTokenEnabled = !AppSettings.Current.StreamTokenEnabled;
            SaveSettingsSafe();
            ShowHomeMenu();
            TaoUi.Toast(this, AppSettings.Current.StreamTokenEnabled
                ? "流访问令牌已开: 本机流地址只有带上令牌才能访问"
                : "流访问令牌已关: 本机流地址对同一 Wi-Fi 全开放");
        }

        // ================= 磁力自动检测 (v3.10) =================

        /// <summary>磁力/m3u8 可用性检测: 后台验可用并取名, 通过即进列表; 状态文字实时跟进</summary>
        internal void StartMagnetAuto(string uri)
        {
            bool isHls = HlsStreamService.IsHlsUrl(uri);
            if (_videos.Any(v => v.Path.Equals(uri, StringComparison.OrdinalIgnoreCase)))
            {
                _magnetStatus.Text = isHls ? "这个 m3u8 地址已经在列表里了, 点开即播" : "这个磁力已经在列表里了, 点开即播";
                return;
            }
            if (isHls)
            {
                // v3.24: m3u8 只拉清单验可用, 检测阶段不落盘; 边播边存只在真正播放时生效
                _magnetStatus.Text = "正在检测 m3u8 可用性…";
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var name = await _host.PrepareHlsAsync(uri);
                        RunOnUiThread(() =>
                        {
                            AddMagnetToList(uri, name, 0);
                            _magnetStatus.Text = $"m3u8 可用: {name}, 已加入列表, 点开即播";
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUiThread(() => _magnetStatus.Text = $"m3u8 检测没过: {ex.Message}");
                    }
                });
                return;
            }
            _magnetStatus.Text = "正在检测磁力可用性…";
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // 检测阶段不落盘: 边播边存只在真正播放时生效
                    var (name, size) = await _host.PrepareMagnetAsync(uri, null);
                    RunOnUiThread(() =>
                    {
                        AddMagnetToList(uri, name, size);
                        _magnetStatus.Text = $"磁力可用: {name}, 已加入列表, 点开即播";
                    });
                }
                catch (Exception)
                {
                    RunOnUiThread(() => _magnetStatus.Text = "这个磁力没检测到可用资源, 可能已失效");
                }
            });
        }

        /// <summary>磁力检测通过后插到列表最前面, 名称/大小用检测结果</summary>
        private void AddMagnetToList(string uri, string name, long size)
        {
            _videos.Insert(0, new VideoItem { Path = uri, NameOverride = name, SizeBytes = size });
            RebuildFiltered();
        }

        // ================= 列表与过滤 =================

        /// <summary>按当前搜索词重建过滤列表 (排序设置变化/扫描完成/撤销删除后也走这里)。</summary>
        internal void RebuildFiltered()
        {
            var src = new List<VideoItem>(_videos);
            if (AppSettings.Current.RecentFirst)
                PlaybackHistory.SortRecentFirst(src, AppSettings.Current.Recent);
            _filtered.Clear();
            var kw = _filterKw;
            foreach (var v in src)
                if (kw == "" || v.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    _filtered.Add(v);
            _adapter.NotifyDataSetChanged();
            WarmThumbsInBackground(src);   // v3.16: 数据就绪即后台预热, 首次进列表也命中缓存毫秒级回贴
        }

        // v3.16 预览毫秒级: 已排队预热过的路径 (预热只排一遍, 解码去重/限流在 DecodeThumb 内)
        private readonly System.Collections.Generic.HashSet<string> _thumbWarmed = new();
        private bool _thumbWarmQueued;

        /// <summary>后台全量预热: 列表数据就绪就逐个排队解码落盘缓存 (低优先级线程, 闸门限流+滚动停车不受影响);
        /// 用户滑到哪都有缓存, 首次/多次进列表都毫秒级出图。</summary>
        private void WarmThumbsInBackground(List<VideoItem> snapshot)
        {
            if (_thumbWarmQueued) return;
            var queue = new List<string>();
            foreach (var v in snapshot)
            {
                if (v.Path.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || v.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (_thumbWarmed.Contains(v.Path)) continue;
                queue.Add(v.Path);
            }
            if (queue.Count == 0) return;
            _thumbWarmQueued = true;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    foreach (var p in queue)
                    {
                        if (_thumbWarmed.Contains(p)) continue;
                        _thumbWarmed.Add(p);
                        _ = DecodeThumb(p);   // 闸门 3 路并发消化, 可见行与预热同队
                    }
                }
                finally { _thumbWarmQueued = false; }
            });
        }

        // ---- 列表点按 ----

        /// <summary>行点按: 起播</summary>
        internal void OnRowTap(HomeRow row)
        {
            var path = row.Tag?.ToString();
            if (path == null) return;
            var pos = _filtered.FindIndex(v => v.Path == path);
            if (pos < 0) return;
            OnRowTapRow(pos);
        }

        /// <summary>按下标起播一条列表项 (点按与长按菜单共用): v3.24.2 抽出统一分诊。</summary>
        private void OnRowTapRow(int pos)
        {
            var path = _filtered[pos].Path;
            // v3.11: 先亮出播放页让画面出口挂稳, 再起播 —— 防止流先出、画面黑着
            // v3.25.8: 本次路径随 ShowPlayer 带入, 开场封面按新片取预览 (旧读 CurrentPath 是上一条)
            ShowPlayer(true, path);
            if (path.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                // v3.10: 磁力条目直接起流播放, 不进连播列表
                var saveDir = AppSettings.Current.AutoSaveMagnet ? AppSettings.Current.SaveFolder : null;
                _ = _host.PlayStandaloneAsync(path, saveDir);
                return;
            }
            if (HlsStreamService.IsHlsUrl(path))
            {
                // v3.24: m3u8 条目直接起流播放, 边播边存开关跟磁力共用, 不进连播列表
                var saveDir = AppSettings.Current.AutoSaveMagnet ? AppSettings.Current.SaveFolder : null;
                _ = _host.PlayStandaloneAsync(path, saveDir);
                return;
            }
            _host.PlayList(_filtered, pos);
            // v3.21.0: 增强入队统一由 CurrentPathChanged 触发, 覆盖上下曲/自动连播/外部点播全部换片路径
        }

        // ================= 列表行 =================

        /// <summary>构造列表行内容: t=紧凑文字行, g=图文行 (v3.25.21 勾选圈随多选一并退场)。</summary>
        internal View BuildRowContent(string mode)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(TaoUi.Dp(this, 14), TaoUi.Dp(this, 12), TaoUi.Dp(this, 14), TaoUi.Dp(this, 12));
            row.Background = TaoUi.Card(this, 24);   // v3.5 ColorOS 卡片: 大圆角无描边, 靠投影分层
            row.Elevation = 4;

            if (mode == "t")
            {
                var tcol = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var tTitle = new TextView(this) { TextSize = 16 };
                tTitle.SetTextColor(TaoTheme.TextMain);
                var tSize = new TextView(this) { TextSize = 12 };
                tSize.SetTextColor(TaoTheme.TextSub);
                tcol.AddView(tTitle);
                tcol.AddView(tSize);
                tcol.AddView(TaoUi.MakeProgressTrack(this));
                row.AddView(tcol, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            }
            else
            {
                var thumb = new ImageView(this) { Background = new ColorDrawable(TaoTheme.Stroke) };
                thumb.SetScaleType(ImageView.ScaleType.CenterCrop);
                // v3.5 ColorOS 圆角缩略图: 占位底与帧图都走同一圆角裁切
                var thumbBg = new GradientDrawable();
                thumbBg.SetColor(TaoTheme.Stroke);
                thumbBg.SetCornerRadius(TaoUi.Dp(this, 14));
                thumb.Background = thumbBg;
                thumb.OutlineProvider = ViewOutlineProvider.Background;
                thumb.ClipToOutline = true;
                row.AddView(thumb, new LinearLayout.LayoutParams(TaoUi.Dp(this, 110), TaoUi.Dp(this, 64)) { RightMargin = TaoUi.Dp(this, 14) });
                var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var t2 = new TextView(this) { TextSize = 16 };
                t2.SetTextColor(TaoTheme.TextMain);
                var size = new TextView(this) { TextSize = 12 };
                size.SetTextColor(TaoTheme.TextSub);
                col.AddView(t2);
                col.AddView(size);
                col.AddView(TaoUi.MakeProgressTrack(this));
                row.AddView(col, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            }
            return row;
        }

        // ================= 适配器与行容器 =================

        // v3.4 缩略图效率: 解码并发闸门 + 缓存出入队记录 (超上限最旧先出)
        private static readonly System.Threading.SemaphoreSlim _thumbGate = new(6);
        private const int ThumbCacheMax = 300;
        private readonly Queue<string> _thumbOrder = new();
        // v3.7: 解码失败负缓存 (本会话不再反复解, 滚动不被反复拖慢) + 快滑暂停旗标
        private readonly HashSet<string> _thumbFailed = new(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _thumbPaused;

        /// <summary>图文列表适配器: 首帧缩略图 + 名称 + 大小</summary>
        internal class VideoAdapter : BaseAdapter<VideoItem>
        {
            private readonly MainActivity _act;
            public VideoAdapter(MainActivity act) { _act = act; }
            public override VideoItem this[int position] => _act._filtered[position];
            public override int Count => _act._filtered.Count;
            public override long GetItemId(int position) => position;

            public override View GetView(int position, View? convertView, ViewGroup? parent)
            {
                var item = _act._filtered[position];
                var mode = _act._galleryMode ? "g" : "t";
                HomeRow wrap;
                View row;
                if (convertView is HomeRow hr && hr.ContentMode == mode)
                {
                    wrap = hr;
                    row = hr.Content;
                }
                else
                {
                    row = _act.BuildRowContent(mode);
                    HomeRow fresh = null!;
                    fresh = new HomeRow(_act,
                        onClick: () => _act.OnRowTap(fresh));   // v3.25.22 老板令: 长按菜单退场, 行只响应点按
                    wrap = fresh;
                    wrap.ContentMode = mode;
                    wrap.Attach(row);
                }

                if (mode == "t")
                {
                    var textRow = (LinearLayout)row;
                    var tc = (LinearLayout)textRow.GetChildAt(0)!;
                    var tTitle = (TextView)tc.GetChildAt(0)!;
                    var tSize = (TextView)tc.GetChildAt(1)!;
                    tTitle.Text = item.Name;
                    tSize.Text = item.SizeText;
                    BindProgress(tc, item);
                }
                else
                {
                    var galleryRow = (LinearLayout)row;
                    var thumb = (ImageView)galleryRow.GetChildAt(0)!;
                    var col = (LinearLayout)galleryRow.GetChildAt(1)!;
                    var title = (TextView)col.GetChildAt(0)!;
                    var size = (TextView)col.GetChildAt(1)!;
                    title.Text = item.Name;
                    size.Text = item.SizeText;
                    BindProgress(col, item);
                    thumb.Tag = item.Path;
                    if (_act._thumbs.TryGetValue(item.Path, out var ready))
                    {
                        thumb.SetImageBitmap(ready);
                    }
                    else
                    {
                        thumb.SetImageBitmap(null);
                        _act.LoadThumb(item.Path, thumb);
                    }
                }
                wrap.Tag = item.Path;
                return wrap;
            }

            private void BindProgress(LinearLayout col, VideoItem item)
            {
                var track = (LinearLayout?)col.GetChildAt(col.ChildCount - 1);
                if (track == null) return;
                var haveResume = AppSettings.Current.Resume.TryGetValue(item.Path, out var resumeMs);
                var haveDur = AppSettings.Current.Durations.TryGetValue(item.Path, out var durMs);
                if (haveResume && haveDur && durMs > 0 && resumeMs > 0 && resumeMs < durMs * 0.98)
                {
                    track.Visibility = ViewStates.Visible;
                    double frac = Math.Clamp((double)resumeMs / durMs, 0.0, 1.0);
                    var fill = (View)track.GetChildAt(0)!;
                    var lp = (LinearLayout.LayoutParams)fill.LayoutParameters!;
                    lp.Weight = (float)frac;
                    fill.LayoutParameters = lp;
                }
                else
                {
                    track.Visibility = ViewStates.Gone;
                }
            }
        }

        /// <summary>给可见行补图 (v3.13 重构): 解码统一走 DecodeThumb (内存→磁盘→取帧), 出图回贴; 行已滚走就不贴。</summary>
        internal async void LoadThumb(string path, ImageView thumb)
        {
            if (_thumbs.TryGetValue(path, out var hit)) { thumb.SetImageBitmap(hit); return; }
            if (_thumbFailed.Contains(path)) return;          // 解不出的不再反复尝试
            if (thumb.Tag?.ToString() != path) return;        // 行已被复用成别的视频, 不解码
            var bmp = await DecodeThumb(path);
            if (bmp == null) return;
            if (thumb.Tag?.ToString() != path) return;        // 解码期间行已滚走, 不回贴
            thumb.SetImageBitmap(bmp);
        }

        // v3.13 预览图磁盘缓存: 解好的帧按路径哈希落 CacheDir, 重启/重扫后一进列表就有图, 不再每次重解
        private string? _thumbDir;
        private string ThumbDir => _thumbDir ??= System.IO.Path.Combine(CacheDir!.AbsolutePath, "thumbs");

        private static string ThumbKey(string path, long len)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path + "|" + len))).Replace("-", "").ToLowerInvariant();
        }

        private static long ThumbLen(string path) { try { return new System.IO.FileInfo(path).Length; } catch { return 0; } }

        /// <summary>预览图解码全链 (v3.13): ①内存缓存 → ②磁盘缓存 → ③MediaMetadataRetriever 取帧;
        /// 并发闸门最多 3 路同时解码; 多时间点取帧失败记负缓存本会话不再重试; 内存封顶 300 条最旧先出。</summary>
        // v3.18 修「预览缓存没起作用」: 在途解码改共享任务——预热队列占住路径时, 可见行等同一结果
        // 而不是空手而回; 预热解完即时回贴, 不再整列灰着等滚动
        private readonly object _thumbInFlightLock = new();
        private readonly Dictionary<string, Task<Bitmap?>> _thumbInFlight = new();

        private Task<Bitmap?> DecodeThumb(string path)
        {
            if (_thumbs.TryGetValue(path, out var hit)) return Task.FromResult<Bitmap?>(hit);
            if (_thumbFailed.Contains(path)) return Task.FromResult<Bitmap?>(null);
            lock (_thumbInFlightLock)
            {
                if (_thumbInFlight.TryGetValue(path, out var inflight)) return inflight;
                var task = DecodeThumbCore(path);
                _thumbInFlight[path] = task;
                return task;
            }
        }

        private async Task<Bitmap?> DecodeThumbCore(string path)
        {
            try
            {
                await _thumbGate.WaitAsync();
                try
                {
                    if (_thumbs.TryGetValue(path, out var again)) return again;   // 双检: 排队期间别处已解出
                    var disk = await System.Threading.Tasks.Task.Run(() => TryLoadThumbDisk(path));   // v3.16: 读盘+解码出主线程
                    if (disk != null) { CacheThumb(path, disk); return disk; }

                    var bmp = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using var retr = new global::Android.Media.MediaMetadataRetriever();
                            retr.SetDataSource(path);
                            int targetW = TaoUi.Dp(this, 110), targetH = TaoUi.Dp(this, 64);
                            int vw = int.TryParse(retr.ExtractMetadata((int)global::Android.Media.MetadataKey.VideoWidth), out var w) ? w : 0;
                            int vh = int.TryParse(retr.ExtractMetadata((int)global::Android.Media.MetadataKey.VideoHeight), out var h) ? h : 0;
                            double cover = (vw > 0 && vh > 0) ? Math.Max((double)targetW / vw, (double)targetH / vh) : 1;
                            int wantW = vw > 0 ? Math.Max(1, (int)Math.Round(vw * cover)) : targetW;
                            int wantH = vh > 0 ? Math.Max(1, (int)Math.Round(vh * cover)) : targetH;

                            // 多时间点逐个试 (ClosestSync 最近帧, 寻址最快): 首帧是黑帧/损坏或取不出的,
                            // 1s 和片长 10% 处通常能出图
                            Bitmap? frame = null;
                            int durUs = 0;
                            if (int.TryParse(retr.ExtractMetadata((int)global::Android.Media.MetadataKey.Duration), out var durMs) && durMs > 0)
                                durUs = durMs * 1000;
                            // 黑屏免入 (2026-11-07 用户定): 逐时间点取帧, 平均亮度达标的才算"有画面";
                            // 片头黑帧直接跳过, 全部偏暗时用最亮的一帧兜底, 绝不交黑屏给预览
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
                            frame = best;
                            if (frame == null) return null;
                            if (chosenUs >= 0) RememberCoverStamp(path, chosenUs);   // v3.25.8 封面帧起播: 记下预览取帧时间点
                            // 兜底再等比收一刀: 只缩不放, 宽高比保持 (交给 CenterCrop 裁掉超出部分)
                            double fw = frame.Width, fh = frame.Height;
                            double fit = Math.Min(targetW / fw, targetH / fh);
                            if (fit < 1)
                            {
                                var scaled = Bitmap.CreateScaledBitmap(frame, Math.Max(1, (int)(fw * fit)), Math.Max(1, (int)(fh * fit)), true);
                                if (scaled != frame) frame.Recycle();
                                frame = scaled;
                            }
                            return frame;
                        }
                        catch { return null; }
                    });
                    if (bmp == null) { _thumbFailed.Add(path); return null; }   // 全时间点解不出: 记入负缓存, 不再重试
                    SaveThumbDisk(path, bmp);
                    CacheThumb(path, bmp);
                    return bmp;
                }
                finally { _thumbGate.Release(); }
            }
            finally
            {
                lock (_thumbInFlightLock) { _thumbInFlight.Remove(path); }
            }
        }

        private void CacheThumb(string path, Bitmap bmp)
        {
            _thumbs[path] = bmp;
            _thumbOrder.Enqueue(path);
            while (_thumbOrder.Count > ThumbCacheMax)
            {
                var oldest = _thumbOrder.Dequeue();
                if (_thumbOrder.Contains(oldest)) continue;   // 同路径重复入队时, 留最新一份
                _thumbs.Remove(oldest);   // 逐出交给 GC 回收, 不强行 Recycle (可能还被显示着)
            }
        }

        private Bitmap? TryLoadThumbDisk(string path)
        {
            try
            {
                var f = System.IO.Path.Combine(ThumbDir, ThumbKey(path, ThumbLen(path)) + ".jpg");
                if (!System.IO.File.Exists(f)) return null;
                var bytes = System.IO.File.ReadAllBytes(f);
                return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            }
            catch { return null; }
        }

        private void SaveThumbDisk(string path, Bitmap bmp)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ThumbDir);
                using var fs = System.IO.File.Create(System.IO.Path.Combine(ThumbDir, ThumbKey(path, ThumbLen(path)) + ".jpg"));
                bmp.Compress(Bitmap.CompressFormat.Jpeg!, 85, fs);
                PruneThumbDisk();
            }
            catch { }
        }

        /// <summary>磁盘缓存封顶 600 张, 超了最旧先删。</summary>
        private void PruneThumbDisk()
        {
            try
            {
                var files = new System.IO.DirectoryInfo(ThumbDir).GetFiles("*.jpg");
                if (files.Length <= 600) return;
                foreach (var f in files.OrderBy(x => x.LastWriteTimeUtc).Take(files.Length - 600))
                { try { f.Delete(); } catch { } }
            }
            catch { }
        }

        /// <summary>v3.13 预加载: 从 from 项起向后预解 count 张预览图 (网络流/磁力不预解), 用户一划到就有图。去重与限流都在 DecodeThumb 里。</summary>
        private void PreloadThumbs(int from, int count)
        {
            if (_thumbPaused) return;
            for (int i = Math.Max(0, from); i < _filtered.Count && i < from + count; i++)
            {
                var p = _filtered[i].Path;
                if (p.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                _ = DecodeThumb(p);
            }
        }

        /// <summary>取帧时间点序列 (微秒): 1s → 片长10% → 3s → 0s, 逐个试到出图为止。</summary>
        /// <summary>预览图亮度门槛 (0-255): 低于此值视为黑帧/黑屏, 跳过不进预览</summary>
        internal const double ThumbLumaMin = 16.0;

        /// <summary>帧平均亮度 (Rec.601 加权, 0-255); 读不出像素当有画面处理, 不误杀</summary>
        internal static double ThumbMeanLuma(global::Android.Graphics.Bitmap b)
        {
            try
            {
                int w = b.Width, h = b.Height;
                var px = new int[w * h];
                b.GetPixels(px, 0, w, 0, 0, w, h);
                long sum = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    int c = px[i];
                    sum += (((c >> 16) & 0xFF) * 299 + ((c >> 8) & 0xFF) * 587 + (c & 0xFF) * 114) / 1000;
                }
                return sum / (double)px.Length;
            }
            catch { return 255.0; }
        }

        private static System.Collections.Generic.IEnumerable<long> ThumbStamps(int durUs)
        {
            yield return 1_000_000L;
            if (durUs > 5_000_000) yield return (long)(durUs * 0.1);
            yield return 3_000_000L;
            yield return 0;
        }

        // ---------------- v3.25.8 封面帧起播: 预览取帧时间点的记账 ----------------
        // 用户拍板: 预览与真实画面衔接时, 播放直接从预览那一帧开始, 不回跳片头黑帧。
        // 时间点随预览解码顺路记下 (内存 + CacheDir 侧账, 重启后读侧账照常衔接)。

        /// <summary>路径 → 预览取帧时间点 (毫秒); 并发字典: 解码在后台线程, 点播在 UI 线程。</summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> CoverStampUs = new();

        /// <summary>预览解出帧时记下取帧时间点 (微秒 → 毫秒), 并落盘侧账供下次启动读。</summary>
        internal void RememberCoverStamp(string path, long stampUs)
        {
            try
            {
                var ms = stampUs / 1000;
                CoverStampUs[path] = ms;
                Directory.CreateDirectory(ThumbDir);
                File.WriteAllText(System.IO.Path.Combine(ThumbDir, ThumbKey(path, ThumbLen(path)) + ".stamp"), ms.ToString());
            }
            catch { /* 记账失败不影响预览 */ }
        }

        /// <summary>起播前查预览取帧时间点 (毫秒); 记过账的本地文件才有, 其余返回 0 = 从头播。</summary>
        internal long GetCoverStartMs(string path)
        {
            if (CoverStampUs.TryGetValue(path, out var hit)) return hit;
            try
            {
                var f = System.IO.Path.Combine(ThumbDir, ThumbKey(path, ThumbLen(path)) + ".stamp");
                if (File.Exists(f) && long.TryParse(File.ReadAllText(f), out var ms) && ms > 0)
                { CoverStampUs[path] = ms; return ms; }
            }
            catch { }
            return 0;
        }

        /// <summary>快滑停稳后把屏幕里还没有图的行补上 (列表滚停不会重走 GetView, 只能这里手动触发)。</summary>
        private void RefreshVisibleThumbs()
        {
            int first = _list.FirstVisiblePosition, last = _list.LastVisiblePosition;
            for (int i = first; i <= last; i++)
            {
                if (_list.GetChildAt(i - first) is not HomeRow hr) continue;
                if (hr.Content is not LinearLayout ll || ll.ChildCount < 3) continue;
                if (ll.GetChildAt(1) is not ImageView iv) continue;
                var p = iv.Tag?.ToString();
                if (p == null || _thumbs.ContainsKey(p) || _thumbFailed.Contains(p)) continue;
                LoadThumb(p, iv);
            }
            PreloadThumbs(last + 1, 24);   // v3.13 停稳后顺带预解下一段, 一划到就有图
        }

        /// <summary>列表行容器: 包住内容并接管点按/长按; 复用时只换内容视图。</summary>
        internal class HomeRow : FrameLayout
        {
            public View Content = null!;
            public string? ContentMode;

            public HomeRow(global::Android.Content.Context ctx, Action onClick) : base(ctx)
            {
                Clickable = true;
                // 卡片行与搜索框左缘对齐 (v3.11: 左侧不再内缩 12dp); 右侧留白交给列表的滚动条槽
                SetPadding(0, TaoUi.Dp((Activity)ctx, 5), 0, 0);
                float slop = ViewConfiguration.Get(ctx)?.ScaledTouchSlop ?? 0;
                float downX = 0, downY = 0;
                bool moved = false;
                // v3.25.8 真机修: 触摸监听全量接管事件后, 系统的点击检测链路 (onTouchEvent) 不会启动,
                // Click 永远不响 —— 自己判定: 按下后未挪出 slop 即点按, 挪动/取消不算
                // v3.25.22 老板令: 长按菜单整个退场, 只留点按判定
                Touch += (s, e) =>
                {
                    var ev = e.Event!;
                    switch (ev.ActionMasked)
                    {
                        case MotionEventActions.Down:
                            downX = ev.GetX(); downY = ev.GetY(); moved = false;
                            e.Handled = true;
                            break;
                        case MotionEventActions.Move:
                            if (Math.Abs(ev.GetX() - downX) > slop || Math.Abs(ev.GetY() - downY) > slop) moved = true;
                            e.Handled = true;
                            break;
                        case MotionEventActions.Up:
                            if (!moved) onClick();
                            e.Handled = true;
                            break;
                        case MotionEventActions.Cancel:
                            e.Handled = true;
                            break;
                    }
                };
            }

            public void Attach(View content)
            {
                Content = content;
                AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            }
        }
    }
}
