using System;
using System.IO;
using System.Linq;
using TaoLive.Foundation;

namespace TaoLive.Players;

/// <summary>
/// 清单观测数据源（vc140 水位大脑合并后瘦身，老板 2026-11-28 拍板「大手术一次到位」）：
/// - 清单 Open：读全文 → 多档清单回调最低 BANDWIDTH（低档起播，vc94 第 1 刀）；单档/无带宽不回调（防黑屏）；
/// - 分片 Open：一律透传内层（vc94 内存预取路径随旧预取器拆除——磁盘缓存是唯一数据面，
///   灌水器经统一出口写穿落盘，播放器读缓存命中，不再有第二份内存账本）。
/// 链路：TicketSwap(Racing(ParallelPrefetch(inner)))——竞速与换票语义原样保留。
/// </summary>
public sealed class ParallelPrefetchDataSource : Java.Lang.Object, AndroidX.Media3.DataSource.IDataSource
{
    private const int ReadBufSize = 64 * 1024;

    private readonly Func<AndroidX.Media3.DataSource.IDataSource> _innerFactory;
    private readonly Action<long>? _minVariantObserver;
    private readonly System.Collections.Generic.List<AndroidX.Media3.DataSource.ITransferListener> _listeners = new();
    private AndroidX.Media3.DataSource.IDataSource? _inner;
    private AndroidX.Media3.DataSource.DataSpec? _lastSpec; // vc145：取址兜底用
    private MemoryStream? _served; // 清单全文的字节流服务

    public ParallelPrefetchDataSource(
        Func<AndroidX.Media3.DataSource.IDataSource> innerFactory,
        Action<long>? minVariantObserver = null)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
        _minVariantObserver = minVariantObserver;
    }

    /// <summary>清单判据（与 RacingDataSource.IsSegment 口径互补）：按 Uri 路径端判 .m3u8，
    /// 避免查询串带 .m3u8 字样的分片被误判为清单。全限定 System.Uri（本类 Uri 属性遮蔽类型名）。</summary>
    public static bool IsPlaylist(string? url)
    {
        if (url is null) return false;
        var path = System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public long Open(AndroidX.Media3.DataSource.DataSpec? dataSpec)
    {
        _lastSpec = dataSpec; // vc145：取址兜底（内层 getUri 为 null 时回退请求址，防清单加载器空指针）
        var url = dataSpec?.Uri?.ToString();
        // 零偏移才走本地服务（清单全文）；带偏移的重开一律透传内层，字节对齐不赌
        if (url is not null && dataSpec!.Position == 0 && IsPlaylist(url))
        {
            return OpenPlaylist(dataSpec, url);
        }
        var inner = _innerFactory();
        _inner = inner;
        foreach (var l in _listeners) inner.AddTransferListener(l);
        return inner.Open(dataSpec);
    }

    /// <summary>清单全文读取并服务：多档清单回调最低带宽（低档起播）。</summary>
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

            if (_minVariantObserver is { } obs
                && HlsPlaylistInspector.TryParseVariantBandwidths(text, out var bandwidths)
                && bandwidths.Count > 1) // 只有多档才降：单档/无带宽信息不降档（防黑屏）
            {
                obs(bandwidths.Min());
            }

            ServeBytes(bytes);
            return bytes.LongLength;
        }
        finally
        {
            try { inner.Close(); } catch { /* 清单路关闭失败无害 */ }
        }
    }

    /// <summary>换缝服务流：先释放旧流再挂新流，防覆盖泄漏（vc96 P3）。</summary>
    private void ServeBytes(byte[] bytes)
    {
        try { _served?.Dispose(); } catch { /* 旧流释放失败无害 */ }
        _served = new MemoryStream(bytes, writable: false);
    }

    public int Read(byte[]? buffer, int offset, int count)
    {
        if (_served is { } served)
        {
            if (count <= 0) return 0;
            int read = served.Read(buffer!, offset, count);
            // vc96 第 19 轮：内存流 EOF 返回 0 违反 Media3 DataSource 契约——length>0 时读到流末尾
            // 须回 RESULT_END_OF_INPUT(-1)（内层 HTTP 源同口径）。返回 0 会被内核装载线程当
            // 「读了 0 字节」立即重读，清单全文读到末尾即空转打满 CPU 且永远读不完。
            if (read == 0) return AndroidX.Media3.Common.C.ResultEndOfInput;
            return read;
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

    public Android.Net.Uri? Uri => _inner?.Uri ?? _lastSpec?.Uri;

    public System.Collections.Generic.IDictionary<string, System.Collections.Generic.IList<string>>? ResponseHeaders =>
        _inner?.ResponseHeaders;

    public void AddTransferListener(AndroidX.Media3.DataSource.ITransferListener? transferListener)
        => _listeners.Add(transferListener!);
}
