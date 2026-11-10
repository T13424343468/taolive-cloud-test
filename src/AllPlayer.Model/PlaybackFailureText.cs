using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// 播放失败文案分类 (纯逻辑, v3.23 P2-1, 可单测):
    /// 网络流按有网/断网给不同指引, 本地文件统一给解码/访问口径。
    /// </summary>
    public static class PlaybackFailureText
    {
        public static string For(string? path, bool hasNetwork)
        {
            if (path != null &&
                (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return hasNetwork
                    ? "网络流中断: 稍后自动重试, 或点播放重试"
                    : "网络不通: 请检查网络后重试";
            }
            return "播放失败: 文件无法解码或无法访问";
        }
    }
}
