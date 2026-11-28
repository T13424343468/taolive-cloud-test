using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.DataSource.Cache;
using TaoLive.Foundation;

namespace TaoLive.Players;

/// <summary>
/// vc140 媒体缓存层（老板 2026-11-28 拍板「大手术一次到位」——vc94 试探并发引擎收编进灌水器，
/// 一个水位大脑管档位与退避，vc94/vc139 双拉拆台根除；vc139 缓存层口径原样保留：老板同日
/// 16:49-16:50 拍板「攒下来的水都应该在缓存方向，播放器吃的永远是缓存中的流；播放器吃了缓存
/// 就清掉被吃掉的」、16:53 追加拍板「攒的新片往缓存里填，保证缓存里永远有满 60 秒的画面」）：
/// 进程级落盘缓存是唯一数据面——网络分片先进缓存，播放器只从缓存读；未命中走网络边拉边写。
/// 同线重连/宽限重试时滑动窗口内分片命中续播，不再从零重攒。直播清单（.m3u8/.mpd）永不缓存
/// （每次必拉最新，防过期窗口）。
/// 「永远满 60 秒」：<see cref="CacheFiller"/> 即唯一水位大脑——后台单路（vc142 老板令「同源后台
/// 只许 1 条」）把未播内容顺灌到满设置页「预取库存时长」旋钮的秒数；水满收兵，清单滚动或消费前推即再灌。
/// 「吃了就清」：LRU 256MB 兜底 + 定期清扫——最近 120 秒（60 秒水位窗+余量）没被读过的分片
/// （已播过/已换台作废）清出；水位窗内正在吃和即将吃的绝不清。缓存目录随进程存活，
/// 内核实例重建不清（这正是「留」的关键）。
/// </summary>
public static class MediaCache
{
    /// <summary>缓存上限（字节）：LRU 淘汰兜底，60 秒水位窗体量远小于此值。</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>缓存水位线（毫秒）：未播内容攒满设置页「预取库存时长」旋钮的秒数才停灌。
    /// vc142 老板令（「同时要排查设置页所有旋钮是否全局生效」）：原 const 60_000 绕开旋钮属实锤，
    /// 改读 AdvancedTuning.PrefetchWindowSec（30/45/60 秒三档，Apply 钳制 10~300）——旋钮写活不写死。</summary>
    internal static long WaterAheadMs => AdvancedTuning.PrefetchWindowSec * 1000L;

    /// <summary>分片存活期（毫秒）：最后一次被读（吃）超过此时长的分片清出——水位窗内的分片最迟
    /// 每窗被吃一次（touch 刷新），TTL 线只清身后已播过/已作废的水。vc142：随水位窗旋钮走
    /// （下限 120 秒=原 60 秒窗+余量口径），窗调深 TTL 同步放大，窗内水绝不清。</summary>
    private static long SliceTtlMs => Math.Max(120_000, WaterAheadMs * 2);

    /// <summary>清扫周期（毫秒）：定期扫缓存把过期分片清掉。</summary>
    private const long SweepPeriodMs = 10_000;

    private static SimpleCache? _cache;
    private static long _retryNotBeforeMs;  // vc144 自愈限频：初始化失败后的重试闸（窗内直连旁路，窗后重建）
    private static long _lastHealAtMs;      // vc144 自愈限频：运行期重建最近一次时刻
    private static string? _dirPath;        // vc144 自愈：缓存目录（连根拔病索引用）
    internal const long HealMinIntervalMs = 5 * 60_000; // 自愈限频窗：5 分钟最多重建一次，防反复擦建
    private static BackupFiller? _backupFiller;
    // vc139 统一出口（老板 2026-11-28 拍板「预取器收编进统一数据面」）：灌水器/备源攒水/预取器后台
    // 拉片共用本工厂——缓存命中零网络，未命中走竞速+换票壳（无预取层，不递归不互扰）边拉边写穿。
    // 双拉拆台根除：预取器与灌水器谁先到谁落盘，另一方命中缓存，弱线带宽不再烧两遍。
    private static IDataSourceFactory? _fetchFactory;
    private static readonly object _lock = new();

