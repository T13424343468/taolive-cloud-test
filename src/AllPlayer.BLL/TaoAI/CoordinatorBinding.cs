using System;
using System.Threading;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 守卫信号源（v3.17.0 按用户定标：增强负载压在打英雄联盟手游所需之下）：
    /// 电池温度 ≥45°C（越过游戏满载包络）→ 发热暂停；43~45°C 区间由 AiAdaptivePolicy 降档；
    /// 播放卡顿 → 让路暂停。Android 侧低频轮询（≥5s）接入，BLL 只收信号。
    /// </summary>
    public static class AiGuard
    {
        /// <summary>发热停增强阈值（电池温度，摄氏度）：英雄联盟手游级满载包络上沿，2026-11-07 用户定</summary>
        public const double ThermalCutoffC = 45.0;

        /// <summary>轮询最小间隔（毫秒），防止高频轮询自身耗电</summary>
        public const int PollIntervalMs = 5000;

        /// <summary>纯函数：温度判定（可测）</summary>
        public static bool IsThermalHigh(double batteryTempC) => batteryTempC >= ThermalCutoffC;

        /// <summary>
        /// 轮询循环（可取消）：每 5s 取一次电池温度与播放卡顿状态，汇入协调器。
        /// pollBatteryTemp: 返回电池温度（不可测返回 double.NaN 跳过）。
        /// isPlaybackStalled: 当前是否卡顿（缓冲/丢帧）。
        /// </summary>
        public static CancellationTokenSource StartPolling(
            Func<double> pollBatteryTemp,
            Func<bool> isPlaybackStalled,
            EnhancementCoordinator coordinator,
            Func<(int Pct, bool Charging)>? pollBattery = null,
            Func<AiEnhanceTier>? preferredTier = null,
            Action<AiEnhanceTier>? onTierAdjusted = null,
            Func<double>? pollPowerW = null)
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var thread = new Thread(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        double t = pollBatteryTemp != null ? pollBatteryTemp() : double.NaN;
                        bool stalled = isPlaybackStalled != null && isPlaybackStalled();
                        coordinator.NotifyThermal(!double.IsNaN(t) && IsThermalHigh(t));
                        if (isPlaybackStalled != null)
                        {
                            if (stalled) coordinator.NotifyPlaybackStall();
                            else coordinator.NotifyPlaybackResume();
                        }
                        // 自适应实时调档（游戏满载包络标定；用户档位=偏好上限，卡顿让路优先）
                        if (preferredTier != null && pollBattery != null)
                        {
                            var (pct, charging) = pollBattery();
                            double pw = pollPowerW != null ? pollPowerW() : double.NaN;
                            var (target, _) = AiAdaptivePolicy.Adjust(
                                coordinator.Tier, preferredTier(), t, pct, charging, stalled, pw);
                            if (target != coordinator.Tier)
                            {
                                coordinator.Tier = target;
                                onTierAdjusted?.Invoke(target);
                            }
                        }
                    }
                    catch { /* 轮询失败不致命，下一轮再试 */ }
                    if (token.WaitHandle.WaitOne(PollIntervalMs)) break;
                }
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "taoai-guard" };
            thread.Start();
            return cts;
        }
    }
}
