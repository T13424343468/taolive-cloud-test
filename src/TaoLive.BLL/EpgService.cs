using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// EPG（节目单）服务：首版覆盖央视 24 路「当前在播 + 下档预告」，
/// 数据来自央视官网公开节目单页（tv.cctv.com/epg 与直播导视），定时抓取缓存。
/// 其余台在源摸底确认有公开节目单后逐路补（接不进的如实标注）。
/// </summary>
public class EpgService
{
    public record Program(string Title, DateTime Start, DateTime End, bool IsNow);

    private readonly Dictionary<string, List<Program>> _cache = new();

    /// <summary>取某频道当前在播与下档；无数据返回 null（界面隐藏信息条该段）。</summary>
    public (Program? Now, Program? Next)? NowNext(string epgKey)
    {
        if (!_cache.TryGetValue(epgKey, out var list) || list.Count == 0) return null;
        var now = DateTime.Now;
        var cur = list.FirstOrDefault(p => p.Start <= now && now < p.End);
        var nxt = list.Where(p => p.Start >= now).OrderBy(p => p.Start).FirstOrDefault();
        return (cur, nxt);
    }

    /// <summary>刷新某频道的节目单（由后台定时器调用；抓取实现按官网页面结构做，未到位前留空不报错）。</summary>
    public void Refresh(string epgKey)
    {
        // TODO: 抓取 tv.cctv.com/epg/{epgKey} 页面 → 解析时段表 → _cache[epgKey]
        // 失败静默：EPG 缺失不影响播放。
    }
}
