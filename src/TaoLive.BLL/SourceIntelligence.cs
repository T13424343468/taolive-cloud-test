using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.BLL;

// ============ vc45 ②·选源智能化核心（方案定稿 2026-11-12 16:5x 拍板，逐条落码）============
// 纯逻辑零网络：探活/发现/IO 全部由调用方注入，所有断言可测（不碰外网）。
// 分件：
//   1. SourceIntelligenceEngine  评分引擎：稳定×时效×清晰度合适度×可插拔因子 − 冷却惩罚；逐因子分值可查；
//                                 最少驻留 + 迟滞（SwitchAdvisor），选源不抖动。
//   2. FailureCooldownLedger     失败模式分级冷却：403 票过期=短 / 超时=中 / DNS 死=长 / 内容校验失败=拉黑。
//   3. StreamLiveness            流活性校验：M3U8 序号前进 + 分片时间戳新鲜，防「HTTP 200 但是死流」假活。
//   4. DeadSourceLedger          判死双通道 + 低频复检复活（30–60 分钟），复活前频道走备援。
//   5. SwitchBudget              切换预算：单位时间切源硬上限，防连环切源拖垮播放。
//   6. DynamicSourceStore        动态源库：自动搜源收录的源落账（内存+可选落盘挂接由调用方做）。
//   7. SourceScout               自动搜源管线：发现→静默校验→评分→入库；死源被新源直接顶替；
//                                 两级触发（常规低频空闲探索 / 救急级立即搜）。
//   8. ChannelClassifier         新频道归类：同名判重并入备源；异名新台按台名归类（央视/卫视/地方）。

/// <summary>评分上下文：引擎只读这些，因子全部可插拔。</summary>
public class SourceScoreContext
{
    public string SourceKey = "";        // 源标识（URL 或 SourceId:EpgKey）
    public double Stability;             // 0..1 稳定度（健康账可靠占比）
    public double Freshness;             // 0..1 时效（最近实测回写的新鲜度/半衰）
    public double ClarityFit;            // 0..1 清晰度合适度（设备档位匹配）
    public bool IsIpLiteral;             // 纯 IP 直连（解析僵死免疫，写活批补记 3 因子）
    public bool ConstantKind;            // 恒定型 URL（票据型降权，热备座次同口径）
    public long CooldownRemainingMs;     // 分级冷却剩余（惩罚项）
}

/// <summary>逐因子分值：S3 决策日志逐条可查，出问题能定位哪个因子拉的分。</summary>
public readonly record struct FactorScore(string Id, double Value);

public sealed class SourceScore
{
    public double Total;
    public List<FactorScore> Factors = new();
}

/// <summary>评分因子接口：独立可替换，新因子实现后加入引擎即可。</summary>
public interface ISourceFactor
{
    string Id { get; }
    /// <summary>0..1 乘法因子。</summary>
    double Score(SourceScoreContext ctx);
}

public sealed class StabilityFactor : ISourceFactor
{
    public string Id => "稳定度";
    public double Score(SourceScoreContext ctx) => Math.Clamp(ctx.Stability, 0, 1);
}

public sealed class FreshnessFactor : ISourceFactor
{
    public string Id => "时效";
    public double Score(SourceScoreContext ctx) => Math.Clamp(ctx.Freshness, 0, 1);
}

public sealed class ClarityFitFactor : ISourceFactor
{
    public string Id => "清晰度合适度";
    public double Score(SourceScoreContext ctx) => Math.Clamp(ctx.ClarityFit, 0, 1);
}

/// <summary>纯 IP 直连免疫因子（写活批补记 3）：纯 IP 1.0，域名 0.9——解析僵死场景纯 IP 天然免疫。</summary>
public sealed class DnsImmunityFactor : ISourceFactor
{
    public string Id => "纯IP免疫";
    public double Score(SourceScoreContext ctx) => ctx.IsIpLiteral ? 1.0 : 0.9;
}

