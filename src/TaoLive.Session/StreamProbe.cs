using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TaoLive.Foundation;

namespace TaoLive.Session;

/// <summary>
/// 健康点真拉流探针（vc142 老板令「健康点真拉流」）：只测取票口=假绿根因——票通≠流通，
/// CCTV1 灰点、其余全绿但流可能死即实锤。真探口径=拉清单+拉一片分片：
/// - 清单（.m3u8）拉全文，解析第一片分片地址再拉一片（读首块即算通，不整片下载）；
/// - 主清单（无 EXTINF）取首个变体清单地址再探一层（封顶一层，防级联打穿）；
/// - 直连地址若本身是分片流（非清单），首拉读到字节即算通；
/// - 首个非空白字节是 '&lt;' 的非清单响应=HTML 广告壳（vc86 老板令口径），判死；
/// - 拉不动（网络错/4xx/5xx/空响应）一律判死亮红灯，不再只测取票口。
/// 纯 IO 薄壳：分片解析复用 <see cref="HlsPlaylistInspector"/>（纯逻辑已测）。
/// </summary>
public static class StreamProbe
{
    /// <summary>单次 HTTP 预算（毫秒）：探活是轻量动作，连接+读取 8 秒封顶（与预检 HttpClient 口径一致）。</summary>
    public const int TimeoutMs = 8_000;

    /// <summary>首块读取上限（字节）：判型与验通只需头部，读满即停（防大文件整拉）。</summary>
    public const int HeadBytes = 16 * 1024;

    /// <summary>纯逻辑：从清单全文取第一个分片地址（解析不出=主清单或残包，返回 null）。</summary>
    public static string? PickFirstSegment(string? playlistText, string baseUri) =>
        HlsPlaylistInspector.TryParse(playlistText, baseUri, out var segs) && segs.Count > 0
            ? segs[0].Url
            : null;

    /// <summary>纯逻辑：主清单判定——解析不出分片但存在非 # 行（变体清单地址候补）。</summary>
    public static string? PickFirstVariant(string? playlistText)
    {
        if (string.IsNullOrWhiteSpace(playlistText)) return null;
        foreach (var raw in playlistText.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            return line.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? line : null;
        }
        return null;
    }

    /// <summary>纯逻辑：HTML 广告壳判定（vc86 口径）——首个非空白字节是 '&lt;' 即壳（真源是 #EXTM3U 文本或 TS 二进制）。</summary>
    public static bool IsAdShell(byte[]? head)
    {
        if (head is null) return false;
        foreach (var b in head)
        {
            if (char.IsWhiteSpace((char)b)) continue;
            return b == (byte)'<';
        }
        return false;
    }

    /// <summary>
    /// 真拉流探活：拉 url（清单或流）→ 清单则再拉一片分片。任一环拉不动即 false（亮红灯）。
    /// userAgent 传取票口同款浏览器 UA（票与请求指纹一致，防 CDN 指纹不符误杀）。
    /// </summary>
    public static async Task<bool> PullAsync(string url, string userAgent, CancellationToken ct = default)
    {
        var head = await FetchHeadAsync(url, userAgent, ct).ConfigureAwait(false);
        if (head is null) return false;
        var text = System.Text.Encoding.UTF8.GetString(head);
        // 清单判定：URL 带 .m3u8 字样或内容以 #EXTM3U 开头（HlsPlaylistInspector 同口径）
        var isPlaylist = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
        if (!isPlaylist)
        {
            // 直连流：读到字节即通；HTML 广告壳判死
            return !IsAdShell(head);
        }
        // 媒体清单：解析第一片分片再拉；解析不出（主清单）取首个变体清单再探一层（封顶一层）
        var seg = PickFirstSegment(text, url);
        if (seg is null)
        {
            var variant = PickFirstVariant(text);
            if (variant is null) return false;
            var variantHead = await FetchHeadAsync(variant, userAgent, ct).ConfigureAwait(false);
            if (variantHead is null) return false;
            seg = PickFirstSegment(System.Text.Encoding.UTF8.GetString(variantHead), variant);
            if (seg is null) return false;
        }
        return await FetchHeadAsync(seg, userAgent, ct).ConfigureAwait(false) is not null;
    }

    /// <summary>拉头部字节（读满 HeadBytes 或流结束即停）；任何失败返回 null（判死）。
    /// vc145 口径收紧（vc144 真机实锤）：探针不再往共享闸记账——探针的 403 多为票过期/防盗链
    /// （与播放器指纹不同源），记账会把整主机锁进冷却窗、播放路陪葬（ysp CDN 全频道不出画教训）；
    /// 冷却窗内仍零网络快速失败（不添请求），成功仍清账（探通=主机活着，帮频控解除）。</summary>
    private static async Task<byte[]?> FetchHeadAsync(string url, string userAgent, CancellationToken ct)
    {
        if (SourceRateGate.IsCooling(url)) return null; // vc144：主机冷却中，探活本地快速失败不添请求
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMilliseconds(TimeoutMs),
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (status >= 400)
            {
                return null; // vc145：探针失败只判死本源，不再记共享账锁主机
            }
            var buf = new byte[HeadBytes];
            var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var total = 0;
            while (total < buf.Length)
            {
                var read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }
            if (total <= 0) return null;
            SourceRateGate.OnOk(url); // vc144：探活成功即清账，频控解除三路恢复
            return buf[..total];
        }
        catch
        {
            return null; // 网络错/超时/取消（探活口径一律判死，取消由调用方轮次兜底）
        }
    }
}
