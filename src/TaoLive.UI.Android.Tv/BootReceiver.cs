using System;
using Android.App;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Tv;

/// <summary>
/// 开机自启（设置页开关，默认关）：BOOT_COMPLETED 后直接拉起主界面。
/// 已知限制（真机实测定）：Android 3.1+ 停止状态拦截——首次开机自启生效前需手动启动过一次；
/// 部分国产 ROM 默认拦第三方自启，TCL 雷鸟 ROM 行为待 50V8G 实测。
/// 道映不声明 HOME/桌面身份：TCL 安装校验链会拒装声明 MAIN+HOME 的第三方包。
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        if (intent?.Action != Intent.ActionBootCompleted) return;
        if (!SettingsStore.AutoStartOnBootEnabled(context)) return; // 默认关
        var launch = new Intent(context, typeof(MainActivity));
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.FromBackground);
        try { context.StartActivity(launch); }
        catch { /* ROM 拦截则静默，不影响系统 */ }
    }
}

/// <summary>设置持久化（骨架）。</summary>
public static class SettingsStore
{
    public static bool AutoStartOnBootEnabled(Context ctx) => false; // TODO: SharedPreferences
}
