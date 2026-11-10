using System;
using Android.App;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Tv;

/// <summary>
/// A 类第6项·后台预热常驻服务（细案 §二）：前台服务（dataSync），持有进程不被冻结，
/// 预热引擎与主界面同进程共享同一份（引擎全局单例，绝不起第二份）。
/// 安装后首次开机自启不生效属 Android 3.1+ 停止状态拦截：必须手动打开过一次 App（设置页已写明）。
/// </summary>
[Service(Name = "com.taolive.tv.WarmupService", Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class WarmupService : Service
{
    public const string ChannelId = "warmup";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
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
}
