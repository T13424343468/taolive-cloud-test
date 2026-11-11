using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using AllPlayer.BLL;
using AllPlayer.Model;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// AI 画质实验档接线（partial MainActivity，播放层零改动只加）：
    ///  - 启动后低优先级装配模型（TaoAIAssets → 缓存目录），内核 Init；
    ///  - EnhancementCoordinator 实例化：磁力白名单注入（MagnetStreamService 活跃判定由
    ///    VideoHost 侧状态汇总）、enhanceStep 接 AiSegmentRunner→SegmentWriter.Finalize；
    ///  - 守卫轮询（电池温度/播放卡顿，5s 低频）；
    ///  - 档位偏好 = AppSettings.AiEnhanceTier（默认关）。
    /// </summary>
    internal static partial class MainActivityAi
    {
        // 字段挂在 MainActivity 实例上（partial 内部聚合）
    }

    public partial class MainActivity
    {
        internal EnhancementCoordinator? _coordinator;
        internal CancellationTokenSource? _guardCts;
        internal AiSegmentRunner? _runner;
        internal static int _taoaiInited;   // 0=未装 1=已装（进程内一次）

        /// <summary>AI 装配：OnCreate 末尾调用；v3.18.5 改为后台无限重试（5s 起步、30s 封顶退避），
        /// 修复旧版「3 次共约 15 秒放弃后基准永久准备中」。失败全程只进日志不上屏。</summary>
        internal void InitAiEnhance()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _taoaiInited, 1, 0) != 0)
                {
                    // v3.25.8 真机修: 活动重建后来到——内核进程级早已装好的直接补挂协调器
                    // (原来新实例永远等不到装配, 状态字卡死「装模型中」)
                    if (_aiModelsDir != null) StartCoordinator();
                    return;
                }
                StartAiAssembly();
            }
            catch (Exception ex) { AppLog.W($"AI 装配启动失败: {ex.GetType().Name} {ex.Message}"); }
        }

        /// <summary>v3.18.5 点按即装：基准入口在模型未就绪时调用（幂等），装好经 AiAssemblyReady 提醒。</summary>
        internal void EnsureAiAssembly() => StartAiAssembly();

        /// <summary>装配完成事件（UI 线程触发）：基准入口订阅一次，就绪后提醒用户再点。</summary>
        internal event Action? AiAssemblyReady;

        private int _aiAssemblyRunning;   // 0=无装配线程 1=在跑（进程内一次）

        private void StartAiAssembly()
        {
            try
            {
                if (_aiModelsDir != null) return;
                if (Interlocked.CompareExchange(ref _aiAssemblyRunning, 1, 0) != 0) return;

                var cacheDir = new Java.IO.File(GetExternalFilesDir(null), "taoai-models");
                var marker = Path.Combine(cacheDir.AbsolutePath, ".assembled");
                if (File.Exists(marker))
                    AppLog.W("AI 装配标记在: 模型文件就位, 本次跳过解压直通内核初始化");
                var assets = new TaoAIAssets(OpenAssetStream, cacheDir.AbsolutePath, AppLog.W);
                var initThread = new Thread(() =>
                {
                    try
                    {
                        int attempt = 0, kernelFails = 0, backoffMs = 5000;
                        while (_aiModelsDir == null)
                        {
                            attempt++;
                            try
                            {
                                string dir = assets.EnsureReady();          // 首用解压（大小级自愈+原子替换；有标记且大小一致时秒过）
                                int rc = TaoAIKernel.Init(dir, 4);
                                if (rc == 0)
                                {
                                    _aiModelsDir = dir;
                                    try { File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
                                    AppLog.W($"AI 装配成功 (第{attempt}次) dir={dir}");
                                    // v3.25.8 真机修: 回调落现役实例——装配发起的活动若已被重建, 新实例照样接上
                                    var live = _liveAiHost;
                                    if (live != null)
                                    {
                                        live.RunOnUiThread(() =>
                                        {
                                            live.StartCoordinator();
                                            try { live.UpdateAiStatusText(); } catch { }   // v3.20.6: 模型装好立即刷新状态字
                                            try { live.AiAssemblyReady?.Invoke(); } catch { }
                                        });
                                    }
                                    return;
                                }
                                kernelFails++;
                                lastRc = rc;
                                lastEx = null;
                                AppLog.W($"AI 内核初始化 rc={rc} (第{attempt}次)");
                                if (kernelFails >= 2)
                                {
                                    // v3.18.5: 连续两次内核装载失败——大小校验挡不住的内容级损坏/混版，
                                    // 整目录重置一次（连带装配标记），下一轮全量重解压
                                    kernelFails = 0;
                                    try { Directory.Delete(dir, recursive: true); AppLog.W("AI 模型缓存整目录重置, 待重装"); } catch { }
                                    assets = new TaoAIAssets(OpenAssetStream, cacheDir.AbsolutePath, AppLog.W);
                                }
                            }
                            catch (Exception ex)
                            {
                                lastEx = ex; lastRc = 0;
                                AppLog.W($"AI 装配异常 (第{attempt}次): {ex.GetType().Name} {ex.Message}");
                            }
                            // v3.18.5: 失败不再静默——具体原因人话上屏（第2次起, 之后每5次提醒一次, 防刷屏）
                            if (attempt == 2 || (attempt > 2 && (attempt - 2) % 5 == 0))
                            {
                                string reason = HumanReason(lastEx, lastRc);
                                RunOnUiThread(() =>
                                {
                                    try { TaoUi.Toast(this, $"增强功能安装失败：{reason}。后台会一直重试，装好提醒你"); } catch { }
                                });
                            }
                            Thread.Sleep(backoffMs);
                            backoffMs = Math.Min(backoffMs * 2, 30000);   // 封顶退避：失败期短暂占用, 成功后即停
                        }
                    }
                    finally { Volatile.Write(ref _aiAssemblyRunning, 0); }
                })
                { IsBackground = true, Priority = System.Threading.ThreadPriority.Normal, Name = "taoai-setup" };
                initThread.Start();
            }
            catch (Exception ex) { AppLog.W($"AI 装配线程启动失败: {ex.GetType().Name} {ex.Message}"); }
        }

        // 装配失败现场（人话原因取材）：线程内记录最近一次异常/错误码
        private Exception? lastEx;
        private int lastRc;

        /// <summary>v3.18.5 失败原因人话化：上屏用, 技术细节仍只进日志。
        /// v3.20.1: 引擎加载失败时带上原生装载器的原始报错（缺哪个符号一目了然，不再两眼一抹黑）。</summary>
        private static string HumanReason(Exception? ex, int rc)
        {
            if (rc == -1) return "模型描述文件读不过";
            if (rc == -2) return "模型权重文件读不过";
            if (ex is DllNotFoundException dll) return "AI 引擎加载失败: " + dll.Message;
            if (ex is UnauthorizedAccessException) return "存储不让写";
            if (ex is IOException ioe && ioe.Message.Contains("缺失")) return "安装包内模型文件缺失";
            if (ex is IOException) return "存储读写失败";
            return "未知原因（" + (ex?.GetType().Name ?? "错误码" + rc) + "）";
        }

        internal static string? _aiModelsDir;   // v3.25.8 真机修: 改进程级——内核是进程级的, 模型目录同理; 原来随活动实例, 活动重建后新实例永远等不到装配, 状态字卡死「装模型中」

        /// <summary>现役活动实例 (v3.25.8 真机修): 装配成功回调落到它身上, 活动重建后新实例接管。</summary>
        internal static MainActivity? _liveAiHost;

        private System.IO.Stream OpenAssetStream(string assetName)
            => Assets!.Open(assetName);

        /// <summary>内核就绪后启动编排（档位=设置值，默认关）。</summary>
        private void StartCoordinator()
        {
            try
            {
                if (_coordinator != null || _aiModelsDir == null) return;

                _runner = new AiSegmentRunner(
                    IsMagnetActive,
                    progress: (done, total) => { /* 进度上屏由播放页覆盖条承担；此处仅状态机记账 */ },
                    paused: () => _coordinator?.State is AiEnhanceState.PausedGuard or AiEnhanceState.PausedThermal,
                    srTile: AiTierPicker.TileOf((AiEnhanceTier)AppSettings.Current.AiEnhanceTier),
                    enableInterpolate: AppSettings.Current.AiEnhanceTier >= 3,
                    srModel: TaoAIKernel.SrModelAnime);

                var coordinator = new EnhancementCoordinator(
                    IsMagnetActive,
                    enhanceStep: path => EnhanceOne(path));
                _coordinator = coordinator;
                coordinator.Tier = (AiEnhanceTier)Math.Max(0, Math.Min(3, AppSettings.Current.AiEnhanceTier));

                // v3.21.1: 增强任务状态变化/进度推进时, 立即刷新播放页状态字
                coordinator.StateChanged += st => { try { RunOnUiThread(() => { try { UpdateAiStatusText(); } catch { } }); } catch { } };
                coordinator.ProgressChanged += (d, t) => { try { RunOnUiThread(() => { try { UpdateAiStatusText(); } catch { } }); } catch { } };

                // 守卫轮询：电池温度/电量/功耗/充电（低频）+ 播放卡顿 + 自适应实时调档（游戏满载包络标定）
                _guardCts = AiGuard.StartPolling(
                    pollBatteryTemp: ReadBatteryTemp,
                    isPlaybackStalled: () => _inPlayer && !_host.IsPlaying,
                    coordinator: coordinator,
                    pollBattery: ReadBatteryLevel,
                    preferredTier: () => AppSettings.Current.AiAutoGear
                        ? AutoPreferredTier()
                        : (AiEnhanceTier)Math.Clamp(AppSettings.Current.AiEnhanceTier, 0, 3),
                    onTierAdjusted: ApplyAdaptiveTier,
                    pollPowerW: ReadPowerW);
            }
            catch { }
        }

        /// <summary>磁力活跃白名单：磁力流服务在播/在下载的文件绝不覆盖。</summary>
        internal bool IsMagnetActive(string path)
        {
            try
            {
                var cur = _host.CurrentPath;
                if (cur != null && string.Equals(Normalize(cur), Normalize(path), StringComparison.OrdinalIgnoreCase))
                {
                    // 当前正在播放的路径若是磁力流（magnet: 或边下边存未完成的文件），判活跃
                    if (cur.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return true;
                    if (_host.Recording) return true;
                }
                return false;
            }
            catch { return true; }   // 存疑一律不覆盖
        }

        private static string Normalize(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        /// <summary>增强一段：跑 AiSegmentRunner（解码→taoai→编码→tmp）→ SegmentWriter.Finalize（三验+原子替换）。
        /// 返回原文件路径（无论替换与否，调用方语义是"该文件处理完"）。</summary>
        internal string? EnhanceOne(string path)
        {
            try
            {
                if (_runner == null) return null;
                AppLog.W($"增强执行: {path}");
                string? md5 = _runner.Run(path, out var info);
                if (md5 == null) { AppLog.W($"增强跳过/失败(原样返回): {path}"); return path; }    // 跳过/失败：原样返回，不覆盖

                var writer = new SegmentWriter(IsMagnetActive);
                var outcome = writer.Finalize(path, SegmentWriter.TmpPathFor(path), md5,
                    probeTmpDuration: p =>
                    {
                        try { return new AiSegmentRunner(null, null, null, 0, false).Probe(p)?.DurationMs ?? 0; }
                        catch { return 0; }
                    },
                    _runner.FrameSamples);
                return path;
            }
            catch { return path; }
        }

        /// <summary>电池温度（摄氏度）；读不到返回 NaN（跳过发热判定）。</summary>
        private double ReadBatteryTemp()
        {
            try
            {
                var intent = RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
                int t = intent?.GetIntExtra("temperature", int.MinValue) ?? int.MinValue;
                return t == int.MinValue ? double.NaN : t / 10.0;
            }
            catch { return double.NaN; }
        }

        /// <summary>电量与充电状态（同一电池粘性广播；读不到返回 0/未充电，策略侧按未知跳过）。</summary>
        private (int Pct, bool Charging) ReadBatteryLevel()
        {
            try
            {
                var intent = RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
                if (intent == null) return (0, false);
                int level = intent.GetIntExtra("level", -1);
                int scale = intent.GetIntExtra("scale", 100);
                int plugged = intent.GetIntExtra("plugged", 0);
                bool charging = plugged != 0;
                if (level < 0 || scale <= 0) return (0, charging);
                return ((int)(level * 100L / scale), charging);
            }
            catch { return (0, false); }
        }

        /// <summary>整机功耗估算（瓦，v3.20.0 守卫用）：电池电压×电流；
        /// 读数不可信（厂商不出电流或恒零）返回 NaN，策略侧按未知跳过。</summary>
        private double ReadPowerW()
        {
            try
            {
                var bm = GetSystemService(BatteryService) as BatteryManager;
                int ua = bm?.GetIntProperty((int)BatteryProperty.CurrentNow) ?? int.MinValue;
                if (ua == int.MinValue || ua == 0) return double.NaN;
                var intent = RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
                int mv = intent?.GetIntExtra("voltage", int.MinValue) ?? int.MinValue;
                if (mv == int.MinValue || mv <= 0) return double.NaN;
                return Math.Abs(ua / 1_000_000.0 * mv / 1000.0);
            }
            catch { return double.NaN; }
        }

        /// <summary>自适应调档落地（守卫线程调）：切档并按新档位重建执行体（tile/插帧随档，即时生效于后续段）。</summary>
        internal void ApplyAdaptiveTier(AiEnhanceTier target)
        {
            try
            {
                if (_coordinator == null) return;
                _coordinator.Tier = target;
                _runner = new AiSegmentRunner(
                    IsMagnetActive,
                    progress: (done, total) => { },
                    paused: () => _coordinator?.State is AiEnhanceState.PausedGuard or AiEnhanceState.PausedThermal,
                    srTile: AiTierPicker.TileOf(target),
                    enableInterpolate: target >= AiEnhanceTier.High,
                    srModel: TaoAIKernel.SrModelAnime);
            }
            catch { }
        }

        /// <summary>播放中文件入队增强（由播放页在起播/换片时调用；档位关闭时内部忽略）</summary>
        internal void QueueEnhanceIfEnabled(string path)
        {
            try { _coordinator?.Enqueue(path); } catch { }
        }

        // ---------- 分段续存（v3.15）：看一段优化一段，每看一次进度就落在文件里 ----------

        internal AiChunkStore? _chunkStore;
        internal AiChunkManifest? _chunkMan;
        internal string? _chunkSrcPath;
        internal AiChunkMerger? _merger;
        internal BlockingCollection<Action>? _chunkQ;   // 分段专用单线程（算段/合并串行，互不打架）
        internal readonly object _chunkLock = new object();
        internal long _lastChunkTickMs;
        internal int _chunkGen;               // v3.17 seek 感知代数：seek/换片即推进，排队任务代数不齐不空算
        internal long _openedMergedMs;        // v3.17 当前播放实例打开时的合并前沿（换源判定基准）
        internal long _lastSwapTickMs = -300000;   // v3.17 上次原地换源时刻（跟播批量换源频率限制：1 次/5 分钟）

        private long ProbeDurMs(string p)
        {
            try { return new AiSegmentRunner(null, null, null, 0, false).Probe(p)?.DurationMs ?? 0; }
            catch { return 0; }
        }

        /// <summary>起播/换片入口（替代 QueueEnhanceIfEnabled 调用点）：
        /// mp4+时长≥120s 走分段续存；否则整条老路原样入队。档位关闭时全部内部忽略。</summary>
        internal void OnPlaybackStartChunkAware(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (AppSettings.Current.AiEnhanceTier <= 0 || _coordinator == null) { QueueEnhanceIfEnabled(path); return; }

                // v3.21.0: 同一部视频重复触发（循环重播/重复回调）不重置分段状态
                lock (_chunkLock)
                {
                    if (_chunkSrcPath != null && _chunkMan != null &&
                        string.Equals(Normalize(_chunkSrcPath), Normalize(path), StringComparison.OrdinalIgnoreCase))
                        return;
                }

                // 换片：上一部还有待合并段，先排合并（异步，不挡播放）；代数推进撤销旧片的排队
                lock (_chunkLock)
                {
                    var oldM = _chunkMan; var oldSrc = _chunkSrcPath;
                    if (oldM != null && oldSrc != null && oldSrc != path && oldM.Chunks.Count > 0)
                        MergeChunks(oldM, oldSrc);
                    _chunkMan = null; _chunkSrcPath = null; _chunkStore = null;
                    Volatile.Write(ref _chunkGen, _chunkGen + 1);
                }
                _lastChunkTickMs = 0;   // v3.21.0: 新片起播即重置进度节流, 首个进度回调立刻排队

                bool progressive = false;
                long durMs = 0;
                try
                {
                    if (!path.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
                        AiSegmentRunner.IsMp4Container(path))
                    {
                        durMs = ProbeDurMs(path);
                        progressive = durMs >= AiChunkPlan.MinProgressiveMs;
                    }
                }
                catch { }

                if (!progressive) { AppLog.W($"增强入队(整条): mp4={AiSegmentRunner.IsMp4Container(path)} dur={durMs} {path}"); QueueEnhanceIfEnabled(path); return; }   // 短片维持现状（TP-AI 断言不破）

                AppLog.W($"增强入队(分段): dur={durMs} {path}");
                // 分段：确保账本存在（时长对不上自动作废重建），首段不等观看进度先算
                EnsureChunkStore(path, durMs);
                QueueNextChunk();
            }
            catch { }
        }

        internal void EnsureChunkStore(string path, long durMs)
        {
            try
            {
                var store = new AiChunkStore(CacheDir!.AbsolutePath, path);
                long bytes = 0;
                try { bytes = new FileInfo(path).Length; } catch { }
                var m = store.LoadOrInit(durMs, bytes);
                lock (_chunkLock) { _chunkStore = store; _chunkMan = m; _chunkSrcPath = path; _openedMergedMs = m.MergedMs; }
                _merger = new AiChunkMerger(IsMagnetActive, ProbeDurMs);
                EnsureChunkWorker();
            }
            catch { }
        }

        /// <summary>观看进度回调（TimeUpdated 内，5s 节流）：值得算就排下一段，回看落后太多不追算。</summary>
        internal void OnWatchProgressChunkAware(string path, long posMs)
        {
            try
            {
                if (_chunkQ == null) return;
                long now = System.Environment.TickCount;
                if (now - _lastChunkTickMs < 5000) return;
                _lastChunkTickMs = now;
                AiChunkManifest? m;
                lock (_chunkLock) { m = _chunkMan; }
                if (m == null || path == null || _chunkSrcPath == null ||
                    !string.Equals(Normalize(path), Normalize(_chunkSrcPath), StringComparison.OrdinalIgnoreCase)) return;
                if (!AiChunkPlan.ShouldEnhanceNext(m, posMs, lookahead: 2)) { TryAutoSwapSource(m, posMs); return; }
                QueueNextChunk();
            }
            catch { }
        }

        /// <summary>跟播批量换源（v3.17）：新增强内容攒够 60 秒且播放头还在旧实例上 → 原地换源一次见全量
        /// 效果（频率 1 次/5 分钟，不与 seek 换源叠加）。</summary>
        internal void TryAutoSwapSource(AiChunkManifest m, long headMs)
        {
            try
            {
                if (System.Environment.TickCount - _lastSwapTickMs < 300000) return;
                if (!AiChunkPlan.ShouldAutoSwapSource(m, _openedMergedMs, headMs, 60_000)) return;
                SwapSourceInPlace(headMs);
            }
            catch { }
        }

        /// <summary>seek 跟随（v3.17）：拖动进度/手势快进收尾时调用。撤销 stale 排队（来回拖动不空算）；
        /// 落点在新增强区 → 原地换源当场见效果；落点在未增强区照常播原画，增强链从合并前沿继续追。</summary>
        internal void OnSeekChunkAware(string? path, long posMs)
        {
            try
            {
                if (_chunkQ == null) return;
                Volatile.Write(ref _chunkGen, _chunkGen + 1);   // 按最后停留位置算：换向后排队里没开工的作废
                AiChunkManifest? m; string? src;
                lock (_chunkLock) { m = _chunkMan; src = _chunkSrcPath; }
                if (m == null || src == null || path == null ||
                    !string.Equals(Normalize(path), Normalize(src), StringComparison.OrdinalIgnoreCase)) return;
                if (AiChunkPlan.ShouldSwapSource(m, _openedMergedMs, posMs))
                    SwapSourceInPlace(posMs);
                _lastChunkTickMs = 0;   // 拖动收尾立即重排，不等 5s 节流
            }
            catch { }
        }

        /// <summary>原地换源（播放引擎层）：重开同路径文件增强内容生效；换源后基准前沿对齐，避免重复换源。</summary>
        internal void SwapSourceInPlace(long posMs)
        {
            try
            {
                if (_host.Player == null) return;
                _lastSwapTickMs = System.Environment.TickCount;
                _openedMergedMs = long.MaxValue;   // 防重入：重开期间不再触发
                _host.ReopenInPlace(posMs);
                AiChunkManifest? m;
                lock (_chunkLock) { m = _chunkMan; }
                if (m != null) _openedMergedMs = m.MergedMs;
            }
            catch { }
        }

        private void EnsureChunkWorker()
        {
            if (_chunkQ != null) return;
            var q = new BlockingCollection<Action>();
            _chunkQ = q;
            var t = new Thread(() =>
            {
                foreach (var job in q.GetConsumingEnumerable())
                {
                    try { job(); } catch { }
                }
            })
            { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "taoai-chunk" };
            t.Start();
        }

        /// <summary>排下一段：链尾接着算；成功落账，攒够 3 段自动合并；失败静默（原文件/账本都不动）。</summary>
        internal void QueueNextChunk()
        {
            try
            {
                if (_runner == null || _chunkStore == null) return;
                EnsureChunkWorker();
                AiChunkManifest? m; string? src; AiChunkStore? store;
                lock (_chunkLock) { m = _chunkMan; src = _chunkSrcPath; store = _chunkStore; }
                if (m == null || src == null || store == null) return;
                int gen = Volatile.Read(ref _chunkGen);
                _chunkQ!.Add(() =>
                {
                    try
                    {
                        if (AiChunkPlan.IsStale(gen, Volatile.Read(ref _chunkGen))) return;   // v3.17: seek/换片后排队的作废，按最后停留位置重排
                        lock (_chunkLock) { }
                        var (startMs, endMs) = AiChunkPlan.NextChunkRange(m, m.DurMs);
                        if (endMs <= startMs) return;   // 全片排完
                        int idx = m.Chunks.Count;
                        string chunkPath = store.ChunkPath(idx);
                        string? md5 = _runner.RunChunk(src, startMs, endMs, chunkPath, out _, out long actualEndMs);
                        if (md5 == null || actualEndMs <= startMs)
                        {
                            AppLog.W($"段增强失败: range=[{startMs},{endMs}) md5={(md5 == null ? "null" : "ok")}");
                            try { File.Delete(chunkPath); } catch { }
                            return;   // 失败静默：账本不动，下次再排
                        }
                        bool needMerge;
                        lock (_chunkLock)
                        {
                            // 账本可能已被合并推进：只接得上链的段才收（范围过期直接丢弃）
                            long tail = m.Chunks.Count > 0 ? m.Chunks[m.Chunks.Count - 1].EndMs : m.MergedMs;
                            if (tail != startMs)
                            {
                                try { File.Delete(chunkPath); } catch { }
                                return;
                            }
                            m.Chunks.Add(new AiChunkManifest.Chunk
                            {
                                File = Path.GetFileName(chunkPath),
                                StartMs = startMs,
                                EndMs = actualEndMs,
                                Bytes = new FileInfo(chunkPath).Length
                            });
                            needMerge = m.Chunks.Count >= 1;   // v3.16 用户定：一段即合，算完立即合并进原文件
                        }
                        store.Save(m);          // 每看一次进度就落在文件里：算完一段立即落账
                        store.EvictToFit(m);    // 封顶不占空间（内部有淘汰才存）
                        if (needMerge) MergeChunks(m, src);
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>排合并（异步，专用单线程）：合并成功 → 账本推进 + 段文件清理。</summary>
        internal void MergeChunks(AiChunkManifest m, string src)
        {
            try
            {
                if (_chunkQ == null) return;
                AiChunkMerger? merger; AiChunkStore? store;
                lock (_chunkLock) { merger = _merger; store = _chunkStore; }
                if (merger == null || store == null) return;

                _chunkQ!.Add(() =>
                {
                    try
                    {
                        // 合并前快照段文件清单（ApplyMerge 清空账本链后仍要删文件）
                        string[] files;
                        lock (_chunkLock) { files = m.Chunks.ConvertAll(c => c.File).ToArray(); }
                        long newMergedMs = merger.Merge(src, m, store);
                        if (newMergedMs > 0)
                        {
                            lock (_chunkLock) { AiChunkPlan.ApplyMerge(m, newMergedMs); }
                            foreach (var f in files) store.DeleteChunkFile(f);
                            store.Save(m);
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>退出播放钩子（OnBackPressed 的 _inPlayer 分支）：有待合并段就合并收口。</summary>
        internal void OnPlaybackExitChunkAware()
        {
            try
            {
                AiChunkManifest? m; string? src;
                lock (_chunkLock) { m = _chunkMan; src = _chunkSrcPath; }
                if (m == null || src == null || m.Chunks.Count == 0) return;
                MergeChunks(m, src);   // 合并前快照已捕获，退出后账本照常推进
            }
            catch { }
        }
    }
}
