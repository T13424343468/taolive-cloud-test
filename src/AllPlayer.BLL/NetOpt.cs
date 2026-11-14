using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AllPlayer.Model;

namespace AllPlayer.BLL
{
    /// <summary>快速通道异常: 携带 HTTP 状态码 (0 = 与状态码无关的失败)。调用方一律回落标准通道。</summary>
    public class FastHttpException : Exception
    {
        public int StatusCode;

        public FastHttpException(int status, string message) : base(message) { StatusCode = status; }
    }

    /// <summary>
    /// 网络优化 (v3.25.0): 多 DNS 择优 + 连接竞速 + 快速通道 (手写 HTTP/1.1 GET) + 用户自配代理。
    /// 设计底线: 快速通道任何异常一律回落标准通道, 功能不倒退; 代理开启时快速通道整体让路 (走代理由标准通道完成)。
    /// 画质硬约束 (2026-11-09 用户令): 自适应降档只许作用于播放链路 (临时模式弱网单步降级),
    /// 边播边存/下载保存的文件恒取最高码率变体, 本类不提供任何"降档取片"接口, 由 HlsStreamService 在 persist 模式下恒走 PickBestVariant 保证。
    /// </summary>
    public static class NetOpt
    {
        // ---------- 诊断出口: UI 层启动时把 AppLog 挂上来, 技术信息只进日志不上屏 ----------

        /// <summary>网络优化诊断事件 (建连耗时等); UI 层挂日志器, 没挂就静默</summary>
        public static event Action<string>? Trace;

        internal static void Emit(string msg) => Trace?.Invoke(msg);

        // ---------- 自配线路 ----------

        /// <summary>按设置构建 WebProxy; 未启用/配置不完整返回 null (直连)</summary>
        public static WebProxy? BuildWebProxy()
        {
            var s = AppSettings.Current;
            if (!s.ProxyEnabled || string.IsNullOrWhiteSpace(s.ProxyHost) || s.ProxyPort <= 0) return null;
            return new WebProxy(s.ProxyHost, s.ProxyPort);
        }

        /// <summary>当前是否走自配代理 (开了代理就没有快速通道, 全部走标准通道经代理)</summary>
        public static bool ProxyActive
        {
            get
            {
                var s = AppSettings.Current;
                return s.ProxyEnabled && !string.IsNullOrWhiteSpace(s.ProxyHost) && s.ProxyPort > 0;
            }
        }

        // ---------- DNS 择优: 系统 DNS + 两个 DoH 并发, 合并去重, 只取 A 记录, 缓存 10 分钟 ----------

        private static readonly HttpClient DohHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, List<IPAddress>>> _dnsCache
            = new ConcurrentDictionary<string, Tuple<DateTime, List<IPAddress>>>();
        private static readonly ConcurrentDictionary<string, IPAddress> _preferred = new ConcurrentDictionary<string, IPAddress>();
        private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>解析主机名: IP 字面量直接返回; 其余系统 DNS 与 DoH (阿里 223.5.5.5 / CF 1.1.1.1) 并发择优合并去重, 缓存 10 分钟。只取 IPv4 (A 记录)。</summary>
        public static async Task<List<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        {
            if (IPAddress.TryParse(host, out var literal))
                return new List<IPAddress> { literal };

            host = host.Trim().ToLowerInvariant();
            var now = DateTime.UtcNow;
            if (_dnsCache.TryGetValue(host, out var hit) && now < hit.Item1)
                return new List<IPAddress>(hit.Item2);

            var jobs = new List<Task<List<IPAddress>>>
            {
                SystemDnsAsync(host, ct),
                DohAsync("223.5.5.5", host, ct),
                DohAsync("1.1.1.1", host, ct),
            };
            var merged = new Dictionary<string, IPAddress>();
            try
            {
                var done = await Task.WhenAll(jobs);
                foreach (var list in done)
                    foreach (var ip in list)
                        merged[ip.ToString()] = ip;
            }
            catch { }

            if (merged.Count == 0) throw new InvalidOperationException($"域名解析失败: {host}");

            // 竞速胜出过的 IP 排前面, 其余按序
            var result = new List<IPAddress>();
            if (_preferred.TryGetValue(host, out var pref) && merged.ContainsKey(pref.ToString()))
            {
                result.Add(pref);
                merged.Remove(pref.ToString());
            }
            result.AddRange(merged.Values);

            _dnsCache[host] = Tuple.Create(now + DnsCacheTtl, new List<IPAddress>(result));
            return result;
        }

