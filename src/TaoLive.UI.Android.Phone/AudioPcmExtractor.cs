using System;
using System.IO;
using Android.Media;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 音频抽取 (v3.25.23): MediaExtractor 选音轨 -> MediaCodec 解码 PCM16 -> 混单声道
    /// -> 流式线性插值重采样 16kHz -> float32 原始文件 (供 whisper)。长片不整块进内存。
    /// v3.25.27 流水线改造: 抽取与识别并行——ExtractStreaming 边解码边重采样顺序写 16k float
    /// 文件, 按约 1 秒粒度 flush 保证识别线程跨线程可见 (E13 口径: 进行中不落孤儿, 中断残留
    /// 由 24 小时清扫兜底; .pcm 不持久, 续跑一律重抽)。
    /// </summary>
    internal static class AudioPcmExtractor
    {
        public const int TargetRate = 16000;

        /// <summary>流水线抽取: 解码即重采样, 顺序写 outFile (float32, 16kHz 单声道)。
        /// resumeOutSamples 为续跑起点 (全片绝对输出样本序号, 文件从该偏移起写空洞占位,
        /// 识别侧 ReadPcm 仍按全片绝对样本寻址); onFlushed 在每个冲盘点回报
        /// 「全片绝对已冲盘样本数」(识别线程跨线程可见口径, 头部空洞不计为新数据);
        /// 返回本会话累计产出样本数。失败抛异常。</summary>
        public static long ExtractStreaming(string videoPath, string outFile, long resumeOutSamples,
                                            Action<string>? progress = null,
                                            System.Threading.CancellationToken? cancel = null,
                                            Action<long>? onFlushed = null)
        {
            progress?.Invoke("抽取音频");
            int srcRate;
            using (var open = new MediaExtractor())
            {
                open.SetDataSource(videoPath);
                try
                {
                    srcRate = open.GetTrackFormat(FindAudioTrack(open)).GetInteger(MediaFormat.KeySampleRate);
                }
                catch (InvalidOperationException) { throw; }   // 「没有音轨」照原样上抛
                catch (Exception ex)
                {
                    // v3.25.28 E11 修: KeySampleRate 缺键的 Java 异常归中文口径, 不裸上屏
                    throw new InvalidOperationException("读取音频参数失败, 该片格式暂不支持智能字幕", ex);
                }
            }
            var res = new AllPlayer.BLL.IncrementalResampler(srcRate, resumeOutSamples);
            // v3.25.28 E2 修: 续跑先跳过断点前约 resumeOutSamples*step 个源样本再喂 sink——
            // resampler 按「本会话喂入段」局部序号映射, 若全段照喂, 片头音频会被记成断点后的绝对序号
            // (字幕内容与时间轴双错位, 且 _pending 堆积断点前全部源样本)
            long skipSrc = resumeOutSamples > 0 ? (long)Math.Round(resumeOutSamples * ((double)srcRate / TargetRate)) : 0;
            using var output = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (resumeOutSamples > 0)
            {
                // 续跑: 文件头部空洞占位, 识别侧绝对寻址不乱
                output.SetLength(resumeOutSamples * 4);
                output.Seek(resumeOutSamples * 4, SeekOrigin.Begin);
            }
            using var bw = new BinaryWriter(output);

            long frames = DecodeMono(videoPath, null, progress, cancel, (buf, start, n) =>
            {
                long before = res.Written;
                for (int i = 0; i < n; i++) res.Feed(buf[start + i], bw);
                // flush 点: 每约 1 秒产出冲一次, 保证识别线程按窗可见 (语种检测等 30 秒可用)
                if (res.Written / TargetRate != before / TargetRate)
                {
                    bw.Flush();
                    onFlushed?.Invoke(resumeOutSamples + res.Written);
                }
            }, skipSrc);
            res.Finish(frames - skipSrc, bw);
            bw.Flush();
            onFlushed?.Invoke(resumeOutSamples + res.Written);
            return res.Written;
        }

        /// <summary>选音轨序号: 找不到音轨抛「没有音轨」。</summary>
        private static int FindAudioTrack(MediaExtractor extractor)
        {
            for (int i = 0; i < extractor.TrackCount; i++)
            {
                var mime = extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime) ?? "";
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return i;
            }
            throw new InvalidOperationException("没有音轨");
        }

        /// <summary>解码音轨为单声道 PCM16; sink 非空时逐缓冲回调单声道样本 (流水线, 带起偏 start), 否则落 outFile (批式)。
        /// skipFrames &gt; 0 时丢弃开头指定帧数 (v3.25.28 E2 修: 续跑跳过断点前源样本)。返回源帧总数 (含跳过段)。</summary>
        private static long DecodeMono(string videoPath, string? outFile,
                                       Action<string>? progress, System.Threading.CancellationToken? cancel,
                                       Action<short[], int, int>? sink, long skipFrames = 0)
        {
            using var extractor = new MediaExtractor();
            extractor.SetDataSource(videoPath);
            int trackIdx = -1;
            MediaFormat? fmt = null;
            for (int i = 0; i < extractor.TrackCount; i++)
            {
                var f = extractor.GetTrackFormat(i);
                var mime = f.GetString(MediaFormat.KeyMime) ?? "";
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) { trackIdx = i; fmt = f; break; }
            }
            if (trackIdx < 0 || fmt == null) throw new InvalidOperationException("没有音轨");
            extractor.SelectTrack(trackIdx);
            int channels;
            try
            {
                channels = fmt.GetInteger(MediaFormat.KeyChannelCount);
            }
            catch (Exception ex)
            {
                // v3.25.28 E11 修: 音频参数缺键的 Java 异常归中文口径, 不裸上屏
                throw new InvalidOperationException("读取音频参数失败, 该片格式暂不支持智能字幕", ex);
            }
            var codec = MediaCodec.CreateDecoderByType(fmt.GetString(MediaFormat.KeyMime)!);
            codec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            FileStream? output = null;
            BinaryWriter? bw = null;
            try
            {
                if (outFile != null)
                {
                    output = new FileStream(outFile, FileMode.Create, FileAccess.Write);
                    bw = new BinaryWriter(output);
                }
                var mono = new short[1 << 14];
                long frames = 0;
                long skip = skipFrames;
                const int IdleLimit = 1500;   // v3.25.28 E11 修: 每轮最多约 20ms (两次 10ms 等待), 约 30 秒无进出判解码器卡死
                int idle = 0;
                var info = new MediaCodec.BufferInfo();
                bool eosIn = false, eosOut = false;
                while (!eosOut)
                {
                    cancel?.ThrowIfCancellationRequested();
                    int inIdx = -1;
                    if (!eosIn)
                    {
                        inIdx = codec.DequeueInputBuffer(10000);
                        if (inIdx >= 0)
                        {
                            var ib = codec.GetInputBuffer(inIdx)!;
                            int len = extractor.ReadSampleData(ib, 0);
                            if (len < 0) { codec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream); eosIn = true; }
                            else { codec.QueueInputBuffer(inIdx, 0, len, extractor.SampleTime, MediaCodecBufferFlags.None); }
                        }
                    }
                    int outIdx = codec.DequeueOutputBuffer(info, 10000);
                    // E11 心跳出口: 既喂不进也吐不出连续约 30 秒 = 解码器卡死, 给失败出口而非永等
                    idle = (inIdx >= 0 || outIdx >= 0) ? 0 : idle + 1;
                    if (idle > IdleLimit) throw new InvalidOperationException("音频解码超时, 该片格式暂不支持智能字幕");
                    if (outIdx >= 0)
                    {
                        if (info.Size > 0)
                        {
                            var ob = codec.GetOutputBuffer(outIdx)!;
                            int total = info.Size / 2 / channels;
                            if (total > mono.Length) mono = new short[total];
                            for (int i = 0; i < total; i++)
                            {
                                float sum = 0;
                                for (int c = 0; c < channels; c++)
                                {
                                    short s = ob.GetShort(i * 2 * channels + c * 2);
                                    sum += s / 32768f;
                                }
                                mono[i] = (short)Math.Max(-32768f, Math.Min(32767f, sum / channels * 32767f));
                            }
                            int emitStart = 0;
                            if (skip > 0)
                            {
                                long d = Math.Min(total, skip);
                                skip -= d;
                                emitStart = (int)d;
                            }
                            int framesHere = total - emitStart;
                            if (framesHere > 0)
                            {
                                if (bw != null) { for (int i = emitStart; i < total; i++) bw.Write(mono[i]); }
                                else sink?.Invoke(mono, emitStart, framesHere);
                            }
                            frames += total;
                            if (frames / (16000L * 60) != (frames - total) / (16000L * 60))
                                progress?.Invoke($"抽取音频 {frames / 16000 / 60} 分钟");
                        }
                        if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) eosOut = true;
                        codec.ReleaseOutputBuffer(outIdx, false);
                    }
                    else if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) { eosOut = true; }
                }
                return frames;
            }
            finally
            {
                bw?.Dispose();
                output?.Dispose();
                codec.Stop();
                codec.Release();
            }
        }
    }
}
