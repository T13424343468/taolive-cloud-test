using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

// ============ 道映 v1.0.8 第20项·清晰度档位自适应（用户 2026-11-08 00:07 拍板）============
// 不同频道有不同清晰度档位（HLS master playlist 多码率变体）；按设备当前网络与性能实况
// 自适应选择或切换；选择依据=保画面保流畅（第一原则）；现状「看着看着画面卡死」的常见
// 根因之一即码率高于网络实况导致缓冲耗尽冻结——降档是看门狗重连之外的先手自救。
// 全部纯逻辑可测；决策不阻塞播放（调用方在巡检定时器里喂数据、拿决策）。

/// <summary>清晰度变体（master playlist 里的一档）。</summary>
public sealed class QualityVariant
{
    public string Uri { get; init; } = "";
    public long BandwidthBps { get; init; }   // 标称码率（bit/s），0=未知
    public int HeightPx { get; init; }        // 分辨率高（0=未知）
    public string Label { get; init; } = "";  // 展示名（1080P/720P…，未知给空）
}

/// <summary>清晰度设置项：自动（默认）/锁定高档/锁定低档。</summary>
public enum QualityMode { Auto = 0, LockHigh = 1, LockLow = 2 }

/// <summary>
/// HLS master playlist 变体解析：#EXT-X-STREAM-INF:BANDWIDTH=..[,RESOLUTION=WxH] + 下一行 URI。
/// 解析失败/无变体 → 返回空表（调用方按单档流如实播放，不硬造档位）。
/// </summary>
public static class MasterPlaylistParser
{
    public static List<QualityVariant> Parse(string masterBody)
    {
        var list = new List<QualityVariant>();
        if (string.IsNullOrEmpty(masterBody)) return list;
        var lines = masterBody.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
            long bw = 0;
            var m = System.Text.RegularExpressions.Regex.Match(line, "BANDWIDTH=(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) long.TryParse(m.Groups[1].Value, out bw);
            int h = 0;
            var r = System.Text.RegularExpressions.Regex.Match(line, "RESOLUTION=\\d+x(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (r.Success) int.TryParse(r.Groups[1].Value, out h);
            // URI 在下一行非 # 开头的行
            string uri = "";
            for (int j = i + 1; j < lines.Length; j++)
            {
                var nxt = lines[j].Trim();
                if (nxt.Length == 0) continue;
                if (!nxt.StartsWith("#")) { uri = nxt; i = j; }
                break;
            }
            if (uri.Length == 0) continue;
            list.Add(new QualityVariant
            {
                Uri = uri,
                BandwidthBps = bw,
                HeightPx = h,
                Label = h >= 2160 ? "4K" : h >= 1080 ? "1080P" : h >= 720 ? "720P" : h > 0 ? $"{h}P" : "",
            });
        }
        // 同档去重（按带宽从低到高排，方便选档与展示）
        return list.OrderBy(v => v.BandwidthBps).ThenBy(v => v.HeightPx).ToList();
    }
}

/// <summary>
/// 清晰度档位自适应器（单实例跟一个播放会话）：
/// - 起步档保守（宁低勿卡）：网络未知先中低档；有网速实况按网速 80% 余量选档；
/// - 卡顿（缓冲耗尽/停滞）→ 立即降一档；连续平稳 CleanTicksToUpgrade 个周期 → 升一档；
/// - 升档设最小间隔与迟滞（MinUpgradeIntervalTicks），不反复横跳；网速持续差维持低流畅档；
/// - 手动锁定（LockHigh/LockLow）如实执行；锁定高档但网速撑不住 → 降档自救并出提示标志；
/// - 单档流（变体≤1）如实单档，自适应不介入。
/// </summary>
public class QualityGovernor
{
    /// <summary>平稳多少个巡检周期后回升一档（巡检 5 秒 × 3 = 15 秒平稳才升，防横跳）。</summary>
    public const int CleanTicksToUpgrade = 3;

    /// <summary>两次升档之间最小间隔（巡检周期数），降档不受限（卡顿立刻降）。</summary>
    public const int MinUpgradeIntervalTicks = 2;

    /// <summary>选档网速余量：带宽 ≤ 实测网速 × 0.8 才算撑得住。</summary>
    public const double HeadroomFactor = 0.8;

    private readonly List<QualityVariant> _variants;
    private readonly QualityMode _mode;
    private int _index;
    private int _cleanTicks;
    private int _sinceLastUpgrade;
    private bool _lockNoticeShown;

    public QualityGovernor(List<QualityVariant> variants, QualityMode mode, long measuredBps = 0)
    {
        _variants = variants ?? new List<QualityVariant>();
        _mode = mode;
        _index = PickStartIndex(measuredBps);
    }

    /// <summary>变体数≤1：单档流，自适应不介入（IsSingleStream=true）。</summary>
    public bool IsSingleStream => _variants.Count <= 1;

    /// <summary>当前档位序号（0=最低档）。</summary>
    public int Index => _index;

    public QualityVariant Current => _variants.Count == 0
        ? new QualityVariant()
        : _variants[Math.Clamp(_index, 0, _variants.Count - 1)];

    /// <summary>起步选档：锁定低=最低档；锁定高=最高档；自动=网速 80% 余量内最高档，未知网速取中低档。</summary>
    private int PickStartIndex(long measuredBps)
    {
        if (_variants.Count <= 1) return 0;
        switch (_mode)
        {
            case QualityMode.LockLow: return 0;
            case QualityMode.LockHigh: return _variants.Count - 1;
            default:
                if (measuredBps <= 0) return (_variants.Count - 1) / 2; // 网速未知：中位偏低档起步（宁低勿卡）
                long budget = (long)(measuredBps * HeadroomFactor);
                int pick = 0;
                for (int i = 0; i < _variants.Count; i++)
                    if (_variants[i].BandwidthBps > 0 && _variants[i].BandwidthBps <= budget) pick = i;
                return pick;
        }
    }

    /// <summary>
    /// 巡检回灌（调用方每 5 秒喂一次）。stalled=本周期内出现缓冲耗尽/画面停滞。
    /// 返回新档位序号；档位变化时调用方轻重启取流切档（同流内优先）。
    /// </summary>
    public int Observe(bool stalled)
    {
        if (IsSingleStream) return _index;
        _sinceLastUpgrade++;
        if (stalled)
        {
            _cleanTicks = 0;
            if (_index > 0)
            {
                _index--;
                _sinceLastUpgrade = 0;
                if (_mode == QualityMode.LockHigh) _lockNoticeShown = true; // 锁定高档撑不住：降档自救，UI 温和提示
            }
            return _index;
        }
        _cleanTicks++;
        if (_cleanTicks >= CleanTicksToUpgrade &&
            _sinceLastUpgrade >= MinUpgradeIntervalTicks &&
            _index < _variants.Count - 1 &&
            _mode != QualityMode.LockLow)
        {
            _index++;
            _cleanTicks = 0;
            _sinceLastUpgrade = 0;
        }
        return _index;
    }

    /// <summary>锁定高档但网速撑不住（已降档自救）：UI 据此温和提示一次，不重复打扰。</summary>
    public bool ConsumeLockNotice()
    {
        if (!_lockNoticeShown) return false;
        _lockNoticeShown = false;
        return true;
    }
}
