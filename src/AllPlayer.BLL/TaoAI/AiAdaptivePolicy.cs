using System;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 自适应调档策略（纯逻辑，可单测）——v3.17.0 用户定（2026-11-07）：
    /// 「增强多少用设备算力，只要不是非常发热耗电即可，小幅度发热耗电可接受；
    ///   阈值标定 = 增强负载压在打英雄联盟手游所需之下」。
    /// 即：旗舰机满载打游戏（英雄联盟手游级）的电池温度包络（约 42~44°C）作热预算上限，
    /// 增强全程不得越过；温度电量宽裕时逐档爬回偏好档，一次一档防抖。
    /// 滞回带：降档 43°C / 升档 41°C、降档 30% / 升档 60%，边界不横跳。
    /// </summary>
    public static class AiAdaptivePolicy
    {
        public const double TempStopC = 45.0;    // ≥45°C：越过游戏满载包络，停增强
        public const double TempDownC = 43.0;    // ≥43°C：进入游戏级热区，降一档
        public const double TempUpC = 41.0;      // ≤41°C：温度回常，可升档
        public const int BatteryStopPct = 15;    // ≤15% 且未充电：停增强
        public const int BatteryDownPct = 30;    // ≤30% 且未充电：降一档
        public const int BatteryUpPct = 60;      // ≥60% 或充电中：电量宽裕，可升档

        /// <summary>整机功耗上限（瓦）: 以英雄联盟手游满载整机功耗为标尺（约 4~5W 量级，
        /// 2026-11-07 用户定标「不超过它就拉满」）。实测 ≥ 上限降档，回常再爬。</summary>
        public const double PowerLoLW = 5.0;
        public const double PowerDownW = 4.5;    // ≥4.5W：追平游戏满载，降一档
        public const double PowerUpW = 3.0;      // ≤3.0W：明显低于游戏满载，可升档

        /// <summary>当前档位/偏好档/电池温度/电量/充电/卡顿/实测功耗(瓦,NaN=未知) → 目标档位与原因（一次最多动一档）。</summary>
        public static (AiEnhanceTier Tier, string Reason) Adjust(
            AiEnhanceTier cur, AiEnhanceTier preferred,
            double batteryTempC, int batteryPct, bool charging, bool stalled,
            double powerW = double.NaN)
        {
            if (cur == AiEnhanceTier.Off || preferred == AiEnhanceTier.Off) return (AiEnhanceTier.Off, "");
            if (stalled) return (cur, "播放卡顿, 守卫让路");
            bool tempKnown = !double.IsNaN(batteryTempC);
            if (tempKnown && batteryTempC >= TempStopC)
                return (AiEnhanceTier.Off, $"温度 {batteryTempC:0.#}°C 越过游戏满载包络, 停增强");
            if (tempKnown && batteryTempC >= TempDownC)
                return (StepDown(cur), $"温度 {batteryTempC:0.#}°C 进入游戏级热区, 降一档");
            if (!charging && batteryPct > 0 && batteryPct <= BatteryStopPct)
                return (AiEnhanceTier.Off, $"电量 {batteryPct}% 过低, 停增强");
            if (!charging && batteryPct > 0 && batteryPct <= BatteryDownPct)
                return (StepDown(cur), $"电量 {batteryPct}% 偏低, 降一档");
            // 功耗守卫（v3.20.0）：功耗读数可信时，追平游戏满载即降档
            bool powerKnown = !double.IsNaN(powerW) && powerW > 0;
            if (powerKnown && !charging && powerW >= PowerDownW)
                return (StepDown(cur), $"功耗 {powerW:0.#}W 追平游戏满载, 降一档");
            bool tempOk = !tempKnown || batteryTempC <= TempUpC;
            bool powerOk = charging || (powerKnown ? powerW <= PowerUpW : true);
            if (tempOk && powerOk && cur < preferred)
                return ((AiEnhanceTier)((int)cur + 1), "温度功耗宽裕, 升一档");
            return (cur, "");
        }

        private static AiEnhanceTier StepDown(AiEnhanceTier t)
            => t <= AiEnhanceTier.Low ? AiEnhanceTier.Low : (AiEnhanceTier)((int)t - 1);
    }
}
