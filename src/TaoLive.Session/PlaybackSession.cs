using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 单个播放会话：一个频道一个会话，会话内一个状态机，
/// 状态只能经 Transition 改变，外部只读不写（封装口径，tao-code-standard 〇·八.1）。
/// 时钟注入可测（Tests 快拍不真等）。
/// 换台顺序（契约，面账归一 2026-11-16 修订）：Stop 旧源 → 状态归 Idle → 选新源 → 起播 → 重挂 Surface。
/// 面账归一：换台/换线/自救不再摘面（真面跨重起播保持挂接），摘面仅壳层纹理销毁/让位/退场调用。
/// 断流自救：内核 OnError 或起播超时 → 依频道备源列表顺序切下一路重起播，不崩；
/// 全部备源耗尽则停在 Failed 并发 AllSourcesFailed 事件，由上层决定重试或放弃。
/// 被换下的会话（Supersede）不再响应内核事件，避免共享内核时旧会话抢自救。
/// </summary>
public sealed class PlaybackSession
{
    private readonly IPlayer _player;
    private readonly IClock _clock;
    private PlaybackState _state = PlaybackState.Idle;
    private List<SourceEndpoint> _queue = new();
    private int _endpointIndex = -1;
    private Channel? _channel;
    private IntPtr _surfaceHandle;
    private bool _hasSurface;
    private bool _active = true;
    private bool _preferLowLatency;
    // 单调计时账（方案书 v12 铁律，四镜·卡顿镜 11-15 修）：open/stall 看门狗判定一律按单调钟，
    // 墙钟（StartedAtUtc 等）仅留展示与台账，开机校时跳变不再误触/漏触看门狗。
    private long? _bufferingSinceMs;
    private long? _reconnectingSinceMs;
    // vc67 进度感知看门狗账：起播/重灌期按内核 BufferedMs 读数是否增长区分「死源」与「慢灌源」——
    // 死源零增长快切（#74 的 3 秒保留）；慢灌源（缓冲在涨但到不了门槛）给满放宽窗，不烧源队列。
    // vc66 真机实锤：3 秒窗 < 5 秒首帧门槛，慢网永远到不了 READY 就被切，5 条线 15 秒烧光→全线路不可达。
    private long _watchdogLastBufferedMs;
    private long? _lastBufferProgressAtMs;
    // 预防性换源账（老板令 #24）：库存跌破线连续两拍（间隔≥1 秒）即主动切备线，不等卡流 15 秒。
    // 单调钟记账；0 读数按「无读数」不判趋势（IPlayer 契约），冷却窗防横跳。
    private int _lowInventoryRounds;
    private long _lowInventoryLastAtMs;
    private long? _lastFailoverAtMs; // 可空：无账=从未切过，不占冷却窗（初值 0 会把开机头 60 秒全锁死）
    // vc67 预防介入上限：慢网盒子（带宽<流码率）库存常态低位，预防切换每冷却窗烧一条线，
    // 几分钟烧光全队列→「出音一会又信号中断，全线路不可达」。上限内坏线照切；到顶后预防退场，
    // 真卡流交看门狗（进度感知窗）与内核报错自救——画面持续低库存抖动也好过切到全线路不可达。
    private int _proactiveCount;
    public const int ProactiveCapPerChannel = 3;
    // vc68 预防误烧闸（老板真机报「出画一帧马上卡死→黑屏断连」实锤）：首帧门槛降到 0.5s 后，
    // 起播头几秒库存读数必然低于 7 秒预防线（0.5s 出画时内核几乎没存货）——预防换源若不看账
    // 立即点火，冷却窗内连烧 3 条线出「全线路不可达」。改为：本线路库存先攒满过线一次才许
    // 按库存线预防介入（说明线是好的、刚才是瞬时欠载）；从没攒满过=线本来就灌不动，宁稳勿烧，
    // 真卡流交看门狗（进度感知窗）。卡流次数判据（#26）不受限。
    private bool _inventoryEverFilled;
    // vc69 坏票不自焚账（真机 2026-11-16 00:08 实锤：缓存过期票 URL 4xx → 4 条线数秒烧光 → 全线路不可达）：
    // 本线路已否用过「作废坏票原地换新票」的一次机会（每线路一次，防死循环）；_ticketRetrySinceMs=重试宽限窗起点。
    private bool _freshTicketRetried;
    private long? _ticketRetrySinceMs;

    /// <summary>坏票重试宽限窗（vc69）：零增长判死账在此窗内让路——换新票全程=取票 HttpClient 超时
    /// 最长 5 秒 + 内核连接超时 8 秒 + 余量 3 秒=16 秒（期满了还在零增长才是真死，照常判死切线）。</summary>
    public static readonly TimeSpan TicketRetryGrace = TimeSpan.FromSeconds(16);

    /// <summary>
    /// vc69 坏票不自焚：会话层在报 HTTP 状态错/取流失败前可先作废该端点换票缓存
    /// （参数：频道 Id（缓存键）、坏票端点）。壳层接 TicketResolver.Invalidate；未接线为空操作（不启用重试）。
    /// </summary>
    public static Action<string, SourceEndpoint>? TicketCacheInvalidator { get; set; }

    /// <summary>
    /// vc69 让道修·前台标记：后台车道门控（BackgroundLane 首帧门控/播放降速档）只认前台会话——
    /// 由管理器在切台/让位时维护。热备攒流与让位会话的状态迁移不得再翻动全局门（真机 00:07-00:09 实锤：
    /// 7 个后台会话翻状态把前台起播期的零后台网络门重新打开，预检探针每秒滚进首帧窗口抢道）。
    /// </summary>
    public bool IsForeground { get; set; }

