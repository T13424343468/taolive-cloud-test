using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace AllPlayer.BLL
{
    /// <summary>一个下载任务 (状态与进度都会持久化, 重开应用后续接)</summary>
    public class DownloadTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string DestPath { get; set; } = ""; // 完成后的最终位置
        public string TempPath { get; set; } = ""; // .part 未完成文件 (断点续传靠它)
        public long Total { get; set; } = -1;      // -1 = 未知大小
        public long Downloaded { get; set; }
        public string Status { get; set; } = "排队中"; // 排队中/下载中/已暂停/已完成/失败
        public string Error { get; set; } = "";
        public DateTime AddedAt { get; set; } = DateTime.Now;

        /// <summary>要求暂停 (不持久化)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public volatile bool PauseRequested;
    }

    /// <summary>
    /// 业务层 - 自带下载管理: 断点续传 (网络断开/应用重开都能从已下字节续接),
    /// 支持单个暂停/继续、全部暂停/全部开始、清理已完成; 同时最多 2 个并发。
    /// 纯 .NET 实现, 无平台依赖。
    /// </summary>
    public class DownloadService
    {
        /// <summary>平台口径的可用空间探测 (v3.25.26 真机误报修): 入参目录, 返回该目录所在分区可用字节;
        /// 返回 null = 探测不了, 回落 DriveInfo (桌面) 或不拦。手机侧启动时注入 StatFs 实现——
        /// DriveInfo 在安卓上探 "/" (rootfs) 必误报「存储空间不足」, 不能作为默认口径。</summary>
        public static Func<string, long?>? FreeBytesProbe { get; set; }

        private readonly List<DownloadTask> _tasks = new List<DownloadTask>();
        private readonly object _lock = new object();
        private readonly string _storePath;
        private string _saveDir;
        private const int MaxParallel = 2;

        /// <summary>任务列表变化 (增删/进度/状态), 约 0.4 秒节流</summary>
        public event Action? Changed;

        public DownloadService(string storePath, string saveDir)
        {
            _storePath = storePath;
            _saveDir = saveDir;
        }

        /// <summary>保存位置 (给界面显示用)</summary>
        public string SaveDir => _saveDir;

        /// <summary>改保存位置: 用户在下载管理里自定义目录后调用, 只影响之后新建的任务</summary>
        public void UpdateSaveDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            lock (_lock) { _saveDir = dir; }
        }
        public IReadOnlyList<DownloadTask> Tasks { get { lock (_lock) return _tasks.ToList(); } }

        private static readonly JsonSerializerOptions _jsonOpt = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>启动时读取任务档案: 未完成的全部转"已暂停", 等用户点继续 (断点字节在 .part 文件里)</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(_storePath))
                {
                    var list = JsonSerializer.Deserialize<List<DownloadTask>>(File.ReadAllText(_storePath)) ?? new List<DownloadTask>();
                    foreach (var t in list)
                    {
                        t.PauseRequested = false;
                        if (t.Status == "下载中" || t.Status == "排队中")
                        {
                            t.Status = "已暂停";
                            t.Error = "应用重启, 点继续接着下";
                        }
                        // .part 没了就从头来
                        if (t.Status != "已完成" && !File.Exists(t.TempPath)) t.Downloaded = 0;
                    }
                    lock (_lock) _tasks.AddRange(list);
                }
            }
            catch { }
            Save();
        }

        private void Save()
        {
            try
            {
                lock (_lock)
                    File.WriteAllText(_storePath, JsonSerializer.Serialize(_tasks, _jsonOpt));
            }
            catch { }
        }

        /// <summary>加入下载: 返回给用户的提示文字</summary>
        public string Enqueue(string url, string? name = null)
        {
            var u = url.Trim();
            var low = u.ToLowerInvariant();
            if (low.Contains(".m3u8") || low.Contains(".mpd"))
                return "这是直播流格式, 建议用播放观看, 暂不支持下载";

            lock (_lock)
            {
                if (_tasks.Any(t => t.Url == u && t.Status != "失败" && t.Status != "已完成"))
                    return "这个任务已经在下载列表里了";
                var t = new DownloadTask
                {
                    Url = u,
                    Name = name ?? SuggestName(u),
                };
                t.DestPath = Path.Combine(_saveDir, t.Name);
                t.TempPath = t.DestPath + ".part";
                // 之前下过一部分? 接着用
                if (File.Exists(t.TempPath)) t.Downloaded = new FileInfo(t.TempPath).Length;
                _tasks.Insert(0, t);
            }
            Save();
            Changed?.Invoke();
            Pump();
            return "已加入下载队列";
        }

        private static string SuggestName(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                var seg = path.Split('/').LastOrDefault(s => s.Length > 0);
                if (!string.IsNullOrEmpty(seg)) return Uri.UnescapeDataString(seg);
            }
            catch { }
            return "TaoLive_" + DateTime.Now.ToString("MMdd-HHmmss") + ".mp4";
        }

        public void Pause(string id)
        {
            lock (_lock)
            {
                var t = _tasks.FirstOrDefault(x => x.Id == id);
                if (t == null) return;
                t.PauseRequested = true;
            }
        }

        public void Resume(string id)
        {
            lock (_lock)
            {
                var t = _tasks.FirstOrDefault(x => x.Id == id);
                if (t == null) return;
                if (t.Status == "已完成") return;
                t.PauseRequested = false;
                t.Error = "";
                t.Status = "排队中";
            }
            Save();
            Changed?.Invoke();
            Pump();
        }

        public void PauseAll()
        {
            lock (_lock)
                foreach (var t in _tasks.Where(t => t.Status == "下载中" || t.Status == "排队中"))
                    t.PauseRequested = true;
        }

        public void ResumeAll()
        {
            lock (_lock)
                foreach (var t in _tasks.Where(t => t.Status == "已暂停" || t.Status == "失败"))
                {
                    t.PauseRequested = false;
                    t.Error = "";
                    t.Status = "排队中";
                }
            Save();
            Changed?.Invoke();
            Pump();
        }

        public void Delete(string id, bool deleteFile)
        {
            DownloadTask? t;
            lock (_lock)
            {
                t = _tasks.FirstOrDefault(x => x.Id == id);
                if (t == null) return;
                t.PauseRequested = true;
                _tasks.Remove(t);
            }
            Save();
            Changed?.Invoke();
            Task.Run(() =>
            {
                try
                {
                    if (deleteFile)
                    {
                        if (File.Exists(t.DestPath)) File.Delete(t.DestPath);
                        if (File.Exists(t.TempPath)) File.Delete(t.TempPath);
                    }
                    else if (File.Exists(t.TempPath))
                    {
                        File.Delete(t.TempPath);
                    }
                }
                catch { }
            });
        }

        /// <summary>清掉已完成与失败的记录 (文件保留)</summary>
        public void ClearFinished()
        {
            lock (_lock)
                _tasks.RemoveAll(t => t.Status == "已完成" || t.Status == "失败");
            Save();
            Changed?.Invoke();
        }

        /// <summary>把等待中的任务喂进并发池</summary>
        private void Pump()
        {
            List<DownloadTask> toStart;
            lock (_lock)
            {
                var running = _tasks.Count(t => t.Status == "下载中");
                toStart = _tasks.Where(t => t.Status == "排队中").Take(Math.Max(0, MaxParallel - running)).ToList();
            }
            foreach (var t in toStart) _ = Task.Run(() => RunTask(t));
        }

        private async Task RunTask(DownloadTask t)
        {
            try
            {
                lock (_lock) { if (_tasks.All(x => x.Id != t.Id)) return; t.Status = "下载中"; }
                Changed?.Invoke();

                // v3.25.20 满盘预检: 目标盘剩余空间不足 64MB 直接报"存储空间不足", 不再白跑一趟
                // v3.25.26 修(真机误报): 安卓上 DriveInfo 对 "/" 报的是 rootfs, 误报「存储空间不足」——
                // 探测优先走平台口径 (FreeBytesProbe, 手机侧注入 StatFs); 未注入 (桌面) 维持 DriveInfo; 探测失败不挡下载
                try
                {
                    long? free = FreeBytesProbe?.Invoke(Path.GetDirectoryName(t.TempPath) ?? t.TempPath);
                    if (free == null)
                    {
                        var root = Path.GetPathRoot(Path.GetFullPath(t.TempPath));
                        if (root != null) free = new DriveInfo(root).AvailableFreeSpace;
                    }
                    if (free.HasValue && free.Value < 64L * 1024 * 1024)
                    {
                        t.PauseRequested = true;
                        t.Status = "已暂停";
                        t.Error = "存储空间不足, 清出空间后点继续接着下";
                        Save(); Changed?.Invoke(); Pump();
                        return;
                    }
                }
                catch { }   // 盘符探测失败不挡下载

                Directory.CreateDirectory(Path.GetDirectoryName(t.DestPath)!);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
                if (t.Downloaded > 0)
                    req.Headers.Range = new RangeHeaderValue(t.Downloaded, null);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if ((int)res.StatusCode == 200) t.Downloaded = 0; // 服务器不支持断点, 只能从头下
                res.EnsureSuccessStatusCode();
                if (res.Content.Headers.ContentLength.HasValue)
                    t.Total = res.Content.Headers.ContentLength.Value + t.Downloaded;

                await using var fs = new FileStream(t.TempPath, t.Downloaded > 0 ? FileMode.Append : FileMode.Create);
                await using var stream = await res.Content.ReadAsStreamAsync();
                var buf = new byte[1 << 16];
                var lastNotify = DateTime.UtcNow;
                int n;
                while ((n = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    if (t.PauseRequested)
                    {
                        t.Status = "已暂停";
                        Save();
                        Changed?.Invoke();
                        Pump();
                        return;
                    }
                    await fs.WriteAsync(buf, 0, n);
                    t.Downloaded += n;
                    if ((DateTime.UtcNow - lastNotify).TotalMilliseconds > 400)
                    {
                        lastNotify = DateTime.UtcNow;
                        Save();
                        Changed?.Invoke();
                    }
                }

                if (File.Exists(t.DestPath)) File.Delete(t.DestPath);
                File.Move(t.TempPath, t.DestPath);
                t.Status = "已完成";
                t.Error = "";
                Save();
                Changed?.Invoke();
                Pump();
            }
            catch (Exception ex)
            {
                // v3.25.20: 磁盘满 (ENOSPC) 单独报"存储空间不足", 不再误报成网络中断让用户白重试
                if (IsDiskFull(ex))
                {
                    t.PauseRequested = true;
                    t.Status = "已暂停";
                    t.Error = "存储空间不足, 清出空间后点继续接着下";
                }
                else
                {
                    // 网络断了也算"已暂停": 字节都留着, 点继续就从断点续接
                    t.PauseRequested = true;
                    t.Status = "已暂停";
                    t.Error = "网络中断, 已记住进度 " + FormatSize(t.Downloaded) + ", 点继续接着下";
                    if (ex.Message.Contains("404") || ex.Message.Contains("403") || ex.Message.Contains("401"))
                    {
                        t.Status = "失败";
                        t.Error = "服务器拒绝了下载, 换个资源试试";
                    }
                }
                Save();
                Changed?.Invoke();
                Pump();
            }
        }

        /// <summary>v3.25.20: 识别磁盘满——Linux/Android 上 .NET 抛 IOException, 报文带 "No space left on device"。
        /// v3.25.25 转 public: 智能字幕模型下载 (SmartSubtitleService) 复用同一套 ENOSPC 判定口径。</summary>
        public static bool IsDiskFull(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is IOException)
                {
                    var msg = e.Message;
                    if (msg.Contains("No space left", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("空间不足", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        public static string FormatSize(long b)
        {
            if (b >= 1073741824L) return (b / 1073741824f).ToString("F2") + " GB";
            if (b >= 1048576L) return (b / 1048576f).ToString("F1") + " MB";
            if (b >= 1024) return (b / 1024f).ToString("F0") + " KB";
            return b + " B";
        }
    }
}
