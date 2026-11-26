using System;
using System.IO;
using System.Linq;
using TaoLive.Foundation;

namespace TaoLive.Players;

/// <summary>
/// 并发预取数据源（vc94 激进版，老板 2026-11-26 拍板，Android 壳）：
/// - 清单 Open：读全文 → 解析分片表/变体带宽 → <see cref="ParallelPrefetcher.UpdatePlaylist"/> 换代，
///   多档清单回调最低 BANDWIDTH（低档起播，vc94 第 1 刀）；单档/无带宽不回调（防黑屏）；
/// - 分片 Open：预取缓存命中即服务（零网络），未命中走内层正常拉流；分片开流即消费记账；
/// - 预取下载走裸 HTTP 内层（后台任务），429/403 被拒记源脾气，冷却期该 host 降回单连接；
/// - 水位账=内核 BufferedMs 注入 + 缓存领先量，≥watermark 停发新请求（水满才撤）。
/// 链路：TicketSwap(Racing(ParallelPrefetch(inner)))——竞速与换票语义原样保留。
/// </summary>
public sealed class ParallelPrefetchDataSource : Java.Lang.Object, AndroidX.Media3.DataSource.IDataSource
{
    private const int ReadBufSize = 64 * 1024;

    private readonly Func<AndroidX.Media3.DataSource.IDataSource> _innerFactory;
    private readonly ParallelPrefetcher _prefetcher;
    private readonly Action<long>? _minVariantObserver;
    private readonly System.Collections.Generic.List<AndroidX.Media3.DataSource.ITransferListener> _listeners = new();
    private AndroidX.Media3.DataSource.IDataSource? _inner;
    private MemoryStream? _served; // 缓存命中/清单全文的字节流服务

    public ParallelPrefetchDataSource(
        Func<AndroidX.Media3.DataSource.IDataSource> innerFactory,
        ParallelPrefetcher prefetcher,
        Action<long>? minVariantObserver = null)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
        _prefetcher = prefetcher ?? throw new ArgumentNullException(nameof(prefetcher));
        _minVariantObserver = minVariantObserver;
    }

    /// <summary>清单判据（与 RacingDataSource.IsSegment 口径互补）：URL 含 .m3u8 即清单。</summary>
    public static bool IsPlaylist(string? url) =>
        url is not null && url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    public long Open(AndroidX.Media3.DataSource.DataSpec? dataSpec)
    {
        var url = dataSpec?.Uri?.ToString();
        // 零偏移才走本地服务（缓存/清单全文）；带偏移的重开一律透传内层，字节对齐不赌
        if (url is not null && dataSpec!.Position == 0)
        {
            if (IsPlaylist(url)) return OpenPlaylist(dataSpec, url);
            if (_prefetcher.TryTake(url, out var cached) && cached is { } bytes)
            {
                _served = new MemoryStream(bytes, writable: false);
                _prefetcher.NotifyConsumed(url);
                _prefetcher.Pump(); // 取走一片即补发新请求（水满才撤判据在预取器内）
                return bytes.LongLength;
            }
        }
        var inner = _innerFactory();
        _inner = inner;
        foreach (var l in _listeners) inner.AddTransferListener(l);
        long opened = inner.Open(dataSpec);
        if (url is not null && !IsPlaylist(url))
        {
            _prefetcher.NotifyConsumed(url); // 分片开流即消费（竞速重开同片幂等）
            _prefetcher.Pump();
        }
        return opened;
    }

    /// <summary>清单全文读取并换代：解析成功即换代排片；多档清单回调最低带宽（低档起播）。</summary>
    private long OpenPlaylist(AndroidX.Media3.DataSource.DataSpec dataSpec, string url)
    {
        var inner = _innerFactory();
        try
        {
            foreach (var l in _listeners) inner.AddTransferListener(l);
            inner.Open(dataSpec);
            using var ms = new MemoryStream();
            var buf = new byte[ReadBufSize];
            int read;
            while ((read = inner.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, read);
            var bytes = ms.ToArray();
            var text = System.Text.Encoding.UTF8.GetString(bytes);

            _prefetcher.UpdatePlaylist(text, url);
            if (_minVariantObserver is { } obs
                && HlsPlaylistInspector.TryParseVariantBandwidths(text, out var bandwidths)
                && bandwidths.Count > 1) // 只有多档才降：单档/无带宽信息不降档（防黑屏）
            {
                obs(bandwidths.Min());
            }

            _served = new MemoryStream(bytes, writable: false);
            _prefetcher.Pump();
            return bytes.LongLength;
        }
        finally
        {
            try { inner.Close(); } catch { /* 清单路关闭失败无害 */ }
        }
    }

    public int Read(byte[]? buffer, int offset, int count)
    {
        if (_served is { } served)
        {
            if (count <= 0) return 0;
            return served.Read(buffer!, offset, count);
        }
        return _inner!.Read(buffer, offset, count);
    }

    public void Close()
    {
        try { _inner?.Close(); } catch { /* 关闭期异常无害 */ }
        _inner = null;
        try { _served?.Dispose(); } catch { /* 同上 */ }
        _served = null;
    }

    public Android.Net.Uri? Uri => _inner?.Uri;

    public System.Collections.Generic.IDictionary<string, System.Collections.Generic.IList<string>>? ResponseHeaders =>
        _inner?.ResponseHeaders;

    public void AddTransferListener(AndroidX.Media3.DataSource.ITransferListener? transferListener)
        => _listeners.Add(transferListener!);

    /// <summary>
    /// 预取请求统一出口（ExoPlayerPlayer 装配时挂到 <see cref="ParallelPrefetcher.FetchRequested"/>）：
    /// 后台任务走裸 HTTP 内层整片下载，成功回报 ReportFetched；429/403 被拒记脾气（rejected:true），
    /// 其余失败原样记账（rejected:false）。任何异常不外抛（预取是让路角色，绝不打断在播）。
    /// </summary>
    internal static void HandleFetchRequested(object? sender, FetchRequest request)
    {
        if (sender is not ParallelPrefetcher prefetcher) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            AndroidX.Media3.DataSource.IDataSource? ds = null;
            try
            {
                ds = RacingDataSourceFactory.CreateInner();
                var builder = new AndroidX.Media3.DataSource.DataSpec.Builder();
                builder.SetUri(Android.Net.Uri.Parse(request.Url));
                ds.Open(builder.Build());
                using var ms = new MemoryStream();
                var buf = new byte[ReadBufSize];
                int read;
                long total = 0;
                while ((read = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                    total += read;
                    if (total > ParallelPrefetcher.CacheByteCap) break; // 单片超顶：停读，回报按顶满弃
                }
                prefetcher.ReportFetched(request.Url, ms.ToArray(), request.Generation);
            }
            catch (AndroidX.Media3.DataSource.HttpDataSourceInvalidResponseCodeException ex)
                when (ex.ResponseCode is 429 or 403)
            {
                prefetcher.ReportFailed(request.Url, rejected: true, request.Generation);
            }
            catch
            {
                prefetcher.ReportFailed(request.Url, rejected: false, request.Generation);
            }
            finally
            {
                try { ds?.Close(); } catch { /* 回收失败无害 */ }
            }
        });
    }
}
