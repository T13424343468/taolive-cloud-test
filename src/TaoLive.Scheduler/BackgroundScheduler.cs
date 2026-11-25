using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.Scheduler;

/// <summary>
/// 后台统一调度器（vc82 方案三，老板拍板 2026-11-16：解决方案单开工程）：
/// 全部后台网络活只登记「干什么、多久一轮、什么档位」，什么时候发、谁先走全归调度器一个循环管。
/// 零业务依赖：时钟、执行口子（车道）全部注入——本工程纯排班逻辑，不含一行业务代码，
/// 不引用任何业务工程；业务工程也永不反向依赖它，壳层负责接线。
/// 执行纪律：调度器只管「谁先谁后」（轮次串行：一轮跑完才看下一路）；车道纪律（绝不并发）由各路活
/// 内部逐请求自过 BackgroundLane 承担（探活/预热/预取全如此）——执行口子绝不整轮再包一层车道闸：
/// 整轮占闸=播放链救助被堵死，且活内部再进车道=嵌套等自己死锁。两层职责正交，各自留保险。
/// 纯逻辑可测：时钟、轮询间隔、执行口子全注入。
/// </summary>
public sealed class BackgroundScheduler
{
    /// <summary>调度档位（与车道三档同构）：救急 > 常规 > 慢探。档位决定「谁先排到」，车道决定「绝不并发」。</summary>
    public enum ScheduleTier { Rescue = 0, Standard = 1, Scout = 2 }

    /// <summary>登记进调度器的活：名字（幂等键，同名只登记一次）、轮询节拍、档位、工作回调。</summary>
    public sealed class Job
    {
        public required string Name { get; init; }
        /// <summary>轮询节拍：距上次跑完至少隔这么久才再轮到；OneShot=true 时只跑一次。</summary>
        public required TimeSpan Interval { get; init; }
        public ScheduleTier Tier { get; init; } = ScheduleTier.Standard;
        public required Func<CancellationToken, Task> Work { get; init; }
        /// <summary>一次性活（开机备货/邻台预取）：仅 TriggerNow 触发，跑完即退役。</summary>
        public bool OneShot { get; init; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;
    private readonly Func<ScheduleTier, Func<CancellationToken, Task>, CancellationToken, Task> _executor;
    private readonly int _pollMs;

    private sealed class JobState
    {
        public required Job Job;
        /// <summary>上次跑完的时刻（单调毫秒）；OneShot 未触发时=null（等待 TriggerNow）。</summary>
        public long? LastRunMs;
        public bool Done; // OneShot 跑完置位
        /// <summary>OneShot 专用：TriggerNow 触发过才到点（防自跑）。</summary>
        public bool Triggered;
    }

    /// <summary>执行口子注入：生产由壳层接 BackgroundLane.RunAsync（档位映射同构）；测试接收集器。</summary>
    public BackgroundScheduler(
        Func<ScheduleTier, Func<CancellationToken, Task>, CancellationToken, Task> executor,
        Func<long>? nowMs = null,
        int pollMs = 500)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        _pollMs = Math.Max(1, pollMs);
    }

    /// <summary>登记（幂等）：同名不重复登记（防同一活排队两次）；运行中登记下轮生效。</summary>
    public void Register(Job job)
    {
        lock (_lock)
        {
            _jobs[job.Name] = new JobState { Job = job };
        }
    }

    /// <summary>触发一次性活立即到点（如换台触发邻台预取）；未登记/已跑完为无害空操作。</summary>
    public void TriggerNow(string name)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(name, out var st) && !st.Done)
            {
                st.LastRunMs = null; // null=到点
                st.Triggered = true;
            }
        }
    }

    /// <summary>停用某路活（退役自跑循环后的清理口）。</summary>
    public void Unregister(string name)
    {
        lock (_lock)
        {
            _jobs.Remove(name);
        }
    }

    /// <summary>当前登记的路数（诊断用）。</summary>
    public int RegisteredCount { get { lock (_lock) { return _jobs.Count; } } }

    /// <summary>
    /// 单循环：从「谁先谁后」到「排到就跑」全归这一个脑子。
    /// 每拍选出到点的活，按档位升序、同档按最久未跑优先，一次跑一个（串行；执行口子再兜一层并发闸）。
    /// 没有到点的活就睡一拍，不空转不抢核。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await PumpAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 停机：收场不报错 */ }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JobState? due = null;
            lock (_lock)
            {
                long now = _nowMs();
                foreach (var st in _jobs.Values)
                {
                    if (st.Done) continue;
                    bool isDue = st.Job.OneShot ? st.Triggered && st.LastRunMs is null
                        : st.LastRunMs is null || now - st.LastRunMs.Value >= st.Job.Interval.TotalMilliseconds;
                    if (!isDue) continue;
                    // 排序：档位优先（小者先）；同档最久未跑优先（从未跑过的当最久看）
                    long age = st.LastRunMs is { } t ? now - t : long.MaxValue;
                    long bestAge = due is { } d ? (d.LastRunMs is { } t2 ? now - t2 : long.MaxValue) : -1;
                    if (due is null
                        || st.Job.Tier < due.Job.Tier
                        || (st.Job.Tier == due.Job.Tier && age > bestAge))
                    {
                        due = st;
                    }
                }
            }
            if (due is not { } run)
            {
                await Task.Delay(_pollMs, ct).ConfigureAwait(false);
                continue;
            }
            try
            {
                await _executor(run.Job.Tier, run.Job.Work, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch { /* 单路活失败不掀调度循环：下轮照常排班 */ }
            lock (_lock)
            {
                run.LastRunMs = _nowMs();
                if (run.Job.OneShot) run.Done = true;
            }
            // 跑完让一拍：给其余路活排队机会，也守执行口子的间隔纪律
            await Task.Delay(_pollMs, ct).ConfigureAwait(false);
        }
    }
}
