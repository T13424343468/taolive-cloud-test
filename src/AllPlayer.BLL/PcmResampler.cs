using System;
using System.Collections.Generic;
using System.IO;

namespace AllPlayer.BLL
{
    /// <summary>
    /// PCM 重采样纯数学 (v3.25.23 智能字幕一期, 可单测):
    /// PCM16 单声道流式线性插值重采样到 16kHz float32, 供 whisper 本地识别输入。
    /// </summary>
    public static class PcmResampler
    {
        public const int TargetRate = 16000;

        /// <summary>流式线性插值重采样: src 为 PCM16 单声道, dst 写 float32 小端; 返回输出采样数。</summary>
        public static long ResampleTo16K(Stream src, Stream dst, long srcFrames, int srcRate)
        {
            if (srcFrames <= 0 || srcRate <= 0) return 0;
            using var br = new BinaryReader(src);
            using var bw = new BinaryWriter(dst);
            double step = (double)srcRate / TargetRate;
            long outSamples = (long)(srcFrames / step);
            // 滑窗: prev/cur 两样本做线性插值, 流式吃源流
            float prev = ReadSample(br);
            float cur = prev;
            long consumed = 1;
            for (long j = 0; j < outSamples; j++)
            {
                double srcPos = j * step;
                while (consumed <= (long)srcPos && consumed < srcFrames)
                {
                    prev = cur;
                    cur = ReadSample(br);
                    consumed++;
                }
                float frac = (float)(srcPos - (long)srcPos);
                float v = prev + (cur - prev) * frac;
                bw.Write(v);
            }
            return outSamples;
        }

        private static float ReadSample(BinaryReader br)
        {
            if (br.BaseStream.Position + 2 > br.BaseStream.Length) return 0f;
            short s = br.ReadInt16();
            return s / 32768f;
        }
    }

    /// <summary>
    /// 增量重采样会话 (v3.25.27 智能字幕流水线): 解码线程逐帧喂源样本, 边喂边产出 16kHz float,
    /// 供识别线程按窗取数——抽取与识别并行, 长片不再整片抽完才动。
    /// 数学口径与 ResampleTo16K 完全一致 (含其插值取样的既有约定), 等价性有单测背书;
    /// startOutSample 支持断点续跑: 本会话只产出全片绝对序号 ≥ startOutSample 的样本。
    /// </summary>
    public sealed class IncrementalResampler
    {
        private readonly double _step;
        private readonly long _startOut;      // 本会话首个输出样本的全片绝对序号
        private readonly Queue<short> _pending = new Queue<short>();   // 已喂入未消费的源样本
        private float _prev, _cur;
        private long _consumed;               // 已消费源样本数 (打底样本计 1)
        private long _fed;                    // 已喂入源样本数 (流式终局界: 即使现在终局, 批式也只出到 (long)(_fed/step))
        private long _nextJ;                  // 下一个待产出输出样本的绝对序号
        private bool _primed;

        public IncrementalResampler(int srcRate, long startOutSample = 0)
        {
            if (srcRate <= 0) throw new ArgumentOutOfRangeException(nameof(srcRate));
            _step = (double)srcRate / PcmResampler.TargetRate;
            _startOut = Math.Max(0, startOutSample);
            _nextJ = _startOut;
        }

        /// <summary>本会话已产出输出样本数 (不含断点之前的存量)。</summary>
        public long Written => _nextJ - _startOut;

        /// <summary>喂入下一个源样本 (须按序从 0 号起喂; 打底样本入 prev/cur, 其余排队)。
        /// PCM16 原始值在此归一化到 [-1,1) (与批式 ReadSample 同口径); 返回本会话累计产出数。</summary>
        public long Feed(short sample, BinaryWriter dst)
        {
            if (!_primed) { _prev = _cur = sample / 32768f; _consumed = 1; _fed = 1; _primed = true; return Written; }
            _pending.Enqueue(sample);
            _fed++;
            Pump(dst, null);
            return Written;
        }

        /// <summary>源流终局: srcFramesTotal 为本会话喂入的源样本总数, 冲出剩余输出; 返回累计产出数。</summary>
        public long Finish(long srcFramesTotal, BinaryWriter dst)
        {
            Pump(dst, Math.Max(0, srcFramesTotal));
            return Written;
        }

        /// <summary>推进产出: 与 ResampleTo16K 的 while 消费/截断口径逐步对齐。
        /// srcFramesTotal 传 null 表示流式未知终局——只有「即使现在终局也仍在批式产出范围」
        /// (jLocal < (long)(_fed/step)) 的样本才出, 尾部候选延到 Finish 由终点界统一裁决,
        /// 保证产出与批式逐样本一致。序号映射: 输出/源位置均按本会话局部序号算
        /// (喂入的是断点之后的尾部段), 绝对输出序号 = 局部 + startOut。</summary>
        private void Pump(BinaryWriter dst, long? srcFramesTotal)
        {
            while (true)
            {
                long jLocal = _nextJ - _startOut;
                long srcPos = (long)(jLocal * _step);
                // 消费推进: 与批式 while (consumed <= (long)srcPos && consumed < srcFrames) 同一口径
                while (_consumed <= srcPos && _pending.Count > 0
                       && (!srcFramesTotal.HasValue || _consumed < srcFramesTotal.Value))
                {
                    _prev = _cur; _cur = _pending.Dequeue() / 32768f; _consumed++;
                }
                // 出样条件: 消费已越过本样位置 (终局时允许钳到末样本), 且流式阶段不越过批式终点界
                bool inRange = srcFramesTotal.HasValue
                    ? jLocal < (long)(srcFramesTotal.Value / _step)
                    : jLocal < (long)(_fed / _step);
                bool canEmit = (_consumed > srcPos || (srcFramesTotal.HasValue && _consumed >= srcFramesTotal.Value))
                    && inRange;
                if (!canEmit) return;
                float frac = (float)(jLocal * _step - srcPos);
                dst.Write(_prev + (_cur - _prev) * frac);
                _nextJ++;
            }
        }
    }
}
