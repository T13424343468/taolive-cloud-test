using System;

namespace TaoLive.BLL;

/// <summary>
/// 换台去抖（v1.0.13 老板 15:38 拍板：切换时立刻停止上一个流，每次连接只管当前台）：
/// 遥控器上下键连按不真切——按键即时更新台号显示，停稳 WindowMs 后才真正起流；
/// 中间台不发真实连接，不打断、不误触发重连。目标台以最后一次按键为准。
/// </summary>
public sealed class SwitchDebouncer
{
    /// <summary>去抖窗口：连续快按的静止等待时长（毫秒）。单次按一下同样等足窗口，统一手感。</summary>
    public const int WindowMs = 400;

    private long _lastTapMs;
    private string _targetKey = string.Empty;

    /// <summary>记录一次按键；目标台以最后一次按键为准。</summary>
    public void Tap(long nowMs, string channelKey)
    {
        _lastTapMs = nowMs;
        _targetKey = channelKey ?? string.Empty;
    }

    /// <summary>窗口是否已停稳（距最后一次按键 ≥ WindowMs）。</summary>
    public bool IsDue(long nowMs) => nowMs - _lastTapMs >= WindowMs;

    /// <summary>当前目标台键（最后一次按键的目标）。</summary>
    public string Target => _targetKey;

    /// <summary>是否有待执行目标。</summary>
    public bool HasTarget => _lastTapMs > 0;

    /// <summary>清空（目标台已起流或作废）。</summary>
    public void Clear()
    {
        _lastTapMs = 0;
        _targetKey = string.Empty;
    }
}