/// <summary>恒定型优先因子（写活批补记 2 座次同口径）：恒定 1.0，票据型 0.95。</summary>
public sealed class ConstantKindFactor : ISourceFactor
{
    public string Id => "恒定型";
    public double Score(SourceScoreContext ctx) => ctx.ConstantKind ? 1.0 : 0.95;
}

public sealed class SourceIntelligenceEngine
{
    public const double FullScore = 100.0;

    /// <summary>冷却惩罚基准：剩余冷却按此封顶折满（满冷却=罚 FullScore，分≤0 沉底）。</summary>
    public const long CooldownFullPenaltyMs = 60 * 60 * 1000;

    private readonly IReadOnlyList<ISourceFactor> _factors;

    /// <summary>因子可插拔：不传用默认五因子（稳定/时效/清晰度/纯IP/恒定）。</summary>
    public SourceIntelligenceEngine(IEnumerable<ISourceFactor>? factors = null) =>
        _factors = (factors ?? DefaultFactors()).ToList();

    public static IReadOnlyList<ISourceFactor> DefaultFactors() => new ISourceFactor[]
    {
        new StabilityFactor(), new FreshnessFactor(), new ClarityFitFactor(),
        new DnsImmunityFactor(), new ConstantKindFactor(),
    };

    /// <summary>总分 = FullScore × Π(因子分) − 冷却惩罚；逐因子分值随行返回（S3 可查）。</summary>
    public SourceScore Score(SourceScoreContext ctx)
    {
        var list = new List<FactorScore>();
        double prod = 1.0;
        foreach (var f in _factors)
        {
            double v = Math.Clamp(f.Score(ctx), 0, 1);
            list.Add(new FactorScore(f.Id, v));
            prod *= v;
        }
        double penalty = Math.Min(1.0, Math.Max(0, ctx.CooldownRemainingMs) / (double)CooldownFullPenaltyMs) * FullScore;
        return new SourceScore { Total = FullScore * prod - penalty, Factors = list };
    }
}

/// <summary>
/// 最少驻留 + 迟滞（方案一）：新源要明显优于当前源（超出迟滞带）且当前源驻留时间已满，才建议切换——防频繁换源画面顿挫。
/// </summary>
public static class SwitchAdvisor
{
    /// <summary>最少驻留：切换后 2 分钟内不再换（可被救急级绕过，预算仍受限）。</summary>
    public const long MinDwellMs = 2 * 60 * 1000;

    /// <summary>迟滞带：候选总分须高出当前分此值才值得切。</summary>
    public const double HysteresisMargin = 15.0;

    public static bool ShouldSwitch(double currentTotal, double candidateTotal, long msSinceLastSwitch) =>
        msSinceLastSwitch >= MinDwellMs && candidateTotal >= currentTotal + HysteresisMargin;
}

// ============ 失败模式分级冷却（方案二·1）============

public enum SourceFailureMode
{
    TicketExpired,    // 403 票过期：短冷却（5 分钟，等下一张票）
    Timeout,          // 超时/断流：中冷却（15 分钟）
    DnsDead,          // DNS 死：长冷却（60 分钟）
    ContentInvalid,   // 内容校验失败（伪 200/垃圾清单）：拉黑（等搜源顶替，不自动复活）
}

public sealed class FailureCooldownLedger
{
    public static readonly long TicketExpiredMs = 5 * 60 * 1000;
    public static readonly long TimeoutMs = 15 * 60 * 1000;
    public static readonly long DnsDeadMs = 60 * 60 * 1000;
    public static readonly long ContentInvalidMs = long.MaxValue; // 拉黑

    private readonly ConcurrentDictionary<string, long> _until = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public FailureCooldownLedger(Func<long>? nowMs = null) =>
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public void Record(string url, SourceFailureMode mode)
    {
        var dur = mode switch
        {
            SourceFailureMode.TicketExpired => TicketExpiredMs,
            SourceFailureMode.Timeout => TimeoutMs,
            SourceFailureMode.DnsDead => DnsDeadMs,
            _ => ContentInvalidMs,
        };
        _until[url] = dur == long.MaxValue ? long.MaxValue : _nowMs() + dur;
    }

