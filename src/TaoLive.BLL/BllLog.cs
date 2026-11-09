using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TaoLive.BLL;

// ============ 道映 v1.0.10【八】BLL 日志通道（老板 13:46 拍板：跳过项全部完成，不留缺口）============
// 「把所有可能发生的异常都写入日志」在 BLL 层同样成立：
// - 分层不破：BLL 只认 IBllLogSink 注入点，通道本身不依赖 UI 工程；UI 侧一行 SetSink 接现有 CrashNet。
// - 后台低优先级异步：入队即返回，专用 Lowest 优先级线程逐行送出，绝不阻塞播放链路。
// - 通道自身兜底：sink 抛异常不外传、队列满丢最旧、未接通道时格式化后即弃——只加日志，不改任何业务行为。

/// <summary>BLL 日志接收口：实现方自行兜底，Write 内部不得抛出。</summary>
public interface IBllLogSink
{
    void Write(string line);
}

/// <summary>委托式接收口（UI 接 CrashNet 用：BllLog.SetSink(new DelegateBllLogSink(CrashNet.Record))）。</summary>
public sealed class DelegateBllLogSink : IBllLogSink
{
    private readonly Action<string> _write;
    public DelegateBllLogSink(Action<string> write) => _write = write ?? throw new ArgumentNullException(nameof(write));
    public void Write(string line) => _write(line);
}

/// <summary>
/// BLL 全量异常日志通道（v1.0.10【八】）：
/// - Warn/Error：异常落日志（模块名、关键参数已由调用方拼好、地址一律 MaskUrl 脱敏、异常类型与消息）；
/// - Info：正常但值得留痕的事件（如预检循环取消）；
/// - MaskUrl：脱敏口径与 UI 侧一致——只留协议+主机，路径与查询串不落日志。
/// </summary>
public static class BllLog
{
    // volatile：SetSink 换通道后，后台泵线程必须读到新引用——否则缓存的旧值（null 或旧 sink）会把日志写丢
    private static volatile IBllLogSink? _sink;
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly SemaphoreSlim _signal = new(0);
    private static readonly object _sync = new(); // 后台泵与 Flush 共用：队列取出即写出，全程无半路日志
    private static int _pumpStarted;
    private const int Capacity = 512;

    /// <summary>注入日志出口（启动时一次；传 null 仅停后续写入，不清已排队）。测试可注入捕获桩。</summary>
    public static void SetSink(IBllLogSink? sink)
    {
        _sink = sink;
        if (sink != null) EnsurePump();
    }

    /// <summary>正常但值得留痕的事件。</summary>
    public static void Info(string module, string message) => Enqueue("INFO", module, message, null);

    /// <summary>异常落日志：调用方已拼好含模块与脱敏参数的消息。</summary>
    public static void Warn(string module, string message, Exception? ex = null) => Enqueue("WARN", module, message, ex);

    /// <summary>严重异常落日志（不改变业务行为，只加日志）。</summary>
    public static void Error(string module, string message, Exception? ex = null) => Enqueue("ERROR", module, message, ex);

    /// <summary>脱敏源地址：只留协议+主机；解析失败按既有口径截断，不暴露完整地址。</summary>
    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "-";
        try { var u = new Uri(url); return $"{u.Scheme}://{u.Host}"; }
        catch { return url.Length > 24 ? url[..24] + "…" : url; }
    }

    /// <summary>测试/退出兜底：同步清空队列逐行送 sink（与后台泵同一把锁、同一出口，语义一致且无竞态）。</summary>
    public static void Flush()
    {
        lock (_sync)
        {
            while (_queue.TryDequeue(out var line))
            {
                try { _sink?.Write(line); }
                catch { /* sink 异常不外传 */ }
            }
        }
    }

    private static void Enqueue(string level, string module, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{level}] [BLL:{module}]  {message}"
                   + (ex == null ? "" : $"  {ex.GetType().Name}: {ex.Message}");
        if (_sink == null) return; // 未接通道：格式化即弃，不积压不外溢
        _queue.Enqueue(line);
        if (_queue.Count > Capacity) _queue.TryDequeue(out _); // 满则丢最旧，绝不阻塞业务线程
        _signal.Release();
        EnsurePump();
    }

    private static void EnsurePump()
    {
        if (Interlocked.CompareExchange(ref _pumpStarted, 1, 0) == 1) return;
        var worker = new Thread(PumpLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest, // 后台低优先级：有播放线程在，日志永远让路
            Name = "BllLog",
        };
        worker.Start();
    }

    private static void PumpLoop()
    {
        while (true)
        {
            try { _signal.Wait(); }
            catch (ObjectDisposedException) { return; }
            catch (ThreadInterruptedException) { return; }
            lock (_sync) // 与 Flush 同一把锁：取出即写出，杜绝「队列已空但日志还在半路」的竞态
            {
                while (_queue.TryDequeue(out var line))
                {
                    try { _sink?.Write(line); }
                    catch { /* sink 异常不外传，日志通道自身绝不反噬业务 */ }
                }
            }
        }
    }
}
