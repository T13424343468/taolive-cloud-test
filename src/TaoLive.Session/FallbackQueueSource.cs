using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 备源队列吃内置表（方案书收口批①）：注册表候选转源端点，供 PlaybackSession.FallbackSourceProvider 装配。
/// 顺序即注册表纪律序（观察档垫底、纯 IP 优先、登记序）——内置表 2026-11-12 实测活性为基线，随季度复测刷新；
/// 不做运行期常驻探活，84 条只在换源触发时遍历，不进预检常驻轮。
/// 活性按健康账轻排序（收口批①+v28 轻排序条款）：同台活源在前、账新者在前（越旧权重越低），
/// 无账/未探/失效候选保持注册表纪律序——只动同台内排序，不改台间顺序。
/// </summary>
public static class FallbackQueueSource
{
    /// <summary>备援候选健康提示（轻排序依据，只读账不写账）：Alive=近账可用；CheckedAtMs=核验时间（Unix 毫秒）。</summary>
    public sealed record FallbackHealthHint(bool Alive, long CheckedAtMs);

    /// <summary>本频道在内置表的全部备援候选（按纪律序+健康账轻排序）；无登记返回空表。
    /// healthOf：候选端点健康账查询（可空注入，空=退化为纯注册表纪律序，行为不倒退）。</summary>
    public static IReadOnlyList<SourceEndpoint> FromRegistry(
        Channel channel, Func<SourceEndpoint, FallbackHealthHint?>? healthOf = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!FallbackSourceRegistry.Has(channel.Name))
        {
            return Array.Empty<SourceEndpoint>();
        }
        var endpoints = FallbackSourceRegistry.GetAll(channel.Name)
            .Select(e => new SourceEndpoint(
                e.Url,
                IsIpLiteral(e.Url) ? SourceKind.PureIp : SourceKind.Cdn,
                e.ObservationRank ? 1 : 0))
            .ToList();
        if (healthOf is null) return endpoints;
        // 稳定轻排序：活源（账新者优先，越旧权重越低）垫到队首，其余保持注册表纪律序（索引稳定序）。
        return endpoints
            .Select((e, i) => (E: e, I: i, H: healthOf(e)))
            .OrderBy(t => t.H is { Alive: true } ? 0 : 1)
            .ThenByDescending(t => t.H is { Alive: true } ? t.H.CheckedAtMs : 0L)
            .ThenBy(t => t.I)
            .Select(t => t.E)
            .ToList();
    }

    /// <summary>纯 IP 字面量判别（直连免系统解析，解析僵死免疫）。</summary>
    private static bool IsIpLiteral(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6);
}
