using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TaoLive.BLL;

/// <summary>预热落盘条目：键=清单/换票地址，值=响应体与取回时刻、保鲜期（毫秒）。</summary>
public class WarmupEntry
{
    public string Body { get; set; } = "";
    public long AtMs { get; set; }          // 取回时刻（Unix 毫秒，跨启动可比）
    public long FreshnessMs { get; set; }   // 保鲜期（按源取 WarmupPolicy 节拍）
}

/// <summary>
/// A 类第6项·预热落盘层（细案 §四）：
/// - 目录：应用私有目录 /files/warmup/，与 source_health.json 并列；
/// - playlist_cache.json：键=清单 URL，值=响应体+时刻+保鲜期；上限 128 条，超限清最旧；
/// - 播放清单是善变数据——落盘只作「起播第一帧加速」，起流校验失败立即现场重取，
///   绝不把旧清单当唯一路径（铁律同 PlaylistPrefetch 注释口径）；
/// - 写入：脏标记 + 3 秒节流 + 原子替换（临时文件+Move），复用健康账同款模式。
/// 纯逻辑可测：时钟与落盘目录注入。
/// </summary>
public class WarmupStore
{
    private const int Capacity = 128;
    private const long ThrottleMs = 3_000;

    private readonly string? _dir;
    private readonly Dictionary<string, WarmupEntry> _cache = new(StringComparer.Ordinal);
    private bool _dirty;
    private bool _loadCompleted; // v28巡逻修：盘上旧账是否已并入内存。false=未确认（灌回未开始或未完成），落盘前须先补并
    private long? _lastFlushMs; // null=从未落盘（首次必落，不吃节流）
    private readonly object _gate = new();

    public WarmupStore(string? dir) => _dir = dir;

    private static string CachePath(string dir) => Path.Combine(dir, "playlist_cache.json");

    /// <summary>锁内补并盘上旧账：只补内存没有的键（旧值不覆盖新值），消「落盘快照缺未灌回条目→覆盖盘上旧账」的丢失窗口。
    /// 损坏文件按无缓存处理（绝不挡起播）；无论成败，完成后 _loadCompleted=true（同一生命周期内补并一次即可）。</summary>
    private void MergeDiskLocked()
    {
        try
        {
            var path = CachePath(_dir!);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, WarmupEntry>>(File.ReadAllText(path));
                if (loaded != null)
                    foreach (var kv in loaded)
                        if (!_cache.ContainsKey(kv.Key)) _cache[kv.Key] = kv.Value;
            }
        }
        catch { /* 损坏即弃：缓存只加速，不承担正确性 */ }
        finally { _loadCompleted = true; }
    }

    /// <summary>启动灌回：读盘。v28巡逻修：全程锁内灌回（消字典并发），只补内存没有的键（旧值不覆盖新值）。</summary>
    public void Load()
    {
        if (string.IsNullOrEmpty(_dir)) return;
        lock (_gate) MergeDiskLocked();
    }

    /// <summary>写入缓存条目（含节流落盘）。nowMs=Unix 毫秒；dir 未注入时只留内存。</summary>
    public void Put(string url, string body, long freshnessMs, long nowMs)
    {
        if (string.IsNullOrEmpty(url) || body is null) return;
        lock (_gate)
        {
            _cache[url] = new WarmupEntry { Body = body, AtMs = nowMs, FreshnessMs = freshnessMs };
            // 超限清最旧
            if (_cache.Count > Capacity)
            {
                foreach (var old in _cache.OrderBy(kv => kv.Value.AtMs).Take(_cache.Count - Capacity).ToList())
                    _cache.Remove(old.Key);
            }
            _dirty = true;
        }
        Flush(nowMs);
    }

    /// <summary>读缓存：命中且在保鲜期内才返回（过期一律现场重取）。</summary>
    public string? Get(string url, long nowMs)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(url, out var e)) return null;
            if (nowMs - e.AtMs > e.FreshnessMs) return null; // 保鲜期外=没有
            return e.Body;
        }
    }

    /// <summary>节流落盘：3 秒内重复写只落一次盘；原子替换（临时文件+Move）。</summary>
    public void Flush(long nowMs)
    {
        string body;
        lock (_gate)
        {
            if (!_loadCompleted) MergeDiskLocked(); // v28巡逻修：灌回未完成时先就地补并盘上旧账再落盘——快照永不缺盘上条目，覆盖丢失窗口消除
            if (!_dirty || (_lastFlushMs is long last && nowMs - last < ThrottleMs)) return;
            _dirty = false;
            _lastFlushMs = nowMs;
            body = JsonSerializer.Serialize(_cache);
        }
        if (string.IsNullOrEmpty(_dir)) return;
        var dst = CachePath(_dir);
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = CachePath(_dir) + ".tmp";
            File.WriteAllText(tmp, body);
            File.Move(tmp, dst, overwrite: true); // 原子替换一步到位（v28：消除 Delete→Move 之间文件不存在的读空窗口）
        }
        catch { /* 落盘失败不挡播放：缓存只加速 */ }
    }

    /// <summary>缓存条目数（对账用）。</summary>
    public int Count { get { lock (_gate) return _cache.Count; } }
}
