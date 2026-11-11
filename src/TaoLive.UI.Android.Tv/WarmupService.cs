using System;
using Android.App;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Tv;

/// <summary>
/// A 类第6项·后台预热常驻服务（细案 §二）：前台服务（dataSync），持有进程不被冻结，
/// 预热引擎与主界面同进程共享同一份（引擎全局单例，绝不起第二份）。
/// 15:01 老板拍板：退出界面后服务继续持有进程（退出=退界面不退进程）。
/// 安装后首次开机自启不生效属 Android 3.1+ 停止状态拦截：必须手动打开过一次 App（设置页已写明）。
/// </summary>
[Service(Name = "com.taolive.tv.WarmupService", Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class WarmupService : Service
{
    public const string ChannelId = "warmup";
    private const int RestartAlarmCode = 1002;
    private static PendingIntent? _pendingRestart; // 服务活着时销掉兜底闹钟，避免重复拉起

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CancelRestartAlarm();
        var prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        if (!prefs.GetBoolean("warmup_enabled", true))
        {
            StopSelf(); // 用户关掉后台预热：服务空转退出（细案 §六：关掉时引擎等待，不探测）
            return StartCommandResult.NotSticky;
        }
        try
        {
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(ChannelId, "后台预热", NotificationImportance.Min);
                ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
                var note = new Notification.Builder(this, ChannelId)
                    .SetContentText("直播源保活中，换台更快")
                    .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                    .Build();
                StartForeground(1001, note);
            }
            // API 26 以下：静默保活（TV 低版本场景，startForeground 需通知这里不强制）
        }
        catch { /* 通知权限被拒：服务照跑只是无通知（细案 §二口径） */ }
        return StartCommandResult.Sticky; // 被杀自动重建（TCL 低配冻结属设计内行为：落盘层兜底）
    }

    /// <summary>
    /// 保活第二道（15:01 老板令：必须要有保活机制）：最近任务被一键清/ROM 清后台时，
    /// 闹钟 5 秒后把前台服务拉回来；Sticky 重建之外再加一层，双保险。
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        try
        {
            var am = (AlarmManager)GetSystemService(AlarmService)!;
            am.SetAndAllowWhileIdle(AlarmType.RtcWakeup,
                SystemClock.CurrentTimeMillis() + 5000, BuildRestartPendingIntent());
        }
        catch { /* ROM 拦截则靠 Sticky 重建兜底 */ }
        base.OnTaskRemoved(rootIntent);
    }

    private PendingIntent BuildRestartPendingIntent()
    {
        var flags = PendingIntentFlags.UpdateCurrent;
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S)
            flags |= PendingIntentFlags.Immutable;
        return PendingIntent.GetForegroundService(this, RestartAlarmCode, new Intent(this, typeof(WarmupService)), flags);
    }

    private void CancelRestartAlarm()
    {
        try
        {
            if (_pendingRestart == null) return;
            ((AlarmManager)GetSystemService(AlarmService)!).Cancel(_pendingRestart);
            _pendingRestart = null;
        }
        catch { /* 闹钟本就没挂：静默 */ }
    }
}
