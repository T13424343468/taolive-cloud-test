using System;
using System.IO;
using Android.Content;
using Android.Runtime;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 崩溃拦截网（v1.0.7）：任何未捕获异常先落日志再放行，让崩溃原因可查可报，
/// 不再「打开就没」。日志写在应用私有目录，电视文件管理器能看到的公共目录也写一份。
/// </summary>
public static class CrashNet
{
    /// <summary>记录一条异常（异常对象版）。</summary>
    public static void Record(Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\n\n");
        }
        catch { }
    }

    /// <summary>记录一条异常（文本版）。</summary>
    public static void Record(string text)
    {
        try
        {
            File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
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

    // 契约非空（接口声明 Thread!/Throwable!）：按接口原样签名收窄参数，转发链路不再空引用入参
    public void UncaughtException(Java.Lang.Thread thread, Java.Lang.Throwable ex)
    {
        CrashNet.Record($"未捕获异常 线程={thread.Name} {ex}");
        _next?.UncaughtException(thread, ex);
    }
}
