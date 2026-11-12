using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.Content;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// S3 应用内自诊断日志（老板 2026-11-12 拍板「带上」）：
/// 出问题时电视上直接看到 版本/时钟/网络/启动与取流失败/健康账 五类摘要，不再靠拍照归因。
/// 全部为现成数据换装展示：不新增采集、不外传用户数据、不碰播放链路（方案：S3自诊断日志-最小方案.md）。
/// 数据供给方是 MainActivity（经其公开成员），本类只组装与落盘导出。
/// </summary>
public static class DiagLog
{
    /// <summary>五类摘要（纯文本、大字号只读展示用）。行内换行安全，不嵌设备标识。</summary>
    public static string BuildSnapshot(
        string versionLine, string clockLine, string networkLine,
        string startupLine, string healthLine, IEnumerable<string> recentFailures)
    {
        var failures = recentFailures?.Take(10).ToList() ?? new List<string>();
        var failBlock = failures.Count == 0 ? "（最近无取流失败记录）" : string.Join("\n", failures);
        return $"【版本】{versionLine}\n" +
               $"【时钟】{clockLine}\n" +
               $"【网络】{networkLine}\n" +
               $"【启动/取流】{startupLine}\n" +
               $"【健康账】{healthLine}\n" +
               $"【最近取流失败】\n{failBlock}";
    }

    /// <summary>
    /// 导出：完整诊断文件复制到应用外部文件目录（电视文件管理器可直接读，无需权限；不进公共媒体库，防误触用户文件）。
    /// 文件名带分钟级时间戳，多次导出互不覆盖。返回实际路径，失败返回 null（调用方提示）。
    /// </summary>
    public static string? Export(Context ctx, string snapshot)
    {
        try
        {
            var dir = ctx.GetExternalFilesDir(null);
            if (dir is null) return null;
            Directory.CreateDirectory(dir.AbsolutePath);
            var path = Path.Combine(dir.AbsolutePath, $"taolive_diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var log = Path.Combine(ctx.FilesDir!.AbsolutePath!, "taolive_crash.log");
            var tail = File.Exists(log)
                ? "\n──── 完整日志（截尾 128KB 内）────\n" + ReadTail(log, 128 * 1024)
                : "\n（暂无运行日志）";
            File.WriteAllText(path, snapshot + "\n" + tail);
            return path;
        }
        catch { return null; }
    }

    /// <summary>读日志尾部（诊断窗/导出共用），避免整文件读入撑内存。</summary>
    public static string ReadTail(string path, int maxBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, fs.Length - maxBytes), SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    /// <summary>从日志尾部统计最近取流失败分类（[ConnectFailure][kind] 现成打点），失败账本现成数据的视图。</summary>
    public static List<string> RecentFailuresFromLog(string logTail, int take)
    {
        var rows = new List<string>();
        try
        {
            foreach (var line in logTail.Split('\n'))
            {
                var i = line.IndexOf("[ConnectFailure]", StringComparison.Ordinal);
                if (i < 0) continue;
                rows.Add(line[(i + "[ConnectFailure]".Length)..].Trim());
            }
        }
        catch { }
        return rows.TakeLast(take).ToList();
    }
}
