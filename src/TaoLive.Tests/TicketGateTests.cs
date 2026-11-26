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

/// <summary>
/// vc93 坏票反查放宽回归（真机 2026-11-26 崩溃日志页实锤：取票成功→内核 4xx→反查未命中×N→不出画循环）：
/// 内核 4xx 的失败 URL 常是子清单/分片，与缓存票不同名——同主机同目录即视为同票换新；
/// 跨目录不认，同域多票无法归属不认（宁缺勿错，交会话自救）。
/// </summary>
public class TicketReissueScopeTests
{
    private const string DirectUrl = "https://cdn.example.com/live/cctv6/master.m3u8";

    private static async Task<TicketResolver> CachedDirectAsync(string channelKey, string url)
    {
        var resolver = new TicketResolver();
        var endpoint = new SourceEndpoint(url, SourceKind.Web, 0);
        var resolved = await resolver.ResolveAsync(endpoint, channelKey, channelKey);
        Assert.Equal(url, resolved); // 直连端点原样放行并落缓存
        return resolver;
    }

    [Fact]
    public async Task Subresource_4xx_same_host_same_directory_reissues_fresh_ticket()
    {
        var resolver = await CachedDirectAsync("cctv6", DirectUrl);
        var fresh = await resolver.ReissueAsync("https://cdn.example.com/live/cctv6/seg0.ts?token=abc");
        Assert.NotNull(fresh); // 同主机同目录的子资源 4xx：命中换票（旧口径全等反查必未命中）
    }

    [Fact]
    public async Task Query_only_difference_still_reissues()
    {
        var resolver = await CachedDirectAsync("cctv6", DirectUrl);
        var fresh = await resolver.ReissueAsync("https://cdn.example.com/live/cctv6/master.m3u8?token=expired");
        Assert.NotNull(fresh); // 同名不同 query（票据过期重签场景）：命中换票
    }

    [Fact]
    public async Task Cross_directory_or_cross_host_misses()
    {
        var resolver = await CachedDirectAsync("cctv6", DirectUrl);
        Assert.Null(await resolver.ReissueAsync("https://cdn.example.com/other/seg0.ts")); // 跨目录不认
        Assert.Null(await resolver.ReissueAsync("https://other.example.com/live/cctv6/seg0.ts")); // 跨主机不认
    }

    [Fact]
    public async Task Two_tickets_same_scope_is_ambiguous_and_misses()
    {
        var resolver = await CachedDirectAsync("cctv6", "https://cdn.example.com/live/master6.m3u8");
        _ = await resolver.ReissueAsync("https://cdn.example.com/live/seg0.ts"); // 先耗掉 cctv6 一票（重取后缓存仍在）
        var resolver2 = resolver; // 同一缓存再挂第二张同域票
        var endpoint2 = new SourceEndpoint("https://cdn.example.com/live/master1.m3u8", SourceKind.Web, 1);
        _ = await resolver2.ResolveAsync(endpoint2, "cctv1", "cctv1");

        Assert.Null(await resolver2.ReissueAsync("https://cdn.example.com/live/seg9.ts")); // 同域两票无法归属：宁缺勿错
    }

    [Fact]
    public void Root_directory_urls_never_match()
    {
        Assert.False(TicketResolver.SameTicketScope("https://cdn.example.com/a.m3u8", "https://cdn.example.com/b.ts"));
    }
}

/// <summary>
/// vc93 换票频控回归（真机 2026-11-26 崩溃日志页实锤：新票秒被 4xx，内核逐分片重开→每秒 1-2 次取票风暴）：
/// 同端点 60 秒窗内换票超 4 次即熔断返回 null，交会话层退避自救；窗口滑过自动恢复。
/// </summary>
public class TicketReissueRateLimitTests
{
    [Fact]
    public async Task Reissue_storm_beyond_four_per_window_is_circuit_broken()
    {
        var resolver = new TicketResolver();
        var url = "https://cdn.example.com/live/master.m3u8";
        var endpoint = new SourceEndpoint(url, SourceKind.Web, 0);
        _ = await resolver.ResolveAsync(endpoint, "cctv1", "cctv1");

        string? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await resolver.ReissueAsync("https://cdn.example.com/live/seg0.ts?x=" + i);
        }
        Assert.Null(last); // 第 5、6 次被频控熔断（前 4 次放行）
    }

    [Fact]
    public async Task Rate_limit_is_per_endpoint_not_global()
    {
        var resolver = new TicketResolver();
        var e1 = new SourceEndpoint("https://cdn-a.example.com/live/master.m3u8", SourceKind.Web, 0);
        var e2 = new SourceEndpoint("https://cdn-b.example.com/live/master.m3u8", SourceKind.Web, 1);
        _ = await resolver.ResolveAsync(e1, "cctv1", "cctv1");
        _ = await resolver.ResolveAsync(e2, "cctv2", "cctv2");

        for (var i = 0; i < 6; i++)
        {
            _ = await resolver.ReissueAsync("https://cdn-a.example.com/live/seg0.ts?x=" + i);
        }
        var other = await resolver.ReissueAsync("https://cdn-b.example.com/live/seg0.ts"); // 另一端点不受连坐
        Assert.NotNull(other);
    }
}
