using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// 本地流访问令牌判定 (纯逻辑, v3.23 P0-2, 可单测):
    /// 请求目标须形如 /stream?token=令牌 且逐字相符才放行;
    /// 令牌要求关闭时一律放行 (退回旧行为, 兼容特殊设备)。
    /// </summary>
    public static class StreamToken
    {
        public static bool RequestAllowed(string? requestTarget, bool tokenRequired, string token)
        {
            if (!tokenRequired) return true;
            if (string.IsNullOrEmpty(requestTarget) || string.IsNullOrEmpty(token)) return false;
            var q = requestTarget.IndexOf('?');
            if (q < 0) return false;
            return string.Equals(requestTarget.Substring(q + 1), "token=" + token, StringComparison.Ordinal);
        }
    }
}
