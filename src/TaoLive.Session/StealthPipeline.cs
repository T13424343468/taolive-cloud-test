using System;
using System.Threading;
using System.Threading.Tasks;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>偷拉装载器（注入便于测试）：从源地址拉一片数据（不进内核，验源+攒池用）。null=本片未取到。</summary>
public interface ISegmentLoader
{
    Task<byte[]?> PullAsync(string sourceUrl, CancellationToken ct);
}

/// <summary>
/// HTTP 偷拉装载器（产线默认）：m3u8 清单→取首个分片地址再拉片；直流→直拉本体（封顶 1MB 验源即够，
/// 池深按切片周期记账不按字节数折算）。失败/异常返回 null 不抛（偷拉是让路角色，绝不打扰前台）。
/// </summary>
public sealed class HttpSegmentLoader : ISegmentLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<byte[]?> PullAsync(string sourceUrl, CancellationToken ct)
    {
        try
        {
            if (QualityHealthStore.IsBlindZoneUrl(sourceUrl))
            {
                return await PullDirect(sourceUrl, ct).ConfigureAwait(false);
            }
            // 清单源：取清单→提首个分片→拉片（只验源可拉性，池深按周期记账）
            using var list = await Http.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!list.IsSuccessStatusCode) return null;
            var text = await list.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                return await PullDirect(line, ct).ConfigureAwait(false);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> PullDirect(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var ms = new System.IO.MemoryStream();
            var buf = new byte[64 * 1024];
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int read, total = 0;
            while ((read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, read);
                total += read;
                if (total >= 1024 * 1024) break; // 封顶 1MB：验源即够，不白吃带宽
            }
            return total > 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 偷拉管道+缓冲池（vc85 蓝本 v2.5，双向无缝切换的备源攒粮线）：
/// 前台在播时对备源/主源单管道偷拉切片，攒池供对齐切换「有粮才切」。
/// - 让路口径：前台水位 &lt; <see cref="GatherGateMs"/>（15 秒）立停偷拉；裕度因子（链路健康线）为 0 也停；
/// - 池深自钳：目标深度=min(<see cref="MaxPoolDepthMs"/>, 资源闸余量注入)，池满即停；
/// - 清单冻结：偷拉中超过 2×切片周期无更新 → <see cref="SupplyFrozen"/>（该源断供记档）；
/// - 重取清单：连败 <see cref="RetakeListAfterConsecutiveFails"/> 次 → <see cref="RetakeListNeeded"/>；
/// - 单管道：Retarget 换向即作废在途（代次防串）；Tick 同步泵（回调 ExecuteSynchronously，测试确定）；
/// - 保温心跳（刀六）：校验通过（首次成功拉片）且池满后，每 <see cref="KeepWarmIntervalMs"/> 续拉一片
///   保鲜——连接不凉、池粮不过期，热备校验流升级秒切备胎；心跳同吃让路闸。
/// </summary>
public sealed class StealthPipeline
{
    /// <summary>让路闸门（毫秒）：前台水位低于此值立停偷拉（带宽让路播放，画面平稳优先令）。</summary>
    public const int GatherGateMs = 15_000;

    /// <summary>池深上限（毫秒）：池满即停偷拉；实际目标=min(此值, 余量因子折算)。</summary>
    public const int MaxPoolDepthMs = 15_000;

    /// <summary>切片周期（毫秒）：池深记账单位，也是清单冻结判据基准。</summary>
    public const int SegmentPeriodMs = 5_000;

    /// <summary>偷拉连败几次判清单需重取。</summary>
    public const int RetakeListAfterConsecutiveFails = 2;

    /// <summary>
    /// 保温心跳间隔（毫秒，=切片周期）：池满且已校验通过后，按此时隔续拉一片保鲜——
    /// 刀六（老板 2026-11-25 九件套定稿）：热备校验流升级秒切备胎，校验通过（首次成功拉片）后
    /// 连接不凉、池粮不过期（心跳成功即刷新断供判据时钟），备援随时可秒切。
    /// </summary>
    public const int KeepWarmIntervalMs = SegmentPeriodMs;

    private readonly ISegmentLoader _loader;
    private readonly Func<long> _monoMs;

    private Channel? _targetChannel;
    private SourceEndpoint? _target;
    private int _generation;
    private Task<byte[]?>? _inFlight;
    private int _inFlightGen;
    private long _pooledMs;
    private long _lastSupplyAtMs = long.MinValue;
    private int _consecutiveFails;
    private bool _frozenAnnounced;
    private long _gateStockMs = GatherGateMs;
    private double _allowFactor = 1.0;
    private bool _validated;                 // 刀六：首次成功拉片=源校验通过，此后池满转保温心跳
    private long _lastKeepWarmAtMs = long.MinValue; // 最近一次发包（含心跳）时刻：心跳节流基准

    /// <summary>偷拉成功一片：参数=（目标台，目标端点，拉完池深）。喂实测账（源头实测可拉）。</summary>
    public event Action<Channel, SourceEndpoint, long>? SegmentPulled;

    /// <summary>清单冻结（超 2×切片周期无更新）：该源断供，预检账记档。</summary>
    public event Action<Channel>? SupplyFrozen;

    /// <summary>偷拉连败（满 2 次）：该源清单需重取。</summary>
    public event Action<Channel>? RetakeListNeeded;

    public StealthPipeline(ISegmentLoader loader, Func<long>? monoMs = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _monoMs = monoMs ?? (() => Environment.TickCount64 & 0x7FFFFFFFFFFFFFFF);
    }

    /// <summary>当前池内存货（毫秒）。</summary>
    public long PooledStockMs
    {
        get
        {
            lock (this) return _pooledMs;
        }
    }

    /// <summary>偷拉目标（诊断/测试观测用）。</summary>
    public SourceEndpoint? Target
    {
        get
        {
            lock (this) return _target;
        }
    }

    /// <summary>
    /// 换向：单管道改偷拉目标，换向即作废在途（代次防串）、清池（池是本次换向攒的粮，换向即弃）。
    /// null=收摊（回主源已接管/换台，不再偷拉）。
    /// </summary>
    public void Retarget(Channel? channel, SourceEndpoint? endpoint)
    {
        lock (this)
        {
            _generation++;
            _targetChannel = channel;
            _target = endpoint;
            _inFlight = null;
            _inFlightGen = 0;
            _pooledMs = 0;
            _consecutiveFails = 0;
            _frozenAnnounced = false;
            _validated = false;              // 刀六：换向即重新验源，保温心跳随旧目标一并作废
            _lastKeepWarmAtMs = long.MinValue;
            _lastSupplyAtMs = endpoint is null ? long.MinValue : _monoMs();
        }
    }

    /// <summary>注闸：前台水位（让路闸）+ 余量因子（链路健康线 AllowFactor）。下一拍 Tick 生效。</summary>
    public void UpdateGates(long playingStockMs, double allowFactor)
    {
        lock (this)
        {
            _gateStockMs = playingStockMs;
            _allowFactor = Math.Clamp(allowFactor, 0, 1);
        }
    }

    /// <summary>
    /// 同步泵（管理器 Tick 驱动）：先收割在途（装载器回调 ExecuteSynchronously 保证测试确定性），
    /// 再判冻结/重取事件，闸门全开且池未满则起下一发偷拉。
    /// </summary>
    public void Tick()
    {
        Channel? channel;
        SourceEndpoint? target;
        int generation;
        lock (this)
        {
            channel = _targetChannel;
            target = _target;
            generation = _generation;
            var now = _monoMs();
            if (channel is null || target is null)
            {
                return; // 收摊态：无目标不泵
            }
            // 清单冻结：偷拉中超 2×切片周期无更新（成功拉片=有更新），一次性宣告直至恢复
            if (_lastSupplyAtMs != long.MinValue
                && now - _lastSupplyAtMs > 2L * SegmentPeriodMs
                && !_frozenAnnounced)
            {
                _frozenAnnounced = true;
                var frozenChannel = channel;
                SessionTrace.Emit($"[偷拉] {frozenChannel.Name ?? frozenChannel.Id} 清单超 {2L * SegmentPeriodMs / 1000}s 无更新，判断供（记档交预检）");
                SupplyFrozen?.Invoke(frozenChannel);
            }
            // 收割在途（装载器回调同步跑：ContinueWith ExecuteSynchronously 口径）
            if (_inFlight is { } running && running.IsCompleted)
            {
                var done = running;
                _inFlight = null;
                int gen = _inFlightGen;
                byte[]? result = done.Status == TaskStatus.RanToCompletion ? done.Result : null;
                if (gen == generation && channel is not null) // 代次防串：换向后的旧在途即弃
                {
                    if (result is not null)
                    {
                        _pooledMs = Math.Min(MaxPoolDepthMs, _pooledMs + SegmentPeriodMs);
                        _lastSupplyAtMs = now;
                        _consecutiveFails = 0;
                        _validated = true; // 刀六：首次成功拉片=源校验通过，池满后转保温心跳
                        if (_frozenAnnounced) _frozenAnnounced = false;
                        SessionTrace.Emit($"[偷拉] {channel.Name ?? channel.Id} 成功拉片，池深 {_pooledMs / 1000}s");
                        SegmentPulled?.Invoke(channel, target, _pooledMs);
                    }
                    else
                    {
                        _consecutiveFails++;
                        if (_consecutiveFails == RetakeListAfterConsecutiveFails)
                        {
                            SessionTrace.Emit($"[偷拉] {channel.Name ?? channel.Id} 连败 {RetakeListAfterConsecutiveFails} 次，需重取清单");
                            RetakeListNeeded?.Invoke(channel);
                        }
                    }
                }
            }
            // 起下一发：闸门全开（前台水位足 + 余量因子非零）才偷；池未满照常攒粮；
            // vc86 F3 让路闸（放行口径同尺）：前台画面在播但水位未稳时偷拉暂停——判稳窗不抽水，
            // 防 15s 线振荡活锁（偷拉压低前台读数→永不判稳→偷拉又停→读数回升→循环）；
            // 刀六保温心跳：已校验通过且池满后，每 KeepWarmIntervalMs 续拉一片保鲜（连接不凉、
            // 池粮不过期——成功即刷新断供判据时钟），备援随时可秒切；心跳同吃让路闸，画面平稳优先令不变
            bool gateOpen = _gateStockMs >= GatherGateMs && _allowFactor > 0
                && (!BackgroundLane.Playing || BackgroundLane.WaterStable);
            long depthCap = Math.Min((long)(MaxPoolDepthMs * _allowFactor), (long)MaxPoolDepthMs);
            bool poolNotFull = _pooledMs + SegmentPeriodMs <= depthCap;
            bool keepWarmDue = _validated && now - _lastKeepWarmAtMs >= KeepWarmIntervalMs;
            if (gateOpen && _inFlight is null && (poolNotFull || keepWarmDue))
            {
                _inFlightGen = generation;
                var pullTarget = target;
                _lastKeepWarmAtMs = now; // 心跳节流基准：含常规攒粮发包（池满后首跳按此时钟顺延）
                _inFlight = _loader.PullAsync(pullTarget.Url, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        try { return t.Status == TaskStatus.RanToCompletion ? t.Result : null; }
                        catch { return (byte[]?)null; }
                    }, TaskContinuationOptions.ExecuteSynchronously);
            }
        }
    }

    /// <summary>
    /// 接管（指针交换口径）：原子取走池内全部存货并清零，交对齐切换动作侧用账。
    /// 真机内核接管后照常重新起播（内核不支持外部注入预取片），池存粮此处作真实「主源实测有粮」凭据与对账数。
    /// </summary>
    public long TakeOver()
    {
        lock (this)
        {
            var taken = _pooledMs;
            _pooledMs = 0;
            _inFlight = null;
            return taken;
        }
    }
}
