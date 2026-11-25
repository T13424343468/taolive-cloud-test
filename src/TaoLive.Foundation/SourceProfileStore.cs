using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TaoLive.Foundation;

/// <summary>源类型定档（老板 2026-11-12 批准的源档案标记架构）：Kind 管形状类，Rotating 管行为标记。引擎搬回适配：2.0 的 Model.SourceKind 管传输协议族（PureIp/Cdn/Web），本枚举管源档形状——两者并存，引用处按全名区分。</summary>
public enum SourceArchiveKind
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
    public string Kind { get; set; } = SourceArchiveKind.Unknown.ToString();
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

    private const int FlushDebounceMs = TaoLive.Foundation.PersistencePolicy.FlushDebounceMs; // 归一（规范〇.7.3）：节拍唯一事实源在 Foundation.PersistencePolicy

    /// <summary>打点委托（BLL 不反引 UI，口径同诊断打点通道）：UI 启动注入，未注入时静默。</summary>
    public static Action<string>? Reporter { get; set; }
    private readonly object _gate = new();
    private readonly object _ioGate = new(); // vc74 P3⑤：落盘互斥——timer Flush 与退出 FlushNow 并撞同一 tmp 不再互吞
    private readonly ConcurrentDictionary<string, SourceProfile> _profiles = new(StringComparer.Ordinal);
    private string _path = "";
    private Timer? _flushTimer;

    // ---- 出厂名册（= 现有点名册原值搬家，出处见 StreamResolver/WarmupPolicy 原注释） ----
    private static readonly SourceProfile[] FactoryProfiles =
    {
        new() { SourceId="yangshipin", Kind=SourceArchiveKind.TicketShort.ToString(), Rotating=true,  RefreshIntervalMs=10*60_000,        MinGapMs=2_000, IpBound=true  },
        new() { SourceId="cztv",       Kind=SourceArchiveKind.ShortSign.ToString(),   Rotating=true,  RefreshIntervalMs=2*60*60_000,      MinGapMs=1_000 },
        new() { SourceId="gzstv",      Kind=SourceArchiveKind.TicketShort.ToString(), Rotating=true,  RefreshIntervalMs=30*60_000,        MinGapMs=1_500 },
        new() { SourceId="jstv",       Kind=SourceArchiveKind.ShortSign.ToString(),   Rotating=true,  RefreshIntervalMs=150_000,          MinGapMs=1_000 },
        new() { SourceId="kankan",     Kind=SourceArchiveKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=0,                MinGapMs=0, UaOverride="Mozilla/5.0 (Linux; Android 12; TCL 50V8G) AppleWebKit/537.36 TaoLive/2.0" }, // 引擎搬回适配：vc53 StreamResolver.KankanFetchUserAgent 在 2.0 归 Catalog 取流层（TicketResolver.ClientUserAgent 同口径），Foundation 不反引 Catalog（// ENGINE-WIRE: 换 UA 时同步此处）
        new() { SourceId="nmtv",       Kind=SourceArchiveKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=30*60_000,        MinGapMs=1_000, BatchAggregation=true },
        new() { SourceId="gxtv",       Kind=SourceArchiveKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=6*60*60_000,      MinGapMs=1_000, BatchAggregation=true },
        new() { SourceId="xatv",       Kind=SourceArchiveKind.Constant.ToString(),    Rotating=false, RefreshIntervalMs=6*60*60_000,      MinGapMs=1_000 },
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

    public SourceArchiveKind KindOf(string sourceId) =>
        Enum.TryParse<SourceArchiveKind>(ProfileOf(sourceId).Kind, out var k) ? k : SourceArchiveKind.Unknown;

    public bool IsRotating(string sourceId) => ProfileOf(sourceId).Rotating;

    /// <summary>新源入库自动定档：按 URL 形状初判（纯 IP→IPDirect；签名参数→ShortSign）；其后两次取样定行为。
    /// vc74 P3⑤：写档改复制-发布（clone→改→TryUpdate），发布后档案不可变——与落盘序列化并发不再脏快照。</summary>
    public void Register(string sourceId, string sampleUrl, long nowMs)
    {
        var kind = ClassifyUrl(sampleUrl).ToString();
        while (true)
        {
            var cur = _profiles.GetOrAdd(sourceId, id => new SourceProfile { SourceId = id });
            var next = CloneProfile(cur);
            if (next.Kind == SourceArchiveKind.Unknown.ToString()) next.Kind = kind;
            if (_profiles.TryUpdate(sourceId, next, cur)) break; // CAS 失败=并发改档，重取重判
        }
        MarkDirty();
    }

    /// <summary>取样回报（换票/取流成功后调用）：两次指纹比对定行为——变了记 Rotating，恒定升 Constant（仅 Unknown 源）。
    /// vc74 P3⑤：写档改复制-发布——旧版在原对象上改字段，与 FlushNow 序列化并发可脏一版快照；
    /// 发布后的档案对象不再被改，序列化只见不可变档案。</summary>
    public void ObserveSample(string sourceId, string url, long nowMs)
    {
        if (string.IsNullOrEmpty(url)) return;
        var key = sourceId ?? "";
        var hash = UrlFingerprint(url);
        while (true)
        {
            var cur = _profiles.GetOrAdd(key, id => new SourceProfile { SourceId = id }); // 未登记源入库即建档案（自动定档起点）
            var next = CloneProfile(cur);
            next.LastUrlHash = hash;
            next.SampledUtcMs = nowMs;
            var kindChanged = false;
            if (next.Kind == SourceArchiveKind.Unknown.ToString())
            {
                var same = cur.LastUrlHash == hash && cur.LastUrlHash != "";
                if (same) { next.Kind = SourceArchiveKind.Constant.ToString(); kindChanged = true; } // 两次同指纹→恒定
                else if (cur.LastUrlHash != "") { next.Rotating = true; kindChanged = true; }        // 两次异指纹→轮换
            }
            if (_profiles.TryUpdate(key, next, cur))
            {
                if (kindChanged) MarkDirty(); // 已定档：指纹照记（稳定度可查），类型不被学习覆盖，不额外标脏
                return;
            }
            // CAS 失败=并发改档：丢失更新封死，重取重判
        }
    }

    /// <summary>档案复制（vc74 P3⑤ 写档复制-发布共用）：逐字段拷贝，不改引用共享对象。</summary>
    private static SourceProfile CloneProfile(SourceProfile p) => new()
    {
        SourceId = p.SourceId,
        Kind = p.Kind,
        Rotating = p.Rotating,
        RefreshIntervalMs = p.RefreshIntervalMs,
        MinGapMs = p.MinGapMs,
        IpBound = p.IpBound,
        BatchAggregation = p.BatchAggregation,
        UaOverride = p.UaOverride,
        LastUrlHash = p.LastUrlHash,
        SampledUtcMs = p.SampledUtcMs,
    };

    /// <summary>URL 形状判别（纯逻辑可测）：纯 IP 主机→IPDirect；带签名/票据参数→ShortSign；其余 Unknown 待行为探测。</summary>
    public static SourceArchiveKind ClassifyUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return SourceArchiveKind.Unknown;
        if (System.Net.IPAddress.TryParse(uri.Host, out _)) return SourceArchiveKind.IPDirect;
        var q = uri.Query.ToLowerInvariant();
        if (q.Contains("auth_key") || q.Contains("txsecret") || q.Contains("txtime") || q.Contains("sign=") || q.Contains("token="))
            return SourceArchiveKind.ShortSign;
        return SourceArchiveKind.Unknown;
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

    /// <summary>脏标记 + 3 秒节流后台落盘（写入绝不阻塞播放线程，口径同 SourceHealthStore）。
    /// vc74 P3⑤修：回调先锁内摘 timer 再锁外落盘——落盘期间新到的脏账立即可建新 timer，不再吞
    /// （vc73 SourceHealthStore ⑩ 同款；_dirty 死状态随同款一并删除，FlushNow 无条件全量写出）。</summary>
    private void MarkDirty()
    {
        lock (_gate)
        {
            if (_flushTimer != null) return;
            _flushTimer = new Timer(_ =>
            {
                Timer? fired;
                lock (_gate)
                {
                    fired = _flushTimer;
                    _flushTimer = null; // 先摘：此后 MarkDirty 立即可建新 timer
                }
                FlushNow(); // 锁外落盘：快照即覆盖截至摘牌时刻的全部脏账
                fired?.Dispose();
            }, null, FlushDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>立即原子落盘（tmp+move 家法；退出/暂停时调用）。
    /// vc74 P3⑤：_ioGate 串行化落盘者，与 timer Flush 并发不再互撞同一 tmp（IOException 互吞）；IO 在 _gate 外，播放线程零阻塞口径兑现。</summary>
    public void FlushNow()
    {
        if (string.IsNullOrEmpty(_path)) return;
        lock (_ioGate)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles.Values.ToArray()));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Reporter?.Invoke($"[源档案] 落盘失败：{ex.Message}"); }
        }
    }
}
