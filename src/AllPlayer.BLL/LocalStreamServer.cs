using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AllPlayer.Model;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 业务层 - 本地 HTTP 小服务: 把"边下边载"中的文件以 HTTP Range 方式喂给播放器。
    /// 播放器请求 http://127.0.0.1:端口/stream 即可像播普通视频一样播。
    /// 基于 TcpListener 手写 HTTP (三端通用, 不依赖 HttpListener)。
    /// </summary>
    public class LocalStreamServer : IDisposable
    {
        private readonly Func<long, Stream> _openStream; // 从指定偏移打开读取流
        private readonly long _length;
        private readonly string _mime;
        private TcpListener? _listener;
        private readonly int _port;
        private readonly string _token = Guid.NewGuid().ToString("N"); // v3.23 P0-2: 会话级访问令牌, 防同网设备直拉
        private volatile bool _running;

        public string Url => $"http://127.0.0.1:{_port}/stream?token={_token}";

        /// <summary>局域网地址 (给电视等设备拉流用); 拿不到局域网 IP 时退回本机地址</summary>
        public string LanUrl
        {
            get
            {
                var ip = CastService.LocalLanIp();
                return ip == null ? Url : $"http://{ip}:{_port}/stream?token={_token}";
            }
        }

        public LocalStreamServer(Func<long, Stream> openStream, long length, string fileName)
        {
            _openStream = openStream;
            _length = length;
            _mime = GuessMime(fileName);
            var rnd = new Random();
            _port = rnd.Next(28000, 40000);
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
                var requestLine = await ReadRequestHead(stream);
                if (requestLine == null) return;

                // v3.23 P0-2: 令牌校验先行——不带令牌的请求一律 403 (设置关闭时退回旧行为)
                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "GET";
                var target = parts.Length > 1 ? parts[1] : "";
                if (!StreamToken.RequestAllowed(target, AppSettings.Current.StreamTokenEnabled, _token))
                {
                    var deny = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(deny, 0, deny.Length);
                    return;
                }
                var rangeHeader = GetHeader(requestLine, "Range");

                long start = 0, end = _length - 1;
                int status = 200;
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    // 形如 bytes=12345- 或 bytes=100-999
                    var m = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
                    if (m.Success)
                    {
                        start = long.Parse(m.Groups[1].Value);
                        if (!string.IsNullOrEmpty(m.Groups[2].Value))
                            end = long.Parse(m.Groups[2].Value);
                    }
                    status = 206;
                }

                end = Math.Min(end, _length - 1);
                var contentLength = end - start + 1;

                var head = $"{(status == 206 ? "HTTP/1.1 206 Partial Content" : "HTTP/1.1 200 OK")}\r\n" +
                           $"Content-Type: {_mime}\r\n" +
                           $"Content-Length: {contentLength}\r\n" +
                           $"Accept-Ranges: bytes\r\n" +
                           (status == 206 ? $"Content-Range: bytes {start}-{end}/{_length}\r\n" : "") +
                           "Connection: close\r\n\r\n";
                var headBytes = System.Text.Encoding.ASCII.GetBytes(head);
                await stream.WriteAsync(headBytes, 0, headBytes.Length);

                if (method == "HEAD")
                    return;

                using var src = _openStream(start);
                // 契约: _openStream(offset) 返回已定位到 offset 的流, 直接从这里往下读
                var buffer = new byte[256 * 1024];
                long sent = 0;
                while (sent < contentLength)
                {
                    var toRead = (int)Math.Min(buffer.Length, contentLength - sent);
                    var read = await src.ReadAsync(buffer, 0, toRead);
                    if (read <= 0) break;
                    await stream.WriteAsync(buffer, 0, read);
                    sent += read;
                }
                await stream.FlushAsync();
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

        private static string GuessMime(string name)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext switch
            {
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".webm" => "video/webm",
                ".flv" => "video/x-flv",
                ".wmv" => "video/x-ms-wmv",
                ".ts" => "video/mp2t",
                _ => "application/octet-stream",
            };
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }
    }
}
