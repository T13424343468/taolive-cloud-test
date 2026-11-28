using System;
using System.Collections.Concurrent;

namespace TaoLive.Foundation;

/// <summary>
/// 源端频控共享闸（vc144 老板令「403 收敛」根治刀）：探活/取票/内核三路对同一主机的 4xx/429
/// 共享一份冷却账——任一路被源端拒绝，整主机进冷却窗（60s 起 ×2 步进封顶 10 分钟），
/// 窗内三路全部本地快速失败零网络，给源端退避窗让频控自然解除；任一路成功即清账。
/// 此前三路各烧各的：探活连打+内核 4xx 换票+坏票轮换互相放大，源端「恢复很快」的频控
/// 被我们打成持续全断（vc142 真机 2026-11-28 实锤：取票口 4 次/60s 频控+全频道亚秒全败）。
/// 纯内存静态账：进程重启即清（源端频控本就随时间解除，无需持久化）。
/// </summary>
public static class SourceRateGate
{
    internal static Func<long> Now = static () => Environment.TickCount64; // 测试注入口

    /// <summary>首拒冷却窗：60 秒（源端频控典型解除量级，vc93 换票频控同窗口径）。</summary>
    internal const long BaseCooldownMs = 60_000;

    /// <summary>冷却封顶：10 分钟（连拒 4 拍即到顶，防账目无限膨胀）。</summary>
    internal const long MaxCooldownMs = 600_000;

    private sealed class Account
    {
        public int Strikes;
        public long UntilMs;
    }

    private static readonly ConcurrentDictionary<string, Account> Accounts = new();

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; } // 非法 URL 原串作键（宁误冷却单串不误整站）
    }

    /// <summary>主机冷却中？冷却窗内调用方应本地快速失败，不发网络请求。</summary>
    public static bool IsCooling(string url)
    {
        return Accounts.TryGetValue(HostOf(url), out var a) && a is not null && Now() < a.UntilMs;
    }

    /// <summary>被源端拒绝（4xx/429）：连拒账 +1，冷却窗 60s×2^(n-1) 步进封顶 10 分钟。</summary>
    public static void OnRejected(string url)
    {
        var a = Accounts.GetOrAdd(HostOf(url), _ => new Account());
        lock (a)
        {
            a.Strikes++;
            var shift = Math.Min(a.Strikes - 1, 10); // 封顶移位防溢出
            a.UntilMs = Now() + Math.Min(BaseCooldownMs << shift, MaxCooldownMs);
        }
    }

    /// <summary>任一路对主机请求成功：清账（频控已解除，三路恢复正常放行）。</summary>
    public static void OnOk(string url)
    {
        if (Accounts.TryGetValue(HostOf(url), out var a))
        {
            lock (a) { a.Strikes = 0; a.UntilMs = 0; }
        }
    }

    internal static void Reset() => Accounts.Clear(); // 测试隔离用
}
