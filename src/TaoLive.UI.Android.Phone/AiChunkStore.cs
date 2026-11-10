using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AllPlayer.BLL;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 分段续存缓存仓（v3.15）：CacheDir/ai-seg/&lt;key&gt;/ 下放账本 + 待合并增强段。
    /// 红线：账本与段文件落盘一律 tmp + 原子替换；源时长对不上（&gt;2s）账本作废清目录；
    /// 缓存总字节封顶，超了按 AiChunkPlan.EvictPlan 淘汰（离合并前沿最远先走）。
    /// </summary>
    internal class AiChunkStore
    {
        /// <summary>待合并段缓存封顶：256MB（封顶不占空间，超了先淘汰后补算）</summary>
        public const long CapBytes = 256L * 1024 * 1024;

        private const string ManifestName = "manifest.json";

        private readonly string _dir;

        public AiChunkStore(string cacheRoot, string srcPath)
        {
            _dir = Path.Combine(cacheRoot ?? ".", "ai-seg", KeyOf(srcPath));
        }

        public string Dir => _dir;

        /// <summary>缓存键：md5(path|len)——同名同长度视为同一文件，长度变了必然重算。</summary>
        private static string KeyOf(string srcPath)
        {
            try
            {
                var fi = new FileInfo(srcPath);
                using (var md5 = MD5.Create())
                {
                    var data = Encoding.UTF8.GetBytes(srcPath + "|" + (fi.Exists ? fi.Length : 0));
                    var h = md5.ComputeHash(data);
                    var sb = new StringBuilder();
                    foreach (var b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "unknown"; }
        }

        /// <summary>加载账本；没有/损坏/源时长对不上（&gt;2s）→ 重建新账本（目录清空重来）。</summary>
        public AiChunkManifest LoadOrInit(long srcDurMs, long srcBytes)
        {
            var fresh = new AiChunkManifest { DurMs = srcDurMs, MergedBytes = srcBytes, SegMs = AiChunkPlan.SegMs };
            try
            {
                Directory.CreateDirectory(_dir);
                string mp = ManifestPath();
                if (File.Exists(mp))
                {
                    if (AiChunkManifest.TryParse(File.ReadAllText(mp), out var m) && m != null &&
                        Math.Abs(m.DurMs - srcDurMs) <= 2000)
                        return m;
                    Abort();   // 时长对不上 → 账本作废，目录清空
                    Directory.CreateDirectory(_dir);
                }
                return fresh;
            }
            catch { return fresh; }
        }

        /// <summary>存账本：tmp + 原子替换，绝不写半份。</summary>
        public void Save(AiChunkManifest m)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string mp = ManifestPath();
                string tmp = mp + ".tmp";
                File.WriteAllText(tmp, m.Serialize());
                if (File.Exists(mp)) File.Delete(mp);
                File.Move(tmp, mp);
            }
            catch { }
        }

        public string ManifestPath() => Path.Combine(_dir, ManifestName);

        /// <summary>新段文件路径（序号递增；合并后链清空重排，序号复用不冲突）。</summary>
        public string ChunkPath(int index) => Path.Combine(_dir, $"seg_{Math.Max(0, index):D6}.mp4");

        public string ChunkFileOf(string name) => Path.Combine(_dir, name);

        public void DeleteChunkFile(string name)
        {
            try { File.Delete(ChunkFileOf(name)); } catch { }
        }

        /// <summary>中断清理：整个缓存目录删掉（原文件不动），下次可重做。</summary>
        public void Abort()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>缓存封顶：按淘汰计划删段文件并从账本摘除；有淘汰才存账本。</summary>
        public void EvictToFit(AiChunkManifest m)
        {
            try
            {
                var doomed = AiChunkPlan.EvictPlan(m, CapBytes);
                if (doomed.Count == 0) return;
                foreach (var c in doomed)
                {
                    DeleteChunkFile(c.File);
                    m.Chunks.Remove(c);
                }
                Save(m);
            }
            catch { }
        }
    }
}
