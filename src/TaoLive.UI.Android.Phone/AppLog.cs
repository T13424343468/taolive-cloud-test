using System;
using System.IO;
using Android.App;
using Android.Content;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 应用内日志 (v3.18 用户定 2026-11-07): 技术性信息只进日志不上屏, 提示对用户是干扰;
    /// 云端调试走日志文件 (外部专属目录 logs/app.log), 不打扰使用。
    /// </summary>
    internal static class AppLog
    {
        private static readonly object _lock = new();
        private static string? _dir;

        public static void W(string msg)
        {
            try
            {
                lock (_lock)
                {
                    _dir ??= Path.Combine(Application.Context.GetExternalFilesDir(null)!.AbsolutePath, "logs");
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(Path.Combine(_dir, "app.log"),
                        DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\n");
                }
            }
            catch { }
        }
    }
}