    public long RemainingMs(string url)
    {
        var until = _until.TryGetValue(url, out var u) ? u : 0;
        if (until == 0) return 0;
        if (until == long.MaxValue) return long.MaxValue;
        return Math.Max(0, until - _nowMs());
    }

    /// <summary>冷却期内不再选它（拉黑=永久冷却，等搜源顶替）。</summary>
    public bool IsCooling(string url) => RemainingMs(url) > 0;

    /// <summary>复检复活/顶替后解除冷却。</summary>
    public void Clear(string url) => _until.TryRemove(url, out _);
}

// ============ 流活性校验（方案二·2）============

public static class StreamLiveness
{
    /// <summary>分片时间戳新鲜窗：3×典型分片时长（≤6 秒/片的 3 倍取 90 秒，覆盖慢端）。</summary>
    public const long FreshWindowMs = 90 * 1000;

    /// <summary>M3U8 媒体序号前进 + 最近分片时间戳在新鲜窗内 = 活流。防「HTTP 200 但是死流」假活。</summary>
    public static bool IsLive(long prevSeq, long newSeq, long lastSegmentUtcMs, long nowMs) =>
        newSeq > prevSeq && nowMs - lastSegmentUtcMs <= FreshWindowMs;
}

// ============ 判死与复活（方案三）============

public sealed class DeadSourceLedger
{
    public const long RecheckMinMs = 30 * 60 * 1000;  // 判死后 30–60 分钟低频复检
    public const long RecheckMaxMs = 60 * 60 * 1000;

    private sealed class DeadState { public long RecheckDueMs; }
    private readonly ConcurrentDictionary<string, DeadState> _dead = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public DeadSourceLedger(Func<long>? nowMs = null) =>
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>判死双通道验证：主解析通道失败 + 公共解析通道复核也失败，两路都挂才判死，不因单路 DNS 抖动误杀。</summary>
    public static bool ShouldDeclareDead(bool primaryChannelFailed, bool relayChannelFailed) =>
        primaryChannelFailed && relayChannelFailed;

    /// <summary>判死入账：复检期取 30–60 分钟内确定性散列（同键稳定、跨键分散）。</summary>
    public void MarkDead(string sourceKey)
    {
        uint h = 2166136261;
        foreach (var c in Encoding.UTF8.GetBytes(sourceKey)) { h ^= c; h *= 16777619; }
        var spread = RecheckMinMs + (long)(h % (uint)((RecheckMaxMs - RecheckMinMs) / 1000)) * 1000;
        _dead[sourceKey] = new DeadState { RecheckDueMs = _nowMs() + spread };
    }

    /// <summary>死亡期内：不选它，频道走备援。</summary>
    public bool IsDead(string sourceKey) => _dead.ContainsKey(sourceKey);

    /// <summary>复检期到：调用方低频探一次，通过即 Revive。</summary>
    public bool IsDueForRecheck(string sourceKey) =>
        _dead.TryGetValue(sourceKey, out var st) && _nowMs() >= st.RecheckDueMs;

    /// <summary>复检通过：自动复活（无需人工干预），解除冷却与死亡账。</summary>
    public void Revive(string sourceKey) => _dead.TryRemove(sourceKey, out _);
}

// ============ 切换预算（方案二·3）============

public sealed class SwitchBudget
{
    public const int DefaultMaxPerWindow = 4;   // 单位时间切源硬上限
    public const long DefaultWindowMs = 90 * 1000;

    private sealed class Win { public readonly List<long> Stamps = new(); }
    private readonly ConcurrentDictionary<string, Win> _wins = new(StringComparer.Ordinal);
    private readonly int _max;
    private readonly long _windowMs;
    private readonly Func<long> _nowMs;

