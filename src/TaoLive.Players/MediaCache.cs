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
/// 「永远满 60 秒」：<see cref="CacheFiller"/> 即唯一水位大脑——后台按试探档位
/// （<see cref="FillThrottle"/>：默认单路、水位不涨逐路加、封顶 3 路、被拒即缩）把未播内容
/// 顺灌到满 60 秒；水满收兵，清单滚动或消费前推即再灌。
/// 「吃了就清」：LRU 256MB 兜底 + 定期清扫——最近 120 秒（60 秒水位窗+余量）没被读过的分片
/// （已播过/已换台作废）清出；水位窗内正在吃和即将吃的绝不清。缓存目录随进程存活，
/// 内核实例重建不清（这正是「留」的关键）。
/// </summary>
public static class MediaCache
{
    /// <summary>缓存上限（字节）：LRU 淘汰兜底，60 秒水位窗体量远小于此值。</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>分片存活期（毫秒）：最后一次被读（吃）超过此时长的分片清出——60 秒水位窗内
    /// 的分片最迟每 60 秒被吃一次（touch 刷新），120 秒线只清身后已播过/已作废的水。</summary>
    private const long SliceTtlMs = 120_000;

    /// <summary>清扫周期（毫秒）：定期扫缓存把过期分片清掉。</summary>
    private const long SweepPeriodMs = 10_000;

    /// <summary>缓存水位线（毫秒）：未播内容攒满 60 秒才停灌（老板 2026-11-28 16:53 拍板）。</summary>
    internal const long WaterAheadMs = 60_000;

    private static SimpleCache? _cache;
    private static CacheFiller? _filler;
    private static BackupFiller? _backupFiller;
    // vc139 统一出口（老板 2026-11-28 拍板「预取器收编进统一数据面」）：灌水器/备源攒水/预取器后台
    // 拉片共用本工厂——缓存命中零网络，未命中走竞速+换票壳（无预取层，不递归不互扰）边拉边写穿。
    // 双拉拆台根除：预取器与灌水器谁先到谁落盘，另一方命中缓存，弱线带宽不再烧两遍。
    private static IDataSourceFactory? _fetchFactory;
    private static readonly object _lock = new();

