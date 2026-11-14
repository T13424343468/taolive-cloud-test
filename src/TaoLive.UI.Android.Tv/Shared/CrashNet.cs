using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Android.Content;
using Android.Runtime;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 崩溃拦截网（v1.0.7）：任何未捕获异常先落日志再放行，让崩溃原因可查可报，
/// 不再「打开就没」。日志写在应用私有目录，电视文件管理器能看到的公共目录也写一份。
/// S6/UR1: 日志改异步队列 + 滚动上限（512KB 截断留尾）——
/// 原同步 AppendAllText 在崩溃现场/高频打点时是 IO 阻塞点, 且文件无上限无限增长（审计 startup-S6 / ui-remote-UR1）。
/// fix36（移植 2026-11-13 修复批A）：Record 改后台批量落盘（每秒一拍≤32KB），主线程零 IO。
/// </summary>
public static class CrashNet
{
    private const long MaxLogBytes = 512L * 1024;
    // fix36（移植 2026-11-13 修复批A）：后台批量落盘——写盘线程有界队列（满载丢弃落账，诊断日志可丢），
    // 每秒一拍 ≤32KB 防单拍过长占住 IO；原单写线程口径保留 RecordNow 给进程即死路径。
    private const int QueueCapacity = 512;
    private static readonly ConcurrentQueue<string> _pending = new();
    private static int _flushing; // 单飞护栏：防上拍未落盘、下拍并发写
    private static readonly System.Threading.Timer _flushTimer = new(_ => Flush(), null, 1000, 1000);

    /// <summary>记录一条异常（异常对象版）。</summary>
    public static void Record(Exception ex) => Record(ex?.ToString() ?? "null");

    /// <summary>记录一条异常（文本版）：fix36 主线程只入队即返回，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(string text)
    {
        try
        {
            Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
    }

    /// <summary>fix36：入队（有界：满了丢新条，保队列稳定不膨胀；落账一条便于诊断）。</summary>
    static void Enqueue(string line)
    {
        if (_pending.Count >= QueueCapacity)
        {
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
            var batch = new System.Text.StringBuilder();
            while (_pending.TryDequeue(out var line))
            {
                batch.Append(line);
                if (batch.Length >= 32 * 1024) break; // 单拍封顶，剩余下拍继续
            }
            if (batch.Length == 0) return;
            TruncateIfNeeded();
            File.AppendAllText(LogPath(), batch.ToString());
        }
        catch { }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>S6: 供未捕获异常等同步现场使用——直接落盘, 不排队（进程可能即刻终止）。</summary>
    public static void RecordNow(string text)
    {
        try
        {
            TruncateIfNeeded();
            File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
    }

    /// <summary>S6: 滚动上限——超 512KB 截断只留尾部, 日志文件不再无限增长。</summary>
    private static void TruncateIfNeeded()
    {
        try
        {
            var path = LogPath();
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            if (fi.Length <= MaxLogBytes) return;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Seek(-MaxLogBytes / 2, SeekOrigin.End);
                var rest = new byte[fs.Length - fs.Position];
                // 审查清零（CA2022）：FileStream.Read 不保证一次读满，循环读到位
                int read, off = 0;
                while (off < rest.Length && (read = fs.Read(rest, off, rest.Length - off)) > 0) off += read;
                fs.SetLength(0);
                fs.Write(rest, 0, rest.Length);
            }
        }
        catch { }
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

    public void UncaughtException(Java.Lang.Thread? thread, Java.Lang.Throwable? ex)
    {
        // UR1: 未捕获异常现场同步落盘（进程可能随默认处理器终止, 排队会丢）
        CrashNet.RecordNow($"未捕获异常 线程={thread?.Name} {ex}");
        if (thread != null && ex != null) _next?.UncaughtException(thread, ex); // 审查清零：接口契约参数非空，双判空消 CS8604
    }
}
