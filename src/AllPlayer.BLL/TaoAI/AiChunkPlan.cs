using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace AllPlayer.BLL
{
    /// <summary>分段增强进度账本（纯逻辑，可单测）：记录已合并进文件的进度与待合并的增强段。</summary>
    public class AiChunkManifest
    {
        public int V = 1;
        public int SegMs = AiChunkPlan.SegMs;   // 默认跟全局段长（3 秒一段）           // 目标段长（2026-11-07 用户定：3 秒一段，一段即合；实际段边界对齐关键帧）
        public long DurMs;                 // 建账时的源时长（±2s 内视为同一文件）
        public long MergedMs;              // 已合并进文件的进度：该时间点之前的内容都已是优化版
        public long MergedBytes;           // 建账时源文件长度（合并后会变，仅作失效参考）
        public List<Chunk> Chunks = new List<Chunk>();   // 待合并的增强段（首尾相接，从 MergedMs 起连成链）

        public class Chunk
        {
            public string File = null!;  // 缓存内文件名 (解析时必赋值)
            public long StartMs;     // 对应原时间轴起点
            public long EndMs;       // 对应原时间轴终点（= 下一段起点 / 合并后新 MergedMs）
            public long Bytes;
        }

        // ---------- 最小 JSON（字段全为数字/无转义文件名，手写够用且 netstandard2.1 可测） ----------
        public string Serialize()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"v\":").Append(V)
              .Append(",\"segMs\":").Append(SegMs)
              .Append(",\"durMs\":").Append(DurMs)
              .Append(",\"mergedMs\":").Append(MergedMs)
              .Append(",\"mergedBytes\":").Append(MergedBytes)
              .Append(",\"chunks\":[");
            for (int i = 0; i < Chunks.Count; i++)
            {
                var c = Chunks[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"file\":\"").Append(c.File).Append("\"")
                  .Append(",\"startMs\":").Append(c.StartMs)
                  .Append(",\"endMs\":").Append(c.EndMs)
                  .Append(",\"bytes\":").Append(c.Bytes).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static bool TryParse(string json, out AiChunkManifest? m)
        {
            m = null;
            try
            {
                if (string.IsNullOrEmpty(json)) return false;
                var r = new AiChunkManifest();
                int segVal = (int)Num(json, "segMs");
                if (segVal > 0) r.SegMs = segVal;
                r.DurMs = Num(json, "durMs");
                r.MergedMs = Num(json, "mergedMs");
                r.MergedBytes = Num(json, "mergedBytes");
                int ci = json.IndexOf("\"chunks\":[", StringComparison.Ordinal);
                if (ci >= 0)
                {
                    ci += 10;
                    int end = json.IndexOf(']', ci);
                    if (end < 0) end = json.Length;
                    string region = json.Substring(ci, end - ci);
                    // 每个段对象一段一段抠出来
                    int p = 0;
                    while (true)
                    {
                        int fb = region.IndexOf("{\"file\":\"", p, StringComparison.Ordinal);
                        if (fb < 0) break;
                        fb += 9;
                        int fe = region.IndexOf('"', fb);
                        if (fe < 0) break;
                        int cb = region.IndexOf('}', fe);
                        if (cb < 0) cb = region.Length;
                        string seg = region.Substring(fb - 9, cb - fb + 9);
                        r.Chunks.Add(new Chunk
                        {
                            File = region.Substring(fb, fe - fb),
                            StartMs = Num(seg, "startMs"),
                            EndMs = Num(seg, "endMs"),
                            Bytes = Num(seg, "bytes")
                        });
                        p = cb + 1;
                    }
                }
                m = r;
                return true;
            }
            catch { return false; }
        }

        private static long Num(string s, string key)
        {
            int k = s.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
            if (k < 0) return 0;
            k += key.Length + 3;
            int e = k;
            while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-')) e++;
            return long.Parse(s.Substring(k, e - k), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>分段增强调度（纯逻辑，可单测）：看多少优化多少、合多少记多少。</summary>
    public static class AiChunkPlan
    {
        public const int SegMs = 3000;   // v3.16 用户定：3 秒一段，一段即合
        public const int MinProgressiveMs = 120000;   // 短片走整条老路，不分段

        public static int ChunkCount(long durMs) => (int)Math.Max(0, (durMs + SegMs - 1) / SegMs);

        /// <summary>下一个要增强的时间范围：链尾接着排（已合并→待合并段→…），单片最长 SegMs，端点对齐关键帧由执行侧落账。</summary>
        public static (long StartMs, long EndMs) NextChunkRange(AiChunkManifest m, long durMs)
        {
            long tailEnd = m.MergedMs;
            if (m.Chunks.Count > 0) tailEnd = m.Chunks[m.Chunks.Count - 1].EndMs;
            if (tailEnd >= durMs) return (0, 0);   // 全片排完
            long start = tailEnd;
            long end = Math.Min(start + m.SegMs, durMs);
            return (start, end);
        }

        /// <summary>调度判断：给定观看位置，是否值得现在去算下一段（观看位置还在已增强区内且落后太多不追算）。
        /// 返回 true 表示应排队增强下一段。</summary>
        public static bool ShouldEnhanceNext(AiChunkManifest m, long watchPosMs, int lookahead)
        {
            if (m.Chunks.Count > 0) return true;                 // 有待合并段，继续算
            long tailEnd = m.MergedMs;
            if (watchPosMs >= tailEnd) return true;              // 观看位置已推进到未增强区
            // 观看位置在已增强区（回看）：落后超过 lookahead 段再算，别白烧电
            return watchPosMs >= tailEnd - (long)lookahead * SegMs;
        }

        /// <summary>合并后账本：进度推进到最后一段终点，清空待合并段。</summary>
        public static void ApplyMerge(AiChunkManifest m, long newMergedMs)
        {
            m.MergedMs = newMergedMs;
            m.Chunks.Clear();
        }

        /// <summary>排队任务是否已过期（v3.17 seek 感知）：拖动/换向后代数不齐的排队不空算，按最后停留位置重排。</summary>
        public static bool IsStale(int jobGen, int curGen) => jobGen != curGen;

        /// <summary>换源判定（v3.17 seek 跟随）：打开实例之后合并前沿又推进了，且目标位置落在「播放中新合并」的
        /// 增强区（≥打开时前沿、<当前前沿）→ 换源可见新增强内容；libvlc 播放中持旧 inode，不换源看不到合并结果。</summary>
        public static bool ShouldSwapSource(AiChunkManifest m, long openedMergedMs, long targetMs)
            => m.MergedMs > openedMergedMs && targetMs >= openedMergedMs && targetMs < m.MergedMs;

        /// <summary>批量换源判定（v3.17 跟播）：新增强内容攒够 leadMs 且播放头不在打开时已见增强区 → 换一次源
        /// 一次见全量效果（频率限制由调用方管，纯逻辑只管值得不值得）。</summary>
        public static bool ShouldAutoSwapSource(AiChunkManifest m, long openedMergedMs, long headMs, long leadMs)
            => m.MergedMs > openedMergedMs && headMs >= openedMergedMs && m.MergedMs - headMs >= leadMs;

        /// <summary>缓存封顶（纯决策）：待合并段总字节超 cap 时，返回应淘汰的段（离合并前沿最远的先走）。</summary>
        public static List<AiChunkManifest.Chunk> EvictPlan(AiChunkManifest m, long capBytes)
        {
            var outList = new List<AiChunkManifest.Chunk>();
            long total = m.Chunks.Sum(c => c.Bytes);
            if (total <= capBytes) return outList;
            // 从链尾（离合并前沿最远）淘汰，马上要合的靠前沿的保留
            for (int i = m.Chunks.Count - 1; i >= 0 && total > capBytes; i--)
            {
                total -= m.Chunks[i].Bytes;
                outList.Add(m.Chunks[i]);
            }
            return outList;
        }
    }
}