    public SwitchBudget(int maxPerWindow = DefaultMaxPerWindow, long windowMs = DefaultWindowMs, Func<long>? nowMs = null)
    {
        _max = Math.Max(1, maxPerWindow);
        _windowMs = windowMs;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>消耗一次切换预算；窗内用满即拒绝（救急级也要守预算，不拿垃圾源凑数）。</summary>
    public bool TryConsume(string channelKey)
    {
        var win = _wins.GetOrAdd(channelKey, _ => new Win());
        lock (win)
        {
            var now = _nowMs();
            win.Stamps.RemoveAll(t => now - t >= _windowMs);
            if (win.Stamps.Count >= _max) return false;
            win.Stamps.Add(now);
            return true;
        }
    }
}

// ============ 动态源库（搜源收录落账）============

public sealed class DynamicSourceEntry
{
    public string ChannelKey = "";   // 归一台名
    public string ChannelName = "";  // 原台名
    public string Category = "";     // 央视/卫视/地方
    public string Url = "";
    public string Referer = "";
    public string UserAgent = "";
    public double Score;             // 入库时评分
    public long AddedUtcMs;
    public bool IsNewChannel;        // 目录外新频道（自动进目录）
}

/// <summary>动态源库：搜源验活收录的源（与内置备援表并行，按台名归一）。</summary>
public sealed class DynamicSourceStore
{
    private readonly ConcurrentDictionary<string, List<DynamicSourceEntry>> _index = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public DynamicSourceStore(Func<long>? nowMs = null) =>
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>台名归一（与 FallbackSourceRegistry.Normalize 同口径：去空格全半角）。</summary>
    public static string Normalize(string name) => (name ?? "").Replace(" ", "").Replace("　", "");

    /// <summary>入库（验活通过才调；同名归一台下按分降序）。</summary>
    public void Add(DynamicSourceEntry e)
    {
        var key = Normalize(e.ChannelName);
        e.ChannelKey = key;
        e.AddedUtcMs = _nowMs();
        var list = _index.GetOrAdd(key, _ => new List<DynamicSourceEntry>());
        lock (list)
        {
            list.Add(e);
            list.Sort((a, b) => b.Score.CompareTo(a.Score)); // 谁分高谁顶上
        }
    }

    public IReadOnlyList<DynamicSourceEntry> Get(string channelName) =>
        _index.TryGetValue(Normalize(channelName), out var l) ? l : (IReadOnlyList<DynamicSourceEntry>)Array.Empty<DynamicSourceEntry>();

    public bool Has(string channelName) => _index.ContainsKey(Normalize(channelName));

    /// <summary>死源直接顶替：把失效动态源摘除（新源入库时旧死源不等复活）。</summary>
    public void Remove(string channelName, string url)
    {
        if (_index.TryGetValue(Normalize(channelName), out var l))
            lock (l) l.RemoveAll(x => x.Url == url);
    }
}

// ============ 新频道归类（方案 §四·补）============

public static class ChannelClassifier
{
    /// <summary>按台名归类：央视（CCTV/CGTN）→ 卫视（×卫视）→ 地方。归类只做目录落位，不改播放。</summary>
    public static string CategoryOf(string name)
    {
        var n = DynamicSourceStore.Normalize(name);
        if (n.StartsWith("CCTV", StringComparison.OrdinalIgnoreCase) || n.StartsWith("CGTN", StringComparison.OrdinalIgnoreCase))
            return "央视";
        if (n.EndsWith("卫视", StringComparison.Ordinal)) return "卫视";
        return "地方";
    }
}

// ============ 自动搜源管线（方案四）============

/// <summary>候选源：发现器产出（URL+可选防盗头）。</summary>
public sealed record ScoutCandidate(string Name, string Url, string Referer = "", string UserAgent = "");

/// <summary>搜源结论：收录了什么、顶替了什么；一步一记录（S3 留痕由调用方接）。</summary>
public sealed class ScoutResult
{
    public List<string> Log = new();
    public DynamicSourceEntry? Best;
    public bool ReplacedDead;         // 死源被新源直接顶替
    public bool EnteredAsNewChannel;  // 目录外新频道自动进目录
}

/// <summary>
/// 自动搜源管线：公开渠道发现候选 → 静默校验（清单活+分片为新鲜真数据，注入探针）→ 评分 → 入库。
/// 常规级（低频空闲）与救急级（频道无可用源）共用本管线；救急级仍受校验与切换预算约束，没验活的源绝不入库。
/// </summary>
public sealed class SourceScout
{
    private readonly SourceIntelligenceEngine _engine;
    private readonly DynamicSourceStore _store;
    private readonly Func<long> _nowMs;

