using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AllPlayer.Model;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 业务层 - m3u8 (HLS) 边播边存服务 (v3.24, 集成在磁力链路上: 同一输入框、同一列表、同一边播边存开关)。
    /// 流程: 拉清单 -> 主清单选最高码率 -> 本机伺服改写后的清单 (分片指向本机) ->
    ///       播放器按需取分片, 后台按序预取落盘; saveDir 非空 = 边播边存 (不清理), 否则存临时目录。
    /// </summary>
    public class HlsStreamService
    {
        private static readonly System.Net.Http.HttpClient Http = CreateHttp();
        private static readonly object _proxyLock = new object();
        private static System.Net.Http.HttpClient? _proxiedHttp;
        private static string _proxiedKey = "";

        private static System.Net.Http.HttpClient CreateHttp(System.Net.Http.HttpClientHandler? handler = null)
        {
            var c = handler != null
                ? new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) }
                : new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // v3.24.1: 不少源站要求浏览器身份, 素身请求直接 403; 补 UA + 接受清单类型
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.apple.mpegurl");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/x-mpegURL");
            c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            return c;
        }

        /// <summary>v3.25.0: 用户自配代理时经代理的标准通道 (配置变化才重建); 直连时用共享实例</summary>
        private static System.Net.Http.HttpClient PickHttp()
        {
            var proxy = NetOpt.BuildWebProxy();
            if (proxy == null) return Http;
            var key = $"{AppSettings.Current.ProxyHost}:{AppSettings.Current.ProxyPort}";
            lock (_proxyLock)
            {
                if (_proxiedHttp == null || _proxiedKey != key)
                {
                    try { _proxiedHttp?.Dispose(); } catch { }
                    _proxiedHttp = CreateHttp(new System.Net.Http.HttpClientHandler { Proxy = proxy, UseProxy = true });
                    _proxiedKey = key;
                }
                return _proxiedHttp;
            }
        }

        private LocalServer? _server;
        private CancellationTokenSource? _cts;
        private string? _savePath;
        private bool _persist; // true = 边播边存, 停止后不清理文件
        private Uri? _mediaUrl; // 当前媒体清单地址 (直播重取用)
        private bool _isLive;
        private readonly List<HlsPlaylistParser.Segment> _segments = new List<HlsPlaylistParser.Segment>();
        private readonly object _segLock = new object();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _segGates = new ConcurrentDictionary<int, SemaphoreSlim>();
        private long _downloadedCount;
        private long _lastRefreshTick; // v3.24.2: 直播清单刷新节流 (毫秒时钟)
        private List<HlsPlaylistParser.Variant>? _variants; // v3.25.0: 主清单变体 (可能有多个码率档)
        private volatile bool _downgraded; // v3.25.0: 已单步降档, 只降一次
        private long _seg0Ms = -1; // v3.25.2: 起播抢跑实测首片耗时 (毫秒); -1 = 未记上, 弱网探测自己补测
        private System.Diagnostics.Stopwatch? _seg0Sw; // v3.25.3: 抢跑超时未落盘时留给探测续测 (整段等待算进首片耗时, 防把慢网测成快)

        /// <summary>单调毫秒时钟 (netstandard2.1 没有 TickCount64, 用秒表时间戳换算)</summary>
        private static long NowMs()
            => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        /// <summary>状态文字(给界面显示)</summary>
        public event Action<string>? StatusChanged;
        /// <summary>缓冲进度 0~1 (直播流恒为 0, 不适用)</summary>
        public event Action<double>? BufferProgress;

        /// <summary>地址名 (供列表起名): 从 URL 推断</summary>
        public string? FoundFileName { get; private set; }

        /// <summary>局域网清单地址 (投屏给电视用); 未启动为 null</summary>
        public string? LanStreamUrl => _server?.LanUrl;

        /// <summary>判定是不是 m3u8 播放地址: http(s) 且路径段里带 .m3u8 (查询串/锚点不影响)</summary>
        public static bool IsHlsUrl(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();
            if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            var q = t.IndexOf('?');
            if (q >= 0) t = t.Substring(0, q);
            var h = t.IndexOf('#');
            if (h >= 0) t = t.Substring(0, h);
            return t.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 启动 m3u8 边播边存, 返回可交给播放器的本地清单地址。
        /// saveDir 非空时 = 边播边存模式, 分片直接存到指定目录 (不清理);
        /// 为空时存临时目录, 停止播放后清理。
        /// </summary>
        public async Task<string> StartAsync(string url, string? saveDir, CancellationToken ct)
        {
            if (!IsHlsUrl(url)) throw new ArgumentException("不是有效的 m3u8 地址");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            if (!string.IsNullOrWhiteSpace(saveDir))
            {
                _persist = true;
                // v3.24.2: 分片收进每个片源自己的子目录, 不再直落用户保存目录 ——
                // 否则不同片源的 seg_000000.ts 互相撞名, 第二部会直接命中第一部残留的错误分片
                var name = string.Concat(HlsPlaylistParser.SuggestName(url).Split(Path.GetInvalidFileNameChars()));
                if (name.Length > 40) name = name.Substring(0, 40);
                _savePath = Path.Combine(saveDir, $"{name}-{url.GetHashCode(StringComparison.OrdinalIgnoreCase):x8}");
            }
            else
            {
                _savePath = Path.Combine(Path.GetTempPath(), "AllPlayer", Guid.NewGuid().ToString("N"));
            }
            Directory.CreateDirectory(_savePath);

            var baseUri = new Uri(url.Trim());
            StatusChanged?.Invoke("正在获取 m3u8 清单...");
            var text = await FetchPlaylistAsync(baseUri, token);

            var variants = HlsPlaylistParser.ParseMaster(text, baseUri);
            if (variants.Count > 0)
            {
                _variants = variants; // v3.25.0: 留档主清单变体, 弱网单步降档用 (仅临时模式)
                baseUri = HlsPlaylistParser.PickBestVariant(variants).Url; // persist/temp 都先取最高码率 (画质硬约束)
                StatusChanged?.Invoke("已选最高画质, 拉取分片清单...");
                text = await FetchPlaylistAsync(baseUri, token);
            }

            var media = HlsPlaylistParser.ParseMedia(text, baseUri);
            if (media.UsesByteRanges)
                throw new InvalidOperationException("该清单使用字节区间分片 (BYTERANGE), 暂不支持这种格式");
            if (media.Segments.Count == 0)
                throw new InvalidOperationException("清单里没有可用分片 (可能不是标准 m3u8, 或源站要求特殊请求头)");
            lock (_segLock)
            {
                _segments.AddRange(media.Segments);
                _isLive = media.IsLive;
            }
            _mediaUrl = baseUri;
            FoundFileName = HlsPlaylistParser.SuggestName(url);
            StatusChanged?.Invoke(_isLive
                ? $"直播流, 共收到 {media.Segments.Count} 个分片, 建立边播边存..."
                : $"共 {media.Segments.Count} 个分片, 建立边播边存...");

            _server = new LocalServer(this);
            _server.Start();

            // v3.25.2 黑屏首帧·m3u8: 首片抢跑 —— 清单一就绪先等第 0 片落地 (上限 1.2 秒, 超时放行不拖起播),
            // 播放器随后请求 seg_0 时命中缓存或与同一次下载合并 (信号量去重), 不再冷启动白等一个来回。
            // 抢跑的实测耗时记给弱网探测 (探测再查首片已是缓存, 只能拿这次的真实计时)
            var seg0Sw = System.Diagnostics.Stopwatch.StartNew();
            _seg0Sw = seg0Sw;   // v3.25.3: 抢跑超时的话探测用这块秒表续测, 整段等待不白测
            try
            {
                var prebuild = EnsureSegmentAsync(0, token);
                var done = await Task.WhenAny(prebuild, Task.Delay(1200, token));
                if (done == prebuild) { await prebuild; _seg0Ms = seg0Sw.ElapsedMilliseconds; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }

            // v3.25.0: 自适应降档只许临时模式 (画质硬约束: 边播边存恒最高画质); 单步降一级, 并行探测不拖慢起播
            if (!_persist && _variants != null && _variants.Count > 1)
                _ = Task.Run(() => AdaptiveProbeAsync(token), token);

            _ = Task.Run(() => PrefetchLoop(token), token);
            return _server.LocalUrl;
        }

        public async Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }
            try { _server?.Dispose(); } catch { }
            _server = null;
            await Task.CompletedTask;
            // 边播边存模式下保留文件; 临时目录在退出时由系统清理
            if (!_persist && _savePath != null)
            {
                try { Directory.Delete(_savePath, true); } catch { }
            }
            _cts?.Dispose();
            _cts = null;
        }

        // ---------- 取数助手: HttpClient 取回内容, 顺路响应取消令牌 ----------

        /// <summary>源站给了明确 HTTP 状态码的拒绝 (401/403/404 等), 与连不上是两回事</summary>
        private sealed class UpstreamRejectException : Exception
        {
            public int Status;
            public UpstreamRejectException(int status) { Status = status; }
        }

        /// <summary>取清单: 网络/超时/被拒逐类归一成中文原因, 供界面精确显示</summary>
        private static async Task<string> FetchPlaylistAsync(Uri url, CancellationToken token)
        {
            try
            {
                return await HttpGetStringAsync(url, token);
            }
            catch (UpstreamRejectException ex)
            {
                // 源站明确拒绝: 分状态码说清楚, 用户/开发者一眼看出是权限还是地址问题
                if (ex.Status == 404) throw new InvalidOperationException("源站说清单不存在 (HTTP 404), 地址多半已失效");
                if (ex.Status == 401 || ex.Status == 403) throw new InvalidOperationException($"源站拒绝访问 (HTTP {ex.Status}), 要鉴权或封了本机请求");
                throw new InvalidOperationException($"源站拒绝 (HTTP {ex.Status})");
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("连接超时, 源站没响应");
            }
            catch (Exception ex) when (HasSocketError(ex, SocketError.HostNotFound))
            {
                throw new InvalidOperationException("域名解析不了 (DNS), 网址拼错或域名已注销");
            }
            catch (Exception ex) when (HasSocketError(ex, SocketError.ConnectionRefused))
            {
                throw new InvalidOperationException("源站拒绝连接 (端口不通或被防火墙拦)");
            }
            catch (Exception ex) when (HasSocketError(ex, SocketError.NetworkUnreachable)
                || HasSocketError(ex, SocketError.HostUnreachable))
            {
                throw new InvalidOperationException("网络不可达, 检查 Wi-Fi 或流量");
            }
            catch (Exception)
            {
                throw new InvalidOperationException("连不上清单服务器 (网络异常)");
            }
        }

        /// <summary>沿异常内层链找 Socket 错误码 (分类 DNS/拒绝/不可达用)</summary>
        private static bool HasSocketError(Exception ex, SocketError code)
        {
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
                if (cur is SocketException se && se.SocketErrorCode == code) return true;
            return false;
        }

        private static async Task<string> HttpGetStringAsync(Uri url, CancellationToken token)
        {
            // v3.25.0 快速通道: 未配代理时先走手写 GET (多 DNS 择优 + 连接竞速), 任何异常回落标准通道
            if (!NetOpt.ProxyActive)
            {
                var fast = await NetOpt.TryGetStringAsync(url, token);
                if (fast != null) return fast;
            }
            using (var resp = await PickHttp().GetAsync(url, token))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new UpstreamRejectException((int)resp.StatusCode);
                return await resp.Content.ReadAsStringAsync();
            }
        }

        private static async Task<byte[]> HttpGetBytesAsync(Uri url, CancellationToken token)
        {
            // v3.25.0 快速通道: 同上, 失败回落标准通道
            if (!NetOpt.ProxyActive)
            {
                var fast = await NetOpt.TryGetBytesAsync(url, token);
                if (fast != null) return fast;
            }
            using (var resp = await PickHttp().GetAsync(url, token))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync();
            }
        }

        // ---------- 分片落盘 ----------

        private string SegmentPath(int index)
        {
            Uri? url;
            lock (_segLock)
            {
                if (index < 0)
                {
                    url = _segments.Count > 0 ? _segments[0].MapUrl : null;
                }
                else
                {
                    if (index >= _segments.Count) throw new ArgumentOutOfRangeException(nameof(index));
                    url = _segments[index].Url;
                }
            }
            var ext = ".ts";
            try
            {
                if (url != null)
                {
                    var e = Path.GetExtension(url.AbsolutePath);
                    if (!string.IsNullOrEmpty(e)) ext = e;
                }
            }
            catch { }
            return Path.Combine(_savePath!, index < 0 ? "seg_map" + ext : $"seg_{index:D6}{ext}");
        }

        /// <summary>确保第 index 个分片已落盘 (已存在直接返回; 否则下载, 并发请求合并为一次)</summary>
        private async Task<string> EnsureSegmentAsync(int index, CancellationToken token)
        {
            var path = SegmentPath(index);
            if (File.Exists(path)) return path;

            Uri url;
            lock (_segLock)
            {
                if (index < 0)
                {
                    if (_segments.Count == 0 || _segments[0].MapUrl == null) throw new InvalidOperationException("清单没有初始化段");
                    url = _segments[0].MapUrl!;
                }
                else
                {
                    if (index >= _segments.Count) throw new ArgumentOutOfRangeException(nameof(index));
                    url = _segments[index].Url;
                }
            }

            var gate = _segGates.GetOrAdd(index, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            try
            {
                if (File.Exists(path)) return path;
                var data = await HttpGetBytesAsync(url, token);
                var tmp = path + ".part";
                await File.WriteAllBytesAsync(tmp, data, token);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Interlocked.Increment(ref _downloadedCount);
                ReportProgress();
                return path;
            }
            finally
            {
                gate.Release();
            }
        }

        private void ReportProgress()
        {
            long total;
            lock (_segLock) total = _segments.Count;
            if (total > 0 && !_isLive) BufferProgress?.Invoke(Math.Min(1.0, (double)_downloadedCount / total));
        }

        // ---------- 后台预取 (按序落盘, 直播流到顶后轮询追新) ----------

        private async Task PrefetchLoop(CancellationToken token)
        {
            int next = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int count;
                    lock (_segLock) count = _segments.Count;
                    bool progressed = false;
                    while (next < count && !token.IsCancellationRequested)
                    {
                        try
                        {
                            await EnsureSegmentAsync(next, token);
                            next++;
                            progressed = true;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // 单片失败继续后面的, 播放器按需取时还能重试
                            next++;
                        }
                    }
                    if (!_isLive) return; // 点播流全部落盘即收工
                    if (!progressed)
                        await RefreshLiveAsync(token);
                    var delay = TimeSpan.FromSeconds(3);
                    await Task.Delay(delay, token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>
        /// v3.25.0 弱网单步降档 (仅临时模式, 只降一次): 首片拉取超 2.5 秒判定弱网, 降一档码率保流畅。
        /// 画质硬约束: 边播边存 (persist) 模式从不进入本函数, 保存恒取最高码率; 降档只影响临时播放链路。
        /// HLS 分片各自独立解码, 降档点前后最多一个分片清晰度有过渡, 不破坏播放连续性。
        /// </summary>
        private async Task AdaptiveProbeAsync(CancellationToken token)
        {
            try
            {
                if (_downgraded) return;
                var variants = _variants;
                if (variants == null || variants.Count < 2) return;

                // v3.25.2: 起播抢跑已实测首片耗时的直接复用 (探测再查首片已是缓存, 自己测只会测到 0);
                // 抢跑超时没记上的 (慢到超出 1.2 秒上限) 仍自己补测一次, 合并到同一次下载
                long seg0Ms;
                if (_seg0Ms >= 0) seg0Ms = _seg0Ms;
                else
                {
                    // v3.25.3: 抢跑超时未落盘的, 续用抢跑秒表 —— 首片耗时从清单一就绪起算,
                    // 整段等待都算进去 (从补测才开始计时会把慢网测成快, 该降档的不降)
                    var sw = _seg0Sw ?? System.Diagnostics.Stopwatch.StartNew();
                    try { await EnsureSegmentAsync(0, token); seg0Ms = sw.ElapsedMilliseconds; }
                    catch { return; } // 首片失败由分诊报错, 不在降档里处理
                }
                if (seg0Ms <= 2500) return; // 网速够, 不降

                if (_downgraded || _mediaUrl == null) return;
                // 按带宽降序找当前档的下一档 (当前档 = 正在用的媒体清单地址)
                var sorted = variants.OrderByDescending(v => v.Bandwidth).ToList();
                var idx = sorted.FindIndex(v => v.Url.AbsoluteUri == _mediaUrl.AbsoluteUri);
                if (idx < 0 || idx + 1 >= sorted.Count) return;
                var lower = sorted[idx + 1];

                var text = await HttpGetStringAsync(lower.Url, token);
                var next = HlsPlaylistParser.ParseMedia(text, lower.Url);
                if (next.Segments.Count == 0) return;

                // 拿稳 0..N 全部分片闸门再换清单, 防在途下载写进新清单的同名分片
                int span;
                lock (_segLock) span = Math.Max(_segments.Count, next.Segments.Count);
                var gates = new List<SemaphoreSlim>();
                for (int i = 0; i < span; i++)
                    gates.Add(_segGates.GetOrAdd(i, _ => new SemaphoreSlim(1, 1)));
                foreach (var g in gates) await g.WaitAsync(token);
                try
                {
                    // 旧分片文件全部作废 (临时目录, 清掉即可), 新清单按新文件下载
                    try
                    {
                        foreach (var f in Directory.GetFiles(_savePath!, "seg_*")) File.Delete(f);
                    }
                    catch { }
                    lock (_segLock)
                    {
                        _segments.Clear();
                        _segments.AddRange(next.Segments);
                        _isLive = next.IsLive;
                    }
                    _mediaUrl = lower.Url; // 直播刷新也跟到新档
                }
                finally
                {
                    foreach (var g in gates) g.Release();
                }
                _downgraded = true;
                Interlocked.Exchange(ref _downloadedCount, 0);
                NetOpt.Emit($"弱网单步降档: 首片耗时 {seg0Ms}ms 超 2500ms, 降一档码率 (临时模式, 不影响保存画质)");
                StatusChanged?.Invoke("弱网, 已自动降一档码率保流畅 (边播边存的画质不受影响)");
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>直播流到顶后重取清单, 只追加新分片 (按地址去重); 800ms 内的重复刷新直接跳过 (播放器反复取清单+预取循环会双路触发)</summary>
        private async Task RefreshLiveAsync(CancellationToken token)
        {
            if (_mediaUrl == null) return;
            var now = NowMs();
            var last = Interlocked.Read(ref _lastRefreshTick);
            if (now - last < 800) return;
            Interlocked.Exchange(ref _lastRefreshTick, now);
            try
            {
                var text = await HttpGetStringAsync(_mediaUrl, token);
                var media = HlsPlaylistParser.ParseMedia(text, _mediaUrl);
                if (media.Segments.Count == 0) return;
                lock (_segLock)
                {
                    var known = new HashSet<string>(_segments.Select(s => s.Url.AbsoluteUri));
                    foreach (var s in media.Segments)
                        if (known.Add(s.Url.AbsoluteUri)) _segments.Add(s);
                    _isLive = media.IsLive; // 直播结束 (出现 ENDLIST) 后转收尾
                }
            }
            catch { }
        }

        // ---------- 本机伺服 ----------

        /// <summary>本地 HTTP 小服务: /playlist 出改写后的清单, /seg/&lt;i&gt; 出分片。TcpListener 手写 HTTP, 三端通用。</summary>
        private sealed class LocalServer : IDisposable
        {
            private readonly HlsStreamService _owner;
            private TcpListener? _listener;
            private readonly int _port;
            private readonly string _token = Guid.NewGuid().ToString("N"); // 会话级访问令牌, 防同网设备直拉
            private volatile bool _running;

            public string LocalUrl => $"http://127.0.0.1:{_port}/playlist?token={_token}";

            /// <summary>局域网地址 (给电视等设备拉清单用); 拿不到局域网 IP 时退回本机地址</summary>
            public string LanUrl
            {
                get
                {
                    var ip = CastService.LocalLanIp();
                    return ip == null ? LocalUrl : $"http://{ip}:{_port}/playlist?token={_token}";
                }
            }

            public LocalServer(HlsStreamService owner)
            {
                _owner = owner;
                _port = new Random().Next(28000, 40000);
            }

            public void Start()
            {
                // 绑全部网卡: 手机自己走 127.0.0.1 照常能用, 电视等同网设备也能连 (投屏需要)
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _running = true;
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (_running && _listener != null)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => Handle(client));
                    }
                    catch (Exception) when (!_running)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // 忽略单次连接异常, 继续服务
                    }
                }
            }

            private async Task Handle(TcpClient client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var head = await ReadRequestHead(stream);
                    if (head == null) return;

                    var parts = head.Split(' ');
                    var method = parts.Length > 0 ? parts[0] : "GET";
                    var target = parts.Length > 1 ? parts[1] : "";
                    if (!StreamToken.RequestAllowed(target, AppSettings.Current.StreamTokenEnabled, _token))
                    {
                        await WriteSimple(stream, "HTTP/1.1 403 Forbidden");
                        return;
                    }

                    var host = SanitizeHost(GetHeader(head, "Host") ?? $"127.0.0.1:{_port}");

                    if (target.StartsWith("/playlist", StringComparison.Ordinal))
                    {
                        await ServePlaylist(stream, host, method == "HEAD");
                    }
                    else if (target.StartsWith("/seg/", StringComparison.Ordinal))
                    {
                        await ServeSegment(stream, target, host, head, method == "HEAD");
                    }
                    else
                    {
                        await WriteSimple(stream, "HTTP/1.1 404 Not Found");
                    }
                }
                catch
                {
                    // 播放器频繁 seek 会主动断开连接, 属正常现象
                }
                finally
                {
                    try { client.Close(); } catch { }
                }
            }

            private async Task ServePlaylist(NetworkStream stream, string host, bool headOnly)
            {
                // 直播流: 出清单前追一次新分片, 播放器反复来取才能跟上直播
                if (_owner._isLive) await _owner.RefreshLiveAsync(CancellationToken.None);

                List<HlsPlaylistParser.Segment> segs;
                bool live;
                double td;
                lock (_owner._segLock)
                {
                    segs = new List<HlsPlaylistParser.Segment>(_owner._segments);
                    live = _owner._isLive;
                    td = 0;
                }

                string? mapUrl = segs.Count > 0 && segs[0].MapUrl != null
                    ? $"http://{host}/seg/map?token={_token}"
                    : null;
                var body = HlsPlaylistParser.BuildLocalPlaylist(segs, live, td,
                    (s, i) => $"http://{host}/seg/{i}?token={_token}", mapUrl);

                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                var respHead = "HTTP/1.1 200 OK\r\n" +
                               "Content-Type: application/vnd.apple.mpegurl\r\n" +
                               $"Content-Length: {bytes.Length}\r\n" +
                               "Cache-Control: no-store\r\n" +
                               "Connection: close\r\n\r\n";
                var headBytes = System.Text.Encoding.ASCII.GetBytes(respHead);
                await stream.WriteAsync(headBytes, 0, headBytes.Length);
                if (!headOnly) await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();
            }

            private async Task ServeSegment(NetworkStream stream, string target, string host, string head, bool headOnly)
            {
                var m = Regex.Match(target, @"^/seg/(map|\d+)");
                if (!m.Success)
                {
                    await WriteSimple(stream, "HTTP/1.1 404 Not Found");
                    return;
                }
                int index = m.Groups[1].Value == "map" ? -1 : int.Parse(m.Groups[1].Value);

                string path;
                try
                {
                    path = await _owner.EnsureSegmentAsync(index, CancellationToken.None);
                }
                catch (Exception)
                {
                    await WriteSimple(stream, "HTTP/1.1 502 Upstream Error");
                    return;
                }

                var info = new FileInfo(path);
                long length = info.Exists ? info.Length : 0;
                if (length <= 0)
                {
                    await WriteSimple(stream, "HTTP/1.1 404 Not Found");
                    return;
                }

                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = ext == ".m4s" ? "video/iso.segment" : "video/mp2t";

                var rangeHeader = GetHeader(head, "Range");
                long start = 0, end = length - 1;
                int status = 200;
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    var rm = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
                    if (rm.Success)
                    {
                        start = long.Parse(rm.Groups[1].Value);
                        if (!string.IsNullOrEmpty(rm.Groups[2].Value))
                            end = long.Parse(rm.Groups[2].Value);
                    }
                    else
                    {
                        // v3.24.2: 后缀区间 bytes=-N (取末尾 N 字节), 一些播放器 seek 会发这种
                        var sm = Regex.Match(rangeHeader, @"bytes=-(\d+)");
                        if (sm.Success)
                        {
                            var suffix = Math.Min(long.Parse(sm.Groups[1].Value), length);
                            start = length - suffix;
                            end = length - 1;
                        }
                    }
                    status = 206;
                }
                end = Math.Min(end, length - 1);
                if (start > end)
                {
                    await WriteSimple(stream, "HTTP/1.1 416 Range Not Satisfiable");
                    return;
                }
                var contentLength = end - start + 1;

                var respHead = $"{(status == 206 ? "HTTP/1.1 206 Partial Content" : "HTTP/1.1 200 OK")}\r\n" +
                               $"Content-Type: {mime}\r\n" +
                               $"Content-Length: {contentLength}\r\n" +
                               "Accept-Ranges: bytes\r\n" +
                               (status == 206 ? $"Content-Range: bytes {start}-{end}/{length}\r\n" : "") +
                               "Connection: close\r\n\r\n";
                var headBytes = System.Text.Encoding.ASCII.GetBytes(respHead);
                await stream.WriteAsync(headBytes, 0, headBytes.Length);
                if (headOnly) return;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[256 * 1024];
                long sent = 0;
                while (sent < contentLength)
                {
                    var toRead = (int)Math.Min(buffer.Length, contentLength - sent);
                    var read = await fs.ReadAsync(buffer, 0, toRead);
                    if (read <= 0) break;
                    await stream.WriteAsync(buffer, 0, read);
                    sent += read;
                }
                await stream.FlushAsync();
            }

            private static async Task WriteSimple(NetworkStream stream, string status)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes($"{status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            private string SanitizeHost(string host)
            {
                return Regex.IsMatch(host, @"^[0-9A-Za-z.\[\]:-]+$") ? host : $"127.0.0.1:{_port}";
            }

            /// <summary>读取请求头 (到空行为止), 返回请求行 + 头部整体文本</summary>
            private static async Task<string?> ReadRequestHead(NetworkStream stream)
            {
                var sb = new System.Text.StringBuilder();
                var buffer = new byte[8 * 1024];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length - total);
                    if (read <= 0) break;
                    sb.Append(System.Text.Encoding.ASCII.GetString(buffer, total, read));
                    total += read;
                    var text = sb.ToString();
                    if (text.Contains("\r\n\r\n"))
                        return text.Substring(0, text.IndexOf("\r\n\r\n") + 2); // 含最后换行
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }

            private static string? GetHeader(string head, string name)
            {
                foreach (var line in head.Split('\n'))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0 && line.Substring(0, idx).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(idx + 1).Trim();
                }
                return null;
            }

            public void Dispose()
            {
                _running = false;
                try { _listener?.Stop(); } catch { }
            }
        }
    }
}
