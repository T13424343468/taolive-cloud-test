using System;
using Android.App;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Tv;

/// <summary>
/// 开机自启（设置页开关，v22 第 15 项：安装默认开启；老用户已保存的设置不受影响）：
/// BOOT_COMPLETED 拉起后台预热保活服务（细案 §二：只拉服务不弹界面）。
/// 已知限制（真机实测定）：Android 3.1+ 停止状态拦截——首次开机自启生效前需手动启动过一次；
/// 部分国产 ROM 默认拦第三方自启，TCL 雷鸟 ROM 行为待 50V8G 实测（设置页自启行长按可跳系统自启管理放行）。
/// 道映不声明 HOME/桌面身份：TCL 安装校验链会拒装声明 MAIN+HOME 的第三方包。
/// v22 第 3 项：类名与接收器名对齐为 WarmupBootReceiver（原 BootReceiver 名实不符，清单残留死条目）。
/// </summary>
[BroadcastReceiver(Name = "com.taolive.tv.WarmupBootReceiver", Enabled = true, Exported = true, DirectBootAware = false,
    Permission = "android.permission.RECEIVE_BOOT_COMPLETED")]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public class WarmupBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        if (intent?.Action != Intent.ActionBootCompleted) return;
        if (!SettingsStore.AutoStartOnBootEnabled(context)) return; // 设置页可关（v22：新装默认开）
        try
        {
            var svc = new Intent(context, typeof(WarmupService)); // A⑥：只拉保活服务，预热引擎同进程共享
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(svc);
            else context.StartService(svc);
        }
        catch { /* ROM 拦截则静默，退化为「打开 App 时才预热」，功能不残废 */ }
    }
}

/// <summary>设置持久化（A⑥接真值：SharedPreferences 与主界面同一份 taolive_prefs；v22 第 15 项：新装默认开）。</summary>
public static class SettingsStore
{
    public static bool AutoStartOnBootEnabled(Context ctx) =>
        ctx.GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!
           .GetBoolean("autostart_enabled", true);
}
