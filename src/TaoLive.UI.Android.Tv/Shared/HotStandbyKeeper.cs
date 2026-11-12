using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TaoLive.BLL;
using TaoLive.Model;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// v25 修法七·热备并行取流守护（老板 12:35 令）：
/// - 当前播放台的主源拉流播放的同时，备援候选源悄悄拉一路做校验——仅「清单+单分片头」轻校验
///   （Range 0–2047 几 KB 级，不建视频传输不解码）；不做合成流（相位差/时间戳不齐，已报老板）；
/// - 校验节拍 ≥15 秒（修法七 1a，不设节拍=持续小流量取流喂风控）；主源为央视频时备路校验降为仅清单级
///   （同 IP 不建双会话）；其余源走「清单+单分片头」；
/// - 生命周期随播放会话（1c）：Start 在新台连接完成 5 秒后（避开首帧补库存同刻并发），换台/退出 Stop 即停；
/// - 请求画像与播放链路同口径（1d：referer/ua 透传播放统一头）；开关关系：断流防护默认开，
///   不受预检/预热开关影响，不设独立开关（1e）；日志只在状态变化时记，不按节拍刷屏；
/// - 备路与主路共用同一本退避账（3：不建第二本）；备路被拒 → 退回单路不硬顶，退避恢复自愈重新热备；
/// - 校验结论只进热备状态机（1b：不进台级三档账、不计台级计负；网络级失败不入账）；
/// - 备援候选 URL 与主路完全相同 → 热备无意义不启动（3b 同流去重）；
/// - 主路断 → TrySwitchAsync 帧级切备路（I 帧边界对齐，内容跳 2–3 秒可接受）；
///   本台会话粘住备路不自动回主路（2a，防二次内容跳变），换台/重进台按健康表自然回归。
/// </summary>
public class HotStandbyKeeper
{
    private readonly StreamResolver _resolver;
    private readonly SourceBackoffLedger _backoff; // 共用退避账（不建第二本）
    private CancellationTokenSource? _cts;
    private long _session; // 会话代数（Stop 即作废，防旧校验循环漏跑）
    private Channel? _channel;
    private string? _standbyUrl;
    private long _standbyFetchedMs; // 接线位 B：备路票取票时刻（票据型按保鲜期换新票）
    private string? _mainUrl;
    private volatile bool _standbyOk;

    /// <summary>接线位 B：备路票据型 URL 保鲜期（150s，与 jstv 节拍同口径——签名期 180s 留 30s 余量）。</summary>
    private const long StandbyTicketTtlMs = 150_000;

    public HotStandbyKeeper(StreamResolver resolver, SourceBackoffLedger backoff)
    {
        _resolver = resolver;
        _backoff = backoff;
    }

    /// <summary>备路就绪且校验通过（供 LiveVideoHost 主路断时帧级切换）。</summary>
    public bool StandbyReady => _standbyOk && _standbyUrl != null;

