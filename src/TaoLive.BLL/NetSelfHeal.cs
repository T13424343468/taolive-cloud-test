using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.BLL;

/// <summary>
/// 三分判别（vc39 ⑤网络自愈·判读书 3.2）：对单个直播域名并发探测三通道——
/// 系统解析 / DoH / IP 直连，产出定位结论：系统解析坏（可自愈）、路由死（不可自愈，如实告示）、出网全断（不可自愈）。
/// 纯逻辑可测：三探测委托与时钟注入；无 IO 默认实现由 SelfHealKernel 提供。
/// </summary>
public class ResolutionTriage
{
    /// <summary>判别结论（有效域=自愈可治；无效域=如实告示不自愈）。</summary>
    public enum VerdictKind { SystemDnsBroken, RouteDead, NetworkDown, Healthy }

    public readonly record struct Result(VerdictKind Kind, string Detail);

    private readonly Func<string, Task<bool>> _systemResolve;   // host → 系统解析是否成功
    private readonly Func<string, Task<bool>> _dohResolve;      // host → DoH 解析是否成功
    private readonly Func<string, IPAddress, int, Task<bool>> _ipConnect; // (host, ip, port) → IP 直连 TCP 是否可达
    private readonly Func<long> _nowMs;
    private readonly Func<IPAddress[], IPAddress> _pickIp;

    public ResolutionTriage(
        Func<string, Task<bool>>? systemResolve = null,
        Func<string, Task<bool>>? dohResolve = null,
        Func<string, IPAddress, int, Task<bool>>? ipConnect = null,
        Func<long>? nowMs = null,
        Func<IPAddress[], IPAddress>? pickIp = null)
    {
        _systemResolve = systemResolve ?? (async host => { try { return await Dns.GetHostAddressesAsync(host).ContinueWith(t => t.Result.Length > 0); } catch { return false; } });
        _dohResolve = dohResolve ?? (async host => { try { return await new StreamDnsPicker(nowMs).ResolveAsync(host).ContinueWith(t => t.Result.Length > 0); } catch { return false; } });
        _ipConnect = ipConnect ?? (async (host, ip, port) => await TcpProbeAsync(ip, port));
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _pickIp = pickIp ?? (ips => ips[0]);
    }

    /// <summary>
    /// 对 host 做三分判别：解析面（系统/DoH）与连通面（IP 直连）各自独立探测，按 3.2 表落结论。
    /// port=目标服务端口（按当前流 URL 实际端口探，混合端口源 443 探不通不等于路由死）。
    /// </summary>
    public async Task<Result> TriageAsync(string host, IPAddress[]? knownIps = null, int port = 443)
    {
        try
        {
            var ips = knownIps;
            if (ips is null || ips.Length == 0)
            {
                try { ips = await new StreamDnsPicker(_nowMs).ResolveAsync(host); } catch { ips = Array.Empty<IPAddress>(); }
            }
            var dohOk = await _dohResolve(host);
            var sysOk = await _systemResolve(host);
            bool connOk = ips is { Length: > 0 } && await _ipConnect(host, _pickIp(ips), port);

            if (dohOk && !sysOk) return connOk ? new Result(VerdictKind.SystemDnsBroken, $"DoH 可解析且 IP 直连通：系统解析坏（可自愈）{host}") : new Result(VerdictKind.RouteDead, $"DoH 可解析但 IP 直连不通：路由/网络栈死（不可自愈）{host}");
            if (!dohOk) return new Result(VerdictKind.NetworkDown, $"DoH 不通：出网全断（不可自愈）{host}");
            return new Result(sysOk && connOk ? VerdictKind.Healthy : VerdictKind.SystemDnsBroken, $"DoH+系统解析正常：{host}（{(connOk ? "连通正常" : "直连探不通，按解析面复判")}）");
        }
        catch (Exception ex) { return new Result(VerdictKind.NetworkDown, $"判别异常按出网全断处理：{ex.Message}"); }
    }

    /// <summary>IP 直连探针：TCP 连上即视为连通（默认 3 秒超时；按实际端口探连通面，证书面不在此探）。</summary>
    private static async Task<bool> TcpProbeAsync(IPAddress ip, int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(ip, port);
            var done = await Task.WhenAny(connect, Task.Delay(3000));
            if (done != connect) return false;
            await connect; // 观察异常
            return tcp.Connected;
        }
        catch { return false; }
    }
}

