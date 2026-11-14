using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// v3.22.6 播放按钮显示统一规则 (老板 12:57 反馈「播放中中央还亮着播放键/左下角也是」):
    /// 中央浮钮与底部播放键共用同一判定, 防止两处各说各话。
    /// 底部按钮: 播放中显暂停键(点按即暂停), 暂停时显播放键 —— 按钮含义=按下后发生的事, 与真实状态一致。
    /// 中央浮钮: 仅「在播放器里 + 未锁定 + 暂停中」三样齐才亮; 播放中绝不亮。
    /// </summary>
    public static class PlaybackUiPolicy
    {
        /// <summary>底部播放键图标: 播放中=暂停, 暂停=播放。</summary>
        public static bool BottomShowsPauseWhilePlaying(bool playing) => playing;

        /// <summary>中央播放浮钮可见性: 三样齐(在播放器+未锁+暂停中)才亮。</summary>
        public static bool CenterPlayVisible(bool playing, bool locked, bool inPlayer)
            => !playing && !locked && inPlayer;
    }
}
