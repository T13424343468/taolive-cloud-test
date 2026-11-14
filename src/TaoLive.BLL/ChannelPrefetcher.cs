using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// vc53 任务8·换台预取票+DNS 预解析（老板 2026-11-14 12:21 批次合并令）：
/// - 换台时对邻台（目录序相邻台）预解析流主机 DNS、预取播放票据——新流首帧后起播少付一次取票时延；
/// - 全部后台网络收编全局单车道 Standard 档（BackgroundLane：首帧门控+播放间隔让路，
///   严禁多源并发）——预取绝不与播放抢路，画面平稳优先令口径不变；
/// - 预取结果只落既有短效缓存/预热池（resolve 委托内部口径），本类不建第二本账（归一纪律）；
/// - DNS 预解析失败静默（取流链路自带系统解析兜底），绝不因预解析挂掉拖垮预取；
/// - IP 字面量流免预解析（无域名可解，收录口径「首条优先纯 IP 免解析」）。
/// 纯逻辑可测：取票与预解析委托注入。
/// </summary>
public class ChannelPrefetcher
{
    private readonly Func<Channel, Task<ResolvedStream?>> _resolveTicket;
    private readonly Func<string, Task>? _prewarmDns;

    public ChannelPrefetcher(
        Func<Channel, Task<ResolvedStream?>> resolveTicket,
        Func<string, Task>? prewarmDns = null)
    {
        _resolveTicket = resolveTicket;
        _prewarmDns = prewarmDns;
    }

    /// <summary>
    /// 换台触发：对 current 在 orderedChannels 中的相邻台（下一台+上一台，越界侧取得到哪侧算哪侧）逐台预取。
    /// 相邻判定按目录序（编号序），与既有 UI 预取队列同一口径。
    /// </summary>
    public async Task PrefetchNeighborsAsync(Channel current, IReadOnlyList<Channel> orderedChannels)
    {
        if (current is null || orderedChannels is null || orderedChannels.Count == 0) return;
        int idx = IndexOf(orderedChannels, current);
        if (idx < 0) return;
        if (idx + 1 < orderedChannels.Count) await PrefetchChannelAsync(orderedChannels[idx + 1]).ConfigureAwait(false);
        if (idx - 1 >= 0) await PrefetchChannelAsync(orderedChannels[idx - 1]).ConfigureAwait(false);
    }

    /// <summary>单台预取：DNS 预解析 → 取票，全程过车道 Standard 档（全局串行+首帧门控+间隔让路）。</summary>
    public async Task PrefetchChannelAsync(Channel channel)
    {
        if (channel is null) return;
        await BackgroundLane.RunAsync(async () =>
        {
            var stream = !string.IsNullOrEmpty(channel.StreamUrl) ? channel.StreamUrl : channel.EntryUrl;
            if (_prewarmDns != null && TryHost(stream, out var host))
            {
                try { await _prewarmDns(host).ConfigureAwait(false); }
                catch { /* DNS 预解析失败不挡预取：取流链路自带兜底 */ }
            }
            await _resolveTicket(channel).ConfigureAwait(false);
        }, tier: BackgroundLane.LaneTier.Standard).ConfigureAwait(false);
    }

    /// <summary>从流/入口地址取域名主机；IP 字面量返回 false（免解析口径）。</summary>
    internal static bool TryHost(string? url, out string host)
    {
        host = "";
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6) return false;
        host = uri.Host;
        return host.Length > 0;
    }

    private static int IndexOf(IReadOnlyList<Channel> list, Channel target)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (ReferenceEquals(c, target)) return i;
            if (string.Equals(c.EpgKey, target.EpgKey, StringComparison.Ordinal)
                && string.Equals(c.SourceId, target.SourceId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }
}