    /// <summary>连接完成 5 秒后启动校验循环；换台/退出调用 Stop（会话代数作废，校验流即停）。</summary>
    public void Start(int seq, Channel ch, string mainUrl, string? referer, string? ua)
    {
        Stop();
        long session = Interlocked.Increment(ref _session);
        _channel = ch;
        _mainUrl = mainUrl;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(session, seq, ch, mainUrl, referer, ua, _cts.Token));
    }

    /// <summary>换台/退出即停：会话代数作废 + 取消校验循环（修法七 1c，防「画面已切走校验流还在漏跑」）。</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _session);
        _cts?.Cancel();
        _cts = null;
        _channel = null;
        _standbyUrl = null;
        _standbyOk = false;
    }

    private async Task LoopAsync(long session, int seq, Channel ch, string mainUrl, string? referer, string? ua, CancellationToken ct)
    {
        try
        {
            await Task.Delay(5_000, ct); // 修法七 1c：连接完成 5 秒后再起校验流
            // 备援候选：与 ResolveWithFailover 同一口径（用户自有 m3u 按台名匹配）
            var fallback = _resolver.UserFallback;
            var alt = fallback != null ? await fallback(ch) : null;
            if (session != Volatile.Read(ref _session)) return; // 期间换台：作废
            // 修法七 3b：同流去重——备路与主路完全相同则热备无意义，保持单路
            if (string.IsNullOrEmpty(alt) || string.Equals(alt, mainUrl, StringComparison.Ordinal)) return;
            _standbyUrl = alt;
            _standbyFetchedMs = Environment.TickCount64;
            _standbyOk = true;
            CrashNet.Record($"[热备] {ch.Name} 备援就绪，热备启动（每 15 秒轻校验）");
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(15_000, ct); // 修法七 1a：≥15 秒节拍
                if (session != Volatile.Read(ref _session) || _standbyUrl is null) return;
                // 接线位 B（老板 2026-11-12 批准）：备路 URL 是签名/票据型时与主路票同寿命——不刷新则热备备而必过期。
                // 按 URL 形状判短效（源档案 ClassifyUrl），超保鲜期重取一张新票；取不到退单路不硬顶
                if (SourceProfileStore.ClassifyUrl(_standbyUrl) is SourceKind.ShortSign or SourceKind.TicketShort
                    && Environment.TickCount64 - _standbyFetchedMs > StandbyTicketTtlMs)
                {
                    var fresh = fallback != null ? await fallback(ch) : null;
                    if (session != Volatile.Read(ref _session)) return;
                    if (!string.IsNullOrEmpty(fresh) && !string.Equals(fresh, _standbyUrl, StringComparison.Ordinal))
                    {
                        _standbyUrl = fresh;
                        _standbyFetchedMs = Environment.TickCount64;
                        CrashNet.Record($"[热备] {ch.Name} 备援票据到期，已按节拍换新票");
                    }
                    else if (string.IsNullOrEmpty(fresh))
                    {
                        _standbyOk = false;
                        CrashNet.Record($"[热备] {ch.Name} 备援票据到期且换票失败：退回单路，下个节拍重试");
                        continue;
                    }
                }
                // 主路 IP 绑定源（源档案 IpBound，出厂=央视频）→ 备路仅清单级（同 IP 不建双会话）；其余源清单+单分片头
                bool segmentLevel = !SourceProfileStore.Shared.ProfileOf(ch.SourceId).IpBound;
                var (ok, rejected) = await _resolver.ProbeHotStandbyAsync(_standbyUrl, referer, ua, segmentLevel);
                if (rejected)
                {
                    // 修法七 3：备路被服务器明确拒绝 → 退避账照记（共用一本）+ 退回单路不硬顶
                    _backoff.RecordLimited(ch.SourceId, Environment.TickCount64);
                    _standbyOk = false;
                    CrashNet.Record($"[热备] {ch.Name} 备援被拒（403/429）：退回单路，退避恢复后自动重新热备");
                    await Task.Delay(600_000, ct); // 退避恢复后自愈：10 分钟后下个节拍重新校验
                    if (session != Volatile.Read(ref _session) || _standbyUrl is null) return;
                    _standbyOk = true; // 自愈：重新热备（下一轮校验给最终状态）
                    CrashNet.Record($"[热备] {ch.Name} 退避恢复：重新热备");
                    continue;
                }
                if (ok != _standbyOk) // 状态变化才落日志（1e：不按节拍刷屏）
                    CrashNet.Record($"[热备] {ch.Name} 备援校验{(ok ? "恢复可用" : "暂不可达（网络级，不入账）")}");
                _standbyOk = ok;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashNet.Record($"[热备] {ch.Name} 校验流退出：{ex.Message}"); }
    }

    /// <summary>
    /// 主路断 → 备路可用即返回备路流（帧级切换，画面跳 2–3 秒可接受）。
    /// 粘住备路（2a）：切换后本台会话热备清零——不再自动回主路，换台/重进台按健康表自然回归。
    /// 切换预算独立（2b：不占 5 秒去重、90 秒窗 4 次）由 LiveVideoHost 侧计数。
    /// </summary>
    public async Task<(string Url, List<(string Key, string Value)> Options)?> TrySwitchAsync()
    {
        if (!_standbyOk || _standbyUrl is null || _channel is null) return null;
        _standbyOk = false; // 粘住备路：本台会话不再自动回主路
        var ch = _channel;
        var opts = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(ch.Referer)) opts.Add(("http-referrer", ch.Referer));
        if (!string.IsNullOrEmpty(ch.UserAgent)) opts.Add(("http-user-agent", ch.UserAgent));
        CrashNet.Record($"[热备] {ch.Name} 主路断 → 帧级切备援（画面跳 2–3 秒；本台会话粘住备路，不回主路）");
        return (_standbyUrl, opts);
    }
}
