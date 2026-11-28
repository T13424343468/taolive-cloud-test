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
    private long? _lowInventorySinceMs; // vc117 水位修复 B：本轮低水位起点（连续低水位时长账，放宽闸判据用）
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
    // 真卡流交看门狗（进度感知窗）。vc89 补口径：预防介入仍守此闸；但看门狗重灌期对「从没攒满过」
    // 的弱线开干库秒切快刀（FastDrainWindow=3 秒零进度即切）——预防不烧线与弱线快切不冲突。
    // 卡流次数判据（#26）不受限。
    private bool _inventoryEverFilled;
    // vc69 坏票不自焚账（真机 2026-11-16 00:08 实锤：缓存过期票 URL 4xx → 4 条线数秒烧光 → 全线路不可达）：
    // 本线路已否用过「作废坏票原地换新票」的一次机会（每线路一次，防死循环）；_ticketRetrySinceMs=重试宽限窗起点。
    private bool _freshTicketRetried;
    private long? _ticketRetrySinceMs;

    // vc146 刀②③（老板令 2026-11-28「这三点你先落」）：官方源拒流账——同台连续 HTTP 状态错计数，
    // 满 2 次=官方源被源端拒流（风控/防盗链），本轮不再取票撞墙（坏票重试路径跳过），并记 3 分钟
    // 跳官方窗：自愈慢轮/轻试探活直接从备源起轮，不弹回官方乒乓。官方源出画即清账（缓过来自动回正）。
    internal const int OfficialRejectStrikes = 2;
    internal static readonly TimeSpan OfficialSkipWindow = TimeSpan.FromMinutes(3);
    private int _httpStrikeCount;
    private long _officialSkipUntilMs;

    // vc87 票账降权（老板拍板「三方案一次到位」）：端点起播时刻账——起播后 ShortTicketGraceMs 内
    // 即报 HTTP 状态错=短命票（拿到票 6 秒就被源端拒的坏票循环病灶），实测账降权不给头名。
    private long? _endpointStartedAtMs;

    /// <summary>短命票判据窗（vc87，拟口径待老板复核）：端点起播后 15 秒内报 HTTP 错即记短命票降权。</summary>
    public const int ShortTicketGraceMs = 15_000;

    /// <summary>端点起播打点（vc87 票账降权）：全部播放点统一调用，记当前线路起播时刻（单调钟）。
    /// vc89 灌速探针收账：换线重记预热机会（同线宽限重试不重记不重发），每轮起播重开采样窗。</summary>
    private void MarkEndpointStarted()
    {
        _endpointStartedAtMs = _clock.NowMs;
        _fillProbeSinceMs = null; // 新一轮起播：采样窗重开
        if (!ReferenceEquals(CurrentEndpoint, _probeEndpoint))
        {
            _probeEndpoint = CurrentEndpoint;
            _fillProbePrewarmed = false; // 换线：预热信号机会重记
        }
    }

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
        _player.OnError += (_, e) => GuardKernel(() => OnPlayerError(e.Code, e.Message));
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
            // vc144 堆栈补丁（老板令「诊断进了没有」）：NPE 排除 media3 Java 侧后指向 C# 数据源链/绑定层——
            // 只落 ex.Message 查不到抛点，改落 ex.ToString()（含托管堆栈），诊断页直接见发源帧
            KernelCallbackFailed?.Invoke(this, $"{CurrentChannel?.Name ?? CurrentChannel?.Id ?? "未知频道"}: {ex}");
        }
    }

    /// <summary>起播超时（看门狗阈值）：缓冲超过此时长即视为断流，走备源切换。
    /// #74 切台提速（2026-11-15 老板定值）：15 秒→3 秒——死源撞满钟是切台黑屏 8-10 秒主账，快切快走。
    /// vc68 三轮（2026-11-16 真机实锤 3004ms≥3000ms·0ms 存货·四线全灭）：判死窗改由重入门槛推导
    /// （StartupThreshold=12 秒）——本窗必须同时盖过两个账：
    /// ①内核连接超时：Media3 默认连接超时 8 秒，窗≤8 秒时内核的报错快刀永远没机会先出手，全被 3 秒误杀
    /// （vc66/vc67/vc68 三次真机同款死局，台账在案：判死窗必须>门槛，慢热源要有证明时间）；
    /// ②冷连接全程：解析+连服+握手+取清单+取首段，老盒子实测可到 13-16 秒出画基线（vc66 实锤）。
    /// 刀二（老板 2026-11-25 九件套定稿）：12 秒收 8 秒（PrefetchCurve.OpenTimeout）——死源黑屏再少等 4 秒。
    /// 收窗前提=流时钟查活续期一起动（vc67 机制在案）：3 秒内缓冲有增长即放宽到慢灌窗（21 秒），
    /// 8 秒只咬零进度真死源；慢而通另有原地宽限重试（进度闸+预算封顶）双保险，慢热源不误杀。
    /// 真死快刀不走此账——内核报错信号直判不变。</summary>
    public static TimeSpan DefaultOpenTimeout { get; } = PrefetchCurve.OpenTimeout;

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

    /// <summary>播放点存货读数（vc85 偷拉管道让路闸用）：内核缓冲水位毫秒；未就绪=0（无数≠见底口径）。</summary>
    public long BufferedStockMs => _player.BufferedMs;

    /// <summary>窥视备援队列指定路（vc85 偷拉单管道换向用）：越界返回 null，不改排队序。</summary>
    public SourceEndpoint? PeekQueueEndpoint(int index) =>
        index >= 0 && index < _queue.Count ? _queue[index] : null;

    /// <summary>竞速回填（vc85 六项优化①）：把先握手成功的路提到队头（余者稳定保序）。
    /// 只影响后续快照（切换申请时取队列快照，重排不追溯）；在播线路身份按端点对象保持，
    /// 索引账随重排校正。越界/0 号本就是头名=无害空操作。</summary>
    public void ReorderQueueWinnerFirst(int winnerIndex)
    {
        if (winnerIndex <= 0 || winnerIndex >= _queue.Count) return;
        var winner = _queue[winnerIndex];
        _queue.RemoveAt(winnerIndex);
        _queue.Insert(0, winner);
        if (_endpointIndex == winnerIndex) _endpointIndex = 0;          // 在播路即胜者：随胜者归队头（vc96 P1 补 == 分支）
        else if (_endpointIndex >= 0 && _endpointIndex < winnerIndex) _endpointIndex++; // 在播路在胜者之前：右移一位保身份
    }

    /// <summary>链路健康线（vc85 两增强①）：坏帧/掉速观察统一喂账；未接=null 静默。</summary>
    public LinkHealthLine? HealthLine { get; set; }

    /// <summary>降档软着陆开关（vc85 蓝本 v2.5，预防性硬切退役）：true=预防判据命中走对齐切原语；
    /// false=旧硬切口径（未接仲裁器的壳层/既有测试契约不变）。管理器接线时随仲裁器一并置位。</summary>
    public bool SoftLandingEnabled { get; set; }

    /// <summary>内核视频故障喂账（vc85 两增强①⑥：坏帧信号进健康线）：实测账坏帧降权+链路裕度负项，
    /// 只记不判（判别归既有守卫与看门狗）。shell 层 OnVideoFault 信号转发至此。</summary>
    public void NotifyVideoFault(string detail)
    {
        if (!_active) return;
        if (QualityBook is { } book && CurrentEndpoint is { } badEndpoint)
        {
            book.ReportBadFrames(_channel?.EpgKey ?? "", _channel?.SourceId ?? "", badEndpoint.Url, 1);
        }
        HealthLine?.ReportBadFrames(1);
        SessionTrace.Emit($"[坏帧] {DescribeChannel()} 线路{CurrentLineIndex + 1} 视频故障入健康线（{detail}）");
    }

    /// <summary>本会话累计断流自救切换次数；换台归零。</summary>
    public int FailoverCount { get; private set; }

    /// <summary>当前自愈退避重试次数（vc113 原型对照令：失败终态文案「第 X/6 次」读数）；
    /// 满额轻试不消耗也不重置（账面钉住满额），换台归零。</summary>
    public int SelfHealAttempts => _selfHealAttempts;

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：建队列时在本台源之后追加该供给给出的备援候选
    /// （URL 去重，本台已有同址不重复入队）；空=不吃内置表（既有契约不变，测试可注空）。
    /// </summary>
    public Func<Channel, IReadOnlyList<SourceEndpoint>>? FallbackSourceProvider { get; set; }

    /// <summary>#74 源队列健康排序（2026-11-15 老板令）：建队（含备源追加后）整体重排——本台源死账时
    /// 死源沉底、健康候选补位。壳层接健康账（账空=原表序不猜）；未设=按原表序（既有契约与测试不变）。</summary>
    public Func<Channel, IEnumerable<SourceEndpoint>, IEnumerable<SourceEndpoint>>? QueueOrderer { get; set; }

    /// <summary>源实测账（vc85 蓝本 v2.1 健康账两本之二，管「好不好」）：微卡上账/盲区标记/实测成功，
    /// 管理器装配后随会话注入；null=账本未接（择路只吃探活账），不挡播放。</summary>
    public QualityHealthStore? QualityBook { get; set; }

    /// <summary>当前输出面句柄（管理器热切换移交用）；未接面为 IntPtr.Zero。</summary>
    public IntPtr SurfaceHandle => _surfaceHandle;

    /// <summary>状态迁移完成事件（迁移成功后触发，含换台与自救引发的迁移）。</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>断流自救切换事件：From 为切出源，To 为切入源，Attempt 从 1 计。</summary>
    public event EventHandler<FailoverEventArgs>? Failover;

    /// <summary>备源耗尽：本频道全部源都试败，会话停在 Failed。</summary>
    public event EventHandler<AllSourcesFailedEventArgs>? AllSourcesFailed;

    /// <summary>慢连宽限启用（vc86 F7 老板令）：宽限窗挂「正在换信号」——如实告知原地等慢源，不是黑屏死机。</summary>
    public event EventHandler? SlowGraceStarted;

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
            // vc86 慢连宽限收账：出画即愈，宽限账复位（新一轮起播重新给满预算，F2 封顶只管本轮起播）
            _slowGraceCount = 0;
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
        _videoStallTimes.Clear(); // vc87：停滞账同口径清零
        _freshTicketRetried = false; // vc69：新一轮起播重记坏票重试机会
        _ticketRetrySinceMs = null;
        _slowGraceCount = 0; // vc86：新一轮起播慢连宽限重新给满（不背旧台旧轮账）
        _microlagSeekAtMs = null; // 微卡回退防振铃账随新起播清零（同欠载账口径）
        _silentRefetchDone = false; // vc117 水位修复 A：补库存名额随新起播重记（每线路一次）
        Transition(PlaybackState.Buffering, "start requested");
        MarkBlindZoneIfAny(p.Endpoint); // 盲区标记（微卡修法三条③）：非 .m3u8 直流源天然贴边，实测账降权不给头名
        MarkEndpointStarted(); // vc87 票账降权：端点起播打点
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
        _httpStrikeCount = 0; // vc146 刀②：拒流账只记本台，换台清零不跟人走
        _officialSkipUntilMs = 0;
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
        _slowGraceCount = 0; // vc86：换台慢连宽限重新给满（账只记本台，不跟人走）
        _proactiveCount = 0; // vc67：换台重给预防介入预算
        _stallTimes.Clear(); // 欠载趋势账随换台清零（老板令 #26）：账只记本台画质，不跟人走
        _smoothnessTimes.Clear(); // vc70：平稳疑点账同口径，换台清零不跟人走
        _videoStallTimes.Clear(); // vc87：停滞账同口径清零
        _microlagSeekAtMs = null; // 微卡回退防振铃账随换台清零（同口径）
        _silentRefetchDone = false; // vc117 水位修复 A：补库存名额随换台重记（每线路一次）
        _lastFailoverAtMs = null; // vc96 P3：预防介入冷却账随换台清零，不跟人走（同限速账口径）
        Transition(PlaybackState.Buffering, "change channel");
        MarkBlindZoneIfAny(_queue[0]); // 盲区标记（微卡修法三条③）：同口径，换台起播首路即标
        MarkEndpointStarted(); // vc87 票账降权：端点起播打点
        _player.Play(new PlaybackParams(channel, _queue[0], _preferLowLatency));
        ReattachSurface();
    }

    /// <summary>盲区标记单点：URL 不含 .m3u8 即直流盲区（内核 ExoPlayerPlayer 只对 .m3u8 挂落后留水，
    /// 直流源播放点贴边赛跑无落点——天然贴边标记进实测账，健康线降权、排序不给头名）。账本未接静默。</summary>
    private void MarkBlindZoneIfAny(SourceEndpoint endpoint)
    {
        if (QualityBook is null) return;
        if (QualityHealthStore.IsBlindZoneUrl(endpoint.Url))
        {
            QualityBook.MarkBlindZone(_channel?.EpgKey ?? "", _channel?.SourceId ?? "", endpoint.Url);
        }
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
        SwitchLineCore(endpointIndex, "line switch", "manual line switch", userIntent: true);
    }

    /// <summary>对齐切原语（vc85 蓝本三原语之「对齐切」）：升降档/回主源统一落点。
    /// 不吃故障限速闸（四原则3），与手动换线同一动作核（停旧流→Idle→起新线→重挂面）；
    /// 切完发 <see cref="AlignedSwitched"/> 供管理器做升档结果回账（逃生口/休眠）。
    /// 与 <see cref="SwitchLine"/> 同样的参数契约与同线无操作语义。</summary>
    public void AlignedSwitchTo(int endpointIndex, string reason)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("AlignedSwitchTo requires a started channel");
        }
        if (endpointIndex < 0 || endpointIndex >= _queue.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointIndex));
        }
        if (endpointIndex == _endpointIndex && _state == PlaybackState.Playing)
        {
            return; // 同线无操作：不打断在播画面
        }
        SwitchLineCore(endpointIndex, "line switch", $"aligned switch: {reason}", userIntent: false);
        AlignedSwitched?.Invoke(this, reason);
    }

    /// <summary>对齐切完成（vc85 仲裁器接线）：管理器据此记升档账（逃生口失败/站住复位）。</summary>
    public event EventHandler<string>? AlignedSwitched;

    /// <summary>换线动作核（SwitchLine/AlignedSwitchTo 共用）：状态清账与起播的单一实现点。</summary>
    private void SwitchLineCore(int endpointIndex, string idleTag, string bufferingTag, bool userIntent)
    {
        // 两个入口（SwitchLine/AlignedSwitchTo）已各自行首校验非空；核内再断言一次供编译器收窄
        var startedChannel = _channel ?? throw new InvalidOperationException("SwitchLineCore requires a started channel");
        if (_state is not PlaybackState.Idle)
        {
            _player.Stop(); // vc68：含 Failed 态一律停（同 Supersede 口径），旧台音画必死
            Transition(PlaybackState.Idle, idleTag);
        }
        _endpointIndex = endpointIndex;
        FailoverCount = 0;
        _freshTicketRetried = false; // vc69：手动换线重记坏票重试机会
        _ticketRetrySinceMs = null;
        if (userIntent) _proactiveCount = 0; // vc67：手动换线=老板意图，重给预防介入预算；vc96 第 5 轮：对齐切（userIntent=false）不清预算，vc67 封顶 3 次恢复效力
        _stallTimes.Clear(); // 手动换线：欠载账随线清零
        _smoothnessTimes.Clear(); // vc70：平稳账随线清零
        _videoStallTimes.Clear(); // vc87：停滞账同口径清零
        _microlagSeekAtMs = null; // 微卡回退防振铃账随线清零（同口径）
        _silentRefetchDone = false; // vc117 水位修复 A：补库存名额随线重记（每线路一次）
        Transition(PlaybackState.Buffering, bufferingTag);
        MarkEndpointStarted(); // vc87 票账降权：端点起播打点
        _player.Play(new PlaybackParams(startedChannel, _queue[endpointIndex], _preferLowLatency));
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
            // 播放实测成功记账（实测账）：首帧落地=切换结果侧记 Good（蓝本 v2.1 第 12 条），择路提权依据
            if (CurrentEndpoint is { } goodEndpoint)
            {
                QualityBook?.ReportGood(_channel?.EpgKey ?? "", _channel?.SourceId ?? "", goodEndpoint.Url);
                // vc146 刀②：官方源出画即清拒流账——官方缓过来下一轮自动回正，跳官方窗提前作废
                if (goodEndpoint.Kind == SourceKind.Web)
                {
                    _httpStrikeCount = 0;
                    _officialSkipUntilMs = 0;
                }
            }
        }
    }

    /// <summary>静音攒流（vc68 漏音根因①修复，热备专用）：暂停出声出画，内核照常缓冲攒流——
    /// 此前热备起播 PlayWhenReady=true，备台攒流期间该台声音直接外放（「切台了还在出上个台的音」）。
    /// Play 后即 Pause：READY 后解码器仍初始化、哑面预热不失效，热切门槛（State==Playing）不破坏。</summary>
    public void PausePlayback() => _player.Pause();
    /// <summary>恢复出声出画（vc68）：热备热切接管时调用，缓冲攒流不中断。</summary>
    public void ResumePlayback() => _player.Resume();

    /// <summary>起播库存档透传（vc94 深水档，老板 2026-11-26 拍板）：热备攒流切 StartFull（接管即深水），
    /// 冷切回默认档。须在 ChangeChannel（→Play）之前调，内核起播装配按档生效。</summary>
    public void SetStartupGate(BufferInventory.Mode mode) => _player.SetStartupGateMode(mode);

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
            // 微卡上账（微卡修法三条②）：进缓冲即试探，实测账窗口内反复才坐实——瞬时断流不记坏账（四原则①）
            if (CurrentEndpoint is { } trippedEndpoint)
            {
                QualityBook?.ReportBufferingTrip(_channel?.EpgKey ?? "", _channel?.SourceId ?? "", trippedEndpoint.Url);
            }
            // 微卡回退（微卡修法①，vc85 蓝本 v2.1 10 秒口径，老板拍板「全采纳」）：
            // 盲区直流源天然贴边赛跑——进缓冲时播放点已追到边缘、零存货，换线自救治标不治本。
            // 回退 10 秒落回已缓存存量接着播，断掉赛跑链条；仅盲区源触发，30 秒防振铃账内最多一次
            // （连环回退=原地打转，第二拍起照旧走 Reconnecting→看门狗→换线既有自救）。
            if (TryMicrolagSeekBack()) return;
            Transition(PlaybackState.Reconnecting, "watchdog stall");
            _reconnectingSinceMs = _clock.NowMs;
            RecordStall(_clock.NowMs); // 欠载趋势账（老板令 #26）：反复短卡流也是主动介入依据
        }
    }

    /// <summary>微卡回退回退量（蓝本口径 10 秒）。</summary>
    public const int MicrolagSeekBackMs = 10_000;

    /// <summary>微卡回退防振铃窗（毫秒）：窗内最多回退一次，第二拍进缓冲照旧走换线自救。</summary>
    public const int MicrolagSeekBackoffMs = 30_000;

    private long? _microlagSeekAtMs; // 最近一次微卡回退记账（单调钟，null=从未回退）

    private bool _silentRefetchDone; // vc117 水位修复 A：本线路已静默重接补过库存（每线路一次，原口径「重接一次」）

    /// <summary>vc117 水位修复 B：慢灌源低水位放宽闸等待窗（毫秒）——读数在涨（线活着）但水位
    /// 持续低于库存线满此时长，也允许预防介入换线，不再要求「曾攒满过库存」。</summary>
    public const int SlowFillProactiveWaitMs = 10_000;

    /// <summary>微卡回退尝试：仅盲区直流源（URL 不含 .m3u8，与内核盲区档同判据）且不在防振铃窗内时
    /// 向内核回退 <see cref="MicrolagSeekBackMs"/> 并保持 Playing（内核存量续播，不进 Reconnecting）；
    /// 返回 false=不回退（非盲区/窗内重复），调用方照旧升级自救。</summary>
    private bool TryMicrolagSeekBack()
    {
        if (CurrentEndpoint is not { } endpoint) return false;
        if (!QualityHealthStore.IsBlindZoneUrl(endpoint.Url)) return false;
        long nowMs = _clock.NowMs;
        if (_microlagSeekAtMs is { } last && nowMs - last < MicrolagSeekBackoffMs) return false;
        _microlagSeekAtMs = nowMs;
        _player.SeekBack(MicrolagSeekBackMs);
        return true;
    }

    /// <summary>
    /// 静默重接补库存（vc117 水位修复 A，2026-11-28 老板拍板「落」）：SilentRefetch 档「先开画后补库存」
    /// 的补库存半边此前只有口径没有执行点（BufferPolicy 库存两档注释），低水位开画后无人补给，
    /// 一秒就追到直播边缘出画。开画后（Playing）水位低于补库目标
    /// （<see cref="BufferInventory.TargetMs"/>）且不在防振铃窗内时，静默回退一次（内核存量续播，
    /// 不进 Reconnecting），把退过的段补进库存——延迟 10~30 秒属老板确认取舍（vc53 00:52 拍板）。
    /// 与微卡回退共用防振铃账（回退类动作 30 秒窗内最多一次，防连环回退原地打转）；
    /// 每线路最多一次（原口径「重接一次」），起播/换台/换线重记。返回是否执行了重接。</summary>
    private bool TrySilentRefetch()
    {
        if (_silentRefetchDone) return false;
        if (CurrentEndpoint is not { } endpoint) return false;
        if (BufferInventory.Default != BufferInventory.Mode.SilentRefetch) return false; // 只在补库存档生效
        long bufferedMs = _player.BufferedMs;
        if (bufferedMs <= 0) return false; // 无读数不判（IPlayer 契约：无数≠见底）
        if (bufferedMs >= BufferInventory.TargetMs)
        {
            _silentRefetchDone = true; // 水位已过补库目标：无需重接，记账免再判
            return false;
        }
        long nowMs = _clock.NowMs;
        if (_microlagSeekAtMs is { } last && nowMs - last < MicrolagSeekBackoffMs) return false; // 防振铃窗共用
        _silentRefetchDone = true;
        _microlagSeekAtMs = nowMs;
        _player.SeekBack(MicrolagSeekBackMs);
        SessionTrace.Emit($"[补库存] {DescribeChannel()} 开画后水位 {bufferedMs}ms<{BufferInventory.TargetMs}ms，静默回退 {MicrolagSeekBackMs / 1000}s 重接补库存（先开画后补库存口径落地）");
        return true;
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

    /// <summary>视频停滞信号窗（vc87 老板拍板「停滞信号接入」）：与欠载账同窗（StallChurnWindow 5min）。</summary>
    public const int VideoStallThreshold = 2; // 窗内停滞满 2 次即坐实坏线（比欠载账严：真停滞比短卡流更毒）

    private readonly List<long> _videoStallTimes = new();

    /// <summary>视频停滞记账（vc87）：壳层「出画中 10 秒零视频帧」锚点上报，只记账不决策——
    /// 滚动窗内攒满即交预防泵换线（判据④），与库存/欠载/慢帧三判据并列。</summary>
    public void NotifyVideoStall()
    {
        if (!_active || _state != PlaybackState.Playing) return; // 停滞锚点只在出画期有意义
        var nowMs = _clock.NowMs;
        _videoStallTimes.RemoveAll(t => nowMs - t >= StallChurnWindow.TotalMilliseconds);
        _videoStallTimes.Add(nowMs);
    }

    /// <summary>
    /// 断流自愈（真机反馈批#5 修 2026-11-15）：Reconnecting 中内核恢复出帧（OnBufferingEnd）即回 Playing，
    /// 看门狗计时账随之清零——好画面不因历史卡流被误杀。Buffering 态（起播期）收 End 走 NotifyFirstFrame 正门，不进此路。
    /// </summary>
    private void NotifyRecovered()
    {
        if (_state == PlaybackState.Reconnecting)
        {
            // vc146 刀②：官方源出画即清拒流账——官方缓过来下一轮自动回正，跳官方窗提前作废
            if (CurrentEndpoint is { } okEp && okEp.Kind == SourceKind.Web)
            {
                _httpStrikeCount = 0;
                _officialSkipUntilMs = 0;
            }
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
        ProbeFillRate(nowMs); // vc89 灌速探针：起播期采样灌速，弱线提前焐热备线票
        // vc117 后台出声根治令（2026-11-28 老板拍板）：后台冻结期自愈慢轮/轻试探活一律不发起——
        // 壳层 OnPause 置 BackgroundHold，回前台 OnResume 撤；后台期间 Failed 挂起等回前台再续，
        // 防「返回=退后台」后门：后台自动重试把音频又拉起来（真机实锤 vc112 放宽后仍出声）。
        if (BackgroundHold) return false;
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
                    _endpointIndex = QueueStartIndex(); // vc146 刀③：跳官方窗内直走备源，不弹回官方乒乓
                    MarkEndpointStarted(); // vc87 票账降权：轻试探活也是端点起播
                    _stallTimes.Clear();   // 轻试探活：停滞账清零重记（vc96 P1，与慢轮同口径）
                    _smoothnessTimes.Clear();
                    _videoStallTimes.Clear();
                    _player.Play(new PlaybackParams(probeChannel, _queue[_endpointIndex], _preferLowLatency));
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
            _videoStallTimes.Clear(); // vc87：停滞账同口径清零
            _lightErrorTimes.Clear();
            _autoFailoverTimes.Clear(); // 自愈慢轮重开一轮限速账，慢试不受旧窗拖累
            _endpointIndex = QueueStartIndex(); // vc146 刀③：跳官方窗内直走备源起轮，不弹回官方乒乓；窗外照旧从头慢轮
            _freshTicketRetried = false; // 坏票重试机会重记
            _ticketRetrySinceMs = null;
            Transition(PlaybackState.Buffering, $"self-heal retry #{_selfHealAttempts}");
            if (_channel is { } healChannel && _queue.Count > 0)
            {
                MarkEndpointStarted(); // vc87 票账降权：自愈慢轮重试也是端点起播
                _player.Play(new PlaybackParams(healChannel, _queue[_endpointIndex], _preferLowLatency));
                ReattachSurface();
            }
            return true;
        }
        if (_state == PlaybackState.Buffering
            && _bufferingSinceMs is { } started)
        {
            // vc112 老板拍板「攒流期看门狗只判真死」：零进度照旧判死快切；慢而通（3 秒内有进度）攒流期不判死——
            // 门没开没画面可看，死活交开画闸（vc121 硬门禁：唯一条件=水位攒满 7 秒）兜底，出画后看门狗接管。
            if (_player.StartupGateAccumulating && _lastBufferProgressAtMs is { } t && nowMs - t < 3_000)
            {
                return false; // 慢而通攒流期不判死（Buffering 态下后续 Reconnecting 分支不适用）
            }
            var windowMs = WatchdogWindowMs(nowMs, SlowStartWindow, started);
            if (nowMs - started >= windowMs)
            {
                // 看门狗打点（埋点补充 2026-11-15）：此前看门狗触发完全静默，真机只能看到「换源」结果看不到因——
                // 补触发原因+已等时长，断流自救链路（含误杀嫌疑对账）可查。vc67：带上进度口径（快切/慢灌让路）。
                SessionTrace.Emit($"[看门狗] {DescribeChannel()} 起播超时 {nowMs - started}ms≥{windowMs}ms（原线路 {CurrentLineIndex + 1}/{LineCount}，{_watchdogLastBufferedMs}ms 存货）");
                // 老板令 2026-11-17 慢源宽限 B（vc86）：open-timeout 与内核超时同账——预算未满先原地宽限重试，
                // 12s 内核超时先到时宽限在报错路径吃掉，此处兜住竞速里看门狗先到的另一半（F1 修）。
                // vc104 宽限不重启：看门狗路径内核还在拉，只重计窗续攒，不扔货不重放（restartPlayer: false）
                if (_state == PlaybackState.Buffering && TrySlowConnectGrace("open timeout", restartPlayer: false))
                {
                    return true;
                }
                Transition(PlaybackState.Failed, "open timeout");
                TryFailover("open timeout");
                return true;
            }
        }
        if (_state == PlaybackState.Reconnecting
            && _reconnectingSinceMs is { } stalled)
        {
            var windowMs = WatchdogWindowMs(nowMs, SlowRecoveryWindow, stalled, allowFastDrain: true);
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

    /// <summary>vc104 慢灌放宽窗（起播）——门槛联动根修：vc103 首帧门槛回退 1 秒后，旧公式
    /// FirstFrameGate(写死 7s)×3=21 秒与出画门槛脱钩（门槛已 1 秒，窗仍 21 秒，慢死源白等）。
    /// 改为 max(OpenTimeout 8s, 出画门槛×3)：默认档 SilentRefetch 门槛 1 秒×3=3 秒 &lt; 8 秒，
    /// 窗值=8 秒与判死钟同账——慢灌让路不再超过兜底超时，慢而通的证明时间交给宽限不重启（本版第二刀）。
    /// 若未来门槛调回 7 秒（StartFull 档），窗自动回 21 秒口径，公式不再写死。</summary>
    /// <remarks>口径：慢灌源带宽 ≥1/3 流码率即灌得到。</remarks>
    public static readonly TimeSpan SlowStartWindow = TimeSpan.FromMilliseconds(Math.Max(
        PrefetchCurve.OpenTimeout.TotalMilliseconds,
        BufferInventory.FirstFrameOpenMs(BufferInventory.Default) * 3));

    /// <summary>vc67 慢灌放宽窗（重灌）：卡后重入门槛 12s×2=24 秒——重灌要满 12 秒媒体，带宽 ≥1/2 流码率即灌得到。</summary>
    public static readonly TimeSpan SlowRecoveryWindow =
        TimeSpan.FromMilliseconds(PrefetchCurve.StartupThreshold.TotalMilliseconds * 2);

    /// <summary>vc89 干库秒切窗：重灌期弱线零进度 3 秒即快切备线（不撞 12 秒判死钟）。
    /// 只对「从没攒满过库存」的线路生效（_inventoryEverFilled=false），好线（攒满过）不受影响走原窗。</summary>
    public static readonly TimeSpan FastDrainWindow = TimeSpan.FromSeconds(3);

    // ---- vc89 灌速探针（老板拍板「一次平稳」）----

    /// <summary>灌速弱线阈值：起播期采样「每等 1 秒灌进几秒存货」，低于 0.25×实时即弱线
    /// （1=灌速恰平播放实时；0.25=出画后 7 秒存货只够再撑约 9 秒就见底）。</summary>
    public const double WeakFillRateThreshold = 0.25;

    /// <summary>灌速探针最短采样时长（毫秒）：不足不给结论，防起播头一拍的抖动读数误判。</summary>
    public const int FillProbeMinElapsedMs = 2_000;

    /// <summary>灌速探针事件：弱线坐实时触发一次（参数=下一条备线端点），壳层接 TicketResolver
    /// 后台焐热备线票——不切线不动画面，干库秒切时零取票等待。每线路最多发一次（换线重记）。</summary>
    public event EventHandler<SourceEndpoint>? WeakFillDetected;

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

    // vc89 灌速探针账：_fillProbeSinceMs=本轮起播采样起点（每次进 Buffering 重开）；
    // _fillProbePrewarmed=本线路预热信号已发（换线才重记，同线宽限重试不重复发）；
    // _probeEndpoint=预热信号发出时端点身份（MarkEndpointStarted 对账判定换线）。
    private long? _fillProbeSinceMs;
    private bool _fillProbePrewarmed;
    private SourceEndpoint? _probeEndpoint;

    /// <summary>灌速探针（vc89 老板拍板「一次平稳」）：起播期采样「每等 1 秒灌进几秒存货」，
    /// 灌速长期低于阈值的弱线提前把下一条备线票焐热（发 <see cref="WeakFillDetected"/>，壳层接
    /// TicketResolver 后台预热）——不切线不动画面，干库秒切时零取票等待。
    /// 由 CheckWatchdog 泵驱动（Buffering 期生效），测试注入 FixedClock 后手动泵。</summary>
    private void ProbeFillRate(long nowMs)
    {
        if (_state != PlaybackState.Buffering) return;
        if (_fillProbePrewarmed || _endpointIndex + 1 >= _queue.Count) return; // 无备线无从预热
        _fillProbeSinceMs ??= nowMs;
        long elapsedMs = nowMs - _fillProbeSinceMs.Value;
        if (elapsedMs < FillProbeMinElapsedMs) return; // 采样窗不足不结论
        double rate = _watchdogLastBufferedMs / (double)elapsedMs; // 每等 1 秒灌进几秒存货
        if (rate >= WeakFillRateThreshold) return;
        _fillProbePrewarmed = true;
        var next = _queue[_endpointIndex + 1];
        SessionTrace.Emit($"[探针] {DescribeChannel()} 线路{CurrentLineIndex + 1} 灌速 {rate:0.00}×实时（< {WeakFillRateThreshold:0.##}×，{elapsedMs}ms 采样 {_watchdogLastBufferedMs}ms 存货），预热备线{CurrentLineIndex + 2}");
        WeakFillDetected?.Invoke(this, next);
    }

    /// <summary>vc67 看门狗窗：3 秒内有缓冲增长→放宽窗（慢灌让路）；零增长→判死窗（vc68 三轮改 12 秒推导值）。
    /// vc69 坏票宽限：换新票期间（取票网络耗时最长 5 秒+余量）宽限窗内不判死，期满恢复原窗。
    /// vc89 干库秒切（老板拍板「一次平稳」）：仅重灌期（画面在播后干库，allowFastDrain）且本线路
    /// 从未攒满过库存、3 秒内零进度——弱线真死不撞满 12 秒钟，3 秒快切备线，黑十来秒病根拔除。
    /// 起播期（Buffering）不适用：冷连接全程要 12 秒证明时间（vc68 三轮实锤），慢热源不能误杀；
    /// 坏票宽限窗仍最高优先（票在换，不切）。</summary>
    private long WatchdogWindowMs(long nowMs, TimeSpan slowWindow, long startedMs, bool allowFastDrain = false)
    {
        bool flowing = _lastBufferProgressAtMs is { } t && nowMs - t < 3_000;
        long windowMs = (long)Math.Max(OpenTimeout.TotalMilliseconds, flowing ? slowWindow.TotalMilliseconds : 0);
        if (allowFastDrain && !_inventoryEverFilled && !flowing)
        {
            windowMs = Math.Min(windowMs, (long)FastDrainWindow.TotalMilliseconds);
        }
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
            _lowInventorySinceMs = null;
            return false;
        }
        TrySilentRefetch(); // vc117 水位修复 A：静默重接补库存（不依赖备线，先于备线闸；幂等由自身账把住）
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

        // 判据④（vc87 老板拍板「停滞信号接入」）：滚动窗内视频停滞满 2 次——出画中 10 秒零帧，
        // 音画同源下比短卡流更毒，壳层停滞锚点已接 NotifyVideoStall，此处并入预防判线
        _videoStallTimes.RemoveAll(t => nowMs - t >= StallChurnWindow.TotalMilliseconds);
        bool stallTripped = _videoStallTimes.Count >= VideoStallThreshold;

        long bufferedMs = _player.BufferedMs;
        bool lowInventory = bufferedMs > 0 && bufferedMs < ProactiveInventoryFloor.TotalMilliseconds;
        bool slowFillTripped = false; // vc117 水位修复 B：慢灌源放宽闸判据（下文坐实）
        if (!lowInventory)
        {
            _lowInventoryRounds = 0; // 库存回线：趋势账清零
            _lowInventorySinceMs = null;
            if (bufferedMs >= ProactiveInventoryFloor.TotalMilliseconds)
            {
                _inventoryEverFilled = true; // vc68 误烧闸：库存攒满过线，本线路取得「预防介入资格」
            }
        }
        else
        {
            _lowInventorySinceMs ??= nowMs; // 低水位起点（连续低水位时长账）
            if (_lowInventoryRounds == 0 || nowMs - _lowInventoryLastAtMs >= 1000)
            {
                _lowInventoryRounds++;
                _lowInventoryLastAtMs = nowMs;
            }
            // vc117 水位修复 B（2026-11-28 老板拍板「落」）：放宽 _inventoryEverFilled 闸——慢灌源
            // （读数在涨=线活着，只是灌得慢）连续满一窗水位仍低于库存线时，也允许预防介入换线，
            // 不再要求「曾攒满过」；零进度死线仍交看门狗干库秒切（FastDrainWindow）管辖，口径不变。
            bool flowing = _lastBufferProgressAtMs is { } t && nowMs - t < 3_000;
            slowFillTripped = flowing && nowMs - _lowInventorySinceMs.Value >= SlowFillProactiveWaitMs;
        }
        bool inventoryTripped = (lowInventory && _inventoryEverFilled && _lowInventoryRounds >= 2)
            || slowFillTripped;
        if (!churnTripped && !inventoryTripped && !smoothnessTripped && !stallTripped)
        {
            return false; // 四条判据都没坐实：单次抖动不切，等趋势
        }
        _lowInventoryRounds = 0;
        _lowInventorySinceMs = null; // vc117 水位修复 B：低水位时长账随介入清零，新线重新起账
        _proactiveCount++; // vc67：预防介入记账，到顶退场（慢网烧线病根）
        // 预防打点（老板令 #24/#26）：主动介入的账面依据——命中哪条判据、账面多少、切前是第几路线
        string basis = stallTripped
            ? $"滚动窗 {StallChurnWindow.TotalMinutes:0}min 内视频停滞 {_videoStallTimes.Count} 次（满 {VideoStallThreshold} 次）"
            : churnTripped
            ? $"滚动窗 {StallChurnWindow.TotalMinutes:0}min 内卡流 {_stallTimes.Count} 次（满 {StallChurnThreshold} 次）"
            : smoothnessTripped
                ? $"滚动窗 {SmoothnessSuspectWindow.TotalSeconds:0}s 内平稳疑点 {_smoothnessTimes.Count} 次（满 {SmoothnessSuspectThreshold} 次，慢帧流坏线）"
                : slowFillTripped
                ? $"慢灌低水位 {bufferedMs}ms 持续 {SlowFillProactiveWaitMs / 1000}s（线活着灌不动，放宽闸介入）"
                : $"库存 {bufferedMs}ms 连续 2 拍低于 {(int)ProactiveInventoryFloor.TotalMilliseconds}ms";
        SessionTrace.Emit($"[预防] {DescribeChannel()} {basis}，预防性换源（原线路 {CurrentLineIndex + 1}/{LineCount}，不等卡流超时）");
        _stallTimes.Clear(); // 介入一次清一次账：新线重新起账，老账不跟去新线
        _smoothnessTimes.Clear();
        _videoStallTimes.Clear(); // vc87：停滞账同口径清零
        if (SoftLandingEnabled)
        {
            // 降档软着陆（vc85 蓝本 v2.5，预防性硬切退役）：对齐切原语落点，不吃故障限速闸（四原则3）；
            // 预防冷却窗与介入封顶在入口已过闸保留。备源全挂仍由看门狗/报错保命路径兜底。
            SessionTrace.Emit($"[软着陆] {DescribeChannel()} 预防判据命中改走对齐切（硬切退役）：切下一路（原线路 {CurrentLineIndex + 1}/{LineCount}）");
            AlignedSwitchTo(_endpointIndex + 1, "proactive soft landing");
            _lastFailoverAtMs = _clock.NowMs; // vc96 第 5 轮：软切也布预防冷却账（硬切路径 TryFailover 同口径），防横跳闸不再双失守
            return true;
        }
        // 走既有自救口径：先归 Failed 再切线（状态机矩阵 Playing 不直跳 Buffering，与看门狗/报错同路）
        Transition(PlaybackState.Failed, "proactive switch");
        TryFailover(stallTripped ? "video stall proactive"
            : churnTripped ? "stall churn proactive"
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

    private void OnPlayerError(string code, string? message = null)
    {
        if (!_active)
        {
            return;
        }
        // vc143 诊断盲区修（真机 19:03 全频道断实锤）：内核已拼好的 cause 链此前在会话层被丢弃，
        // 诊断页只见裸错误码查不到因——透传落账，宽限/落账/换源行直接见真因
        var detail = string.IsNullOrWhiteSpace(message)
            ? $"player error {code}"
            : $"player error {code} | {message}";
        if (_state is not (PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Reconnecting))
        {
            return;
        }
        // vc69 坏票不自焚（真机 2026-11-16 00:08 实锤：缓存过期票 URL 4xx → 4 条线数秒烧光 → 全线路不可达）：
        // HTTP 状态错（坏票/签名失效）先原地作废票据换新票重试一次，不烧线不切台。
        // 每线路一次（TryFailover 切线时重记），防坏票死循环；未接作废器（测试/壳层未接）不启用重试。
        // vc145 摘闸（真机 vc144 实锤）：换票是恢复路，冷却中不许换票=频道锁死——取票口主机与流主机
        // 分账，流主机冷却不挡换票；取票口自身频控由取票器冷却快速失败管着，此处不再重复设卡。
        // vc146 刀②（老板令 2026-11-28「这三点你先落」）：官方源拒流账——同台连续 HTTP 状态错满
        // 2 次=源端在拒流（风控/防盗链），换新票也是白打，本轮不再取票直接走备源（跳官方窗见刀③）。
        if (code.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
        {
            _httpStrikeCount++;
            // 满 2 次布窗；窗过期后拒流仍在继续（计数只增不再等于 2）须重布窗，否则跳官方窗只生效一轮
            if (_httpStrikeCount >= OfficialRejectStrikes
                && _clock.NowMs >= _officialSkipUntilMs
                && CurrentEndpoint is { } strikeEp && strikeEp.Kind == SourceKind.Web)
            {
                _officialSkipUntilMs = _clock.NowMs + (long)OfficialSkipWindow.TotalMilliseconds;
                SessionTrace.Emit($"[官方源拒流] {DescribeChannel()} 连续 {_httpStrikeCount} 次 HTTP 状态错，本轮不再取票，直接走备源（{OfficialSkipWindow.TotalMinutes:0} 分钟内自愈不弹回官方）");
            }
        }
        if (code.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
            && _httpStrikeCount < OfficialRejectStrikes
            && !_freshTicketRetried
            && TicketCacheInvalidator is { } invalidate
            && _channel is { } retryChannel
            && CurrentEndpoint is { } badEndpoint)
        {
            _freshTicketRetried = true;
            _ticketRetrySinceMs = _clock.NowMs;
            invalidate(retryChannel.Id, badEndpoint);
            // vc87 票账降权：端点起播后短窗内即报 HTTP 状态错=短命坏票，实测账降权不给头名
            if (_endpointStartedAtMs is { } startedAt && _clock.NowMs - startedAt < ShortTicketGraceMs)
            {
                QualityBook?.ReportShortTicket(retryChannel.EpgKey, retryChannel.SourceId, badEndpoint.Url);
            }
            SessionTrace.Emit($"[坏票] {DescribeChannel()} 线路{CurrentLineIndex + 1} HTTP 状态错（{code}），作废票据原地换新票重试（宽限窗 {TicketRetryGrace.TotalSeconds:0}s）");
            // 迁移次序照 TryFailover 口径：Failed 落账后必须回 Buffering 起新一轮——
            // 不回 Buffering 状态机停在 Failed，看门狗失灵、后续报错被终态守卫吞掉（全测 2 例红实锤）
            Transition(PlaybackState.Failed, $"bad ticket {code}");
            Transition(PlaybackState.Buffering, "bad ticket retry");
            MarkEndpointStarted(); // vc87 票账降权：换新票重开=新起播时刻
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
        // 老板令 2026-11-17 慢源宽限 B（vc86）：起播期超时类错误先原地宽限重试——把「慢而通」和「真死」分家；
        // 看门狗 open-timeout 同账同口径（TrySlowConnectGrace 单点），真死（拒绝连接/DNS/404）不进宽限照旧快切。
        // 判定必须在 Transition(Failed) 之前：落账后 _state 已是 Failed，宽限判 Buffering 永远轮不到（vc86 审查修正）
        if (_state == PlaybackState.Buffering
            && IsTimeoutClassError(code)
            && TrySlowConnectGrace(detail))
        {
            return;
        }
        Transition(PlaybackState.Failed, detail);
        // 换源账随真实码（真机诊断 11-15）：诊断页换源行能看到具体错误码，不再只剩裸词
        TryFailover(detail);
    }

    /// <summary>慢连宽限预算（vc86 老板拍板）：每台封顶 2 次——同族慢源最坏 2×24s 有界，不无限黑屏。</summary>
    public const int SlowConnectGraceBudget = 2;

    private int _slowGraceCount; // 慢连宽限账：到达 Playing 即复位（新一轮起播重给预算），换台清零

    /// <summary>超时类错误判据（vc86）：内核错误码带 timeout 字样才算慢连；拒绝连接/DNS/状态码不进宽限。</summary>
    private static bool IsTimeoutClassError(string code) =>
        !string.IsNullOrEmpty(code) && code.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    /// <summary>慢连宽限原语（vc86 老板令）：Failed 落账后回 Buffering 原地重试本线（坏票宽限同骨架），
    /// 看门狗窗随 Buffering 重计；预算耗尽返回 false 走既有换源。
    /// vc104 第二刀（老板令「宽限不重启」）：restartPlayer=false 时（看门狗路径）内核还活着——
    /// 不 Transition、不 _player.Play()、不 MarkEndpointStarted，只重计宽限窗续攒，货不扔；
    /// 内核错误路径（restartPlayer=true 默认）内核已死必须重放，照原骨架。</summary>
    private bool TrySlowConnectGrace(string reason, bool restartPlayer = true)
    {
        if (_channel is null || CurrentEndpoint is null || _queue.Count == 0) return false;
        // vc87 进度闸（老板拍板「三方案一次到位」，坏票 6 秒循环病灶根修）：宽限前先补采样进度账——
        // 零存货且 3 秒内零增长=真死（源端即拒），不白等宽限直接走既有换源；
        // 有存货或正在灌（慢灌让路口径与 vc67 同账）才准进宽限，慢而通不切线。
        var nowMs = _clock.NowMs;
        SampleBufferProgress(nowMs);
        bool hasProgress = _watchdogLastBufferedMs > 0
            || (_lastBufferProgressAtMs is { } at && nowMs - at < 3_000);
        if (!hasProgress) return false; // 零进度=真死快切，不吃宽限预算
        if (_slowGraceCount >= SlowConnectGraceBudget) return false; // vc86 预算闸（方案 v2 F2）：封顶 2 次后放行走既有换源，真死不无限原地打转
        _slowGraceCount++;
        SessionTrace.Emit($"[慢连] {DescribeChannel()} 超时先原地宽限重试（{_slowGraceCount}/{SlowConnectGraceBudget}，线 {CurrentLineIndex + 1}/{LineCount}，{reason}）——慢而通不切线");
        if (!restartPlayer)
        {
            // vc104 宽限不重启（看门狗路径）：内核在拉，只重计 Buffering 窗续攒，不扔货不重放
            _bufferingSinceMs = _clock.NowMs;
            SlowGraceStarted?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (_state != PlaybackState.Failed) // 内核报错路径已在 OnPlayerError 落过 Failed，重复落账属非法迁移（矩阵 Failed→Failed）
        {
            Transition(PlaybackState.Failed, $"slow grace {reason}"); // 看门狗路径：Buffering→Failed 落账再回 Buffering，照坏票宽限骨架
        }
        Transition(PlaybackState.Buffering, "slow connect grace");
        MarkEndpointStarted(); // vc87 票账降权：宽限重试也算端点起播
        _player.Play(new PlaybackParams(_channel, CurrentEndpoint, _preferLowLatency));
        ReattachSurface();
        SlowGraceStarted?.Invoke(this, EventArgs.Empty);
        return true;
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

    /// <summary>vc117 后台出声根治令（2026-11-28 老板拍板）：后台冻结旗——壳层 OnPause 置位、OnResume 撤。
    /// 置位期间：看门狗自愈不发起（CheckWatchdog 直落）、换源自救只停不播（TryFailover 短路）——
    /// 后台期间任何路径都不得把播放内核重新拉起来出声。</summary>
    public static volatile bool BackgroundHold;

    private void TryFailover(string reason, bool prepared = false)
    {
        // vc117 后台出声根治：后台冻结期换源自救只停内核+排自愈账，不 Play（防后台出声）；
        // 回前台 OnResume 撤旗后，自愈慢轮/用户重试照常续。
        if (BackgroundHold)
        {
            _player.Stop();
            ScheduleSelfHeal();
            SessionTrace.Emit($"[后台冻结] {DescribeChannel()} 换源自救挂起（{reason}）：后台不重播，回前台续");
            return;
        }
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
        // vc139 先攒后切：预备切换（备源水已攒满，切换瞬间秒出画）不烧线，豁免限速闸但仍记账。
        var nowMs = _clock.NowMs;
        _autoFailoverTimes.RemoveAll(t => nowMs - t >= FailoverThrottleWindow.TotalMilliseconds);
        var throttleCap = AdvancedTuning.FailoverCapPerMin;
        if (!prepared && _autoFailoverTimes.Count >= throttleCap)
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
        _videoStallTimes.Clear(); // vc87：停滞账同口径清零
        _lightErrorTimes.Clear(); // vc82：轻错账同口径只记本线，切线清账
        FailoverCount++;
        _freshTicketRetried = false; // vc69：坏票重试机会每线路一次，切线重记
        _httpStrikeCount = 0; // vc146 刀②修正（全测红实锤）：拒流账只记本线，切线重记——否则首线烧满 2 次后，后续线路永远拿不到原地换票机会
        _ticketRetrySinceMs = null;
        _lastFailoverAtMs = _clock.NowMs; // 预防性介入冷却账起算（老板令 #24）
        Transition(PlaybackState.Buffering, $"failover after {reason}");
        MarkEndpointStarted(); // vc87 票账降权：备源起播打点
        _player.Play(new PlaybackParams(_channel, _queue[next], _preferLowLatency));
        ReattachSurface();
        Failover?.Invoke(this, new FailoverEventArgs(from, _queue[next], FailoverCount, reason));
    }

    /// <summary>自愈起轮索引（vc146 刀③）：跳官方窗内直接从首个备源起轮，不弹回官方乒乓；
    /// 窗外或队列全是官方源时照旧从 0 起（从头慢轮原口径不变）。</summary>
    private int QueueStartIndex()
    {
        if (_clock.NowMs < _officialSkipUntilMs)
        {
            var idx = _queue.FindIndex(s => s.Kind != SourceKind.Web);
            if (idx > 0)
            {
                SessionTrace.Emit($"[官方源拒流] {DescribeChannel()} 跳官方窗内自愈直走备源（线路 {idx + 1}/{_queue.Count} 起）");
                return idx;
            }
        }
        return 0;
    }

    /// <summary>备源水备好（vc139 老板令「先攒后切」）：备源已在缓存攒满门槛水，执行切换——
    /// 切换瞬间内核吃缓存秒满门槛秒出画，不黑屏。三重闸把住口径：
    /// ①只在起播攒流期切（已出画不打断在播，水留着干库快切时用）；
    /// ②备源仍是下一条（已换线/换台即作废，防旧回调误切）；
    /// ③走预备切换路径（豁免限速闸但仍记账——水已备好不烧线）。</summary>
    public void NotifyBackupWaterReady(SourceEndpoint endpoint)
    {
        if (_state != PlaybackState.Buffering) return;
        if (!_player.StartupGateAccumulating) return; // 已出画：不打断在播
        var next = _endpointIndex + 1;
        if (_channel is null || next >= _queue.Count
            || !string.Equals(_queue[next].Url, endpoint.Url, StringComparison.Ordinal))
        {
            return; // 已换线/换台：旧攒水回调作废
        }
        SessionTrace.Emit($"[先攒后切] {DescribeChannel()} 备线水攒满，切换出画（原线路 {CurrentLineIndex + 1}/{LineCount}）");
        TryFailover("backup water ready", prepared: true);
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
