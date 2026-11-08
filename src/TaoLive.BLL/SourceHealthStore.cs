using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TaoLive.BLL;

// ============ 道映 v1.0.10 第①项·源健康账持久化（用户 2026-11-08 12:34 拍板）============
// 「按这个改」：预检确认可用的源连同验证时间落盘本地，重启仍在；换台优先直连已验证源；
// 账本随预检循环与播放实测持续回写，连续失败自动淘汰；与卡死自救共用同一份账。

/// <summary>持久化条目：键 = SourceId:EpgKey，时间 = Unix 毫秒（跨启动可比）。</summary>
public class SourceHealthRecord
{
    public string K { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string EpgKey { get; set; } = "";
    public string State { get; set; } = "";   // Alive / Dead
    public long UtcMs { get; set; }           // 最近核验时间（Unix 毫秒）
}

/// <summary>
/// 源健康账本地存储（BLL 纯逻辑，文件路径由 UI 传入应用私有目录）：
/// - Load：启动载入并按过期规则清理（Alive 超 24 小时丢、Dead 超 30 分钟丢）；
/// - MarkDirty：脏标记 + 3 秒节流后台落盘（写入绝不阻塞播放线程）；
/// - FlushNow：立即原子落盘（临时文件 + 替换），退出/暂停时调用。
/// 多线程安全：读写均经锁；条目本体仍由 ChannelPrecheck 的并发字典持有。
/// </summary>
public class SourceHealthStore
{
    /// <summary>Alive 条目保质期：超过视为陈旧丢弃（预检循环会很快重探刷新）。</summary>
    public const long AliveTtlMs = 24 * 60 * 60 * 1000L;

    /// <summary>Dead 条目保质期：留半小时供列表延续「无信号」展示，过期交回预检重探。</summary>
    public const long DeadTtlMs = 30 * 60 * 1000L;

    private const int FlushDebounceMs = 3000;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ConcurrentDictionary<string, SourceHealthEntry> _owner;
    private System.Threading.Timer? _flushTimer;
    private bool _dirty;

    /// <summary>载入/落盘的条目集合（ChannelPrecheck 的健康表本体）。</summary>
    internal ConcurrentDictionary<string, SourceHealthEntry> Entries => _owner;

    /// <summary>dir：应用私有目录；entries：健康表本体（先建后注均可，Attach 时再灌）。</summary>
    public SourceHealthStore(string dir, ConcurrentDictionary<string, SourceHealthEntry>? entries = null)
    {
        _path = Path.Combine(dir ?? ".", "source_health.json");
        _owner = entries ?? new ConcurrentDictionary<string, SourceHealthEntry>();
    }

    /// <summary>启动载入：按过期规则清理后灌入健康表（不覆盖会话内已有条目）。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var records = JsonSerializer.Deserialize<SourceHealthRecord[]>(json ?? "[]") ?? Array.Empty<SourceHealthRecord>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var r in records)
            {
                if (r is null || string.IsNullOrEmpty(r.K) || string.IsNullOrEmpty(r.SourceId)) continue;
                var fresh = r.State switch
                {
                    "Alive" => now - r.UtcMs <= AliveTtlMs,
                    "Dead" => now - r.UtcMs <= DeadTtlMs,
                    _ => false,
                };
                if (!fresh) continue; // 过期丢弃，交回预检重探
                _owner.TryAdd(r.K, new SourceHealthEntry
                {
                    SourceId = r.SourceId,
                    EpgKey = r.EpgKey,
                    State = r.State == "Alive" ? ChannelHealth.Alive : ChannelHealth.Dead,
                    CheckedAtMs = r.UtcMs, // 统一 Unix 毫秒时间基，跨启动可比
                });
            }
        }
        catch
        {
            // 账本损坏不致命：当无账处理，预检循环很快重建
        }
    }

    /// <summary>脏标记：3 秒内合并多次写入，后台线程统一落盘（节流防频繁 IO）。</summary>
    public void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_flushTimer != null) return;
            _flushTimer = new System.Threading.Timer(_ =>
            {
                FlushNow();
                lock (_gate) _flushTimer?.Dispose();
                lock (_gate) _flushTimer = null;
            }, null, FlushDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>立即落盘：临时文件写入后替换，进程被杀也不会损坏旧账。无条件全量写出（退出/暂停时也能把手工修订一并落账）。</summary>
    public void FlushNow()
    {
        SourceHealthRecord[] snapshot;
        lock (_gate)
        {
            _dirty = false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            snapshot = _owner
                .Select(kv => new SourceHealthRecord
                {
                    K = kv.Key,
                    SourceId = kv.Value.SourceId,
                    EpgKey = kv.Value.EpgKey,
                    State = kv.Value.State == ChannelHealth.Alive ? "Alive" : "Dead",
                    UtcMs = kv.Value.CheckedAtMs,
                })
                .Where(r => r.State == "Alive" ? now - r.UtcMs <= AliveTtlMs : now - r.UtcMs <= DeadTtlMs)
                .ToArray();
        }
        try
        {
            var json = JsonSerializer.Serialize(snapshot);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch
        {
            // 落盘失败静默：下轮脏标记再试，绝不影响播放
        }
    }
}
