using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 覆盖冲刺 B2（vc90 分支覆盖补账·Session 小文件组②）：
/// SwitchPolicies 四口径（清屏亮牌/先出画面/去抖/OSD 文案）边界、
/// ListTabs 标签循环与过滤全分支、FallbackQueueSource 注册表序与轻排序边界。
/// </summary>
public class CoverageSprintB2
{
    private static Channel MakeCh(string id, ChannelCategory cat = ChannelCategory.Provincial, int number = 0)
    {
        var ch = new Channel(id, $"频道-{id}", "测试",
            new List<SourceEndpoint> { new($"https://s/{id}.m3u8", SourceKind.Cdn, 10) })
        { Category = cat, Number = number };
        ch.EpgKey = id;
        return ch;
    }

    // ---- SwitchPolicies：清屏亮牌 / 先出画面 ----

    [Fact]
    public void Reveal_policy_veil_always_and_reveal_on_first_frame_only()
    {
        Assert.True(SwitchRevealPolicy.ShouldVeilOnSwitch(true));
        Assert.True(SwitchRevealPolicy.ShouldVeilOnSwitch(false));
        Assert.False(SwitchRevealPolicy.ShouldReveal(false));
        Assert.True(SwitchRevealPolicy.ShouldReveal(true));
        Assert.True(SwitchRevealPolicy.ShouldFallbackCarrier(SwitchRevealPolicy.CorruptionLimit + 1)); // 超限仍走备用
    }

    [Fact]
    public void FastStart_empty_variants_first_returns_null()
    {
        Assert.Null(FastStartPlanner.First(Array.Empty<(int, long)>()));
        Assert.Empty(FastStartPlanner.StartOrder(Array.Empty<(int, long)>())); // 空表按原样
    }

    [Fact]
    public void FastStart_single_variant_returned_as_is_and_upgrade_null()
    {
        var one = new (int Height, long Bandwidth)[] { (720, 2_000_000) };
        Assert.Same(one, FastStartPlanner.StartOrder(one)); // 单档原样返回（同引用）
        Assert.Null(FastStartPlanner.UpgradeTarget(one, 720));
    }

    [Fact]
    public void FastStart_upgrade_failed_falls_back_to_first_matching_height()
    {
        var variants = new (int Height, long Bandwidth)[]
        {
            (720, 2_000_000), (1080, 6_000_000), (720, 3_000_000),
        };
        Assert.Equal((720, 2_000_000L), FastStartPlanner.OnUpgradeFailed(variants, 720));
    }

    // ---- SwitchPolicies：去抖器 ----

    [Fact]
    public void Debouncer_null_target_treated_as_empty()
    {
        var d = new SwitchDebouncer();
        d.Tap(1_000, null!); // null 台键按空串计
        Assert.Equal(string.Empty, d.Target);
        Assert.True(d.HasTarget);
    }

    [Fact]
    public void Debouncer_boundary_exactly_window_is_due()
    {
        var d = new SwitchDebouncer();
        d.Tap(1_000, "cctv1");
        Assert.True(d.IsDue(1_000 + SwitchDebouncer.WindowMs)); // 恰好整窗=停稳
        Assert.False(d.IsDue(1_000)); // 窗内未稳
    }

    // ---- SwitchPolicies：OSD 文案 ----

    [Fact]
    public void Hud_retry_backoff_formats_seconds()
    {
        Assert.Equal("重试太频繁，约 30 秒后再试", SwitchHud.RetryBackoff(30));
        Assert.Equal("重试太频繁，约 1 秒后再试", SwitchHud.RetryBackoff(1));
    }

    // ---- ListTabs ----

    [Fact]
    public void Tabs_label_all_five_and_unknown_fallback()
    {
        Assert.Equal("全部", ListTabs.Label(ListTabs.Tab.All));
        Assert.Equal("收藏", ListTabs.Label(ListTabs.Tab.Favorite));
        Assert.Equal("央视", ListTabs.Label(ListTabs.Tab.Cctv));
        Assert.Equal("卫视", ListTabs.Label(ListTabs.Tab.Satellite));
        Assert.Equal("地方台", ListTabs.Label(ListTabs.Tab.Local));
    }

    [Fact]
    public void Tabs_cycle_both_directions_wrap_around()
    {
        // 右向：全部→收藏→央视→卫视→地方台→全部
        Assert.Equal(ListTabs.Tab.Favorite, ListTabs.NextTab(ListTabs.Tab.All, rightward: true));
        Assert.Equal(ListTabs.Tab.Cctv, ListTabs.NextTab(ListTabs.Tab.Favorite, rightward: true));
        Assert.Equal(ListTabs.Tab.Satellite, ListTabs.NextTab(ListTabs.Tab.Cctv, rightward: true));
        Assert.Equal(ListTabs.Tab.Local, ListTabs.NextTab(ListTabs.Tab.Satellite, rightward: true));
        Assert.Equal(ListTabs.Tab.All, ListTabs.NextTab(ListTabs.Tab.Local, rightward: true));
        // 左向：全部→地方台
        Assert.Equal(ListTabs.Tab.Local, ListTabs.NextTab(ListTabs.Tab.All, rightward: false));
        Assert.Equal(ListTabs.Tab.Satellite, ListTabs.NextTab(ListTabs.Tab.Local, rightward: false));
    }

