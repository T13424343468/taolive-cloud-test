using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TaoLive.BLL;

// ============ 道映 v1.0.13·缓存分层（老板 2026-11-08 15:50 拍板「不变化的可以持久化」）============
// 分层口径：不变化的持久化（档位目录，24 小时）；缓变的内存缓存（清单 90 秒随预热刷新）；
// 善变的永不缓存（播放清单/分片现场拿最新）。本类只落档位目录（各台总清单），绝不落播放清单。

/// <summary>档位目录持久化：各台总清单（#EXT-X-STREAM-INF 变体表）基本不变——
/// 预热抓到后写本地文件，重启电视免等网络直接读；24 小时过期重抓，抓到即刷新落盘。</summary>
public sealed class VariantTableStore
{
    /// <summary>落盘有效期：24 小时。过期即弃（预热循环很快重抓刷新），绝不把过时档位当唯一路径。</summary>
    public const long TtlMs = 24 * 60 * 60 * 1000L;

    private const int FlushDebounceMs = 3000;

    private sealed class Record
    {
        public string Url { get; set; } = "";
        public string Body { get; set; } = "";
        public long AtMs { get; set; }
    }

    /// <summary>全局实例（UI 启动时注入应用私有目录；BLL 测试直接 new 独立实例）。</summary>
    public static VariantTableStore? Shared;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ConcurrentDictionary<string, Record> _entries = new();
    private System.Threading.Timer? _flushTimer;
    private bool _dirty;

    public VariantTableStore(string dir)
    {
        _path = Path.Combine(dir ?? ".", "variant_table_store.json");
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<Record[]>(File.ReadAllText(_path)) ?? Array.Empty<Record>();
            foreach (var r in list)
                if (!string.IsNullOrEmpty(r?.Url) && !string.IsNullOrEmpty(r.Body)) _entries[r.Url] = r;
        }
        catch (Exception ex) { BllLog.Warn("VariantTableStore", $"档位目录落盘载入失败，按空表起用：{ex.Message}"); }
    }

    /// <summary>读档位目录：命中且未过期返回 true；过期即弃交回预热重抓（不判生死）。</summary>
    public bool TryGet(string? url, long nowMs, out string body)
    {
        body = string.Empty;
        if (string.IsNullOrEmpty(url)) return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(url, out var hit)) return false;
            if (nowMs - hit.AtMs >= TtlMs) { _entries.TryRemove(url, out _); MarkDirty(); return false; }
            body = hit.Body;
            return true;
        }
    }

    /// <summary>写入档位目录：空值拒收；写后按节流落盘。</summary>
    public void Put(string? url, string? body, long nowMs)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(body)) return;
        lock (_gate)
        {
            _entries[url] = new Record { Url = url, Body = body, AtMs = nowMs };
            if (_entries.Count > 128) _entries.Clear(); // 上限兜底：预热会很快回填热台
            MarkDirty();
        }
    }

    /// <summary>立即原子落盘（临时文件+替换）；退出/暂停时调用。外部落盘后回收定时器，杜绝后续写丢盘。</summary>
    public void FlushNow()
    {
        lock (_gate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            _dirty = false;
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries.Values));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex) { BllLog.Warn("VariantTableStore", $"档位目录落盘失败下个周期再试：{ex.Message}"); }
        }
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_flushTimer != null) return;
            _flushTimer = new System.Threading.Timer(_ =>
            {
                bool fire;
                lock (_gate)
                {
                    // 定时器触发即回收自身：无论是否到点落盘，下次脏标记都能重新排程，绝不丢盘
                    _flushTimer?.Dispose();
                    _flushTimer = null;
                    fire = _dirty;
                    _dirty = false;
                }
                if (fire) FlushNow();
            }, null, FlushDebounceMs, Timeout.Infinite);
        }
    }
}
