using System;
using System.Collections.Generic;
using System.Linq;
using AllPlayer.Model;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 播放历史与续播记录的维护规则 (纯逻辑, 三端共用, 可单测)。
    /// 最近播放: 路径 -> 最后播放时刻 (Unix 毫秒), 超量时先淘汰最久没播的;
    /// 续播记录: 条数超上限时, 淘汰最近播放表里也查不到或最久没播的条目, 防设置文件无限膨胀。
    /// </summary>
    public static class PlaybackHistory
    {
        public const int RecentCap = 100;
        public const int ResumeCap = 300;

        /// <summary>记录一次播放 (路径, 时刻), 并按容量裁剪; 返回是否写入</summary>
        public static bool RecordRecent(AppSettings s, string path, DateTimeOffset when)
        {
            if (s == null || string.IsNullOrEmpty(path)) return false;
            s.Recent[path] = when.ToUnixTimeMilliseconds();
            TrimToCap(s.Recent, RecentCap);
            return true;
        }

        /// <summary>续播记录超上限时裁剪; 返回被移除的路径</summary>
        public static List<string> TrimResume(AppSettings s)
        {
            var removed = new List<string>();
            if (s == null || s.Resume.Count <= ResumeCap) return removed;
            // 优先淘汰"最近播放表里没有"的; 仍超量再按最近播放时间从旧到新淘汰
            var order = s.Resume.Keys
                .OrderBy(p => s.Recent.TryGetValue(p, out var t) ? t : long.MinValue)
                .ToList();
            foreach (var p in order)
            {
                if (s.Resume.Count <= ResumeCap) break;
                s.Resume.Remove(p);
                removed.Add(p);
            }
            return removed;
        }

        /// <summary>通用裁剪: 按"最后播放时间从旧到新"淘汰, 直到条数不超上限</summary>
        private static void TrimToCap(Dictionary<string, long> map, int cap)
        {
            if (map.Count <= cap) return;
            foreach (var p in map.Keys.OrderBy(k => map[k]).Take(map.Count - cap).ToList())
                map.Remove(p);
        }

        /// <summary>按"最近播放优先"排序 (原地); 没有记录的排后面, 保持原相对顺序</summary>
        public static void SortRecentFirst(List<VideoItem> items, IDictionary<string, long> recent)
        {
            if (items == null || items.Count < 2) return;
            long Key(VideoItem v) => recent.TryGetValue(v.Path, out var t) ? t : -1;
            var sorted = items.OrderByDescending(Key).ToList();
            for (int i = 0; i < items.Count; i++) items[i] = sorted[i];
        }
    }
}
