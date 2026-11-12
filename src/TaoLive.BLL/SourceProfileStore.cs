using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TaoLive.BLL;

/// <summary>源类型定档（老板 2026-11-12 批准的源档案标记架构）：Kind 管形状类，Rotating 管行为标记。</summary>
public enum SourceKind
{
    Unknown,    // 未定档：等两次取样行为探测
    Constant,   // 恒定 URL：落盘价值最高
    TicketShort,// 短票据：落了必过期，不落盘
    ShortSign,  // 短签名：保鲜须短于签名期
    IPDirect,   // 纯 IP 直连：不走系统解析，对解析僵死免疫
}

/// <summary>
/// 单源档案：类型、保鲜期、节拍、防盗头、稳定度（老板批准字段）。
/// JSON 持久化（source_profiles.json，与 source_health.json 同目录），重启仍在。
/// </summary>
public sealed class SourceProfile
{
    public string SourceId { get; set; } = "";
    public string Kind { get; set; } = SourceKind.Unknown.ToString();
    public bool Rotating { get; set; }        // 行为标记：换票会轮换 URL
    public long RefreshIntervalMs { get; set; } = 0;  // 保鲜期（0=未定，吃 WarmupPolicy 兜底）
    public long MinGapMs { get; set; } = 0;           // 两次请求最小间隔（0=未定）
    public bool IpBound { get; set; }         // IP 绑定画像（同 IP 不建双会话、时段豁免等特判的统一判据）
    public bool BatchAggregation { get; set; } // 整表聚合：一次请求覆盖全源，节拍按源不按台（nmtv/gxtv）
    public string UaOverride { get; set; } = "";      // 防盗头：取流统一 UA（空=用频道默认）
    public string LastUrlHash { get; set; } = "";     // 上次取样指纹（判轮换）
    public long SampledUtcMs { get; set; }            // 最近取样时间（Unix 毫秒）
}

/// <summary>
/// 源档案库（上善若水纪律·点名册写活）：机制读档案不读代码——
/// - 出厂名册：现有已核实源的默认档案（档案的出厂值，非代码点名册）；
/// - 入库自动定档：新源 Register 按形状初判（纯 IP/签名参数），Unknown 源两次取样判行为（URL 轮换→Rotating，恒定→Constant）；
/// - 各机制回报：换票取样经 ObserveSample 更新档案，档案随世界变；
/// - 查询回落：KindOf/ProfileOf 未登记源按 Unknown 兜底，机制侧自备保守口径。
/// </summary>
public sealed class SourceProfileStore
{
    /// <summary>进程内单例（BLL 各策略经 Shared 读写；UI 启动注入目录并 Load）。
    /// Lazy 保序：出厂名册（静态字段）先于实例构造完成，防静态初始化环。</summary>
    private static readonly Lazy<SourceProfileStore> _shared = new(() => new SourceProfileStore());
    public static SourceProfileStore Shared => _shared.Value;

    private const int FlushDebounceMs = 3000;

    /// <summary>打点委托（BLL 不反引 UI，口径同 SelfHealProxyKernel.Reporter）：UI 启动注入，未注入时静默。</summary>
    public static Action<string>? Reporter { get; set; }
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SourceProfile> _profiles = new(StringComparer.Ordinal);
    private string _path = "";
    private Timer? _flushTimer;
    private bool _dirty;

    // ---- 出厂名册（= 现有点名册原值搬家，出处见 StreamResolver/WarmupPolicy 原注释） ----
    private static readonly SourceProfile[] FactoryProfiles =
    {
        new() { SourceId="yangshipin", Kind=SourceKind.TicketShort.ToString(), Rotating=true,  RefreshIntervalMs=10*60_000,        MinGapMs=2_000, IpBound=true  },
        new() { SourceId="cztv",       Kind=SourceKind.ShortSign.ToString(),   Rotating=true,  RefreshIntervalMs=2*60*60_000,      MinGapMs=1_000 },
        new() { SourceId="gzstv",      Kind=SourceKind.TicketShort.ToString(), Rotating=true,  RefreshIntervalMs=30*60_000,        MinGapMs=1_500 },
        new() { SourceId="jstv",       Kind=SourceKind.ShortSign.ToString(),   Rotating=true,  RefreshIntervalMs=150_000,          MinGapMs=1_000 },
        new() { SourceId="kankan",     Kind=SourceKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=0,                MinGapMs=0, UaOverride=StreamResolver.KankanFetchUserAgent },
        new() { SourceId="nmtv",       Kind=SourceKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=30*60_000,        MinGapMs=1_000, BatchAggregation=true },
        new() { SourceId="gxtv",       Kind=SourceKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=6*60*60_000,      MinGapMs=1_000, BatchAggregation=true },
        new() { SourceId="xatv",       Kind=SourceKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=6*60*60_000,      MinGapMs=1_000 },
    };

