using System;
using System.IO;
using Android.Content;
using Android.Runtime;
using TaoLive.BLL;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 崩溃拦截网（v1.0.7）：任何未捕获异常先落日志再放行，让崩溃原因可查可报，
/// 不再「打开就没」。日志写在应用私有目录，电视文件管理器能看到的公共目录也写一份。
/// v1.0.20 ⑨ 接线：每次写入前先过轮转执行器，1MB 两代封顶，绝不限时占满存储。
/// </summary>
public static class CrashNet
{
    /// <summary>记录一条异常（异常对象版）。</summary>
    public static void Record(Exception ex)
    {
        try
        {
            WriteAndRotate($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\n\n");
        }
        catch { }
    }

    /// <summary>记录一条异常（文本版）。</summary>
    public static void Record(string text)
    {
        try
        {
            WriteAndRotate($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n");
        }
        catch { }
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
        CrashNet.Record($"未捕获异常 线程={thread.Name} {ex}");
        _next?.UncaughtException(thread, ex);
    }
}