/// <summary>
/// 触发式门控（vc39 ⑤·防 v28 ④择路病复发）：平时零介入；只有「起播连续失败 + 系统解析坏」证据链成立才启用代理，
/// 自愈成功或恢复后自动退出。纯逻辑可测：时钟与失败记账注入。
/// </summary>
public class SelfHealGate
{
    // 调参档接线（老板 2026-11-12 令·旋钮全量进设置页）：门槛与回探读 AdvancedTuning，门面即单一事实源
    public static int FailThreshold => AdvancedTuning.SelfHealFailGate;   // 连续起播失败达到该数才进入判别
    public const long GateHoldMs = 10 * 60_000;  // 启用后最短保持时长（防抖动反复开关，内部防抖不进设置页）
    public static long RetestMs => AdvancedTuning.SelfHealReprobeMs;     // 启用期间按设置间隔复测系统解析

    private readonly Func<long> _nowMs;
    private int _consecutiveFails;
    private long _gateUntilMs;
    private long _lastRetestMs;

    public SelfHealGate(Func<long>? nowMs = null) { _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }

    /// <summary>门控是否已启用（启用后 LibVLC 播放链走本地代理）。</summary>
    public bool IsEngaged { get; private set; }

    public string? LastVerdict { get; private set; }

    /// <summary>起播失败记账：达到阈值且未启用时，返回 true 提示调用方做三分判别（判别结果经 ReportTriage 落账）。</summary>
    public bool OnPlayFailed()
    {
        _consecutiveFails++;
        return !IsEngaged && _consecutiveFails >= FailThreshold;
    }

    /// <summary>起播成功记账：连续失败清零；启用期已过保持窗则退出门控。</summary>
    public void OnPlayOk()
    {
        _consecutiveFails = 0;
        if (IsEngaged && _nowMs() >= _gateUntilMs) { IsEngaged = false; LastVerdict = "自愈保持期结束，门控退出（回归系统解析）"; }
    }

    /// <summary>三分判别结论落账：系统解析坏才启用；路由死/出网断不启用（如实告示）。
    /// 启用期复测得健康（且已过保持窗）即退出——恢复自动退出，不靠播放成功才收。</summary>
    public bool ReportTriage(ResolutionTriage.Result triage)
    {
        LastVerdict = triage.Detail;
        if (IsEngaged)
        {
            if (triage.Kind == ResolutionTriage.VerdictKind.Healthy && _nowMs() >= _gateUntilMs)
            {
                IsEngaged = false;
                LastVerdict = "复测系统解析恢复，门控退出（回归系统解析）";
            }
            return false;
        }
        if (triage.Kind == ResolutionTriage.VerdictKind.SystemDnsBroken)
        {
            IsEngaged = true;
            _gateUntilMs = _nowMs() + GateHoldMs;
            _lastRetestMs = _nowMs();
            return true;
        }
        return false;
    }

    /// <summary>启用期复测：到点由调用方重做判别，系统恢复则退出；返回 true 表示需要复测。</summary>
    public bool ShouldRetest()
        => IsEngaged && _nowMs() - _lastRetestMs >= RetestMs && (_lastRetestMs = _nowMs()) > 0;

    /// <summary>③热修 A3·门控困死退出联动：门控启用后代理失活时由宿主层调用退场——
    /// 否则播放失败判别 verdict 仍 SystemDnsBroken，ReportTriage 已启用分支不重启代理不退出，永困死代理。</summary>
    internal void Disengage(string reason)
    {
        if (!IsEngaged) return;
        IsEngaged = false;
        LastVerdict = reason;
    }
}

/// <summary>
/// HLS 清单正文改写（vc39 ⑤·播放链覆盖）：把子清单/分片里的绝对域名 URL 改写为本地代理 URL（127.0.0.1:port/?u=原URL），
/// 分片流量直过代理不缓存。纯字符串变换可测；改写只发生在门控启用后由代理侧调用。
/// </summary>
public static class HlsRewriter
{
    public const string ProxyHost = "127.0.0.1";
    public static int ProxyPort => AdvancedTuning.ProxyPort; // 调参档接线：设置页可调（改后下次播放生效）

