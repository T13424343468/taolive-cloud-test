using Android.OS;
using TaoLive.Session;

namespace TaoLive.Tv;

/// <summary>
/// 主线程调度器（2.0 W5）：会话层事件经它回 Android 主线程。
/// 一期口径：内核/网络线程不得直接摸 UI 与 ExoPlayer，统一过主线程。
/// </summary>
public sealed class AndroidMainThreadDispatcher : IMainThreadDispatcher
{
    private readonly Handler _handler;
    public AndroidMainThreadDispatcher(Handler handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Post(Action action) => _handler.Post(action);
}
