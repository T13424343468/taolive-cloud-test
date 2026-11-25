namespace TaoLive.Model;

/// <summary>取流结果：换票后的可播地址 + 透传头（Referer/UA）。引擎搬回：vc53 BLL StreamResolver 内类型上移 Model（Session 预取与 Catalog 取票共用）。</summary>
public class ResolvedStream
{
    public Channel Channel { get; set; } = new();
    public string Url { get; set; } = "";
    public List<(string Key, string Value)> Options { get; set; } = new();
}
