using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc142 健康点真拉流（老板令「健康点真拉流」）：纯逻辑用例——分片挑选、主清单变体候补、
/// HTML 广告壳判定。IO 薄壳（FetchHeadAsync）不进单测（网络依赖），由真机验收兜底。
/// </summary>
public class StreamProbeTests
{
    // ---- PickFirstSegment：媒体清单取第一片（复用 HlsPlaylistInspector 解析） ----

    [Fact]
    public void PickFirstSegment_MediaPlaylist_ReturnsFirstSegment()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:5.0,\nseg1.ts\n#EXTINF:5.0,\nseg2.ts\n";
        var seg = StreamProbe.PickFirstSegment(text, "https://cdn.example.com/live/index.m3u8");
        Assert.Equal("https://cdn.example.com/live/seg1.ts", seg);
    }

    [Fact]
    public void PickFirstSegment_MasterPlaylist_ReturnsNull()
    {
        var text = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nvariant.m3u8\n";
        Assert.Null(StreamProbe.PickFirstSegment(text, "https://cdn.example.com/live/index.m3u8"));
    }

    [Fact]
    public void PickFirstSegment_EmptyText_ReturnsNull()
    {
        Assert.Null(StreamProbe.PickFirstSegment("", "https://cdn.example.com/a.m3u8"));
        Assert.Null(StreamProbe.PickFirstSegment(null, "https://cdn.example.com/a.m3u8"));
    }

    // ---- PickFirstVariant：主清单取首个变体地址（相对地址不收，须自带绝对地址） ----

    [Fact]
    public void PickFirstVariant_MasterPlaylist_ReturnsFirstAbsoluteVariant()
    {
        var text = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nhttps://cdn.example.com/live/720p.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nhttps://cdn.example.com/live/480p.m3u8\n";
        Assert.Equal("https://cdn.example.com/live/720p.m3u8", StreamProbe.PickFirstVariant(text));
    }

    [Fact]
    public void PickFirstVariant_RelativeOnly_ReturnsNull()
    {
        // 相对变体地址不收（探针不做基址拼接第二层，宁判死交预检重探，不误绿）
        var text = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nvariant.m3u8\n";
        Assert.Null(StreamProbe.PickFirstVariant(text));
    }

    [Fact]
    public void PickFirstVariant_EmptyText_ReturnsNull()
    {
        Assert.Null(StreamProbe.PickFirstVariant(""));
        Assert.Null(StreamProbe.PickFirstVariant(null));
    }

    // ---- IsAdShell：HTML 广告壳判定（vc86 口径：首个非空白字节 '<' 即壳） ----

    [Fact]
    public void IsAdShell_HtmlHead_True()
    {
        Assert.True(StreamProbe.IsAdShell("<html><body>ad</body></html>"u8.ToArray()));
        Assert.True(StreamProbe.IsAdShell("  \r\n<!DOCTYPE html>"u8.ToArray())); // 前导空白跳过
    }

    [Fact]
    public void IsAdShell_RealStreamOrPlaylist_False()
    {
        Assert.False(StreamProbe.IsAdShell("#EXTM3U\n#EXTINF:5.0,\nseg.ts\n"u8.ToArray()));
        // TS 二进制同步字节 0x47
        Assert.False(StreamProbe.IsAdShell(new byte[] { 0x47, 0x40, 0x01 })); 
        Assert.False(StreamProbe.IsAdShell(new byte[] { 0x00, 0x00, 0x01 }));
    }

    [Fact]
    public void IsAdShell_EmptyOrNull_False()
    {
        Assert.False(StreamProbe.IsAdShell(null));
        Assert.False(StreamProbe.IsAdShell(Array.Empty<byte>()));
        Assert.False(StreamProbe.IsAdShell("   "u8.ToArray())); // 全空白：无首个非空白字节，不判壳
    }
}
