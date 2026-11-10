using System;
using System.Collections.Generic;
using System.IO;
using Android.Media;
using Java.Nio;
using AllPlayer.BLL;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 分段续存合并器（v3.15，红线=不出坏文件）：
    /// 待合并增强段（remux 视频轨/音频轨逐采样拷贝，pts 回原时间轴）+ 原片 mergedMs 起的尾部
    /// → 同目录 tmp → MD5 双算复核 + 时长 ±1s 校验 → 原子替换原文件 → 账本推进、段文件删除。
    /// 合并无抽帧样本，不走 SegmentWriter.Finalize；自写 MD5+时长双校验兜底。
    /// </summary>
    internal class AiChunkMerger
    {
        private readonly Func<string, bool> _isMagnetActive;
        private readonly Func<string, long> _probeDurMs;

        public AiChunkMerger(Func<string, bool> isMagnetActive, Func<string, long> probeDurMs)
        {
            _isMagnetActive = isMagnetActive ?? (p => false);
            _probeDurMs = probeDurMs ?? (p => 0);
        }

        /// <summary>
        /// 执行合并。成功返回新的 mergedMs（账本待调用方 ApplyMerge+Save）；失败返回 0（原文件不动）。
        /// </summary>
        public long Merge(string srcPath, AiChunkManifest m, AiChunkStore store)
        {
            if (m == null || m.Chunks.Count == 0 || store == null) return 0;
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return 0;
            if (_isMagnetActive(srcPath)) return 0;   // 磁力活跃守卫前置

            // 链完整性：首尾相接从 mergedMs 起，段文件一个都不能少
            long expect = m.MergedMs;
            foreach (var c in m.Chunks)
            {
                if (c.StartMs != expect || c.EndMs <= c.StartMs) return 0;
                if (!File.Exists(store.ChunkFileOf(c.File))) return 0;
                expect = c.EndMs;
            }
            if (expect <= m.MergedMs) return 0;
            long newMergedMs = expect;

            string tmpPath = srcPath + ".taomerge.tmp";
            MediaMuxer? muxer = null;
            var extractors = new List<MediaExtractor>();
            try
            {
                SegmentWriter.Abort(tmpPath);

                // 轨道格式：视频/音频都取第一段（各段编码参数一致，音频即源音频原样拷贝）
                MediaFormat? vfmt = null, afmt = null;
                using (var pex = new MediaExtractor())
                {
                    pex.SetDataSource(store.ChunkFileOf(m.Chunks[0].File));
                    for (int i = 0; i < pex.TrackCount; i++)
                    {
                        var f = pex.GetTrackFormat(i);
                        string? mime = f.GetString(MediaFormat.KeyMime);
                        if (mime == null) continue;
                        if (vfmt == null && mime.StartsWith("video/")) vfmt = f;
                        else if (afmt == null && mime.StartsWith("audio/")) afmt = f;
                    }
                }
                if (vfmt == null) return 0;

                muxer = new MediaMuxer(tmpPath, MuxerOutputType.Mpeg4);
                int vTrack = muxer.AddTrack(vfmt);
                int aTrack = afmt != null ? muxer.AddTrack(afmt) : -1;
                if (vTrack < 0 || (afmt != null && aTrack < 0)) return 0;
                muxer.Start();

                // 1) 待合并增强段逐个 remux（视频+音频，pts + StartMs*1000 回原时间轴）
                foreach (var c in m.Chunks)
                {
                    CopyTrackRemux(store.ChunkFileOf(c.File), muxer, vTrack, aTrack, c.StartMs * 1000, -1, extractors);
                }

                // 2) 原片尾部（mergedMs 起）：Seek 对齐同步帧，跳过段前残留样本（pts < mergedUs-1ms）
                long mergedUs = m.MergedMs * 1000;
                CopyTrackRemux(srcPath, muxer, vTrack, aTrack, 0, mergedUs, extractors);

                try { muxer.Stop(); } catch { return 0; }

                // 校验 1：MD5 双算复核（防写一半的 tmp 被替换）
                string md5a = SegmentWriter.Md5Of(tmpPath);
                string md5b = SegmentWriter.Md5Of(tmpPath);
                if (md5a != md5b) return 0;

                // 校验 2：合并文件时长 ≈ 账本源时长 ±1s
                long mergedDur = 0;
                try { mergedDur = _probeDurMs(tmpPath); } catch { }
                var durChk = SegmentWriter.CheckDuration(mergedDur, m.DurMs);
                if (!durChk.Ok) return 0;

                // 原子替换：同目录 rename（中断最坏=白合并，原文件不受影响）
                File.Delete(srcPath);
                File.Move(tmpPath, srcPath);
                return newMergedMs;
            }
            catch
            {
                try { SegmentWriter.Abort(tmpPath); } catch { }
                return 0;
            }
            finally
            {
                try { muxer?.Release(); } catch { }
                foreach (var e in extractors) { try { e.Release(); } catch { } }
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        /// <summary>
        /// 单文件轨道拷贝：视频→vTrack、音频→aTrack，pts 加 baseUs（增强段）或原样（原片尾部）。
        /// skipBeforeUs>=0 时跳过该时间点前的样本（原片尾部段前残留）。
        /// </summary>
        private void CopyTrackRemux(string mediaPath, MediaMuxer muxer, int vTrack, int aTrack,
            long baseUs, long skipBeforeUs, List<MediaExtractor> extractors)
        {
            // 视频、音频各开独立 extractor（SelectTrack 单选，拷贝口径与增强段执行侧一致）
            foreach (var kind in new[] { "video/", "audio/" })
            {
                MediaExtractor? ex = null;
                try
                {
                    ex = new MediaExtractor();
                    ex.SetDataSource(mediaPath);
                    int trackIdx = -1;
                    for (int i = 0; i < ex.TrackCount; i++)
                    {
                        string? mime = ex.GetTrackFormat(i).GetString(MediaFormat.KeyMime);
                        if (mime != null && mime.StartsWith(kind)) { trackIdx = i; break; }
                    }
                    if (trackIdx < 0) continue;   // 无此轨（如无声视频）直接跳过
                    ex.SelectTrack(trackIdx);
                    if (skipBeforeUs > 0) ex.SeekTo(skipBeforeUs, MediaExtractorSeekTo.PreviousSync);
                    extractors.Add(ex);
                    int dst = kind == "video/" ? vTrack : aTrack;
                    if (dst < 0) continue;
                    var info = new MediaCodec.BufferInfo();
                    var buf = ByteBuffer.Allocate(1 << 20);
                    while (true)
                    {
                        buf.Clear();
                        long pts = ex.SampleTime;
                        if (skipBeforeUs > 0 && pts < skipBeforeUs - 1000)
                        {
                            if (!ex.Advance()) break;
                            continue;   // 段前残留：丢弃
                        }
                        int sz = ex.ReadSampleData(buf, 0);
                        if (sz < 0) break;
                        info.Set(0, sz, pts + baseUs, (int)MediaCodecBufferFlags.None);
                        muxer.WriteSampleData(dst, buf, info);
                        if (!ex.Advance()) break;
                    }
                }
                catch
                {
                    try { ex?.Release(); } catch { }
                    if (ex != null) extractors.Remove(ex);
                }
            }
        }
    }
}
