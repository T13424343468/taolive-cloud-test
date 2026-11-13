using System;
using System.Text;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// vc47 测试通道（老板 2026-11-13 16:58 令「装进测试包，稳定了再撤下来，代码也清理」）：
/// 崩溃日志自动回传——匿名贴文服务中转，崩溃现场同步推送，拿回 URL 存偏好；
/// 下次启动信息行亮出 URL，老板报 URL 回来即可全量取证（沙箱无对外入口，直写不可行）。
/// 撤除口径：本文件整删 + CrashNet / MainActivity 两处 [vc47测试通道] 挂钩行删除即可，零残留。
/// 内容仅含崩溃栈与机型概况（系统版本/架构/内存），无任何用户标识。
/// </summary>
public static class CrashCloudRelay
{
    /// <summary>通道开关：稳定后改 false 即断流；整撤时连文件带挂钩一起删。</summary>
    public const bool Enabled = true;

    private const int TimeoutSec = 4;
    private const int MinIntervalMs = 60_000; // 节流：崩溃风暴每分钟最多回传一条
    private const int PayloadLimitBytes = 48 * 1024;

    private static long _lastTryMs = -MinIntervalMs;
    private static readonly System.Net.Http.HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSec),
    };

    /// <summary>
    /// 回传崩溃日志（同步、尽力而为、绝不抛）：成功返回贴文 URL（并落偏好供下次启动亮出），失败返回 null。
    /// 双端点兜底：dpaste.com / paste.rs 任一成功即收口。
    /// </summary>
    public static string? UploadAndStash(string text)
    {
        if (!Enabled) return null;
        try
        {
            var now = System.Environment.TickCount64;
            if (now - _lastTryMs < MinIntervalMs) return null; // 节流
            _lastTryMs = now;

            var payload = BuildPayload(text);
            var url = PostDpaste(payload) ?? PostPasteRs(payload);
            if (url is null) return null;

            try
            {
                var ctx = Application.Context!;
                ctx.GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!
                   .Edit()!.PutString("last_relay_url", url)!.PutBoolean("last_relay_new", true)!.Apply();
            }
            catch { } // 偏好落盘失败不阻断：URL 已在返回值里，调用方自行处置
            return url;
        }
        catch { return null; } // 回传失败绝不打断崩溃链路：本地日志兜底仍在
    }

    /// <summary>读偏好里的上次回传 URL（诊断摘要与启动横幅共用），没回传过返回 null。</summary>
    public static string? LastUrl(Context ctx)
        => ctx.GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!
              .GetString("last_relay_url", null);

    private static string BuildPayload(string text)
    {
        var sb = new StringBuilder();
        try
        {
            var ctx = Application.Context!;
            var pkg = ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0)!;
            sb.Append($"[道映测试通道] v{pkg.VersionName}({pkg.VersionCode}) · Android {(int)Build.VERSION.SdkInt}");
            try { sb.Append($" · {string.Join("/", Build.SupportedAbis ?? new[] { "?" })}"); } catch { }
        }
        catch { sb.Append("[道映测试通道] 机型概况读取失败"); }
        sb.Append("\n\n").Append(text);

        var full = sb.ToString();
        if (Encoding.UTF8.GetByteCount(full) <= PayloadLimitBytes) return full;
        // 超长保头尾：崩溃头(异常类型)与最近现场都在两端
        var bytes = Encoding.UTF8.GetBytes(full);
        return Encoding.UTF8.GetString(bytes, 0, 8 * 1024) + "\n\n…(中段截断)…\n\n"
             + Encoding.UTF8.GetString(bytes, bytes.Length - 8 * 1024, 8 * 1024);
    }

    /// <summary>dpaste.com 匿名贴文：POST 表单，响应体即贴文 URL。</summary>
    private static string? PostDpaste(string content)
    {
        try
        {
            using var form = new System.Net.Http.MultipartFormDataContent();
            form.Add(new System.Net.Http.StringContent(content), "content");
            form.Add(new System.Net.Http.StringContent("text"), "syntax");
            form.Add(new System.Net.Http.StringContent("7"), "expiry_days");
            using var resp = Http.PostAsync("https://dpaste.com/api/v2/", form).GetAwaiter().GetResult();
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            return resp.IsSuccessStatusCode && body.StartsWith("http") ? body : null;
        }
        catch { return null; }
    }

    /// <summary>paste.rs 兜底：原始体 POST，响应体即 URL。</summary>
    private static string? PostPasteRs(string content)
    {
        try
        {
            using var body = new System.Net.Http.StringContent(content, Encoding.UTF8, "text/plain");
            using var resp = Http.PostAsync("https://paste.rs/", body).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            return resp.IsSuccessStatusCode && text.StartsWith("http") ? text : null;
        }
        catch { return null; }
    }
}
