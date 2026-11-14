using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AllPlayer.BLL
{
    /// <summary>AI 画质档位（实验档口径）：0 关 / 1 低档 / 2 中档 / 3 高档。默认关。</summary>
    public enum AiEnhanceTier
    {
        Off = 0,
        Low = 1,
        Mid = 2,
        High = 3
    }

    /// <summary>
    /// 设备分级（纯函数，可测）：按可用内存与 SoC 年代分档。
    /// 自学习如实口径 v1 = 设备能力分级 + 档位偏好记忆；不做端上训练，不宣传"AI 学习"。
    /// </summary>
    public static class AiTierPicker
    {
        /// <summary>
        /// totalRamBytes: 设备总内存（RuntimeInformation 不可靠时由 UI 层注入 ActivityManager 上报值）。
        /// socYear: SoC 年代（0=未知）。未知时按内存保守分档。
        /// </summary>
        public static AiEnhanceTier FromDevice(long totalRamBytes, int socYear)
        {
            if (totalRamBytes <= 0) return AiEnhanceTier.Off;
            const long MB = 1024 * 1024;
            if (totalRamBytes >= 6 * 1024 * MB && socYear >= 2020) return AiEnhanceTier.High;
            if (totalRamBytes >= 6 * 1024 * MB) return AiEnhanceTier.Mid;
            if (totalRamBytes >= 4 * 1024 * MB) return AiEnhanceTier.Mid;
            if (totalRamBytes >= 2 * 1024 * MB) return AiEnhanceTier.Low;
            return AiEnhanceTier.Off;
        }

        /// <summary>档位 → 单 tile 边长（超分切块大小：低档小切块省内存、高档大块提吞吐）</summary>
        public static int TileOf(AiEnhanceTier tier)
        {
            switch (tier)
            {
                case AiEnhanceTier.High: return 512;
                case AiEnhanceTier.Mid: return 384;
                default: return 256;
            }
        }

        /// <summary>档位 → 推理线程数（永远给播放留余量：高档最多 2 线程）</summary>
        public static int ThreadsOf(AiEnhanceTier tier)
        {
            switch (tier)
            {
                case AiEnhanceTier.High: return 2;
                case AiEnhanceTier.Mid: return 2;
                default: return 1;
            }
        }
    }

    /// <summary>增强任务状态机</summary>
    public enum AiEnhanceState
    {
        Idle,          // 无任务
        Queued,        // 排队中
        Running,       // 增强中
        PausedGuard,   // 播放卡顿让路（守卫暂停）
        PausedThermal, // 发热守卫暂停（实验档保守：≥40°C 即暂停）
        PausedByUser,  // 用户手动暂停
        Done,          // 完成（已原子替换）
        Failed,        // 失败（原文件保持完好）
        SkippedMagnet  // 磁力下载中文件：不入队（白名单判定存疑一律不覆盖）
    }

    /// <summary>
    /// 逐段增强写回编排器（实验档 v1）。
    /// 红线（AI画质实验档重设计 2026-11-06 第六节）：
    ///  1) 不出坏文件——写回前原文件始终完好；MD5+时长+抽帧校验不过不落盘；
    ///  2) 不卡播放——AI 线程后台低优先级，播放卡顿/发热即暂停增强；
    ///  3) 不留坏缓存——任务队列封顶 3 个，LRU 逐出，损坏自检重建；
    ///  4) 磁力下载中文件不入队（IsMagnetActive 白名单判定，存疑一律跳过）。
    /// 本类只做编排与状态机，不直接碰 MediaCodec；逐段执行体由 IEnhanceStep 注入
    /// （真机实现：解码→taoai_sr/rife→重编码→校验→原子替换，实验档 v2 接线）。
    /// </summary>
    public class EnhancementCoordinator : IDisposable
    {
        /// <summary>待办队列封顶（缓存自愈长期标准）</summary>
        public const int MaxQueue = 3;

        private readonly object _lock = new object();
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly HashSet<string> _queued = new HashSet<string>();

        private AiEnhanceTier _tier = AiEnhanceTier.Off;   // 默认关
        private AiEnhanceState _state = AiEnhanceState.Idle;
        private string? _current;
        private int _segmentsDone;
        private int _segmentsTotal;
        private bool _thermalHigh;
        private bool _playbackStalled;
        private bool _disposed;

        private readonly Func<string, bool> _isMagnetActive;   // 磁力下载中判定（UI 层注入）
        private readonly Func<string, string?>? _enhanceStep;    // 逐段增强执行体（v2 注入；null 时状态机空转）

        public event Action<AiEnhanceState>? StateChanged;
        public event Action<int, int>? ProgressChanged;         // (done, total)

        public EnhancementCoordinator(Func<string, bool> isMagnetActive, Func<string, string?>? enhanceStep = null)
        {
            _isMagnetActive = isMagnetActive ?? (p => false);
            _enhanceStep = enhanceStep;
        }

        /// <summary>当前档位（默认关）。切档即时生效于后续任务。</summary>
        public AiEnhanceTier Tier
        {
            get { lock (_lock) { return _tier; } }
            set
            {
                lock (_lock)
                {
                    _tier = value;
                    if (value == AiEnhanceTier.Off)
                    {
                        _queue.Clear();
                        _queued.Clear();
                        _current = null;
                        SetStateLocked(AiEnhanceState.Idle);
                    }
                }
            }
        }

        public AiEnhanceState State { get { lock (_lock) { return _state; } } }
        public (int Done, int Total) Progress { get { lock (_lock) { return (_segmentsDone, _segmentsTotal); } } }

        /// <summary>当前正在处理的文件路径（v3.21.1 状态字如实上屏；无任务为 null）。</summary>
        public string? CurrentPath { get { lock (_lock) { return _current; } } }

        /// <summary>指定路径是否还在待办队列（v3.21.1 状态字区分排队/处理中）。</summary>
        public bool IsQueued(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock) { return _queued.Contains(path); }
        }

        /// <summary>入队：文件路径。磁力下载中 → SkippedMagnet；已在队列 → 忽略；队列满 → LRU 逐出最老。</summary>
        public AiEnhanceState Enqueue(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return AiEnhanceState.Idle;
            lock (_lock)
            {
                if (_tier == AiEnhanceTier.Off) return AiEnhanceState.Idle;
                if (_isMagnetActive(path)) return AiEnhanceState.SkippedMagnet;
                if (_queued.Contains(path) || path == _current) return _state;
                if (_queued.Count >= MaxQueue)
                {
                    // LRU 逐出最老待办
                    foreach (var old in _queue) { _queued.Remove(old); break; }
                    _queue.Dequeue();
                }
                _queue.Enqueue(path);
                _queued.Add(path);
                if (_current == null) PumpLocked();
                else SetStateLocked(AiEnhanceState.Queued);
                return _state;
            }
        }

        /// <summary>守卫信号：播放卡顿（AI 让路，增强暂停）</summary>
        public void NotifyPlaybackStall()
        {
            lock (_lock) { _playbackStalled = true; RefreshRunStateLocked(); }
        }

        public void NotifyPlaybackResume()
        {
            lock (_lock) { _playbackStalled = false; RefreshRunStateLocked(); }
        }

        /// <summary>守卫信号：设备发热（实验档保守阈值：高温即暂停，真机标定待正式档）</summary>
        public void NotifyThermal(bool high)
        {
            lock (_lock) { _thermalHigh = high; RefreshRunStateLocked(); }
        }

        /// <summary>磁力下载完成事件：补队（下载中文件此前被 SkippedMagnet，完成后可入队）</summary>
        public void NotifyMagnetFinished(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Enqueue(path);
        }

        /// <summary>逐段推进（由执行体回调：每完成一段调一次；total 首次回调时确定）</summary>
        public void ReportSegment(int done, int total)
        {
            lock (_lock)
            {
                _segmentsDone = done;
                _segmentsTotal = total;
            }
            var ev = ProgressChanged;
            if (ev != null) ev(done, total);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Clear();
                _queued.Clear();
                _current = null;
                SetStateLocked(AiEnhanceState.Idle);
            }
        }

        // ---------- 内部 ----------
        private void RefreshRunStateLocked()
        {
            if (_current == null) return;
            if (_thermalHigh) SetStateLocked(AiEnhanceState.PausedThermal);
            else if (_playbackStalled) SetStateLocked(AiEnhanceState.PausedGuard);
            else SetStateLocked(AiEnhanceState.Running);
        }

        private void PumpLocked()
        {
            if (_queue.Count == 0) { _current = null; SetStateLocked(AiEnhanceState.Idle); return; }
            _current = _queue.Dequeue();
            _queued.Remove(_current);
            _segmentsDone = 0;
            _segmentsTotal = 0;
            if (_thermalHigh) { SetStateLocked(AiEnhanceState.PausedThermal); RunStepInBackground(_current); return; }
            if (_playbackStalled) { SetStateLocked(AiEnhanceState.PausedGuard); RunStepInBackground(_current); return; }
            SetStateLocked(AiEnhanceState.Running);
            // 状态机空转模式（执行体未注入）：实验档 v2 由 SegmentWriter 接手；
            // 此处立即置完成以保证状态机自洽、可测。
            if (_enhanceStep == null)
            {
                _segmentsDone = _segmentsTotal = 1;
                ReportSegment(1, 1);
                _current = null;
                PumpLocked();
                return;
            }
            // v3.21.1 修复「短片整条增强入队后没人执行」：注入了执行体就必须真的开跑。
            // 暂停语义由执行体内部的 paused 回调承担（守卫状态切换即时生效），线程照常启动。
            RunStepInBackground(_current);
        }

        /// <summary>后台执行当前任务的增强步骤（v3.21.1）：完成/失败后收口并泵下一个；
        /// 期间关档或队列清空（_current 变化）则只记结果不再推进状态机。</summary>
        private void RunStepInBackground(string path)
        {
            var step = _enhanceStep;
            if (step == null) return;
            var worker = new Thread(() =>
            {
                string? outcome = null;
                try { outcome = step(path); } catch { outcome = null; }   // 执行体异常按失败收口，原文件由其内部保证完好
                lock (_lock)
                {
                    if (_disposed || _current != path) return;   // 关档/任务已更替：不再动状态机
                    _current = null;
                    SetStateLocked(!string.IsNullOrEmpty(outcome) ? AiEnhanceState.Done : AiEnhanceState.Failed);
                    PumpLocked();
                }
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "taoai-enhance" };
            worker.Start();
        }

        private void SetStateLocked(AiEnhanceState s)
        {
            if (_state == s) return;
            _state = s;
            var ev = StateChanged;
            if (ev != null) ev(s);
        }
    }
}