    private SourceProfileStore()
    {
        foreach (var p in FactoryProfiles) _profiles[p.SourceId] = p;
    }

    /// <summary>UI 启动注入目录并载入（与 source_health.json 同目录；落盘条目不覆盖出厂名册未覆盖字段之外——以落盘为准）。</summary>
    public void Load(string dir)
    {
        _path = Path.Combine(dir ?? ".", "source_profiles.json");
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<SourceProfile[]>(json ?? "[]") ?? Array.Empty<SourceProfile>();
            foreach (var p in loaded)
                if (!string.IsNullOrEmpty(p.SourceId)) _profiles[p.SourceId] = p;
        }
        catch (Exception ex) { Reporter?.Invoke($"[源档案] 载入失败按空档处理：{ex.Message}"); }
    }

    /// <summary>档案查询：未登记源返回空档（Unknown），机制侧按保守口径兜底。</summary>
    public SourceProfile ProfileOf(string sourceId) =>
        _profiles.TryGetValue(sourceId ?? "", out var p) ? p : new SourceProfile { SourceId = sourceId ?? "" };

    public SourceKind KindOf(string sourceId) =>
        Enum.TryParse<SourceKind>(ProfileOf(sourceId).Kind, out var k) ? k : SourceKind.Unknown;

    public bool IsRotating(string sourceId) => ProfileOf(sourceId).Rotating;

    /// <summary>新源入库自动定档：按 URL 形状初判（纯 IP→IPDirect；签名参数→ShortSign）；其后两次取样定行为。</summary>
    public void Register(string sourceId, string sampleUrl, long nowMs)
    {
        var p = _profiles.TryAdd(sourceId, new SourceProfile { SourceId = sourceId })
            ? _profiles[sourceId]
            : _profiles[sourceId];
        if (p.Kind == SourceKind.Unknown.ToString())
            p.Kind = ClassifyUrl(sampleUrl).ToString();
        MarkDirty();
    }

    /// <summary>取样回报（换票/取流成功后调用）：两次指纹比对定行为——变了记 Rotating，恒定升 Constant（仅 Unknown 源）。</summary>
    public void ObserveSample(string sourceId, string url, long nowMs)
    {
        if (string.IsNullOrEmpty(url)) return;
        var p = _profiles.GetOrAdd(sourceId ?? "", id => new SourceProfile { SourceId = id }); // 未登记源入库即建档案（自动定档起点）
        var hash = UrlFingerprint(url);
        var prev = p.LastUrlHash;
        bool same = prev == hash && prev != "";
        p.LastUrlHash = hash;
        p.SampledUtcMs = nowMs;
        if (p.Kind != SourceKind.Unknown.ToString()) return; // 已定档：指纹照记（稳定度可查），类型不被学习覆盖
        if (same) { p.Kind = SourceKind.Constant.ToString(); MarkDirty(); }          // 两次同指纹→恒定
        else if (!same && prev != "") { p.Rotating = true; MarkDirty(); }            // 两次异指纹→轮换
        // 首次取样（prev 为空）：只记指纹，等下一次定档
    }

    /// <summary>URL 形状判别（纯逻辑可测）：纯 IP 主机→IPDirect；带签名/票据参数→ShortSign；其余 Unknown 待行为探测。</summary>
    public static SourceKind ClassifyUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return SourceKind.Unknown;
        if (System.Net.IPAddress.TryParse(uri.Host, out _)) return SourceKind.IPDirect;
        var q = uri.Query.ToLowerInvariant();
        if (q.Contains("auth_key") || q.Contains("txsecret") || q.Contains("txtime") || q.Contains("sign=") || q.Contains("token="))
            return SourceKind.ShortSign;
        return SourceKind.Unknown;
    }

    /// <summary>URL 指纹：主机+路径+查询键名序列（参数值不参与）——换票值变指纹不变，路径变才算轮换。</summary>
    public static string UrlFingerprint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var keys = string.Join(",", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('=', 2)[0]).OrderBy(s => s, StringComparer.Ordinal));
        var raw = $"{uri.Host}{uri.AbsolutePath}|{keys}";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>脏标记 + 3 秒节流后台落盘（写入绝不阻塞播放线程，口径同 SourceHealthStore）。</summary>
    private void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_flushTimer != null) return;
            _flushTimer = new Timer(_ => { lock (_gate) { if (_dirty) { _dirty = false; FlushNow(); } } }, null, FlushDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>立即原子落盘（tmp+move 家法；退出/暂停时调用）。</summary>
    public void FlushNow()
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles.Values.ToArray()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Reporter?.Invoke($"[源档案] 落盘失败：{ex.Message}"); }
    }
}