    public SourceScout(SourceIntelligenceEngine engine, DynamicSourceStore store, Func<long>? nowMs = null)
    {
        _engine = engine;
        _store = store;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 跑一轮搜源。
    /// discover：公开免费渠道发现候选（IO 注入）；validate：静默校验（IO 注入，清单活+分片新鲜真数据）；
    /// existingSources：该台既有源（内置表+动态库）评分基线（用于迟滞与顶替判断）；
    /// channelKnown：该台是否已在目录（false=目录外新台，验活后自动进目录）。
    /// </summary>
    public async Task<ScoutResult> RunAsync(
        string channelName,
        Func<Task<List<ScoutCandidate>>> discover,
        Func<ScoutCandidate, Task<bool>> validate,
        double existingBestScore,
        bool channelKnown,
        CancellationToken ct = default)
    {
        var result = new ScoutResult();
        List<ScoutCandidate> cands;
        try { cands = await discover(); }
        catch (Exception ex) { result.Log.Add($"[搜源] {channelName} 发现失败：{ex.Message}"); return result; }

        double best = existingBestScore;
        DynamicSourceEntry? bestEntry = null;
        foreach (var c in cands)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            try { ok = await validate(c); }   // 静默校验：没验活的源绝不入库
            catch (Exception ex) { result.Log.Add($"[搜源] {channelName} 校验异常 {c.Url}：{ex.Message}"); continue; }
            if (!ok) { result.Log.Add($"[搜源] {channelName} 未验活不入库：{c.Url}"); continue; }

            var score = _engine.Score(new SourceScoreContext
            {
                SourceKey = c.Url,
                Stability = 1.0,  // 新源无历史账：验活即给基准分，后续实测回写收敛
                Freshness = 1.0,
                ClarityFit = 1.0,
                IsIpLiteral = Uri.TryCreate(c.Url, UriKind.Absolute, out var u)
                              && (u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6),
                ConstantKind = SourceProfileStore.ClassifyUrl(c.Url) is SourceKind.Constant or SourceKind.IPDirect,
                CooldownRemainingMs = 0,
            });
            result.Log.Add($"[搜源] {channelName} 候选 {c.Url} 得分 {score.Total:F1}（{string.Join("/", score.Factors.Select(f => $"{f.Id}:{f.Value:F2}"))}）");
            if (score.Total > best)
            {
                best = score.Total;
                bestEntry = new DynamicSourceEntry
                {
                    ChannelName = c.Name, Url = c.Url, Referer = c.Referer, UserAgent = c.UserAgent,
                    Score = score.Total, Category = ChannelClassifier.CategoryOf(c.Name),
                    IsNewChannel = !channelKnown,
                };
            }
        }
        if (bestEntry != null)
        {
            _store.Add(bestEntry);
            result.Best = bestEntry;
            result.EnteredAsNewChannel = bestEntry.IsNewChannel;
            result.ReplacedDead = existingBestScore <= 0; // 既有分≤0（死/冷却）= 顶替
            result.Log.Add($"[搜源] {channelName} 收录 {bestEntry.Url}（{(bestEntry.IsNewChannel ? "新频道进目录" : "备源")}，分 {bestEntry.Score:F1}）");
        }
        else
        {
            result.Log.Add($"[搜源] {channelName} 本轮无优于既有源的候选，不收录（不拿垃圾源凑数）");
        }
        return result;
    }
}
