using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 业务层 - 磁力链接 -> 边下边播服务 (MonoTorrent 3.x 流式接口)。
    /// 流程: 解析磁力 -> DHT 获取种子信息 -> 选最大的视频文件 ->
    ///       StreamProvider 创建内置 HTTP 流 (自动优先下载被读取的分片)。
    /// </summary>
    public class MagnetStreamService
    {
        private ClientEngine? _engine;
        private TorrentManager? _manager;
        private MonoTorrent.Streaming.IHttpStream? _httpStream;
        private LocalStreamServer? _lanProxy; // 局域网代理 (投屏用)
        private long _streamLength;
        private string? _savePath;
        private bool _persist; // true = 边播边存, 停止后不清理文件

        /// <summary>状态文字(给界面显示)</summary>
        public event Action<string>? StatusChanged;
        /// <summary>缓冲进度 0~1</summary>
        public event Action<double>? BufferProgress;

        /// <summary>局域网可访问的播放流地址 (给电视投屏用); 流没起来时为 null</summary>
        public string? LanStreamUrl => _lanProxy?.LanUrl;

        /// <summary>选中文件名 (含扩展名): 元数据到手后可读, 供磁力自动检测给列表起名 (v3.10)</summary>
        public string? FoundFileName { get; private set; }
        /// <summary>选中文件大小 (字节): 元数据到手后可读 (v3.10)</summary>
        public long FoundFileSize { get; private set; }

        /// <summary>
        /// 启动磁力播放, 返回可交给播放器的本地地址。
        /// saveDir 非空时 = 边播边存模式, 文件直接存到指定目录 (不清理);
        /// 为空时存临时目录, 停止播放后清理。
        /// </summary>
        public async Task<string> StartAsync(string magnetUri, string? saveDir, CancellationToken ct, bool warmHead = true)
        {
            if (!magnetUri.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不是有效的磁力链接");

            StatusChanged?.Invoke("连接 DHT 网络...");

            if (!string.IsNullOrWhiteSpace(saveDir))
            {
                _savePath = saveDir;
                _persist = true;
            }
            else
            {
                _savePath = Path.Combine(Path.GetTempPath(), "AllPlayer", Guid.NewGuid().ToString("N"));
            }
            Directory.CreateDirectory(_savePath);

            _engine = new ClientEngine(new EngineSettingsBuilder
            {
                // 允许同时连更多节点, 提升冷门资源的速度
                MaximumConnections = 200,
            }.ToSettings());

            var magnet = MagnetLink.Parse(magnetUri);
            _manager = await _engine.AddStreamingAsync(magnet, _savePath);
            await _manager.StartAsync();

            // 等待拿到种子元数据 (磁力链接本身不含文件列表)
            // v3.25.2 黑屏首帧·磁力: 轮询 1s→150ms, 元数据到位后最多再等 150ms 就交给播放器,
            // 不再死等下一个 1 秒刻度 (平均省 ~0.5s); 120 秒总上限不变 (秒表计时)
            var metaSw = System.Diagnostics.Stopwatch.StartNew();
            while (!_manager.HasMetadata)
            {
                ct.ThrowIfCancellationRequested();
                if (metaSw.ElapsedMilliseconds > 120_000) throw new TimeoutException("120 秒内未获取到资源信息, 该磁力可能已失效");
                await Task.Delay(150, ct);
                StatusChanged?.Invoke($"正在获取资源信息... {(int)(metaSw.ElapsedMilliseconds / 1000)}s");
            }

            // 选最大的文件作为播放目标
            var file = _manager.Files.OrderByDescending(f => f.Length).First();
            FoundFileName = Path.GetFileName(file.Path);   // v3.10: 检测结果带出去, 列表直接用真名
            FoundFileSize = file.Length;
            StatusChanged?.Invoke($"已找到: {FoundFileName} ({file.Length / 1024 / 1024} MB), 建立边下边播...");

            // 内置 HTTP 流: 自动优先下载播放位置附近的分片, 支持 Range/206
            var provider = _manager.StreamProvider ?? throw new InvalidOperationException("磁力流提供方不可用");
            _httpStream = await provider.CreateHttpStreamAsync(file);
            var httpStream = _httpStream;   // v3.25.2: 后台抢跑用局部捕获, 防停止路径清空字段的竞态

            // v3.25.2 黑屏首帧·磁力: 首块抢跑 —— 播放器拿到地址前先拉起头部 1MB,
            // 第一片在开播前就在路上; 走既有内置 HTTP 地址按序读, 失败静默 (播放器按需再拉)。
            // v3.25.3 推演清零: 仅播放场景抢跑 (检测只验元数据, 不白拉流量); WebResponse 释放防连接泄漏
            if (warmHead)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(httpStream.FullUri);
                        using var resp = req.GetResponse();
                        using var s = resp.GetResponseStream();
                        if (s != null)
                        {
                            var buf = new byte[64 * 1024];
                            long got = 0;
                            int n;
                            // v3.25.4 清零: 引擎停了就收手 (防停止后幽灵请求挂到连接超时)
                            while (got < 1024 * 1024 && _running && (n = await s.ReadAsync(buf, 0, buf.Length)) > 0) got += n;
                        }
                    }
                    catch { }
                });
            _streamLength = file.Length;

            // 局域网代理: 磁力自带流只听本机回环, 电视够不着; 包一层局域网服务, 投屏才有地址可给
            _lanProxy = new LocalStreamServer(OpenStreamAt, file.Length, Path.GetFileName(file.Path));
            _lanProxy.Start();

            // 后台持续汇报缓冲进度
            _ = Task.Run(async () =>
            {
                while (_running)
                {
                    try
                    {
                        BufferProgress?.Invoke(_manager.Progress);
                        StatusChanged?.Invoke(
                            $"边下边播中 | 缓冲 {FormatBytes(_manager.Monitor.DownloadRate)} | 总进度 {_manager.Progress:P1}");
                    }
                    catch { }
                    await Task.Delay(1000);
                }
            });

            return _httpStream.FullUri;
        }

        private volatile bool _running = true;

        /// <summary>按偏移打开一段磁力流 (给局域网代理用; 电视拖动进度时带 Range 进来)</summary>
        private Stream OpenStreamAt(long offset)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(_httpStream!.FullUri);
            if (offset > 0) req.AddRange(offset);
            var resp = req.GetResponse();
            return resp.GetResponseStream()!;
        }

        private static string FormatBytes(double bps)
        {
            string[] u = { "B/s", "KB/s", "MB/s" };
            int i = 0;
            while (bps >= 1024 && i < u.Length - 1) { bps /= 1024; i++; }
            return $"{bps:F1} {u[i]}";
        }

        public async Task StopAsync()
        {
            _running = false;
            try { _lanProxy?.Dispose(); } catch { }
            _lanProxy = null;
            try
            {
                if (_manager != null) await _manager.StopAsync();
                if (_engine != null) await _engine.StopAllAsync();
                (_engine as IDisposable)?.Dispose();
            }
            catch { }
            // 边播边存模式下保留文件; 临时目录在退出时由系统清理
            if (!_persist && _savePath != null)
            {
                try { Directory.Delete(_savePath, true); } catch { }
            }
            _manager = null;
            _engine = null;
            _httpStream = null;
        }
    }
}
