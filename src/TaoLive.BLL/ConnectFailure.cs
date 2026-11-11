using System;

namespace TaoLive.BLL;

/// <summary>
/// 连接失败分类与冷却退避（v1.0.10 修复⑫）：不预设单一根因，逐类探测、提示、自愈与落日志。
/// 失败分类从异常与状态推断（纯函数可测），逐类对应处理动作；限流类一律冷却退避不无限重试。
/// </summary>
public enum ConnectFailureKind
{
    None,
    DnsFailure,        // 解析失败：源域名解析不到 → 备援候选换源
    ConnectTimeout,    // 连接超时：网络名存实亡/路由阻断 → 探网+备援
    Rejected,          // 被拒（HTTP 4xx/5xx、业务码 iretcode≠0）→ 源健康账淘汰+备援
    RateLimited,       // 限流/风控（403、取票高频）→ 冷却退避、错峰重试
    TimeSkew,          // 证书类错误/时间偏差 → 校时触发（NTP/HTTP Date）
    DecoderSurface,    // 播放器内部错误（解码/表面/缓冲耗尽）→ 三级自救+备用载体
    Unknown,           // 未知异常 → 落日志，界面「暂时连不上，正在自动重试」
}

public static class ConnectFailure
{
    /// <summary>把异常文本/HTTP 状态归到失败类（不笼统「连接失败」）。httpStatus 为空表示未到 HTTP 层。</summary>
    public static ConnectFailureKind Classify(string? errorText, int? httpStatus = null)
    {
        var t = (errorText ?? "").ToLowerInvariant();
        if (t.Length == 0) return ConnectFailureKind.None;
        if (t.Contains("certificate") || t.Contains("ssl") || t.Contains("trust")
            || t.Contains("clock skew") || t.Contains("signature"))
            return ConnectFailureKind.TimeSkew;
        if (t.Contains("name resolution") || t.Contains("dns") || t.Contains("getaddrinfo")
            || t.Contains("no address associated") || t.Contains("temporary failure in name"))
            return ConnectFailureKind.DnsFailure;
        if (t.Contains("timed out") || t.Contains("timeout") || t.Contains("connection refused"))
            return ConnectFailureKind.ConnectTimeout;
        if (httpStatus == 403 || t.Contains("iretcode") && !t.Contains("iretcode=0")
            || t.Contains("rate limit"))
            return ConnectFailureKind.RateLimited;
        if (httpStatus is >= 400 || t.Contains("http"))
            return ConnectFailureKind.Rejected;
        if (t.Contains("decode") || t.Contains("surface") || t.Contains("buffer"))
            return ConnectFailureKind.DecoderSurface;
        return ConnectFailureKind.Unknown;
    }

    /// <summary>
    /// 限流冷却退避（纯函数）：第 n 次连续失败（0 起）冷却 2^n 秒，封顶 60 秒；
    /// 冷却期内不重试，不无限打爆取票入口。
    /// </summary>
    public static int CooldownSeconds(int consecutiveFailures) =>
        Math.Min((int)Math.Pow(2, Math.Max(0, consecutiveFailures)), 60);
}
