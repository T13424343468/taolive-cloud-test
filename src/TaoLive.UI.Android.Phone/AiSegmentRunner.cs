using System;
using System.IO;
using System.Collections.Generic;
using Android.Media;
using Java.Nio;
using AllPlayer.BLL;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// Android 逐段增强执行体：MediaExtractor→MediaCodec 解码（getOutputImage YUV_420_888）
    /// → YUV→RGBA → taoai 超分（4×）/插帧 → RGBA→YUV → MediaCodec 编码 → MediaMuxer 写 tmp。
    /// 供 EnhancementCoordinator 的 enhanceStep 插槽；红线：不写差文件
    /// （tmp + MD5 复核 + 时长/抽帧三验 + 原子替换，全部在 SegmentWriter.Finalize）。
    /// 实验档 v1 边界：
    ///  - 只处理 MP4 封装源（mkv/ts 容器 MediaMuxer 不支持，如实跳过，不覆盖）；
    ///  - 音轨用独立 extractor 原样拷贝；超分输出不再人为封顶 (v3.25.1), 4× 上限只受硬件编码器真实能力约束；
    ///  - 插帧仅高档开：v3.25.6 起与超分同开——先超分 4×, 再在输出上插帧;
    ///    v3.25.10 起倍率按设备最高刷新率动态倒推 (探不到屏幕回退内核 4×/2× 固定档)。
    /// </summary>
    public class AiSegmentRunner
    {
        private readonly Func<string, bool> _isMagnetActive;
        private readonly Action<int, int>? _progress;
        private readonly Func<bool>? _paused;
        private readonly int _srTile;
        private readonly bool _enableInterpolate;
        private readonly int _srModel;

        private byte[]? _srOut;          // 超分输出缓冲复用
        private byte[]? _prevSr;         // 插帧前帧 (超分后, 输出尺寸)
        private byte[]? _midRgba;        // 插帧合成帧 (2× 档 t=0.5 / 动态档 k≥2 乒乓)
        private byte[]? _midA;           // 插帧合成帧 (动态档首帧 t=1/N)
        private byte[]? _downRgba;       // 降采样输入缓冲

        public AiSegmentRunner(Func<string, bool>? isMagnetActive, Action<int, int>? progress,
            Func<bool>? paused, int srTile, bool enableInterpolate, int srModel = TaoAIKernel.SrModelAnime)
        {
            _isMagnetActive = isMagnetActive ?? (p => false);
            _progress = progress;
            _paused = paused;
            _srTile = srTile > 0 ? srTile : 256;
            _enableInterpolate = enableInterpolate;
            _srModel = srModel;
        }

        public IReadOnlyList<(double, double)>? FrameSamples { get; private set; }

        /// <summary>v3.20.3 顶栏状态快照: 最近一次增强规格（源帧率/是否插帧/源宽高/输出宽高/输出帧率）, 顶栏透明文字读取。
        /// v3.20.9: 规格属于「最近增强过的那个视频」, 换片即作废——不然新片状态字挂着旧片的帧率分辨率。
        /// v3.25.6: 增加 OutFps——插帧×超分同开后, 输出帧率=源帧率×插帧倍率, 不再硬编码 ×2。</summary>
        internal static (int SrcFps, bool Interp, int SrcW, int SrcH, int OutW, int OutH, int OutFps) LatestSpec { get; private set; }
        internal static bool LatestSpecValid { get; private set; }

        /// <summary>v3.20.9 换片作废规格快照: 新视频增强跑出第一帧前, 状态字回到「处理中未出规格」。</summary>
        internal static void InvalidateSpec() => LatestSpecValid = false;

        /// <summary>执行入口：返回 tmp 的 MD5（mux 完成时点）；失败/跳过返回 null（原文件不动）。</summary>
        public string? Run(string sourcePath, out SourceVideoInfo? info)
        {
            info = null;
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;
                if (_isMagnetActive(sourcePath)) return null;
                if (!IsMp4Container(sourcePath)) return null;

                var writer = new SegmentWriter(_isMagnetActive);
                if (writer.Preflight(sourcePath) != SegmentOutcome.Replaced) return null;

                string tmpPath = SegmentWriter.TmpPathFor(sourcePath);
                SegmentWriter.Abort(tmpPath);

                info = Probe(sourcePath);
                if (info == null || info.Width <= 0 || info.Height <= 0) return null;

                var samples = new SampleBrightness();
                string? md5 = MuxEnhanced(sourcePath, tmpPath, info, samples);
                if (md5 == null)
                {
                    SegmentWriter.Abort(tmpPath);
                    return null;
                }
                FrameSamples = samples.List;
                return md5;
            }
            catch
            {
                try { SegmentWriter.Abort(SegmentWriter.TmpPathFor(sourcePath)); } catch { }
                return null;
            }
        }

        /// <summary>
        /// 分段增强（v3.15 分段续存）：只增强原片 [startMs, endMs) 一步，段终点对齐同步帧落账。
        /// 返回段文件 MD5；actualEndMs = 实际段终点（下一段起点）。失败返回 null（不写任何文件）。
        /// </summary>
        public string? RunChunk(string sourcePath, long startMs, long endMs, string outPath,
            out SourceVideoInfo? info, out long actualEndMs)
        {
            info = null;
            actualEndMs = endMs;
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;
                if (_isMagnetActive(sourcePath)) return null;
                if (!IsMp4Container(sourcePath)) return null;

                info = Probe(sourcePath);
                if (info == null || info.Width <= 0 || info.Height <= 0) return null;

                var samples = new SampleBrightness();
                string? md5 = MuxEnhancedRange(sourcePath, outPath, info, samples,
                    startMs * 1000, endMs * 1000, out long actEndUs);
                if (md5 == null) return null;
                actualEndMs = actEndUs / 1000;
                FrameSamples = samples.List;
                return md5;
            }
            catch { return null; }
        }

        public SourceVideoInfo? Probe(string path)
        {
            try
            {
                using (var ex = new MediaExtractor())
                {
                    ex.SetDataSource(path);
                    MediaFormat? fmt = null;
                    for (int i = 0; i < ex.TrackCount; i++)
                    {
                        var f = ex.GetTrackFormat(i);
                        string? mime = f.GetString(MediaFormat.KeyMime);
                        if (mime != null && mime.StartsWith("video/")) { fmt = f; break; }
                    }
                    if (fmt == null) return null;
                    long durUs = TryLong(fmt, MediaFormat.KeyDuration);
                    int fps = (int)TryLong(fmt, "frame-rate");
                    return new SourceVideoInfo
                    {
                        DurationMs = durUs / 1000,
                        Bitrate = (int)TryLong(fmt, MediaFormat.KeyBitRate),
                        Width = TryInt(fmt, MediaFormat.KeyWidth),
                        Height = TryInt(fmt, MediaFormat.KeyHeight),
                        FrameCountEstimate = fps > 0 ? (int)(durUs / 1000000.0 * fps) : 0
                    };
                }
            }
            catch { return null; }
        }

        internal static bool IsMp4Container(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var head = new byte[12];
                    if (fs.Read(head, 0, head.Length) < 12) return false;
                    string b3 = System.Text.Encoding.ASCII.GetString(head, 4, 4);
                    return b3 == "ftyp" || b3 == "moov" || b3 == "mdat" || b3 == "moof" ||
                           b3 == "free" || b3 == "skip" || b3 == "wide";
                }
            }
            catch { return false; }
        }

        private sealed class SampleBrightness
        {
            public readonly List<(double, double)> List = new List<(double, double)>();
            public void Add(double src, double enh) { if (List.Count < 3) List.Add((src, enh)); }
        }

        private string? MuxEnhanced(string srcPath, string tmpPath, SourceVideoInfo info, SampleBrightness samples)
            => MuxEnhancedRange(srcPath, tmpPath, info, samples, 0, 0, out _);

        /// <summary>核心执行：[chunkStartUs, chunkEndUs) 区间增强；chunkEndUs&lt;=0 = 整条（原路径，行为不变）。</summary>
        private string? MuxEnhancedRange(string srcPath, string outPath, SourceVideoInfo info, SampleBrightness samples,
            long chunkStartUs, long chunkEndUs, out long actualEndUs)
        {
            actualEndUs = chunkEndUs > 0 ? chunkEndUs : 0;
            MediaExtractor? ex = null;
            MediaCodec? decoder = null, encoder = null;
            MediaMuxer? muxer = null;
            try
            {
                ex = new MediaExtractor();
                ex.SetDataSource(srcPath);

                int videoIdx = -1;
                MediaFormat? vfmt = null;
                MediaFormat? afmt = null; int audioIdxInEx = -1;
                for (int i = 0; i < ex.TrackCount; i++)
                {
                    var f = ex.GetTrackFormat(i);
                    string? mime = f.GetString(MediaFormat.KeyMime);
                    if (videoIdx < 0 && mime != null && mime.StartsWith("video/")) { videoIdx = i; vfmt = f; }
                    else if (afmt == null && mime != null && mime.StartsWith("audio/")) { afmt = f; audioIdxInEx = i; }
                }
                if (videoIdx < 0 || vfmt == null) return null;

                // 输出尺寸: v3.25.6 插帧与超分同开——统一走超分 4× 尺寸链 (不再人为封顶 1920 长边,
                // 离线跑没有实时帧预算, 1080p 源也该真出 4×; 只守硬件编码器真实能力上限,
                // 探不到按 4096 长边保守值), 插帧在超分后的输出上做
                int iw = info.Width, ih = info.Height;
                int srInW = iw, srInH = ih;
                int outW, outH;
                {
                    var (maxW, maxH) = EncoderMaxSize();
                    if ((long)iw * 4 > maxW || (long)ih * 4 > maxH)
                    {
                        // 4× 超出编码器能力: 输出压到编码器上限内 (16 宏块对齐), 输入同比缩
                        double k = Math.Min((double)maxW / (iw * 4), (double)maxH / (ih * 4));
                        int outWLimited = (int)(iw * 4 * k) & ~15;
                        int outHLimited = (int)(ih * 4 * k) & ~15;
                        if (outWLimited < 64 || outHLimited < 64) return null;
                        srInW = outWLimited / 4; srInH = outHLimited / 4;
                        srInW = Math.Max(64, srInW & ~1); srInH = Math.Max(64, srInH & ~1);
                        outW = srInW * 4; outH = srInH * 4;
                    }
                    else
                    {
                        outW = (int)(iw * 4); outH = (int)(ih * 4);
                    }
                }

                int targetBitrate = SegmentWriter.TargetBitrate(info.Bitrate);
                double pixRatio = (double)outW * outH / ((double)iw * ih);
                // v3.25.1: 分辨率不再人为封顶后, 码率按真实像素比给 (4× 线性尺寸 = 16× 像素量)
                targetBitrate = SegmentWriter.TargetBitrate((int)(targetBitrate * Math.Min(pixRatio, 16.0)));

                int fps = (int)TryLong(vfmt, "frame-rate");
                if (fps <= 0 || fps > 120) fps = 30;

                // v3.25.10 动态插帧: 倍率按源帧率与设备最高刷新率倒推 (探不到屏幕回退内核 4×/2× 固定档);
                // 内核探测仍用 64×64 假帧试 t=0.25, 只判断内核能否插帧
                int interpMult = 1;
                if (_enableInterpolate)
                {
                    var probeA = new byte[64 * 64 * 4];
                    var probeB = new byte[64 * 64 * 4];
                    var probeM = new byte[64 * 64 * 4];
                    bool kernelQuad = TaoAIKernel.Interpolate(probeA, probeB, 0.25f, 64, 64, probeM) == 0;
                    interpMult = AllPlayer.Model.DeviceCapabilityPolicy.InterpMultiplier(fps, DeviceSpecProbe.MaxHz, kernelQuad);
                }
                int outFps = _enableInterpolate ? fps * interpMult : fps;

                // v3.20.3 顶栏状态快照: 最近一次增强规格（源帧率/插帧/源与输出分辨率/输出帧率）, 顶栏透明文字读取
                LatestSpec = (fps, _enableInterpolate, iw, ih, outW, outH, outFps);
                LatestSpecValid = true;

                string? vmime = vfmt.GetString(MediaFormat.KeyMime);
                if (vmime == null) return null;
                decoder = MediaCodec.CreateDecoderByType(vmime);
                decoder.Configure(vfmt, null, null, MediaCodecConfigFlags.None);
                decoder.Start();

                var efmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, outW, outH);
                efmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
                efmt.SetInteger(MediaFormat.KeyBitRate, targetBitrate);
                // v3.25.6: 插帧时帧率乘倍 (4×/2×), 原来只配源帧率, 插帧档本就少配了一倍
                efmt.SetInteger(MediaFormat.KeyFrameRate, outFps);
                efmt.SetInteger(MediaFormat.KeyIFrameInterval, 2);
                encoder = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
                encoder.Configure(efmt, null, null, MediaCodecConfigFlags.None);
                encoder.Start();

                muxer = new MediaMuxer(outPath, MuxerOutputType.Mpeg4);

                var bufInfo = new MediaCodec.BufferInfo();
                bool decEos = false, encEos = false, muxStarted = false;
                int muxVideoTrack = -1;
                long framesDone = 0;
                long totalEst = info.FrameCountEstimate;
                if (chunkEndUs > 0)
                {
                    // 分段模式：段内帧数按段时长占比估算（抽样点落段内）
                    long fileDurUs = TryLong(vfmt, MediaFormat.KeyDuration);
                    if (fileDurUs > 0)
                        totalEst = Math.Max(1, (long)(totalEst * (double)(chunkEndUs - chunkStartUs) / fileDurUs));
                }
                long s0 = totalEst / 10, s1 = totalEst / 2, s2 = totalEst * 9 / 10;
                long lastFedUs = 0;
                _prevSr = null;   // 段首重置（插帧交接不跨段）

                if (chunkEndUs > 0)
                {
                    // 分段：只喂视频轨（音频走 CopyAudioRange），起点对齐段起最近同步帧
                    ex.SelectTrack(videoIdx);
                    if (chunkStartUs > 0) ex.SeekTo(chunkStartUs, MediaExtractorSeekTo.PreviousSync);
                }

                while (!encEos)
                {
                    while (_paused != null && _paused()) System.Threading.Thread.Sleep(200);

                    // 1) 喂解码器
                    if (!decEos)
                    {
                        int inIdx = decoder.DequeueInputBuffer(10000);
                        if (inIdx >= 0)
                        {
                            ByteBuffer? ib = decoder.GetInputBuffer(inIdx);
                            if (ib == null) continue;   // 无缓冲可喂, 下一轮再取
                            long pts = ex.SampleTime;
                            if (chunkEndUs > 0 && pts >= chunkEndUs && IsSyncSample(ex))
                            {
                                // 段边界：下一同步帧归下一段，本段到此收口（actualEnd 落账该帧 pts）
                                actualEndUs = pts;
                                decoder.QueueInputBuffer(inIdx, 0, 0, pts - chunkStartUs, MediaCodecBufferFlags.EndOfStream);
                                decEos = true;
                            }
                            else
                            {
                                int sz = ex.ReadSampleData(ib, 0);
                                if (sz < 0 || !ex.Advance())
                                {
                                    if (chunkEndUs > 0) actualEndUs = lastFedUs > 0 ? lastFedUs : chunkEndUs;
                                    decoder.QueueInputBuffer(inIdx, 0, 0, pts - chunkStartUs, MediaCodecBufferFlags.EndOfStream);
                                    decEos = true;
                                }
                                else
                                {
                                    lastFedUs = pts;
                                    decoder.QueueInputBuffer(inIdx, 0, sz, chunkStartUs > 0 ? pts - chunkStartUs : pts, 0);
                                }
                            }
                        }
                    }

                    // 2) 解码帧 → RGBA → 增强 → 编码
                    int dOut = decoder.DequeueOutputBuffer(bufInfo, 10000);
                    if (dOut >= 0)
                    {
                        if ((bufInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                        {
                            // 收尾：把最后一帧交给编码器后 EOS
                            if (_prevSr != null && _enableInterpolate)
                            {
                                // 插帧模式：把缓存的最后一帧增强帧也编出去
                                EncodeFrame(encoder, _prevSr, outW, outH, bufInfo.PresentationTimeUs);
                                _prevSr = null;
                            }
                            int eIn = encoder.DequeueInputBuffer(-1);
                            if (eIn >= 0) encoder.QueueInputBuffer(eIn, 0, 0, bufInfo.PresentationTimeUs, MediaCodecBufferFlags.EndOfStream);
                            decoder.ReleaseOutputBuffer(dOut, false);
                        }
                        else if (chunkEndUs > 0 && bufInfo.PresentationTimeUs < 0)
                        {
                            // 分段：起点对齐同步帧可能带进段前残留帧，直接丢弃不编码
                            Image? skipImg = decoder.GetOutputImage(dOut);
                            skipImg?.Close();
                            decoder.ReleaseOutputBuffer(dOut, false);
                        }
                        else
                        {
                            Image? img = decoder.GetOutputImage(dOut);
                            if (img != null)
                            {
                                int gw = img.Width, gh = img.Height;
                                var rgba = new byte[gw * gh * 4];
                                Yuv420ToRgba(img, rgba);
                                img.Close();
                                decoder.ReleaseOutputBuffer(dOut, false);

                                double srcBright = FrameBrightness(rgba, gw, gh);
                                bool sampleHere = (framesDone == s0 || framesDone == s1 || framesDone == s2) && totalEst > 0;

                                // v3.25.6 统一管线: 所有档位先超分, 插帧在超分后的输出尺寸上做
                                byte[] srIn = rgba;
                                int ein = gw, eh = gh;
                                if (srInW != gw || srInH != gh)
                                {
                                    if (_downRgba == null || _downRgba.Length < srInW * srInH * 4) _downRgba = new byte[srInW * srInH * 4];
                                    DownscaleRgba(rgba, gw, gh, _downRgba, srInW, srInH);
                                    srIn = _downRgba; ein = srInW; eh = srInH;
                                }
                                if (_srOut == null || _srOut.Length < ein * 4 * eh * 4 * 4L) _srOut = new byte[ein * 4 * eh * 4 * 4L];
                                int rc = TaoAIKernel.SuperResolve(srIn, ein, eh, _srTile, _srModel, _srOut);
                                if (rc != 0) return null;   // 内核失败 → 整段弃用

                                if (_enableInterpolate)
                                {
                                    if (_prevSr != null)
                                    {
                                        long half = 500000L / Math.Max(fps, 1);
                                        bool done = false;
                                        if (interpMult > 2)
                                        {
                                            // v3.25.10 动态插帧: 帧对间按 k/N 均布合成 N-1 中间帧 (双缓冲乒乓),
                                            // 时间戳按 1/N 格均分; 首帧合成失败本对交回 2×, 中途失败截断本对 (少几帧中间帧, 时间戳不倒流)
                                            if (_midA == null || _midA.Length < outW * outH * 4) _midA = new byte[outW * outH * 4];
                                            if (_midRgba == null || _midRgba.Length < outW * outH * 4) _midRgba = new byte[outW * outH * 4];
                                            int n = interpMult;
                                            long step = 2 * half / n;
                                            if (TaoAIKernel.Interpolate(_prevSr, _srOut, 1f / n, outW, outH, _midA) != 0)
                                            {
                                                interpMult = 2;   // 内核不支持该倍率, 本段起回落 2×
                                            }
                                            else
                                            {
                                                EncodeFrame(encoder, _prevSr, outW, outH, bufInfo.PresentationTimeUs - (n - 1) * step);
                                                EncodeFrame(encoder, _midA, outW, outH, bufInfo.PresentationTimeUs - (n - 2) * step);
                                                bool ok = true;
                                                for (int k = 2; k < n && ok; k++)
                                                {
                                                    if (TaoAIKernel.Interpolate(_prevSr, _srOut, (float)k / n, outW, outH, _midRgba) != 0)
                                                    {
                                                        interpMult = 2;   // 中途失败: 本帧对截断, 下段起回落 2×
                                                        ok = false;
                                                    }
                                                    else
                                                    {
                                                        EncodeFrame(encoder, _midRgba, outW, outH, bufInfo.PresentationTimeUs - (n - 1 - k) * step);
                                                        if (k == n - 1 && sampleHere) samples.Add(srcBright, FrameBrightness(_midRgba, outW, outH));
                                                    }
                                                }
                                                done = true;
                                            }
                                        }
                                        if (!done)
                                        {
                                            // 2× 插帧: 帧对间合成 t=0.5
                                            if (_midRgba == null || _midRgba.Length < outW * outH * 4) _midRgba = new byte[outW * outH * 4];
                                            int rrc = TaoAIKernel.Interpolate(_prevSr, _srOut, 0.5f, outW, outH, _midRgba);
                                            if (rrc != 0) return null;
                                            EncodeFrame(encoder, _prevSr, outW, outH, bufInfo.PresentationTimeUs - half);
                                            EncodeFrame(encoder, _midRgba, outW, outH, bufInfo.PresentationTimeUs);
                                            if (sampleHere) samples.Add(srcBright, FrameBrightness(_midRgba, outW, outH));
                                        }
                                    }
                                    else if (sampleHere) samples.Add(srcBright, FrameBrightness(_srOut, outW, outH));
                                    if (_prevSr == null) _prevSr = new byte[outW * outH * 4];
                                    System.Buffer.BlockCopy(_srOut, 0, _prevSr, 0, outW * outH * 4);
                                }
                                else
                                {
                                    if (sampleHere) samples.Add(srcBright, FrameBrightness(_srOut, outW, outH));
                                    EncodeFrame(encoder, _srOut, outW, outH, bufInfo.PresentationTimeUs);
                                }

                                framesDone++;
                                if (totalEst > 0 && (framesDone % 10 == 0)) _progress?.Invoke((int)(framesDone % int.MaxValue), (int)totalEst);
                            }
                            else decoder.ReleaseOutputBuffer(dOut, false);
                        }
                    }

                    // 3) 编码输出 → mux
                    while (true)
                    {
                        int eOut = encoder.DequeueOutputBuffer(bufInfo, 0);
                        if (eOut == (int)MediaCodecInfoState.OutputFormatChanged)
                        {
                            muxVideoTrack = muxer.AddTrack(encoder.OutputFormat);
                            if (afmt != null) muxer.AddTrack(afmt);   // 音频轨先登记（Start 前必须齐）
                            muxer.Start();
                            muxStarted = true;
                            // 音轨原样拷贝（独立 extractor，PTS 不变；快操作，阻塞可接受）
                            if (afmt != null && !CopyAudioRange(srcPath, muxer, chunkStartUs, chunkEndUs))
                                return null;
                            continue;
                        }
                        if (eOut >= 0)
                        {
                            ByteBuffer? ob = encoder.GetOutputBuffer(eOut);
                            if (ob != null && muxStarted && muxVideoTrack >= 0)
                                muxer.WriteSampleData(muxVideoTrack, ob, bufInfo);
                            encoder.ReleaseOutputBuffer(eOut, false);
                            if ((bufInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0) encEos = true;
                        }
                        else break;
                    }
                }

                if (!muxStarted) return null;
                try { muxer.Stop(); } catch { return null; }
                return SegmentWriter.Md5Of(outPath);
            }
            catch
            {
                return null;
            }
            finally
            {
                try { decoder?.Stop(); } catch { }
                try { decoder?.Release(); } catch { }
                try { encoder?.Stop(); } catch { }
                try { encoder?.Release(); } catch { }
                try { muxer?.Release(); } catch { }
                try { ex?.Release(); } catch { }
            }
        }

        /// <summary>音频轨独立 extractor 拷贝（原样封装，PTS 不变）。失败返回 false → 整段弃用。</summary>
        private static bool CopyAudio(string srcPath, MediaMuxer muxer)
        {
            MediaExtractor? audioEx = null;
            try
            {
                audioEx = new MediaExtractor();
                audioEx.SetDataSource(srcPath);
                for (int i = 0; i < audioEx.TrackCount; i++)
                {
                    var f = audioEx.GetTrackFormat(i);
                    string? mime = f.GetString(MediaFormat.KeyMime);
                    if (mime != null && mime.StartsWith("audio/"))
                    {
                        int muxTrack = muxer.AddTrack(f);
                        if (muxTrack < 0) return false;
                        audioEx.SelectTrack(i);
                        var info = new MediaCodec.BufferInfo();
                        var buf = ByteBuffer.Allocate(1 << 20);
                        while (true)
                        {
                            buf.Clear();
                            
                            int sz = audioEx.ReadSampleData(buf, 0);
                            if (sz < 0) break;
                            info.Set(0, sz, audioEx.SampleTime, (int)MediaCodecBufferFlags.None);
                            muxer.WriteSampleData(muxTrack, buf, info);
                            if (!audioEx.Advance()) break;
                        }
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { audioEx?.Release(); } catch { }
            }
        }

        /// <summary>音频拷贝（分段口径）：[startUs, endUs) 原样封装，pts 回段内（-startUs）；
        /// endUs&lt;=0 = 整条拷贝（原路径）。</summary>
        private static bool CopyAudioRange(string srcPath, MediaMuxer muxer, long startUs, long endUs)
        {
            if (endUs <= 0) return CopyAudio(srcPath, muxer);
            MediaExtractor? audioEx = null;
            try
            {
                audioEx = new MediaExtractor();
                audioEx.SetDataSource(srcPath);
                for (int i = 0; i < audioEx.TrackCount; i++)
                {
                    var f = audioEx.GetTrackFormat(i);
                    string? mime = f.GetString(MediaFormat.KeyMime);
                    if (mime != null && mime.StartsWith("audio/"))
                    {
                        int muxTrack = muxer.AddTrack(f);
                        if (muxTrack < 0) return false;
                        audioEx.SelectTrack(i);
                        if (startUs > 0) audioEx.SeekTo(startUs, MediaExtractorSeekTo.PreviousSync);
                        var info = new MediaCodec.BufferInfo();
                        var buf = ByteBuffer.Allocate(1 << 20);
                        while (true)
                        {
                            buf.Clear();
                            long pts = audioEx.SampleTime;
                            if (pts < startUs) { if (!audioEx.Advance()) break; continue; }   // 跳过段前残留
                            if (pts >= endUs) break;
                            int sz = audioEx.ReadSampleData(buf, 0);
                            if (sz < 0) break;
                            info.Set(0, sz, pts - startUs, (int)MediaCodecBufferFlags.None);
                            muxer.WriteSampleData(muxTrack, buf, info);
                            if (!audioEx.Advance()) break;
                        }
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { audioEx?.Release(); } catch { }
            }
        }

        /// <summary>当前样本是否同步帧（读不到标志按同步帧处理，段边界保守收口）。</summary>
        private static bool IsSyncSample(MediaExtractor ex)
        {
            try { return (ex.SampleFlags & MediaExtractorSampleFlags.Sync) != 0; }
            catch { return true; }
        }

        private void EncodeFrame(MediaCodec encoder, byte[] rgba, int w, int h, long ptsUs)
        {
            int inIdx = encoder.DequeueInputBuffer(10000);
            if (inIdx < 0) return;
            Image? img = encoder.GetInputImage(inIdx);
            if (img != null)
            {
                RgbaToYuv420(rgba, w, h, img);
                img.Close();
                encoder.QueueInputBuffer(inIdx, 0, w * h * 3 / 2, ptsUs, 0);
            }
            else encoder.QueueInputBuffer(inIdx, 0, 0, 0, 0);
        }

        // ---------------- 颜色空间（查表法，帧级开销可接受） ----------------

        internal static void Yuv420ToRgba(Image img, byte[] rgba)
        {
            var planes = img.GetPlanes()!;
            var py = planes[0]; var pu = planes[1]; var pv = planes[2];
            var by = py.Buffer!; var bu = pu.Buffer!; var bv = pv.Buffer!;
            int w = img.Width, h = img.Height;
            int ys = py.RowStride, yps = py.PixelStride;
            int us = pu.RowStride, ups = pu.PixelStride;
            int vs = pv.RowStride, vps = pv.PixelStride;

            for (int row = 0; row < h; row++)
            {
                int yRow = row * ys;
                int uRow = (row / 2) * us;
                int vRow = (row / 2) * vs;
                int outRow = row * w * 4;
                for (int col = 0; col < w; col++)
                {
                    int y = by.Get(yRow + col * yps) & 0xFF;
                    int u = bu.Get(uRow + (col / 2) * ups) & 0xFF;
                    int v = bv.Get(vRow + (col / 2) * vps) & 0xFF;
                    int c = y - 16, d = u - 128, e2 = v - 128;
                    int r = (298 * c + 409 * e2 + 128) >> 8;
                    int g = (298 * c - 100 * d - 208 * e2 + 128) >> 8;
                    int b2 = (298 * c + 516 * d + 128) >> 8;
                    int o = outRow + col * 4;
                    rgba[o] = Clamp8(r);
                    rgba[o + 1] = Clamp8(g);
                    rgba[o + 2] = Clamp8(b2);
                    rgba[o + 3] = 255;
                }
            }
        }

        internal static void RgbaToYuv420(byte[] rgba, int w, int h, Image img)
        {
            var planes = img.GetPlanes()!;
            var py = planes[0]; var pu = planes[1]; var pv = planes[2];
            var by = py.Buffer!; var bu = pu.Buffer!; var bv = pv.Buffer!;
            int ys = py.RowStride, yps = py.PixelStride;
            int us = pu.RowStride, ups = pu.PixelStride;
            int vs = pv.RowStride, vps = pv.PixelStride;

            for (int row = 0; row < h; row++)
            {
                int yRow = row * ys;
                int srcRow = row * w * 4;
                for (int col = 0; col < w; col++)
                {
                    int i = srcRow + col * 4;
                    int r = rgba[i], g = rgba[i + 1], b2 = rgba[i + 2];
                    int y = ((77 * r + 150 * g + 29 * b2) >> 8) * 219 / 255 + 16;
                    by.Put(yRow + col * yps, (sbyte)Clamp8(y));
                }
            }
            for (int row = 0; row < h / 2; row++)
            {
                int uRow = row * us, vRow = row * vs;
                int srcRow = row * 2 * w * 4;
                for (int col = 0; col < w / 2; col++)
                {
                    int i = srcRow + (col * 2) * 4;
                    int r = rgba[i], g = rgba[i + 1], b2 = rgba[i + 2];
                    int u = ((-43 * r - 85 * g + 128 * b2) >> 8) + 128;
                    int v = ((128 * r - 107 * g - 21 * b2) >> 8) + 128;
                    bu.Put(uRow + col * ups, (sbyte)Clamp8(u));
                    bv.Put(vRow + col * vps, (sbyte)Clamp8(v));
                }
            }
        }

        /// <summary>双线性降采样（长边封顶前置步骤）</summary>
        internal static void DownscaleRgba(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
            {
                double fy = (y + 0.5) * sh / dh - 0.5;
                int y0 = Math.Max(0, (int)Math.Floor(fy));
                int y1 = Math.Min(sh - 1, y0 + 1);
                double wy = fy - y0;
                for (int x = 0; x < dw; x++)
                {
                    double fx = (x + 0.5) * sw / dw - 0.5;
                    int x0 = Math.Max(0, (int)Math.Floor(fx));
                    int x1 = Math.Min(sw - 1, x0 + 1);
                    double wx = fx - x0;
                    int d = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        double v =
                            src[(y0 * sw + x0) * 4 + c] * (1 - wx) * (1 - wy) +
                            src[(y0 * sw + x1) * 4 + c] * wx * (1 - wy) +
                            src[(y1 * sw + x0) * 4 + c] * (1 - wx) * wy +
                            src[(y1 * sw + x1) * 4 + c] * wx * wy;
                        dst[d + c] = (byte)Math.Max(0, Math.Min(255, (int)v));
                    }
                }
            }
        }

        internal static double FrameBrightness(byte[] rgba, int w, int h)
        {
            long sum = 0; int n = 0;
            int stride = Math.Max(4, w / 16) * 4;   // 每行抽 16 个样本
            for (int y = 0; y < h; y += 4)
            {
                int rowStart = y * w * 4;
                for (int i = rowStart; i + 3 < rowStart + w * 4 && i + 3 < rgba.Length; i += stride)
                {
                    sum += (rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114) / 1000;
                    n++;
                }
            }
            return n == 0 ? 0 : (double)sum / n;
        }

        private static byte Clamp8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        /// <summary>v3.25.1: 探 H.264 硬件编码器最大支持宽高; 全程反射按名找成员 (绑定差异一次绕开), 探不到按 4096 长边保守值</summary>
        private static (int MaxW, int MaxH) EncoderMaxSize()
        {
            try
            {
                var mcList = new MediaCodecList(MediaCodecListKind.AllCodecs);
                // CodecInfos 在不同绑定里可能是属性或方法: 反射按名找, 不赌绑定版本
                object? infos = typeof(MediaCodecList).GetProperty("CodecInfos")?.GetValue(mcList);
                if (infos == null)
                {
                    var m = typeof(MediaCodecList).GetMethod("GetCodecInfos") ?? typeof(MediaCodecList).GetMethod("getCodecInfos");
                    infos = m?.Invoke(mcList, null);
                }
                if (infos is not System.Collections.IEnumerable e) return (4096, 4096);

                foreach (var item in e)
                {
                    if (item == null) continue;
                    var t = item.GetType();
                    // isEncoder
                    var isEncProp = t.GetProperty("IsEncoder");
                    bool isEnc = isEncProp != null
                        ? (bool)isEncProp.GetValue(item)!
                        : t.GetMethod("getIsEncoder")?.Invoke(item, null) is true;
                    if (!isEnc) continue;
                    // supportedTypes 含 video/avc
                    var stMi = t.GetMethod("GetSupportedTypes") ?? t.GetMethod("getSupportedTypes");
                    var types = stMi?.Invoke(item, null) as string[];
                    if (types == null || !types.Contains(MediaFormat.MimetypeVideoAvc)) continue;
                    // capabilities → videoCapabilities → 支持区间上界
                    var capMi = t.GetMethod("GetCapabilitiesForType") ?? t.GetMethod("getCapabilitiesForType");
                    var caps = capMi?.Invoke(item, new object[] { MediaFormat.MimetypeVideoAvc });
                    var vc = caps?.GetType().GetProperty("VideoCapabilities")?.GetValue(caps);
                    if (vc == null) continue;
                    int w = RangeUpper(vc, "SupportedWidths");
                    int h = RangeUpper(vc, "SupportedHeights");
                    if (w > 0 && h > 0) return (w, h);
                }
            }
            catch { }
            return (4096, 4096);
        }

        /// <summary>反射取 android.util.Range 的 Upper (绑定差异: int 或 Java.Lang.Integer 都能接)</summary>
        private static int RangeUpper(object capabilities, string propName)
        {
            var range = capabilities.GetType().GetProperty(propName)?.GetValue(capabilities);
            if (range == null) return 0;
            return range switch
            {
                Java.Lang.Integer ji => (int)ji,
                int i => i,
                _ => 0,
            };
        }

        private static long TryLong(MediaFormat f, string key) { try { return f.ContainsKey(key) ? f.GetLong(key) : 0; } catch { return 0; } }
        private static int TryInt(MediaFormat f, string key) { try { return f.ContainsKey(key) ? f.GetInteger(key) : 0; } catch { return 0; } }
    }
}
