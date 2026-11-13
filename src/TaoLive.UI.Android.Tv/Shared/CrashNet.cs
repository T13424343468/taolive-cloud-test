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
/// S6/UR1: 日志改异步队列单写线程 + 滚动上限（512KB 截断留尾）——
/// 原同步 AppendAllText 在崩溃现场/高频打点时是 IO 阻塞点, 且文件无上限无限增长（审计 startup-S6 / ui-remote-UR1）。
/// </summary>
public static class CrashNet
{
    private const long MaxLogBytes = 512L * 1024;
    private static readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private static int _writerStarted;

    /// <summary>记录一条异常（异常对象版）。</summary>
    public static void Record(Exception ex) => Record(ex?.ToString() ?? "null");

    /// <summary>记录一条异常（文本版）：入队即返回, 写盘在后台单线程。</summary>
    public static void Record(string text)
    {
        try
        {
            EnsureWriter();
            _queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
    }

    private static string _lastText = "";
    private static long _lastAtMs = long.MinValue;

    /// <summary>S6: 供未捕获异常等同步现场使用——直接落盘, 不排队（进程可能即刻终止）。
    /// [vc48测试通道] 增加同文 5 秒去重：托管/Java 双路钩子可能对同一次崩溃各报一次。</summary>
    public static void RecordNow(string text)
    {
        var now = System.Environment.TickCount64;
        var probe = text.Length > 200 ? text[..200] : text;
        if (now - _lastAtMs < 5000 && probe == _lastText) return; // 双路钩子同崩去重
        _lastText = probe; _lastAtMs = now;
        try
        {
            TruncateIfNeeded();
            File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
        // [vc48测试通道] 崩溃现场自动回传（同步尽力而为；稳定后整撤：连本段带 CrashCloudRelay.cs 一起删）
        var relayUrl = CrashCloudRelay.UploadAndStash(text);
        if (relayUrl != null)
        {
            try { File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [回传] 崩溃日志已发云端：{relayUrl}\n\n"); }
            catch { }
        }
    }

    private static void EnsureWriter()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1) return;
        var thread = new Thread(() =>
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    TruncateIfNeeded();
                    File.AppendAllText(LogPath(), line);
                }
                catch { }
            }
        }) { IsBackground = true, Name = "crashnet-writer" };
        thread.Start();
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
                fs.Read(rest, 0, rest.Length);
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
        _next?.UncaughtException(thread, ex);
    }
}
