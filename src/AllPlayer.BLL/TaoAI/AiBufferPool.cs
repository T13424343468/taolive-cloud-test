using System;
using System.Collections.Generic;

namespace AllPlayer.BLL
{
    /// <summary>分片处理动作：Process=进 AI 逐段加工；DirectPlay=原画质直出（跳过加工）。</summary>
    public enum AiIngestAction
    {
        Process = 0,
        DirectPlay = 1
    }

    /// <summary>直出原因：None=正常加工；LagCap=落后 60 秒封顶强制直出；Thermal=过热退直出；LowBattery=低电退直出。</summary>
    public enum AiBypassReason
    {
        None = 0,
        LagCap = 1,
        Thermal = 2,
        LowBattery = 3
    }

    /// <summary>入队决策：本分片加工还是直出，以及是否建议降档。</summary>
    public sealed class AiIngestDecision
    {
        public AiIngestAction Action { get; internal set; }
        public AiBypassReason Reason { get; internal set; }
        /// <summary>加工追不上实时时建议降档；到底档仍追不上由 LagCap 直出兜底。</summary>
        public bool SuggestDowngrade { get; internal set; }
        /// <summary>当前管线滞后秒数（=总入队时长 - 已播放时长）。</summary>
        public double LagSeconds { get; internal set; }
    }

    /// <summary>
    /// 直播 AI 缓冲池（老板 11-11 00:08 拍板口径，纯逻辑可测、无 IO 无 UI、线程安全）：
    /// 分片到手立即逐段加工，加工完放行播放；初始缓冲 10–20 秒（目标 15 秒）；
    /// 三道保险 = ①追不上自动降档 ②过热/低电退原画质直出 ③落后 60 秒封顶强制直出。
    /// 内核红线：不做文件 IO、不做 UI，输入输出走内存；优先级让路播放。
    /// </summary>
    public sealed class AiBufferPool
    {
        private readonly object _lock = new object();
        private readonly Func<double> _clockSeconds;

        private readonly double _initialTargetSeconds;
        private readonly double _maxLagSeconds;
        private readonly double _lagResumeSeconds;
        private readonly double _minProcessRate;

        // 累计口径（秒）
        private double _ingestedSec;   // 累计入队（含直出）
        private double _processedSec;  // 累计加工完成
        private double _playedSec;     // 累计已播放（直出分片即时计入）
        private double _processWallSec; // 加工累计墙钟（用于速率统计）

        private readonly Queue<double> _pendingSegments = new Queue<double>(); // 未加工分片时长

        // 速率 EWMA：processedSec / wallSec
        private double _rateEwma = 1.0;
        private bool _rateHasSample;
        private const double RateAlpha = 0.3;

        private AiBypassReason _bypassHold;   // 直出保持态（滞回用），Thermal/LowBattery 期间恒直出
        private bool _overheating;
        private bool _lowBattery;

        public AiBufferPool(double initialTargetSeconds = 15.0,
                            double maxLagSeconds = 60.0,
                            double lagResumeSeconds = 45.0,
                            double minProcessRate = 1.0,
                            Func<double>? clockSeconds = null)
        {
            // 口径钳制：初始缓冲必须在 10–20 秒区间内（老板拍板区间）
            if (initialTargetSeconds < 10.0) initialTargetSeconds = 10.0;
            if (initialTargetSeconds > 20.0) initialTargetSeconds = 20.0;
            _initialTargetSeconds = initialTargetSeconds;
            _maxLagSeconds = maxLagSeconds;
            _lagResumeSeconds = lagResumeSeconds;
            _minProcessRate = minProcessRate;
            _clockSeconds = clockSeconds ?? DefaultClock;
        }

        private static double DefaultClock() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

