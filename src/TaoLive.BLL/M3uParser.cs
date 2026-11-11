using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// m3u/m3u8 导入解析器（自有源口子，不设上限）。
/// 容错策略：坏行跳过不中断；#EXTINF 的 tvg-* 属性用于命名与分组；
/// 可播性由 HealthCheck 后台逐个探测，不可播标灰不删。
/// </summary>
public class M3uParser
{
    public class Entry
    {
        public string Name { get; set; } = "";
        public string GroupHint { get; set; } = "";  // tvg-group / group-title
        public string Url { get; set; } = "";
        public bool Dead { get; set; }               // 探测不可播，标灰
    }

    public List<Entry> ParseFile(string path)
        => Parse(File.ReadAllLines(path));

    public List<Entry> ParseText(string text)
        => Parse(text.Replace("\r", "").Split('\n'));

    private static List<Entry> Parse(IReadOnlyList<string> lines)
    {
        var result = new List<Entry>();
        Entry? cur = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                cur = new Entry();
                int comma = line.IndexOf(',');
                var attrs = comma > 0 ? line[8..comma] : line[8..];
                cur.Name = comma > 0 ? line[(comma + 1)..].Trim() : "";
                var g = ExtractAttr(attrs, "group-title");
                if (!string.IsNullOrEmpty(g)) cur.GroupHint = g;
            }
            else if (line.StartsWith("#"))
            {
                // 其他注释行忽略
            }
            else if (!string.IsNullOrEmpty(line) && cur != null)
            {
                cur.Url = line;
                if (cur.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    result.Add(cur);
                cur = null;
            }
        }
        return result;
    }

    private static string? ExtractAttr(string attrs, string key)
    {
        var marker = key + "=\"";
        int i = attrs.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + marker.Length;
        int end = attrs.IndexOf('"', start);
        return end > start ? attrs[start..end] : null;
    }
}
