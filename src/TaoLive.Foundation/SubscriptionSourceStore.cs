using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.Foundation;

/// <summary>
/// 内置订阅源库（老板 2026-11-26 拍板「只做内置订阅」）：
/// 定期拉公开免费 IPTV 合集（m3u），按台名归一匹配，查重（内置备援表 ContainsUrl + 本库）后
/// 试活（清单 EXTM3U + 分片 TS 魔数）入库垫底；开关默认关，落盘重启仍在。
/// 只做备援候选（动态库），不改频道目录、不进预检常驻轮——消费点 FallbackQueueSource.FromRegistry 末位追加
/// （权重 2 恒垫在内置表观察档之后）。收录范围=已知频道名（刷新时注入目录台名），不盲收陌生台。
/// </summary>
public sealed class SubscriptionSourceStore
{
    /// <summary>一条订阅收录源：台名（目录口径）+ 直连 URL + 可选 UA + 最近试活结论。</summary>
    public sealed record SubEntry(string Name, string Url, string Ua = "", long ProbedUtcMs = 0, bool Alive = false);

    private static readonly Lazy<SubscriptionSourceStore> _shared = new(() => new SubscriptionSourceStore());
    public static SubscriptionSourceStore Shared => _shared.Value;

    /// <summary>内置订阅源（公开免费合集）：vbskycn/iptv 社区维护中文合集（IPv4+IPv6 双份，2026-11-26 实测可达、
    /// 卫视台名中文口径与目录一致；iptv-org cn 全英文台名对不上号不收，fanmingming 沙箱侧不可达不收）。</summary>
    public static readonly string[] Feeds =
    {
        "https://raw.githubusercontent.com/vbskycn/iptv/master/tv/iptv4.m3u",
        "https://raw.githubusercontent.com/vbskycn/iptv/master/tv/iptv6.m3u",
    };

    /// <summary>收录上限：单台最多 4 路（防单台刷屏），全库最多 300 条（防合集爆炸拖慢换源遍历）。</summary>
    public const int MaxPerChannel = 4;
    public const int MaxTotal = 300;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly object _gate = new();
    private readonly Dictionary<string, List<SubEntry>> _byName = new(StringComparer.Ordinal); // 键=归一台名
    private string _path = "";
    public static Action<string>? Reporter { get; set; }