    public PlaybackSession(IPlayer player, IClock clock)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        // 内核回调守卫（闪退镜黄⑤ 11-15）：矩阵外跳转等异常不掀回调链崩进程，落 KernelCallbackFailed 交壳层记台账
        _player.OnError += (_, e) => GuardKernel(() => OnPlayerError(e.Code));
        _player.OnFirstFrame += (_, _) => GuardKernel(NotifyFirstFrame);
        // 卡流自救接线（方案书 v99 四镜·卡顿镜 11-15 修）：内核缓冲事件接会话，播放中卡流进 Reconnecting 走看门狗。
        // NotifyStalled 内部有 Playing 态守卫，起播期缓冲不误触发。
        _player.OnBufferingStart += (_, _) => GuardKernel(NotifyStalled);
        // 恢复沿接线（真机反馈批#5 修 2026-11-15）：内核 Ready 发 OnBufferingEnd=画面恢复出帧（契约文档明载
        // 「断流自救期间会成对反复出现」），Reconnecting 态收即回 Playing——此前矩阵无恢复沿，短卡流后即使
        // 自行恢复也会被 15 秒看门狗误杀切备源（真机「断流后一直黑屏」归因实锤）。
        _player.OnBufferingEnd += (_, _) => GuardKernel(NotifyRecovered);
    }

    /// <summary>内核回调链异常出口（闪退镜黄⑤）：壳层接 CrashNet 落运行台账。</summary>
    public static event EventHandler<string>? KernelCallbackFailed;

    private void GuardKernel(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            KernelCallbackFailed?.Invoke(this, $"{CurrentChannel?.Name ?? CurrentChannel?.Id ?? "未知频道"}: {ex.Message}");
        }
    }

    /// <summary>起播超时（看门狗阈值）：缓冲超过此时长即视为断流，走备源切换。
    /// #74 切台提速（2026-11-15 老板定值）：15 秒→3 秒——死源撞满钟是切台黑屏 8-10 秒主账，快切快走。
    /// vc68 三轮（2026-11-16 真机实锤 3004ms≥3000ms·0ms 存货·四线全灭）：判死窗改由重入门槛推导
    /// （StartupThreshold=12 秒）——本窗必须同时盖过两个账：
    /// ①内核连接超时：Media3 默认连接超时 8 秒，窗≤8 秒时内核的报错快刀永远没机会先出手，全被 3 秒误杀
    /// （vc66/vc67/vc68 三次真机同款死局，台账在案：判死窗必须>门槛，慢热源要有证明时间）；
    /// ②冷连接全程：解析+连服+握手+取清单+取首段，老盒子实测可到 13-16 秒出画基线（vc66 实锤）。
    /// 取 12 秒：内核报错快刀（8 秒）能先出手、冷连接有证明时间、仍&lt;慢灌窗（21 秒）不与 #74 提速意图对着干；
    /// 真死快刀不走此账——内核报错信号直判不变。</summary>
    public static TimeSpan DefaultOpenTimeout { get; } = PrefetchCurve.StartupThreshold;

    /// <summary>本会话的起播超时阈值，测试可调。</summary>
    public TimeSpan OpenTimeout { get; set; } = DefaultOpenTimeout;

    public PlaybackState State => _state;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    /// <summary>当前会话所属频道；从未起播过则为空。</summary>
    public Channel? CurrentChannel => _channel;

    /// <summary>当前正在拉取的源端点；未起播为空。</summary>
    public SourceEndpoint? CurrentEndpoint =>
        _endpointIndex >= 0 && _endpointIndex < _queue.Count ? _queue[_endpointIndex] : null;

    /// <summary>本频道线路数（手动换线路用，2.0 W8）；未起播为 0。</summary>
    public int LineCount => _queue.Count;

    /// <summary>当前线路序号（0 基）；未起播为 -1。</summary>
    public int CurrentLineIndex => _endpointIndex;

    /// <summary>本会话累计断流自救切换次数；换台归零。</summary>
    public int FailoverCount { get; private set; }

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：建队列时在本台源之后追加该供给给出的备援候选
    /// （URL 去重，本台已有同址不重复入队）；空=不吃内置表（既有契约不变，测试可注空）。
    /// </summary>
    public Func<Channel, IReadOnlyList<SourceEndpoint>>? FallbackSourceProvider { get; set; }

    /// <summary>#74 源队列健康排序（2026-11-15 老板令）：建队（含备源追加后）整体重排——本台源死账时
    /// 死源沉底、健康候选补位。壳层接健康账（账空=原表序不猜）；未设=按原表序（既有契约与测试不变）。</summary>
    public Func<Channel, IEnumerable<SourceEndpoint>, IEnumerable<SourceEndpoint>>? QueueOrderer { get; set; }

    /// <summary>当前输出面句柄（管理器热切换移交用）；未接面为 IntPtr.Zero。</summary>
    public IntPtr SurfaceHandle => _surfaceHandle;

    /// <summary>状态迁移完成事件（迁移成功后触发，含换台与自救引发的迁移）。</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>断流自救切换事件：From 为切出源，To 为切入源，Attempt 从 1 计。</summary>
    public event EventHandler<FailoverEventArgs>? Failover;

    /// <summary>备源耗尽：本频道全部源都试败，会话停在 Failed。</summary>
    public event EventHandler<AllSourcesFailedEventArgs>? AllSourcesFailed;

    /// <summary>受控迁移：仅允许状态机矩阵内的跳转，非法跳转抛异常不静默。</summary>
    public void Transition(PlaybackState next, string? reason = null)
    {
        bool legal = (_state, next) switch
        {
            (PlaybackState.Idle, PlaybackState.Buffering) => true,
            (PlaybackState.Idle, PlaybackState.Failed) => true,
            (PlaybackState.Buffering, PlaybackState.Playing) => true,
            (PlaybackState.Buffering, PlaybackState.Reconnecting) => true,
            (PlaybackState.Buffering, PlaybackState.Failed) => true,
            (PlaybackState.Buffering, PlaybackState.Idle) => true,
            (PlaybackState.Playing, PlaybackState.Reconnecting) => true,
            (PlaybackState.Playing, PlaybackState.Failed) => true,
            (PlaybackState.Playing, PlaybackState.Idle) => true,
            (PlaybackState.Reconnecting, PlaybackState.Buffering) => true,
            (PlaybackState.Reconnecting, PlaybackState.Playing) => true,
            (PlaybackState.Reconnecting, PlaybackState.Failed) => true,
            // BUG-1（测试分身 2026-11-15 报告）：卡流窗口换台必经 Stop→Idle，矩阵补齐合法迁移
            (PlaybackState.Reconnecting, PlaybackState.Idle) => true,
            (PlaybackState.Failed, PlaybackState.Buffering) => true,
            (PlaybackState.Failed, PlaybackState.Idle) => true,
            _ => false,
        };
        if (!legal)
        {
            throw new InvalidOperationException(
                $"Illegal playback transition {_state} -> {next} (reason: {reason ?? "n/a"})");
        }
        if (next == PlaybackState.Buffering && (_state == PlaybackState.Idle || _state == PlaybackState.Failed))
        {
            // 起播计时刻：首播与失败重试都算新一轮，StartedAtUtc 以本次缓冲起点为准（墙钟仅展示）。
            StartedAtUtc = _clock.UtcNow;
            _bufferingSinceMs = _clock.NowMs;
        }
        if (next == PlaybackState.Buffering || next == PlaybackState.Reconnecting)
        {
            // vc67：进起播/重灌期即清进度账——新源从头采样，死源快切口径不被上一源的陈账污染
            _watchdogLastBufferedMs = 0;
            _lastBufferProgressAtMs = null;
        }
        if (next == PlaybackState.Playing && _state != PlaybackState.Playing)
        {
            // vc68 预防误烧闸：进播放/恢复播放，本线路攒满账重记——新源从头攒，旧账不背锅
            _inventoryEverFilled = false;
        }
        if (next != PlaybackState.Reconnecting)
        {
            _reconnectingSinceMs = null;
        }
        // vc69 让道修（真机 00:07-00:09 实锤，老板镜「是不是没给当前播放让道」=对）：
        // 车道门控此前被每个会话的迁移写同一面全局旗——热备/让位会话一翻状态就把前台起播期的
        // 「零后台网络」门重新打开，预检探针每秒滚进首帧窗口抢道。改口径：门控只认前台会话，
        // 非前台会话（热备攒流/让位）一律不碰门。前台语义照车道令原设计：
        // 起播/换台/自救过渡期（Buffering/Reconnecting）静默零后台网络；首帧落地（Playing）恢复放行；
        // 失败/空闲（Failed/Idle，无画面在播）回空闲档放行——断网期预检照跑，恢复沿才回得来。
        if (IsForeground)
        {
            if (next == PlaybackState.Buffering || next == PlaybackState.Reconnecting)
                BackgroundLane.SetFirstFrameLanded(false);
            else
                BackgroundLane.SetFirstFrameLanded(true);
            BackgroundLane.SetPlaying(next == PlaybackState.Playing);
        }
        if (next == PlaybackState.Playing)
        {
            // vc69 坏票重试收账：换新票成功出画，宽限窗与每线路重试机会一并复位（后续卡流另有预算）
            _ticketRetrySinceMs = null;
            _freshTicketRetried = false;
            // vc82 自愈收账：画面恢复即全愈，退避账清零（下次全断从第一档重新起算）
            _selfHealDueAtMs = null;
            _selfHealAttempts = 0;
        }
        var fromState = _state;
        _state = next;
        // 起播打点（真机反馈批·埋点补充 2026-11-15）：起播/换台/换源指令到首帧的耗时账——
        // 覆盖正常换台与备源自救两条路径（换源后路径带换源次数），是白屏/换台慢排查的第一账；
        // 热备攒流会话同样落账（攒流到门槛线的真实时长可查）。
        if (next == PlaybackState.Playing && fromState == PlaybackState.Buffering
            && _bufferingSinceMs is { } startedMs)
        {
            var elapsedMs = _clock.NowMs - startedMs;
            SessionTrace.Emit($"[起播] {DescribeChannel()} 首帧 {elapsedMs}ms"
                + $"（线路 {CurrentLineIndex + 1}/{LineCount}，第 {FailoverCount} 次换源后）");
        }
        StateChanged?.Invoke(this, next);
    }

    /// <summary>起播入口：Idle/Failed 均可发起新一轮播放。</summary>
    public void Start(PlaybackParams p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (_state != PlaybackState.Idle && _state != PlaybackState.Failed)
        {
            throw new InvalidOperationException($"Start only legal from Idle/Failed, got {_state}");
        }
        _active = true;
        _channel = p.Channel;
        _queue = p.Channel.Sources.Where(s => s.IsValid).ToList();
        AppendFallbacks();
        if (QueueOrderer is { } orderer2) _queue = orderer2(p.Channel, _queue).ToList(); // #74 健康排序：备源入队后整体重排
        _endpointIndex = _queue.FindIndex(s => s.Url == p.Endpoint.Url);
        if (_endpointIndex < 0)
        {
            // 未入队端点（闪退镜黄④ 11-15）：实际播的就是这个端点，插入队列首位=当前线——
            // 此前只记账归 0，CurrentEndpoint/CurrentLineIndex 与真实播放流错位（vc73 审查清零 P2）。
            _queue.Insert(0, p.Endpoint);
            _endpointIndex = 0;
        }
        _preferLowLatency = p.PreferLowLatency;
        _proactiveCount = 0; // vc67：新一轮起播（含 OK 重试）重给预防介入预算
        _stallTimes.Clear(); // vc73 审查清零 P2：新一轮起播重新起账（含 OK 重试），同线重试不背旧疑点账——与换台/换线/自救清账同口径
        _smoothnessTimes.Clear();
        _freshTicketRetried = false; // vc69：新一轮起播重记坏票重试机会
        _ticketRetrySinceMs = null;
        Transition(PlaybackState.Buffering, "start requested");
        _player.Play(p);
        ReattachSurface();
    }

    /// <summary>
    /// 换台：一个频道一个会话的入口。顺序按类注释契约（面账归一 2026-11-16：换台不再摘面，
    /// 真面跨换台保持挂接——摘面会让重挂判空走哑面，自动换源后画面永久冻帧）；
    /// 频道无任何合法源则抛参数异常，会话状态不变。
    /// </summary>
    public void ChangeChannel(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.IsValid)
        {
            throw new ArgumentException($"Channel {channel.Id} has no valid sources", nameof(channel));
        }
        if (_state is not PlaybackState.Idle)
        {
            _player.Stop(); // vc68：含 Failed 态一律停（同 Supersede 口径），旧台音画必死
            Transition(PlaybackState.Idle, "channel switch");
        }
        _active = true;
        _channel = channel;
        _selfHealDueAtMs = null; // vc82：用户主动换台即接管，自愈慢轮收摊，不背着旧台的全断账
        _selfHealAttempts = 0;
        _autoFailoverTimes.Clear(); // 限速账同口径：换台重开一轮
        _queue = channel.Sources.Where(s => s.IsValid).ToList();
        if (_queue.Count == 0)
        {
            // 全端点无效（IsValid 只查端点数量不查有效性，闪退镜红③ 11-15）：判失败终态不越界，
            // 交壳层失败终态空态文案与重试路径；不抛索引越界崩主线程。
            Transition(PlaybackState.Failed, "no valid sources");
            return;
        }
        AppendFallbacks();
        if (QueueOrderer is { } orderer) _queue = orderer(channel, _queue).ToList(); // #74 健康排序：备源入队后整体重排，死源沉底
        _endpointIndex = 0;
        FailoverCount = 0;
        _freshTicketRetried = false; // vc69：换台重记坏票重试机会
        _ticketRetrySinceMs = null;
        _proactiveCount = 0; // vc67：换台重给预防介入预算
        _stallTimes.Clear(); // 欠载趋势账随换台清零（老板令 #26）：账只记本台画质，不跟人走
        _smoothnessTimes.Clear(); // vc70：平稳疑点账同口径，换台清零不跟人走
        Transition(PlaybackState.Buffering, "change channel");
        _player.Play(new PlaybackParams(channel, _queue[0], _preferLowLatency));
        ReattachSurface();
    }

    /// <summary>
    /// 手动换线路（2.0 W8）：切到本频道指定序号的源端点（0 基），顺序契约与换台一致：
    /// 停旧流 → 状态归 Idle → 起播新线路 → 重挂 Surface。
    /// 播放中选同一路为无害操作；未起播先抛异常（先有台才有线路）。
    /// </summary>
    public void SwitchLine(int endpointIndex)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("SwitchLine requires a started channel");
        }
        if (endpointIndex < 0 || endpointIndex >= _queue.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointIndex));
        }
        if (endpointIndex == _endpointIndex && _state == PlaybackState.Playing)
        {
            return; // 同线无操作：不打断在播画面
        }
        if (_state is not PlaybackState.Idle)
        {
            _player.Stop(); // vc68：含 Failed 态一律停（同 Supersede 口径），旧台音画必死
            Transition(PlaybackState.Idle, "line switch");
        }
        _endpointIndex = endpointIndex;
        FailoverCount = 0;
        _freshTicketRetried = false; // vc69：手动换线重记坏票重试机会
        _ticketRetrySinceMs = null;
        _proactiveCount = 0; // vc67：手动换线=老板意图，重给预防介入预算
        _stallTimes.Clear(); // 手动换线：欠载账随线清零
        _smoothnessTimes.Clear(); // vc70：平稳账随线清零
        Transition(PlaybackState.Buffering, "manual line switch");
        _player.Play(new PlaybackParams(_channel, _queue[endpointIndex], _preferLowLatency));
        ReattachSurface();
    }

    /// <summary>接输出面：会话记住句柄，换台/自救重起播后自动重挂。契约见 IPlayer。</summary>
    public void AttachSurface(IntPtr surfaceHandle)
    {
        _surfaceHandle = surfaceHandle;
        _hasSurface = true;
        _player.AttachSurface(surfaceHandle);
    }

    /// <summary>摘输出面（面账归一 2026-11-16）：仅壳层纹理销毁/让位/退场路径调用——
    /// 换台/换线/自救不再摘面（真面热切不闪黑）。清账无条件执行（重复调用无害），
    /// 清后 ReattachSurface 不再重挂，防死面复活。
    /// clearHandle 仅壳层纹理销毁传 true（默认）：句柄随真面销毁归零；
    /// 让位（Supersede）摘面传 false 保留句柄——热切接管要按句柄把真面移交热备，不能清。</summary>
    public void DetachSurface(bool clearHandle = true)
    {
        if (_hasSurface)
        {
            _player.DetachSurface();
        }
        _hasSurface = false;
        if (clearHandle)
        {
            _surfaceHandle = IntPtr.Zero;
        }
    }

    /// <summary>首帧落地：Buffering 到 Playing 的唯一正门。</summary>
    public void NotifyFirstFrame()
    {
        if (_state == PlaybackState.Buffering)
        {
            Transition(PlaybackState.Playing, "first frame");
        }
    }

    /// <summary>静音攒流（vc68 漏音根因①修复，热备专用）：暂停出声出画，内核照常缓冲攒流——
    /// 此前热备起播 PlayWhenReady=true，备台攒流期间该台声音直接外放（「切台了还在出上个台的音」）。
    /// Play 后即 Pause：READY 后解码器仍初始化、哑面预热不失效，热切门槛（State==Playing）不破坏。</summary>
    public void PausePlayback() => _player.Pause();

    /// <summary>恢复出声出画（vc68）：热备热切接管时调用，缓冲攒流不中断。</summary>
    public void ResumePlayback() => _player.Resume();

    /// <summary>当前会话内核（vc66：壳层帧账归本会话用——热备攒流哑面解码的帧不再替当前台续零帧账）。</summary>
    public IPlayer Kernel => _player;

    /// <summary>壳层只出声自救终段（vc66）：线路走完仍零帧，强制转 Failed 走换源队列——
    /// 备源耗尽即全线路不可达终态，壳层空态接管；Playing/Reconnecting 之外的调用无害。</summary>
    public void ForceFailover(string reason)
    {
        if (!_active) return;
        if (_state is not (PlaybackState.Playing or PlaybackState.Reconnecting)) return;
        Transition(PlaybackState.Failed, reason);
        TryFailover(reason);
    }

    /// <summary>播放中卡流：进入换台中间态，等待看门狗决策（计时按单调钟）。</summary>
    public void NotifyStalled()
    {
        if (_state == PlaybackState.Playing)
        {
            Transition(PlaybackState.Reconnecting, "watchdog stall");
            _reconnectingSinceMs = _clock.NowMs;
            RecordStall(_clock.NowMs); // 欠载趋势账（老板令 #26）：反复短卡流也是主动介入依据
        }
    }

    /// <summary>持续欠载判据（老板令 #26）：滚动窗内短卡流满此次数即预防性换线，
    /// 不等下一次卡死 15 秒——库存读数看「这一刻还剩多少货」，欠载账看「这阵子饿了几次」，两判据互补。</summary>
    public const int StallChurnThreshold = 3;

    /// <summary>持续欠载滚动窗（毫秒）：窗内卡流满 3 次即触发，超窗自动滑出。</summary>
    public static readonly TimeSpan StallChurnWindow = TimeSpan.FromMinutes(5);

    private readonly List<long> _stallTimes = new();

    private void RecordStall(long nowMs)
    {
        _stallTimes.RemoveAll(t => nowMs - t >= StallChurnWindow.TotalMilliseconds);
        _stallTimes.Add(nowMs);
    }

    /// <summary>慢帧流滚动窗（vc70 老板真机「画面不停地抖」台账定案）：窗内平稳疑点满此次数即坏线证据。
    /// 台账三张定案：换源/重建/带宽/解码丢帧四路全排除（全程零换源行、零重建行、估速 6.7~14.6 Mbps 富余、
    /// 零解码报错/丢帧/欠载行），实锤=出画帧间隔 P95 锁死 478~489ms（≈2 帧/秒）连续 2 分多钟不断——
    /// 画面每半秒才更新一次，帧在上游被节流；探活通、库存足、带宽够，既有自救全不触发，须单独判据。</summary>
    public static readonly TimeSpan SmoothnessSuspectWindow = TimeSpan.FromSeconds(90);

    /// <summary>慢帧流判线阈值：平稳疑点（连续 3 窗超阈的怀疑游程，壳层每 10 秒上报一次）满 3 次
    /// =持续约 1 分钟坏帧坐实，预防性换线。同样吃预防冷却窗与介入封顶，防坏线连切横跳。</summary>
    public const int SmoothnessSuspectThreshold = 3;

    private readonly List<long> _smoothnessTimes = new();

    /// <summary>壳层平稳观测上报（vc70）：画面平稳疑点游程成立时每拍记账，滚动窗内攒满即交预防泵换线。
    /// 只记账不决策（画面平稳优先令：计算归会话预防泵，壳层只埋点）。</summary>
    public void NotifySmoothnessSuspect()
    {
        var nowMs = _clock.NowMs;
        _smoothnessTimes.RemoveAll(t => nowMs - t >= SmoothnessSuspectWindow.TotalMilliseconds);
        _smoothnessTimes.Add(nowMs);
    }

    /// <summary>
    /// 断流自愈（真机反馈批#5 修 2026-11-15）：Reconnecting 中内核恢复出帧（OnBufferingEnd）即回 Playing，
    /// 看门狗计时账随之清零——好画面不因历史卡流被误杀。Buffering 态（起播期）收 End 走 NotifyFirstFrame 正门，不进此路。
    /// </summary>
    private void NotifyRecovered()
    {
        if (_state == PlaybackState.Reconnecting)
        {
            Transition(PlaybackState.Playing, "stream recovered");
        }
    }

    /// <summary>
    /// 看门狗泵：缓冲超时或卡流超时即断流自救。返回是否触发了迁移。
    /// 生产由壳层定时器或管理器 Tick 驱动，测试注入 FixedClock 后手动泵。判定一律按单调钟。
    /// vc67 进度感知：死源（内核 BufferedMs 零增长）按 #74 的 3 秒快切不动；
    /// 慢灌源（读数在涨、到不了门槛）放宽到首帧门槛×3（起播）/重入门槛×2（重灌）——
    /// vc66 真机实锤：3 秒窗 < 5 秒首帧门槛是数学死局，慢网烧光源队列出「全线路不可达」。
    /// </summary>
    public bool CheckWatchdog()
    {
        long nowMs = _clock.NowMs;
        SampleBufferProgress(nowMs);
        // vc82 第三刀「全断不装死」（老板真机实锤：半分钟整个信号中断挂起，装死等按键）：
        // Failed 态且自愈慢轮到点——退避节奏自动重试（从头慢轮整条队列），不静默挂起。
        if (_state == PlaybackState.Failed
            && _selfHealDueAtMs is { } due && nowMs >= due)
        {
            if (_selfHealAttempts >= SelfHealMaxAttempts)
            {
                // 满额收摊（vc82 口径不变：保持空态等按键，不无限烧）→ 改接「自救重探间隔」旋钮
                // （老板 2026-11-17 拍板，假旋钮接通第 2 位）：满额后按重探间隔周期性轻试一次探活，
                // 缓过来就自动复活；比空态死等快，比重试烧线省。重试账不重开，探活复用既有播放重试路径。
                // 注意：重试账已满，此处轻试不清账不重开，探活成功（内核回报 Playing/错误回调触发换线）按既有路径走。
                _selfHealDueAtMs = nowMs + (long)SelfHealReprobeInterval.TotalMilliseconds;
                _selfHealAttempts = SelfHealMaxAttempts; // 账面钉住满额：轻试不消耗也不重置重试次数
                if (_channel is { } probeChannel && _queue.Count > 0)
                {
                    // 轻试探活：只重放队首一拍（既有播放重试路径），不重开限速账不重记坏票账
                    Transition(PlaybackState.Buffering, "self-heal reprobe");
                    _player.Play(new PlaybackParams(probeChannel, _queue[0], _preferLowLatency));
                    ReattachSurface();
                }
                return true;
            }
            _selfHealAttempts++;
            var nextDelay = SelfHealDelay(_selfHealAttempts);
            _selfHealDueAtMs = nowMs + (long)nextDelay.TotalMilliseconds;
            SessionTrace.Emit($"[自愈] {DescribeChannel()} 全断退避重试第 {_selfHealAttempts}/{SelfHealMaxAttempts} 次（下一档 {nextDelay.TotalSeconds:0}s 节奏），从头慢轮");
            _stallTimes.Clear();
            _smoothnessTimes.Clear();
            _lightErrorTimes.Clear();
            _autoFailoverTimes.Clear(); // 自愈慢轮重开一轮限速账，慢试不受旧窗拖累
            _endpointIndex = 0; // 从头慢轮：整条队列重试，源头可能已缓过来
            _freshTicketRetried = false; // 坏票重试机会重记
            _ticketRetrySinceMs = null;
            Transition(PlaybackState.Buffering, $"self-heal retry #{_selfHealAttempts}");
            if (_channel is { } healChannel && _queue.Count > 0)
            {
                _player.Play(new PlaybackParams(healChannel, _queue[0], _preferLowLatency));
                ReattachSurface();
            }
            return true;
        }
        if (_state == PlaybackState.Buffering
            && _bufferingSinceMs is { } started)
        {
            var windowMs = WatchdogWindowMs(nowMs, SlowStartWindow, started);
            if (nowMs - started >= windowMs)
            {
                // 看门狗打点（埋点补充 2026-11-15）：此前看门狗触发完全静默，真机只能看到「换源」结果看不到因——
                // 补触发原因+已等时长，断流自救链路（含误杀嫌疑对账）可查。vc67：带上进度口径（快切/慢灌让路）。
                SessionTrace.Emit($"[看门狗] {DescribeChannel()} 起播超时 {nowMs - started}ms≥{windowMs}ms，转备源自救（原线路 {CurrentLineIndex + 1}/{LineCount}，{_watchdogLastBufferedMs}ms 存货）");
                Transition(PlaybackState.Failed, "open timeout");
                TryFailover("open timeout");
                return true;
            }
        }
        if (_state == PlaybackState.Reconnecting
            && _reconnectingSinceMs is { } stalled)
        {
            var windowMs = WatchdogWindowMs(nowMs, SlowRecoveryWindow, stalled);
            if (nowMs - stalled >= windowMs)
            {
                SessionTrace.Emit($"[看门狗] {DescribeChannel()} 卡流超时 {nowMs - stalled}ms≥{windowMs}ms，转备源自救（原线路 {CurrentLineIndex + 1}/{LineCount}，{_watchdogLastBufferedMs}ms 存货）");
                Transition(PlaybackState.Failed, "stall timeout");
                TryFailover("stall timeout");
                return true;
            }
        }
        return false;
    }

    /// <summary>vc67 慢灌放宽窗（起播）：首帧门槛×7（3×7=21 秒；vc68 三轮改推导式——门槛再定标自动跟随）。
    /// ×7 定标依据：vc66 真机出画基线 13-16 秒，×5=15 秒盖不住 16 秒尾巴（差 1 秒误杀最慢盒子），
    /// ×7=21 秒盖住基线留 5 秒余量，仍&lt;慢恢复窗 24 秒，三窗次序不打架。
    /// 口径：慢灌源带宽 ≥1/3 流码率即灌得到。</summary>
    public static readonly TimeSpan SlowStartWindow =
        TimeSpan.FromMilliseconds(PrefetchCurve.FirstFrameGate.TotalMilliseconds * 7);

    /// <summary>vc67 慢灌放宽窗（重灌）：卡后重入门槛 12s×2=24 秒——重灌要满 12 秒媒体，带宽 ≥1/2 流码率即灌得到。</summary>
    public static readonly TimeSpan SlowRecoveryWindow =
        TimeSpan.FromMilliseconds(PrefetchCurve.StartupThreshold.TotalMilliseconds * 2);

    /// <summary>进度采样（vc67）：内核 BufferedMs 读数有增长即记时刻——3 秒内有增长算「慢灌在走」。</summary>
    private void SampleBufferProgress(long nowMs)
    {
        long buffered = _player.BufferedMs;
        if (buffered > _watchdogLastBufferedMs)
        {
            _watchdogLastBufferedMs = buffered;
            _lastBufferProgressAtMs = nowMs;
        }
    }

    /// <summary>vc67 看门狗窗：3 秒内有缓冲增长→放宽窗（慢灌让路）；零增长→判死窗（vc68 三轮改 12 秒推导值）。
    /// vc69 坏票宽限：换新票期间（取票网络耗时最长 5 秒+余量）宽限窗内不判死，期满恢复原窗。</summary>
    private long WatchdogWindowMs(long nowMs, TimeSpan slowWindow, long startedMs)
    {
        bool flowing = _lastBufferProgressAtMs is { } t && nowMs - t < 3_000;
        long windowMs = (long)Math.Max(OpenTimeout.TotalMilliseconds, flowing ? slowWindow.TotalMilliseconds : 0);
        if (_ticketRetrySinceMs is { } t0)
        {
            long graceEnd = t0 + (long)TicketRetryGrace.TotalMilliseconds;
            if (nowMs < graceEnd)
            {
                windowMs = Math.Max(windowMs, graceEnd - startedMs); // 宽限终点前看门狗让路
            }
        }
        return windowMs;
    }

    /// <summary>预防性换源库存线（老板令 #24）：Playing 期库存跌破起播下限连续两拍即主动切备线——
    /// 卡顿前动手，不等卡流 15 秒看门狗。终值随真机定标。</summary>
    /// <summary>触发线随落后旋钮联动（老板 2026-11-17 拍板：触发线 = min(7 秒，落后目标 ÷ 2)）：
    /// 落后 60/30 秒档=原 7 秒不变；10 秒档 5 秒、5 秒档 2.5 秒；贴边 0 档「水位&lt;0」不可能=绝对线自然失效，
    /// 只认外推预测跌停（外推降档入 v2.5 蓝本，触发前提=水位 ≥ max(1.5 秒，落后 ÷ 4)，低于此归微卡修法管辖）。
    /// 每拍现取门面值=快照语义，改档不打断进行中的判据账，下一拍起按新线判。</summary>
    public static TimeSpan ProactiveInventoryFloor => TimeSpan.FromMilliseconds(
        Math.Min(BufferInventory.TargetMs, AdvancedTuning.LiveLagSec * 500));

    /// <summary>预防性换源冷却窗：切换后冷却期内不再预防性介入（防坏线连切横跳）；卡流看门狗自救不受限。</summary>
    public static readonly TimeSpan ProactiveCooldown = TimeSpan.FromSeconds(60);

    // ---- vc82 第二刀/第三刀：连环限速 + 全断自愈（老板 2026-11-16 真机实锤三条账）----

    /// <summary>连环限速滚动窗：窗内自动换源（真断/坏票/预防介终路）次数封顶，防连环快切烧线。</summary>
    public static readonly TimeSpan FailoverThrottleWindow = TimeSpan.FromSeconds(60);

    /// <summary>自愈首档退避：全断后 5 秒起慢轮重试。</summary>
    public static readonly TimeSpan SelfHealFirstDelay = TimeSpan.FromSeconds(5);

    /// <summary>自愈退避封顶：再久也不超过 60 秒一档。</summary>
    public static readonly TimeSpan SelfHealMaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>自愈重试封顶次数：退避重试满 6 次仍全断即停止自动重试，保持空态等按键（不无限烧）。</summary>
    public const int SelfHealMaxAttempts = 6;

    /// <summary>自愈慢轮落定后的重探间隔（断网/断源态的探活回看节拍）：接通「自救重探间隔」旋钮
    /// （老板 2026-11-17 拍板，假旋钮接通第 2 位）。</summary>
    /// <remarks>语义映射（实现前已核对）：退避节奏（SelfHealDelay 5/10/20/40/60，重试节拍）与重探间隔
    /// （探活回看节拍）是两件事，不硬套——慢轮重试期间节拍仍走退避函数；慢轮落定（满额收摊/断网静默态）
    /// 后的周期性探活用本旋钮，每拍现取，改档下一轮生效。</remarks>
    public static TimeSpan SelfHealReprobeInterval => TimeSpan.FromMilliseconds(AdvancedTuning.SelfHealReprobeMs);

    private readonly List<long> _autoFailoverTimes = new();
    private long? _selfHealDueAtMs;
    private int _selfHealAttempts;

    /// <summary>自愈退避节奏：5/10/20/40/60/60 秒（指数翻倍，封顶 60 秒）。</summary>
    private static TimeSpan SelfHealDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(SelfHealMaxDelay.TotalSeconds, SelfHealFirstDelay.TotalSeconds * Math.Pow(2, attempt - 1)));

    /// <summary>排一次自愈慢轮（vc82）：全断/限速冻结后按退避节奏自动重试，绝不静默挂起。</summary>
    private void ScheduleSelfHeal()
    {
        if (!_selfHealDueAtMs.HasValue)
        {
            _selfHealDueAtMs = _clock.NowMs + (long)SelfHealFirstDelay.TotalMilliseconds;
        }
    }

    /// <summary>
    /// 预防性介入泵（老板令 #24/#26：主动预防，不做「出了问题再处理问题」）：
    /// Playing 期两条判据，命中其一且有备线、不在冷却窗 → 预防性切下一路线：
    /// ① 库存线（#24，触发线随落后旋钮联动=min(7 秒，落后÷2)）：内核库存（BufferedMs）跌破线（&gt;0 且低于触发线）连续两拍（间隔≥1 秒，单次网络抖动不切）；
    /// ② 欠载线（#26）：滚动窗内短卡流满 3 次——反复饿也算坏线，不等下一次卡死 15 秒。
    /// 0 读数按「无读数」不判趋势（IPlayer 契约：直播源无数≠见底）；真见底（内核进卡流态）走既有看门狗。
    /// 生产由管理器 Tick 驱动，与 CheckWatchdog 同泵；测试注入 FixedClock 后手动泵。
    /// </summary>
    public bool CheckProactiveFailover()
    {
        if (!_active || _state != PlaybackState.Playing)
        {
            _lowInventoryRounds = 0;
            return false;
        }
        if (_endpointIndex + 1 >= _queue.Count)
        {
            return false; // 没有备线，无从预防（最后一根线由看门狗兜底）
        }
        long nowMs = _clock.NowMs;
        if (_lastFailoverAtMs is { } lastFailover
            && nowMs - lastFailover < ProactiveCooldown.TotalMilliseconds)
        {
            _lowInventoryRounds = 0; // vc73 审查清零 P2 修：冷却窗内不切，趋势账清零，防坏线连切横跳
            return false;
        }
        if (_proactiveCount >= ProactiveCapPerChannel)
        {
            // vc67：预防介入到顶——本台不再预防性切线（慢网烧线病根），真卡流交看门狗与内核报错
            _lowInventoryRounds = 0;
            return false;
        }

        // 判据②（老板令 #26）：滚动窗内卡流满 3 次，账内即时坐实，不需第二拍等趋势
        _stallTimes.RemoveAll(t => nowMs - t >= StallChurnWindow.TotalMilliseconds);
        bool churnTripped = _stallTimes.Count >= StallChurnThreshold;

        // 判据③（vc70 慢帧流）：滚动窗内平稳疑点满 3 次——帧在上游被节流（画面每半秒一帧），
        // 探活通/库存足/带宽够/解码无错，全不触发既有自救，只能靠平稳账单独定罪
        _smoothnessTimes.RemoveAll(t => nowMs - t >= SmoothnessSuspectWindow.TotalMilliseconds);
        bool smoothnessTripped = _smoothnessTimes.Count >= SmoothnessSuspectThreshold;

        long bufferedMs = _player.BufferedMs;
        bool lowInventory = bufferedMs > 0 && bufferedMs < ProactiveInventoryFloor.TotalMilliseconds;
        if (!lowInventory)
        {
            _lowInventoryRounds = 0; // 库存回线：趋势账清零
            if (bufferedMs >= ProactiveInventoryFloor.TotalMilliseconds)
            {
                _inventoryEverFilled = true; // vc68 误烧闸：库存攒满过线，本线路取得「预防介入资格」
            }
        }
        else if (_lowInventoryRounds == 0 || nowMs - _lowInventoryLastAtMs >= 1000)
        {
            _lowInventoryRounds++;
            _lowInventoryLastAtMs = nowMs;
        }
        bool inventoryTripped = lowInventory && _inventoryEverFilled && _lowInventoryRounds >= 2;
        if (!churnTripped && !inventoryTripped && !smoothnessTripped)
        {
            return false; // 三条判据都没坐实：单次抖动不切，等趋势
        }
        _lowInventoryRounds = 0;
        _proactiveCount++; // vc67：预防介入记账，到顶退场（慢网烧线病根）
        // 预防打点（老板令 #24/#26）：主动介入的账面依据——命中哪条判据、账面多少、切前是第几路线
        string basis = churnTripped
            ? $"滚动窗 {StallChurnWindow.TotalMinutes:0}min 内卡流 {_stallTimes.Count} 次（满 {StallChurnThreshold} 次）"
            : smoothnessTripped
                ? $"滚动窗 {SmoothnessSuspectWindow.TotalSeconds:0}s 内平稳疑点 {_smoothnessTimes.Count} 次（满 {SmoothnessSuspectThreshold} 次，慢帧流坏线）"
                : $"库存 {bufferedMs}ms 连续 2 拍低于 {(int)ProactiveInventoryFloor.TotalMilliseconds}ms";
        SessionTrace.Emit($"[预防] {DescribeChannel()} {basis}，预防性换源（原线路 {CurrentLineIndex + 1}/{LineCount}，不等卡流超时）");
        _stallTimes.Clear(); // 介入一次清一次账：新线重新起账，老账不跟去新线
        _smoothnessTimes.Clear();
        // 走既有自救口径：先归 Failed 再切线（状态机矩阵 Playing 不直跳 Buffering，与看门狗/报错同路）
        Transition(PlaybackState.Failed, "proactive switch");
        TryFailover(churnTripped ? "stall churn proactive"
            : smoothnessTripped ? "slow-frame proactive"
            : "low inventory proactive");
        return true;
    }

    /// <summary>被管理器换下：停内核、摘面、状态归 Idle，此后不再响应内核事件。
    /// vc68 漏音根因②修复：停内核非 Idle 态一律执行——此前 Failed 态（看门狗/ForceFailover 烧光备源后）
    /// 跳过 Stop，内核最后一路流的音频还在外放，切台后「画是新的音是旧的」。Idle 态内核已停（Stop 幂等
    /// 且 Idle 只在停后/未起播出现），跳过无害；音画同源同一内核，Stop 同拆音视频渲染器，同进同出。</summary>
    public void Supersede()
    {
        _active = false;
        IsForeground = false; // vc69 让道修：让位即失前台，门控从此不碰（热备/让位会话不翻全局车道门）
        DetachSurface(clearHandle: false); // 让位摘面保留句柄：热切接管按句柄移交真面（面账归一 2026-11-16）
        if (_state is not PlaybackState.Idle)
        {
            _player.Stop(); // vc68：含 Failed 态一律停，旧台音画必死
            Transition(PlaybackState.Idle, "superseded");
        }
    }

    /// <summary>
    /// 释放内核（独享内核场景下管理器 LRU 淘汰会话用）：仅限已让位（Superseded）会话，
    /// 释放后本会话不可再起播。在播/在备会话调用抛异常（资源闸不误杀热备）。
    /// </summary>
    public void ReleaseKernel()
    {
        if (_active)
        {
            throw new InvalidOperationException("ReleaseKernel only legal on superseded sessions");
        }
        _player.Dispose();
    }

    private void OnPlayerError(string code)
    {
        if (!_active)
        {
            return;
        }
        if (_state is not (PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Reconnecting))
        {
            return;
        }
        // vc69 坏票不自焚（真机 2026-11-16 00:08 实锤：缓存过期票 URL 4xx → 4 条线数秒烧光 → 全线路不可达）：
        // HTTP 状态错（坏票/签名失效）先原地作废票据换新票重试一次，不烧线不切台。
        // 每线路一次（TryFailover 切线时重记），防坏票死循环；未接作废器（测试/壳层未接）不启用重试。
        if (code.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
            && !_freshTicketRetried
            && TicketCacheInvalidator is { } invalidate
            && _channel is { } retryChannel
            && CurrentEndpoint is { } badEndpoint)
        {
            _freshTicketRetried = true;
            _ticketRetrySinceMs = _clock.NowMs;
            invalidate(retryChannel.Id, badEndpoint);
            SessionTrace.Emit($"[坏票] {DescribeChannel()} 线路{CurrentLineIndex + 1} HTTP 状态错（{code}），作废票据原地换新票重试（宽限窗 {TicketRetryGrace.TotalSeconds:0}s）");
            // 迁移次序照 TryFailover 口径：Failed 落账后必须回 Buffering 起新一轮——
            // 不回 Buffering 状态机停在 Failed，看门狗失灵、后续报错被终态守卫吞掉（全测 2 例红实锤）
            Transition(PlaybackState.Failed, $"bad ticket {code}");
            Transition(PlaybackState.Buffering, "bad ticket retry");
            _player.Play(new PlaybackParams(retryChannel, badEndpoint, _preferLowLatency));
            ReattachSurface();
            return;
        }
        // vc82 第一刀（老板真机 2026-11-16 实锤：音画正常也被 5 秒连切 3 次）：换源前先判「画面还活着吗」——
        // Playing（画面在播）且库存未干（BufferedMs>0）时，内核轻错误一律只记账不切线，让内核自己重试续拉；
        // 真断（无画面/库存见底/看门狗超时无起色）走下方原路换源。画面是唯一裁判：画面在播即好线。
        if (_state == PlaybackState.Playing && _player.BufferedMs > 0)
        {
            RecordLightError(code);
            return;
        }
        Transition(PlaybackState.Failed, $"player error {code}");
        // 换源账随真实码（真机诊断 11-15）：诊断页换源行能看到具体错误码，不再只剩裸词
        TryFailover($"player error {code}");
    }

    /// <summary>vc82 轻错误记账窗：同线连续轻错误只滚动计数，不升级不切线（切不切由画面与看门狗说话）。</summary>
    public static readonly TimeSpan LightErrorTraceWindow = TimeSpan.FromSeconds(30);

    private readonly List<long> _lightErrorTimes = new();

    /// <summary>轻错误记账（vc82）：滚动窗计数，首条与每满 5 条打点一次——真机日志可查「误杀嫌疑对账」，
    /// 同时防刷屏。账不触发任何切线；内核报错后画面若真断，看门狗（NotifyStalled→Reconnecting→超时）照常兜底。</summary>
    private void RecordLightError(string code)
    {
        long nowMs = _clock.NowMs;
        _lightErrorTimes.RemoveAll(t => nowMs - t >= LightErrorTraceWindow.TotalMilliseconds);
        _lightErrorTimes.Add(nowMs);
        if (_lightErrorTimes.Count == 1 || _lightErrorTimes.Count % 5 == 0)
        {
            SessionTrace.Emit($"[轻错] {DescribeChannel()} 线路{CurrentLineIndex + 1} 报 {code}，画面在播+存货 {_player.BufferedMs}ms，只记账不切线（近窗第 {_lightErrorTimes.Count} 次）");
        }
    }

    private void TryFailover(string reason)
    {
        var next = _endpointIndex + 1;
        if (_channel is null || next >= _queue.Count)
        {
            // vc68 渲染必须结束（老板真机报「黑屏只出音」实锤）：备源耗尽即全线路不可达，
            // 壳层出空态——但此前的守卫只进 Failed 不停内核，最后一颗内核的音频照放。
            // 停机幂等，OK 重试从 Failed 再起播不受影响。
            _player.Stop();
            ScheduleSelfHeal(); // vc82 全断不装死：耗尽即排自愈慢轮，不再只等按键
            AllSourcesFailed?.Invoke(this, new AllSourcesFailedEventArgs(_channel?.Id ?? string.Empty, reason));
            return;
        }
        // vc82 第二刀（老板令「连环限速」）：滚动窗内自动换源满额即冻结——不再继续烧线，
        // 转空态排自愈慢轮（退避节奏定点慢试），把「连环快切」变成「慢试不烧线」。
        // 第 7 旋钮接通（老板 2026-11-17 拍板）：封顶次数读「防快切闸」旋钮，全局钳制下限 1 次/分钟调不灭；
        // 吃闸的只有故障硬切，对齐软切豁免（四原则 3），冻结后转自愈慢轮不空转。
        var nowMs = _clock.NowMs;
        _autoFailoverTimes.RemoveAll(t => nowMs - t >= FailoverThrottleWindow.TotalMilliseconds);
        var throttleCap = AdvancedTuning.FailoverCapPerMin;
        if (_autoFailoverTimes.Count >= throttleCap)
        {
            SessionTrace.Emit($"[限速] {DescribeChannel()} {FailoverThrottleWindow.TotalSeconds:0} 秒内自动换源已满 {throttleCap} 次，冻结不切（{reason}），转自愈慢轮");
            _player.Stop();
            ScheduleSelfHeal();
            AllSourcesFailed?.Invoke(this, new AllSourcesFailedEventArgs(_channel.Id, $"throttled: {reason}"));
            return;
        }
        _autoFailoverTimes.Add(nowMs);
        var from = _queue[_endpointIndex];
        _endpointIndex = next;
        _stallTimes.Clear(); // vc72 审查清零 P1：自救路径同换台/换线/预防介入口径清账（账只记本线，不跟去新线）
        _smoothnessTimes.Clear();
        _lightErrorTimes.Clear(); // vc82：轻错账同口径只记本线，切线清账
        FailoverCount++;
        _freshTicketRetried = false; // vc69：坏票重试机会每线路一次，切线重记
        _ticketRetrySinceMs = null;
        _lastFailoverAtMs = _clock.NowMs; // 预防性介入冷却账起算（老板令 #24）
        Transition(PlaybackState.Buffering, $"failover after {reason}");
        _player.Play(new PlaybackParams(_channel, _queue[next], _preferLowLatency));
        ReattachSurface();
        Failover?.Invoke(this, new FailoverEventArgs(from, _queue[next], FailoverCount, reason));
    }

    private void ReattachSurface()
    {
        // 面账归一（2026-11-16 换源冻帧方案 A）：以会话句柄账为准——换台/换线/自救不再清面账，
        // 只要壳层真面仍在（句柄非零），重起播后必重挂真面；句柄为零（壳层纹理已销毁）才不挂，防死面复活。
        if (_surfaceHandle != IntPtr.Zero)
        {
            _player.AttachSurface(_surfaceHandle);
        }
    }

    /// <summary>台账频道名（埋点补充 2026-11-15）：名称优先，无名退 Id，从未起播记「未知频道」。</summary>
    private string DescribeChannel() =>
        _channel?.Name ?? _channel?.Id ?? "未知频道";

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：换源触发时才遍历注册表（不进预检常驻轮），
    /// 本台源在前、注册表纪律序（观察档垫底/纯 IP 优先/登记序=2026-11-12 实测活性基线）在后；
    /// 同址去重，本台已有的源不重复入队。注册表 Referer/UA 在 2.0 端点模型无承接面（内核不带每源头），仅收录 URL。
    /// </summary>
    private void AppendFallbacks()
    {
        if (_channel is null || FallbackSourceProvider is not { } provider)
        {
            return;
        }
        var seen = new HashSet<string>(_queue.Select(s => s.Url), StringComparer.Ordinal);
        foreach (var candidate in provider(_channel))
        {
            if (candidate.IsValid && seen.Add(candidate.Url))
            {
                _queue.Add(candidate);
            }
        }
    }
}

/// <summary>时钟抽象：测试注入固定时钟，生产用系统时钟。NowMs 为单调毫秒（计时账铁律）。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>单调时钟毫秒读数：只前进不回跳，供运行期计时账使用（开机校时跳变不影响）。</summary>
    long NowMs { get; }
}

/// <summary>断流自救切换事件参数。</summary>
public sealed class FailoverEventArgs(
    SourceEndpoint from,
    SourceEndpoint to,
    int attempt,
    string reason)
    : EventArgs
{
    public SourceEndpoint From { get; } = from;
    public SourceEndpoint To { get; } = to;
    public int Attempt { get; } = attempt;
    public string Reason { get; } = reason;
}

/// <summary>备源耗尽事件参数。</summary>
public sealed class AllSourcesFailedEventArgs(string channelId, string reason) : EventArgs
{
    public string ChannelId { get; } = channelId;
    public string Reason { get; } = reason;
}
