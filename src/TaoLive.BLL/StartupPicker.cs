using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// 开机默认台（v1.0.8 第16项，用户 23:28 拍板）：
/// - 收藏列表非空 → 播收藏第一个；收藏为空 → 播常规列表第一个；
/// - 中央一台永远在常规列表第一台（对 CCTV1 综合的位置约束做断言兜底）；
/// - 与循环预检衔接：默认台不可用（预检判死）→ 跳下一个可用台；全部不可用 → 返回 null，UI 亮「无信号」。
/// 纯函数，可测。
/// </summary>
public static class StartupPicker
{
    /// <summary>开播台：收藏优先、常规兜底；health 判死的台跳过（未探测=null 视为可播，预检很快会补上）。</summary>
    public static Channel? Pick(
        IReadOnlyList<Channel> favorites,
        IReadOnlyList<Channel> regular,
        Func<Channel, ChannelHealth>? health = null)
    {
        return FirstAlive(favorites, health) ?? FirstAlive(regular, health);
    }

    private static Channel? FirstAlive(IReadOnlyList<Channel> list, Func<Channel, ChannelHealth>? health)
    {
        foreach (var ch in list)
        {
            if (health == null) return ch;
            if (health(ch) != ChannelHealth.Dead) return ch; // 可用/检测中/未探测都先开播
        }
        return null;
    }
}