    /// <summary>进程级缓存实例（单例复用）：目录 cacheDir/mediacache，LRU 淘汰 + 定期清扫。
    /// vc144 自愈重建（老板令「修法以根治缓存层为主」）：初始化失败（坏索引/磁盘满/IO 异常）
    /// 不再永久禁用——连根拔掉缓存目录（病索引随目录一起清）后返回 null 交调用方降级直连
    /// （Wrap/BuildPlayerCacheFactory 按 null 走旁路），直连只是自愈窗内的临时便道；
    /// 窗后（<see cref="HealMinIntervalMs"/>）下次装配全新缓存上岗，缓存机制始终活着。</summary>
    public static SimpleCache? Get(Context ctx)
    {
        lock (_lock)
        {
            if (_cache is { } c) return c;
            long now = System.Environment.TickCount64;
            if (now < _retryNotBeforeMs) return null; // 自愈窗内：直连便道，窗后重建
            try
            {
                var dir = new Java.IO.File(ctx.CacheDir!.AbsolutePath, "mediacache");
                _dirPath = dir.AbsolutePath;
                // 三参构造（非弃用重载）：databaseProvider=null 走文件索引，缓存只在本应用沙箱目录
                _cache = new SimpleCache(dir, new LeastRecentlyUsedCacheEvictor(MaxBytes),
                    (AndroidX.Media3.Database.IDatabaseProvider?)null);
                StartSweeper();
                return _cache;
            }
            catch (Exception ex)
            {
                // vc144 自愈：初始化失败=病索引/坏账在目录里——连根拔目录（SimpleCache 索引坏账
                // 不会自愈，留着只会反复抛），设重试闸窗后全新重建，不永久禁用
                _cache = null;
                try { if (_dirPath is not null && Directory.Exists(_dirPath)) Directory.Delete(_dirPath, true); }
                catch { /* 目录拔不掉无害：窗后重建再试 */ }
                _retryNotBeforeMs = now + HealMinIntervalMs;
                Android.Util.Log.Warn("TaoLive", $"[缓存] 初始化失败，拔病索引目录，{HealMinIntervalMs / 60000} 分钟后自愈重建：{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>vc144 自愈重建（老板令「根治缓存层」）：运行期缓存层抛病（索引坏账/写路径空指针）
    /// 时由旁路壳调用——释放旧缓存实例、连根拔病索引目录、清统一出口（<see cref="_fetchFactory"/>
    /// 一并作废，下个装配周期全新缓存+全新工厂上岗），缓存机制始终活着，直连只是自愈窗内便道。
    /// 限频 <see cref="HealMinIntervalMs"/>：5 分钟最多重建一次，防反复擦建。</summary>
    internal static void Heal(string reason)
    {
        lock (_lock)
        {
            long now = System.Environment.TickCount64;
            if (now - _lastHealAtMs < HealMinIntervalMs) return; // 限频：窗内不反复擦建
            _lastHealAtMs = now;
            try { _cache?.Release(); } catch { /* 释放失败无害：目录连根拔兜底 */ }
            _cache = null;
            _fetchFactory = null; // 统一出口作废：下个装配周期按新缓存重建（Wrap 单例键解除）
            try { if (_dirPath is not null && Directory.Exists(_dirPath)) Directory.Delete(_dirPath, true); }
            catch { /* 目录拔不掉无害：Get 初始化失败路径再拔 */ }
            _retryNotBeforeMs = now + HealMinIntervalMs; // 重建闸：窗内直连便道
            Android.Util.Log.Warn("TaoLive", $"[缓存] 缓存层病，自愈重建（{reason}）：旧实例已释放、病索引已拔，窗后全新上岗");
        }
    }

    /// <summary>定期清扫（吃了就清）：遍历缓存分片，最后读取时间超过存活期的清出——
    /// 播放器吃过的旧段不占地方，缓存里只留水位窗内的水；LRU 上限作第二道兜底。
    /// 在途写入段（开放段）绝不清（写手还握着）。清扫失败静默（LRU 兜底），下轮再扫。</summary>
    private static void StartSweeper()
    {
        var handler = new Handler(Looper.MainLooper!);
        void Sweep()
        {
            try
            {
                var cache = _cache;
                if (cache is not null)
                {
                    long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                    var keys = cache.Keys;
                    if (keys is not null)
                    {
                        foreach (var keyObj in (IEnumerable)keys)
                        {
                            var key = keyObj?.ToString();
                            if (string.IsNullOrEmpty(key)) continue;
                            var spans = cache.GetCachedSpans(key);
                            if (spans is null) continue;
                            List<CacheSpan>? stale = null;
                            foreach (var obj in (IEnumerable)spans)
                            {
                                if (obj is not CacheSpan span || span.IsOpenEnded) continue;
                                if (now - span.LastTouchTimestamp > SliceTtlMs)
                                    (stale ??= new List<CacheSpan>()).Add(span);
                            }
                            if (stale is null) continue;
                            foreach (var span in stale)
                            {
                                try { cache.RemoveSpan(span); } catch { /* 单段清除失败无害 */ }
                            }
                        }
                    }
                }
            }
            catch
            {
                // 清扫失败不影响播放（LRU 上限兜底），下轮再扫
            }
            handler.PostDelayed(Sweep, SweepPeriodMs);
        }
        handler.PostDelayed(Sweep, SweepPeriodMs);
    }

    /// <summary>把缓存层包在既有数据源工厂外：播放器只从缓存读，未命中走 upstream（竞速壳
    /// 原样保留）边拉边写——网络只负责往缓存灌，播放器永远吃缓存中的流。网络错误时回源。
    /// 外层再包观测壳：清单全文喂灌水器换代、分片开流喂灌水器记账（消费位置推进）。</summary>
    public static IDataSourceFactory Wrap(IDataSourceFactory upstream, Context ctx, SourceTemperStore? temper = null)
    {
        lock (_lock)
        {
            // vc141 复查修：统一拉片出口进程级只建一次——工厂无实例状态（上游 RacingDataSourceFactory(null)
            // 无会话上下文），重复装配纯浪费；_fetchFactory 单例后 FillBackup/CreateFetchSource 不再随内核
            // 重建被换缝，热备双内核并发装配也不互踩。
            if (_fetchFactory is null)
            {
                var cache = Get(ctx);
                if (cache is null)
                {
                    // vc144 自愈窗：缓存不可用→统一出口退化为裸 HTTP（灌水器照常拉片，只是不落盘）；
                    // 窗后 Get 重建成功，本单例键随 Heal 作废，下个装配周期回到缓存写穿
                    _fetchFactory = RacingDataSourceFactory.CreateRawHttpFactory();
                }
                else
                {
                    // vc142 老板令（「同源后台只许 1 条、不碰换票链」）：灌水器统一出口 upstream 改裸 HTTP 工厂——
                    // 原 RacingDataSourceFactory(null) 带 TicketSwap 换票壳，灌水器 4xx 即触发坏票轮换
                    // （作废票据缓存+烧频控账），播放内核换不到新票→全线路不可达（CCTV 全断根因②）。
                    // 裸内层 4xx 只走灌水器自己的退避账（OnRejected/OnFailed），票据缓存分毫不动；
                    // 缓存键=URL 与播放器缓存互通，写穿落盘口径不变。
                    var fetch = new CacheDataSource.Factory();
                    fetch.SetCache(cache);
                    fetch.SetUpstreamDataSourceFactory(RacingDataSourceFactory.CreateRawHttpFactory());
                    fetch.SetCacheReadDataSourceFactory(new FileDataSource.Factory());
                    fetch.SetCacheKeyFactory(new PlaylistBypassKeyFactory());
                    fetch.SetFlags(CacheDataSource.FlagIgnoreCacheOnError);
                    _fetchFactory = fetch;
                }
            }
            // vc141 根因修③（filler 泄漏）：灌水器随内核实例走——Wrap 每调一次新建 filler（分片账/消费位
            // 是实例账本），调用方（ExoPlayerPlayer）持有观测工厂复用，内核盲区档重建不再新建 filler，
            // 旧 filler 的 FillLoop(while true) 不再逐个泄漏；Dispose 时经 Filler.Stop 停车。
            var filler = new CacheFiller(_fetchFactory, new FillThrottle(WaterAheadMs, temper));
            return new ObservingFactory(BuildPlayerCacheFactory(upstream, ctx), filler);
        }
    }

    /// <summary>播放器侧缓存工厂（vc141 拆出）：缓存命中零网络，未命中走 upstream（竞速+换票壳）边拉边写。
    /// 清单永不缓存（每次必拉最新，防过期窗口），网络错误回源。
    /// vc144 自愈口径：缓存不可用=直连 upstream（自愈窗内临时便道）；缓存可用再包运行期旁路壳——
    /// 缓存层读写抛 CacheCacheException/空指针（vc140 真机病灶）时本实例降级直连并触发
    /// <see cref="MediaCache.Heal"/> 自愈重建，缓存病了不堵播放路，窗后全新缓存上岗。</summary>
    private static IDataSourceFactory BuildPlayerCacheFactory(IDataSourceFactory upstream, Context ctx)
    {
        var cache = Get(ctx);
        if (cache is null) return upstream; // vc144：无缓存直连旁路
        var factory = new CacheDataSource.Factory();
        factory.SetCache(cache);
        factory.SetUpstreamDataSourceFactory(upstream);
        factory.SetCacheReadDataSourceFactory(new FileDataSource.Factory());
        factory.SetCacheKeyFactory(new PlaylistBypassKeyFactory());
        factory.SetFlags(CacheDataSource.FlagIgnoreCacheOnError);
        return new CacheBypassFactory(factory, upstream);
    }

    /// <summary>统一拉片出口（vc139 老板拍板）：缓存命中零网络直读，未命中走竞速+换票壳
    /// 边拉边写穿落盘。灌水器/备源攒水/预取器后台拉片共用；内核未装配返回 null（调用方退回裸内层）。</summary>
    public static IDataSource? CreateFetchSource()
    {
        lock (_lock)
        {
            return _fetchFactory?.CreateDataSource();
        }
    }

    /// <summary>换台停车（vc139 老板拍板）：换台即作废旧备源攒水器，不再白灌旧台的水；
    /// 换线不调（同台换线攒水继续有效，误切由会话层三重闸把住）。</summary>
    public static void StopBackup()
    {
        lock (_lock)
        {
            _backupFiller?.Stop();
            _backupFiller = null;
        }
    }

    /// <summary>备源攒水（vc139 老板令「先攒后切」）：弱线坐实、备线票焐热后，让备源在缓存里
    /// 后台攒水——拉备源清单解析分片，顺灌进缓存（不动画面不出声不算切源），攒满目标秒数
    /// 回调 onReady（会话层执行切换，切换瞬间内核吃缓存秒满门槛秒出画）。备源也死则静默退避，
    /// 不触发任何自救（主源继续，既有自救链兜底）。新请求替换旧请求（换线/换台口径）。</summary>
    public static void FillBackup(string playlistUrl, int targetAheadSec, Action? onReady)
    {
        lock (_lock)
        {
            if (_fetchFactory is null) return; // 内核未装配：无统一出口可写，跳过
            _backupFiller?.Stop();
            _backupFiller = new BackupFiller(_fetchFactory, playlistUrl, targetAheadSec, onReady);
            _backupFiller.Start();
        }
    }

    /// <summary>清单请求不缓存：缓存 key 加代次后缀，key 每次不同=永不命中=每次走网络拉最新。
    /// 分片 URL 稳定，原 URL 作 key，重连命中续播。
    /// vc141 复查留账：判定用全串 contains（比观测壳 IsPlaylist 的路径端后缀更保守）——分片查询串
    /// 带 .m3u8 字样时被误判清单=永不命中，只损失缓存不致命；反向漏判（清单 URL 全程无 .m3u8/.mpd
    /// 字样）无法从 URL 防守，症状=过期清单 404，交既有换票/自救兜底（现实源概率极低）。</summary>
    private sealed class PlaylistBypassKeyFactory : Java.Lang.Object, ICacheKeyFactory
    {
        private long _gen;
        public string BuildCacheKey(DataSpec? dataSpec)
        {
            var url = dataSpec?.Uri?.ToString() ?? dataSpec?.Key ?? "";
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
            {
                return $"{url}#{Java.Lang.JavaSystem.CurrentTimeMillis()}_{System.Threading.Interlocked.Increment(ref _gen)}";
            }
            return url;
        }
    }

    /// <summary>观测工厂：包住缓存工厂，创建观测数据源——清单全文与分片开流喂灌水器。
    /// vc141：改 internal 并暴露 Filler——内核实例持有观测工厂，Dispose 时停车灌水器（防孤儿循环）。</summary>
    internal sealed class ObservingFactory : Java.Lang.Object, IDataSourceFactory
    {
        private readonly IDataSourceFactory _inner;
        private readonly CacheFiller _filler;
        public ObservingFactory(IDataSourceFactory inner, CacheFiller filler)
        {
            _inner = inner;
            _filler = filler;
        }
        /// <summary>本工厂绑定的灌水器（内核 Dispose 时停车用）。</summary>
        internal CacheFiller Filler => _filler;
        public IDataSource CreateDataSource() => new ObservingDataSource(_inner.CreateDataSource()!, _filler);
    }

    /// <summary>观测数据源：透传缓存源的读写；清单（.m3u8）读取全文暂存、关闭时喂灌水器换代；
    /// 分片开流即通知灌水器推进消费位置。灌水器自己的拉流不经此壳（直接用缓存工厂），不递归。</summary>
    private sealed class ObservingDataSource : Java.Lang.Object, IDataSource
    {
        private readonly IDataSource _inner;
        private readonly CacheFiller _filler;
        private MemoryStream? _capture;
        private string? _captureUrl;
        private DataSpec? _lastSpec; // vc145：取址兜底（内层 getUri 为 null 时回退请求址，防清单加载器空指针）

        public ObservingDataSource(IDataSource inner, CacheFiller filler)
        {
            _inner = inner;
            _filler = filler;
        }

        public long Open(DataSpec? dataSpec)
        {
            _lastSpec = dataSpec;
            var url = dataSpec?.Uri?.ToString();
            _capture?.Dispose();
            _capture = null;
            _captureUrl = null;
            if (url is not null && dataSpec!.Position == 0 && ParallelPrefetchDataSource.IsPlaylist(url))
            {
                _capture = new MemoryStream();
                _captureUrl = url;
            }
            else if (url is not null)
            {
                _filler.NotifySegmentOpened(url);
            }
            return _inner.Open(dataSpec);
        }

        public int Read(byte[]? buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (_capture is { } cap && read > 0) cap.Write(buffer!, offset, read);
            return read;
        }

        public void Close()
        {
            try { _inner.Close(); } catch { /* 关闭期异常无害 */ }
            if (_capture is { } cap && _captureUrl is { } url)
            {
                try
                {
                    var text = System.Text.Encoding.UTF8.GetString(cap.ToArray());
                    _filler.UpdatePlaylist(text, url);
                }
                catch { /* 清单残包不掀桌，下次刷新再换代 */ }
            }
            _capture?.Dispose();
            _capture = null;
            _captureUrl = null;
        }

        public Android.Net.Uri? Uri => _inner.Uri ?? _lastSpec?.Uri;
        public IDictionary<string, IList<string>>? ResponseHeaders => _inner.ResponseHeaders;
        public void AddTransferListener(ITransferListener? transferListener) => _inner.AddTransferListener(transferListener);
    }

    /// <summary>
    /// 缓存灌水器=唯一水位大脑（vc140，老板 2026-11-28 拍板「大手术一次到位」；vc142 老板令
    /// 「同源后台只许 1 条、不碰换票链」单路化收口）：
    /// - 清单换代：观测壳喂全文，解析分片表（复用 <see cref="HlsPlaylistInspector"/>）；
    ///   换台（清单地址变）旧账全清+退避整账清零；同台滚动只重整已灌/在途/退避账；
    /// - 消费记账：播放器开流某分片即前推消费位置（幂等不回退）；
    /// - 顺灌：每拍发单路（基本盘 1 路，试探加路已拆）从消费位+2 段起顺序选未灌/未在途/未退避
    ///   分片，经统一出口（裸内层缓存工厂，4xx 不碰票据）拉满写穿落盘；未播内容攒满旋钮秒数收兵；
    /// - 纪律（<see cref="FillThrottle"/> 退避与脾气账保留）：429/403 被拒即源脾气入账；
    ///   其他失败 per-URL 指数退避（2 秒~5 分钟）；
    /// - 在途账：<see cref="_inFlight"/> 防同片重复发路；成功/失败/被拒各回报，finally 收账+Kick。
    /// </summary>
    internal sealed class CacheFiller
    {
        private readonly IDataSourceFactory _fetchFactory;
        private readonly FillThrottle _throttle;
        private readonly object _gate = new();
        private List<HlsSegmentInfo> _segments = new();
        private readonly HashSet<string> _filled = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
        private int _consumedIndex = -1;
        private string? _playlistUrl;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private Task? _loop;
        private volatile bool _stopped; // vc141：停车旗（内核 Dispose 时置位，FillLoop 退出不再空转）
        private long _cooldownUntilMs = long.MinValue; // vc141 根因修②：换台冷却——换代后歇 2 秒再开灌

        /// <summary>换台冷却（毫秒）：换代后灌水器歇 2 秒，头段让播放器先吃——防同段写锁竞争雪上加霜；
        /// 同台滚动不冷却（滚动是常态节奏，冷却只给换台）。</summary>
        internal const long ChannelSwitchCooldownMs = 2_000;

        /// <summary>让位段数（vc141 根因修①）：灌水器从消费位+2 段起灌——播放器正要吃/正在吃的段
        /// （消费位+1）留给播放器自己拉（写穿落盘），同段写锁竞争根除（播放线程在
        /// SimpleCache.startReadWrite 上被灌水器阻塞是 vc140 切台信号中断的根因）。</summary>
        internal const int YieldAheadSegments = 2;

        public CacheFiller(IDataSourceFactory fetchFactory, FillThrottle throttle)
        {
            _fetchFactory = fetchFactory;
            _throttle = throttle;
        }

        /// <summary>停车（vc141）：内核 Dispose 时调，FillLoop 退出、后续 Kick 静默——防孤儿循环泄漏。</summary>
        public void Stop() => _stopped = true;

        /// <summary>清单换代：解析成功即换代；换台（清单地址变）旧账全清（分片表/已灌/在途/消费位）
        /// +试探档位退避整账清零（源脾气为持久化账不清）；同台滚动只重整账（滚出清单的条目作废）。</summary>
        public void UpdatePlaylist(string text, string url)
        {
            if (!HlsPlaylistInspector.TryParse(text, url, out var segs)) return;
            bool channelChanged;
            lock (_gate)
            {
                channelChanged = !string.Equals(_playlistUrl, url, StringComparison.Ordinal);
                if (channelChanged)
                {
                    _playlistUrl = url;
                    _segments = new List<HlsSegmentInfo>();
                    _filled.Clear();
                    _inFlight.Clear();
                    _consumedIndex = -1;
                }
                _segments = segs;
                // 旧账按新清单重整：滚出清单的已灌/在途条目作废（防长播字典无界）；退避账同步 prune
                var valid = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in segs) valid.Add(s.Url);
                _filled.RemoveWhere(u => !valid.Contains(u));
                _inFlight.RemoveWhere(u => !valid.Contains(u));
            }
            if (channelChanged)
            {
                _throttle.Reset(); // 换台：档位/采样/退避整账清零（源脾气持久化账不清）
                // vc141 根因修②：换台冷却 2 秒——头段让播放器先吃，灌水器不抢跑
                _cooldownUntilMs = System.Environment.TickCount64 + ChannelSwitchCooldownMs;
            }
            else
            {
                _throttle.PruneBackoff(segs.Select(s => s.Url)); // 同台滚动：只重整退避账
            }
            Kick();
        }

        /// <summary>消费记账：播放器开流某分片即前推消费位置（幂等不回退），并触发顺灌。</summary>
        public void NotifySegmentOpened(string url)
        {
            lock (_gate)
            {
                var idx = _segments.FindIndex(s => string.Equals(s.Url, url, StringComparison.Ordinal));
                if (idx >= 0 && idx > _consumedIndex) _consumedIndex = idx;
            }
            Kick();
        }

        private void Kick()
        {
            if (_stopped) return; // vc141：已停车静默
            if (_loop is null) _loop = Task.Run(FillLoop);
            try { _signal.Release(); } catch { /* 信号满位无害 */ }
        }

        private async Task FillLoop()
        {
            while (!_stopped) // vc141：停车即退场，不再 while(true) 空转泄漏
            {
                try { await _signal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* 等待异常无害，下轮再灌 */ }
                if (_stopped) return;
                try { PumpTick(); }
                catch { /* 灌水失败不外抛，退避账兜底 */ }
            }
        }

        /// <summary>顺灌一拍：锁内算磁盘领先水位（aheadMs）——水满收兵；否则从消费位之后顺序选
        /// 一个未灌/未在途/未退避分片发单路拉取（vc142 老板令「同源后台只许 1 条」：试探加路整段拆除，
        /// <see cref="FillThrottle"/> 的 ProbeLevel 加路逻辑不再被灌水器使用，退避与脾气账保留）；
        /// 锁外异步拉满落盘。</summary>
        private void PumpTick()
        {
            if (_stopped) return;
            if (System.Environment.TickCount64 < _cooldownUntilMs) return; // vc141 根因修②：换台冷却期不开灌
            List<string> picks;
            lock (_gate)
            {
                var segs = _segments;
                var consumed = _consumedIndex;
                // 水位=已落盘领先量：消费位之后已灌分片的时长和
                long aheadMs = 0;
                for (int i = consumed + 1; i < segs.Count; i++)
                {
                    if (_filled.Contains(segs[i].Url)) aheadMs += (long)(segs[i].DurationSec * 1000);
                }
                if (aheadMs >= WaterAheadMs) return; // 水满收兵（读旋钮实时值）

                // vc142 老板令：同源后台只许 1 条连接——灌水器基本盘 1 路，试探加路（1+ProbeLevel）拆除；
                // 4 条打同源抢带宽抢写锁（vc140 手术实锤）的根因随单路化根除
                const int budget = 1;
                picks = new List<string>(budget);
                // vc141 根因修①：从消费位+2 段起灌（让位播放头）——播放器正要吃的段（消费位+1）留给
                // 播放器自己拉，同段写锁竞争（播放线程被 SimpleCache.startReadWrite 无限期阻塞）根除
                for (int i = consumed + YieldAheadSegments; i < segs.Count && picks.Count < budget; i++)
                {
                    var url = segs[i].Url;
                    if (_filled.Contains(url) || _inFlight.Contains(url) || _throttle.IsBackedOff(url)) continue;
                    picks.Add(url);
                }
                foreach (var url in picks) _inFlight.Add(url);
            }
            if (picks.Count == 0) return;
            foreach (var url in picks)
            {
                var u = url;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FetchSegmentAsync(u).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate) _inFlight.Remove(u);
                        Kick();
                    }
                });
            }
        }