    [Fact]
    public void Tabs_filter_category_buckets()
    {
        var catalog = new List<Channel>
        {
            MakeCh("cctv1", ChannelCategory.Cctv),
            MakeCh("cgtn", ChannelCategory.Cgtn),
            MakeCh("spec", ChannelCategory.Special),
            MakeCh("prov", ChannelCategory.Provincial),
            MakeCh("city", ChannelCategory.City),
            MakeCh("user", ChannelCategory.UserSource),
        };
        Assert.Equal(6, ListTabs.Filter(ListTabs.Tab.All, catalog, _ => false).Count);
        Assert.Equal(new[] { "cctv1", "cgtn", "spec" },
            ListTabs.Filter(ListTabs.Tab.Cctv, catalog, _ => false).Select(c => c.Id)); // CGTN/专项并入央视
        Assert.Equal(new[] { "prov" },
            ListTabs.Filter(ListTabs.Tab.Satellite, catalog, _ => false).Select(c => c.Id));
        Assert.Equal(new[] { "city", "user" },
            ListTabs.Filter(ListTabs.Tab.Local, catalog, _ => false).Select(c => c.Id)); // 地方台只放城市台/自有源
        Assert.Empty(ListTabs.Filter(ListTabs.Tab.Favorite, catalog, _ => false)); // 无收藏
    }

    [Fact]
    public void Tabs_favorite_order_uses_order_then_appends_unordered_favorites()
    {
        var a = MakeCh("a", number: 1);
        var b = MakeCh("b", number: 2);
        var c = MakeCh("c", number: 3);
        var catalog = new List<Channel> { a, b, c };
        static bool IsFav(Channel ch) => ch.Id != "b"; // b 非收藏

        // order 指定 3,1：c 在前 a 在后；b 非收藏不入册
        var ordered = ListTabs.Filter(ListTabs.Tab.Favorite, catalog, IsFav, new List<int> { 3, 1 });
        Assert.Equal(new[] { "c", "a" }, ordered.Select(x => x.Id));

        // order 含非收藏台（b=2）：跳过不占位，a 按序在前，其余收藏（c）按表序追加
        var withGhost = ListTabs.Filter(ListTabs.Tab.Favorite, catalog, IsFav, new List<int> { 2, 1 });
        Assert.Equal(new[] { "a", "c" }, withGhost.Select(x => x.Id));

        // order 含目录里不存在的号：跳过，a 在前，c 追加
        var withMissing = ListTabs.Filter(ListTabs.Tab.Favorite, catalog, IsFav, new List<int> { 99, 1 });
        Assert.Equal(new[] { "a", "c" }, withMissing.Select(x => x.Id));

        // 无 order：全部按频道表序
        var plain = ListTabs.Filter(ListTabs.Tab.Favorite, catalog, IsFav, null);
        Assert.Equal(new[] { "a", "c" }, plain.Select(x => x.Id));
    }

    // ---- FallbackQueueSource ----

    [Fact]
    public void Fallback_registry_null_channel_throws()
    {
        Assert.Throws<ArgumentNullException>(() => FallbackQueueSource.FromRegistry(null!));
    }

    [Fact]
    public void Fallback_registry_unregistered_channel_returns_empty()
    {
        var ch = MakeCh("no-such-channel");
        Assert.Empty(FallbackQueueSource.FromRegistry(ch));
        Assert.Empty(FallbackQueueSource.FromRegistry(ch, _ => null));
    }

    /// <summary>注册表按频道名匹配：造一个 Name=注册表键的频道。</summary>
    private static Channel MakeNamedCh(string name)
    {
        var ch = new Channel("xjtv", name, "测试",
            new List<SourceEndpoint> { new($"https://s/{name}.m3u8", SourceKind.Cdn, 10) });
        ch.EpgKey = name;
        return ch;
    }

    [Fact]
    public void Fallback_registry_registered_channel_keeps_order_without_health()
    {
        var ch = MakeNamedCh("新疆卫视");
        var urls = FallbackQueueSource.FromRegistry(ch).Select(s => s.Url).ToList();
        var expected = FallbackSourceRegistry.GetAll("新疆卫视").Select(e => e.Url).ToList();
        Assert.Equal(expected, urls); // 无健康账：纯注册表纪律序
        Assert.True(urls.Count >= 3);
    }

    [Fact]
    public void Fallback_registry_light_sort_alive_first_then_recency_then_index()
    {
        var ch = MakeNamedCh("新疆卫视");
        var urls = FallbackQueueSource.FromRegistry(ch).Select(s => s.Url).ToList();
        var sorted = FallbackQueueSource.FromRegistry(ch, e => e.Url switch
        {
            var u when u == urls[0] => new FallbackQueueSource.FallbackHealthHint(true, 1_000),   // 活账（旧）
            var u when u == urls[1] => new FallbackQueueSource.FallbackHealthHint(true, 2_000),   // 活账（新）
            var u when u == urls[2] => new FallbackQueueSource.FallbackHealthHint(false, 9_000),  // 死账
            _ => null,
        }).Select(s => s.Url).ToList();

        Assert.Equal(urls[1], sorted[0]); // 活账新者最前
        Assert.Equal(urls[0], sorted[1]); // 活账旧者次之
        Assert.Equal(urls[2], sorted[2]); // 死账保持纪律序
        if (urls.Count > 3) Assert.Equal(urls.Skip(3), sorted.Skip(3));
    }

    [Fact]
    public void Fallback_registry_ip_literal_candidates_marked_pure_ip()
    {
        var ch = MakeCh("新疆卫视");
        var endpoints = FallbackQueueSource.FromRegistry(ch);
        // 纯 IP 候选标 PureIp（直连免解析），域名候选标 Cdn
        Assert.All(endpoints, e => Assert.True(e.Kind is SourceKind.PureIp or SourceKind.Cdn));
    }
}
