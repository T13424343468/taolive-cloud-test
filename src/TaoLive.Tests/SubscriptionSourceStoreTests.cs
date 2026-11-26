using System.IO;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 内置订阅源库（老板 2026-11-26 拍板「只做内置订阅」）：
/// m3u 解析容错、查重（内置表+本库）、单台/全库封顶、落盘回载、FromRegistry 末位追加（权重 2 恒垫底）。
/// 网络拉取与试活不进单测（外部依赖），以解析与入库纪律为主。
/// </summary>
public class SubscriptionSourceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"taolive-sub-{Guid.NewGuid():N}");
    private readonly SubscriptionSourceStore _store = new();

    public SubscriptionSourceStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败不碍事 */ }
    }

    // ---- m3u 解析：EXTINF 台名 + 地址对，坏段跳过 ----

    [Fact]
    public void ParseM3U_extracts_name_url_pairs_and_skips_bad_lines()
    {
        const string m3u = """
            #EXTM3U
            #EXTINF:-1 tvg-id="a" group-title="央卫",CCTV-1 综合
            http://1.2.3.4/live/cctv1.m3u8
            #EXTINF:-1,湖南卫视
            https://example.com/hn.m3u8
            坏行没有台名
            #EXTINF:-1,空地址台
            #EXTINF:-1,湖北卫视
            ftp://not-http.example.com/x.m3u8
            """;
        var parsed = SubscriptionSourceStore.ParseM3U(m3u);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("CCTV-1 综合", parsed[0].Name);
        Assert.Equal("http://1.2.3.4/live/cctv1.m3u8", parsed[0].Url);
        Assert.Equal("湖南卫视", parsed[1].Name);
    }

    [Fact]
    public void ParseM3U_handles_empty_and_garbage_input()
    {
        Assert.Empty(SubscriptionSourceStore.ParseM3U(""));
        Assert.Empty(SubscriptionSourceStore.ParseM3U("不是 m3u 的乱文本"));
    }

    // ---- 入库纪律：查重、单台封顶、全库封顶 ----

    [Fact]
    public void Ingest_dedups_against_builtin_registry_and_self()
    {
        _store.Load(_dir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 内置表已收录的 URL（湖南 tsfile）不入库
        var added = _store.Ingest(new List<(string, string, string)>
        {
            ("湖南卫视", "http://124.165.251.82:85/tsfile/live/0128_1.m3u8?key=txiptv&playlive=1&authid=0", ""),
        }, now);
        Assert.Equal(0, added);
        // 同 URL 二次入库去重
        added = _store.Ingest(new List<(string, string, string)>
        {
            ("测试台", "http://10.0.0.1/a.m3u8", ""),
            ("测试台", "http://10.0.0.1/a.m3u8", ""),
        }, now);
        Assert.Equal(1, added);
        Assert.Single(_store.GetAll("测试台"));
    }

    [Fact]
    public void Ingest_caps_per_channel_and_total()
    {
        _store.Load(_dir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = Enumerable.Range(1, SubscriptionSourceStore.MaxPerChannel + 2)
            .Select(i => ($"封顶台", $"http://10.0.{i}.0/live.m3u8", "")).ToList();
        Assert.Equal(SubscriptionSourceStore.MaxPerChannel, _store.Ingest(batch, now));
        // 全库封顶：MaxTotal 条后不再收
        var total = Enumerable.Range(0, SubscriptionSourceStore.MaxTotal)
            .Select(i => ($"台{i}", $"http://10.1.{i / 256}.{i % 256}/live.m3u8", "")).ToList();
        Assert.Equal(SubscriptionSourceStore.MaxTotal - SubscriptionSourceStore.MaxPerChannel,
            _store.Ingest(total, now));
        Assert.Equal(0, _store.Ingest(new List<(string, string, string)>
        {
            ("溢出台", "http://10.9.9.9/live.m3u8", ""),
        }, now));
    }

    // ---- 落盘回载：重启仍在 ----

    [Fact]
    public void Load_restores_persisted_entries()
    {
        _store.Load(_dir);
        _store.Ingest(new List<(string, string, string)>
        {
            ("回载台", "http://10.2.2.2/live.m3u8", "UA-X"),
        }, 1234567890L);
        var fresh = new SubscriptionSourceStore();
        fresh.Load(_dir);
        var entries = fresh.GetAll("回载台");
        Assert.Single(entries);
        Assert.Equal("http://10.2.2.2/live.m3u8", entries[0].Url);
        Assert.Equal("UA-X", entries[0].Ua);
        Assert.True(fresh.Has("回载台"));
        Assert.False(fresh.Has("没收录的台"));
    }

    // ---- FromRegistry 接线：订阅源末位追加（权重 2 恒垫底）；注册表无登记台也能吃订阅源 ----

    [Fact]
    public void FromRegistry_appends_subscription_sources_after_builtin()
    {
        _store.Load(_dir);
        _store.Ingest(new List<(string, string, string)>
        {
            ("新疆卫视", "http://10.3.3.3/sub.m3u8", ""),
        }, 1L);
        var ch = new Channel("xj", "新疆卫视", "测试", new List<SourceEndpoint>());
        var endpoints = FallbackQueueSource.FromRegistry(ch, subscriptionStore: _store);
        Assert.True(endpoints.Count >= 4, "内置表 3 路 + 订阅 1 路");
        Assert.Equal("http://10.3.3.3/sub.m3u8", endpoints[^1].Url); // 末位
        Assert.Equal(2, endpoints[^1].Weight);                        // 权重 2 恒垫底
        Assert.All(endpoints.Take(endpoints.Count - 1), e => Assert.True(e.Weight < 2));
    }

    [Fact]
    public void FromRegistry_serves_subscription_only_channels()
    {
        _store.Load(_dir);
        _store.Ingest(new List<(string, string, string)>
        {
            ("订阅独有台", "http://10.4.4.4/only.m3u8", ""),
        }, 1L);
        var ch = new Channel("sub-only", "订阅独有台", "测试", new List<SourceEndpoint>());
        var endpoints = FallbackQueueSource.FromRegistry(ch, subscriptionStore: _store);
        Assert.Single(endpoints);
        Assert.Equal(2, endpoints[0].Weight);
        // 两库皆无 → 空表（行为不倒退）
        Assert.Empty(FallbackQueueSource.FromRegistry(
            new Channel("none", "两库皆无台", "测试", new List<SourceEndpoint>()), subscriptionStore: _store));
    }

    // ---- 新收录扩编（老板 2026-11-26 令「要 100 覆盖，最好还能有新收录」）：归一、别名、口径、限额、并入 ----

    [Fact]
    public void NormalizeName_strips_spaces_and_dashes()
    {
        Assert.Equal("CCTV1综合", SubscriptionSourceStore.NormalizeName("CCTV-1 综合"));
        Assert.Equal("北京卫视", SubscriptionSourceStore.NormalizeName("北京 卫视"));
        Assert.Equal("北京卫视", SubscriptionSourceStore.NormalizeName("北京　卫视")); // 全角空格
        Assert.Equal("北京卫视", SubscriptionSourceStore.NormalizeName("北京－卫视")); // 全角横杠
        Assert.Equal("", SubscriptionSourceStore.NormalizeName(null!));
    }

    [Fact]
    public void BuildAliasMaps_cctv_base_names_and_drops_ambiguous()
    {
        var alias = SubscriptionSourceStore.BuildAliasMap(new[]
        {
            "CCTV1综合", "CCTV2财经", "CCTV4中文国际", "CCTV4K超高清", "CCTV5+体育赛事",
        });
        Assert.Equal("CCTV1综合", alias["CCTV1"]);   // CCTV-1/CCTV1 → CCTV1综合
        Assert.Equal("CCTV2财经", alias["CCTV2"]);
        Assert.Equal("CCTV5+体育赛事", alias["CCTV5+"]);
        Assert.False(alias.ContainsKey("CCTV4"));    // CCTV4中文国际 与 CCTV4K超高清 共用基础名 → 歧义弃用
        Assert.False(SubscriptionSourceStore.BuildAliasMap(new[] { "湖南卫视" }).ContainsKey("湖南"));
    }

    [Fact]
    public void IsMainstreamNewChannel_gates_to_satellite_cctv_cgtn()
    {
        Assert.True(SubscriptionSourceStore.IsMainstreamNewChannel("北京卫视"));
        Assert.True(SubscriptionSourceStore.IsMainstreamNewChannel("CCTV5+"));
        Assert.True(SubscriptionSourceStore.IsMainstreamNewChannel("CGTN纪录"));
        Assert.False(SubscriptionSourceStore.IsMainstreamNewChannel("CCTV4K超高清")); // CCTV 前缀但非数字频道
        Assert.False(SubscriptionSourceStore.IsMainstreamNewChannel("某某县新闻综合"));
        Assert.False(SubscriptionSourceStore.IsMainstreamNewChannel(""));
    }

    [Fact]
    public void Ingest_caps_new_channels_and_per_new_channel()
    {
        _store.Load(_dir);
        var now = 1L;
        // 目录外新台：单台最多 MaxPerNewChannel 路
        var per = Enumerable.Range(1, SubscriptionSourceStore.MaxPerNewChannel + 1)
            .Select(i => ("新收录台", $"http://10.5.{i}.0/live.m3u8", "")).ToList();
        Assert.Equal(SubscriptionSourceStore.MaxPerNewChannel, _store.Ingest(per, now, new[] { "已知台" }));
        // 新收录台数封顶 MaxNewChannels：已有台补路不受限
        var batch = Enumerable.Range(0, SubscriptionSourceStore.MaxNewChannels)
            .Select(i => ($"卫视{i}", $"http://10.6.{i / 256}.{i % 256}/live.m3u8", "")).ToList();
        Assert.Equal(SubscriptionSourceStore.MaxNewChannels - 1, _store.Ingest(batch, now, new[] { "已知台" }));
        Assert.Equal(1, _store.Ingest(new List<(string, string, string)>
        {
            ("已知台", "http://10.7.7.7/known.m3u8", ""), // 目录内台补路：不受新收录限额
        }, now, new[] { "已知台" }));
        // 第 MaxNewChannels+1 个新台被拒
        Assert.Equal(0, _store.Ingest(new List<(string, string, string)>
        {
            ("超限卫视", "http://10.8.8.8/live.m3u8", ""),
        }, now, new[] { "已知台" }));
    }

    [Fact]
    public void GetNewChannels_returns_unknown_channels_only()
    {
        _store.Load(_dir);
        _store.Ingest(new List<(string, string, string)>
        {
            ("目录内台", "http://10.9.1.1/a.m3u8", ""),
            ("新收录卫视", "http://10.9.1.2/b.m3u8", "UA-1"),
            ("新收录卫视", "http://10.9.1.3/c.m3u8", ""),
        }, 1L, new[] { "目录内台" });
        var fresh = _store.GetNewChannels(new[] { "目录内台" });
        Assert.Single(fresh);
        Assert.Equal("新收录卫视", fresh[0].Name);
        Assert.Equal(2, fresh[0].Entries.Count);
        Assert.Equal("UA-1", fresh[0].Entries[0].Ua);
        // 目录名吃满后无新台可报
        Assert.Empty(_store.GetNewChannels(new[] { "目录内台", "新收录卫视" }));
    }
}