    /// <summary>UI 启动注入目录并载入（与 source_profiles.json 同目录）。</summary>
    public void Load(string dir)
    {
        _path = Path.Combine(dir ?? ".", "subscription_sources.json");
        lock (_gate)
        {
            _byName.Clear();
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<SubEntry[]>(json ?? "[]") ?? Array.Empty<SubEntry>();
                foreach (var e in loaded)
                {
                    if (string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.Url)) continue;
                    AddLocked(NormalizeName(e.Name), e);
                }
            }
            catch (Exception ex) { Reporter?.Invoke($"[源订阅] 载入失败按空库处理：{ex.Message}"); }
        }
    }

    /// <summary>该频道是否有订阅收录的备援源。</summary>
    public bool Has(string channelName)
    {
        lock (_gate) { return _byName.ContainsKey(NormalizeName(channelName)); }
    }

    /// <summary>该频道的全部订阅收录源（收录序）；无收录返回空表。</summary>
    public IReadOnlyList<SubEntry> GetAll(string channelName)
    {
        lock (_gate)
        {
            return _byName.TryGetValue(NormalizeName(channelName), out var list)
                ? list.ToArray()
                : Array.Empty<SubEntry>();
        }
    }

    /// <summary>本库 URL 查重（含内置表口径由调用方前置——Refresh 内已并查）。</summary>
    public bool ContainsUrl(string url)
    {
        lock (_gate) { return _byName.Values.Any(l => l.Any(e => string.Equals(e.Url, url, StringComparison.Ordinal))); }
    }

    /// <summary>试活通过的条目入库：查重（内置表+本库）、按台封顶、全库封顶；返回新增条数。</summary>
    public int Ingest(IEnumerable<(string Name, string Url, string Ua)> probed, long nowMs)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var (name, url, ua) in probed)
            {
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                if (FallbackSourceRegistry.ContainsUrl(url)) continue; // 内置表已收录，免双探
                var key = NormalizeName(name);
                if (_byName.Values.Sum(l => l.Count) >= MaxTotal) break;
                if (!_byName.TryGetValue(key, out var list)) { list = new List<SubEntry>(); _byName[key] = list; }
                if (list.Any(e => string.Equals(e.Url, url, StringComparison.Ordinal))) continue;
                if (list.Count >= MaxPerChannel) continue;
                list.Add(new SubEntry(name, url, ua, nowMs, Alive: true));
                added++;
            }
            if (added > 0) SaveLocked();
        }
        return added;
    }

    /// <summary>解析 m3u 文本为（台名, URL）对：EXTINF 行取逗号后台名，其后首个非注释行为地址；坏段跳过。</summary>
    public static List<(string Name, string Url)> ParseM3U(string text)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(text)) return result;
        string? pendingName = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                pendingName = comma >= 0 && comma < line.Length - 1 ? line[(comma + 1)..].Trim() : null;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                if (pendingName is not null && Uri.TryCreate(line, UriKind.Absolute, out var u)
                    && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                    result.Add((pendingName, line));
                pendingName = null;
            }
        }
        return result;
    }

    /// <summary>全量刷新：拉合集 → 按已知台名过滤 → 查重 → 并发试活（上限 4）→ 入库。返回新增条数。</summary>
    public async Task<int> RefreshAsync(
        IEnumerable<string> knownChannelNames, string ua, CancellationToken ct = default)
    {
        var known = new HashSet<string>(knownChannelNames.Select(NormalizeName), StringComparer.Ordinal);
        var candidates = new List<(string Name, string Url, string Ua)>();
        foreach (var feed in Feeds)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = await Http.GetStringAsync(feed, ct).ConfigureAwait(false); }
            catch (Exception ex) { Reporter?.Invoke($"[源订阅] 合集拉取失败跳过：{feed} → {ex.Message}"); continue; }
            candidates.AddRange(ParseM3U(text)
                .Where(p => known.Contains(NormalizeName(p.Name)))
                .Where(p => !FallbackSourceRegistry.ContainsUrl(p.Url) && !ContainsUrl(p.Url))
                .Select(p => (p.Name, p.Url, ua)));
        }
        // 并发试活（上限 4）：清单 EXTM3U + 分片 TS 魔数双闸
        var alive = new ConcurrentBag<(string, string, string)>();
        using var gate = new SemaphoreSlim(4);
        var tasks = candidates.Select(async c =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                if (await ProbeAsync(c.Item2, c.Item3, ct).ConfigureAwait(false)) alive.Add(c);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* 单条失败照常跳过 */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return Ingest(alive, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>试活单路：清单须 EXTM3U，首个分片首字节须 TS 魔数 0x47。</summary>
    public static async Task<bool> ProbeAsync(string url, string ua, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(ua)) req.Headers.UserAgent.ParseAdd(ua);
            using var pl = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!pl.IsSuccessStatusCode) return false;
            var body = await pl.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!body.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return false;
            var seg = body.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (seg is null) return false;
            if (!Uri.TryCreate(seg, UriKind.Absolute, out var segUri))
                seg = new Uri(new Uri(url), seg).ToString();
            using var segReq = new HttpRequestMessage(HttpMethod.Get, seg);
            if (!string.IsNullOrEmpty(ua)) segReq.Headers.UserAgent.ParseAdd(ua);
            segReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 524287);
            using var segRes = await Http.SendAsync(segReq, ct).ConfigureAwait(false);
            if (!segRes.IsSuccessStatusCode) return false;
            var buf = new byte[1];
            var segStream = await segRes.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var n = await segStream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            return n == 1 && buf[0] == 0x47;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>台名归一（与 ChannelPrecheck.NormalizeName 同口径）：去空格后精确匹配。</summary>
    public static string NormalizeName(string name) =>
        (name ?? "").Replace(" ", "").Replace("　", "");

    private void AddLocked(string key, SubEntry e)
    {
        if (!_byName.TryGetValue(key, out var list)) { list = new List<SubEntry>(); _byName[key] = list; }
        if (!list.Any(x => string.Equals(x.Url, e.Url, StringComparison.Ordinal))) list.Add(e);
    }

    private void SaveLocked()
    {
        if (_path.Length == 0) return;
        try
        {
            var snapshot = _byName.SelectMany(kv => kv.Value).ToArray();
            var json = JsonSerializer.Serialize(snapshot);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Reporter?.Invoke($"[源订阅] 落盘失败：{ex.Message}"); }
    }
}
