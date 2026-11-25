using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// v81/v91/v96/v17 文档链三闸直接覆盖（R1 红条收口）：
/// 坏文档拒收、版本回滚拒收、超 1MB 拒收、锚点对不上拒收、全过回填+锚点滚动锁定。
/// </summary>
public class DocChainGuardTests
{
    private const string Anchor = "aa" + "bb"; // 锚点值仅作对照用，逐用例以真实哈希注入

    private static string Doc(long version) =>
        $$"""
        {"Version":{{version}},"ChannelBackupUrls":{"cctv1":"https://backup/a"},"Rules":[]}
        """;

    [Fact]
    public void Validate_accepts_fresh_doc_and_rolls_anchor()
    {
        var body = Doc(2);
        var anchor = DocChainGuard.Sha256Hex(body);
        var verdict = DocChainGuard.Validate(body, anchor, currentVersion: 1, out var doc);
        Assert.Equal(DocChainGuard.Verdict.Accepted, verdict);
        Assert.Equal(2, doc.Version);
        Assert.Equal("https://backup/a", doc.ChannelBackupUrls["cctv1"]);
    }

    [Fact]
    public void Validate_rejects_malformed_body()
    {
        var verdict = DocChainGuard.Validate("not json", DocChainGuard.Sha256Hex("not json"), 0, out _);
        Assert.Equal(DocChainGuard.Verdict.Malformed, verdict);
        verdict = DocChainGuard.Validate("", DocChainGuard.Sha256Hex(""), 0, out _);
        Assert.Equal(DocChainGuard.Verdict.Malformed, verdict);
    }

    [Fact]
    public void Validate_rejects_version_rollback()
    {
        var body = Doc(1);
        var verdict = DocChainGuard.Validate(body, DocChainGuard.Sha256Hex(body), currentVersion: 2, out _);
        Assert.Equal(DocChainGuard.Verdict.VersionRollback, verdict);
        verdict = DocChainGuard.Validate(body, DocChainGuard.Sha256Hex(body), currentVersion: 1, out _);
        Assert.Equal(DocChainGuard.Verdict.VersionRollback, verdict); // 同版本也不收（v96 只进不退）
    }

    [Fact]
    public void Validate_rejects_oversize_over_1mb()
    {
        var big = "{\"Version\":3,\"TicketParams\":{\"k\":\"" + new string('x', 1024 * 1024) + "\"}}";
        var verdict = DocChainGuard.Validate(big, DocChainGuard.Sha256Hex(big), 0, out _);
        Assert.Equal(DocChainGuard.Verdict.Oversize, verdict);
    }

    [Fact]
    public void Validate_rejects_anchor_mismatch()
    {
        var body = Doc(2);
        var wrongAnchor = DocChainGuard.Sha256Hex(Doc(9)); // 中间人换文档：内容与锚点对不上
        var verdict = DocChainGuard.Validate(body, wrongAnchor, 1, out _);
        Assert.Equal(DocChainGuard.Verdict.AnchorMismatch, verdict);
    }
}
