using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TaoLive.Scheduler;

using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 统一调度器用例（vc82 方案三，老板拍板单开工程）：
/// 登记/幂等、节拍轮询、档位排序、一次性触发、串行不重叠、失败不掀循环。
/// 轮询压小（10ms）、假时钟推进，不真等不抢核。
/// </summary>
public class SchedulerTests
{
    /// <summary>执行收集器：代替车道，记账执行序与并发数（串行断言用）。</summary>
    private sealed class Collector
    {
        public List<string> Executed { get; } = new();
        public int Running;
        public int MaxConcurrent;
        private readonly object _lock = new();

        public async Task ExecuteAsync(BackgroundScheduler.ScheduleTier tier, Func<CancellationToken, Task> work, CancellationToken ct)
        {
            lock (_lock) { Running++; MaxConcurrent = Math.Max(MaxConcurrent, Running); }
            try { await work(ct).ConfigureAwait(false); lock (_lock) { Executed.Add(work.ToString() ?? string.Empty); } }
            finally { lock (_lock) { Running--; } }
        }
    }

    [Fact]
    public async Task Periodic_job_runs_when_interval_elapses()
    {
        long now = 0;
        var collector = new Collector();
        var scheduler = new BackgroundScheduler(collector.ExecuteAsync, nowMs: () => Volatile.Read(ref now), pollMs: 10);
        var fired = new SemaphoreSlim(0);
        scheduler.Register(new BackgroundScheduler.Job
        {
            Name = "scout-round",
            Interval = TimeSpan.FromMilliseconds(60),
            Tier = BackgroundScheduler.ScheduleTier.Scout,
            Work = _ => { fired.Release(); return Task.CompletedTask; },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pump = scheduler.RunAsync(cts.Token);
        Assert.True(await fired.WaitAsync(TimeSpan.FromSeconds(4), cts.Token), "周期活到点必须跑");
        Volatile.Write(ref now, 1000); // 时钟推过节拍线
        Assert.True(await fired.WaitAsync(TimeSpan.FromSeconds(4), cts.Token), "节拍滑到即再轮到");
        await cts.CancelAsync();
        await pump;
    }

    [Fact]
    public async Task Higher_tier_runs_first_when_both_due()
    {
        long now = 0;
        var order = new List<string>();
        var collector = new Collector();
        var scheduler = new BackgroundScheduler(collector.ExecuteAsync, nowMs: () => Volatile.Read(ref now), pollMs: 10);
        var bothDone = new SemaphoreSlim(0);
        scheduler.Register(new BackgroundScheduler.Job
        {
            Name = "scout",
            Interval = TimeSpan.FromHours(1),
            Tier = BackgroundScheduler.ScheduleTier.Scout,
            Work = _ => { lock (order) order.Add("scout"); if (order.Count >= 2) bothDone.Release(); return Task.CompletedTask; },
        });
        scheduler.Register(new BackgroundScheduler.Job
        {
            Name = "standard",
            Interval = TimeSpan.FromHours(1),
            Tier = BackgroundScheduler.ScheduleTier.Standard,
            Work = _ => { lock (order) order.Add("standard"); if (order.Count >= 2) bothDone.Release(); return Task.CompletedTask; },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pump = scheduler.RunAsync(cts.Token);
        Assert.True(await bothDone.WaitAsync(TimeSpan.FromSeconds(4), cts.Token));
        await cts.CancelAsync();
        await pump;
        lock (order) Assert.True(order.IndexOf("standard") < order.IndexOf("scout"), $"档位排序破：{string.Join(",", order)}");
    }

    [Fact]
    public void Register_is_idempotent_by_name()
    {
        var scheduler = new BackgroundScheduler((_, _, _) => Task.CompletedTask);
        scheduler.Register(new BackgroundScheduler.Job { Name = "x", Interval = TimeSpan.FromSeconds(1), Work = _ => Task.CompletedTask });
        scheduler.Register(new BackgroundScheduler.Job { Name = "x", Interval = TimeSpan.FromSeconds(2), Work = _ => Task.CompletedTask });
        Assert.Equal(1, scheduler.RegisteredCount); // 同名只认一份：同一活不排队两次
    }

    [Fact]
    public async Task One_shot_runs_only_via_trigger_and_never_repeats()
    {
        var collector = new Collector();
        var scheduler = new BackgroundScheduler(collector.ExecuteAsync, pollMs: 10);
        var fired = new SemaphoreSlim(0);
        scheduler.Register(new BackgroundScheduler.Job
        {
            Name = "boot-warm",
            Interval = TimeSpan.FromHours(1),
            OneShot = true,
            Work = _ => { fired.Release(); return Task.CompletedTask; },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pump = scheduler.RunAsync(cts.Token);
        await Task.Delay(60, cts.Token); // 未触发：绝不自跑
        Assert.Empty(collector.Executed);
        scheduler.TriggerNow("boot-warm");
        Assert.True(await fired.WaitAsync(TimeSpan.FromSeconds(4), cts.Token));
        await Task.Delay(60, cts.Token); // 跑完退役：不再跑第二次
        Assert.Single(collector.Executed);
        scheduler.TriggerNow("boot-warm"); // 退役后触发=空操作
        await Task.Delay(60, cts.Token);
        Assert.Single(collector.Executed);
        await cts.CancelAsync();
        await pump;
    }

    [Fact]
    public async Task Works_never_overlap()
    {
        var collector = new Collector();
        var scheduler = new BackgroundScheduler(collector.ExecuteAsync, pollMs: 10);
        var fired = new SemaphoreSlim(0);
        scheduler.Register(new BackgroundScheduler.Job
        {
            Name = "busy",
            Interval = TimeSpan.FromMilliseconds(1),
            Work = async _ => { fired.Release(); await Task.Delay(30); }, // 故意慢：给重叠机会
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pump = scheduler.RunAsync(cts.Token);
        Assert.True(await fired.WaitAsync(TimeSpan.FromSeconds(4), cts.Token));
        await Task.Delay(200, cts.Token); // 多轮
        Assert.True(collector.MaxConcurrent <= 1, $"调度器必须串行：实测并发 {collector.MaxConcurrent}");
        await cts.CancelAsync();
        await pump;
    }

    [Fact]
    public async Task Throwing_work_does_not_kill_loop()
    {
        var collector = new Collector();
        var scheduler = new BackgroundScheduler(collector.ExecuteAsync, pollMs: 10);
        var fired = new SemaphoreSlim(0);
        scheduler.Register(new BackgroundScheduler.Job
        {
            Name = "bad-then-good",
            Interval = TimeSpan.FromMilliseconds(20),
            Work = _ => { fired.Release(); throw new InvalidOperationException("单路活炸了"); },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pump = scheduler.RunAsync(cts.Token);
        Assert.True(await fired.WaitAsync(TimeSpan.FromSeconds(4), cts.Token));
        Assert.True(await fired.WaitAsync(TimeSpan.FromSeconds(4), cts.Token)); // 炸了下一轮照常排班
        await cts.CancelAsync();
        await pump;
    }
}
