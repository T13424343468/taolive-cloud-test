using System;
using System.IO;
using System.Threading.Tasks;
using Android.Media;
using Java.Nio;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 录制/缓存的 TS 流 → MP4 无损转封装 (系统自带 MediaExtractor/MediaMuxer, 不重编码, 零画质损失)。
    /// 视频要求 H.264/H.265 + AAC 才能进 MP4; 不满足时保留原 TS 并报告失败。
    /// </summary>
    public static class TsRemuxer
    {
        /// <summary>后台转封装: 成功删原 TS, 失败保留; 结果通过回调带回 (已在后台线程)</summary>
        public static void RemuxToMp4Async(string tsPath, Action<bool, string, string> done)
        {
            Task.Run(() =>
            {
                var ok = TryRemux(tsPath, out var mp4, out var err);
                done(ok, ok ? mp4 : tsPath, err);
            });
        }

        private static bool TryRemux(string tsPath, out string mp4Path, out string error)
        {
            mp4Path = Path.ChangeExtension(tsPath, ".mp4");
            error = "";
            MediaExtractor? extractor = null;
            MediaMuxer? muxer = null;
            try
            {
                if (!File.Exists(tsPath) || new FileInfo(tsPath).Length < 4096)
                {
                    error = "缓存内容太小, 像是没录到东西";
                    return false;
                }

                extractor = new MediaExtractor();
                extractor.SetDataSource(tsPath);

                muxer = new MediaMuxer(mp4Path, MuxerOutputType.Mpeg4);
                var trackMap = new int[extractor.TrackCount];
                var selected = 0;
                for (int i = 0; i < extractor.TrackCount; i++)
                {
                    var mime = extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime) ?? "";
                    if (mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("audio/", StringComparison.Ordinal))
                    {
                        trackMap[i] = muxer.AddTrack(extractor.GetTrackFormat(i));
                        extractor.SelectTrack(i);
                        selected++;
                    }
                    else
                    {
                        trackMap[i] = -1;
                    }
                }
                if (selected == 0)
                {
                    error = "没找到可封装的视频/音频轨";
                    return false;
                }

                muxer.Start();
                var buf = ByteBuffer.Allocate(1 << 20);
                var info = new MediaCodec.BufferInfo();
                while (true)
                {
                    var size = extractor.ReadSampleData(buf, 0);
                    if (size < 0) break;
                    var ti = extractor.SampleTrackIndex;
                    if (ti < 0) { extractor.Advance(); continue; }
                    info.Set(0, size, extractor.SampleTime,
                        extractor.SampleFlags == MediaExtractorSampleFlags.Sync ? MediaCodecBufferFlags.KeyFrame : MediaCodecBufferFlags.None);
                    muxer.WriteSampleData(trackMap[ti], buf, info);
                    extractor.Advance();
                }
                muxer.Stop();
                return true; // mp4Path
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                return false;
            }
            finally
            {
                try { muxer?.Release(); } catch { }
                try { extractor?.Release(); } catch { }
            }
        }
    }
}
