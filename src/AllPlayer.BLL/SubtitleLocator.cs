using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 外挂字幕匹配规则 (纯逻辑, 三端共用, 可单测):
    /// 1) 同目录文件名(不含扩展名)与视频完全一致者优先, 字幕扩展名按常见顺序;
    /// 2) 其次匹配"视频名开头"的字幕 (如 movie.chs.srt 配 movie.mp4);
    /// 3) 都没有则返回空, 播放器不挂字幕。
    /// </summary>
    public static class SubtitleLocator
    {
        public static readonly string[] Extensions = { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx" };

        public static bool IsSubtitleFile(string path) =>
            Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

        /// <summary>从候选文件里挑出最匹配视频的字幕; 找不到返回 null</summary>
        public static string? Pick(string? videoPath, IEnumerable<string>? candidatePaths)
        {
            if (string.IsNullOrEmpty(videoPath) || candidatePaths == null) return null;
            var stem = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(stem)) return null;

            var subs = candidatePaths
                .Where(IsSubtitleFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sameStem = subs
                .Where(p => Path.GetDirectoryName(p) == Path.GetDirectoryName(videoPath)) // 只在同目录找, 避免跨文件夹同名误挂
                .OrderBy(p => Array.IndexOf(Extensions, Path.GetExtension(p).ToLowerInvariant()))
                .FirstOrDefault(p => string.Equals(
                    Path.GetFileNameWithoutExtension(p), stem, StringComparison.OrdinalIgnoreCase));
            if (sameStem != null) return sameStem;

            return subs
                .OrderBy(p => Path.GetFileNameWithoutExtension(p).Length)   // 前缀越接近视频名越好
                .ThenBy(p => Array.IndexOf(Extensions, Path.GetExtension(p).ToLowerInvariant()))
                .FirstOrDefault(p =>
                    Path.GetFileNameWithoutExtension(p).StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && Path.GetDirectoryName(p) == Path.GetDirectoryName(videoPath));
        }
    }
}