        private static async Task<List<IPAddress>> SystemDnsAsync(string host, CancellationToken ct)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host);
                return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch { return new List<IPAddress>(); }
        }

        /// <summary>DoH 查询 (RFC8484 JSON 接口): 只取 Answer 里 type=1 的 IPv4 值。DoH 服务器本身是 IP 字面量, 不依赖 DNS。</summary>
        private static async Task<List<IPAddress>> DohAsync(string server, string host, CancellationToken ct)
        {
            try
            {
                var json = await DohHttp.GetStringAsync($"https://{server}/resolve?name={Uri.EscapeDataString(host)}&type=A");
                var found = new List<IPAddress>();
                foreach (Match m in Regex.Matches(json, "\"data\"\\s*:\\s*\"((?:[0-9]{1,3}\\.){3}[0-9]{1,3})\""))
                {
                    if (IPAddress.TryParse(m.Groups[1].Value, out var ip)
                        && ip.AddressFamily == AddressFamily.InterNetwork)
                        found.Add(ip);
                }
                return found;
            }
            catch (OperationCanceledException) { throw; }
            catch { return new List<IPAddress>(); }
        }

        // ---------- 连接竞速: 所有候选 IP 并发建连, 2.5 秒超时, 最快者胜出进画像 ----------

        private const int RaceTimeoutMs = 2500;

        /// <summary>并发对多个候选 IP 建连, 最快成功者胜出 (记入画像, 下次解析排前); 全败抛异常由调用方回落。</summary>
        public static async Task<TcpClient> RaceConnectAsync(IReadOnlyList<IPAddress> ips, int port, CancellationToken ct)
        {
            if (ips == null || ips.Count == 0) throw new InvalidOperationException("没有候选地址可连");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(RaceTimeoutMs);
                var pending = new List<Task<TcpClient?>>();
                foreach (var ip in ips) pending.Add(ConnectOneAsync(ip, port, timeout.Token));

                TcpClient? winner = null;
                while (pending.Count > 0 && winner == null)
                {
                    var done = await Task.WhenAny(pending);
                    pending.Remove(done);
                    winner = await done; // ConnectOneAsync 自带 2.5s 上限, 不会悬死
                }
                if (winner != null)
                {
                    timeout.Cancel(); // 提前收掉落败连接的等待
                    return winner;
                }
                throw new InvalidOperationException("竞速建连全部失败");
            }
        }

        private static async Task<TcpClient?> ConnectOneAsync(IPAddress ip, int port, CancellationToken ct)
        {
            var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(ip, port);
                var finished = await Task.WhenAny(connectTask, Task.Delay(RaceTimeoutMs, ct));
                if (finished != connectTask) { try { client.Dispose(); } catch { } return null; }
                await connectTask; // 观察连接异常
                return client;
            }
            catch (OperationCanceledException)
            {
                try { client.Dispose(); } catch { }
                throw;
            }
            catch
            {
                try { client.Dispose(); } catch { }
                return null;
            }
        }

        // ---------- 快速通道: 手写 HTTP/1.1 GET, 失败即回落标准通道 ----------

        private const long MaxResponseBytes = 64 * 1024 * 1024; // 64MB 上限
        private const int FailStreakLimit = 3;

        private static int _failStreak; // 连续快速失败计数, 超限本轮进程内关闭快速通道 (成功一次清零)

        /// <summary>快速通道 GET, 返回响应体; 任何异常返回 null (调用方回落标准通道)。取消令牌触发时原样抛出。</summary>
        public static async Task<string?> TryGetStringAsync(Uri url, CancellationToken ct)
        {
            try
            {
                return Encoding.UTF8.GetString(await FastGetAsync(url, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>快速通道 GET, 返回响应体字节; 任何异常返回 null (调用方回落标准通道)。取消令牌触发时原样抛出。</summary>
        public static async Task<byte[]?> TryGetBytesAsync(Uri url, CancellationToken ct)
        {
            try
            {
                return await FastGetAsync(url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task<byte[]> FastGetAsync(Uri url, CancellationToken ct)
        {
            if (ProxyActive) throw new FastHttpException(0, "代理开启, 快速通道让路");
            if (_failStreak >= FailStreakLimit)
                throw new FastHttpException(0, "快速通道连续失败, 本轮进程让路");
            if (url.Scheme != "http" && url.Scheme != "https")
                throw new FastHttpException(0, "不支持非 http(s) 地址");

            var host = url.Host;
            var secure = url.Scheme == "https";
            var port = url.IsDefaultPort ? (secure ? 443 : 80) : url.Port;

            // 整体 30 秒上限 (与标准通道一致): 源站接了连接却慢速滴流时, 快速通道不能比标准通道挂更久。
            // 超时抛 OCE, 调用方分诊里正好归为"连接超时"。
            using (var total = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                total.CancelAfter(TimeSpan.FromSeconds(30));
                return await FastGetCoreAsync(url, host, secure, port, total.Token);
            }
        }

        private static async Task<byte[]> FastGetCoreAsync(Uri url, string host, bool secure, int port, CancellationToken ct)
        {
            // 观察点 (v3.25.0): 分片耗时拆成 解析/建连/取体 三段, 真机上据此判断握手占比, 决定要不要上热连接池
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ips = await ResolveAsync(host, ct);
            var resolveMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var client = await RaceConnectAsync(ips, port, ct);
            var connectMs = sw.ElapsedMilliseconds;
            sw.Restart();

            try
            {
                Stream stream = client.GetStream();
                if (secure)
                {
                    // IP 连接 + 真实主机名校验证书 (TargetHost = host), 不降低校验标准
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsClientAsync(host);
                    stream = ssl;
                }
                var head =
                    $"GET {url.PathAndQuery} HTTP/1.1\r\n" +
                    $"Host: {host}\r\n" +
                    "User-Agent: Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36\r\n" +
                    "Accept: */*\r\n" +
                    "Connection: close\r\n\r\n";
                var headBytes = Encoding.ASCII.GetBytes(head);
                await stream.WriteAsync(headBytes, 0, headBytes.Length, ct);

                var (statusCode, headers, leftover) = await ReadResponseHeadAsync(stream, ct);
                if (statusCode != 200) throw new FastHttpException(statusCode, $"HTTP {statusCode}");
                var contentEnc = GetHeader(headers, "Content-Encoding");
                if (contentEnc != null && contentEnc != "identity")
                    throw new FastHttpException(0, "响应带压缩编码, 回落标准通道");
                var redirect = statusCode >= 300 && statusCode < 400;
                if (redirect) throw new FastHttpException(statusCode, "重定向, 回落标准通道");

                // 头和体可能同包到达: 剩余体字节用前缀流接住, 不丢不重
                Stream bodyStream = leftover.Length > 0 ? new PrefixedStream(leftover, stream) : stream;
                byte[] body;
                if (GetHeader(headers, "Transfer-Encoding")?.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    body = await ReadChunkedBodyAsync(bodyStream, ct);
                }
                else
                {
                    var lenText = GetHeader(headers, "Content-Length");
                    body = long.TryParse(lenText, out var len) && len >= 0
                        ? await ReadExactAsync(bodyStream, len, ct)
                        : await ReadToLengthAsync(bodyStream, ct); // 无长度也无 chunked: 读到连接关闭
                }
                if (body.Length > MaxResponseBytes) throw new FastHttpException(0, "响应超过 64MB 上限");
                _failStreak = 0;
                Emit($"快速通道 {host}{url.PathAndQuery}: 解析 {resolveMs}ms 建连 {connectMs}ms 取体 {sw.ElapsedMilliseconds}ms 共 {body.Length}B");
                return body;
            }
            catch (FastHttpException)
            {
                Interlocked.Increment(ref _failStreak);
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failStreak);
                throw new FastHttpException(0, ex.Message);
            }
            finally
            {
                try { client.Dispose(); } catch { }
            }
        }

        // ---- HTTP 解析助手 (公开便于测试) ----

        /// <summary>读响应头 (至空行), 返回状态码、头部文本与同包已读到的剩余体字节 (Latin-1 字节安全往返)</summary>
        public static async Task<Tuple<int, string, byte[]>> ReadResponseHeadAsync(Stream stream, CancellationToken ct)
        {
            var latin1 = Encoding.GetEncoding("ISO-8859-1");
            var sb = new StringBuilder();
            var buffer = new byte[8 * 1024];
            while (sb.Length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) throw new FastHttpException(0, "响应头不完整, 连接提前关闭");
                sb.Append(latin1.GetString(buffer, 0, read));
                var text = sb.ToString();
                var cut = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (cut >= 0)
                {
                    var head = text.Substring(0, cut + 2); // 含最后换行
                    var leftover = latin1.GetBytes(text.Substring(cut + 4)); // 空行之后的体首段
                    var statusMatch = Regex.Match(head, @"^HTTP/1\.[01]\s+(\d{3})");
                    if (!statusMatch.Success) throw new FastHttpException(0, "状态行无法解析");
                    return Tuple.Create(int.Parse(statusMatch.Groups[1].Value), head, leftover);
                }
                // 头太长没见空行: 继续读, 直到缓冲用尽按异常处理
            }
            throw new FastHttpException(0, "响应头过大");
        }

        /// <summary>前缀流: 先吐预读进来的体首段, 再接原流 (头体同包不丢字节)</summary>
        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private int _pos;
            private readonly Stream _inner;

            public PrefixedStream(byte[] prefix, Stream inner) { _prefix = prefix; _inner = inner; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos < _prefix.Length)
                {
                    var n = Math.Min(count, _prefix.Length - _pos);
                    Array.Copy(_prefix, _pos, buffer, offset, n);
                    _pos += n;
                    return n;
                }
                return _inner.Read(buffer, offset, count);
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (_pos < _prefix.Length)
                {
                    var n = Math.Min(count, _prefix.Length - _pos);
                    Array.Copy(_prefix, _pos, buffer, offset, n);
                    _pos += n;
                    return n;
                }
                return await _inner.ReadAsync(buffer, offset, count, ct);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>读满 Content-Length 字节; 响应体超过 64MB 上限按异常处理</summary>
        public static async Task<byte[]> ReadExactAsync(Stream stream, long length, CancellationToken ct)
        {
            if (length > MaxResponseBytes) throw new FastHttpException(0, "响应超过 64MB 上限");
            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(body, offset, (int)(length - offset), ct);
                if (read <= 0) throw new FastHttpException(0, "响应体不完整, 连接提前关闭");
                offset += read;
            }
            return body;
        }

        /// <summary>Transfer-Encoding: chunked 解析: 逐块 (16 进制长度 + 数据), 终止于 0 块。超过 64MB 上限按异常处理。</summary>
        public static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(stream, ct);
                var sizeMatch = Regex.Match(sizeLine, "^([0-9A-Fa-f]+)");
                if (!sizeMatch.Success) throw new FastHttpException(0, "分块长度行无法解析");
                var size = Convert.ToInt64(sizeMatch.Groups[1].Value, 16);
                if (size == 0)
                {
                    // 0 块后是结尾头部区, 读到空行 (连接即关, 简化处理直接读完)
                    while (true)
                    {
                        var line = await ReadLineAsync(stream, ct);
                        if (line.Length == 0) break;
                    }
                    return body.ToArray();
                }
                if (body.Length + size > MaxResponseBytes) throw new FastHttpException(0, "响应超过 64MB 上限");
                var chunk = await ReadExactAsync(stream, size, ct);
                body.Write(chunk, 0, chunk.Length);
                await ReadLineAsync(stream, ct); // 吃掉块尾 CRLF
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, 1, ct);
                if (read <= 0) throw new FastHttpException(0, "行读取到连接结束");
                if (buffer[0] == '\n') break;
                if (buffer[0] != '\r') sb.Append((char)buffer[0]);
            }
            return sb.ToString();
        }

        /// <summary>无 Content-Length 无 chunked: 读到连接关闭 (Connection: close 场景)</summary>
        private static async Task<byte[]> ReadToLengthAsync(Stream stream, CancellationToken ct)
        {
            using var body = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) return body.ToArray();
                if (body.Length + read > MaxResponseBytes) throw new FastHttpException(0, "响应超过 64MB 上限");
                body.Write(buffer, 0, read);
            }
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
    }
}