        /// <summary>单段拉取时长预算（vc141 复查修）：灌水器原无超时——无限流分片（源把整场直播当
        /// 一片返回）会永久占住在途账与带宽；单次 Read 有内核 15 秒读超时管着，逐块检查总预算即截断。</summary>
        internal const long SegmentFetchBudgetMs = 60_000;

        /// <summary>经统一出口拉满一片（缓存命中零网络，未命中写穿落盘）；成功/429·403 被拒/
        /// 其他失败各回报纪律账（vc142 起档位引擎闲置，退避与源脾气账照走）。</summary>
        private async Task FetchSegmentAsync(string url)
        {
            // vc144 频控共享闸：主机冷却中零网络快速失败（探活/取票/内核三路共账），
            // 记灌水器自己账 OnRejected（档位归零+源脾气入账，既有退避口径不变）
            if (TaoLive.Foundation.SourceRateGate.IsCooling(url))
            {
                _throttle.OnRejected(url);
                return;
            }
            AndroidX.Media3.DataSource.IDataSource? ds = null;
            try
            {
                ds = _fetchFactory.CreateDataSource()!;
                var builder = new DataSpec.Builder();
                builder.SetUri(Android.Net.Uri.Parse(url));
                ds.Open(builder.Build());
                var budget = System.Diagnostics.Stopwatch.StartNew();
                var buf = new byte[64 * 1024];
                while (await Task.Run(() => ds.Read(buf, 0, buf.Length)).ConfigureAwait(false) > 0) // 读满即落盘
                {
                    // vc141 复查修：超预算即弃（走退避账），防无限流分片永久占住在途账与带宽
                    if (budget.ElapsedMilliseconds > SegmentFetchBudgetMs)
                        throw new TimeoutException($"segment fetch budget {SegmentFetchBudgetMs}ms exceeded");
                }
                lock (_gate) _filled.Add(url);
                _throttle.OnGood(url);
                TaoLive.Foundation.SourceRateGate.OnOk(url); // vc144：拉片成功清共享账
            }
            catch (Exception ex)
            {
                // 429/403=源拒绝：档位归零+源脾气入账；其他失败=per-URL 指数退避
                if (ex is AndroidX.Media3.DataSource.HttpDataSourceInvalidResponseCodeException http
                    && (http.ResponseCode == 429 || http.ResponseCode == 403))
                {
                    _throttle.OnRejected(url);
                    TaoLive.Foundation.SourceRateGate.OnRejected(url); // vc144：被拒记共享账，整主机进冷却窗
                }
                else
                {
                    _throttle.OnFailed(url);
                }
            }
            finally
            {
                try { ds?.Close(); } catch { /* 回收失败无害 */ }
            }
        }
    }

    /// <summary>
    /// 备源攒水器（vc139 老板令「先攒后切」）：备线票焐热后，后台把备源的水灌进缓存——
    /// 拉备源清单（最多 3 试，全败静默放弃=备源也死，主源继续）→ 解析分片表 →
    /// 经缓存工厂顺灌分片（读满即落盘），攒满目标秒数回调 onReady（会话层执行切换）。
    /// 单路顺序（同一时刻只发一路），分片失败歇 5 秒灌下一片（分片独立不互相挡）；
    /// Stop 随新请求/换台作废。任何失败静默，不触发自救（既有自救链兜底）。
    /// </summary>
    internal sealed class BackupFiller
    {
        private const int PlaylistAttempts = 3;      // 清单最多试 3 次，全败=备源死，放弃
        private const long RetryDelayMs = 5_000;     // 分片失败歇 5 秒灌下一片

        private readonly IDataSourceFactory _fetchFactory;
        private readonly string _playlistUrl;
        private readonly int _targetAheadSec;
        private readonly Action? _onReady;
        private readonly CancellationTokenSource _cts = new();

        public BackupFiller(IDataSourceFactory fetchFactory, string playlistUrl, int targetAheadSec, Action? onReady)
        {
            _fetchFactory = fetchFactory;
            _playlistUrl = playlistUrl;
            _targetAheadSec = targetAheadSec;
            _onReady = onReady;
        }

        public void Start() => _ = Task.Run(Run);

        public void Stop()
        {
            try { _cts.Cancel(); } catch { /* 重复取消无害 */ }
        }

        private async Task Run()
        {
            try
            {
                var ct = _cts.Token;
                // 1. 拉备源清单（最多 3 试）
                List<HlsSegmentInfo>? segs = null;
                for (int attempt = 0; attempt < PlaylistAttempts && !ct.IsCancellationRequested; attempt++)
                {
                    var text = await FetchTextAsync(ct).ConfigureAwait(false);
                    if (text is not null && HlsPlaylistInspector.TryParse(text, _playlistUrl, out var parsed))
                    {
                        segs = parsed;
                        break;
                    }
                    if (text is not null) break; // 拉到了但解析不出（主清单/残包）：不空试，放弃
                    await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMs), ct).ConfigureAwait(false);
                }
                if (segs is null) return; // 备源清单拉不到：静默放弃，主源继续

                // 2. 顺灌分片到攒满目标秒数
                double aheadSec = 0;
                foreach (var seg in segs)
                {
                    if (ct.IsCancellationRequested || aheadSec >= _targetAheadSec) break;
                    if (await FillSegmentAsync(seg.Url, ct).ConfigureAwait(false))
                    {
                        aheadSec += seg.DurationSec;
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMs), ct).ConfigureAwait(false); // 失败歇一下灌下一片
                    }
                }
                if (aheadSec >= _targetAheadSec && !ct.IsCancellationRequested) _onReady?.Invoke();
            }
            catch (System.OperationCanceledException) { }
            catch
            {
                // 备源攒水失败静默：主源继续，既有自救链兜底
            }
        }

        /// <summary>拉备源清单全文（裸内层 HTTP，UA 对齐口径原样）。vc141 复查修：加 30 秒总预算，
        /// 防无限流响应永久占住攒水任务。</summary>
        private async Task<string?> FetchTextAsync(CancellationToken ct)
        {
            AndroidX.Media3.DataSource.IDataSource? ds = null;
            try
            {
                ds = RacingDataSourceFactory.CreateInner();
                var builder = new DataSpec.Builder();
                builder.SetUri(Android.Net.Uri.Parse(_playlistUrl));
                ds.Open(builder.Build());
                using var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                var budget = System.Diagnostics.Stopwatch.StartNew();
                int read;
                while ((read = await Task.Run(() => ds.Read(buf, 0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    if (budget.ElapsedMilliseconds > 30_000) return null; // 超预算：按拉不到处理
                    ms.Write(buf, 0, read);
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch { return null; }
            finally
            {
                try { ds?.Close(); } catch { /* 回收失败无害 */ }
            }
        }

        /// <summary>经统一出口拉满一片（缓存命中零网络，未命中写穿落盘），成功 true。
        /// vc141 复查修：加 60 秒总预算（同灌水器口径），防无限流分片永久占住攒水任务。</summary>
        private async Task<bool> FillSegmentAsync(string url, CancellationToken ct)
        {
            AndroidX.Media3.DataSource.IDataSource? ds = null;
            try
            {
                ds = _fetchFactory.CreateDataSource()!;
                var builder = new DataSpec.Builder();
                builder.SetUri(Android.Net.Uri.Parse(url));
                ds.Open(builder.Build());
                var buf = new byte[64 * 1024];
                var budget = System.Diagnostics.Stopwatch.StartNew();
                int read;
                while ((read = await Task.Run(() => ds.Read(buf, 0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    if (budget.ElapsedMilliseconds > 60_000) return false; // 超预算：按失败处理
                }
                return true;
            }
            catch { return false; }
            finally
            {
                try { ds?.Close(); } catch { /* 回收失败无害 */ }
            }
        }
    }

    /// <summary>
    /// 缓存旁路工厂（vc144 老板令「一次性改到位」）：包播放器侧缓存工厂，产出旁路数据源——
    /// 缓存层读写抛 CacheCacheException/空指针（vc140 真机病灶：SimpleCache 索引坏账永久记异常、
    /// 每次取片秒抛）时，本实例降级直连 upstream 并触发 <see cref="MediaCache.Heal"/> 自愈重建，
    /// 缓存病了不堵播放路，直连只是自愈窗内临时便道。缓存健康时零开销直通。
    /// upstream 的 HTTP 4xx 原样上抛（会话层换票/换源既有口径不变），旁路只接缓存层自身的病。
    /// </summary>
    internal sealed class CacheBypassFactory : Java.Lang.Object, IDataSourceFactory
    {
        private readonly IDataSourceFactory _cacheFactory;
        private readonly IDataSourceFactory _upstream;

        public CacheBypassFactory(IDataSourceFactory cacheFactory, IDataSourceFactory upstream)
        {
            _cacheFactory = cacheFactory ?? throw new ArgumentNullException(nameof(cacheFactory));
            _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        }

        public IDataSource CreateDataSource()
            => new CacheBypassDataSource(_cacheFactory.CreateDataSource()!, _upstream);
    }

    /// <summary>旁路数据源（vc144）：Open 阶段缓存层抛 CacheException/空指针即切直连重开；
    /// 已切直连后 Read/Close 全走直连实例（本实例生命周期内不回头，防反复在病缓存上撞）。</summary>
    internal sealed class CacheBypassDataSource : Java.Lang.Object, IDataSource
    {
        private readonly IDataSource _cacheInner;
        private readonly IDataSourceFactory _upstreamFactory;
        private readonly List<ITransferListener> _listeners = new();
        private IDataSource? _fallback;
        private IDataSource _active;
        private DataSpec? _lastSpec; // vc145：取址兜底（内层 getUri 为 null 时回退请求址，防清单加载器空指针）

        public CacheBypassDataSource(IDataSource cacheInner, IDataSourceFactory upstreamFactory)
        {
            _cacheInner = cacheInner ?? throw new ArgumentNullException(nameof(cacheInner));
            _upstreamFactory = upstreamFactory ?? throw new ArgumentNullException(nameof(upstreamFactory));
            _active = _cacheInner;
        }

        public long Open(DataSpec? dataSpec)
        {
            _lastSpec = dataSpec;
            try
            {
                return _active.Open(dataSpec);
            }
            catch (Exception ex) when (IsCacheSickness(ex))
            {
                // vc144：缓存层自身的病（索引坏账/写路径空指针）→ 降级直连重开，同时触发自愈重建
                // （释放旧实例+拔病索引+清统一出口，窗后全新缓存上岗——直连只是自愈窗内临时便道）；
                // upstream 的 HTTP 错误（换票壳要接的 4xx）不在旁路范围，原样上抛
                MediaCache.Heal(ex.GetType().Name);
                try { _cacheInner.Close(); } catch { /* 关闭失败无害 */ }
                var fallback = _upstreamFactory.CreateDataSource()!;
                foreach (var l in _listeners) fallback.AddTransferListener(l);
                _fallback = fallback;
                _active = fallback;
                Android.Util.Log.Warn("TaoLive", $"[缓存] 缓存层病降级直连：{ex.GetType().Name}: {ex.Message}");
                return fallback.Open(dataSpec);
            }
        }

        /// <summary>旁路判据：只接缓存层自身的病（CacheCacheException=索引/写账坏账，media3 绑定摊平名；
        /// NullPointer=写路径空指针 vc140 真机病灶）。HTTP 状态错/网络错是 upstream 的账，交换票壳与会话层，不旁路。</summary>
        private static bool IsCacheSickness(Exception ex)
            => ex is CacheCacheException || ex is Java.Lang.NullPointerException;

        public int Read(byte[]? buffer, int offset, int length) => _active.Read(buffer, offset, length);

        public void Close()
        {
            try { _active.Close(); } catch { /* 关闭失败无害 */ }
        }

        public Android.Net.Uri? Uri => _active.Uri ?? _lastSpec?.Uri;

        public IDictionary<string, IList<string>>? ResponseHeaders => _active.ResponseHeaders;

        public void AddTransferListener(ITransferListener? transferListener)
        {
            _listeners.Add(transferListener!);
            _active.AddTransferListener(transferListener);
        }
    }
}
