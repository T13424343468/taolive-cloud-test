using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace TaoLive.BLL;

/// <summary>
/// vc45②：公开免费渠道发现器（搜源管线 discover 注入的实现件）——
/// 拉公开免费 m3u 聚合清单，按台名归一过滤出候选交给 SourceScout 静默校验；
/// 清单缓存 10 分钟（一轮搜源只拉一次网）；拉不动返回空（本轮不搜，不重试拖死）。
/// 红线口径：仅收录免费直连源，不碰加密保护；验活与评分在管线内收口，本件只做「发现」。
/// </summary>
public static class PublicListDiscover
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>发现 feed：公开免费 m3u 清单（中国列表）。后续按真机反馈增删。</summary>
    private static readonly string[] Feeds =
    {
        "https://iptv-org.github.io/iptv/countries/cn.m3u",
    };

    private static List<ScoutCandidate>? _cache;
    private static long _cacheAtMs;
    private static readonly object _gate = new();
    private const int CacheMs = 10 * 60_000;
    // vc46C single-flight：进行中的拉网任务按 feed 收口（per-key Task 缓存）——此前锁内判空放锁拉网，
    // 并发到期 N 个调用方各拉一遍全表（N 倍带宽+响应放大）；改为首个调用方建 Task，其余 await 同一任务。
    private static readonly Dictionary<string, Task<List<ScoutCandidate>>> _listTasks = new(StringComparer.Ordinal);

    /// <summary>按台名找候选（最多 5 条；归一包含匹配，宁缺毋滥——宽匹配靠校验兜底，污染风险不进库）。</summary>
    public static async Task<List<ScoutCandidate>> FindAsync(string channelName)
    {
        var list = await GetListAsync();
        var key = DynamicSourceStore.Normalize(channelName);
        if (key.Length == 0) return new List<ScoutCandidate>();
        return list.Where(c =>
        {
            var n = DynamicSourceStore.Normalize(c.Name);
            return n.Contains(key, StringComparison.Ordinal) || key.Contains(n, StringComparison.Ordinal) && n.Length >= 2;
        }).Take(5).ToList();
    }

    private static Task<List<ScoutCandidate>> GetListFlightAsync()
    {
        lock (_gate)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_cache != null && nowMs - _cacheAtMs < CacheMs)
                return Task.FromResult(_cache); // 新鲜缓存直接命中
            // 缓存过期：取/建本 feed 的 single-flight 任务（进行中拉网所有并发调用方共用）
            if (_listTasks.TryGetValue("list", out var flight)) return flight;
            flight = Task.Run(FetchFeedsAsync);
            _listTasks["list"] = flight;
            return flight;
        }
    }

    private static async Task<List<ScoutCandidate>> GetListAsync()
    {
        var found = await GetListFlightAsync();
        lock (_gate)
        {
            _cache = found;
            _cacheAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _listTasks.Remove("list"); // vc46C：落袋即清——后续走缓存判定，过期时重建 flight
        }
        return found;
    }

    private static async Task<List<ScoutCandidate>> FetchFeedsAsync()
    {
        var found = new List<ScoutCandidate>();
        foreach (var url in Feeds)
        {
            try
            {
                using var res = await Http.GetAsync(url);
                if (!res.IsSuccessStatusCode) continue;
                var body = await res.Content.ReadAsStringAsync();
                string? name = null;
                foreach (var raw in body.Split('\n'))
                {
                    var t = raw.TrimEnd('\r').Trim();
                    if (t.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = t.IndexOf(',');
                        name = comma >= 0 && comma + 1 < t.Length ? t[(comma + 1)..].Trim() : null;
                    }
                    else if (!string.IsNullOrEmpty(t) && !t.StartsWith("#", StringComparison.Ordinal) && name != null)
                    {
                        found.Add(new ScoutCandidate(name, t));
                        name = null;
                    }
                }
            }
            catch { /* 拉 feed 失败换下一家；全失败=本轮无候选，不拖播放 */ }
        }
        return found;
    }
}
