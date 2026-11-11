using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Android.Media;
using Android.OS;
using Java.Nio;
using AllPlayer.BLL;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 实时增强基准执行体（v3.17.0）：MediaExtractor→MediaCodec 解源文件前 N 帧 → RGBA，
    /// 逐帧计时跑 SuperResolve（动漫/通用 × tile 256/512 四档）与 Interpolate（相邻帧对 t=0.5）。
    /// 红线：只读源文件，不写任何视频文件、不动播放；ARM 内核失败记 N/A 不中断。
    /// 用途：真机（一加 15T）内核帧耗时 → 自渲染实时管线（解码→taoai→Surface + AudioTrack）定档。
    /// </summary>
    internal static class AiRealtimeBench
    {
        public const int DefaultFrames = 6;

        public static AiRealtimeBenchLog Run(string sourcePath, string modelsDir, int frames = DefaultFrames)
        {
            var log = new AiRealtimeBenchLog
            {
                TakenAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Device = Build.Model ?? "",
                Source = Path.GetFileName(sourcePath) ?? "",
                Frames = frames
            };
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    log.Ok = false; log.Note = "源文件不存在";
                    return log;
                }
                if (string.IsNullOrEmpty(modelsDir))
                {
                    log.Ok = false; log.Note = "AI 模型目录未就绪";
                    return log;
                }

                // 1) 解码前 N 帧 RGBA（原生尺寸，插帧用）
                var native = DecodeRgbaFrames(sourcePath, frames, out int dw, out int dh);
                if (native.Count == 0 || dw <= 0 || dh <= 0)
                {
                    log.Ok = false; log.Note = "解不出视频帧（格式不支持或文件损坏）";
                    return log;
                }
                log.SrcW = dw; log.SrcH = dh;

                // 2) 超分输入帧（与管线同口径：4× 后长边 ≤1920，否则先降输入）
                var (srW, srH) = AiRealtimeBenchPlan.SrInputSize(dw, dh);
                log.SrInW = srW; log.SrInH = srH;
                List<byte[]> srFrames = new List<byte[]>();
                foreach (var f in native)
                {
                    if (srW == dw && srH == dh) { srFrames.Add(f); continue; }
                    var d = new byte[srW * srH * 4];
                    AiSegmentRunner.DownscaleRgba(f, dw, dh, d, srW, srH);
                    srFrames.Add(d);
                }

                // 3) 超分四档计时（模型装载失败记 N/A 不中断）
                var configs = new (string Label, int ModelId, int Tile)[]
                {
                    ("超分·动漫 tile512", TaoAIKernel.SrModelAnime, 512),
                    ("超分·动漫 tile256", TaoAIKernel.SrModelAnime, 256),
                    ("超分·通用 tile512", TaoAIKernel.SrModelGeneral, 512),
                    ("超分·通用 tile256", TaoAIKernel.SrModelGeneral, 256),
                };
                foreach (var cfg in configs)
                {
                    var item = new AiRealtimeBenchLog.Item
                    {
                        Kind = "sr", Label = cfg.Label, Tile = cfg.Tile, ModelId = cfg.ModelId
                    };
                    try
                    {
                        int rc = TaoAIKernel.LoadSrModel(modelsDir, cfg.ModelId);
                        if (rc != 0) { item.Note = "模型装载失败"; item.FailFrames = frames; }
                        else
                        {
                            var outBuf = new byte[(long)srW * 4 * srH * 4 * 4];
                            long totalMs = 0;
                            foreach (var rgba in srFrames)
                            {
                                var sw = Stopwatch.StartNew();
                                int r = TaoAIKernel.SuperResolve(rgba, srW, srH, cfg.Tile, cfg.ModelId, outBuf);
                                sw.Stop();
                                if (r == 0) { totalMs += sw.ElapsedMilliseconds; item.OkFrames++; }
                                else item.FailFrames++;
                            }
                            if (item.OkFrames > 0) item.AvgMs = (double)totalMs / item.OkFrames;
                        }
                    }
                    catch (Exception ex) { item.Note = ex.Message; }
                    log.Items.Add(item);
                }

                // 4) 插帧 RIFE 计时（原生尺寸相邻帧对；内核不支持则记 N/A）
                if (native.Count >= 2)
                {
                    var item = new AiRealtimeBenchLog.Item { Kind = "rife", Label = "插帧 RIFE", ModelId = -1 };
                    try
                    {
                        TaoAIKernel.Init(modelsDir, 4);   // 幂等；插帧模型常驻装载
                        var outBuf = new byte[(long)dw * dh * 4];
                        long totalMs = 0;
                        for (int i = 0; i + 1 < native.Count; i++)
                        {
                            var sw = Stopwatch.StartNew();
                            int r = TaoAIKernel.Interpolate(native[i], native[i + 1], 0.5f, dw, dh, outBuf);
                            sw.Stop();
                            if (r == 0) { totalMs += sw.ElapsedMilliseconds; item.OkFrames++; }
                            else { item.FailFrames++; if (item.FailFrames == 1) item.Note = "内核返回 " + r; }
                        }
                        if (item.OkFrames > 0) item.AvgMs = (double)totalMs / item.OkFrames;
                        if (item.FailFrames > 0 && item.OkFrames == 0) item.Note = "RIFE 不支持该设备 (ARM N/A)";
                    }
                    catch (Exception ex) { item.Note = ex.Message; }
                    log.Items.Add(item);
                }
                return log;
            }
            catch (Exception ex2)
            {
                log.Ok = false; log.Note = ex2.Message;
                return log;
            }
        }

        /// <summary>解源文件前 maxFrames 帧 RGBA（H.264/H.265 硬解；失败返回空列表不抛）。</summary>
        private static List<byte[]> DecodeRgbaFrames(string path, int maxFrames, out int w, out int h)
        {
            w = 0; h = 0;
            var frames = new List<byte[]>();
            MediaExtractor? ex = null;
            MediaCodec? dec = null;
            try
            {
                ex = new MediaExtractor();
                ex.SetDataSource(path);
                MediaFormat? fmt = null;
                for (int i = 0; i < ex.TrackCount; i++)
                {
                    var f = ex.GetTrackFormat(i);
                    string? mime = f.GetString(MediaFormat.KeyMime);
                    if (mime != null && mime.StartsWith("video/")) { fmt = f; ex.SelectTrack(i); break; }
                }
                if (fmt == null) return frames;

                string? vmime = fmt.GetString(MediaFormat.KeyMime);
                if (vmime == null) return frames;
                w = AiSegmentRunnerProbe(fmt);
                h = AiSegmentRunnerProbeH(fmt);
                dec = MediaCodec.CreateDecoderByType(vmime);
                dec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
                dec.Start();

                var info = new MediaCodec.BufferInfo();
                bool fedEos = false, decEos = false;
                int idle = 0;
                while (frames.Count < maxFrames && !decEos)
                {
                    if (!fedEos)
                    {
                        int inIdx = dec.DequeueInputBuffer(10000);
                        if (inIdx >= 0)
                        {
                            ByteBuffer? ib = dec.GetInputBuffer(inIdx);
                            if (ib == null) break;   // 输入缓冲拿不到：按失败收尾，避免空转
                            int sz = ex.ReadSampleData(ib, 0);
                            if (sz < 0 || !ex.Advance())
                            {
                                dec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                                fedEos = true;
                            }
                            else dec.QueueInputBuffer(inIdx, 0, sz, ex.SampleTime, 0);
                        }
                    }

                    int outIdx = dec.DequeueOutputBuffer(info, 10000);
                    if (outIdx >= 0)
                    {
                        idle = 0;
                        if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) decEos = true;
                        else
                        {
                            Image? img = dec.GetOutputImage(outIdx);
                            if (img != null)
                            {
                                int iw = img.Width, ih = img.Height;
                                var rgba = new byte[iw * ih * 4];
                                AiSegmentRunner.Yuv420ToRgba(img, rgba);
                                img.Close();
                                w = iw; h = ih;
                                frames.Add(rgba);
                            }
                            dec.ReleaseOutputBuffer(outIdx, false);
                        }
                    }
                    else if (outIdx == -1)
                    {
                        if (fedEos && ++idle > 50) decEos = true;   // EOS 后拿不到输出别死等
                    }
                }
                return frames;
            }
            catch { return frames; }
            finally
            {
                try { dec?.Stop(); } catch { }
                try { dec?.Release(); } catch { }
                try { ex?.Release(); } catch { }
            }
        }

        private static int AiSegmentRunnerProbe(MediaFormat f)
        { try { return f.ContainsKey(MediaFormat.KeyWidth) ? f.GetInteger(MediaFormat.KeyWidth) : 0; } catch { return 0; } }
        private static int AiSegmentRunnerProbeH(MediaFormat f)
        { try { return f.ContainsKey(MediaFormat.KeyHeight) ? f.GetInteger(MediaFormat.KeyHeight) : 0; } catch { return 0; } }
    }
}
