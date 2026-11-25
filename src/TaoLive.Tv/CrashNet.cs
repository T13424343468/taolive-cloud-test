using System.Collections.Concurrent;
using System.Text;

namespace TaoLive.Tv;

/// <summary>
/// 崩溃拦截网（2.0 自建，口径照 vc53）：任何未捕获异常先落日志再放行，崩溃原因可查可报。
/// 日志写在应用私有目录 taolive_crash.log；写入前 1MB 两代轮转封顶，绝不限时占满存储。
/// 主线程只入队（有界，满了丢新条），后台定时器每秒攒批一次写入——磁盘 IO 不占主线程；
/// 进程即死路径（未捕获异常处理器）走 RecordNow 同步落盘——进程来不及等批量。
/// </summary>
public static class CrashNet
{
    // 后台批量队列：有界，满了丢新条最省事（诊断日志可丢，绝不因日志积压拖死主线程/内存）
    private const int QueueCapacity = 512;
    private static readonly ConcurrentQueue<string> _pending = new();
    private static readonly System.Threading.Timer _flushTimer;
    private static int _flushing; // 单飞护栏：防上拍未落盘、下拍并发写
    private static int _count;    // 配额计数（vc73 审查清零 P2）：Count 检查与入队非原子会略超 512，改 Interlocked 原子配额
    private static readonly object _ioGate = new(); // 写盘互斥（vc73 审查清零 P2）：RecordNow 与后台 Flush 并发走同一文件，无共享锁会一条 IOException 被吞丢崩溃账

    static CrashNet()
    {
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>挂接未捕获异常：托管域 + Java 侧线程（含内核渲染线程）双入口，记日志后照常交系统，不吞异常。</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordNow($"未捕获异常 {e.ExceptionObject}");
        var next = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
        if (next is not UncaughtCrashPeer)
            Java.Lang.Thread.DefaultUncaughtExceptionHandler = new UncaughtCrashPeer(next);
    }

    /// <summary>记录一条异常（异常对象版）。主线程只入队，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(Exception ex) => Record(ex.ToString());

    /// <summary>记录一条异常（文本版）。主线程只入队，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(string text)
    {
        try { Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n"); }
        catch { }
    }

    /// <summary>同步落盘（不走批量队列）——仅供进程即死的兜底路径（未捕获异常处理器）使用。</summary>
    public static void RecordNow(string text)
    {
        try { WriteAndRotate($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n"); }
        catch { }
    }

    /// <summary>入队（有界：满了丢新条，保队列稳定不膨胀）。
    /// vc73 审查清零 P2：配额走 Interlocked 原子计数——原「先查 Count 再入队」两步非原子，
    /// 并发下会略超 512（不致命，但口径应精确）。</summary>
    static void Enqueue(string line)
    {
        if (Interlocked.Increment(ref _count) > QueueCapacity)
        {
            Interlocked.Decrement(ref _count); // 超配额：计数回滚，丢新条
            return;
        }
        _pending.Enqueue(line);
    }

    /// <summary>后台批量落盘（每秒一拍，单飞护栏防并发写；≤32KB/拍防单拍过长占住 IO）。</summary>
    static void Flush()
    {
        if (_pending.IsEmpty) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return; // 上拍未完成：留到下拍
        try
        {
            var batch = new StringBuilder();
            while (_pending.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _count); // vc73：与 Enqueue 配额计数配对，出队即减
                batch.Append(line);
                if (batch.Length >= 32 * 1024) break; // 单拍封顶，剩余下拍继续
            }
            if (batch.Length == 0) return;
            WriteAndRotate(batch.ToString());
        }
        catch { }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>写入前按轮转策略收敛历史日志（1MB 两代封顶），再追加本次内容。
    /// vc73 审查清零 P2：轮转与追加同一把 _ioGate 互斥——RecordNow（进程即死兜底）与后台
    /// Flush 并发走到这里，无共享锁时 File.AppendAllText 会抛 IOException，旧空吞=丢一条崩溃账；
    /// 落在 _ioGate 后互斥串行，IOException 概率归零，仍兜 catch（失败落 Console.Error 不空吞、不抛）。</summary>
    static void WriteAndRotate(string line)
    {
        var path = LogPath();
        try
        {
            lock (_ioGate)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                {
                    var bak = path + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(path, bak);
                }
                File.AppendAllText(path, line);
            }
        }
        catch (Exception ex)
        {
            // 轮转/追加失败：落 Console.Error 不空吞（三.2），崩溃兜底路径仍不能抛
            try { Console.Error.WriteLine($"[CrashNet] write failed: {ex.Message}"); } catch { }
        }
    }

    static string LogPath() =>
        Path.Combine(global::Android.App.Application.Context!.FilesDir!.AbsolutePath!, "taolive_crash.log");
}

/// <summary>
/// Java 侧未捕获异常挂接：任何线程（含内核渲染线程）抛出的异常，先记日志，
/// 再交给系统默认处理器照常弹错，不吞异常。
/// </summary>
public class UncaughtCrashPeer : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
{
    private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _next;

    public UncaughtCrashPeer(Java.Lang.Thread.IUncaughtExceptionHandler? next)
    {
        _next = next;
    }

    public void UncaughtException(Java.Lang.Thread thread, Java.Lang.Throwable ex) // 契约按绑定非空签名；方法内仍安全处理
    {
        if (thread is null || ex is null) return; // 崩溃兜底路径参数缺失：只保证不转发，异常照常由系统处理
        CrashNet.RecordNow($"未捕获异常 线程={thread.Name} {ex}"); // 进程即死：同步落盘，等不到批量拍
        _next?.UncaughtException(thread, ex);
    }
}
