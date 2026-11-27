using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace TaoLive.Phone;

/// <summary>
/// 后台保活前台服务（老板令 2026-11-27 #20「切后台要保活」）：切后台时以前台服务身份持有进程，
/// 播放内核与看门狗泵照常运转，当前台继续把水位灌满；回前台即撤（OnResume 停服务）。
/// 服务本身不碰播放器——进程活着、Activity 未销毁，播放与缓冲就不会停；
/// 切回时画面原样接续（不重新起播、不重新缓冲），即「从水位接续点续播」。
/// </summary>
[Service(Name = "com.taolive.phone.KeepAliveService",
         Enabled = true,
         Exported = false,
         ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public class KeepAliveService : Service
{
    public const string ExtraChannel = "channel_name";
    private const int NotifyId = 2047;
    private const string ChannelId = "taolive_keepalive";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string title = intent?.GetStringExtra(ExtraChannel) is { Length: > 0 } ch ? ch : "直播";
        var notification = BuildNotification(title);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotifyId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotifyId, notification);
        return StartCommandResult.NotSticky; // 进程被杀即终，冷启动走既有「上次台」恢复口径
    }

    private void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var mgr = GetSystemService(NotificationService) as NotificationManager;
        if (mgr?.GetNotificationChannel(ChannelId) is not null) return;
        var ch = new NotificationChannel(ChannelId, "后台播放", NotificationImportance.Low);
        ch.Description = "切后台时保持直播不断流";
        ch.SetShowBadge(false);
        mgr?.CreateNotificationChannel(ch);
    }

    private Notification BuildNotification(string title)
    {
        EnsureChannel();
        // 点通知回应用=切回前台续播场景
        var back = new Intent(this, typeof(MainActivity));
        var pi = PendingIntent.GetActivity(this, 0, back,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder.SetSmallIcon(Resource.Drawable.ic_fg)
               .SetContentTitle("TaoLive 后台播放中")
               .SetContentText($"正在缓冲：{title}（回来自动接上）")
               .SetOngoing(true)
               .SetContentIntent(pi);
        return builder.Build()!;
    }
}
