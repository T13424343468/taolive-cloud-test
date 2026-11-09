using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// 跨源兜底链（冻结稿 v3.1，老板追加令「网络优化跨源兜底链条都要做」2026-11-09）：
/// 每频道按摸底健康度排兜底链——只排官方源，央视频不再是唯一支柱，
/// 同名台的全部官方源都进链参选；链序=健康评分降序（未测记 0 分，稳定排序不折腾），
/// 拉黑观察期（A 源级死判）内的整段跳过——死源快认输，绝不空转。
/// 老板修正令（2026-11-09）：链内不排用户自有源（用户暂无自有源；导入功能保留，
/// 将来自动进链），自有源兜底仍走现行备援链路，本方法不做自有源前置。
/// 纯逻辑产出链序，执行走 StreamResolver.ResolveWithChainAsync（既有 SourceFailover 备援口径：
/// 备援连续成功两轮自动回链首），链尾全败交回「全源不可达」现行收口。
/// </summary>
public static class FallbackChainPlanner
{
    /// <summary>用户自有源标识（App 内 user-m3u 口径）：由现行备援链路承接，不进本兜底链。</summary>
    public const string UserSourceId = "user-m3u";

    /// <summary>
    /// 产出该频道的兜底链：current（主源）与 sameNameCandidates（UI 注入的同名台跨源候选）合并、
    /// 去重（同一来源适配器只留一个候选）、剔除非法/拉黑源与用户自有源（自有源由现行备援链路承接）；
    /// 其余按评分降序。
    /// score 传 null 时全部按未测 0 分保持原序；sourceBlackedOut 传 null 视为无拉黑。
    /// </summary>
    public static List<Channel> Plan(
        Channel? current,
        IEnumerable<Channel>? sameNameCandidates,
        Func<Channel, double?>? score,
        Func<string, bool>? sourceBlackedOut)
    {
        var merged = new List<Channel>();
        if (current is not null) merged.Add(current);
        if (sameNameCandidates is not null) merged.AddRange(sameNameCandidates);

        // 去重：同一来源适配器只留一个候选（先到先得，保持注入顺序）；非法流地址整条剔除（取流总闸同口径）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<Channel>();
        foreach (var c in merged)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.SourceId)) continue;
            if (string.Equals(c.SourceId, UserSourceId, StringComparison.Ordinal)) continue; // 自有源由现行备援链路承接，不进本链
            if (!StreamUrlGuard.IsValid(c.StreamUrl)) continue; // 恶意/坏地址不进链（总闸前先拦，省一次总闸往返）
            if (!seen.Add(c.SourceId)) continue;                // 一源一候选
            if (sourceBlackedOut is not null)
            {
                bool blacked;
                try { blacked = sourceBlackedOut(c.SourceId); }
                catch (Exception) { blacked = false; }           // 判定异常按未拉黑，交回执行层兜底
                if (blacked) continue;                           // 拉黑观察期：整段跳过，不空转
            }
            chain.Add(c);
        }

        // 官方源按健康评分降序（未测 0 分，稳定排序——LINQ OrderByDescending 保序，同分维持供给序）
        return chain.OrderByDescending(c => score?.Invoke(c) ?? 0.0).ToList();
    }
}
