using System;
using System.Collections.Generic;
using System.Globalization;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 业务层 - HLS (m3u8) 播放列表纯解析 (v3.24, 无网络依赖, 可单测):
    /// 主清单列变体/选最高码率、媒体清单切分片 (含 fMP4 初始化段)、本地化重写。
    /// </summary>
    public static class HlsPlaylistParser
    {
        /// <summary>主清单里的一个码率变体</summary>
        public sealed class Variant
        {
            public Uri Url = default!;
            public long Bandwidth;
        }

        /// <summary>媒体清单里的一个分片</summary>
        public sealed class Segment
        {
            public Uri Url = default!;
            public double Duration;
            /// <summary>生效中的 fMP4 初始化段 (#EXT-X-MAP), 纯 TS 流为 null</summary>
            public Uri? MapUrl;
            /// <summary>生效中的解密钥匙行 (#EXT-X-KEY, URI 已解析为上游绝对地址), 无加密为空表</summary>
            public List<string> KeyLines = EmptyKeys;
            /// <summary>该分片之前有 #EXT-X-DISCONTINUITY (编码参数切换/广告插入), 本地清单需原样透传</summary>
            public bool AfterDiscontinuity;
            internal static readonly List<string> EmptyKeys = new List<string>();
        }

        /// <summary>媒体清单解析结果</summary>
        public sealed class MediaPlaylist
        {
            public List<Segment> Segments = new List<Segment>();
            public bool IsLive;   // 无 #EXT-X-ENDLIST = 直播流, 分片会持续追加
            public double TargetDuration;
            /// <summary>清单用 #EXT-X-BYTERANGE 字节区间分片 (本服务暂不支持这种格式)</summary>
            public bool UsesByteRanges;
        }

        /// <summary>解析主清单, 返回全部码率变体; 纯媒体清单返回空表</summary>
        public static List<Variant> ParseMaster(string text, Uri baseUri)
        {
            text = text.TrimStart('\uFEFF');   // v3.24.2: 带 BOM 的清单首行不再被误当成分片地址
            var variants = new List<Variant>();
            long bandwidth = 0;
            bool wantVariant = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    bandwidth = ParseAttrLong(line, "BANDWIDTH");
                    wantVariant = true;
                }
                else if (line.StartsWith("#"))
                {
                    continue;
                }
                else if (wantVariant)
                {
                    variants.Add(new Variant { Url = new Uri(baseUri, line), Bandwidth = bandwidth });
                    wantVariant = false;
                }
            }
            return variants;
        }

        /// <summary>选码率最高的变体 (画质优先, 与磁力选最大文件同一取向)</summary>
        public static Variant PickBestVariant(List<Variant> variants)
        {
            if (variants == null || variants.Count == 0) throw new ArgumentException("主清单里没有变体");
            Variant best = variants[0];
            foreach (var v in variants)
                if (v.Bandwidth > best.Bandwidth) best = v;
            return best;
        }

        /// <summary>解析媒体清单: 逐行配对 EXTINF 与分片地址, 记录初始化段与直播属性</summary>
        public static MediaPlaylist ParseMedia(string text, Uri baseUri)
        {
            text = text.TrimStart('\uFEFF');   // v3.24.2: 带 BOM 的清单首行不再被误当成分片地址
            var pl = new MediaPlaylist();
            Uri? map = null;
            var keyLines = new List<string>();   // 生效中的钥匙行 (METHOD=NONE 清空)
            double pendingDur = 0;
            bool hasPending = false;
            bool pendingDisc = false;   // 断点标记挂到下一个分片头上
            bool hasEndlist = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var body = line.Substring(8);
                    var comma = body.IndexOf(',');
                    var durText = comma >= 0 ? body.Substring(0, comma) : body;
                    double.TryParse(durText, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDur);
                    hasPending = true;
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    var u = ParseAttr(line, "URI");
                    if (!string.IsNullOrEmpty(u)) map = new Uri(baseUri, u);
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                {
                    // 加密清单: 钥匙行原样保留, URI 解析成上游绝对地址 (播放器自己去拿钥匙解密)
                    var method = ParseAttr(line, "METHOD");
                    var uri = ParseAttr(line, "URI");
                    if (method != null && method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        keyLines = new List<string>();
                    }
                    else if (!string.IsNullOrEmpty(uri))
                    {
                        var abs = new Uri(baseUri, uri).AbsoluteUri;
                        keyLines = new List<string> { ReplaceUriAttr(line, abs) };
                    }
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
                {
                    // 断点标记 (广告插入/编码参数切换): 记到下一个分片头上, 本地化时原样透传 (v3.25.0)
                    pendingDisc = true;
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase))
                {
                    pl.UsesByteRanges = true;
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    hasEndlist = true;
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(line.Substring(22), NumberStyles.Float, CultureInfo.InvariantCulture, out pl.TargetDuration);
                }
                else if (line.StartsWith("#"))
                {
                    continue;
                }
                else
                {
                    pl.Segments.Add(new Segment
                    {
                        Url = new Uri(baseUri, line),
                        Duration = hasPending ? pendingDur : 0,
                        MapUrl = map,
                        KeyLines = keyLines,
                        AfterDiscontinuity = pendingDisc,
                    });
                    pendingDisc = false;
                    hasPending = false;
                }
            }
            pl.IsLive = !hasEndlist;
            return pl;
        }

        /// <summary>本地化重写: 分片地址换成函数给的本地地址, 保留初始化段/时长/断点标记/直播属性</summary>
        public static string BuildLocalPlaylist(List<Segment> segments, bool isLive, double targetDuration,
            Func<Segment, int, string> localSegUrl, string? localMapUrl)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("#EXTM3U\n");
            sb.Append("#EXT-X-VERSION:3\n");
            double td = targetDuration;
            foreach (var s in segments) if (s.Duration > td) td = s.Duration;
            if (td < 1) td = 1;
            sb.Append($"#EXT-X-TARGETDURATION:{Math.Ceiling(td)}\n");
            sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
            if (localMapUrl != null && segments.Count > 0)
                sb.Append($"#EXT-X-MAP:URI=\"{localMapUrl}\"\n");
            // v3.25.0: 钥匙行按生效期逐段对比, 变化才输出。
            // 旧版"同一把钥匙只出一次"会吞掉 加密→明文→再加密 里第二段加密的钥匙行,
            // 播放器仍按明文解密密文导致花屏; 引用比较 (同生效期共享同一实例) 天然区分两种情况。
            // 从有钥匙回到明文时必须显式补 METHOD=NONE, 否则播放器沿用旧钥匙解密明文段 (HLS 规范: 钥匙持续生效直到下一条)。
            object? prevKeys = null;
            bool prevHadKeys = false;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s.AfterDiscontinuity) sb.Append("#EXT-X-DISCONTINUITY\n");
                if (!ReferenceEquals(s.KeyLines, prevKeys))
                {
                    if (s.KeyLines.Count == 0)
                    {
                        if (prevHadKeys) sb.Append("#EXT-X-KEY:METHOD=NONE\n");
                    }
                    else
                    {
                        foreach (var k in s.KeyLines) sb.Append(k + "\n");
                    }
                    prevKeys = s.KeyLines;
                    prevHadKeys = s.KeyLines.Count > 0;
                }
                var dur = s.Duration > 0 ? s.Duration : 1.0;
                sb.Append($"#EXTINF:{dur.ToString("F3", CultureInfo.InvariantCulture)},\n");
                sb.Append(localSegUrl(s, i) + "\n");
            }
            if (!isLive) sb.Append("#EXT-X-ENDLIST\n");
            return sb.ToString();
        }

        /// <summary>从地址里猜显示名: 取 m3u8 上一级路径段, 退化用主机名</summary>
        public static string SuggestName(string url)
        {
            try
            {
                var u = new Uri(url);
                var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string? name = null;
                if (parts.Length >= 1)
                {
                    var last = Uri.UnescapeDataString(parts[^1]);
                    if (last.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parts.Length >= 2)
                            name = Uri.UnescapeDataString(parts[^2]);
                    }
                    else name = last;
                }
                if (string.IsNullOrWhiteSpace(name)) name = u.Host;
                return name;
            }
            catch
            {
                return "m3u8 视频";
            }
        }

        /// <summary>把标签里的 URI="..." 替换为给定绝对地址 (钥匙行本地化用)</summary>
        public static string ReplaceUriAttr(string line, string newUri)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                line, "URI=\"[^\"]*\"", "URI=\"" + newUri.Replace("\"", "%22") + "\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>取标签属性值 (形如 #EXT-X-MAP:URI="xx" 的 xx)</summary>
        private static string? ParseAttr(string line, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                line, name + "=(?:\"([^\"]*)\"|([^,\\s]*))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }

        private static long ParseAttrLong(string line, string name)
        {
            var v = ParseAttr(line, name);
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
    }
}
