using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 会话管理器：会话表（一个频道一个会话）、当前会话切换、事件回主线程。
/// 换台时旧会话被 Supersede（停内核、状态归 Idle、不再响应内核事件），
/// 新会话按频道 Id 从会话表复用，同频道切回即原地重起播。
/// M3 升级 Current+Standby 双实例热切（方案书 v5：主界面只收事件不掺内部）：
/// - PrepareStandby 预先把目标台会话攒流（无面起播，预取水位线内后头填）；
/// - SwitchTo 时若热备已达 Playing（起播门槛线已过），同步换面接管——
///   新画面直接覆盖旧画面，旧会话随后让位，主界面无感、不露黑；
/// - 无热备或热备未就绪时回落既有会话表复用路径（冷切），W1 契约不变。
/// 每会话独享内核实例（kernelFactory 注入；不注入则共用单内核=只走冷切）。
/// 会话表按 LRU 封顶（MaxRetainedSessions），淘汰已让位会话并释放内核，
/// 热备资源闸（方案书：内存 ≤30MB 初值）不被历史频道内核累积击穿。
/// </summary>
public sealed class PlaybackSessionManager
{
    /// <summary>会话表容量上限（LRU 淘汰已让位会话；Current/Standby 永不淘汰）。收口批③后=高配档默认值，实际容量经 RetainedCapacity 分档。</summary>
    public const int MaxRetainedSessions = 8;

    /// <summary>回主源稳定窗口（方案书收口批①·迟滞判据初值）：断流自救后播放无断流满此时长才回；终值随真机定标。</summary>
    public static readonly TimeSpan ReturnToPrimaryStableFor = TimeSpan.FromMinutes(5);

    /// <summary>回主源健康账门槛：主源预检轮次连续命中 OK 至少此次数（复用预检结果，不另起独立探测）。</summary>
    public const int ReturnToPrimaryMinConsecutiveOks = 3;

    /// <summary>热备攒流窗口（方案书 v75）：窗口内攒不到起播门槛线（切片预取双曲线 7 秒同口径）即放弃热备转冷切，不无限等；终值随真机定标。</summary>
    public static readonly TimeSpan StandbyAccumulateWindow = TimeSpan.FromSeconds(7);

    /// <summary>会话表容量（收口批③·LRU 按内存分档）：高配 8（兼作既有常量默认）；低配 4。</summary>
    public const int HighMemorySessions = 8;

    /// <summary>低配档容量：低配 4-5 取下限（低配档内单会话预算更紧），终值随真机定标。</summary>
    public const int LowMemorySessions = 4;

    /// <summary>分档界线初值：memoryClass ≤128MB 低配档、&gt;128MB 高配档；定标后只调常量不改逻辑。</summary>
    public const int LowMemoryClassThresholdMb = 128;

    /// <summary>按设备内存分档（方案书收口批③）：界线改动只动常量。</summary>
    public static int CapacityFor(int memoryClassMb) =>
        memoryClassMb <= LowMemoryClassThresholdMb ? LowMemorySessions : HighMemorySessions;

