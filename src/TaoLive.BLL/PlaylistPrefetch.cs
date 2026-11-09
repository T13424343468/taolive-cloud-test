using System.Collections.Concurrent;

namespace TaoLive.BLL;

/// <summary>
/// 清单预取缓存（v1.0.13 老板 15:41 拍板「能压的都要压」+ 15:42「预热后台可以不用那么快」）：
/// 预热循环顺路把各路源的播放清单抓回缓存，换台时变体发现/起流直接读缓存，
/// 不再现场等网络——切台出画面更快。TTL 内复用，超时即弃、不判生死（起播兜底照走原链路）。
/// 只做加速，绝不当唯一路径：缓存未命中一律按原路现场抓取，绝不把台连死。
/// </summary>
public static class PlaylistPrefetch
{
    /// <summary>清单缓存有效期（毫秒）。预热一轮约 1~2 分钟，90 秒取中间值保证新鲜。</summary>
    public const int TtlMs = 90_000;

    /// <summary>条目上限防膨胀；超限整表清空（热台会随下一轮预热自动回填）。</summary>
    public const int MaxEntries = 64;

    private static readonly ConcurrentDictionary<string, (string Body, long AtMs)> _cache = new();

    /// <summary>取缓存：命中且未过期返回 true；过期/未命中返回 false 并顺手清掉旧条目。</summary>
    public static bool TryGet(string? url, long nowMs, out string body)
    {
        body = string.Empty;
        if (string.IsNullOrEmpty(url)) return false;
        if (_cache.TryGetValue(url, out var hit) && nowMs - hit.AtMs < TtlMs)
        {
            body = hit.Body;
            return true;
        }
        _cache.TryRemove(url, out _);
        return false;
    }

    /// <summary>写入缓存：空地址/空内容拒收；超上限清空整表兜底。</summary>
    public static void Put(string? url, string? body, long nowMs)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(body)) return;
        if (_cache.Count >= MaxEntries) _cache.Clear();
        _cache[url] = (body, nowMs);
    }

    /// <summary>清空缓存（测试与状态复位用）。</summary>
    public static void Clear() => _cache.Clear();
}
