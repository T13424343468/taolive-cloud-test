using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AllPlayer.BLL;
using TaoLive.BLL;
using TaoLive.Model;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// r3 直播主线（partial）：频道目录/取流/健康账全接电视版家底（TaoLive.BLL 同一套），
    /// 界面按手机版老电视风格：直播页顶部组合筛选框（数字命中频道号、文字命中台名、
    /// 边输边筛），点台即播；播放页上台/下台=直播频道上一/下一；
    /// 分片账目过 AiBufferPool（初始缓冲 10-20s、落后 60s 封顶直出、过热/低电直出、
    /// 降档建议回写 AI 档位），换台清池复位。
    /// </summary>
    public partial class MainActivity
    {
        // ---- 直播态字段 ----
        internal static volatile bool _isLive;              // 播放页直播态（static: VideoHost 回调跨 partial 可读）
        internal ChannelCatalog? _liveCatalog;
        internal StreamResolver? _liveResolver;
        internal DynamicSourceStore? _liveDynStore; // vc45②：动态源库（搜源验活收录，落盘 dynamic_sources.json）
        internal AiBufferPool? _livePool;
        internal LiveHlsRelay? _liveRelay;
        internal Channel? _liveCurrent;
        internal List<Channel> _liveAll = new();
        internal int _liveSeq;                              // 换台防串号
        internal int _liveSeqPlaying = -1;
        internal long _downgradeHintAtMs;                   // 降档建议冷却
        internal LinearLayout? _livePanel;                   // 直播列表覆盖层
        internal EditText? _liveFilter;
        internal LinearLayout? _liveListHost;
        internal TextView? _liveStatus;
        private CancellationTokenSource? _liveGuardCts;

        /// <summary>直播装配：OnCreate 末尾后台跑, 不挡启动。</summary>
        internal void InitLive()
        {
            Task.Run(() =>
            {
                try
                {
                    DeviceIdentity.Attach(FilesDir!.AbsolutePath);   // 央视频 guid 设备身份, 与电视版同口径
                    _livePool = new AiBufferPool();
                    var resolver = _liveResolver = new StreamResolver();
                    // vc45②：选源智能化接线（与电视版同口径，无常规级节拍——手机前台约束，救急级已覆盖）
                    var dynStore = _liveDynStore = new DynamicSourceStore();
                    var filesDir = FilesDir!.AbsolutePath;
                    // vc46E 时序修复：先赋 OnChanged 再启动回灌——回灌窗口内 Add 不再触发 OnChanged=null 漏存
                    dynStore.OnChanged = () =>
                    {
                        try { DynamicSourceRegistry.Save(dynStore, filesDir); }
                        catch { /* 落盘失败不拖播放：下次变更重试 */ }
                    };
                    Task.Run(() => DynamicSourceRegistry.Load(dynStore, filesDir));
                    resolver.DynamicSources = dynStore;
                    resolver.Scout = new SourceScout(new SourceIntelligenceEngine(), dynStore);
                    resolver.DiscoverCandidates = PublicListDiscover.FindAsync;
                    // vc46②9：三本账接线（与电视版同口径：冷却/判死/预算全量启用）
                    resolver.Cooldowns = new FailureCooldownLedger();
                    resolver.DeadLedger = new DeadSourceLedger();
                    resolver.Budget = new SwitchBudget();
                    var fav = new FavoriteStore();
                    _liveCatalog = new ChannelCatalog(fav);
                    _liveCatalog.RegisterSource(new YangshipinSource());
                    _liveCatalog.RegisterSource(new CztvSource());
                    _liveCatalog.RegisterSource(new GzstvSource());
                    _liveCatalog.RegisterSource(new XatvSource());
                    _liveCatalog.RegisterSource(new NmtvSource());
                    _liveCatalog.RegisterSource(new GxtvSource());
                    _liveCatalog.RegisterSource(new CityCandidatesSource());
                    _liveCatalog.Rebuild();
                    _liveAll = _liveCatalog.All.Where(c => !c.Unavailable).OrderBy(c => c.Number).ToList();
                    RunOnUiThread(() => { if (_liveStatus != null) _liveStatus.Text = $"{_liveAll.Count} 路 · 点台即播"; });
                    StartLiveGuard();
                }
                catch (Exception ex)
                {
                    AppLog.W("直播目录装配失败: " + ex.Message);
                    RunOnUiThread(() => { if (_liveStatus != null) _liveStatus.Text = "台单没装上, 重启再试"; });
                }
            });
        }

        /// <summary>守卫轮询：5s 低频喂过热/低电进池; 池报降档建议就给 AI 档位降档（60s 冷却）。</summary>
        private void StartLiveGuard()
        {
            _liveGuardCts = new CancellationTokenSource();
            var ct = _liveGuardCts.Token;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, ct);
                        var pool = _livePool;
                        if (pool == null) continue;
                        var temp = ReadBatteryTemp();
                        var (pct, charging) = ReadBatteryLevel();
                        pool.SetThermalGuard(!double.IsNaN(temp) && temp >= 45.0, !charging && pct > 0 && pct <= 15);
                        if (pool.LagSeconds() > 45 && AllPlayer.Model.AppSettings.Current.AiEnhanceTier > 0
                            && System.Environment.TickCount64 - _downgradeHintAtMs > 60_000)
                        {
                            _downgradeHintAtMs = System.Environment.TickCount64;
                            AllPlayer.Model.AppSettings.Current.AiEnhanceTier = AllPlayer.Model.AppSettings.Current.AiEnhanceTier - 1;   // 降档建议: 减一档
                            SaveSettingsSafe();
                            RunOnUiThread(() => TaoUi.Toast(this, "直播负载高, AI 画质已自动降档"));
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch { }
                }
            }, ct);
        }

        // ================= 直播列表页 =================

        /// <summary>直播页: 顶部组合筛选框 + 分类分组频道列表 (老电视风格, 覆盖在主页之上)。</summary>
        internal void ShowLivePanel()
        {
            if (_livePanel != null) return;
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetBackgroundColor(TaoTheme.Bg);
            _root.AddView(panel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // 顶行: 标题 + 关闭
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            top.SetPadding(TaoUi.Dp(this, 16), TaoUi.Dp(this, 14), TaoUi.Dp(this, 16), TaoUi.Dp(this, 10));
            var title = new TextView(this) { Text = "直播", TextSize = 20, Typeface = Typeface.DefaultBold };
            title.SetTextColor(TaoTheme.TextMain);
            top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var close = new TextView(this) { Text = "✕", TextSize = 20, Gravity = GravityFlags.Center };
            close.SetTextColor(TaoTheme.TextSub);
            close.SetPadding(TaoUi.Dp(this, 16), TaoUi.Dp(this, 8), TaoUi.Dp(this, 8), TaoUi.Dp(this, 8));
            close.Click += (s, e) => CloseLivePanel();
            top.AddView(close, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            panel.AddView(top, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // 组合筛选框: 数字命中频道号, 文字命中台名, 边输边筛
            _liveFilter = new EditText(this) { Hint = "频道号或台名, 如 1 或 央视", TextSize = 14 };
            _liveFilter.SetSingleLine(true);
            _liveFilter.SetPadding(TaoUi.Dp(this, 12), TaoUi.Dp(this, 10), TaoUi.Dp(this, 12), TaoUi.Dp(this, 10));
            _liveFilter.TextChanged += (s, e) => RebuildLiveList();
            var filterWrap = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            filterWrap.SetPadding(TaoUi.Dp(this, 16), 0, TaoUi.Dp(this, 16), TaoUi.Dp(this, 8));
            filterWrap.AddView(_liveFilter, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            panel.AddView(filterWrap);

            _liveStatus = new TextView(this) { Text = "台单装台中…", TextSize = 12 };
            _liveStatus.SetTextColor(TaoTheme.TextSub);
            _liveStatus.SetPadding(TaoUi.Dp(this, 16), 0, TaoUi.Dp(this, 16), TaoUi.Dp(this, 6));
            panel.AddView(_liveStatus);

            _liveListHost = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var scroll = new ScrollView(this) { ScrollBarStyle = ScrollbarStyles.OutsideOverlay };
            scroll.AddView(_liveListHost);
            panel.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            _livePanel = panel;
            RebuildLiveList();
        }

        internal void CloseLivePanel()
        {
            if (_livePanel == null) return;
            _root.RemoveView(_livePanel);
            _livePanel = null;
            _liveFilter = null;
            _liveListHost = null;
            _liveStatus = null;
        }

        /// <summary>组合筛选: 提取数字与文字, 数字前缀命中频道号, 文字包含命中台名, 两者同满足 (混合通配口径)。</summary>
        private List<Channel> LiveFilterChannels()
        {
            var kw = (_liveFilter?.Text?.ToString() ?? "").Trim();
            if (kw.Length == 0) return _liveAll;
            var digits = new string(kw.Where(char.IsDigit).ToArray());
            var text = new string(kw.Where(c => !char.IsDigit(c) && c != ' ').ToArray());
            IEnumerable<Channel> q = _liveAll;
            if (digits.Length > 0) q = q.Where(c => c.Number.ToString().StartsWith(digits, StringComparison.Ordinal));
            if (text.Length > 0) q = q.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
            return q.ToList();
        }

        private void RebuildLiveList()
        {
            var host = _liveListHost;
            if (host == null) return;
            host.RemoveAllViews();
            if (_liveAll.Count == 0)
            {
                var wait = new TextView(this) { Text = "台单装台中, 一会儿就好…", TextSize = 13 };
                wait.SetTextColor(TaoTheme.TextSub);
                wait.SetPadding(TaoUi.Dp(this, 16), TaoUi.Dp(this, 24), TaoUi.Dp(this, 16), TaoUi.Dp(this, 8));
                host.AddView(wait);
                return;
            }
            var list = LiveFilterChannels();
            if (list.Count == 0)
            {
                var none = new TextView(this) { Text = "没找到对应的台, 换个说法试试", TextSize = 13 };
                none.SetTextColor(TaoTheme.TextSub);
                none.SetPadding(TaoUi.Dp(this, 16), TaoUi.Dp(this, 24), TaoUi.Dp(this, 16), TaoUi.Dp(this, 8));
                host.AddView(none);
                return;
            }
            string? lastCat = null;
            foreach (var ch in list)
            {
                var catTitle = ch.Category switch
                {
                    ChannelCategory.Cctv => "央视",
                    ChannelCategory.Cgtn => "CGTN",
                    ChannelCategory.Special => "专项",
                    ChannelCategory.Provincial => "省台",
                    ChannelCategory.City => "城市台",
                    _ => "自有源",
                };
                if (catTitle != lastCat)
                {
                    lastCat = catTitle;
                    var head = new TextView(this) { Text = catTitle, TextSize = 12, Typeface = Typeface.DefaultBold };
                    head.SetTextColor(TaoTheme.TextSub);
                    head.SetPadding(TaoUi.Dp(this, 16), TaoUi.Dp(this, 12), TaoUi.Dp(this, 16), TaoUi.Dp(this, 4));
                    host.AddView(head);
                }
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(TaoUi.Dp(this, 16), TaoUi.Dp(this, 11), TaoUi.Dp(this, 16), TaoUi.Dp(this, 11));
                var num = new TextView(this) { Text = ch.Number.ToString(), TextSize = 15, Typeface = Typeface.DefaultBold };
                num.SetTextColor(TaoTheme.Accent);
                num.SetPadding(0, 0, TaoUi.Dp(this, 14), 0);
                row.AddView(num, new LinearLayout.LayoutParams(TaoUi.Dp(this, 34), ViewGroup.LayoutParams.WrapContent));
                var name = new TextView(this) { Text = ch.Name, TextSize = 15 };
                name.SetTextColor(TaoTheme.TextMain);
                row.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                if (_liveCurrent?.Number == ch.Number)
                {
                    var playing = new TextView(this) { Text = "▶", TextSize = 13 };
                    playing.SetTextColor(TaoTheme.Accent);
                    row.AddView(playing);
                }
                row.Click += (s, e) => PlayLiveChannel(ch);
                host.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            }
        }

        // ================= 直播播放与换台 =================

        /// <summary>点台即播: 取流(4 秒快失败+备援) → 本机中转挂池 → 直播播放。</summary>
        internal void PlayLiveChannel(Channel ch)
        {
            if (_liveResolver == null) { TaoUi.Toast(this, "台单还没装好, 稍等一下"); return; }
            _liveSeq++;
            int seq = _liveSeq;
            _isLive = true;
            _liveCurrent = ch;
            RebuildLiveList();   // 播放标记刷新
            CloseLivePanel();
            ShowPlayer(true, $"live:{ch.Number}");
            _title.Text = ch.Name;
            TaoUi.Toast(this, $"{ch.Number}  {ch.Name}  连接中…");

            _ = Task.Run(async () =>
            {
                ResolvedStream? resolved = null;
                try
                {
                    var main = _liveResolver.ResolvePlayUrlAsync(ch);
                    var winner = await Task.WhenAny(main, Task.Delay(4000));
                    resolved = winner == main ? await main : await _liveResolver.ResolveWithFailoverAsync(ch);
                    if (resolved == null && seq == _liveSeq) resolved = await main;   // 备援也空回主取流兜底
                }
                catch (Exception ex)
                {
                    AppLog.W($"[直播] 取流异常 {ch.Name}: {ex.Message}");
                }
                if (seq != _liveSeq) return;   // 换台后旧任务作废
                if (resolved == null)
                {
                    RunOnUiThread(() => { if (seq == _liveSeq) TaoUi.Toast(this, $"{ch.Name}  暂时没接上, 先看别的台"); });
                    return;
                }
                // 本机中转: 分片过池账目+保险; 起服务失败即直连裸播 (不挡播)
                _liveRelay?.Stop();
                _livePool?.Reset();
                string playUrl = resolved.Url;
                try
                {
                    var relay = new LiveHlsRelay(_livePool!);
                    playUrl = relay.Start(resolved.Url, resolved.Options);
                    _liveRelay = relay;
                }
                catch (Exception ex)
                {
                    AppLog.W("[直播] 中转起服务失败, 走直连: " + ex.Message);
                    _liveRelay = null;
                }
                RunOnUiThread(() =>
                {
                    if (seq != _liveSeq) return;
                    _ = _host.PlayLiveAsync(playUrl);
                    UpdateAiStatusText();
                });
            });
        }

        /// <summary>直播上一台/下一台: 按频道号顺位, 空台跳过。</summary>
        internal void LiveSwitch(int dir)
        {
            if (_liveAll.Count == 0) return;
            int idx = _liveCurrent == null ? -1 : _liveAll.FindIndex(c => c.Number == _liveCurrent!.Number);
            Channel next;
            for (int step = 1; step <= _liveAll.Count; step++)
            {
                int at = ((idx < 0 ? 0 : idx) + dir * step) % _liveAll.Count;
                if (at < 0) at += _liveAll.Count;
                next = _liveAll[at];
                if (next.Unavailable) continue;
                PlayLiveChannel(next);
                return;
            }
        }

        /// <summary>停直播: 中转/池复位/态位清理 (退出播放页与换非直播片共用)。</summary>
        internal void StopLivePlayback()
        {
            _isLive = false;
            _liveCurrent = null;
            try { _liveRelay?.Stop(); } catch { }
            _liveRelay = null;
            try { _livePool?.Reset(); } catch { }
        }

        /// <summary>直播态 AI 状态字: 池实时账 (初始缓冲/落后/直出)。</summary>
        internal string LivePoolStatusText()
        {
            var pool = _livePool;
            if (pool == null) return "";
            if (!pool.IsInitialBufferReady()) return $"AI池: 初始缓冲中 ({pool.LagSeconds():F0}s 封顶 60s)";
            var lag = pool.LagSeconds();
            return lag >= 55 ? "AI池: 直出 (落后封顶)" : $"AI池: 落后 {lag:F0}s · 加工跟播";
        }
    }
}
