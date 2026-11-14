using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// 路径隐藏判定 (纯逻辑, 各层共用): 路径任意一段以点开头即视为「藏起来的」。
    /// 普通扫描据此排除, 保证藏起来的视频不混进普通列表。
    /// </summary>
    public static class PathHiding
    {
        public static bool IsHiddenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var parts = path.Split(new[] { '/', '\\', System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in parts)
                    if (seg.Length > 0 && seg.StartsWith(".")) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
