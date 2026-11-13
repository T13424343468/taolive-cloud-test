using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Text;
using Android.Content;
using Android.Runtime;
using TaoLive.BLL;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 崩溃拦截网（v1.0.7）：任何未捕获异常先落日志再放行，让崩溃原因可查可报，
/// 不再「打开就没」。日志写在应用私有目录，电视文件管理器能看到的公共目录也写一份。
/// v1.0.20 ⑨ 接线：每次写入前先过轮转执行器，1MB 两代封顶，绝不限时占满存储。
/// fix36：Record 改后台批量落盘——File.AppendAllText 是磁盘 IO，看门狗/内核事件/自救链路
/// 每秒可打多条，此前调用线程同步写盘（冻结审计 A6：主线程只入队即可）；现主线程只入
/// ConcurrentQueue 即返回，后台定时器每秒攒批（≤32KB/拍）一次写入，轮转口径不变。
/// 进程即死的兜底路径（UncaughtCrashPeer）走 RecordNow 同步落盘——进程来不及等批量。
/// </summary>
public static class CrashNet
{
    // fix36 后台批量队列：有界，满了丢最旧最省事（诊断日志可丢，绝不因日志积压拖死主线程/内存）
    private const int QueueCapacity = 512;
    private static readonly ConcurrentQueue<string> _pending = new();
    private static readonly System.Threading.Timer _flushTimer;
    private static int _flushing; // 单飞护栏：防上拍未落盘、下拍并发写

    static CrashNet()
    {
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>记录一条异常（异常对象版）。fix36：主线程只入队，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(Exception ex)
    {
        try
        {
            Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\n\n");
        }
        catch { }
    }

    /// <summary>记录一条异常（文本版）。fix36：主线程只入队，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(string text)
    {
        try
        {
            Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
    }

    /// <summary>fix36：同步落盘（不走批量队列）——仅供进程即死的兜底路径（未捕获异常处理器）使用：
    /// 进程马上终结，后台批量拍等不到，必须当场写入。</summary>
    public static void RecordNow(string text)
    {
        try
        {
            WriteAndRotate($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
    }

    /// <summary>fix36：入队（有界：满了丢新条，保队列稳定不膨胀）。</summary>
    static void Enqueue(string line)
    {
        if (_pending.Count >= QueueCapacity)
        {
            // 队列满载=写盘长期跟不上（存储异常/磁盘满）：诊断日志可丢，丢弃本次并保队列不再增长
            try { File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [CrashNet] fix36 批量队列满载，丢弃日志一条\n\n"); } catch { }
            return;
        }
        _pending.Enqueue(line);
    }

    /// <summary>fix36：后台批量落盘（每秒一拍，单飞护栏防并发写；≤32KB/拍防单拍过长占住 IO）。</summary>
    static void Flush()
    {
        if (_pending.IsEmpty) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return; // 上拍未完成：留到下拍
        try
        {
            var batch = new StringBuilder();
            while (_pending.TryDequeue(out var line))
            {
                batch.Append(line);
                if (batch.Length >= 32 * 1024) break; // 单拍封顶，剩余下拍继续
            }
            if (batch.Length == 0) return;
            WriteAndRotate(batch.ToString());
        }
        catch { }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>写入前先按轮转策略收敛历史日志（1MB 两代封顶），再追加本次内容。</summary>
    static void WriteAndRotate(string line)
    {
        var path = LogPath();
        LogRotationPolicy.RotateIfNeeded(path); // 失败静默返回, 不影响本次写入
        File.AppendAllText(path, line);
    }

    static string LogPath() =>
        Path.Combine(AppContext.FilesDir!.AbsolutePath!, "taolive_crash.log");

    static Context AppContext => global::Android.App.Application.Context!;
}

/// <summary>
/// 未捕获异常挂接：Java 侧任何线程（含内核渲染线程）抛出的异常，
/// 先记日志，再交给系统默认处理器照常弹错，不吞异常。
/// </summary>
public class UncaughtCrashPeer : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
{
    private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _next;

    public UncaughtCrashPeer(Java.Lang.Thread.IUncaughtExceptionHandler? next)
    {
        _next = next;
    }

    public void UncaughtException(Java.Lang.Thread thread, Java.Lang.Throwable ex) // 契约按 Java 绑定非空签名；方法内仍安全处理
    {
        if (thread is null || ex is null) return; // 崩溃兜底路径参数缺失：只保证不转发，异常照常由系统处理
        // fix36：进程即死，等不到后台批量拍——走 RecordNow 同步落盘兜底
        CrashNet.RecordNow($"未捕获异常 线程={thread.Name} {ex}");
        _next?.UncaughtException(thread, ex);
    }
}
