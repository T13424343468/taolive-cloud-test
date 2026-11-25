using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaoLive.Session;

/// <summary>
/// 热更新配置文档（v99 方案书·自愈3 三层）：L1 纯数据 + L2/L3 取流适配规则，数据驱动不执行远程代码。
/// 总镜子 R1 红条收口：文档模型自 RemoteResilience（成建制死码，整文件清除）迁入本文件，
/// 与文档链校验同处——热更新车道落地时（拉取器接线预检空闲段）随契约一并启用。
/// </summary>
public sealed class RemoteConfigDoc
{
    /// <summary>文档版本号（v96）：随文档内递增，回滚至低版本拒收。</summary>
    public long Version { get; init; }

    /// <summary>L1：频道备源地址（EPG 键 → 备源 URL）。</summary>
    public Dictionary<string, string> ChannelBackupUrls { get; init; } = new();

    /// <summary>L1：取票参数覆盖（参数名 → 值）。</summary>
    public Dictionary<string, string> TicketParams { get; init; } = new();

    /// <summary>L1：备用域名（主域 → 备用域列表，DNS/接口失败时重试）。</summary>
    public Dictionary<string, string[]> BackupDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>L2/L3：取流适配规则（按数组顺序生效）。</summary>
    public List<AdaptRule> Rules { get; init; } = new();

    public static RemoteConfigDoc Empty { get; } = new();

    public static bool TryParse(string json, out RemoteConfigDoc doc)
    {
        doc = Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<RemoteConfigDoc>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed is null) return false;
            doc = parsed;
            return true;
        }
        catch { return false; }
    }
}

/// <summary>热更新取流适配规则（L2/L3 同构）：数据驱动，不执行远程代码，只做确定性变换。</summary>
public sealed class AdaptRule
{
    /// <summary>规则层：L2=取流适配规则，L3=脚本适配规则（换套路级，失败回落原生并明确提示）。</summary>
    public string Layer { get; set; } = "L2";

    /// <summary>命中哪个域（子串匹配，空=对所有 URL 生效）。</summary>
    public string MatchHost { get; set; } = "";

    /// <summary>域名替换（host 级换源）。</summary>
    public string? ReplaceHost { get; set; }

    /// <summary>追加/覆盖查询参数，形如 "k=v"。</summary>
    public string? AddQueryParam { get; set; }

    /// <summary>覆盖头，形如 "Referer=https://x/"。</summary>
    public string? Header { get; set; }
}

/// <summary>
/// 热更新文档链三闸（方案书 v81/v91/v96，R1 红条收口）：
/// v81 文档大小上限 1MB（防大文件占车道）；
/// v91 SHA-256 锚点校验（锚点随包内置或上版哈希链，校验不过不落盘不生效，回退包内基准文档，不中断预检）；
/// v96 文档版本号随文档内递增，回滚至低版本拒收。
/// 锚点滚动更新（v17）：校验通过的文档哈希自动锁定为运行时锚点，锚点只随包升级不随文档漂移。
/// </summary>
public static class DocChainGuard
{
    /// <summary>文档大小上限 1MB（v81）。</summary>
    public const int MaxDocBytes = 1024 * 1024;

    /// <summary>文档体 SHA-256 十六进制（小写，锚点比对口径）。</summary>
    public static string Sha256Hex(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>校验结论：Accepted=可落盘生效；其余=拒绝（回退缓存/包内基准）。</summary>
    public enum Verdict
    {
        Accepted = 0,
        Malformed = 1,        // 空体/解析失败
        Oversize = 2,         // 超 1MB（v81）
        VersionRollback = 3,  // 版本不高于当前（v96 回滚拒收）
        AnchorMismatch = 4,   // 锚点对不上（v91 中间人换文档）
    }

    /// <summary>
    /// 文档链全量校验：先解析（坏文档不落盘），再版本（回滚拒收），再大小，最后锚点。
    /// 全过才回填 <paramref name="doc"/>。
    /// </summary>
    public static Verdict Validate(string body, string trustedSha256, long currentVersion, out RemoteConfigDoc doc)
    {
        doc = RemoteConfigDoc.Empty;
        if (string.IsNullOrWhiteSpace(body)) return Verdict.Malformed;
        if (!RemoteConfigDoc.TryParse(body, out var parsed)) return Verdict.Malformed;
        if (parsed.Version <= currentVersion) return Verdict.VersionRollback; // v96
        if (Encoding.UTF8.GetByteCount(body) > MaxDocBytes) return Verdict.Oversize; // v81
        if (!string.Equals(Sha256Hex(body), trustedSha256, StringComparison.OrdinalIgnoreCase))
            return Verdict.AnchorMismatch; // v91
        doc = parsed;
        return Verdict.Accepted;
    }
}
