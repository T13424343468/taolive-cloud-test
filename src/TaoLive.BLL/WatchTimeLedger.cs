using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TaoLive.BLL;

/// <summary>
/// vc53 任务10·播放时长画像（老板 2026-11-14 12:21 批次合并令，纯本地零网络）：
/// - 每台累计观看毫秒账（EpgKey 一格），数据仅供后续排序/推荐用；
/// - 只做「票+清单禁段预囤」口径：预取票据、预热清单产生的未观看时段一律不入账，
///   调用方在画面真实可见期间记账（首帧后起、切台/离开时封账）；
/// - 落盘：watchtime.json，脏标记+3 秒节流+tmp+move 原子替换（全线家法，WarmupStore 同款）；
///   目录未注入只留内存。损坏文件按零账处理（画像不承担正确性，绝不挡播放）。
/// 纯逻辑可测：节流时钟由调用方传 Unix 毫秒。
/// </summary>
public class WatchTimeLedger
{
    private const long ThrottleMs = 3_000;

    private readonly string? _dir;
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private bool _dirty;
    private long? _lastFlushMs;
    private readonly object _gate = new();

    public WatchTimeLedger(string? dir) => _dir = dir;

    private static string PathOf(string dir) => Path.Combine(dir, "watchtime.json");

    /// <summary>启动灌回：读盘。损坏按零账（画像只加速排序）；盘上较小值不覆盖内存较大值（防旧快照回退）。</summary>
    public void Load()
    {
        if (string.IsNullOrEmpty(_dir)) return;
        lock (_gate)
        {
            try
            {
                var path = PathOf(_dir!);
                if (!File.Exists(path)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
                if (loaded == null) return;
                foreach (var kv in loaded)
                {
                    _totals.TryGetValue(kv.Key, out var cur);
                    if (kv.Value > cur) _totals[kv.Key] = kv.Value;
                }
            }
            catch { /* 损坏即弃：画像不承担正确性 */ }
        }
    }

    /// <summary>记一段观看时长（毫秒）。非法段（非正/空键）不入账——禁段预囤口径在调用方，本层只拒脏数据。</summary>
    public void Record(string epgKey, long deltaMs, long nowMs)
    {
        if (string.IsNullOrEmpty(epgKey) || deltaMs <= 0) return;
        lock (_gate)
        {
            _totals.TryGetValue(epgKey, out var cur);
            _totals[epgKey] = cur + deltaMs;
            _dirty = true;
        }
        Flush(nowMs);
    }

    /// <summary>某台累计观看毫秒（未知台=0）。</summary>
    public long TotalMs(string epgKey)
    {
        lock (_gate) return _totals.TryGetValue(epgKey ?? "", out var v) ? v : 0;
    }

    /// <summary>观看时长降序台账快照（画像消费口：排序/推荐）。空表返回空列表。</summary>
    public List<(string EpgKey, long TotalMs)> Snapshot()
    {
        lock (_gate)
            return _totals.OrderByDescending(kv => kv.Value)
                .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>节流落盘：3 秒窗口内重复写只落一次盘；tmp+move 原子替换（防断电整本清零）。</summary>
    public void Flush(long nowMs)
    {
        string body;
        lock (_gate)
        {
            if (!_dirty || (_lastFlushMs is long last && nowMs - last < ThrottleMs)) return;
            _dirty = false;
            _lastFlushMs = nowMs;
            body = JsonSerializer.Serialize(_totals);
        }
        if (string.IsNullOrEmpty(_dir)) return;
        try
        {
            Directory.CreateDirectory(_dir!);
            var tmp = PathOf(_dir!) + ".tmp";
            File.WriteAllText(tmp, body);
            File.Move(tmp, PathOf(_dir!), overwrite: true);
        }
        catch { /* 落盘失败不挡播放：画像只加速 */ }
    }

    /// <summary>立落（退出/关键点）：无视节流窗口，有脏必落盘（防进程先死文件没写完）。</summary>
    public void FlushNow() => Flush(long.MaxValue);
}
