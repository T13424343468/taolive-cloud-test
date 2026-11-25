namespace TaoLive.Session;

/// <summary>
/// 主线程调度抽象（研发路黄灯采纳）：壳层注册安卓实现（Handler.post），
/// Tests 注入同步实现，防共享 VM 测试退化成异步竞态。
/// </summary>
public interface IMainThreadDispatcher
{
    void Post(Action work);
}

/// <summary>测试用同步实现：立即在调用线程执行。</summary>
public sealed class ImmediateMainThreadDispatcher : IMainThreadDispatcher
{
    public void Post(Action work) => work();
}
