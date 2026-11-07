using System;
using System.IO;
using Android.Content;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 画面载体策略（v1.0.7 启动自检）：
/// 纹理视图在部分低配 32 位机型上有崩溃风险，用「启动—首帧」存活判定自愈：
/// - 每次以纹理载体启动前打 attempt 标记；
/// - 首帧落地或用户正常退后台即清标记（载体兼容）；
/// - 若上一次启动的标记未清（进程死在起播过程中），永久回退到已验证的表面视图，
///   用户不再经历第二次闪退。
/// </summary>
public static class CarrierPolicy
{
    const string PrefName = "carrier";

    static ISharedPreferences Prefs(Context c) =>
        c.GetSharedPreferences(PrefName, FileCreationMode.Private)!;

    /// <summary>启动时决定载体：纹理可用返回 true；上一次纹理启动没活到首帧则回退表面视图。</summary>
    public static bool ResolveStartupCarrier(Context c)
    {
        var p = Prefs(c);
        if (p.GetInt("mode", 1) != 1) return false; // 0=表面视图（已永久回退）
        if (p.GetInt("attempt", 0) == 1)
        {
            // 上一次纹理启动没活到首帧：判定不兼容，永久回退
            p.Edit()!.PutInt("mode", 0).PutInt("attempt", 0).Apply();
            try
            {
                File.AppendAllText(NoticePath(c),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  上次纹理载体启动未存活到首帧，自动回退稳定画面载体\n");
            }
            catch { }
            return false;
        }
        return true;
    }

    public static void MarkAttempt(Context c) =>
        Prefs(c).Edit()!.PutInt("attempt", 1).Apply();

    /// <summary>首帧落地（或正常退后台）= 载体兼容，清标记。</summary>
    public static void MarkOk(Context c) =>
        Prefs(c).Edit()!.PutInt("attempt", 0).PutInt("ok", 1).Apply();

    /// <summary>纹理接线当场报错：永久回退，并把原因写进日志。</summary>
    public static void DisableTexture(Context c, string reason)
    {
        Prefs(c).Edit()!.PutInt("mode", 0).PutInt("attempt", 0).Apply();
        try
        {
            File.AppendAllText(NoticePath(c),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  画面载体回退稳定模式：{reason}\n");
        }
        catch { }
    }

    static string NoticePath(Context c) =>
        Path.Combine(c.FilesDir!.AbsolutePath!, "carrier_notice.log");
}
