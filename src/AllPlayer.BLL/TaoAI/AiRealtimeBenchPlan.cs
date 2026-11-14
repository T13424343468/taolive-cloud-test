using System;
using System.Collections.Generic;
using System.Globalization;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 实时增强基准结果（纯逻辑，可单测）。v3.17.0：上真机拿一加 15T 内核帧耗时数据，
    /// 为自渲染实时管线（MediaCodec 解码 → taoai → Surface 自渲染 + AudioTrack）定参数。
    /// 账本手写 JSON（netstandard2.1 无 System.Text.Json，照 AiChunkManifest 风格）。
    /// </summary>
    public class AiRealtimeBenchLog
    {
        public int V = 1;
        public bool Ok = true;            // 基准是否跑成（解码失败/模型装载失败 = false）
        public string Note = "";          // 失败原因或补充说明
        public long TakenAtMs;            // Unix 毫秒
        public string Device = "";        // Build.Model
        public string Source = "";        // 源文件名（不含路径）
        public int SrcW, SrcH;            // 解码尺寸
        public int SrInW, SrInH;          // 超分输入尺寸（与 AiSegmentRunner 管线同口径）
        public int Frames;                // 送测帧数
        public List<Item> Items = new List<Item>();

        public class Item
        {
            public string Kind = "";      // sr / rife
            public string Label = "";     // 展示名：超分·动漫 tile256 等
            public int Tile;
            public int ModelId = -1;
            public double AvgMs;          // 单帧平均耗时
            public int OkFrames;
            public int FailFrames;
            public string Note = "";      // 单项异常（如 RIFE 不支持 ARM）
        }

        // ---------- 最小 JSON（数字 + 短字符串，手写够用且可单测） ----------
        public string Serialize()
        {
            string S(string s) => (s ?? "").Replace("\"", "").Replace("\\", "");
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"v\":").Append(V)
              .Append(",\"ok\":").Append(Ok ? 1 : 0)
              .Append(",\"note\":\"").Append(S(Note)).Append('"')
              .Append(",\"takenAtMs\":").Append(TakenAtMs)
              .Append(",\"device\":\"").Append(S(Device)).Append('"')
              .Append(",\"source\":\"").Append(S(Source)).Append('"')
              .Append(",\"srcW\":").Append(SrcW)
              .Append(",\"srcH\":").Append(SrcH)
              .Append(",\"srInW\":").Append(SrInW)
              .Append(",\"srInH\":").Append(SrInH)
              .Append(",\"frames\":").Append(Frames)
              .Append(",\"items\":[");
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"kind\":\"").Append(S(it.Kind)).Append('"')
                  .Append(",\"label\":\"").Append(S(it.Label)).Append('"')
                  .Append(",\"tile\":").Append(it.Tile)
                  .Append(",\"modelId\":").Append(it.ModelId)
                  .Append(",\"avgMs\":").Append(it.AvgMs.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(",\"okFrames\":").Append(it.OkFrames)
                  .Append(",\"failFrames\":").Append(it.FailFrames)
                  .Append(",\"note\":\"").Append(S(it.Note)).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static bool TryParse(string json, out AiRealtimeBenchLog? r)
        {
            r = null;
            try
            {
                if (string.IsNullOrEmpty(json)) return false;
                var x = new AiRealtimeBenchLog
                {
                    Ok = Num(json, "ok") != 0,
                    Note = Str(json, "note"),
                    TakenAtMs = Num(json, "takenAtMs"),
                    Device = Str(json, "device"),
                    Source = Str(json, "source"),
                    SrcW = (int)Num(json, "srcW"),
                    SrcH = (int)Num(json, "srcH"),
                    SrInW = (int)Num(json, "srInW"),
                    SrInH = (int)Num(json, "srInH"),
                    Frames = (int)Num(json, "frames")
                };
                int ai = json.IndexOf("\"items\":[", StringComparison.Ordinal);
                if (ai >= 0)
                {
                    ai += 9;
                    int end = json.IndexOf(']', ai);
                    if (end < 0) end = json.Length;
                    string region = json.Substring(ai, end - ai);
                    int p = 0;
                    while (true)
                    {
                        int fb = region.IndexOf("{\"kind\":\"", p, StringComparison.Ordinal);
                        if (fb < 0) break;
                        fb += 9;
                        int fe = region.IndexOf('"', fb);
                        if (fe < 0) break;
                        int cb = region.IndexOf('}', fe);
                        if (cb < 0) cb = region.Length;
                        string seg = region.Substring(fb - 9, cb - fb + 9);
                        x.Items.Add(new Item
                        {
                            Kind = region.Substring(fb, fe - fb),
                            Label = Str(seg, "label"),
                            Tile = (int)Num(seg, "tile"),
                            ModelId = (int)Num(seg, "modelId"),
                            AvgMs = Dbl(seg, "avgMs"),
                            OkFrames = (int)Num(seg, "okFrames"),
                            FailFrames = (int)Num(seg, "failFrames"),
                            Note = Str(seg, "note")
                        });
                        p = cb + 1;
                    }
                }
                r = x;
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

        private static double Dbl(string s, string key)
        {
            int k = s.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
            if (k < 0) return 0;
            k += key.Length + 3;
            int e = k;
            while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-' || s[e] == '.')) e++;
            return double.Parse(s.Substring(k, e - k), CultureInfo.InvariantCulture);
        }

        private static string Str(string s, string key)
        {
            int k = s.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
            if (k < 0) return "";
            k += key.Length + 4;
            int e = s.IndexOf('"', k);
            return e < 0 ? "" : s.Substring(k, e - k);
        }
    }

    /// <summary>实时可行性折算（纯决策，可单测）：帧耗时 → 可支撑帧率 → 实时倍率 → 结论。</summary>
    public static class AiRealtimeBenchPlan
    {
        public const double RealtimeFps = 24.0;   // 实时判定基准（影院帧率）

        /// <summary>帧平均耗时 → 可支撑帧率（耗时≤0 视为无效）。</summary>
        public static double FpsOf(double avgMs) => avgMs > 0 ? 1000.0 / avgMs : 0;

        /// <summary>实时倍率：可支撑帧率 ÷ 实时基准帧率。≥1 即赶得上播放。</summary>
        public static double RealtimeX(double avgMs, double targetFps = RealtimeFps)
            => (avgMs > 0 && targetFps > 0) ? (1000.0 / avgMs) / targetFps : 0;

        /// <summary>结论文案：≥1 可实时；[0.5,1) 接近实时；&lt;0.5 不可实时。</summary>
        public static string Verdict(double x)
            => x >= 1.0 ? "可实时" : x >= 0.5 ? "接近实时" : "不可实时";

        /// <summary>超分输入尺寸（与 AiSegmentRunner 管线同口径）：4× 后长边超 outLongEdge 则先降输入。</summary>
        public static (int W, int H) SrInputSize(int w, int h, int outLongEdge = 1920)
        {
            if (w <= 0 || h <= 0) return (0, 0);
            if ((long)Math.Max(w, h) * 4 <= outLongEdge) return (w, h);
            double k = (double)outLongEdge / ((long)Math.Max(w, h) * 4);
            return (Math.Max(64, (int)(w * k) & ~1), Math.Max(64, (int)(h * k) & ~1));
        }

        /// <summary>单项汇报行：耗时 → 可支撑帧率 → 实时倍率 + 结论。</summary>
        public static string SummaryLine(AiRealtimeBenchLog.Item it)
        {
            double x = RealtimeX(it.AvgMs);
            return $"{it.Label}: {it.AvgMs:0.}ms/帧 → 可支撑 {FpsOf(it.AvgMs):0.#}fps（实时倍率 {x:0.00}×，{Verdict(x)}）";
        }

        /// <summary>整体结论：取最好一档超分（实时倍率最大）判断实时增强可行性。</summary>
        public static string Headline(AiRealtimeBenchLog r)
        {
            double best = 0; string bestLabel = "";
            foreach (var it in r.Items)
            {
                if (it.Kind != "sr" || it.FailFrames > it.OkFrames) continue;
                double x = RealtimeX(it.AvgMs);
                if (x > best) { best = x; bestLabel = it.Label; }
            }
            if (best <= 0) return "内核尚跑不动实时：需换更小输入或等内核升级";
            if (best >= 1.0) return $"实时增强可行（{bestLabel} 实时倍率 {best:0.00}×），可上自渲染管线";
            if (best >= 0.5) return $"接近实时（{bestLabel} 实时倍率 {best:0.00}×）：可先上低分辨率档自渲染管线";
            return $"差距较大（最好 {bestLabel} 实时倍率 {best:0.00}×）：实时管线需再降输入或等内核优化";
        }
    }
}
