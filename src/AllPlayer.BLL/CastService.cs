using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AllPlayer.BLL
{
    /// <summary>局域网内一台可投屏的设备 (DLNA/UPnP 渲染器)</summary>
    public class CastDevice
    {
        public string Name = "";
        public string ControlUrl = ""; // AVTransport 控制地址
        public string Location = "";

        public override string ToString() => Name;
    }

    /// <summary>
    /// 业务层 - 投屏: SSDP 发现局域网 DLNA 电视, SOAP 把播放地址交给电视, 由电视原生解码。
    /// 地址是原样直传的 (4K 也一样), 手机不做任何转码, 所以画质无损 = 高保真。
    /// 本地文件配合 LanFileServer 提供局域网 HTTP 地址。
    /// </summary>
    public static class CastService
    {
        /// <summary>搜索局域网内支持 AVTransport 的设备 (约 2.5 秒)</summary>
        public static async Task<List<CastDevice>> DiscoverAsync(int ms = 2500)
        {
            var found = new Dictionary<string, string>(); // location -> usn
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 500);
                var req = string.Join("\r\n",
                    "M-SEARCH * HTTP/1.1",
                    "HOST: 239.255.255.250:1900",
                    "MAN: \"ssdp:discover\"",
                    "MX: 2",
                    "ST: urn:schemas-upnp-org:service:AVTransport:1",
                    "", "");
                var bytes = Encoding.ASCII.GetBytes(req);
                await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
                var deadline = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        var res = udp.Receive(ref ep);
                        var text = Encoding.ASCII.GetString(res);
                        var loc = ParseHeader(text, "LOCATION") ?? ParseHeader(text, "location");
                        if (loc != null && !found.ContainsKey(loc)) found[loc] = ParseHeader(text, "USN") ?? loc;
                    }
                    catch (SocketException) { /* 超时继续等 */ }
                    catch (ObjectDisposedException) { break; }
                }
            }
            catch { }
            var devices = new List<CastDevice>();
            foreach (var loc in found.Keys)
            {
                try
                {
                    var d = await DescribeAsync(loc);
                    if (d != null && devices.All(x => x.ControlUrl != d.ControlUrl)) devices.Add(d);
                }
                catch { }
            }
            return devices;
        }

        private static string? ParseHeader(string text, string name)
        {
            foreach (var line in text.Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                if (line.Substring(0, idx).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(idx + 1).Trim();
            }
            return null;
        }

        /// <summary>拉设备描述, 找出 friendlyName 与 AVTransport 控制地址</summary>
        private static async Task<CastDevice?> DescribeAsync(string location)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var xml = await http.GetStringAsync(location);
            var doc = XDocument.Parse(xml);
                        var root = doc.Root;
            if (root == null) return null;

            // 基地址: URLBase 元素, 否则用 LOCATION 的源
            var baseUri = new Uri(location);
            var urlBaseEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase");
            if (urlBaseEl != null && Uri.TryCreate(urlBaseEl.Value.Trim(), UriKind.Absolute, out var ub)) baseUri = ub;

            var name = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value ?? "未知设备";
                        string? control = null;
            foreach (var svc in root.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                var st = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                if (!st.Contains("AVTransport", StringComparison.OrdinalIgnoreCase)) continue;
                var cu = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (string.IsNullOrEmpty(cu)) continue;
                control = new Uri(baseUri, cu).AbsoluteUri;
                break;
            }
            if (control == null) return null;
            return new CastDevice { Name = name, ControlUrl = control, Location = location };
        }

        /// <summary>把地址递给电视: SetAVTransportURI + Play (原样直传, 电视自己解码, 画质无损)</summary>
        public static async Task<bool> CastAsync(CastDevice device, string url, string title = "")
        {
            var meta = string.IsNullOrEmpty(title) ? "" :
                $"<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"><item id=\"1\"><dc:title>{Esc(title)}</dc:title><upnp:class>object.item.videoItem</upnp:class></item></DIDL-Lite>";
            var ok = await SoapAsync(device.ControlUrl, "SetAVTransportURI",
                $"<InstanceID>0</InstanceID><CurrentURI>{Esc(url)}</CurrentURI><CurrentURIMetaData>{Esc(meta)}</CurrentURIMetaData>");
            if (!ok) return false;
            return await SoapAsync(device.ControlUrl, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
        }

        /// <summary>通知电视停止</summary>
        public static Task<bool> StopAsync(CastDevice device) =>
            SoapAsync(device.ControlUrl, "Stop", "<InstanceID>0</InstanceID>");

        /// <summary>查电视播放状态 (v3.23 P2-2: GetTransportInfo)。
        /// 返回 PLAYING / PAUSED_PLAYBACK / STOPPED / TRANSITIONING / NO_MEDIA_PRESENT 等; 查询失败返回 null。</summary>
        public static async Task<string?> TransportStateAsync(CastDevice device)
        {
            var (ok, body) = await SoapRequestAsync(device.ControlUrl, "GetTransportInfo", "<InstanceID>0</InstanceID>");
            if (!ok) return null;
            var m = Regex.Match(body, "CurrentTransportState[^>]*>\\s*([A-Za-z_]+)\\s*<");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string Esc(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

        private static async Task<(bool Ok, string Body)> SoapRequestAsync(string controlUrl, string action, string argsXml)
        {
            try
            {
                var body = string.Join("\r\n",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">",
                    "<s:Body>",
                    $"<u:{action} xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">{argsXml}</u:{action}>",
                    "</s:Body>",
                    "</s:Envelope>");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var msg = new HttpRequestMessage(HttpMethod.Post, controlUrl);
                msg.Content = new StringContent(body, Encoding.UTF8, "text/xml");
                msg.Headers.TryAddWithoutValidation("SOAPACTION", $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"");
                using var res = await http.SendAsync(msg);
                var text = await res.Content.ReadAsStringAsync();
                return (res.IsSuccessStatusCode, text);
            }
            catch { return (false, ""); }
        }

        private static async Task<bool> SoapAsync(string controlUrl, string action, string argsXml)
        {
            try
            {
                var body = string.Join("\r\n",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">",
                    "<s:Body>",
                    $"<u:{action} xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">{argsXml}</u:{action}>",
                    "</s:Body>",
                    "</s:Envelope>");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var msg = new HttpRequestMessage(HttpMethod.Post, controlUrl);
                msg.Content = new StringContent(body, Encoding.UTF8, "text/xml");
                msg.Headers.TryAddWithoutValidation("SOAPACTION", $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"");
                using var res = await http.SendAsync(msg);
                return res.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>本机在局域网里的 IP (拿不到返回 null)</summary>
        public static IPAddress? LocalLanIp()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("8.8.8.8", 53);
                return (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 业务层 - 临时局域网文件服务: 投屏本地文件时, 起一个支持 Range 断点的大文件 HTTP 服务给电视拉流。
    /// 用完即停, 不常驻。
    /// </summary>
    public class LanFileServer : IDisposable
    {
        private readonly string _path;
        private readonly long _length;
        private readonly string _mime;
        private TcpListener? _listener;
        private volatile bool _running;

        public int Port { get; }
        /// <summary>给电视用的地址 (含本机局域网 IP)</summary>
        public string LanUrl { get; }

        public LanFileServer(string filePath)
        {
            _path = filePath;
            var fi = new FileInfo(filePath);
            _length = fi.Length;
            _mime = GuessMime(filePath);
            Port = new Random().Next(28000, 40000);
            var ip = CastService.LocalLanIp() ?? IPAddress.Loopback;
            LanUrl = $"http://{ip}:{Port}/video{Path.GetExtension(filePath).ToLowerInvariant()}";
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            _ = Task.Run(AcceptLoop);
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private async Task AcceptLoop()
        {
            while (_running && _listener != null)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception) when (!_running) { break; }
                catch { continue; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var buf = new byte[8192];
                    var headLen = 0;
                    while ((headLen = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        var req = Encoding.ASCII.GetString(buf, 0, headLen);
                        if (!req.Contains("GET")) return;
                        long from = 0, to = _length - 1;
                        // Range: bytes=100-200 / 100-
                        var lines = req.Split('\n');
                        foreach (var line in lines)
                        {
                            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                            var spec = line.Substring(6).Trim(); // bytes=...
                            var eq = spec.IndexOf('=');
                            if (eq < 0) break;
                            var range = spec.Substring(eq + 1).Trim();
                            var dash = range.IndexOf('-');
                            if (dash > 0) long.TryParse(range.Substring(0, dash).Trim(), out from);
                            var endPart = dash >= 0 ? range.Substring(dash + 1).Trim() : "";
                            if (long.TryParse(endPart, out var e2) && e2 < _length) to = e2;
                            if (dash == 0) // bytes=-500 (最后500字节)
                            {
                                if (long.TryParse(endPart, out var tail))
                                {
                                    from = Math.Max(0, _length - tail); to = _length - 1;
                                }
                            }
                            break;
                        }
                        if (from > to || from >= _length) { from = 0; to = _length - 1; }

                        var sb = new StringBuilder();
                        sb.Append("HTTP/1.1 206 Partial Content\r\n");
                        sb.Append($"Content-Type: {_mime}\r\n");
                        sb.Append("Accept-Ranges: bytes\r\n");
                        sb.Append($"Content-Range: bytes {from}-{to}/{_length}\r\n");
                        sb.Append($"Content-Length: {to - from + 1}\r\n");
                        sb.Append("Connection: close\r\n\r\n");
                        var head = Encoding.ASCII.GetBytes(sb.ToString());
                        await stream.WriteAsync(head, 0, head.Length);

                        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                        fs.Seek(from, SeekOrigin.Begin);
                        var remain = to - from + 1;
                        var chunk = new byte[1 << 16];
                        while (remain > 0 && _running)
                        {
                            var n = await fs.ReadAsync(chunk, 0, (int)Math.Min(chunk.Length, remain));
                            if (n <= 0) break;
                            await stream.WriteAsync(chunk, 0, n);
                            remain -= n;
                        }
                        await stream.FlushAsync();
                    }
                }
                catch { }
            }
        }

        private static string GuessMime(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".ts" => "video/mp2t",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".flv" => "video/x-flv",
                ".wmv" => "video/x-ms-wmv",
                ".m4v" => "video/x-m4v",
                ".3gp" => "video/3gpp",
                ".mp3" => "audio/mpeg",
                ".m3u8" => "application/vnd.apple.mpegurl",
                _ => "application/octet-stream",
            };
        }
    }
}
