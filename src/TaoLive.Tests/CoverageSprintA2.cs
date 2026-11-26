using System.Text.Json;
using TaoLive.Foundation;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 覆盖冲刺 A2（vc90 分支覆盖补账）：WatchTimeLedger 灌回合并/损坏弃账/节流落盘/立落恢复。
/// 落盘目录用 TempDir 模式，节流时钟由测试注入，不真等。
/// </summary>
public class CoverageSprintA2
{
    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "taolive-cova2-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Load_without_dir_stays_memory_only()
    {
        // 目录未注入只留内存：Load/Flush 全程无害
        var ledger = new WatchTimeLedger(null);
        ledger.Load();
        ledger.Record("k", 100, 1);
        Assert.Equal(100, ledger.TotalMs("k"));
        Assert.DoesNotContain(ledger.Snapshot(), kv => kv.EpgKey == "nope");
    }

    [Fact]
    public void Load_missing_file_yields_zero_account()
    {
        var dir = TempDir();
        var ledger = new WatchTimeLedger(dir);
        ledger.Load(); // 无 watchtime.json：按零账
        Assert.Equal(0, ledger.TotalMs("cctv1"));
    }

    [Fact]
    public void Load_corrupt_file_treated_as_zero()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "watchtime.json"), "{这不是JSON");
        var ledger = new WatchTimeLedger(dir);
        ledger.Load(); // 损坏即弃：画像不承担正确性，绝不挡播放
        Assert.Equal(0, ledger.TotalMs("cctv1"));
        Assert.DoesNotContain(ledger.Snapshot(), kv => kv.EpgKey.Length > 0);
    }

    [Fact]
    public void Load_merges_disk_values_into_memory()
    {
        var dir = TempDir();
        var first = new WatchTimeLedger(dir);
        first.Record("a", 100, 1); // 落盘 a=100
        first.Record("b", 500, 4000);

        var second = new WatchTimeLedger(dir);
        second.Load(); // 整本灌回
        Assert.Equal(100, second.TotalMs("a"));
        Assert.Equal(500, second.TotalMs("b"));
    }

    [Fact]
    public void Load_keeps_larger_in_memory_value_over_disk()
    {
        // 防旧快照回退：盘上较小值不覆盖内存较大值
        var dir = TempDir();
        var first = new WatchTimeLedger(dir);
        first.Record("a", 100, 1); // 落盘 a=100

        var second = new WatchTimeLedger(dir);
        second.Record("a", 30, 2);  // 落盘 a=30（整本覆盖写）
        second.Record("a", 70, 3);  // 节流窗内被压：内存 a=100，盘上仍 30
        second.Load();              // 盘上 30 较小，不回退内存 100
        Assert.Equal(100, second.TotalMs("a"));
    }

    [Fact]
    public void Record_rejects_invalid_segments()
    {
        // 非法段（非正/空键）不入账：本层只拒脏数据
        var ledger = new WatchTimeLedger(TempDir());
        ledger.Record("", 100, 1);
        ledger.Record("k", 0, 1);
        ledger.Record("k", -5, 1);
        Assert.Equal(0, ledger.TotalMs("k"));
        Assert.DoesNotContain(ledger.Snapshot(), kv => kv.EpgKey.Length > 0);
    }

    [Fact]
    public void TotalMs_unknown_key_returns_zero()
    {
        var ledger = new WatchTimeLedger(null);
        Assert.Equal(0, ledger.TotalMs("没看过的台"));
        Assert.Equal(0, ledger.TotalMs("")); // 空键同样按未知台
    }

    [Fact]
    public void Snapshot_orders_by_total_desc()
    {
        var ledger = new WatchTimeLedger(null);
        ledger.Record("a", 100, 1);
        ledger.Record("b", 300, 4000);
        ledger.Record("c", 200, 8000);
        var snap = ledger.Snapshot();
        Assert.Equal(new[] { "b", "c", "a" }, snap.Select(kv => kv.EpgKey).ToArray());
        Assert.Equal(300, snap[0].TotalMs);
    }

    [Fact]
    public void Flush_throttles_repeated_writes_within_window()
    {
        var dir = TempDir();
        var ledger = new WatchTimeLedger(dir);
        ledger.Record("x", 10, 1_000);   // 首写：落盘 x=10
        ledger.Record("x", 10, 2_000);   // 3 秒窗内：节流压住，盘上仍 10

        var reader = new WatchTimeLedger(dir);
        reader.Load();
        Assert.Equal(10, reader.TotalMs("x")); // 节流窗内第二笔未落盘

        ledger.Record("x", 10, 4_001);   // 越过节流窗：落盘 x=30
        var reader2 = new WatchTimeLedger(dir);
        reader2.Load();
        Assert.Equal(30, reader2.TotalMs("x"));
    }

    [Fact]
    public void FlushNow_bypasses_throttle_and_keeps_throttle_alive()
    {
        // vc74 P3④回归：立落只越节流、不把 _lastFlushMs 写成长眠值——
        // FlushNow 之后的节流 Flush 仍能正常落盘
        var dir = TempDir();
        var ledger = new WatchTimeLedger(dir);
        ledger.Record("x", 10, 1_000);
        ledger.Record("x", 10, 2_000);   // 节流窗内被压
        ledger.FlushNow();               // 立落 x=20

        var reader = new WatchTimeLedger(dir);
        reader.Load();
        Assert.Equal(20, reader.TotalMs("x"));

        ledger.Record("x", 10, 2_500);   // FlushNow 后节流 Flush 不再被压死（旧版此处静默停摆）
        ledger.FlushNow();               // 再立落 x=30
        var reader2 = new WatchTimeLedger(dir);
        reader2.Load();
        Assert.Equal(30, reader2.TotalMs("x"));
    }

    [Fact]
    public void Flush_without_dirty_writes_nothing()
    {
        var dir = TempDir();
        new WatchTimeLedger(dir).Flush(1_000); // 无脏不落盘：不产生文件
        Assert.False(File.Exists(Path.Combine(dir, "watchtime.json")));
    }

    [Fact]
    public void Persisted_file_is_valid_json_dictionary()
    {
        var dir = TempDir();
        var ledger = new WatchTimeLedger(dir);
        ledger.Record("a", 100, 1);
        ledger.FlushNow();
        var body = File.ReadAllText(Path.Combine(dir, "watchtime.json"));
        var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(body);
        Assert.NotNull(parsed);
        Assert.Equal(100, parsed!["a"]);
    }
}
