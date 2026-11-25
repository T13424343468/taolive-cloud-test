using System;

using System.Net.Sockets;

using System.Threading;

using System.Threading.Tasks;

namespace TaoLive.Session;

/// <summary>

/// 等网就绪（方案书收口批②·开机自启链路）：等网就绪后再进默认台。

/// 探测=轻量 TCP 连通（方案书 v82：非 Wi-Fi 状态位，防假在线）；初值 15 秒，

/// 超时照常放行进默认台（起播失败走既有重试）。计时按单调钟。

/// </summary>

public static class NetReadiness

{

    public const int TimeoutMs = 15_000;

    public const int ProbeIntervalMs = 1_000;

    public const int SingleProbeTimeoutMs = 2_000;

    /// <summary>公共 DNS anycast：只证网络连通，不依赖任何单一业务服务器。</summary>

    public const string ProbeHost = "223.5.5.5";

    public const int ProbePort = 53;

    /// <summary>等网就绪：探通即回 true；到时上限未通回 false（调用方照常进默认台）。probe 可注入供测试。</summary>

    public static async Task<bool> WaitAsync(

        int timeoutMs = TimeoutMs,

        Func<string, int, CancellationToken, Task<bool>>? probe = null,

        Action<string>? report = null)

    {

        probe ??= TcpProbeAsync;

        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)

        {

            try

            {

                if (await probe(ProbeHost, ProbePort, CancellationToken.None)) return true;

            }

            catch (Exception ex) { report?.Invoke($"[NetReadiness] 单探异常续探：{ex.Message}"); }

            long remain = deadline - Environment.TickCount64;

            if (remain <= 0) return false; // 超时照常放行（方案书收口批②）

            await Task.Delay((int)Math.Min(ProbeIntervalMs, remain)).ConfigureAwait(false);

        }

    }

    private static async Task<bool> TcpProbeAsync(string host, int port, CancellationToken ct)

    {

        using var tcp = new TcpClient();

        using var timeout = new CancellationTokenSource(SingleProbeTimeoutMs);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try

        {

            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false); // 单探不恋战

            return tcp.Connected;

        }

        catch (OperationCanceledException) { return false; }

    }

}
