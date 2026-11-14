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
        // vc53 云测金证修复（round19 全程 logcat 定谳 ForegroundServiceDidNotStartInTimeException）：
        // startForegroundService 拉起后 5 秒窗内必须转前台，本方法任何早退/异常路径都不许绕过。
        // 故 StartForeground 无条件最先执行（通道构建失败也不阻断，降级裸通知再试）；
        // 关开关分支改为「先转前台再 StopSelf」（先转前台再退合法，封死超时窗口）。
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) /* CA1416 场景守卫：行为不变 */
            {
                var channel = new NotificationChannel(ChannelId, "后台预热", NotificationImportance.Min);
                ((NotificationManager?)GetSystemService(NotificationService))?.CreateNotificationChannel(channel);
                var note = new Notification.Builder(this, ChannelId)
                    .SetContentText("直播源保活中，换台更快")
                    .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                    .Build();
                StartForeground(1001, note);
            }
            // API 26 以下：静默保活（TV 低版本场景，startForeground 需通知这里不强制）
        }
        catch
        {
            // 通知渠道/权限异常不得吞掉 startForeground 义务：降级独立渠道再试一次（round19 修复令 A）
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    const string FallbackChannelId = "warmup_fb";
                    ((NotificationManager?)GetSystemService(NotificationService))?.CreateNotificationChannel(
                        new NotificationChannel(FallbackChannelId, "后台预热", NotificationImportance.Min));
                    var bare = new Notification.Builder(this, FallbackChannelId)
                        .SetContentText("直播源保活中，换台更快")
                        .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                        .Build();
                    StartForeground(1001, bare);
                }
            }
            catch (Exception fgEx) { Shared.CrashNet.Record($"[Warmup] 转前台失败（可能被系统降级为普通服务）：{fgEx.Message}"); }
        }
        var prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        if (!prefs.GetBoolean("warmup_enabled", true))
        {
            StopSelf(); // 用户关掉后台预热：已转前台再退（细案 §六：关掉时引擎等待，不探测）
            return StartCommandResult.NotSticky;
        }
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
                Java.Lang.JavaSystem.CurrentTimeMillis() + 5000, BuildRestartPendingIntent());
        }
        catch { /* ROM 拦截则靠 Sticky 重建兜底 */ }
        base.OnTaskRemoved(rootIntent);
    }

    private PendingIntent BuildRestartPendingIntent()
    {
        var flags = PendingIntentFlags.UpdateCurrent;
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S)
            flags |= PendingIntentFlags.Immutable;
        // 审查清零：API<26 无 GetForegroundService（CA1416 分支），退 StartService 装 PendingIntent（TV 低版本场景注释在 OnStartCommand）
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) /* CA1416 场景守卫：行为不变 */
        {
            var pi = PendingIntent.GetService(this, RestartAlarmCode, new Intent(this, typeof(WarmupService)), flags);
            return pi ?? throw new InvalidOperationException("PendingIntent 创建失败（系统级异常，不该发生）");
        }
        var pif = PendingIntent.GetForegroundService(this, RestartAlarmCode, new Intent(this, typeof(WarmupService)), flags);
        return pif ?? throw new InvalidOperationException("PendingIntent 创建失败（系统级异常，不该发生）");
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
