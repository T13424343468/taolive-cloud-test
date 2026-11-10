using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 智能字幕纯逻辑 (v3.25.23 一期, 可单测):
    /// 模型资产口径 (下载地址/缓存自愈) + 语种决策 (中文只转写, 日韩英法德转写+英译) + SRT 生成。
    /// 翻译引擎接口本期预留: 外文片一期产出原文+英文字幕, 中文字幕引擎二期接入 (opus-mt 单语向 ONNX)。
    /// </summary>
    public static class SmartSubPlan
    {
        /// <summary>识别模型: whisper tiny 多语种 (f16, 约 74MB), 覆盖日韩英法德等 99 种语言</summary>
        public const string ModelFileName = "ggml-tiny.bin";
        public const string ModelUrl = "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin";
        public const long ModelExpectedSize = 77691713L;   // 缓存自愈: 存在且大小一致才复用

        /// <summary>whisper 包装层返回的时间为 10ms 单位, 转 SRT 毫秒</summary>
        public static long Tick10ToMs(long t10) => t10 * 10L;

        /// <summary>分窗识别 (v3.25.25 E8 修): 窗起点样本数换算毫秒 (16kHz 单声道, 每毫秒 16 样本)。
        /// 旧码 off/160 是 10ms tick 口径, 加在毫秒上导致长片第二窗起全部错位 (600s 窗挂 60s)。</summary>
        public static long WindowOffsetMs(long offSamples) => offSamples / 16L;

        /// <summary>窗内 10ms tick 时间加上窗偏移, 得全片毫秒时间轴 (v3.25.25 E8 修统一走此口径)</summary>
        public static long WindowTimestampMs(long offSamples, long tick10)
            => WindowOffsetMs(offSamples) + tick10 * 10L;

        /// <summary>SRT 时间戳: HH:mm:ss,fff</summary>
        public static string FormatSrtTime(long ms)
        {
            if (ms < 0) ms = 0;
            var h = ms / 3600000;
            var m = ms % 3600000 / 60000;
            var s = ms % 60000 / 1000;
            var f = ms % 1000;
            return $"{h:D2}:{m:D2}:{s:D2},{f:D3}";
        }

        /// <summary>段列表转标准 SRT 文本 (时间须为毫秒; 空文本段跳过; 按开始时间排序)</summary>
        public static string BuildSrt(IEnumerable<(long t0ms, long t1ms, string text)> segments)
        {
            var sb = new StringBuilder();
            int idx = 0;
            foreach (var seg in (segments ?? Enumerable.Empty<(long, long, string)>())
                     .Where(s => !string.IsNullOrWhiteSpace(s.text))
                     .OrderBy(s => s.t0ms))
            {
                idx++;
                sb.AppendLine(idx.ToString());
                sb.AppendLine($"{FormatSrtTime(seg.t0ms)} --> {FormatSrtTime(seg.t1ms)}");
                sb.AppendLine(seg.text!.Trim());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>SRT 文本解析回段列表 (v3.25.27 流水线续跑: 整文件重写前要把既有部分读回)。
        /// 容忍坏行 (序号行/时间轴不合法直接跳过), 空文本段不收。
        /// v3.25.28 E9 修: 纯数字行不再一律当序号丢弃——序号行判定收紧为
        /// 「全数字行且下一条非空行是时间轴行」, 台词「2024」照常保留。</summary>
        public static List<(long t0ms, long t1ms, string text)> ParseSrt(string? srtText)
        {
            var list = new List<(long, long, string)>();
            if (string.IsNullOrEmpty(srtText)) return list;
            var lines = srtText.Replace("\r\n", "\n").Split('\n');
            long t0 = 0, t1 = 0;
            var textBuf = new List<string>();
            bool pending = false;
            void Flush()
            {
                if (pending && textBuf.Count > 0)
                    list.Add((t0, t1, string.Join("\n", textBuf).Trim()));
                textBuf.Clear(); pending = false;
            }
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (TryParseSrtTimeLine(line, out var a, out var b))
                {
                    Flush(); t0 = a; t1 = b; pending = true; continue;
                }
                if (pending && line.Length > 0 && !IsIndexLine(lines, i)) textBuf.Add(line);
                if (line.Length == 0) Flush();
            }
            Flush();
            return list;
        }

        /// <summary>序号行判定 (v3.25.28 E9 修, 可测): 全数字行且下一条非空行是时间轴行才算序号;
        /// 否则 (如台词「2024」) 是文本, 照常收。</summary>
        private static bool IsIndexLine(string[] lines, int index)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || !line.All(char.IsDigit)) return false;
            for (int j = index + 1; j < lines.Length; j++)
            {
                var next = lines[j].Trim();
                if (next.Length == 0) continue;
                return TryParseSrtTimeLine(next, out _, out _);
            }
            return false;
        }

        private static bool TryParseSrtTimeLine(string line, out long t0ms, out long t1ms)
        {
            t0ms = t1ms = 0;
            var parts = line.Split(new[] { "-->" }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            return TryParseSrtTime(parts[0].Trim(), out t0ms) && TryParseSrtTime(parts[1].Trim(), out t1ms);
        }

        private static bool TryParseSrtTime(string s, out long ms)
        {
            ms = 0;
            // HH:mm:ss,fff
            if (s.Length != 12 || s[2] != ':' || s[5] != ':' || s[8] != ',') return false;
            if (!long.TryParse(s.Substring(0, 2), out var h) || !long.TryParse(s.Substring(3, 2), out var m)
                || !long.TryParse(s.Substring(6, 2), out var sec) || !long.TryParse(s.Substring(9, 3), out var f))
                return false;
            ms = ((h * 60 + m) * 60 + sec) * 1000 + f;
            return true;
        }

        /// <summary>段合并去重 (v3.25.27 闭环补丁①; v3.25.28 E4 修): 按 (t0,t1) 时间轴去重, 无重句无丢句;
        /// 撞键时以重放 (incoming) 为准——崩溃截断的残句/垃圾拼接不再永久压制正确句 (重放窗是幂等重跑,
        /// 内容必然比崩溃前落盘的残片新)。结果按开始时间排序, 交给 BuildSrt 统一整文件重写+续编号。</summary>
        public static List<(long t0ms, long t1ms, string text)> MergeSegments(
            IEnumerable<(long t0ms, long t1ms, string text)> existing,
            IEnumerable<(long t0ms, long t1ms, string text)> incoming)
        {
            var byKey = new Dictionary<(long, long), (long t0ms, long t1ms, string text)>();
            foreach (var seg in (existing ?? Enumerable.Empty<(long, long, string)>())
                     .Where(s => !string.IsNullOrWhiteSpace(s.text)))
                byKey[(seg.t0ms, seg.t1ms)] = seg;
            foreach (var seg in (incoming ?? Enumerable.Empty<(long, long, string)>())
                     .Where(s => !string.IsNullOrWhiteSpace(s.text)))
                byKey[(seg.t0ms, seg.t1ms)] = seg;   // E4: 撞键以重放为准, 幂等覆盖
            return byKey.Values.OrderBy(s => s.t0ms).ToList();
        }

        /// <summary>模型缓存路径: 固定子目录 + 固定文件名</summary>
        public static string ModelPath(string cacheDir) => Path.Combine(cacheDir ?? "", "taosub", ModelFileName);

        /// <summary>缓存自愈口径: 文件存在且大小与期望一致才复用 (损坏/截断重下)</summary>
        public static bool ModelReady(string modelPath)
        {
            try { return File.Exists(modelPath) && new FileInfo(modelPath).Length == ModelExpectedSize; }
            catch { return false; }
        }

        /// <summary>语种决策: 中文只转写; 日韩英法德转写+英译 (whisper 自带英译); 其他语种只转写</summary>
        public static SmartSubAction Decide(string? detectedLang)
        {
            switch (detectedLang)
            {
                case "zh": return SmartSubAction.TranscribeOnly;
                case "ja":
                case "ko":
                case "en":
                case "fr":
                case "de": return SmartSubAction.TranscribePlusTranslate;
                default: return SmartSubAction.TranscribeOnly;
            }
        }

        /// <summary>识别结果缓存文件: 按视频路径指纹 (路径哈希) 落缓存, 同片二次播放秒挂</summary>
        public static string SrtCachePath(string cacheDir, string videoPath, string suffix)
        {
            var key = (videoPath ?? "").GetHashCode().ToString("X8");
            return Path.Combine(cacheDir ?? "", "taosub", key + suffix);
        }

        /// <summary>缓存命中要挂载的 SRT 列表 (v3.25.25 E9 修): 原文在先, 中文字幕存在则随后挂上
        /// (与实时链路「先挂原文再挂中文」口径一致); 缓存命中的外文片不再只挂原文。</summary>
        public static List<string> CachedSrtMounts(string origSrtPath, string zhSrtPath, Func<string, bool> exists)
        {
            var list = new List<string>();
            if (origSrtPath == null || !exists(origSrtPath)) return list;
            list.Add(origSrtPath);
            if (zhSrtPath != null && exists(zhSrtPath)) list.Add(zhSrtPath);
            return list;
        }

        /// <summary>Kick 判定 (v3.25.27 E9 口径升级, 可单测):
        /// 哨兵在 = 缓存不完整 → 挂已有部分字幕照常看 + 后台从哨兵偏移续跑 (resume=true);
        /// 哨兵无且 SRT 在 = 完整缓存 → 只挂不重跑; 哨兵无且无缓存 = 全新跑。
        /// 调用方口径: mounts.Count>0 且 !resume → 完整缓存直挂返回; resume → 挂已有部分后续跑。</summary>
        public static (List<string> mounts, bool resume) KickPlan(
            string origSrtPath, string zhSrtPath, bool sentinelExists, Func<string, bool> exists)
            => (CachedSrtMounts(origSrtPath, zhSrtPath, exists), sentinelExists);

        /// <summary>网络路径判定 (v3.25.25 E14 修): 磁力/m3u8/http(s) 直链进不了 MediaExtractor 抽音频, 智能字幕必败, 入口直接跳过。</summary>
        public static bool IsNetworkPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var p = path.Trim();
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return true;
            // m3u8 即使落在本地也是流清单, 抽不了音频, 同口径跳过
            return p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>满盘预检 (v3.25.25 E12 引入; v3.25.26 改纯函数): 剩余空间不足所需字节直接报, 不白跑一趟。
        /// 空间探测改由调用方按平台口径取 (安卓 StatFs / 桌面 DriveInfo), BLL 不再自探——
        /// DriveInfo 在安卓上探根分区必误报, 真机实锤 (空闲 191G 仍报不足)。</summary>
        public static bool EnoughDiskSpace(long availableBytes, long neededBytes) => availableBytes >= neededBytes;

        // ---- v3.25.24 英译中模型 (opus-mt-en-zh int8): 五语全走「whisper 英译 -> 英译中」一条链, 一个模型覆盖 ----

        public const string TransModelDirName = "mt-en-zh";
        public const string TransBaseUrl = "https://hf-mirror.com/Xenova/opus-mt-en-zh/resolve/main/";

        /// <summary>翻译模型文件: 名称 / 期望字节数 / 仓内路径 (下载地址 = TransBaseUrl + RepoPath)</summary>
        public static readonly (string Name, long Size, string RepoPath)[] TransFiles =
        {
            ("encoder_model_quantized.onnx", 52899742L, "onnx/encoder_model_quantized.onnx"),
            ("decoder_model_quantized.onnx", 59842102L, "onnx/decoder_model_quantized.onnx"),
            ("source.spm", 806435L, "source.spm"),
            ("tokenizer.json", 6380952L, "tokenizer.json"),
        };

        public const int TransVocabLines = 65001;   // tokenizer.json 词表行数, 抽取后校验

        /// <summary>翻译模型目录: 识别模型同级的固定子目录</summary>
        public static string TransModelDir(string cacheDir) => Path.Combine(cacheDir ?? "", "taosub", TransModelDirName);

        /// <summary>翻译模型就绪口径: 四个文件都在且大小一致, 且 vocab.txt 已抽取且行数一致</summary>
        public static bool TransModelReady(string transDir)
        {
            try
            {
                foreach (var f in TransFiles)
                {
                    var p = Path.Combine(transDir ?? "", f.Name);
                    if (!File.Exists(p) || new FileInfo(p).Length != f.Size) return false;
                }
                var vocab = Path.Combine(transDir ?? "", "vocab.txt");
                if (!File.Exists(vocab)) return false;
                long lines = 0;
                using var r = new StreamReader(vocab);
                while (r.ReadLine() != null) lines++;
                return lines == TransVocabLines;
            }
            catch { return false; }
        }
    }

    /// <summary>语种决策结果</summary>
    public enum SmartSubAction
    {
        /// <summary>只出原文转写 (中文片/未识别语种)</summary>
        TranscribeOnly,
        /// <summary>原文转写 + 英译 (日韩英法德; whisper 自带英译, 中译二期接)</summary>
        TranscribePlusTranslate,
    }
}
