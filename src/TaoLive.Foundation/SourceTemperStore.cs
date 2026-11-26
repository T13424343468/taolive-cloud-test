using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TaoLive.Foundation;

/// <summary>持久化条目：键 = host（小写），时间 = Unix 毫秒（跨启动可比）。</summary>
public class SourceTemperRecord
{
    public string Host { get; set; } = "";
    public int Rejections { get; set; }        // 累计被拒次数（429/403/被踢）
    public long LastRejectedUtcMs { get; set; } // 最近被拒时刻
    public long ConservativeUntilUtcMs { get; set; } // 保守期截止：期内该 host 只用单路
    public int GoodStreak { get; set; }        // 试探加路平安连续计数（调度依据：好源下次直接试探）
    public long LastGoodUtcMs { get; set; }    // 最近平安时刻
}

/// <summary>
/// 源脾气账·持久化版（vc96，老板 2026-11-26 拍板「每个源的情况作为下次调度的依据，并入健康账口径」）：
/// - per-host 记账：被拒（429/403/被踢）记保守期，期内不并发试探；试探平安记好账，好源下次允许直接试探；
/// - 落盘本地（source_temper.json），重启不清零——下次调度直接读账定打法；
/// - 保守期过期自动失效（交回试探）；好账 7 天不平安即衰减清零（防陈旧画像）；
/// - 写入走脏标记 + 3 秒节流后台落盘（与源健康账同口径，绝不阻塞播放线程）。
/// 线程安全：读写均经锁。
/// </summary>
public sealed class SourceTemperStore
{
    /// <summary>默认保守期：30 分钟（与内存版 TemperLedger 口径一致）。</summary>
    public const int DefaultCooldownMs = 30 * 60 * 1000;

    /// <summary>好账保质期：7 天内无平安记录即清（陈旧画像不赖账）。</summary>
    public const long GoodTtlMs = 7 * 24 * 60 * 60 * 1000L;

    private const int FlushDebounceMs = PersistencePolicy.FlushDebounceMs;

    private readonly object _gate = new();
    private readonly object _ioGate = new(); // vc96 P3：落盘 IO 锁——并发 FlushNow（定时器+退出）不再互踩同一 tmp 文件
    private readonly string _path;
    private readonly Dictionary<string, SourceTemperRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _nowUtcMs;
    private System.Threading.Timer? _flushTimer;

    /// <summary>dir：应用私有目录；nowUtcMs：时间源注入（快测不真等）。</summary>
    public SourceTemperStore(string dir, Func<long>? nowUtcMs = null)
    {
        _path = Path.Combine(dir ?? ".", "source_temper.json");
        _nowUtcMs = nowUtcMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>启动载入：过期条目（保守期已过且好账超保质期）丢弃。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var records = JsonSerializer.Deserialize<SourceTemperRecord[]>(json ?? "[]") ?? Array.Empty<SourceTemperRecord>();
            lock (_gate)
            {
                var now = _nowUtcMs();
                foreach (var r in records)
                {
                    if (r is null || string.IsNullOrEmpty(r.Host)) continue;
                    var conservativeLive = now < r.ConservativeUntilUtcMs;
                    var goodLive = r.GoodStreak > 0 && now - r.LastGoodUtcMs <= GoodTtlMs;
                    if (!conservativeLive && !goodLive) continue; // 双过期：交回试探重摸
                    _records[r.Host] = r;
                }
            }
        }
        catch
        {
            // 账本损坏不致命：当无账处理，试探循环很快重建
        }
    }

    /// <summary>记被拒：累计次数 + 保守期起算（重复被拒顺延），脏标记落盘。</summary>
    public void MarkRejected(string host, int cooldownMs = DefaultCooldownMs)
    {
        if (string.IsNullOrEmpty(host)) return;
        lock (_gate)
        {
            var now = _nowUtcMs();
            if (!_records.TryGetValue(host, out var r))
            {
                r = new SourceTemperRecord { Host = host };
                _records[host] = r;
            }
            r.Rejections++;
            r.LastRejectedUtcMs = now;
            r.ConservativeUntilUtcMs = now + (cooldownMs > 0 ? cooldownMs : DefaultCooldownMs);
            r.GoodStreak = 0; // 被拒清好账：保守从头计
        }
        MarkDirty();
    }

    /// <summary>记平安：试探加路未被拒连续计数（好源下次调度允许直接试探），脏标记落盘。</summary>
    public void MarkGood(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        lock (_gate)
        {
            var now = _nowUtcMs();
            if (!_records.TryGetValue(host, out var r))
            {
                r = new SourceTemperRecord { Host = host };
                _records[host] = r;
            }
            r.GoodStreak++;
            r.LastGoodUtcMs = now;
        }
        MarkDirty();
    }

    /// <summary>该 host 是否仍在保守期（期内不并发试探，只用单路）。</summary>
    public bool IsConservative(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        lock (_gate)
        {
            return _records.TryGetValue(host, out var r) && _nowUtcMs() < r.ConservativeUntilUtcMs;
        }
    }

    /// <summary>好账连续数（0=无好账；调度可据此决定是否跳过试探直接并发）。</summary>
    public int GoodStreakOf(string host)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        lock (_gate)
        {
            if (!_records.TryGetValue(host, out var r) || r.GoodStreak <= 0) return 0;
            return _nowUtcMs() - r.LastGoodUtcMs <= GoodTtlMs ? r.GoodStreak : 0;
        }
    }

    /// <summary>host 取自 URL（解析失败返回原串，账不丢）。</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? u.Host
            : url;

    /// <summary>脏标记：3 秒节流后台落盘（与源健康账同口径）。</summary>
    public void MarkDirty()
    {
        lock (_gate)
        {
            if (_flushTimer != null) return;
            _flushTimer = new System.Threading.Timer(_ =>
            {
                System.Threading.Timer? fired;
                lock (_gate)
                {
                    fired = _flushTimer;
                    _flushTimer = null;
                }
                FlushNow();
                fired?.Dispose();
            }, null, FlushDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>立即落盘：临时文件写入后替换（退出/暂停时调用）。</summary>
    public void FlushNow()
    {
        SourceTemperRecord[] snapshot;
        lock (_gate)
        {
            var now = _nowUtcMs();
            var list = new List<SourceTemperRecord>();
            foreach (var r in _records.Values)
            {
                var conservativeLive = now < r.ConservativeUntilUtcMs;
                var goodLive = r.GoodStreak > 0 && now - r.LastGoodUtcMs <= GoodTtlMs;
                if (conservativeLive || goodLive) list.Add(r);
            }
            snapshot = list.ToArray();
        }
        try
        {
            var json = JsonSerializer.Serialize(snapshot);
            var tmp = _path + ".tmp";
            lock (_ioGate) // vc96 P3：写-替换原子化，防并发落盘互踩 tmp
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch
        {
            // 落盘失败不致命：下轮脏标记再试，不抛出、不影响播放
        }
    }
}