    /// <summary>
    /// 把正文中所有 http(s) 绝对 URL 包进代理（已代理过的跳过）；
    /// 相对路径行（评审观察②修法）：给了 baseUrl 时先按子清单自身地址转绝对再包——VLC 默认不发 Referer，
    /// 代理侧 Referer 还原兜不住，必须在改写时一次做对，零状态。
    /// </summary>
    public static string RewritePlaylist(string playlist, string? baseUrl = null)
    {
        if (string.IsNullOrEmpty(playlist)) return playlist;
        Uri? baseUri = null;
        if (!string.IsNullOrEmpty(baseUrl)) Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);
        var lines = playlist.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue; // 标签行不动
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Contains($":{ProxyPort}/?u=")) continue; // 已是代理 URL：不二次包裹
                lines[i] = Wrap(line);
                continue;
            }
            if (baseUri != null)
            {
                try
                {
                    var abs = new Uri(baseUri, line).ToString();
                    // ③热修 C7：基址带票据 query（重定向后最终 URL）时相对分片原样带上——
                    // RFC 3986 相对解析会丢基址 query，票据型轮换源相对分片丢票即 404
                    var q = baseUri.Query;
                    if (!string.IsNullOrEmpty(q) && !abs.Contains('?')) abs += q;
                    lines[i] = Wrap(abs);
                }
                catch { /* 解不了原样保留 */ }
            }
        }
        return string.Join("\n", lines);
    }

    public static string Wrap(string url) => $"http://{ProxyHost}:{ProxyPort}/?u={Uri.EscapeDataString(url)}";

    /// <summary>查询串解析（手写拆对，避免引 System.Web）：无 ? 时空表；只取第一个等号后的值。</summary>
    internal static System.Collections.Generic.Dictionary<string, string> ParseQuery(string query)
    {
        var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return map;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in q.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq].Replace("+", " "));
            var val = Uri.UnescapeDataString(pair[(eq + 1)..].Replace("+", " "));
            if (!map.ContainsKey(key)) map[key] = val;
        }
        return map;
    }
    public static bool TryUnwrap(string proxyUrl, out string original)
    {
        original = "";
        try
        {
            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || uri.Host != ProxyHost || uri.Port != ProxyPort) return false;
            if (!ParseQuery(uri.Query).TryGetValue("u", out var inner) || inner is not { Length: > 0 }) return false;
            original = inner;
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// 自愈本地代理内核（vc39 ⑤·播放链覆盖，判读书 3.3 A 路线）：
/// HttpListener 监听 127.0.0.1:47618；LibVLC 播放链全部请求打代理，代理按存活通道（DoH 择优解析）
/// 回源转发：子清单正文域名 URL 改写为代理 URL（HlsRewriter），TS/分片字节直过不缓存。
/// 门控未启用时 Start 后也接受请求但仅透传（不启用改写）——实际由 LiveVideoHost 按门控决定播原始 URL 还是代理 URL。
/// 生命周期：Start 幂等；Stop 释放监听；起播失败/恢复由 SelfHealGate 记账驱动。
/// </summary>
public class SelfHealProxyKernel : IDisposable
{
    /// <summary>打点出口（BLL 不反引 UI）：由宿主层注入 CrashNet.Record；未注入只静默。</summary>
    public static Action<string>? Reporter { get; set; }

    private static void Report(string msg)
    {
        try { Reporter?.Invoke($"[网络自愈代理] {msg}"); } catch { }
    }

    private System.Net.HttpListener? _listener;
    private CancellationTokenSource? _cts;

    // vc52 黄四：AcceptLoop 每请求 Task.Run 无并发上限——坏源慢响应时回源任务无限堆积，
    // 线程池/连接被分片拖死（长跑劣化）。加信号量限流（network-streaming 评审 P2 建议 8–16 取 12），
    // 满载新请求 503 快速失败（VLC 对分片 503 会重试，不锁死播放链）
    internal const int MaxConcurrentForwards = 12;
    private readonly SemaphoreSlim _inflight = new(MaxConcurrentForwards, MaxConcurrentForwards);

    // 评审观察①修法（最重红）：回源 HttpClient 不得走系统解析——SystemDnsBroken 场景裸 HttpClient 同样 502，
    // ⑤在目标病根下等于没生效。SocketsHttpHandler.ConnectCallback 按 DoH 择优解析拿 IP 直连（SNI 由 handler
    // 按原请求主机补齐，TLS 不受影响）；连接超时 5 秒，IP 逐个试不连坏 IP 死磕。
    private static readonly System.Net.Http.HttpClient Forward = CreateForwardClient();

    private static System.Net.Http.HttpClient CreateForwardClient()
    {
        var dns = new StreamDnsPicker();
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                int port = ctx.DnsEndPoint.Port;
                IPAddress[] ips;
                try { ips = await dns.ResolveAsync(host); }
                catch { ips = Array.Empty<IPAddress>(); }
                Exception? last = null;
                foreach (var ip in ips)
                {
                    // ③热修 B6：失败分支必须 Dispose——sock 提到 try 外声明，catch 才能收（防 finalizer 兜底泄漏）
                    var sock = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    try
                    {
                        await sock.ConnectAsync(new IPEndPoint(ip, port), ct);
                        return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { sock.Dispose(); } catch { }
                    }
                }
                throw last ?? new System.IO.IOException($"自愈代理回源无可用 IP：{host}");
            },
        };
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public SelfHealProxyKernel(StreamDnsPicker? dns = null)
    {
        _ = dns; // 回源解析已统一下沉到 Forward 的 ConnectCallback（构造注入仅为兼容签名保留）
        _listener = CreateListener();
    }

    /// <summary>vc52 红三：监听实例构造收口——构造与 Start 复活路径共用（前缀串两现提方法，防改一处漏一处）。</summary>
    private static System.Net.HttpListener CreateListener()
    {
        var l = new System.Net.HttpListener();
        l.Prefixes.Add($"http://{HlsRewriter.ProxyHost}:{HlsRewriter.ProxyPort}/");
        return l;
    }

    public bool IsRunning { get; private set; }

    /// <summary>③热修 A1：AcceptLoop 异常退出收账——监听异常后不得留僵尸态（监听开着、IsRunning 仍 true、accept 无人接）。
    /// 与 Stop 同路径收账并打点；tests 直调断言 IsRunning=false。</summary>
    internal void MarkDead()
    {
        Report("AcceptLoop 异常退出，代理收账（IsRunning=false），等下次启用重建");
        Stop();
    }

    /// <summary>评审观察③修法：127.0.0.1 假活陷阱——IsRunning 只代表监听起过，运行中可能僵死。
    /// 切代理前实测本机回环可达，探不通按未启用处理（不把播放链切进死代理）。
    /// ③热修 A2：TCP connect 只证明监听 socket 开着，accept 停摆时内核 backlog 兜着 connect 仍成功（假活）——
    /// 升级两道：TCP 连不上直接判死；连上再发 GET /ping 要 HTTP 实响应（任意状态码，500ms 超时）才算活。</summary>
    public bool IsAlive
    {
        get
        {
            if (!IsRunning) return false;
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connect = tcp.ConnectAsync(IPAddress.Loopback, HlsRewriter.ProxyPort);
                if (!connect.Wait(500) || !tcp.Connected) return false;
            }
            catch { return false; }
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                var probe = http.GetAsync($"http://{HlsRewriter.ProxyHost}:{HlsRewriter.ProxyPort}/ping");
                if (!probe.Wait(500)) return false;
                _ = probe.Result.StatusCode; // 收到 HTTP 响应（任意状态码）=accept 环活着
                return true;
            }
            catch { return false; }
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        // vc52 红三：复活链打通——Stop()/MarkDead()/Start 异常路径都把 _listener 置 null，
        // 旧写法对 null 直接 return，代理停过一次即永死（门控启用时播放链切进死代理，
        // 复活只能靠宿主 new 新内核，而内核是进程级单例永不再建）。null 时原地重建监听实例，
        // Start 幂等口径不变：IsRunning 已 true 才拒
        _listener ??= CreateListener();
        try
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _ = Task.Run(() => AcceptLoopAsync(_cts!.Token));
        }
        catch (Exception ex)
        {
            // 端口被占/权限不足：代理起不来按未启用处理（LibVLC 继续播原始 URL），不挡播放
            Report(ex.Message);
            _listener = null;
            IsRunning = false;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        _listener = null;
        IsRunning = false;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch
            {
                // ③热修 A1：异常退出也要收账——旧写法 return 后监听仍开着、IsRunning 仍 true（僵尸态，
                // 假活陷阱半修只修了一半）。正常 Stop 路径本就 IsRunning=false，MarkDead 幂等无副作用
                MarkDead();
                return;
            }
            // vc52 黄四：满载快速失败——Wait(0) 同步探测不阻塞 accept 环；超限 503 立即回，
            // 不入队（回源任务由 finally 保证 Release）
            if (!_inflight.Wait(0, ct))
            {
                try { ctx.Response.StatusCode = 503; ctx.Response.Close(); } catch { }
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(ctx, ct); }
                catch (Exception ex) { Report($"代理单请求失败：{ex.Message}"); try { ctx.Response.StatusCode = 502; ctx.Response.Close(); } catch { } }
                finally { _inflight.Release(); }
            }, ct);
        }
    }

    private async Task HandleAsync(System.Net.HttpListenerContext ctx, CancellationToken ct)
    {
        var raw = ctx.Request.Url?.ToString() ?? "";
        // ③热修 A2 配套：/ping 短路端点（防自环）——探活请求若按普通 URL 走 Forward，
        // 127.0.0.1 为 IP 字面量直连回自身 → 自我递归死循环；必须在入口短路直回
        if (ctx.Request.Url?.AbsolutePath == "/ping")
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return;
        }
        // 代理 URL 解包：127.0.0.1:port/?u=原URL；非代理形态直接按原 URL 用
        var target = HlsRewriter.TryUnwrap(raw, out var inner) ? inner : raw;
        // 相对路径（子清单里的变体分片）：按上一跳 Referer 还原为原站绝对地址
        if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var referer = ctx.Request.Headers["Referer"];
            if (Uri.TryCreate(referer, UriKind.Absolute, out var baseUri) && HlsRewriter.TryUnwrap(baseUri.ToString(), out var baseInner))
                target = new Uri(new Uri(baseInner), target).ToString();
            else { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }
        }

        // v46 修（自环兜底 B1）：target 指向本代理自身（127.0.0.1/localhost + 本端口）且非 ?u= 解包形态
        // （如清单被污染或误填本机地址）时，Forward 会打回自身监听器形成无限代理链——
        // Forward 超时逐层 502 虽不永久存活，但触发时是 15 秒连接风暴；与 /ping 同口径入口短路直回 404
        if (Uri.TryCreate(target, UriKind.Absolute, out var selfCheck)
            && (selfCheck.Host == "127.0.0.1" || selfCheck.Host == "localhost")
            && selfCheck.Port == HlsRewriter.ProxyPort)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        // 评审观察①修法：回源解析已下沉到 Forward 的 ConnectCallback（DoH 择优 IP 直连），
        // 此处不再空跑 ResolveAsync（旧写法结果丢弃，SystemDnsBroken 场景照样 502）

        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, target);
        // 透传关键头：Range（分片续传）、UA/Referer（防盗链）、Host 由 HttpClient 按目标 URL 自带
        if (ctx.Request.Headers["Range"] is { Length: > 0 } range) req.Headers.TryAddWithoutValidation("Range", range);
        if (ctx.Request.Headers["User-Agent"] is { Length: > 0 } ua) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        if (ctx.Request.Headers["Referer"] is { Length: > 0 } rf && HlsRewriter.TryUnwrap(rf, out var rfInner)) req.Headers.TryAddWithoutValidation("Referer", rfInner);

        using var resp = await Forward.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
        // 评审观察①延伸修法（票据型轮换源 302）：回源默认跟随重定向，响应体来自最终 URL；
        // 相对分片的解析基址必须是重定向后的最终 URL（票据 ?tm&key 也须随基址带上），
        // 用入口 target 当基址会让相对分片落到入口 host → 实测 404（74.91 家族 19:2x 沙箱实证）。
        var finalBase = resp.RequestMessage?.RequestUri?.ToString();
        if (string.IsNullOrEmpty(finalBase)) finalBase = target;
        ctx.Response.StatusCode = (int)resp.StatusCode;
        ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "";
        if (resp.Content.Headers.ContentLength is { } len) ctx.Response.ContentLength64 = len;

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        bool isPlaylist = media.Contains("mpegurl") || target.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        if (isPlaylist)
        {
            // 子清单/主清单：正文域名 URL 全改写为代理 URL（播放链后续请求继续走代理）；
            // 相对路径按重定向后最终地址转绝对再包（零状态，票据 query 随基址保留）
            var body = await resp.Content.ReadAsStringAsync(ct);
            var rewritten = HlsRewriter.RewritePlaylist(body, finalBase);
            var buf = System.Text.Encoding.UTF8.GetBytes(rewritten);
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf, ct);
        }
        else
        {
            // TS/分片：字节直过，零缓存零改写
            // ③热修 B5：body 拷贝挂 30 秒 CTS（与 kernel ct 链接）——HttpClient.Timeout 在
            // ResponseHeadersRead 下只盖到响应头，CopyToAsync 可无限期挂住（长跑线程/连接泄漏）
            using var bodyCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCts.CancelAfter(TimeSpan.FromSeconds(30));
            await resp.Content.CopyToAsync(ctx.Response.OutputStream, bodyCts.Token);
        }
        ctx.Response.Close();
    }

    public void Dispose() => Stop();
}
