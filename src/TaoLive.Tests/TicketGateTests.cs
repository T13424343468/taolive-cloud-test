using System.Net;
using TaoLive.Catalog;
using TaoLive.Model;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc73 审查清零 P2 回归：取票闸按上游主机粒度单飞——
/// 同上游仍串行（防重复解析/限流），不同上游互不排队（旧全局闸让 5s 慢请求阻塞所有端点取票）。
/// </summary>
public class TicketGateTests
{
    /// <summary>按请求主机路由的假 HTTP：slow 主机挂起等待放行，其余主机即时回 404。</summary>
    private sealed class HostGatedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public HostGatedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _respond(request);
    }

    [Fact]
    public async Task Distinct_hosts_resolve_concurrently_without_blocking_each_other()
    {
        var slowTcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = TicketResolver.Http;
        Task<string?> slowCall = Task.FromResult<string?>(null);
        try
        {
            TicketResolver.Http = new HttpClient(new HostGatedHandler(_ => slowTcs.Task))
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            var resolver = new TicketResolver();
            var slowHost = new SourceEndpoint("https://api.gzstv.com/v1/tv/1", SourceKind.Web, 1);
            var fastHost = new SourceEndpoint("https://lived.jstv.com/hd/x.pro.m3u8", SourceKind.Web, 2);

            slowCall = resolver.ResolveAsync(slowHost, "ch1", "台1"); // 卡在假 HTTP：占住 gzstv 主机闸
            var fastCall = resolver.ResolveAsync(fastHost, "ch2", "台2"); // jstv 同步解析：不该被 gzstv 卡住

            var done = await Task.WhenAny(fastCall, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(fastCall, done); // 2 秒内完成=主机粒度单飞生效（全局闸下会被 slowCall 卡死）
            Assert.NotNull(await fastCall);
        }
        finally
        {
            slowTcs.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)); // 放行慢请求，避免悬挂
            TicketResolver.Http = previous;
        }
        await slowCall; // 收尾等完，防 xUnit 警告未观察任务
    }
}
