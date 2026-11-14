using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AllPlayer.BLL;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// r3 直播 HLS 本地中转：源清单/分片全走本机代理，分片账目过 AiBufferPool——
    /// 分片「到手」即入池记账；起播先攒初始缓冲（10-20s 池口径）再放行；
    /// 落后 60s 封顶池内自动直出；过热/低电直出、降档建议回写 AI 档位由外层守卫轮询喂入。
    /// v1 边界（如实注明）：AiSegmentRunner 现只吃 MP4 封装（MediaMuxer 不支持 ts），
    /// 直播 ts 分片走池的账目+保险路径=直出不挡播；加工插槽留好，runner 支持 ts 后自动接上。
    /// ReportPlayed 以分片送达播放端近似（libvlc 内部拉流进度不外露），账目口径见发布说明。
    /// </summary>
    internal sealed class LiveHlsRelay : IDisposable
    {
        private const int MaxCachedSegs = 32;          // 内存分片缓存上限 (~1-2MB/片, 封顶约 64MB)
        private const int InitialPreheatTimeoutMs = 20_000;  // 初始缓冲等待硬超时=强制放行

        private readonly AiBufferPool _pool;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();
        private readonly Dictionary<int, byte[]> _segCache = new();
        private readonly Queue<int> _segOrder = new();
        private string _srcUrl = "";
        private List<(string Key, string Value)> _headers = new();
        private string? _cachedManifestBody;            // 源清单缓存（重写前的原始行）
        private List<double> _segDurations = new();     // 逐段秒数（EXTINF）
        private int _readyPort;

        public int Port => _readyPort;

        public LiveHlsRelay(AiBufferPool pool) => _pool = pool;

        /// <summary>起服务：返回本地清单地址。失败抛异常由调用方兜底直连。</summary>
        public string Start(string srcUrl, List<(string Key, string Value)> headers)
        {
            _srcUrl = srcUrl;
            _headers = headers ?? new();
            _cts = new CancellationTokenSource();
            // HttpListener 拿不到随机端口绑定信息——改为显式挑端口：从 47000 起探测
            Exception? lastErr = null;
            for (int p = 47000; p < 47100; p++)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{p}/");
                    l.Start();
                    _listener = l;
                    _readyPort = p;
                    break;
                }
                catch (Exception ex) { lastErr = ex; }
            }
            if (_listener == null) throw new InvalidOperationException($"直播中转起服务失败: {lastErr?.Message}");
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            return $"http://127.0.0.1:{_readyPort}/live.m3u8";
        }

        private async void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener!.GetContextAsync(); }
                catch { return; }   // 停服务/异常即退出
                _ = Task.Run(() => Handle(ctx), ct);
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (path.EndsWith("/live.m3u8"))
                    await ServeManifest(ctx);
                else if (path.StartsWith("/seg/"))
                    await ServeSegment(ctx, path);
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }
            catch { try { ctx.Response.Close(); } catch { } }
        }

        private async Task ServeManifest(HttpListenerContext ctx)
        {
            var body = await FetchTextAsync(_srcUrl);
            if (body == null) { ctx.Response.StatusCode = 502; ctx.Response.Close(); return; }
            // 首次取到清单即预热：逐片下载入池, 攒到初始缓冲就绪（或 20s 硬超时强制放行）
            if (_segDurations.Count == 0) await PreheatAsync(body);
            var rewritten = RewriteManifest(body);
            var bytes = Encoding.UTF8.GetBytes(rewritten);
            ctx.Response.ContentType = "application/vnd.apple.mpegurl";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        /// <summary>预热：按清单顺序把分片下载入池, 直到池报初始缓冲就绪（硬超时=强制放行）。</summary>
        private async Task PreheatAsync(string manifest)
        {
            var segs = ParseSegments(manifest);
            _segDurations = segs.ConvertAll(s => s.dur);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < segs.Count && sw.ElapsedMilliseconds < InitialPreheatTimeoutMs; i++)
            {
                await DownloadSeg(i, segs[i].url);
                _pool.ReportProcessed(i, segs[i].dur, segs[i].dur);
                if (_pool.IsInitialBufferReady()) return;   // 10-20s 攒够即放行
            }
        }

        private async Task ServeSegment(HttpListenerContext ctx, string path)
        {
            var idxStr = path.Substring("/seg/".Length);
            if (!int.TryParse(idxStr, out var idx) || idx < 0) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            var data = await GetSegAsync(idx);
            if (data == null) { ctx.Response.StatusCode = 502; ctx.Response.Close(); return; }
            double dur = idx < _segDurations.Count ? _segDurations[idx] : 4.0;
            // 分片送达播放端即视为已播账目（近似口径, 见类注释）；池内落后超 60s 会自动转直出
            _pool.EnqueueSegment(idx, dur);
            _pool.ReportProcessed(idx, dur, 0);
            _pool.ReportPlayed(idx, dur);
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength64 = data.Length;
            await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
            ctx.Response.Close();
        }

        private async Task<byte[]?> GetSegAsync(int idx)
        {
            lock (_lock)
            {
                if (_segCache.TryGetValue(idx, out var hit)) return hit;
            }
            var segUrl = SegUrlOf(idx);
            if (segUrl == null) return null;
            var data = await FetchBytesAsync(segUrl);
            if (data != null)
            {
                lock (_lock)
                {
                    _segCache[idx] = data; _segOrder.Enqueue(idx);
                    while (_segOrder.Count > MaxCachedSegs && _segOrder.TryDequeue(out var evict))
                        _segCache.Remove(evict);
                }
            }
            return data;
        }

        private string? SegUrlOf(int idx)
        {
            lock (_lock)
            {
                if (_segUrls != null && idx < _segUrls.Count) return _segUrls[idx];
            }
            return null;
        }

        private List<string>? _segUrls;

        private async Task DownloadSeg(int idx, string url)
        {
            if (SegUrlOf(idx) == null) return;   // 未登记不预取
            await GetSegAsync(idx);
        }

        private List<(string url, double dur)> ParseSegments(string manifest)
        {
            var result = new List<(string, double)>();
            lock (_lock) { if (_segUrls != null && _segUrls.Count > 0) return result; }
            var baseUri = new Uri(_srcUrl);
            var lines = manifest.Split('\n');
            double pending = 4.0;
            var urls = new List<string>(); var durs = new List<double>();
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(line, @"#EXTINF:\s*([0-9.]+)", RegexOptions.IgnoreCase);
                    pending = m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 4.0;
                }
                else if (line.Length > 0 && !line.StartsWith("#"))
                {
                    urls.Add(new Uri(baseUri, line).AbsoluteUri);
                    durs.Add(pending);
                    pending = 4.0;
                }
            }
            lock (_lock) { _segUrls = urls; _segDurations = durs; }
            var r = new List<(string, double)>();
            for (int i = 0; i < urls.Count; i++) r.Add((urls[i], durs[i]));
            return r;
        }

        /// <summary>清单改写：分片行替换为本机 /seg/N, 其余原样（含 #EXT-X 头）。</summary>
        private string RewriteManifest(string manifest)
        {
            var baseUri = new Uri(_srcUrl);
            var lines = manifest.Split('\n');
            var sb = new StringBuilder();
            int segIdx = 0;
            lock (_lock) { if (_segUrls == null || _segUrls.Count == 0) { _segUrls = new(); } }
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0 && !line.StartsWith("#"))
                {
                    var abs = new Uri(baseUri, line).AbsoluteUri;
                    lock (_lock)
                    {
                        if (_segUrls!.Count <= segIdx) _segUrls.Add(abs);
                    }
                    sb.Append($"/seg/{segIdx}").Append('\n');
                    segIdx++;
                }
                else sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        private async Task<string?> FetchTextAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (k, v) in _headers) req.Headers.TryAddWithoutValidation(k, v);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        private async Task<byte[]?> FetchBytesAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (k, v) in _headers) req.Headers.TryAddWithoutValidation(k, v);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return null;
                using var ms = new MemoryStream();
                await resp.Content.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            lock (_lock) { _segCache.Clear(); _segOrder.Clear(); _segUrls = null; }
            _segDurations = new();
        }

        public void Dispose() => Stop();
    }
}
