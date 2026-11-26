using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>竞速握手探测（六项优化①）：对候选并发握手（只等响应头，不拉数据），谁先成用谁。</summary>
public interface IHandshakeProber
{
    /// <summary>
    /// 对前 maxRacers 路候选并发握手，返回最先握手成功者的候选下标；全败/全超时 -1。
    /// 实现承诺：不取消自身参数令牌（调用方生命周期归调用方）。
    /// </summary>
    Task<int> RaceAsync(Channel channel, IReadOnlyList<SourceEndpoint> candidates, int maxRacers = 3, CancellationToken ct = default);
}

/// <summary>赛马裁判（刀一抽公共）：并发任务谁先报正数下标谁赢，赢者定即撤全场；全败 -1。
/// vc96 P2：赛手任务挂 linked.Token（撤场即真取消，不再空转）；收场走 finally 统一撤场。</summary>
internal static class HandshakeRace
{
    public static async Task<int> FirstWinnerAsync(Func<CancellationToken, List<Task<int>>> startTasks, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var tasks = startTasks(linked.Token); // 赛手挂 linked.Token：赢者定即真取消在途路
            int pending = tasks.Count;
            while (pending > 0)
            {
                var done = await Task.WhenAny(tasks).ConfigureAwait(false);
                pending--;
                int result = await done.ConfigureAwait(false);
                if (result >= 0)
                {
                    linked.Cancel(); // 赢者已定：撤其余在途
                    return result;
                }
                tasks.Remove(done);
            }
            return -1;
        }
        finally
        {
            linked.Cancel(); // 撤场收尾：异常/全败路径同样撤清在途（vc96 P2）
        }
    }
}

/// <summary>
/// HTTP 竞速握手（产线默认）：前 min(maxRacers, count) 路并发 GET（ResponseHeadersRead——
/// 响应头到即判成功，不读体不拉数据），最先成功者赢，其余取消。把 12 秒串行盲等变毫秒级赛马。
/// </summary>
public sealed class HttpHandshakeProber : IHandshakeProber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<int> RaceAsync(Channel channel, IReadOnlyList<SourceEndpoint> candidates, int maxRacers = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return -1;
        int racerCount = Math.Clamp(maxRacers, 1, candidates.Count);
        return await HandshakeRace.FirstWinnerAsync(token => candidates.Take(racerCount).Select(async (ep, i) =>
        {
            try
            {
                using var resp = await Http.GetAsync(ep.Url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                return code >= 200 && code < 400 ? i : -1;
            }
            catch
            {
                return -1; // 超时/取消/网络错：本路败，不掀整场
            }
        }).ToList(), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// 刀一·竞速与取票合成一次（老板 2026-11-25 九件套定稿）：自救回合的选源竞速原先只探端点可达，
/// 赢家起播还要再付一次取票网络（两次串行）。合成口径——
/// - Web 取票口端点：直接以「换票成功」为探活判据（换票即探通，票落进程级 5 分钟缓存，
///   赢家起播取票零等待）；失败即该路败，与旧探活同账（预检探通同用此判据，口径归一）；
/// - 直连端点：照旧握手赛马（无票可取，一次连接即取流本身）。
/// resolve 委托由壳层注入（TicketResolver.ResolveAsync，缓存进程级共享，不建第二本账）。
/// </summary>
public sealed class TicketFusedProber : IHandshakeProber
{
    private readonly IHandshakeProber _inner; // 直连端点握手赛马
    private readonly Func<Channel, SourceEndpoint, Task<string?>> _resolve;

    public TicketFusedProber(IHandshakeProber inner, Func<Channel, SourceEndpoint, Task<string?>> resolve)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public async Task<int> RaceAsync(Channel channel, IReadOnlyList<SourceEndpoint> candidates, int maxRacers = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || channel is null) return -1;
        int racerCount = Math.Clamp(maxRacers, 1, candidates.Count);
        return await HandshakeRace.FirstWinnerAsync(token => candidates.Take(racerCount).Select(async (ep, i) =>
        {
            try
            {
                if (ep.Kind == SourceKind.Web)
                {
                    var url = await _resolve(channel, ep).ConfigureAwait(false); // 取票即探通（合成一次）
                    return string.IsNullOrEmpty(url) ? -1 : i;
                }
                return await _inner.RaceAsync(channel, new[] { ep }, 1, token).ConfigureAwait(false) >= 0 ? i : -1;
            }
            catch
            {
                return -1; // 单路败不掀整场
            }
        }).ToList(), ct).ConfigureAwait(false);
    }
}
