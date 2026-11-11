using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AllPlayer.Model;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 业务层 - 录制 TS 落盘后的后台无损转换队列:
    /// ffmpeg -c copy 直接转封装为 MKV, 不重编码、零画质损失。
    /// 找不到 ffmpeg 时保留原文件并在状态栏提示 (电视/手机版通常无 ffmpeg, 自动跳过)。
    /// </summary>
    public static class RecordConverter
    {
        private static readonly Queue<string> _queue = new Queue<string>();
        private static readonly object _lock = new object();
        private static bool _workerRunning;

        /// <summary>转换进度/结果文字 (显示到状态栏)</summary>
        public static event Action<string>? StatusChanged;

        public static void Enqueue(string tsPath)
        {
            lock (_lock)
            {
                _queue.Enqueue(tsPath);
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    Task.Run(WorkerLoop);
                }
            }
        }

        private static async Task WorkerLoop()
        {
            while (true)
            {
                string? ts = null;
                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        _workerRunning = false;
                        return;
                    }
                    ts = _queue.Dequeue();
                }
                await ConvertAsync(ts);
            }
        }

        private static async Task ConvertAsync(string tsPath)
        {
            try
            {
                if (!File.Exists(tsPath) || new FileInfo(tsPath).Length < 1024 * 100)
                    return; // 空文件/几乎没有内容, 跳过

                var ffmpeg = FindFfmpeg();
                if (ffmpeg == null)
                {
                    StatusChanged?.Invoke("后台转换跳过: 未找到 ffmpeg (原 TS 文件已保留)");
                    return;
                }

                var outPath = Path.ChangeExtension(tsPath, ".mkv");
                StatusChanged?.Invoke("后台无损转换中: " + Path.GetFileName(tsPath) + " → MKV");

                // -c copy: 直接搬运音视频流, 无重编码无画质损失; -map 0 保留全部轨道
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{tsPath}\" -map 0 -c copy -f matroska \"{outPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi)!;
                var stderr = await proc.StandardError.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit(600000));

                if (proc.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    if (AppSettings.Current.DeleteTsAfterConvert)
                    {
                        try { File.Delete(tsPath); } catch { }
                    }
                    StatusChanged?.Invoke("无损转换完成: " + Path.GetFileName(outPath));
                }
                else
                {
                    StatusChanged?.Invoke("后台转换失败 (原 TS 已保留): " + Path.GetFileName(tsPath));
                    try { File.Delete(outPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("后台转换出错 (原 TS 已保留): " + ex.Message);
            }
        }

        /// <summary>定位 ffmpeg: 设置指定 → 程序目录 → PATH (仅桌面端; 非桌面端直接放弃)</summary>
        private static string? FindFfmpeg()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            var s = AppSettings.Current;
            if (!string.IsNullOrWhiteSpace(s.FfmpegPath) && File.Exists(s.FfmpegPath))
                return s.FfmpegPath;

            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                var line = p.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
                p.WaitForExit(3000);
                return line?.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
