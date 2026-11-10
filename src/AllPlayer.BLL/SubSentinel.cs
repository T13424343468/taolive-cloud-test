using System;
using System.IO;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 智能字幕进度哨兵 (v3.25.27 流水线): 每窗成果落 SRT 后挪哨兵, 记已处理样本偏移 + 语种 + 动作。
    /// 判定口径 (E9 升级): 哨兵在 = 缓存不完整, 从哨兵偏移续跑; 哨兵无 = 完整缓存。
    /// 写入走 tmp + 原子替换; 读取损坏按无哨兵处理 (从 0 重跑, SRT 整文件重写幂等收敛)。
    /// 哨兵挪动必须在 SRT 落盘之后 (闭环补丁③: SRT 为准, 哨兵冗余)。
    /// </summary>
    public static class SubSentinel
    {
        /// <summary>哨兵文件路径: 与该片的 SRT 缓存同指纹同目录 (不取名 Path 防与 System.IO.Path 混淆)</summary>
        public static string SentPath(string cacheDir, string videoPath)
            => SmartSubPlan.SrtCachePath(cacheDir, videoPath, ".sent");

        /// <summary>哨兵内容: 版本行 + 偏移 + 语种 + 动作 (整数, 对应 SmartSubAction)</summary>
        public static string Format(long offsetSamples, string lang, int action)
            => $"v1\n{Math.Max(0, offsetSamples)}\n{lang ?? "unknown"}\n{action}";

        /// <summary>解析哨兵; 格式不符/损坏返回 false (视为无哨兵)。</summary>
        public static bool TryParse(string? text, out long offsetSamples, out string lang, out int action)
        {
            offsetSamples = 0; lang = ""; action = -1;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 4 || lines[0].Trim() != "v1") return false;
            if (!long.TryParse(lines[1].Trim(), out var off) || off < 0) return false;
            if (!int.TryParse(lines[3].Trim(), out var act) || act < 0) return false;
            offsetSamples = off; lang = lines[2].Trim(); action = act;
            return true;
        }

        /// <summary>写入哨兵 (tmp + 原子替换, 防写一半损坏; v3.25.28 E5 修: 消灭 Delete~Move 之间
        /// 进程被杀丢哨兵的非原子窗口——已存在走 File.Replace 单调用原子替换, 首建走 Move, 两条路都不留空窗)。</summary>
        public static void Write(string path, long offsetSamples, string lang, int action)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Format(offsetSamples, lang, action));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>读取哨兵; 不存在或损坏返回 false。</summary>
        public static bool TryRead(string path, out long offsetSamples, out string lang, out int action)
        {
            try
            {
                if (!File.Exists(path)) { offsetSamples = 0; lang = ""; action = -1; return false; }
                return TryParse(File.ReadAllText(path), out offsetSamples, out lang, out action);
            }
            catch { offsetSamples = 0; lang = ""; action = -1; return false; }
        }

        /// <summary>清哨兵 = 完整缓存转正 (处理到片尾才清)。</summary>
        public static void Clear(string path)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".tmp"); } catch { }
        }
    }
}