        /// <summary>分片入队：决定加工还是直出。直出分片视同即时播放。</summary>
        public AiIngestDecision EnqueueSegment(long segIndex, double durationSeconds)
        {
            if (durationSeconds < 0) durationSeconds = 0;
            lock (_lock)
            {
                _ingestedSec += durationSeconds;
                double lag = _ingestedSec - _playedSec;

                var d = new AiIngestDecision();

                // 保险②：过热/低电 → 原画质直出
                if (_overheating)
                {
                    d.Action = AiIngestAction.DirectPlay; d.Reason = AiBypassReason.Thermal;
                    d.LagSeconds = lag;
                    DirectPlayLocked(durationSeconds);
                    return d;
                }
                if (_lowBattery)
                {
                    d.Action = AiIngestAction.DirectPlay; d.Reason = AiBypassReason.LowBattery;
                    d.LagSeconds = lag;
                    DirectPlayLocked(durationSeconds);
                    return d;
                }

                // 保险③：落后 60 秒封顶强制直出；回落到恢复线以下退出（滞回防抖）
                if (_bypassHold == AiBypassReason.LagCap)
                {
                    // 封顶后持续直出，延迟锁定在封顶线；恢复路径=播放确认追平（lag 归零）或换台 Reset()
                    d.Action = AiIngestAction.DirectPlay; d.Reason = AiBypassReason.LagCap;
                    d.LagSeconds = lag;
                    DirectPlayLocked(durationSeconds);
                    if (lag <= 0.0001) _bypassHold = AiBypassReason.None;
                    return d;
                }
                else if (lag > _maxLagSeconds)
                {
                    _bypassHold = AiBypassReason.LagCap;
                    d.Action = AiIngestAction.DirectPlay; d.Reason = AiBypassReason.LagCap;
                    d.LagSeconds = lag;
                    DirectPlayLocked(durationSeconds);
                    return d;
                }

                // 正常入加工队列；保险①：速率追不上实时建议降档
                _pendingSegments.Enqueue(durationSeconds);
                bool behind = _rateHasSample && _rateEwma < _minProcessRate && _pendingSegments.Count > 0;
                d.Action = AiIngestAction.Process;
                d.Reason = AiBypassReason.None;
                d.SuggestDowngrade = behind;
                d.LagSeconds = _ingestedSec - _playedSec;
                return d;
            }
        }

        /// <summary>加工完成回报：duration=该分片时长，wall=本次加工耗时。更新速率 EWMA。</summary>
        public void ReportProcessed(long segIndex, double durationSeconds, double wallSeconds)
        {
            if (wallSeconds < 0) wallSeconds = 0;
            lock (_lock)
            {
                _processedSec += durationSeconds;
                _processWallSec += wallSeconds;
                if (wallSeconds > 0)
                {
                    double instant = durationSeconds / wallSeconds; // 本片"倍速"
                    _rateEwma = _rateHasSample ? _rateEwma + RateAlpha * (instant - _rateEwma) : instant;
                    _rateHasSample = true;
                }
            }
        }

        /// <summary>播放确认：按播放顺序确认分片已播出（加工分片与直出分片均走此口）。</summary>
        public void ReportPlayed(long segIndex, double durationSeconds)
        {
            if (durationSeconds < 0) durationSeconds = 0;
            lock (_lock)
            {
                _playedSec += durationSeconds;
                if (_playedSec > _ingestedSec) _playedSec = _ingestedSec; // 不越界
            }
        }

        /// <summary>保险②入口：过热或低电上报，期间恒直出；恢复传 false/false。</summary>
        public void SetThermalGuard(bool overheating, bool lowBattery)
        {
            lock (_lock)
            {
                _overheating = overheating;
                _lowBattery = lowBattery;
            }
        }

        /// <summary>初始缓冲是否攒够（加工完成的存量 ≥ 目标秒数）；直出态恒放行。</summary>
        public bool IsInitialBufferReady()
        {
            lock (_lock)
            {
                if (_overheating || _lowBattery) return true;
                if (_bypassHold == AiBypassReason.LagCap) return true;
                if (_ingestedSec > 0.0001 && _pendingSegments.Count == 0 && _processedSec >= _playedSec && _ingestedSec <= _playedSec + 0.0001) return true; // 流尾（有入队才判尾）
                return (_processedSec - _playedSec) >= _initialTargetSeconds;
            }
        }

        /// <summary>当前落后秒数（总入队 - 已播放）。</summary>
        public double LagSeconds()
        {
            lock (_lock) { return _ingestedSec - _playedSec; }
        }

        /// <summary>当前加工速率（EWMA，倍速口径：1.0=跟实时打平）。</summary>
        public double ProcessRate()
        {
            lock (_lock) { return _rateEwma; }
        }

        /// <summary>换台/重开流时清池复位：清空待加工队列与累计账目，直出保持态一并解除。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _pendingSegments.Clear();
                _ingestedSec = 0; _processedSec = 0; _playedSec = 0; _processWallSec = 0;
                _bypassHold = AiBypassReason.None;
                _rateEwma = 1.0; _rateHasSample = false;
            }
        }

        /// <summary>档位被 UI 调整后重置速率统计（降档后重新观测，避免旧样本压判定）。</summary>
        public void ResetRateStats()
        {
            lock (_lock)
            {
                _rateEwma = 1.0;
                _rateHasSample = false;
                _processWallSec = 0;
            }
        }

        private void DirectPlayLocked(double durationSeconds)
        {
            // 直出分片即时播出：从待加工队列移除并直接计入已播
            if (_pendingSegments.Count > 0) _pendingSegments.Dequeue();
            _playedSec = System.Math.Min(_playedSec + durationSeconds, _ingestedSec);
        }
    }
}
