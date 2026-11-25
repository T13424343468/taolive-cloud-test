using System.Text.Json;
using TaoLive.Catalog;
using TaoLive.Foundation;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// vc74 审查清零 P3 回归：
/// ③ TicketResolver 整表缓存并发 miss 单飞（一次网络）+ 失败即摘牌（毒任务不滞留）+ TTL 命中不重发；
/// ④ WatchTimeLedger FlushNow 后节流恢复（旧版 _lastFlushMs=long.MaxValue 长眠压死节流 Flush）+ 并发落盘账完整；
/// ⑤ SourceProfileStore 复制-发布语义不变（Constant/Rotating/IPDirect 判定）+ 并发取样与落盘 JSON 合法。
/// </summary>
public class LedgerProfileTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "taolive-p3-" + Guid.NewGuid().ToString("N"))).FullName;

    // ---- ③ TicketResolver：FetchTableCachedAsync（internal，Tests 直测） ----

    [Fact]
    public async Task Table_fetch_concurrent_miss_shares_single_flight()
    {
        var resolver = new TicketResolver();
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Fetch()
        {
            Interlocked.Increment(ref calls);
            return gate.Task.ContinueWith(_ => (string?)"{\"rows\":[]}");
        }

        // FetchTableCachedAsync 登记飞在 await 前同步段完成：两次调用必同飞
        var t1 = resolver.FetchTableCachedAsync("src-single", Fetch);
        var t2 = resolver.FetchTableCachedAsync("src-single", Fetch);
        Assert.Equal(1, Volatile.Read(ref calls)); // 旧版各发各网（双发多耗）
        gate.TrySetResult();
        Assert.Equal("{\"rows\":[]}", await t1);
        Assert.Equal("{\"rows\":[]}", await t2);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Table_fetch_failure_does_not_poison_cache_or_flight()
    {
        var resolver = new TicketResolver();
        var calls = 0;
        Task<string?> Fetch()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null); // 拉网失败
        }

        Assert.Null(await resolver.FetchTableCachedAsync("src-fail", Fetch));
        Assert.Null(await resolver.FetchTableCachedAsync("src-fail", Fetch));
        Assert.Equal(2, Volatile.Read(ref calls)); // 失败不缓存且飞牌必摘：下次照发，毒任务不滞留
    }

    [Fact]
    public async Task Table_fetch_hits_ttl_cache_without_refetch()
    {
        var resolver = new TicketResolver();
        var calls = 0;
        Task<string?> Fetch()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("{\"v\":1}");
        }

        Assert.Equal("{\"v\":1}", await resolver.FetchTableCachedAsync("src-ttl", Fetch));
        Assert.Equal("{\"v\":1}", await resolver.FetchTableCachedAsync("src-ttl", Fetch));
        Assert.Equal(1, Volatile.Read(ref calls)); // TTL 内同源只发 1 次（原口径不变）
    }

    [Fact]
    public void Table_ttl_follows_list_cache_knob()
    {
        // 清单缓存旋钮接通（老板 2026-11-17 拍板，假旋钮接通第 3 位）：整表 TTL 读「清单缓存」旋钮，
        // 出厂 60 秒=原常量口径；上限 10 分钟即蓝图 v2.5「清单缓存上限 10min」钳制。
        try
        {
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                60, AdvancedTuning.DefaultRetryBaseMs, AdvancedTuning.DefaultRetryMaxMs,
                AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.DefaultLiveLagSec);
            Assert.Equal(TimeSpan.FromSeconds(60), TicketResolver.TableTtl); // 出厂=原常量口径
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                300, AdvancedTuning.DefaultRetryBaseMs, AdvancedTuning.DefaultRetryMaxMs,
                AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.DefaultLiveLagSec);
            Assert.Equal(TimeSpan.FromMinutes(5), TicketResolver.TableTtl); // 改档即时生效
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                600, AdvancedTuning.DefaultRetryBaseMs, AdvancedTuning.DefaultRetryMaxMs,
                AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.DefaultLiveLagSec);
            Assert.Equal(TimeSpan.FromMinutes(10), TicketResolver.TableTtl); // 上限=10min 钳制
            AdvancedTuning.Apply(AdvancedTuning.DefaultFailGate, AdvancedTuning.DefaultReprobeMs,
                2, AdvancedTuning.DefaultRetryBaseMs, AdvancedTuning.DefaultRetryMaxMs,
                AdvancedTuning.DefaultPrefetchWindowSec, AdvancedTuning.DefaultLiveLagSec);
            Assert.Equal(TimeSpan.FromSeconds(5), TicketResolver.TableTtl); // 下限 5 秒钳制
        }
        finally
        {
            AdvancedTuning.ResetToDefaults();
        }
    }

    // ---- ④ WatchTimeLedger ----

    private static Dictionary<string, long> ReadLedger(string dir) =>
        JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Path.Combine(dir, "watchtime.json")))
            ?? new Dictionary<string, long>();

    [Fact]
    public void Ledger_flushnow_does_not_kill_throttled_flush_afterwards()
    {
        var dir = TempDir();
        try
        {
            var ledger = new WatchTimeLedger(dir);
            ledger.Record("ch1", 1000, 1000);
            Assert.Equal(1000, ReadLedger(dir)["ch1"]);
            ledger.FlushNow(); // 旧版：_lastFlushMs=long.MaxValue → 此后所有 Flush 长眠压死
            ledger.Record("ch1", 500, 12000);
            ledger.Flush(12000); // 旧版盘上停 1000，账滞留内存；新版 last=1000，12000-1000≥3000 → 落
            Assert.Equal(1500, ReadLedger(dir)["ch1"]);
            ledger.Record("ch1", 100, 12500);
            ledger.FlushNow(); // 立落第二次起失效的旧病根
            Assert.Equal(1600, ReadLedger(dir)["ch1"]);
            Assert.Equal(1600, ledger.TotalMs("ch1"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ledger_concurrent_records_and_flushes_keep_totals_complete()
    {
        var dir = TempDir();
        try
        {
            var ledger = new WatchTimeLedger(dir);
            const int threads = 8, perThread = 50;
            var start = new Barrier(threads);
            var flusher = Task.Run(() => { for (var i = 0; i < 20; i++) ledger.FlushNow(); }); // 并发立落压 tmp 碰撞
            var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < perThread; i++)
                {
                    ledger.Record("ch-x", 10, 1_000_000 + i * 4000 + t); // 错开节流窗
                    if (i % 10 == 9) ledger.FlushNow();
                }
            })).ToList();
            tasks.Add(flusher);
            await Task.WhenAll(tasks);
            ledger.FlushNow(); // 收尾保证全部入账落盘
            var expected = threads * perThread * 10L;
            Assert.Equal(expected, ledger.TotalMs("ch-x"));
            Assert.Equal(expected, ReadLedger(dir)["ch-x"]); // 旧版并发写 tmp 互吞可丢账
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- ⑤ SourceProfileStore（Shared 单例；各测用唯一源号 + 自持临时目录，类内串行安全） ----

    [Fact]
    public void Profile_copy_publish_keeps_register_and_observe_semantics()
    {
        var dir = TempDir();
        try
        {
            var store = SourceProfileStore.Shared;
            store.Load(dir);

            // Register：形状初判 IPDirect；重复 Register 不覆盖已定档
            store.Register("p3-reg", "http://10.0.0.9/x.m3u8", 1);
            Assert.Equal(SourceArchiveKind.IPDirect, store.KindOf("p3-reg"));
            store.Register("p3-reg", "https://cdn.example.com/x.m3u8?sign=abc", 2);
            Assert.Equal(SourceArchiveKind.IPDirect, store.KindOf("p3-reg"));

            // 未登记源两次同指纹（参数值不参与）→ Constant；两次异指纹（路径变）→ Rotating
            store.ObserveSample("p3-same", "http://live.example.com/a.m3u8?k=1", 1);
            store.ObserveSample("p3-same", "http://live.example.com/a.m3u8?k=2", 2);
            Assert.Equal(SourceArchiveKind.Constant, store.KindOf("p3-same"));
            Assert.False(store.IsRotating("p3-same"));

            store.ObserveSample("p3-rot", "http://live.example.com/a.m3u8", 1);
            store.ObserveSample("p3-rot", "http://live.example.com/b.m3u8", 2);
            Assert.Equal(SourceArchiveKind.Unknown, store.KindOf("p3-rot")); // Kind 不被学习覆盖
            Assert.True(store.IsRotating("p3-rot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Profile_concurrent_samples_and_flushes_produce_valid_json()
    {
        var dir = TempDir();
        try
        {
            var store = SourceProfileStore.Shared;
            store.Load(dir);
            var workers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                {
                    store.ObserveSample("p3-conc", $"http://10.0.0.1/live.m3u8?sign={i}", 100 + i); // 同形状（指纹恒定）并发取样
                    if (i % 25 == 24) store.FlushNow();
                }
            })).ToList();
            var flusher = Task.Run(() => { for (var i = 0; i < 20; i++) store.FlushNow(); }); // 并发落盘压 tmp 碰撞
            workers.Add(flusher);
            await Task.WhenAll(workers);
            store.FlushNow();

            var json = File.ReadAllText(Path.Combine(dir, "source_profiles.json"));
            var profiles = JsonSerializer.Deserialize<SourceProfile[]>(json);
            Assert.NotNull(profiles);
            var conc = profiles!.First(p => p.SourceId == "p3-conc");
            Assert.Equal(SourceArchiveKind.Constant.ToString(), conc.Kind); // 同指纹并发取样→恒定（复制-发布不坏语义）
            Assert.False(conc.Rotating);
            Assert.InRange(conc.SampledUtcMs, 100, 199); // 最后一次取样时间在账（丢失更新封死）
            Assert.Equal(SourceProfileStore.UrlFingerprint("http://10.0.0.1/live.m3u8?sign=0"),
                conc.LastUrlHash); // 同形状 URL 指纹一致（参数值不参与）
        }
        finally { Directory.Delete(dir, true); }
    }
}
