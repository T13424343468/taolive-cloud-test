using Android.App;
using Android.Content;
using TaoLive.Foundation;

namespace TaoLive.Tv;

/// <summary>
/// 开机自启（方案书收口批②：BootReceiver+开关行，安装默认开启；照 vc53 BootReceiver 口径搬运）：
/// BOOT_COMPLETED 拉起主界面进默认台（⑥）。已知限制（vc53 实测定）：
/// Android 3.1+ 停止状态拦截——首次开机自启生效前需手动启动过一次；
/// 部分国产 ROM 默认拦第三方自启，TCL 雷鸟 ROM 以真机实测为准（设置页自启行长按可跳系统自启管理放行）。
/// </summary>
[BroadcastReceiver(Name = "com.taolive.tv.BootReceiver", Enabled = true, Exported = true, DirectBootAware = false,
    Permission = "android.permission.RECEIVE_BOOT_COMPLETED")]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionBootCompleted) return;
        if (!context.GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!
            .GetBoolean("autostart_enabled", true)) return; // 设置页可关（默认开；与设置面板/主界面同一份偏好）
        try
        {
            var launch = new Intent(context, typeof(MainActivity));
            launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            launch.PutExtra("from_boot", true); // 开机链路：等网就绪→预检焐热→进默认台（MainActivity.EnterByBootAsync）
            context.StartActivity(launch);
        }
        catch (Exception ex)
        {
            // ROM 拦截则静默降级：退化为「用户打开 App 时才进默认台」，功能不残废（照 vc53 收敛口径留痕）
            CrashNet.Record($"[Boot] 开机拉起被 ROM 拦截（退化为打开 App 时才进默认台，功能不残废）：{ex.Message}");
        }
    }
}
