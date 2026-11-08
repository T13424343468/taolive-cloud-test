using System;

namespace TaoLive.BLL;

/// <summary>
/// 画面信息行重试口径（v1.0.10 修复⑤）：HLS 轨道起播后才出现，解析放首帧后重试——
/// 每 500ms 一次、至多 10 次，后台进行不阻塞播放；到限仍取不到如实显「--」，不停留「检测中」。
/// </summary>
public static class StatsLinePolicy
{
    public const int IntervalMs = 500;
    public const int MaxRetries = 10;

    /// <summary>下一次重试延时：已重试 retryCount 次（0 起），超过上限返回 null（停止）。</summary>
    public static int? NextDelayMs(int retryCount) =>
        retryCount >= MaxRetries ? (int?)null : IntervalMs;

    /// <summary>到限仍无测量的显示文案：显「--」不留「检测中」。</summary>
    public const string UnavailableText = "--";
}
