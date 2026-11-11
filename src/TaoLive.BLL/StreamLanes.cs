using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

// ============ 道映 v1.0.8 第19项·通道化架构（用户 2026-11-08 00:01 拍板）============
// 取流/取票/解密/校验等所有接入方式统一抽象为可插拔「通道」：现有来源各注册为独立通道，
// 每个通道自带探测、取流、校验与失败特征；每个频道按实际探测结果绑定通道路由
// （首选→备选→兜底自有 m3u）；路由表数据驱动、可经热更新下发；全部通道失效才明确提示。
// 纯逻辑可测；真实取流仍收口在 StreamResolver（既有取票实现注册为内置通道）。

/// <summary>通道（接入方式）抽象：自带探测与取流，失败有明确特征。</summary>
public interface IStreamLane
{
    /// <summary>通道标识（数据驱动路由表引用；热更新可下发新路由指向新通道）。</summary>
    string LaneId { get; }

    /// <summary>该通道能否处理此频道（按 SourceId/URL 特征判断，不硬编码机型）。</summary>
    bool CanHandle(Channel ch);

    /// <summary>取流：产出可播 URL；失败返回 null（失败特征由调用方按候选序降级）。</summary>
    Task<string?> FetchAsync(Channel ch);
}

/// <summary>
/// 通道路由表（数据驱动，可热更新覆盖）：频道键 → 有序通道候选（首选→备选→兜底）。
/// 未登记的频道按「内置注册顺序」尝试（CanHandle 命中者优先）。
/// </summary>
public class LaneRouter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<string>> _routes = new();   // EpgKey → 通道候选序
    private readonly List<IStreamLane> _lanes = new();
    private readonly SourceFailover _failover = new();                    // 自愈1：切换与回主源共用状态

    /// <summary>注册通道（幂等：同 LaneId 不重复）。</summary>
    public void Register(IStreamLane lane)
    {
        if (lane is null) return;
        lock (_lock)
        {
            if (_lanes.Any(l => l.LaneId == lane.LaneId)) return;
            _lanes.Add(lane);
        }
    }

    /// <summary>热更新/预检下发路由（整表覆盖，坏表不落盘）。</summary>
    public void SetRoute(string channelKey, IEnumerable<string> laneIdsInOrder)
    {
        if (string.IsNullOrEmpty(channelKey)) return;
        lock (_lock) _routes[channelKey] = laneIdsInOrder.ToList();
    }

    public IReadOnlyList<string> RouteOf(string channelKey)
    {
        lock (_lock)
        {
            return _routes.TryGetValue(channelKey, out var r) ? r.ToList() : new List<string>();
        }
    }

    /// <summary>按路由取该台当前应使用的通道（候选序号经自愈1备援状态裁决）。</summary>
    public IStreamLane? CurrentLane(string channelKey, Channel ch)
    {
        var candidates = RouteOf(channelKey);
        if (candidates.Count == 0)
        {
            lock (_lock) candidates = _lanes.Where(l => l.CanHandle(ch)).Select(l => l.LaneId).ToList();
        }
        if (candidates.Count == 0) return null;
        var idx = Math.Clamp(_failover.CurrentIndex(channelKey), 0, candidates.Count - 1);
        var laneId = candidates[idx];
        lock (_lock) return _lanes.FirstOrDefault(l => l.LaneId == laneId);
    }

    /// <summary>取流失败：降级到下一候选；返回降级后的通道（null=全部候选耗尽，UI 明确提示）。</summary>
    public IStreamLane? AdvanceOnFailure(string channelKey, Channel ch)
    {
        var candidates = RouteOf(channelKey);
        if (candidates.Count == 0)
            lock (_lock) candidates = _lanes.Where(l => l.CanHandle(ch)).Select(l => l.LaneId).ToList();
        if (candidates.Count == 0) return null; // 无候选=耗尽
        var prev = _failover.CurrentIndex(channelKey);
        var next = _failover.NextCandidateOnFailure(channelKey, candidates.Count);
        // 已在最后一个候选上再失败 → 全部通道耗尽，明确失败（UI 提示不静默）
        if (next >= candidates.Count || (prev == next && next == candidates.Count - 1)) return null;
        lock (_lock) return _lanes.FirstOrDefault(l => l.LaneId == candidates[next]);
    }

    /// <summary>当前候选成功回灌（自愈1：备援连续成功达标自动回主通道）。</summary>
    public void MarkSuccess(string channelKey) => _failover.OnSuccess(channelKey);

    /// <summary>该台当前候选序号（0=首选通道）。</summary>
    public int CurrentIndex(string channelKey) => _failover.CurrentIndex(channelKey);
}
