namespace TaoLive.Model;

/// <summary>
/// 源端点：一路可拉的流地址。Kind 区分协议族，权重供备援排序。
/// </summary>
public sealed record SourceEndpoint(
    string Url,
    SourceKind Kind,
    int Weight)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Url)
        && Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>源协议族。纯 IP 直连优先，CDN 次之。</summary>
public enum SourceKind
{
    PureIp,
    Cdn,
    Web,
}
