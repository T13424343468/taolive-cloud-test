using System;
using System.Collections.Generic;
using System.IO;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 启动清扫 (v3.23 P0-3, 可单测): 磁力临时模式靠"正常停止时清理",
    /// 进程被杀会残留临时目录占空间——启动时清掉根目录下无活跃会话的子目录。
    /// 删不掉静默放过 (文件被占用/权限受限等), 下次启动再清。
    /// </summary>
    public static class TempSweeper
    {
        /// <summary>清扫 root 下除 keepPath 外的全部子目录, 返回删除数</summary>
        public static int Sweep(string root, string? keepPath = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;
            }
            catch { return 0; }

            var removed = 0;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if (keepPath != null &&
                            string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keepPath),
                                StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>孤儿 PCM 判定 (v3.25.25 E13, 纯逻辑可测): 文件名以 .pcm/.pcm16 结尾即认。</summary>
        public static bool IsOrphanPcmName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".pcm16", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>孤儿过期判定 (v3.25.25 E13, 纯逻辑可测): 修改时间距 now 超过 maxAge 才清, 正在进行的识别不受影响。</summary>
        public static bool OrphanExpired(DateTime lastWriteUtc, DateTime nowUtc, TimeSpan maxAge)
            => nowUtc - lastWriteUtc >= maxAge;

        /// <summary>清扫 dir 下修改时间超过 maxAge 的 .pcm/.pcm16 孤儿 (v3.25.25 E13):
        /// 识别中途进程被杀的残留按片重播会覆盖同名, 未重播的孤儿白占空间, 启动时回收。
        /// 删不掉静默放过, 返回删除数。</summary>
        public static int SweepOrphanPcm(string dir, TimeSpan maxAge)
        {
            int removed = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
                var nowUtc = DateTime.UtcNow;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        if (!IsOrphanPcmName(Path.GetFileName(f))) continue;
                        if (!OrphanExpired(File.GetLastWriteTimeUtc(f), nowUtc, maxAge)) continue;
                        File.Delete(f);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>智能字幕缓存分组键 (v3.25.28 E7, 纯逻辑可测): &lt;指纹&gt;.orig.srt / &lt;指纹&gt;.zh.srt /
        /// &lt;指纹&gt;.sent 同属一个指纹组 (必须整组同删, 保 E9「哨兵无=完整」口径不破);
        /// 其余文件 (识别/翻译模型本体等) 返回 null 不碰。</summary>
        public static string? SubCacheGroupKey(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var suf in new[] { ".orig.srt", ".zh.srt", ".sent" })
                if (name.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                    return name[..^suf.Length];
            return null;
        }

        /// <summary>清扫 dir 下整组过期的智能字幕缓存 (v3.25.28 E7): 同指纹的 SRT+哨兵按组内
        /// 最新修改时间超 maxAge 判过期, 整组同删——只删 SRT 留哨兵会让部分缓存被 E9 误判完整;
        /// 只删哨兵留 SRT 则把部分成果当完整直挂, 两个口径都必须一起走。
        /// 附带回收超 maxAge 的 .dl 下载残留 (v3.25.28 E6, 含子目录, 兜进程被杀漏网的)。
        /// 删不掉静默放过, 返回删除文件数。</summary>
        public static int SweepStaleSubCaches(string dir, TimeSpan maxAge)
        {
            int removed = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
                var nowUtc = DateTime.UtcNow;
                var groups = new Dictionary<string, (DateTime Latest, List<string> Files)>();
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var key = SubCacheGroupKey(Path.GetFileName(f));
                    if (key == null) continue;
                    try
                    {
                        var wt = File.GetLastWriteTimeUtc(f);
                        if (groups.TryGetValue(key, out var g))
                        {
                            if (wt > g.Latest) g.Latest = wt;
                            g.Files.Add(f);
                            groups[key] = g;
                        }
                        else groups[key] = (wt, new List<string> { f });
                    }
                    catch { }
                }
                foreach (var g in groups.Values)
                {
                    if (!OrphanExpired(g.Latest, nowUtc, maxAge)) continue;
                    foreach (var f in g.Files) { try { File.Delete(f); removed++; } catch { } }
                }
                // E6 兜底: 下载中断/进程被杀留下的 .dl 残留按期回收 (DownloadFile 失败路径之外的漏网)
                foreach (var f in Directory.EnumerateFiles(dir, "*.dl", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!OrphanExpired(File.GetLastWriteTimeUtc(f), nowUtc, maxAge)) continue;
                        File.Delete(f);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }
    }
}
