using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AllPlayer.BLL;
using AllPlayer.Model;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 智能字幕编排 (v3.25.23 一期 / v3.25.27 流水线改造): 开关开启后全自动——
    /// 查模型 (无则后台下载, 进度上屏) -> 边抽取边识别边翻译边挂载: 抽取线程顺序写 16k float,
    /// 识别线程按 10 分钟窗「凑满一段跑一段」(原文识别 + 英译 + 逐句翻中同窗完成),
    /// 每窗落盘 .orig/.zh.srt 并重挂, 首窗字幕几分钟内可见, 外文片不再整片跑两遍 whisper。
    /// 部分成果持久化: 每窗成果即落 SRT + 进度哨兵 (已处理样本偏移/语种/动作);
    /// 再次 Kick 哨兵在 = 缓存不完整——先挂已有部分字幕照常看, 后台从哨兵偏移续跑
    /// 「重抽音频 -> 识别 -> 翻译」, 新窗整文件重写回写同一 SRT (时间轴去重, 序号续编) 再重挂;
    /// 处理到片尾清哨兵转正为完整缓存。E9 判定升级: 哨兵无 = 完整、哨兵在 = 续跑。
    /// </summary>
    internal class SmartSubtitleService
    {
        private readonly Func<string?> _currentPath;
        private readonly Action<string> _status;        // 底栏状态小字 (UI 线程由调用方保证)
        private readonly Action<string> _mountSrt;      // 挂字幕 (VideoHost.AddSlave)
        private readonly string _cacheDir;

        private CancellationTokenSource? _cts;
        private string? _busyPath;                      // 正在处理的片
        private readonly HashSet<string> _mounted = new();   // 本次会话已挂过的 SRT
        private readonly object _mountedLock = new();   // v3.25.25 E11 修: UI 线程 (Kick) 与后台线程 (RunAsync) 并发读写

        private const long WindowSamples = 10L * 60 * 16000;   // 识别窗: 10 分钟 (9,600,000 样本)

        public SmartSubtitleService(Func<string?> currentPath, Action<string> status, Action<string> mountSrt, string cacheDir)
        {
            _currentPath = currentPath;
            _status = status;
            _mountSrt = mountSrt;
            _cacheDir = cacheDir;
        }

        /// <summary>对当前片触发一次智能字幕。
        /// pathHint: 起播入口带来的本次要播路径——ShowPlayer 时内核 CurrentPath 可能还是上一条 (v3.25.8 同款时序), 优先用它。</summary>
        public void Kick(string? pathHint = null)
        {
            if (!AppSettings.Current.SmartSubtitleEnabled) return;
            var path = pathHint ?? _currentPath();
            if (string.IsNullOrEmpty(path)) return;

            // v3.25.25 E14 修: 磁力/m3u8/http(s) 网络直链抽不了音频, 开智能字幕必败, 直接中文提示跳过
            if (SmartSubPlan.IsNetworkPath(path))
            {
                _status("网络视频暂不支持智能字幕");
                return;
            }

            // 同片进行中直接返回: 后台流水线每窗自己挂新字幕, 这里不代挂 (E16 真机口径)
            if (_busyPath == path) return;

            // v3.25.27 E9 判定升级: 哨兵在 = 缓存不完整 (部分成果续跑); 哨兵无 = 完整缓存直挂
            var sentPath = SubSentinel.SentPath(_cacheDir, path);
            var cachedOrig = SmartSubPlan.SrtCachePath(_cacheDir, path, ".orig.srt");
            var cachedZh = SmartSubPlan.SrtCachePath(_cacheDir, path, ".zh.srt");
            var (mounts, resume) = SmartSubPlan.KickPlan(cachedOrig, cachedZh, File.Exists(sentPath), File.Exists);
            if (mounts.Count > 0)
            {
                foreach (var m in mounts)
                {
                    lock (_mountedLock) { if (!_mounted.Add(m)) continue; }
                    _mountSrt(m);
                }
                if (!resume) { _status("智能字幕: 已挂载"); return; }
                // 部分成果: 已有字幕先挂上照常看, 后台从哨兵偏移续跑补窗
                _status("智能字幕: 续跑中");
            }
            var hasSentinel = resume;

            Interlocked.Exchange(ref _cts, new CancellationTokenSource())?.Cancel();
            var cts = _cts!;
            _busyPath = path;
            if (!hasSentinel) _status("智能字幕: 准备模型");
            _ = Task.Run(() => RunAsync(path, cts.Token));
        }

        public void Cancel() { try { _cts?.Cancel(); } catch { } _busyPath = null; }

        /// <summary>抽取线程状态: Flushed = 全片绝对已冲盘样本数 (每冲盘点回报, 识别线程可见口径);
        /// Done/Total 判终局与片尾。不用文件长度判可用——续跑时 .pcm 头部是空洞占位, 长度含空洞会误判。</summary>
        private sealed class ExtractState
        {
            private long _flushed;
            public long Flushed { get => Interlocked.Read(ref _flushed); set => Interlocked.Exchange(ref _flushed, value); }   // long 不能 volatile (32 位原子性), 走 Interlocked
            public volatile bool Done;
            public long Total;
        }

        private async Task RunAsync(string videoPath, CancellationToken ct)
        {
            try
            {
                var modelPath = SmartSubPlan.ModelPath(_cacheDir);
                if (!SmartSubPlan.ModelReady(modelPath))
                    await DownloadModel(modelPath, ct);

                Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
                var pcmFile = SmartSubPlan.SrtCachePath(_cacheDir, videoPath, ".pcm");
                var srtOrig = SmartSubPlan.SrtCachePath(_cacheDir, videoPath, ".orig.srt");
                var srtZh = SmartSubPlan.SrtCachePath(_cacheDir, videoPath, ".zh.srt");
                var sentPath = SubSentinel.SentPath(_cacheDir, videoPath);

                // 哨兵在 = 断点续跑: 语种/动作不重复检测 (闭环补丁③), 音频从哨兵偏移续抽 (补丁②, .pcm 不持久)
                var hasSentinel = SubSentinel.TryRead(sentPath, out var resumeOff, out var lang, out var actionInt);

                _status("智能字幕: 抽取音频");
                var st = new ExtractState();
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var extTask = Task.Run(() =>
                {
                    st.Total = AudioPcmExtractor.ExtractStreaming(videoPath, pcmFile,
                        hasSentinel ? resumeOff : 0, s => _status("智能字幕: " + s), runCts.Token,
                        flushed => st.Flushed = flushed);
                    st.Total += hasSentinel ? resumeOff : 0;   // 绝对口径: 续跑产出 + 断点存量
                    st.Flushed = st.Total;
                    st.Done = true;
                    return st.Total;
                }, runCts.Token);

                var ctx = TaowhisperApi.Init(modelPath);
                if (ctx == IntPtr.Zero)
                {
                    _status("智能字幕: 内核加载失败");
                    runCts.Cancel();
                    try { File.Delete(pcmFile); } catch { }   // v3.25.28 E6 修: 内核加载失败路径补删 pcm, 不留残留 (此前绕过内层 finally)
                    if (_busyPath == videoPath) _busyPath = null;
                    return;
                }
                try
                {
                    if (!hasSentinel)
                    {
                        // 语种检测改用首窗前 30 秒: 等抽取线程冲出 30 秒 PCM 即可, 不等整片
                        var probeNeed = 30L * 16000;
                        await WaitSamplesAsync(probeNeed, st, extTask, ct);
                        if (extTask.IsCompleted) await extTask;   // 抽取失败 (没有音轨等) 即抛
                        // v3.25.28 E1 修: 首窗零产出判定改 Flushed (Interlocked 可见口径)——WaitSamplesAsync 因
                        // 「30 秒已冲盘」放行时抽取线程尚未返回, st.Total 还是 0, 旧判定对一切超 30 秒的片误报零产出;
                        // Total<=0 只在 st.Done 为真时才当真零产出
                        if (st.Done && st.Flushed <= 0) { _status("智能字幕: 没抽到音频"); if (_busyPath == videoPath) _busyPath = null; return; }
                        int probe = (int)Math.Min(probeNeed, st.Flushed);
                        var probePcm = ReadPcm(pcmFile, 0, probe);
                        lang = TaowhisperApi.Detect(ctx, probePcm, probe) ?? "unknown";
                        actionInt = (int)SmartSubPlan.Decide(lang);
                        SubSentinel.Write(sentPath, resumeOff, lang, actionInt);   // 语种与动作存哨兵, 续跑不再检测
                        hasSentinel = true;
                    }
                    var action = (SmartSubAction)actionInt;

                    // 翻译模型在首窗识别完成前确保就绪: 与识别并行预取, 首窗翻译不干等模型下载
                    var transDir = SmartSubPlan.TransModelDir(_cacheDir);
                    var transReady = action == SmartSubAction.TranscribePlusTranslate
                        ? Task.Run(async () => { if (!SmartSubPlan.TransModelReady(transDir)) await EnsureTransModel(transDir, ct); }, ct)
                        : Task.CompletedTask;
                    _status("智能字幕: 识别中");

                    // 既有部分字幕读回: 整文件重写口径, 新窗时间轴去重合并后续编号 (闭环补丁①)
                    var origSegs = File.Exists(srtOrig) ? SmartSubPlan.ParseSrt(File.ReadAllText(srtOrig))
                                                        : new List<(long, long, string)>();
                    var zhSegs = File.Exists(srtZh) ? SmartSubPlan.ParseSrt(File.ReadAllText(srtZh))
                                                    : new List<(long, long, string)>();

                    long off = resumeOff;
                    bool runFailed = false;   // v3.25.28 E8 修: 识别失败窗标志——不动哨兵不写 SRT, 留待下次续跑幂等重放
                    bool transDown = false;   // v3.25.28 E10 修: 翻译模型下载失败降级标志 (只出原文, 只提示一次)
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (extTask.IsCompleted) await extTask;   // 抽取失败即抛
                        long avail = st.Done ? st.Total : st.Flushed;
                        if (avail <= off)
                        {
                            if (st.Done) break;                   // 片尾到 (含续跑已到尾)
                            await Task.Delay(300, ct); continue;  // 流上还没到, 等抽取
                        }
                        if (avail < off + WindowSamples && !st.Done) { await Task.Delay(300, ct); continue; }
                        int n = (int)Math.Min(WindowSamples, avail - off);   // 凑满整窗才跑, 终局放行尾窗

                        // 原文识别 (task=0); 识别时间基准 = 全片绝对样本偏移 (E8 公式推广到续跑: 断点偏移+窗内偏移)
                        var pcm = ReadPcm(pcmFile, off, n);
                        int rc = TaowhisperApi.Run(ctx, pcm, n, 0, lang, out var text);
                        if (rc != 0) rc = TaowhisperApi.Run(ctx, pcm, n, 0, lang, out text);   // v3.25.28 E8 修: tao_run 返回码不再丢弃, 失败重试一次
                        ct.ThrowIfCancellationRequested();   // v3.25.28 E3 修: native Run 不可中途取消, 返回后先复查归属再收尾
                        if (rc != 0)
                        {
                            // E8: 该窗识别失败——不动哨兵不写 SRT (下次续跑从本窗重放), 上屏提示
                            _status("智能字幕: 本段识别失败, 稍后重试");
                            runFailed = true; break;
                        }
                        origSegs = SmartSubPlan.MergeSegments(origSegs, WinSegs(text, off));

                        if (action == SmartSubAction.TranscribePlusTranslate)
                        {
                            // 同窗完成英译 (task=1) + 逐句翻中
                            int rcEn = TaowhisperApi.Run(ctx, pcm, n, 1, lang, out var textEn);
                            if (rcEn != 0) rcEn = TaowhisperApi.Run(ctx, pcm, n, 1, lang, out textEn);   // E8: 同口径重试一次
                            if (rcEn != 0) { _status("智能字幕: 本段识别失败, 稍后重试"); runFailed = true; break; }

                            // v3.25.28 E10 修: 翻译模型下载失败降级为只出原文+提示一次, 不陪葬整条流水线 (取消仍照常上抛)
                            try { await transReady; }   // 首窗翻译前翻译模型已确保就绪
                            catch (OperationCanceledException) { throw; }
                            catch { transDown = true; _status("智能字幕: 翻译模型下载失败, 先出原文"); }

                            if (!transDown && TaotranslateApi.EnsureLoaded(transDir))
                            {
                                var enSegs = WinSegs(textEn, off);
                                var zhWin = new List<(long, long, string)>();
                                for (int i = 0; i < enSegs.Count; i++)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var t = TaotranslateApi.Translate(enSegs[i].text);
                                    if (!string.IsNullOrWhiteSpace(t)) zhWin.Add((enSegs[i].t0, enSegs[i].t1, t));
                                }
                                zhSegs = SmartSubPlan.MergeSegments(zhSegs, zhWin);
                            }
                            else if (!transDown)
                            {
                                _status("智能字幕: 翻译内核加载失败");
                            }
                        }

                        // v3.25.28 E3 修: 写盘/挪哨兵/挂载前最后复查归属, 换片/取消立即收手, 旧片字幕不挂新片
                        ct.ThrowIfCancellationRequested();
                        // SRT 整文件重写, 不真追加 (闭环补丁①): 崩溃落在「SRT 已写/哨兵未挪」之间时,
                        // 从哨兵窗幂等重放, 靠时间轴定序去重, 无重句无丢句
                        File.WriteAllText(srtOrig, SmartSubPlan.BuildSrt(origSegs));
                        if (zhSegs.Count > 0) File.WriteAllText(srtZh, SmartSubPlan.BuildSrt(zhSegs));

                        // 哨兵挪动必须在 SRT 落盘之后 (闭环补丁③: SRT 为准, 哨兵冗余)
                        off += n;
                        SubSentinel.Write(sentPath, off, lang, actionInt);

                        // 每窗直调重挂 (_mounted 幂等不阻塞重挂; E16: 重复 AddSlave 是否叠显待真机确认)
                        _mountSrt(srtOrig);
                        if (zhSegs.Count > 0) _mountSrt(srtZh);
                        lock (_mountedLock) { _mounted.Add(srtOrig); _mounted.Add(srtZh); }
                        _status($"智能字幕: {off / 960000} 分钟");
                    }

                    if (!runFailed)
                    {
                        // 处理到片尾: 清哨兵 = 完整缓存转正 (pcm 清扫沿用 E13 口径);
                        // E8 修: 失败窗不动哨兵, 下次 Kick 从断点续跑
                        SubSentinel.Clear(sentPath);
                        _status(action == SmartSubAction.TranscribePlusTranslate ? "智能字幕: 中文字幕已挂载" : "智能字幕: 已挂载");
                    }
                }
                finally
                {
                    TaowhisperApi.Free(ctx);
                    runCts.Cancel();                              // 中断时叫停还在跑的抽取
                    try { File.Delete(pcmFile); } catch { }
                    if (_busyPath == videoPath) _busyPath = null;   // v3.25.25 E10 修: 同守卫
                }
            }
            catch (OperationCanceledException) { _status("智能字幕: 已取消"); if (_busyPath == videoPath) _busyPath = null; }   // v3.25.25 E10 修: 旧任务取消不清新片标记
            catch (Exception ex)
            {
                // v3.25.28 E11 修: 统一走中文口径——磁盘满单列, Java/native 原始异常不裸上屏
                _status("智能字幕: " + DescribeError(ex));
                if (_busyPath == videoPath) _busyPath = null;   // v3.25.25 E10 修: 同守卫
            }
        }

        /// <summary>失败文案归中文口径 (v3.25.28 E11 修): 磁盘满单列; Java/native 层原始异常
        /// (如 KeySampleRate 缺键、codec 初始化失败抛出的 Java.Lang.xxx) 不裸上屏, 归成可读中文。</summary>
        private static string DescribeError(Exception ex)
        {
            if (DownloadService.IsDiskFull(ex)) return "存储空间不足, 清出空间后重试";
            if (ex is OperationCanceledException) return "已取消";
            var msg = ex.Message ?? "";
            if (ex is Java.Lang.Exception || msg.StartsWith("Java.", StringComparison.Ordinal))
                return "音频解码失败, 该片格式暂不支持智能字幕";
            return msg;
        }

        /// <summary>识别线程等抽取线程冲出指定秒数 PCM (终局放行, 失败即抛)。</summary>
        private static async Task WaitSamplesAsync(long need, ExtractState st, Task extTask, CancellationToken ct)
        {
            while (!st.Done && st.Flushed < need)
            {
                if (extTask.IsCompleted) await extTask;
                await Task.Delay(200, ct);
            }
        }

        /// <summary>whisper 输出行 (t0\tt1\ttext, 10ms tick) 转全片毫秒段列表;
        /// 时间基准 = 全片绝对样本偏移 (E8 公式: 偏移/16 换毫秒, 续跑断点偏移同样适用)。</summary>
        private static List<(long t0, long t1, string text)> WinSegs(string text, long off)
        {
            var list = new List<(long, long, string)>();
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (!long.TryParse(parts[0], out var t0) || !long.TryParse(parts[1], out var t1)) continue;
                list.Add((SmartSubPlan.WindowTimestampMs(off, t0), SmartSubPlan.WindowTimestampMs(off, t1), parts[2].Trim()));
            }
            return list;
        }

        private static float[] ReadPcm(string file, long startSample, int count)
        {
            var pcm = new float[count];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = startSample * 4;
            using var br = new BinaryReader(fs);
            for (int i = 0; i < count; i++) pcm[i] = br.ReadSingle();
            return pcm;
        }

        private async Task DownloadModel(string modelPath, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            await DownloadFile(SmartSubPlan.ModelUrl, modelPath, SmartSubPlan.ModelExpectedSize, "下模型", ct);
        }

        /// <summary>通用下载: 流式落盘, 10% 步进进度上屏, 大小校验+原子改名。
        /// v3.25.25 E12 修: 下载前按模型实际大小做满盘预检 (阈值=expectedSize, 对齐 DownloadService 既有口径),
        /// 不够直接中文报「存储空间不足」, 不白跑一趟; 真实写盘的 ENOSPC 由外层统一归类。
        /// v3.25.26 修(真机误报): 预检改 Android StatFs 口径——DriveInfo 在安卓上探的是根分区 (rootfs 几乎无空间),
        /// 用户盘明明空闲近 200G 仍误报「存储空间不足」; 探测失败跳过预检, 交给真实写盘 ENOSPC 兜底。</summary>
        private async Task DownloadFile(string url, string destPath, long expectedSize, string label, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var freeBytes = DiskProbe.FreeBytes(Path.GetDirectoryName(destPath)!);
            if (freeBytes.HasValue && !SmartSubPlan.EnoughDiskSpace(freeBytes.Value, expectedSize))
                throw new IOException("存储空间不足, 清出空间后重试");
            var tmp = destPath + ".dl";
            try
            {
                using var http = new HttpClient();
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? expectedSize;
                    using var src = await resp.Content.ReadAsStreamAsync(ct);
                    using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                    var buf = new byte[1 << 20];
                    long got = 0; int read; int lastPct = -1;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        got += read;
                        var pct = (int)(got * 100 / Math.Max(1, total));
                        if (pct / 10 > lastPct / 10) { lastPct = pct; _status($"智能字幕: {label} {pct}%"); }
                    }
                }
                if (new FileInfo(tmp).Length != expectedSize)
                    throw new IOException(label + "下载不完整");
                File.Move(tmp, destPath, overwrite: true);
            }
            catch
            {
                // v3.25.28 E6 修: 失败即清 .dl 残留 (重试自建, 不留最大约 190MB 的孤儿)
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>确保翻译模型四件套齐全: 缺哪个下哪个 (大小校验+原子改名), 再从 tokenizer.json 抽词表。</summary>
        private async Task EnsureTransModel(string dir, CancellationToken ct)
        {
            Directory.CreateDirectory(dir);
            foreach (var f in SmartSubPlan.TransFiles)
            {
                var dest = Path.Combine(dir, f.Name);
                if (File.Exists(dest) && new FileInfo(dest).Length == f.Size) continue;
                await DownloadFile(SmartSubPlan.TransBaseUrl + f.RepoPath, dest, f.Size, "下翻译模型", ct);
            }
            // 词表抽取: tokenizer.json 的 model.vocab (piece, score) 按序写 vocab.txt, 与模型编号同序
            var vocabPath = Path.Combine(dir, "vocab.txt");
            if (!File.Exists(vocabPath) || new FileInfo(vocabPath).Length == 0)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tokenizer.json")));
                var vocab = doc.RootElement.GetProperty("model").GetProperty("vocab");
                using var w = new StreamWriter(vocabPath);
                foreach (var item in vocab.EnumerateArray())
                    w.WriteLine(item[0].GetString());
            }
            if (!SmartSubPlan.TransModelReady(dir))
                throw new IOException("翻译模型校验不通过");
        }
    }
}
