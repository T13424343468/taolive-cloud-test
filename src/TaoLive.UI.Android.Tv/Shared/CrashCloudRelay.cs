using System;
using System.Text;
using Android.Content;
using Android.OS;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// vc48 测试通道（老板 2026-11-13 17:3x 令「不要任何中间操作，直接从设备写到你能拿到的云端」）：
/// 崩溃日志直写固定中转站（ntfy 主题，地址随包内置、零凭据、老板零操作），沙箱侧直接轮询取回；
/// 超长或直写失败时退回匿名贴文（dpaste/paste.rs）取 URL，URL 也一并写进中转站，双侧不漏。
/// 撤除口径：本文件整删 + CrashNet / MainActivity 两处 [vc48测试通道] 挂钩行删除即可，零残留。
/// 内容仅含崩溃栈与机型概况（系统版本/架构/内存），无任何用户标识。
/// </summary>
public static class CrashCloudRelay
{
    /// <summary>通道开关：稳定后改 false 即断流；整撤时连文件带挂钩一起删。</summary>
    public const bool Enabled = true;

    /// <summary>中转站主题（含随机盐，仅随本测试包存在；整撤即失效）。沙箱侧 GET /{topic}/json?poll=1 直取。</summary>
    private const string RelayTopic = "taolive-crash-k7q2m9x4vc48";

    private const int TimeoutSec = 4;
    /// <summary>ntfy 免费档单条消息约 4KB，直写只走 3.6KB 以内的负载，超长走贴文 URL 兜底。</summary>
    private const int NtfyLimitBytes = 3_600;
    private const int MinIntervalMs = 60_000; // 节流：崩溃风暴每分钟最多回传一条
    private const int PayloadLimitBytes = 48 * 1024;

    private static long _lastTryMs = -MinIntervalMs;
    private static readonly System.Net.Http.HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSec),
    };

    /// <summary>
    /// 回传崩溃日志（同步、尽力而为、绝不抛）：成功返回贴文 URL 或中转站标识（并落偏好供下次启动亮出），失败返回 null。
    /// 三端点兜底：ntfy 直写（≤3.6KB 全文直落中转站）→ dpaste.com → paste.rs；贴文 URL 会同步写进中转站。
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
            var bytes = Encoding.UTF8.GetByteCount(payload);
            string? result;
            if (bytes <= NtfyLimitBytes)
            {
                result = PostNtfy(payload, title: "crash-full");
                if (result is not null) return result; // 全文已直落中转站，沙箱侧可取
                result = PostDpaste(payload) ?? PostPasteRs(payload);
            }
            else
            {
                result = PostDpaste(payload) ?? PostPasteRs(payload);
                if (result is not null)
                    PostNtfy($"[超长崩溃] 贴文URL={result}", title: "crash-url"); // URL 同步进中转站
            }
            if (result is null) return null;

            try
            {
                var ctx = Application.Context!;
                ctx.GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!
                   .Edit()!.PutString("last_relay_url", result)!.PutBoolean("last_relay_new", true)!.Apply();
            }
            catch { } // 偏好落盘失败不阻断：URL 已在返回值里，调用方自行处置
            return result;
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

    /// <summary>ntfy 中转站直写：原始体 POST 到固定主题，2xx 即成；沙箱侧 GET /{topic}/json?poll=1 直取，零中间操作。</summary>
    private static string? PostNtfy(string content, string title)
    {
        try
        {
            using var body = new System.Net.Http.StringContent(content, Encoding.UTF8, "text/plain");
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"https://ntfy.sh/{RelayTopic}");
            req.Content = body;
            req.Headers.TryAddWithoutValidation("Title", title);
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode ? $"ntfy:{title}" : null;
        }
        catch { return null; }
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