    /// <summary>进程级缓存实例（单例复用）：目录 cacheDir/mediacache，LRU 淘汰 + 定期清扫。</summary>
    public static SimpleCache Get(Context ctx)
    {
        lock (_lock)
        {
            if (_cache is { } c) return c;
            var dir = new Java.IO.File(ctx.CacheDir!.AbsolutePath, "mediacache");
            // 三参构造（非弃用重载）：databaseProvider=null 走文件索引，缓存只在本应用沙箱目录
            _cache = new SimpleCache(dir, new LeastRecentlyUsedCacheEvictor(MaxBytes),
                (AndroidX.Media3.Database.IDatabaseProvider?)null);
            StartSweeper();
            return _cache;
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
        var factory = new CacheDataSource.Factory();
        factory.SetCache(Get(ctx));
        factory.SetUpstreamDataSourceFactory(upstream);
        factory.SetCacheReadDataSourceFactory(new FileDataSource.Factory());
        factory.SetCacheKeyFactory(new PlaylistBypassKeyFactory());
        factory.SetFlags(CacheDataSource.FlagIgnoreCacheOnError);
        // 统一拉片出口：上游换无预取层的裸竞速+换票壳（vc94 并发预取只服务播放器读路径，
        // 后台拉片不再自携预取壳——防递归、防灌水器开流污染预取器消费账）
        var fetch = new CacheDataSource.Factory();
        fetch.SetCache(Get(ctx));
        fetch.SetUpstreamDataSourceFactory(new RacingDataSourceFactory(null));
        fetch.SetCacheReadDataSourceFactory(new FileDataSource.Factory());
        fetch.SetCacheKeyFactory(new PlaylistBypassKeyFactory());
        fetch.SetFlags(CacheDataSource.FlagIgnoreCacheOnError);
        _fetchFactory = fetch;
        // vc140 水位大脑：试探引擎（档位/退避/源脾气）并进灌水器，一份账本管水位
        _filler = new CacheFiller(fetch, new FillThrottle(WaterAheadMs, temper));
        return new ObservingFactory(factory, _filler);
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
    /// 分片 URL 稳定，原 URL 作 key，重连命中续播。</summary>
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

    /// <summary>观测工厂：包住缓存工厂，创建观测数据源——清单全文与分片开流喂灌水器。</summary>
    private sealed class ObservingFactory : Java.Lang.Object, IDataSourceFactory
    {
        private readonly IDataSourceFactory _inner;
        private readonly CacheFiller _filler;
        public ObservingFactory(IDataSourceFactory inner, CacheFiller filler)
        {
            _inner = inner;
            _filler = filler;
        }
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

        public ObservingDataSource(IDataSource inner, CacheFiller filler)
        {
            _inner = inner;
            _filler = filler;
        }

        public long Open(DataSpec? dataSpec)
        {
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

        public Android.Net.Uri? Uri => _inner.Uri;
        public IDictionary<string, IList<string>>? ResponseHeaders => _inner.ResponseHeaders;
        public void AddTransferListener(ITransferListener? transferListener) => _inner.AddTransferListener(transferListener);
    }

    /// <summary>
    /// 缓存灌水器=唯一水位大脑（vc140，老板 2026-11-28 拍板「大手术一次到位」——vc94 试探并发
    /// 引擎收编进灌水器，<see cref="FillThrottle"/> 管档位与退避，本类管分片账与水位）：
    /// - 清单换代：观测壳喂全文，解析分片表（复用 <see cref="HlsPlaylistInspector"/>）；
    ///   换台（清单地址变）旧账全清+试探档位归零；同台滚动只重整已灌/在途/退避账；
    /// - 消费记账：播放器开流某分片即前推消费位置（幂等不回退）；
    /// - 顺灌：每拍按档位 <c>budget = 1 + ProbeLevel</c> 发并发路（每路经统一出口
    ///   <see cref="HlsPlaylistInspector"/> 同款缓存工厂拉满写穿落盘）——第 0 路是基本盘不做
    ///   host 保守检查，第 1 路起避开保守 host（源脾气账）；未播内容攒满 60 秒收兵；
    /// - 试探纪律（<see cref="FillThrottle"/>）：默认单路；水位采样不涨才逐路加（封顶 3 路）；
    ///   429/403 被拒即档位归零+源脾气入账；其他失败 per-URL 指数退避（2 秒~5 分钟）；
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

        public CacheFiller(IDataSourceFactory fetchFactory, FillThrottle throttle)
        {
            _fetchFactory = fetchFactory;
            _throttle = throttle;
        }

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
            if (_loop is null) _loop = Task.Run(FillLoop);
            try { _signal.Release(); } catch { /* 信号满位无害 */ }
        }

        private async Task FillLoop()
        {
            while (true)
            {
                try { await _signal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* 等待异常无害，下轮再灌 */ }
                try { PumpTick(); }
                catch { /* 灌水失败不外抛，退避账兜底 */ }
            }
        }

        /// <summary>顺灌一拍：锁内算磁盘领先水位（aheadMs）喂试探引擎采样——水满收兵；否则按档位
        /// <c>budget = 1 + ProbeLevel</c> 从消费位之后顺序选未灌/未在途/未退避分片发并发路
        /// （第 0 路基本盘不做 host 保守检查，第 1 路起避开保守 host）；锁外逐路异步拉满落盘。</summary>
        private void PumpTick()
        {
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
                _throttle.OnSample(aheadMs);
                if (aheadMs >= WaterAheadMs) return; // 水满收兵（档位保留，回落自然收敛）

                int budget = 1 + _throttle.ProbeLevel;
                picks = new List<string>(budget);
                for (int i = consumed + 1; i < segs.Count && picks.Count < budget; i++)
                {
                    var url = segs[i].Url;
                    if (_filled.Contains(url) || _inFlight.Contains(url) || _throttle.IsBackedOff(url)) continue;
                    // 第 1 路起避开保守 host（源脾气账：被拒过的 host 先让基本盘顶）
                    if (picks.Count > 0 && _throttle.IsHostConservative(SourceTemperStore.HostOf(url))) continue;
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

        /// <summary>经统一出口拉满一片（缓存命中零网络，未命中写穿落盘）；成功/429·403 被拒/
        /// 其他失败各回报试探引擎（档位、退避、源脾气各按账走）。</summary>
        private async Task FetchSegmentAsync(string url)
        {
            AndroidX.Media3.DataSource.IDataSource? ds = null;
            try
            {
                ds = _fetchFactory.CreateDataSource()!;
                var builder = new DataSpec.Builder();
                builder.SetUri(Android.Net.Uri.Parse(url));
                ds.Open(builder.Build());
                var buf = new byte[64 * 1024];
                while (await Task.Run(() => ds.Read(buf, 0, buf.Length)).ConfigureAwait(false) > 0) { } // 读满即落盘
                lock (_gate) _filled.Add(url);
                _throttle.OnGood(url);
            }
            catch (Exception ex)
            {
                // 429/403=源拒绝：档位归零+源脾气入账；其他失败=per-URL 指数退避
                if (ex is AndroidX.Media3.DataSource.HttpDataSourceInvalidResponseCodeException http
                    && (http.ResponseCode == 429 || http.ResponseCode == 403))
                {
                    _throttle.OnRejected(url);
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

        /// <summary>拉备源清单全文（裸内层 HTTP，UA 对齐口径原样）。</summary>
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
                int read;
                while ((read = await Task.Run(() => ds.Read(buf, 0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
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

        /// <summary>经统一出口拉满一片（缓存命中零网络，未命中写穿落盘），成功 true。</summary>
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
                int read;
                while ((read = await Task.Run(() => ds.Read(buf, 0, buf.Length), ct).ConfigureAwait(false)) > 0) { }
                return true;
            }
            catch { return false; }
            finally
            {
                try { ds?.Close(); } catch { /* 回收失败无害 */ }
            }
        }
    }
}