    private readonly Func<PlaybackSession> _sessionFactory;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Action<Channel>? _onSwitchingChannel;
    private readonly bool _hotStandbyEnabled;
    private readonly Dictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.Ordinal);
    // 超龄清理的时间账（老板令 #21）：会话让位（不再是 Current/Standby）时按单调钟记账；
    // 与 LRU 序数账 _lastUsed 分开——序数是字典内部记账，不能和毫秒钟相减。
    private readonly Dictionary<string, long> _supersededAtMs = new(StringComparer.Ordinal);
    private readonly object _returnGate = new();
    private readonly Func<long> _monotonicMs;
    private readonly RetryBackoffAccount _retryBackoff;
    private Func<Channel, IReadOnlyList<SourceEndpoint>>? _fallbackProvider;
    private Func<Channel, bool>? _primaryAliveConfirmed;
    private PlaybackSession? _returnStableSession;
    private long _returnStableSinceMs;
    private long _standbySinceMs;
    private long _stamp;

    // ---- vc85 双向无缝切换接线（蓝本 v2.5：仲裁器/偷拉管道/链路健康线/握手竞速）----
    // 全部可空注入：未接=null=旧硬切口径（既有测试与未升级壳层契约不变）；壳层建齐后软着陆/对齐切/逃生口生效。
    private StealthPipeline? _pipeline;             // 偷拉管道（SegmentLoader 注入后懒建）
    private long _stealthHealthLastMs = long.MinValue; // 链路健康线掉速观察上一拍
    private BitrateGovernor? _bitrateGovernor;      // 分片级码率自适应（六项优化④，健康线接线后懒建）
    private long? _lastBitrateCeiling;              // 上拍下发内核的码率上限（比对换档才下发，防抖）
    private long _stealthHealthLastStockMs;            // 同上（两拍差算掉速）
    private long? _alignEpisodeAtMs;                // 升档对齐切在途账（逃生口结果回账用；只记升往 0 路的对齐切）
    private bool _raceInFlight;                     // 握手竞速单飞旗（同一切换回合只赛一场）

    /// <summary>切换仲裁器（vc85 蓝本三原语）：未接=null=预防硬切与回主源硬切旧口径；接上后软着陆/对齐回主源/逃生口生效。</summary>
    public SwitchArbiter? Arbiter { get; set; }

    /// <summary>链路健康线（vc85 两增强①）：水位掉速+分片间隔+吞吐+坏帧合成裕度，偷拉闸门与坏帧喂账共用。</summary>
    public LinkHealthLine? HealthLine { get; set; }

    /// <summary>偷拉装载器（vc85 ②偷拉管道）：注入后管理器泵内懒建 <see cref="StealthPipeline"/>；未接=不偷拉。</summary>
    public ISegmentLoader? SegmentLoader { get; set; }

    /// <summary>握手竞速探测（vc85 六项优化①）：自救回合内对队列前几路并发握手，先成者优；未接=串行盲等旧口径。</summary>
    public IHandshakeProber? RaceProber { get; set; }

    /// <summary>主线程投递器（vc85 竞速回填用）：竞速回调在后台线程，重排队须经此回主线程；未接=竞速只记账不回填。</summary>
    public Action<Action>? MainDispatcher { get; set; }

    public PlaybackSessionManager(Func<PlaybackSession> sessionFactory, IMainThreadDispatcher? dispatcher = null,
        Action<Channel>? onSwitchingChannel = null, bool hotStandbyEnabled = false,
        Func<Channel, bool>? primaryAliveConfirmed = null, Func<long>? monotonicMs = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();
        _onSwitchingChannel = onSwitchingChannel;
        _hotStandbyEnabled = hotStandbyEnabled;
        _primaryAliveConfirmed = primaryAliveConfirmed;
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64); // 单调时钟（方案书 v12：计时账一律按单调钟）
        _retryBackoff = new RetryBackoffAccount(_monotonicMs);
    }

    /// <summary>会话表容量上限（收口批③·LRU 按内存分档）：壳层按设备 memoryClass 经 CapacityFor 设定。</summary>
    public int RetainedCapacity { get; set; } = HighMemorySessions;

    /// <summary>
    /// OK 重试接退避账（收口批③）：Failed 态手动重试放行判定；退避窗未满返回 false 并带出
    /// 还须等待的毫秒数（壳层提示节奏）。播放成功起播即清账（StateChanged Playing）。
    /// </summary>
    public bool TryManualRetry(string channelId, out long retryAfterMs) =>
        _retryBackoff.TryAcquire(channelId ?? string.Empty, out retryAfterMs);

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：新会话与既有会话统一挂供给；未设=不吃内置表。
    /// </summary>
    public Func<Channel, IReadOnlyList<SourceEndpoint>>? FallbackSourceProvider
    {
        get => _fallbackProvider;
        set
        {
            _fallbackProvider = value;
            foreach (var session in _sessions.Values)
            {
                session.FallbackSourceProvider = value;
            }
        }
    }

    /// <summary>
    /// 回主源健康账判据（方案书收口批①）：主源预检轮次连续命中 OK ≥3 次才准回；未设=不回主源（从严）。
    /// </summary>
    public Func<Channel, bool>? PrimaryAliveConfirmed
    {
        get => _primaryAliveConfirmed;
        set => _primaryAliveConfirmed = value;
    }

    /// <summary>#74 源队列健康排序（2026-11-15 老板令）：转配到全部会话（后建会话经 WireSession 同挂）；
    /// 未设=按原表序。</summary>
    public Func<Channel, IEnumerable<SourceEndpoint>, IEnumerable<SourceEndpoint>>? QueueOrderer
    {
        get => _queueOrderer;
        set
        {
            _queueOrderer = value;
            foreach (var session in _sessions.Values)
            {
                session.QueueOrderer = value;
            }
        }
    }
    private Func<Channel, IEnumerable<SourceEndpoint>, IEnumerable<SourceEndpoint>>? _queueOrderer;

    /// <summary>源实测账（vc85 蓝本 v2.1 健康账两本之二）：管理器统一持有，建会话/后建会话经 WireSession 同挂；
    /// 壳层 QueueOrderer 与会话记账共用同一实例，两读数同源不错位。</summary>
    public QualityHealthStore? QualityBook { get; set; }

    /// <summary>当前前台会话；尚未切换过为空。</summary>
    public PlaybackSession? Current { get; private set; }

    /// <summary>热备会话（PrepareStandby 攒流中的目标台）；未备为空。</summary>
    public PlaybackSession? Standby { get; private set; }

    /// <summary>会话表快照：频道 Id 到会话的映射，只读。</summary>
    public IReadOnlyDictionary<string, PlaybackSession> Sessions => _sessions;

    /// <summary>壳层退场全表清场（vc68 退出令）：让位并停掉会话表内全部会话（含当前台/热备/表内
    /// 让位会话）——此前退出只让位 Current+Standby，vc67 下 Failed 漏停的孤儿内核无人接管，
    /// 退软件后音频照放。渲染必须结束口径：退场即全停，一个内核不留。</summary>
    public void Shutdown()
    {
        foreach (var session in _sessions.Values)
        {
            session.Supersede(); // vc68 起非 Idle 一律停，含 Failed 态漏音内核
        }
        Current = null;
        Standby = null;
    }

    /// <summary>当前会话切换完成事件：保证经主线程调度器触发。</summary>
    public event EventHandler<PlaybackSession>? CurrentChanged;

    /// <summary>任一会话状态变化事件：保证经主线程调度器触发。</summary>
    public event EventHandler<SessionStateEventArgs>? SessionStateChanged;

    /// <summary>裸建一个会话并置为当前（保留 W1 语义，供壳层自定义起播）。</summary>
    public PlaybackSession Begin()
    {
        var session = _sessionFactory();
        WireSession(session);
        Current = session;
        session.IsForeground = true; // vc69 让道修：车道门控只认前台会话（管理器统一维护）
        RaiseCurrentChanged(session);
        return session;
    }

    /// <summary>
    /// 热备请求（换台竞速思路·vc53）：为指定频道提前备一个可接管会话。
    /// 同台重复请求、目标即当前台均无害（返回既有会话不重播）；
    /// 换备目标时旧热备先行让位（停内核，不白烧网络）。
    /// 新会话无面起播攒流（ExoPlayer 无面照样缓冲），到 Playing 即具备接管条件。
    /// 未开热备（共用单内核）时返回空。
    /// </summary>
    public PlaybackSession? PrepareStandby(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!_hotStandbyEnabled || !channel.IsValid) return null;
        if (Current is { } cur && string.Equals(cur.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal))
        {
            return cur; // 就是当前台，无需备
        }
        if (Standby is { } sb && string.Equals(sb.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal))
        {
            return sb; // 已在备
        }
        // 旧热备让位：换备目标即停旧攒流（内核独享实例，不停则白烧网络）
        if (Standby is { } oldSb
            && !string.Equals(oldSb.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal)
            && !ReferenceEquals(oldSb, Current))
        {
            SessionTrace.Emit($"[热备] 备台 {oldSb.CurrentChannel?.Name ?? oldSb.CurrentChannel?.Id ?? "?"} 让位（攒流 {_monotonicMs() - _standbySinceMs}ms 未接管），改备 {channel.Name}");
            oldSb.Supersede();
            MarkSuperseded(oldSb); // 超龄账起算（#21）
        }
        if (_sessions.TryGetValue(channel.Id, out var existing))
        {
            Touch(channel.Id);
            _supersededAtMs.Remove(channel.Id); // 重新成为热备，超龄账清零
            existing.ChangeChannel(channel);
            existing.PausePlayback(); // vc68 漏音根因①：备台静音攒流，不出声不出画（缓冲照常）
            Standby = existing;
            _standbySinceMs = _monotonicMs(); // 攒流超时账起算（v75）
            SessionTrace.Emit($"[热备] 备台 {channel.Name} 起播攒流（会话表复用）");
            return existing;
        }
        var session = _sessionFactory();
        _sessions[channel.Id] = session;
        _lastUsed[channel.Id] = _stamp++;
        _supersededAtMs.Remove(channel.Id);
        WireSession(session);
        EvictIfOverCapacity();
        session.ChangeChannel(channel); // 无面起播：攒流到门槛线，不接管画面
        session.PausePlayback(); // vc68 漏音根因①：备台静音攒流，不出声不出画（缓冲照常）
        Standby = session;
        _standbySinceMs = _monotonicMs();
        SessionTrace.Emit($"[热备] 备台 {channel.Name} 起播攒流（新会话）");
        return session;
    }

    /// <summary>切到指定频道：热备就绪走热切（接管），否则冷切重播。
    /// vc68 接管修（老板拍板「这版最重要的就是热切」+真机黑屏实锤）：接管次序倒过来——
    /// 此前新内核先挂面、旧内核后让位，两颗解码器同时连一个屏（后连的被挤掉/输出落不了屏=黑屏实锤）；
    /// 改为旧内核先摘面停机、新内核再挂面恢复，解码器与屏一对一连接。让位瞬间旧画面冻结一拍
    /// 由切台遮罩（不透明水墨底）盖住，不露黑。</summary>
    public PlaybackSession SwitchTo(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        // vc76 审查清零 P2 前置校验：无效频道在 Current 易主前抛出，会话表状态不被半途污染
        if (!channel.IsValid)
        {
            throw new ArgumentException($"Channel {channel.Id} has no valid sources", nameof(channel));
        }
        _onSwitchingChannel?.Invoke(channel); // 换台即触发邻台预取（ChannelPrefetcher，单车道让路口径）

        // 热切：热备就是目标台且已达 Playing（起播门槛线已过）→ 接管
        if (Current is { } cur
            && Standby is { } sb
            && string.Equals(sb.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal)
            && sb.State == PlaybackState.Playing)
        {
            // 热切打点（埋点补充 2026-11-15）：攒流到接管的真实时长落账，换台快慢归因热备链路可查
            SessionTrace.Emit($"[热备] 热切接管 {channel.Name}（攒流 {_monotonicMs() - _standbySinceMs}ms 后出画就绪）");
            // vc68 接管修：旧内核先彻底退场（摘面+停机，屏连接先释放），新内核再干净接管——
            // 解码器与屏一对一，不再两颗抢一面（黑屏根因）。让位瞬间的旧帧由遮罩盖住不露黑。
            cur.Supersede();
            MarkSuperseded(cur); // 超龄账起算（#21）
            sb.AttachSurface(cur.SurfaceHandle);
            sb.ResumePlayback(); // 备台静音攒流至此恢复出声出画（缓冲攒流不中断）
            Current = sb;
            sb.IsForeground = true; // vc69 让道修：接管前台，门控归当前会话（让位会话已在 Supersede 清旗）
            Standby = null;
            _supersededAtMs.Remove(channel.Id); // 目标台接管前台，超龄账清零
            Touch(channel.Id);
            RaiseCurrentChanged(sb);
            return sb;
        }

        // 冷切（W1 契约路径）：旧会话让位，目标会话从会话表复用或新建，然后起播。
        if (Standby is { } notReady
            && string.Equals(notReady.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal))
        {
            SessionTrace.Emit($"[热备] 热备未就绪（{notReady.State}，攒流 {_monotonicMs() - _standbySinceMs}ms），冷切 {channel.Name}");
        }
        var prev = Current;
        if (prev is { } p
            && p.CurrentChannel is { } prevChannel
            && !string.Equals(prevChannel.Id, channel.Id, StringComparison.Ordinal))
        {
            p.Supersede();
            MarkSuperseded(p); // 超龄账起算（#21）
        }
        if (ReferenceEquals(Standby, prev)) Standby = null;
        if (!_sessions.TryGetValue(channel.Id, out var session))
        {
            session = _sessionFactory();
            _sessions[channel.Id] = session;
            _lastUsed[channel.Id] = _stamp++;
            WireSession(session);
        }
        if (ReferenceEquals(Standby, session)) Standby = null;
        Touch(channel.Id);
        _supersededAtMs.Remove(channel.Id); // 目标台接管前台，超龄账清零
        Current = session;
        // vc76 审查清零 P2（车道门控冷切窗回归）：先接管前台再 ChangeChannel——vc69 让道修后
        // 门控只认前台会话，旧会话让位不翻门、新会话若在 ChangeChannel 的 Transition(Buffering)
        // 之后才接管，起播期零后台网络门全程关不上（预检探针抢道换台带宽）。次序换位即闭合。
        session.IsForeground = true;
        EvictIfOverCapacity();
        session.ChangeChannel(channel);
        RaiseCurrentChanged(session);
        return session;
    }

    /// <summary>按频道 Id 查会话；查无返回空。</summary>
    public PlaybackSession? TryGet(string channelId) =>
        _sessions.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>
    /// 内存压力缩编（老板令 #25：低配机不等系统来杀，先自瘦身）：系统内存告警时，
    /// 让位会话（非 Current/Standby）全数出表并释放内核，当前台与热备不动——
    /// 旧台缓存是「锦上添花」，内存吃紧时第一个让路。返回清掉的会话数（壳层落台账）。
    /// </summary>
    public int ShrinkForMemoryPressure()
    {
        List<string>? victims = null;
        foreach (var (id, s) in _sessions)
        {
            if (ReferenceEquals(s, Current) || ReferenceEquals(s, Standby)) continue;
            (victims ??= new List<string>()).Add(id);
        }
        if (victims is null) return 0;
        foreach (var id in victims)
        {
            var victim = _sessions[id];
            _sessions.Remove(id);
            _lastUsed.Remove(id);
            _supersededAtMs.Remove(id);
            try { victim.ReleaseKernel(); }
            catch { /* 释放失败不拖垮缩编：内核随进程回收 */ }
        }
        if (victims.Count > 0)
        {
            SessionTrace.Emit($"[资源] 内存压力缩编：让位会话清 {victims.Count} 个（当前台/热备保留，老板令 #25）");
        }
        return victims.Count;
    }

    /// <summary>看门狗泵：由壳层定时器周期调用，仅作用于当前会话；顺带跑预防性介入（老板令 #24）、回主源迟滞判据与热备攒流超时账。</summary>
    public void Tick()
    {
        var current = Current;
        if (current is null)
        {
            lock (_returnGate) _returnStableSession = null;
            TryStandbyTimeout();
            return;
        }
        current.CheckWatchdog();
        current.CheckProactiveFailover();
        TryReturnToPrimary(current);
        PumpStealth(current);
        PumpAlignOutcome(current);
        PumpRaceProbe(current);
        Arbiter?.SweepPending();
        TryStandbyTimeout();
    }

    /// <summary>
    /// 偷拉管道泵（vc85 ②偷拉管道+⑤两增强①，管理器 Tick 同拍驱动，不新增泵）：
    /// - 让路闸：前台水位（<see cref="PlaybackSession.BufferedStockMs"/>）+ 链路裕度因子注入；
    /// - 单管道换向：朝正在播一路的反方向——在播备源偷主源（焊死 0 路），在播主源偷下一备路；
    /// - 拉片成功喂实测账（主源有粮凭据）；掉速观察喂链路健康线（两增强①）。
    /// 未接 SegmentLoader/HealthLine=全链静默（管道不建）。
    /// </summary>
    private void PumpStealth(PlaybackSession current)
    {
        if (HealthLine is null || SegmentLoader is null) return;
        if (_pipeline is null)
        {
            _pipeline = new StealthPipeline(SegmentLoader, _monotonicMs);
            _pipeline.SegmentPulled += (ch, ep, pooled) =>
            {
                // 主源有粮实测凭据（蓝本八项落法：回主源判据加实测二次确认——分片真拉到才准切）
                if (QualityBook is { } book)
                {
                    book.ReportGood(ch.EpgKey, ch.SourceId, ep.Url);
                }
            };
            _pipeline.SupplyFrozen += ch => SessionTrace.Emit($"[偷拉] {ch.Name ?? ch.Id} 断供记档（清单冻结，交预检账）");
            _pipeline.RetakeListNeeded += ch =>
            {
                SessionTrace.Emit($"[偷拉] {ch.Name ?? ch.Id} 清单需重取（连败，作废票据缓存换新清单）");
                foreach (var ep in ch.Sources)
                {
                    PlaybackSession.TicketCacheInvalidator?.Invoke(ch.Id, ep); // 清单重取触发（蓝本八项落法）
                }
            };
        }
        var pipe = _pipeline;
        // 掉速观察喂链路健康线（两增强①）：两拍水位差折每秒掉速，EMA 归并
        long nowMs = _monotonicMs();
        long buffered = current.BufferedStockMs;
        if (_stealthHealthLastMs != long.MinValue && nowMs > _stealthHealthLastMs)
        {
            HealthLine.ReportStockDrop(buffered, _stealthHealthLastStockMs, nowMs - _stealthHealthLastMs);
        }
        _stealthHealthLastMs = nowMs;
        _stealthHealthLastStockMs = buffered;
        pipe.UpdateGates(buffered, HealthLine.AllowFactor());
        // 分片级码率自适应（六项优化④）：健康裕度快照喂迟滞闸，换档才经 IPlayer 契约下发内核
        // （一细一粗与整路降档互补：整路管撑不撑得住，档内管带宽变薄）。LoggingPlayer 等 DIM 内核=无害空转。
        _bitrateGovernor ??= new BitrateGovernor();
        var ceiling = _bitrateGovernor.Update(HealthLine.Margin(), nowMs);
        if (!Nullable.Equals(ceiling, _lastBitrateCeiling))
        {
            _lastBitrateCeiling = ceiling;
            current.Kernel.ApplyBitrateCeiling(ceiling);
        }
        // 单管道换向：朝正在播一路的反方向（换向即作废在途清池，代次防串在管道内）
        var channel = current.CurrentChannel;
        SourceEndpoint? desired = null;
        if (channel is { } ch && current.State is PlaybackState.Playing or PlaybackState.Buffering)
        {
            desired = current.CurrentLineIndex > 0
                ? ch.Sources.FirstOrDefault() // 在播备源→偷主源（主源身份焊死，运行期不漂移）
                : current.PeekQueueEndpoint(1); // 在播主源→偷下一备路（降档有粮）
        }
        if (!string.Equals(desired?.Url, pipe.Target?.Url, StringComparison.Ordinal))
        {
            pipe.Retarget(channel, desired);
        }
        pipe.Tick();
    }

    /// <summary>升档结果回账泵（vc85 ④逃生口）：升往 0 路的对齐切站满 <see cref="SwitchArbiter.UpgradeFailWindow"/>
    /// 且画面在播=站住，逃生口账清零；窗内再断由 WireSession 的 Failover 钩子记失败。</summary>
    private void PumpAlignOutcome(PlaybackSession current)
    {
        if (Arbiter is null || _alignEpisodeAtMs is not { } started) return;
        long nowMs = _monotonicMs();
        if (nowMs - started < SwitchArbiter.UpgradeFailWindow.TotalMilliseconds) return;
        if (current.CurrentLineIndex == 0 && current.State == PlaybackState.Playing)
        {
            Arbiter.ReportOutcome(true); // 站住：逃生口账清零
        }
        _alignEpisodeAtMs = null; // 一期一账：失败已由 Failover 钩子记账，站住已复位
    }

    /// <summary>握手竞速泵（vc85 六项优化①）：自救回合（Failed 态）对队列前 3 路并发握手，
    /// 先成者回填队列头（下拍起播/换线按新序取路）；探测结果喂实测账（竞速喂健康账口径）。
    /// 刀一（老板 2026-11-25 九件套定稿）：候选改传端点本体——Web 取票口端点由探针直接换票探通
    /// （票落进程级缓存，赢家起播取票零等待），选源竞速与取流两次串行网络合成一次。
    /// 单飞旗防同回合连发；全败不动序（串行盲等旧口径兜底）。</summary>
    private void PumpRaceProbe(PlaybackSession current)
    {
        if (RaceProber is null || _raceInFlight) return;
        if (current.State != PlaybackState.Failed || current.LineCount < 2) return;
        var channel = current.CurrentChannel;
        if (channel is null) return;
        var candidates = Enumerable.Range(0, Math.Min(3, current.LineCount))
            .Select(i => current.PeekQueueEndpoint(i)).Where(ep => ep is { IsValid: true }).Cast<SourceEndpoint>().ToList();
        if (candidates.Count < 2) return;
        _raceInFlight = true;
        var episodeFailover = current.FailoverCount;
        _ = RaceProber.RaceAsync(channel, candidates).ContinueWith(t =>
        {
            try
            {
                int winner = t.Status == TaskStatus.RanToCompletion ? t.Result : -1;
                MainDispatcher?.Invoke(() =>
                {
                    _raceInFlight = false;
                    // 回合防串：竞速期间会话已切走/换台/新一轮自救=作废，不追溯（自救队列快照化纪律）
                    if (winner < 0 || ReferenceEquals(Current, current) == false
                        || current.State != PlaybackState.Failed || current.FailoverCount != episodeFailover) return;
                    if (winner > 0)
                    {
                        SessionTrace.Emit($"[竞速] {channel.Name ?? channel.Id} 前 {candidates.Count} 路握手赛马，第 {winner + 1} 路先成，提到队头");
                        current.ReorderQueueWinnerFirst(winner);
                    }
                });
            }
            catch { _raceInFlight = false; }
        });
    }

    /// <summary>
    /// 热备攒流超时（方案书 v75）：窗口内未达门槛（Playing）即 Supersede 热备转冷切，不无限等——
    /// 用户侧由后续 SwitchTo 自然走冷切路径不卡死，但不再烧网络白攒；
    /// 已达门槛（Playing）的热备不受窗口约束，常备待热切。计时按单调钟（v12）。
    /// </summary>
    private void TryStandbyTimeout()
    {
        var sb = Standby;
        if (sb is null || ReferenceEquals(sb, Current) || sb.State == PlaybackState.Playing) return;
        if (_monotonicMs() - _standbySinceMs < StandbyAccumulateWindow.TotalMilliseconds) return;
        // 攒流超时打点（埋点补充 2026-11-15）：热备为何没了的账面依据（攒流烧满窗口未达门槛）
        SessionTrace.Emit($"[热备] 备台 {sb.CurrentChannel?.Name ?? sb.CurrentChannel?.Id ?? "?"} 攒流超时 {StandbyAccumulateWindow.TotalSeconds}s 未达门槛，放弃热备转冷切");
        sb.Supersede(); // 攒不到门槛即放弃热备；会话留表，冷切可复用
        MarkSuperseded(sb); // 超龄账起算（#21）
        Standby = null;
    }

    /// <summary>
    /// 稳定后回主源（方案书收口批①·迟滞判据；vc85 蓝本 v2.5 升级为对齐切）：
    /// 播放无断流满 5 分钟（回后若再断重新计时）且主源预检连续 OK ≥3 次，
    /// 仅当前台前台稳定时触发，热备攒流中不触发（Tick 只泵 Current，防主备横跳）。
    /// 仲裁器已接=对齐软切口径：休眠期拒绝（逃生口）、主源池粮不足 15s 不切（攒够才切）、
    /// 同向冷却 5 分钟防横跳，切走 <see cref="PlaybackSession.AlignedSwitchTo"/> 原语；
    /// 未接=旧硬切 SwitchLine(0) 契约不变（既有测试与降级口径）。
    /// </summary>
    private void TryReturnToPrimary(PlaybackSession current)
    {
        if (current.CurrentLineIndex <= 0 || current.State != PlaybackState.Playing)
        {
            return;
        }
        lock (_returnGate)
        {
            if (!ReferenceEquals(_returnStableSession, current)
                || _monotonicMs() - _returnStableSinceMs < ReturnToPrimaryStableFor.TotalMilliseconds)
            {
                return;
            }
            var channel = current.CurrentChannel;
            if (channel is null || _primaryAliveConfirmed?.Invoke(channel) != true)
            {
                return;
            }
            _returnStableSession = null; // 回后若再断由 Failover 事件重新起窗计时
        }
        if (Arbiter is not { } arbiter)
        {
            // 回主源打点（埋点补充 2026-11-15）：稳定 5 分钟+主源连续 3 轮 OK 的双判据同时满足才触发，此账回答「怎么又切回原线路了」
            SessionTrace.Emit($"[回主源] {current.CurrentChannel?.Name ?? current.CurrentChannel?.Id ?? "?"} 稳定 {ReturnToPrimaryStableFor.TotalMinutes}min 且主源预检连续 {ReturnToPrimaryMinConsecutiveOks} 轮 OK，切回主源");
            current.SwitchLine(0);
            return;
        }
        // —— 对齐软切口径（蓝本 v2.5）：三道闸全过才切 ——
        if (arbiter.IsPrimaryDormant)
        {
            SessionTrace.Emit($"[回主源] {current.CurrentChannel?.Name ?? "?"} 主源休眠中（逃生口已触发），期内不回；升档目标=实测账最优备路");
            return;
        }
        if (_pipeline is not null && _pipeline.PooledStockMs < StealthPipeline.MaxPoolDepthMs)
        {
            // 切换条件=主源库存也攒够 15s（池内有粮=偷拉分片真拉到的实测凭据）；粮不足继续攒
            return;
        }
        if (!arbiter.Submit(SwitchIntentKind.ReturnPrimary))
        {
            return; // 同向冷却 5 分钟：防横跳（对齐软切不吃故障限速闸，但吃自己的方向冷却）
        }
        SessionTrace.Emit($"[回主源] {current.CurrentChannel?.Name ?? "?"} 稳定 {ReturnToPrimaryStableFor.TotalMinutes}min+预检连续 OK+池粮足，对齐切回主源（软切）");
        _alignEpisodeAtMs = _monotonicMs(); // 升档一期起账：站住复位/窗内再断记逃生口
        current.AlignedSwitchTo(0, "return to primary");
    }

    private void Touch(string channelId)
    {
        if (_lastUsed.ContainsKey(channelId)) _lastUsed[channelId] = _stamp++;
    }

    /// <summary>已让位会话寿命（老板令 2026-11-15 #21）：上次使用超此时长的热备/留台会话一律释放——
    /// 缓冲内容永远只服务近期看过的台，旧台不囤积。5 分钟兼作秒回切窗口，终值随真机定标。</summary>
    public static readonly TimeSpan SupersededSessionTtl = TimeSpan.FromMinutes(5);

    /// <summary>资源闸：先清超龄已让位会话（老板令 #21：旧台限时丢弃，超龄即清，与容量无关），
    /// 再超容淘汰最久未用者；Current/Standby 永不清。
    /// 超龄按让位时间账（_supersededAtMs，单调钟）；无账可查的让位会话视作刚让位，不立即清（防误杀）。</summary>
    private void EvictIfOverCapacity()
    {
        long nowMs = _monotonicMs();
        List<string>? staleIds = null;
        foreach (var (id, s) in _sessions)
        {
            if (ReferenceEquals(s, Current) || ReferenceEquals(s, Standby)) continue;
            long supersededAt = _supersededAtMs.GetValueOrDefault(id, nowMs);
            if (nowMs - supersededAt >= SupersededSessionTtl.TotalMilliseconds)
                (staleIds ??= new List<string>()).Add(id);
        }
        if (staleIds != null)
        {
            foreach (var id in staleIds)
            {
                var stale = _sessions[id];
                _sessions.Remove(id);
                _lastUsed.Remove(id);
                _supersededAtMs.Remove(id);
                try { stale.ReleaseKernel(); }
                catch { /* 超龄释放失败不拖垮会话表：内核随进程回收 */ }
            }
            SessionTrace.Emit($"[热备] 超龄让位会话清理 {staleIds.Count} 个（寿命 {SupersededSessionTtl.TotalMinutes:0} 分钟，老板令 #21）");
        }
        while (_sessions.Count > RetainedCapacity)
        {
            string? victimId = null;
            long oldest = long.MaxValue;
            foreach (var (id, s) in _sessions)
            {
                if (ReferenceEquals(s, Current) || ReferenceEquals(s, Standby)) continue;
                long used = _lastUsed.GetValueOrDefault(id, 0);
                if (used < oldest)
                {
                    oldest = used;
                    victimId = id;
                }
            }
            if (victimId is null) break; // 全被保护：宁可超容，不误杀热备
            var victim = _sessions[victimId];
            _sessions.Remove(victimId);
            _lastUsed.Remove(victimId);
            _supersededAtMs.Remove(victimId);
            try { victim.ReleaseKernel(); }
            catch { /* 淘汰释放失败不拖垮会话表：内核随进程回收 */ }
        }
    }

    /// <summary>会话让位（老板令 #21 超龄账）：按单调钟记让位时刻，超龄清理据此判账。
    /// 无频道会话（CurrentChannel 为空，未取得归属台）无账可记，空操作合理（vc73 审查清零核验定案）。</summary>
    private void MarkSuperseded(PlaybackSession session)
    {
        if (session.CurrentChannel is { } ch) _supersededAtMs[ch.Id] = _monotonicMs();
    }

    private void WireSession(PlaybackSession session)
    {
        session.FallbackSourceProvider = _fallbackProvider;
        session.QueueOrderer = _queueOrderer; // #74 健康排序：后建会话同挂
        session.QualityBook = QualityBook;    // 实测账（vc85 两本账之二）：微卡/盲区/实测成功记账随会话走
        session.HealthLine = HealthLine;      // 链路健康线（vc85 两增强①）：坏帧喂账随会话走
        session.SoftLandingEnabled = Arbiter is not null; // 仲裁器接上=预防硬切退役走软着陆（蓝本 v2.5）
        session.Failover += (s, _) =>
        {
            // 回主源计时账（方案书 v12 单调钟）：每次断流自救切换重新起窗（回后若再断重新计时）
            if (ReferenceEquals(s, Current))
            {
                lock (_returnGate)
                {
                    _returnStableSession = null; // 先归 Idle/Buffering，等 Playing 再起窗
                    _returnStableSinceMs = _monotonicMs();
                }
            }
            // 升档失败入逃生口（vc85 ④）：对齐切回主源后 UpgradeFailWindow 内再断=升档失败，
            // 窗内满 3 次→主源休眠 60 分钟（与升档无关的断流由仲裁器账内自辨不冤枉）
            if (Arbiter is { } arb && _alignEpisodeAtMs is { } started
                && _monotonicMs() - started <= SwitchArbiter.UpgradeFailWindow.TotalMilliseconds)
            {
                arb.ReportOutcome(false);
                _alignEpisodeAtMs = null;
            }
        };
        session.AlignedSwitched += (_, reason) =>
        {
            // 对齐切落点记账（vc85 蓝本三原语）：升往 0 路的才入逃生口结果账（降档软着陆不计）
            if (ReferenceEquals(session, Current) && session.CurrentLineIndex == 0)
            {
                _alignEpisodeAtMs = _monotonicMs();
            }
            SessionTrace.Emit($"[对齐切] {session.CurrentChannel?.Name ?? "?"} 对齐切换完成（{reason}，现线路 {session.CurrentLineIndex + 1}/{session.LineCount}）");
        };
        session.StateChanged += (_, state) =>
        {
            TrackReturnStability(session, state);
            if (state == PlaybackState.Playing && session.CurrentChannel is { } playingChannel)
            {
                _retryBackoff.Clear(playingChannel.Id); // 播放成功起播即清退避账（共账不共判据：换源断路器另有判据）
                // Begin 会话取得频道即入表（vc73 审查清零 P2，孤儿内核封死）：Begin 是公开 API，
                // 壳层未用≠无人用——不入表的话 Current 一旦易主，旧会话既无人停也无 LRU/TTL 兜底，
                // 内核白烧到进程退出。TryAdd 语义：热备/切台路径已显式登记的会话不覆盖、不动账。
                if (_sessions.TryAdd(playingChannel.Id, session))
                {
                    _lastUsed[playingChannel.Id] = _stamp++;
                    EvictIfOverCapacity();
                }
            }
            _dispatcher.Post(() => SessionStateChanged?.Invoke(this, new SessionStateEventArgs(session, state)));
        };
    }

    /// <summary>回主源稳定窗：当前台切回 Playing 起窗，离开 Playing（断流/换台）即重新计时。</summary>
    private void TrackReturnStability(PlaybackSession session, PlaybackState state)
    {
        lock (_returnGate)
        {
            if (state == PlaybackState.Playing
                && ReferenceEquals(session, Current)
                && session.CurrentLineIndex > 0)
            {
                _returnStableSession = session;
                _returnStableSinceMs = _monotonicMs();
            }
            else if (_returnStableSession is { } armed
                     && (state != PlaybackState.Playing || !ReferenceEquals(session, Current)))
            {
                _returnStableSession = null;
            }
        }
    }

    private void RaiseCurrentChanged(PlaybackSession session) =>
        _dispatcher.Post(() => CurrentChanged?.Invoke(this, session));
}

/// <summary>会话状态变化事件参数：携带会话与迁移后的状态。</summary>
public sealed class SessionStateEventArgs(PlaybackSession session, PlaybackState state) : EventArgs
{
    public PlaybackSession Session { get; } = session;
    public PlaybackState State { get; } = state;
}
