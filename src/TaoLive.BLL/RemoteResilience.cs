using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TaoLive.BLL;

// ============ 道映 v1.0.8 第19项·全面自愈化（自愈2/3/7/8 项的纯逻辑核心）============
// 自愈3·热更新三层：L1 纯数据层（频道备源/取票参数/备用域名）+ L2 取流适配层（数据驱动规则）
// + L3 脚本适配层（同为规则列表、按序执行、失败自动回落原生并明确提示）。
// 配置经 GitHub 免费 raw 托管定期拉取（$0），拉取失败用本地缓存继续跑；整体换代仍需出新版本。

/// <summary>热更新取流适配规则（L2/L3 同构）：数据驱动，不执行远程代码，只做确定性变换。</summary>
public sealed class AdaptRule
{
    /// <summary>规则层：L2=取流适配规则，L3=脚本适配规则（换套路级，失败回落原生并提示）。</summary>
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

public static class ScriptAdapter
{
    /// <summary>
    /// 按序应用适配规则（L2/L3）。任一规则产出的 URL 不合法（空/非 http）→ 整体判定规则失败，
    /// 调用方自动回落原生路径；L3 失败时 needsNotice=true（UI 明确提示，不静默坏掉）。
    /// </summary>
    public static (string Url, bool Applied, bool NeedsNotice) Apply(
        string url, IEnumerable<AdaptRule> rules, out AdaptRule? failed)
    {
        failed = null;
        if (string.IsNullOrEmpty(url) || rules == null) return (url, false, false);
        var current = url;
        foreach (var rule in rules)
        {
            try
            {
                if (!string.IsNullOrEmpty(rule.MatchHost) && !current.Contains(rule.MatchHost, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(rule.ReplaceHost))
                {
                    var idx = current.IndexOf("://", StringComparison.Ordinal);
                    if (idx < 0) { failed = rule; return FallbackNotice(rule); }
                    var rest = current[(idx + 3)..];
                    var slash = rest.IndexOf('/', StringComparison.Ordinal);
                    current = current[..(idx + 3)] + rule.ReplaceHost + (slash >= 0 ? rest[slash..] : "/");
                }
                if (!string.IsNullOrEmpty(rule.AddQueryParam))
                {
                    current += (current.Contains('?', StringComparison.Ordinal) ? "&" : "?") + rule.AddQueryParam;
                }
                if (string.IsNullOrEmpty(current) ||
                    (!current.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !current.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    failed = rule;
                    return FallbackNotice(rule);
                }
            }
            catch
            {
                failed = rule;
                return FallbackNotice(rule);
            }
        }
        return (current, !ReferenceEquals(current, url) || !string.Equals(current, url, StringComparison.Ordinal), false);
    }

    /// <summary>规则命中失败：L3 明确提示（needsNotice=true），调用方回落原生路径。</summary>
    public static bool IsScriptLayer(AdaptRule rule) => rule.Layer == "L3";

    private static (string, bool, bool) FallbackNotice(AdaptRule rule) =>
        (string.Empty, false, IsScriptLayer(rule));
}

/// <summary>热更新配置文档：L1 数据 + L2/L3 规则。JSON 解析失败即判损坏（走本地缓存）。</summary>
public sealed class RemoteConfigDoc
{
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

/// <summary>热更新拉取结果。</summary>
public enum FetchOutcome
{
    Fresh = 0,      // 拉到新配置并已应用（静默）
    UsedCache = 1,  // 拉取失败，本地缓存续跑（静默，日志记录）
    Failed = 2,     // 拉取失败且无可用缓存：保持默认（首次可提示一次）
}

/// <summary>
/// 自愈3·热更新拉取器：远程文档成功才覆盖本地缓存（坏数据不落盘）；
/// 拉取失败读本地缓存继续跑（核心要求：源端变更免安装包修复，断网不受影响）。
/// 下载函数由 UI 注入（标准 HttpClient，$0 托管在 GitHub raw）。
/// </summary>
public class RemoteConfigFetcher
{
    private readonly Func<string, Task<string?>> _download;
    private readonly Func<string, string?> _readCache;
    private readonly Action<string, string> _writeCache;

    public RemoteConfigFetcher(Func<string, Task<string?>> download,
        Func<string, string?> readCache, Action<string, string> writeCache)
    {
        _download = download;
        _readCache = readCache;
        _writeCache = writeCache;
    }

    public RemoteConfigDoc Current { get; private set; } = RemoteConfigDoc.Empty;

    public async Task<FetchOutcome> FetchAsync(string url)
    {
        string? body = null;
        try { body = await _download(url); }
        catch { body = null; }
        if (!string.IsNullOrWhiteSpace(body) && RemoteConfigDoc.TryParse(body, out var fresh))
        {
            Current = fresh;
            try { _writeCache(url, body); } catch { /* 缓存写失败不影响本次应用 */ }
            return FetchOutcome.Fresh;
        }
        var cached = _readCache(url);
        if (!string.IsNullOrWhiteSpace(cached) && RemoteConfigDoc.TryParse(cached, out var cachedDoc))
        {
            Current = cachedDoc;
            return FetchOutcome.UsedCache;
        }
        return FetchOutcome.Failed;
    }
}

/// <summary>
/// 自愈5·DNS/接口失败重试备用域名：主域请求失败 → 备用域列表按序重试一次。
/// 备用域表由热更新 L1 数据下发（RemoteConfigDoc.BackupDomains），也可本地内置；不硬编码机型。
/// </summary>
public static class DomainFallback
{
    /// <summary>主域（小写 host）→ 备用域列表。热更新成功时覆盖，失败保留现有表继续跑。</summary>
    public static Dictionary<string, string[]> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取 URL 的备用域列表（无表或无匹配返回空）。</summary>
    public static string[] AlternativesFor(string url)
    {
        if (string.IsNullOrEmpty(url) || Map.Count == 0) return Array.Empty<string>();
        try
        {
            var host = new Uri(url).Host;
            return Map.TryGetValue(host, out var alts) ? alts : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>把 URL 的 host 换成备用域（path/query 原样保留）。</summary>
    public static string WithHost(string url, string newHost) => new UriBuilder(new Uri(url)) { Host = newHost }.Uri.ToString();
}

// ============ 自愈2·取票算法多版本 ============

/// <summary>取票算法版本接口：v1 内置 cKey；后续版本经热更新参数化注入，按序尝试。</summary>
public interface ITicketAlgorithm
{
    string Version { get; }
    /// <summary>产出该版本的票据查询 URL（null=该版本不适用此台）。</summary>
    string? Build(string channelId, long epochSeconds, string guid);
}

/// <summary>取票算法 v1：内置央视频 cKey（与参考实现对拍过的既有算法）。</summary>
public sealed class CkeyAlgorithmV1 : ITicketAlgorithm
{
    public const string GuidConst = "00112233445566778899aabbccddeeff";
    public string Version => "v1-cKey";
    public string? Build(string channelId, long epochSeconds, string guid)
    {
        var svc = new YspTicketService();
        var parts = channelId.Split(':');
        var (cnlid, livepid, defn) = parts.Length == 3
            ? (parts[0], parts[1], parts[2])
            : (channelId, "900001", "fhd");
        return svc.BuildTicketUrl(cnlid, livepid, defn, epochSeconds);
    }
}

/// <summary>
/// 取票多版本注册表：按序产出各版本票据 URL，全失效由 UI 明确提示
/// （「直播源票据全部失效，可稍后重试或导入自有源」），不静默坏掉；用户自有 m3u 兜底。
/// </summary>
public static class TicketAlgorithms
{
    private static readonly List<ITicketAlgorithm> _versions = new() { new CkeyAlgorithmV1() };

    /// <summary>热更新注入新版本算法（追加到尝试序列尾部）。</summary>
    public static void Register(ITicketAlgorithm algo)
    {
        if (algo != null && !_versions.Any(a => a.Version == algo.Version)) _versions.Add(algo);
    }

    public static IReadOnlyList<ITicketAlgorithm> Versions => _versions;

    /// <summary>产出全部版本的候选票据 URL（顺序即尝试顺序）。</summary>
    public static List<string> TryAll(string channelId, long epochSeconds, string guid)
    {
        var result = new List<string>();
        foreach (var algo in _versions)
        {
            try
            {
                var url = algo.Build(channelId, epochSeconds, guid);
                if (!string.IsNullOrEmpty(url)) result.Add(url);
            }
            catch { /* 单版本异常跳过，不拖垮整体 */ }
        }
        return result;
    }
}

// ============ 自愈7·配置损坏自动重建与快照恢复 ============

/// <summary>配置恢复路径。</summary>
public enum RecoveryPath
{
    Main = 0,       // 主档完好
    Snapshot = 1,   // 主档损坏，快照恢复
    Default = 2,    // 主档与快照全坏，重建默认（UI 明确提示一次）
}

/// <summary>
/// 自愈7·配置损坏：读主档 → 损坏按滚动快照回放（1→2→3）→ 全坏重建默认。
/// 写入时滚动快照（主档前移留档），保留 SnapshotKeep 份。读写委托由 UI 注入（文件或 SharedPreferences）。
/// </summary>
public class SnapshotStore
{
    private readonly Func<string, string?> _read;
    private readonly Action<string, string> _write;

    public SnapshotStore(Func<string, string?> read, Action<string, string> write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>滚动快照份数。</summary>
    public const int SnapshotKeep = 3;

    public const string MainKey = "cfg:main";

    /// <summary>保存：主档写入并把旧主档滚动进快照链（坏数据只影响下一次读，本档不受损）。</summary>
    public void Save(string content)
    {
        try
        {
            var prev = _read(MainKey);
            if (prev != null)
            {
                for (int i = SnapshotKeep; i >= 2; i--)
                {
                    var older = _read(SnapshotKey(i - 1));
                    if (older != null) _write(SnapshotKey(i), older);
                }
                _write(SnapshotKey(1), prev);
            }
            _write(MainKey, content);
        }
        catch { /* 存储异常不拖垮保存流程，主档为准 */ }
    }

    private static string SnapshotKey(int index) => $"cfg:snap{index}";

    /// <summary>读取：主档完好即用；损坏（null/空白/解析失败）依次回放快照；全坏返回默认。</summary>
    public (string? Content, RecoveryPath Path) Load(Func<string?, bool> validate)
    {
        var main = _read(MainKey);
        if (!string.IsNullOrWhiteSpace(main) && validate(main)) return (main, RecoveryPath.Main);
        for (int i = 1; i <= SnapshotKeep; i++)
        {
            var snap = _read(SnapshotKey(i));
            if (!string.IsNullOrWhiteSpace(snap) && validate(snap)) return (snap, RecoveryPath.Snapshot);
        }
        return (null, RecoveryPath.Default);
    }
}

// ============ 自愈8·崩溃自恢复 ============

/// <summary>崩溃后下次启动的自恢复计划。</summary>
public sealed class RecoveryPlan
{
    /// <summary>清除取流缓存（可疑状态）。</summary>
    public bool ClearResolverCache { get; init; }

    /// <summary>预检全量重测（失效台重新探测）。</summary>
    public bool RequeueAllPrecheck { get; init; }

    /// <summary>画面载体回稳定档（纹理载体在崩溃场景下先回表面载体，真机再验证纹理）。</summary>
    public bool FallbackToStableCarrier { get; init; }

    /// <summary>连续崩溃次数（供日志与提示语）。</summary>
    public int CrashCount { get; init; }
}

/// <summary>
/// 自愈8·崩溃自恢复：崩溃旗标由 CrashNet 落盘，下次启动消费并按计划重置可疑状态（静默自愈，
/// 日志可查；仅在连续崩溃≥3 时屏显一次「已自动恢复」提示）。旗标本体不敏感，只含计数与时间。
/// </summary>
public static class CrashRecovery
{
    public const int NoticeAfterConsecutive = 3;

    public static RecoveryPlan Plan(int crashCount) => new()
    {
        ClearResolverCache = crashCount > 0,
        RequeueAllPrecheck = crashCount > 0,
        FallbackToStableCarrier = crashCount > 0,
        CrashCount = crashCount,
    };

    public static bool ShouldNotice(RecoveryPlan plan) => plan.CrashCount >= NoticeAfterConsecutive;
}
