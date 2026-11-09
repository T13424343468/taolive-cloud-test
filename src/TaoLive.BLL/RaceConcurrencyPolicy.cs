using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// 竞速并发收缩（冻结稿场景⑫，方案 B 补充）：竞速并发路数按内存三档自适应收缩，
/// 与 SelfHeal.ResourceProfile 同口径分档（充裕 ≥800MB / 紧张 400–800MB / 极低 &lt;400MB）：
/// 充裕 3 路、紧张 2 路、极低 2 路——低配设备预检/内存占用不升（验收口径⑫），
/// 探测本就是 2KB 轻探，收缩只降并发不降可用性（30 秒同源间隔护栏不变）。
/// 内存未知（≤0）按充裕兜底 3 路（ResourceProfile 既有约定；未知不误伤）。
/// </summary>
public static class RaceConcurrencyPolicy
{
    /// <summary>分档阈值（与 SelfHeal.ResourceProfile 一致）：低于此字节数为极低档。</summary>
    public const long VeryLowBytes = 400L * 1024 * 1024;

    /// <summary>分档阈值（与 SelfHeal.ResourceProfile 一致）：低于此字节数为紧张档。</summary>
    public const long TightBytes = 800L * 1024 * 1024;

    /// <summary>充裕档并发路数（与 RacePreconnector.MaxParallel 同值）。</summary>
    public const int FullParallel = 3;

    /// <summary>紧张/极低档收缩后并发路数。</summary>
    public const int ShrunkParallel = 2;

    /// <summary>按可用内存档位给竞速并发上限：充裕/未知 3 路，紧张/极低 2 路。</summary>
    public static int Cap(long availBytes) =>
        (availBytes > 0 && availBytes < TightBytes) ? ShrunkParallel : FullParallel;

    /// <summary>H 连接复用·内存档自适应（冻结稿 H）：取流连接池单域并发上限按可用内存自适应——
    /// 低配设备（紧张/极低档，与竞速同口径分档）缩到 8，防单域 16 连接挤占低配内存；充裕/未知维持 16 不降可用性。</summary>
    public const int FullPoolPerServer = 16;

    /// <summary>低配档连接池单域并发上限。</summary>
    public const int LowMemPoolPerServer = 8;

    /// <summary>按可用内存给连接池单域并发上限：充裕/未知 16，紧张/极低 8（availBytes≤0 视为未知不误伤）。</summary>
    public static int PoolPerServer(long availBytes) =>
        (availBytes > 0 && availBytes < TightBytes) ? LowMemPoolPerServer : FullPoolPerServer;

    /// <summary>
    /// 纯逻辑截选：候选按评分降序取前 cap 路（cap 由 <see cref="Cap"/> 给出）；
    /// 评分缺失（null）按 0 兜底排序，排序稳定性与 RacePreconnector.Pick 口径一致——
    /// 调用方把截选结果交给 Pick 仍会再按 30 秒间隔过滤，本方法只做「低配少并发」这一件事。
    /// </summary>
    public static List<Channel> Shrink(IEnumerable<Channel> candidates, Func<Channel, double?> score, int cap)
    {
        if (candidates is null) return new List<Channel>();
        if (cap >= RacePreconnector.MaxParallel) return candidates.ToList();
        return candidates
            .Select(c => (Ch: c, S: score(c) ?? 0.0))
            .OrderByDescending(x => x.S)
            .Take(Math.Max(0, cap))
            .Select(x => x.Ch)
            .ToList();
    }
}
