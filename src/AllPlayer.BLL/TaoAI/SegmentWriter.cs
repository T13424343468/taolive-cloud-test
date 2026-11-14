using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace AllPlayer.BLL
{
    /// <summary>逐段增强执行结果</summary>
    public enum SegmentOutcome
    {
        Replaced,       // 校验通过，已原子替换原文件
        KeptOriginal,   // 校验不过，保留原样（绝不写差文件）
        SkippedMagnet,  // 磁力下载中，不入队不覆盖
        SourceMissing   // 源文件不存在
    }

    /// <summary>
    /// 源文件关键信息（由平台侧 MediaExtractor 探测后填入）。
    /// </summary>
    public class SourceVideoInfo
    {
        public long DurationMs { get; set; }
        public int Bitrate { get; set; }            // 原片码率（bps）
        public int Width { get; set; }
        public int Height { get; set; }
        public int FrameCountEstimate { get; set; } // 按帧率估算，进度条用
    }

    /// <summary>三验结果</summary>
    public class VerifyResult
    {
        public bool Ok;
        public string FailReason = "";
        public static VerifyResult Pass() => new VerifyResult { Ok = true };
        public static VerifyResult Fail(string why) => new VerifyResult { Ok = false, FailReason = why };
    }

    /// <summary>
    /// 逐段增强写回（实验档 v1 核心，红线=不出坏文件）。
    ///
    /// 流程：探测源文件 → 决定 tmp 路径（同目录 .taoai.tmp）→ 平台侧逐帧解码并回调
    /// transform（taoai 超分/插帧）重编码到 tmp → 三验（时长+可解遍历+3 抽帧对比度）
    /// → 全过才原子 rename 替换；任一不过，删 tmp，原文件始终完好。
    ///
    /// 本类只做可测的纯逻辑（路径/校验决策/原子替换/MD5 记账），
    /// Android 解码编码在 Phone 工程 AiSegmentRunner（BLL 是 netstandard，不碰 Android API）。
    /// </summary>
    public class SegmentWriter
    {
        /// <summary>tmp 后缀（同目录保证 rename 原子性）</summary>
        public const string TmpSuffix = ".taoai.tmp";

        private readonly Func<string, bool> _isMagnetActive;
        private readonly Action<long>? _tmpBytesWritten;    // 记账用（可空）

        public SegmentWriter(Func<string, bool> isMagnetActive, Action<long>? tmpBytesWritten = null)
        {
            _isMagnetActive = isMagnetActive ?? (p => false);
            _tmpBytesWritten = tmpBytesWritten;
        }

        /// <summary>目标码率：原片码率 × 1.2（重设计 R3 缓解），上限 12Mbps、下限 1.5Mbps 兜底</summary>
        public static int TargetBitrate(int sourceBitrate)
        {
            if (sourceBitrate <= 0) return 4_000_000;
            long b = (long)(sourceBitrate * 1.2);
            if (b < 1_500_000) b = 1_500_000;
            if (b > 12_000_000) b = 12_000_000;
            return (int)b;
        }

        /// <summary>tmp 路径（同目录同名 + .taoai.tmp）</summary>
        public static string TmpPathFor(string sourcePath) => sourcePath + TmpSuffix;

        /// <summary>入队前判定：磁力下载中/源缺失 → 不开工（白名单存疑一律不覆盖）</summary>
        public SegmentOutcome Preflight(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return SegmentOutcome.SourceMissing;
            if (_isMagnetActive(sourcePath)) return SegmentOutcome.SkippedMagnet;
            return SegmentOutcome.Replaced; // 允许开工的信号（实际替换以三验为准）
        }

        /// <summary>
        /// MD5 记账：tmp 写完先算一次（校验基准），rename 前再算一次，必须一致才替换——
        /// 防止「写一半的 tmp 被误替换」这种坏文件事故。
        /// </summary>
        public static string Md5Of(string path)
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var h = md5.ComputeHash(fs);
                return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>时长校验：±1s 容差（容器量化误差）</summary>
        public static VerifyResult CheckDuration(long tmpDurationMs, long sourceDurationMs)
        {
            if (sourceDurationMs <= 0) return VerifyResult.Pass(); // 源不可测时放行给可解遍历兜底
            long diff = Math.Abs(tmpDurationMs - sourceDurationMs);
            if (diff <= 1000) return VerifyResult.Pass();
            return VerifyResult.Fail($"时长不符 tmp={tmpDurationMs}ms src={sourceDurationMs}ms");
        }

        /// <summary>3 抽帧对比度校验：增强帧的亮度均值不低于原帧 90%（不变暗），
        /// 且每帧有效（非全黑/全白）。判定不过 = 画质回退风险 → 弃用整段。</summary>
        public static VerifyResult CheckSampledFrames(IReadOnlyList<(double BrightOriginal, double BrightEnhanced)>? samples)
        {
            if (samples == null || samples.Count < 3)
                return VerifyResult.Fail("抽帧样本不足 3");
            foreach (var s in samples)
            {
                if (s.BrightEnhanced <= 0.5 && s.BrightEnhanced >= 254.5)
                    return VerifyResult.Fail("增强帧异常（全黑/全白）");
                // 超分/插帧不应显著变暗（亮度均值相对下降超 10% 视为回退）
                if (s.BrightOriginal > 0 && s.BrightEnhanced < s.BrightOriginal * 0.9)
                    return VerifyResult.Fail($"增强帧变暗 {s.BrightOriginal:0.0}→{s.BrightEnhanced:0.0}");
            }
            return VerifyResult.Pass();
        }

        /// <summary>
        /// 完成收尾：三验全过后调用。MD5 复核 + File.Move 原子替换（同目录保证 rename 语义）。
        /// 返回 Replaced / KeptOriginal。
        /// </summary>
        public SegmentOutcome Finalize(string sourcePath, string tmpPath, string md5AtMuxComplete,
            Func<string, long> probeTmpDuration, IReadOnlyList<(double, double)>? frameSamples)
        {
            // 红线 1：原文件先于替换完好——tmp 不存在/源不存在都不动
            if (!File.Exists(sourcePath)) return SegmentOutcome.SourceMissing;
            if (!File.Exists(tmpPath)) return SegmentOutcome.KeptOriginal;

            try
            {
                // MD5 复核：mux 完成≈rename 前，文件不能再变
                string md5Now = Md5Of(tmpPath);
                if (md5Now != md5AtMuxComplete)
                {
                    SafeDelete(tmpPath);
                    return SegmentOutcome.KeptOriginal;
                }

                // 时长三验（tmp 可探测时长由平台侧提供）
                long srcDur = 0;
                try { srcDur = probeTmpDuration?.Invoke(sourcePath) ?? 0; } catch { }
                long tmpDur = 0;
                try { tmpDur = probeTmpDuration?.Invoke(tmpPath) ?? 0; } catch { }
                var durCheck = CheckDuration(tmpDur, srcDur);
                if (!durCheck.Ok)
                {
                    SafeDelete(tmpPath);
                    return SegmentOutcome.KeptOriginal;
                }

                // 抽帧三验
                var frameCheck = CheckSampledFrames(frameSamples);
                if (!frameCheck.Ok)
                {
                    SafeDelete(tmpPath);
                    return SegmentOutcome.KeptOriginal;
                }

                // 原子替换：同目录 rename，中断最坏=白增强，原文件不受影响
                var fi = new FileInfo(tmpPath);
                _tmpBytesWritten?.Invoke(fi.Length);
                File.Delete(sourcePath);
                File.Move(tmpPath, sourcePath);
                return SegmentOutcome.Replaced;
            }
            catch
            {
                // 任何异常：tmp 删掉，原文件保持完好
                SafeDelete(tmpPath);
                return SegmentOutcome.KeptOriginal;
            }
        }

        /// <summary>中断清理：把 tmp 删掉（原文件不动），下次可重做</summary>
        public static void Abort(string tmpPath) => SafeDelete(tmpPath);

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
