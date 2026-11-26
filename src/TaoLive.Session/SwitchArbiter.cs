using System;

namespace TaoLive.Session;

/// <summary>切换意图（蓝本 v2.5 三原语：保命 / 对齐 / 回退跳）：枚举值即优先级，大者压小者。</summary>
public enum SwitchIntentKind
{
    /// <summary>回主源（对齐切回，同向升档口径吃同一冷却）。</summary>
    ReturnPrimary = 0,
    /// <summary>升档对齐切（备稳养主，同向升档冷却）。</summary>
    AlignUpgrade = 1,
    /// <summary>降档软着陆（主粮将尽提前对齐切备源，无前置闸）。</summary>
    SoftLanding = 2,
    /// <summary>保命（画面将断必须马上切，最高优先级直通）。</summary>
    LifeSaving = 3,
}

/// <summary>
/// 切换仲裁器（vc85 蓝本 v2.5，预防性硬切升级为「先裁决后动作」）：
/// - Submit：意图按优先级覆盖挂账（不排队），LifeSaving 直通；返回是否放行。
/// - 三原语之「对齐切」：SoftLanding/AlignUpgrade/ReturnPrimary 走裁决——同向升档（含回主源）
///   冷却 5 分钟防横跳；逃生口账：1 小时窗内升档失败满 3 次 → 主源休眠 60 分钟（双绿复位）。
/// - 三原语之「回退跳」：TryRequestAutoRewind 防振铃 30 秒一次。
/// - 挂账超 3 秒无人消费即 SweepPending 自动放行（裁决滞留不阻塞后续）。
/// - CancelAll：换台即作废挂账（动作层已无意义）；逃生口/休眠是源健康账，不随换台清。
/// 线程口径：全部方法锁内完成（调用方=主线程泵与内核回调两路，账面不撕裂即可）。
/// </summary>
public sealed class SwitchArbiter
{
    /// <summary>裁决超时（毫秒）：挂账超此时长无人消费即自动放行清账。</summary>
    public const int DecisionTimeoutMs = 3000;

    /// <summary>同向升档冷却（蓝本 v2.5：同向升档 5 分钟）：升档/回主源放行后 5 分钟内同类意图不再放行。</summary>
    public static readonly TimeSpan AlignUpgradeCooldown = TimeSpan.FromMinutes(5);

    /// <summary>逃生口观察窗：升档失败账只认此窗内（1 小时）。</summary>
    public static readonly TimeSpan EscapeWindow = TimeSpan.FromHours(1);

    /// <summary>逃生口失败上限：窗内满此次数 → 主源休眠。</summary>
    public const int EscapeFailLimit = 3;

    /// <summary>主源休眠时长：触发逃生口后冷却此时长，期内回主源一律拒绝。</summary>
    public static readonly TimeSpan Dormancy = TimeSpan.FromMinutes(60);

    /// <summary>回退跳防振铃：30 秒内至多放行一次自动回退。</summary>
    public static readonly TimeSpan RewindAntiRing = TimeSpan.FromSeconds(30);

    /// <summary>升档后多长时间内再断计入逃生口账（蓝本口径：升级后 60s 内再断=升档失败）。</summary>
    public static readonly TimeSpan UpgradeFailWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Func<long> _monoMs;

    private SwitchIntentKind _pendingKind = (SwitchIntentKind)(-1);
    private long _pendingAtMs;
    private long _lastAlignAtMs = long.MinValue; // 同向升档最近放行时刻（单调 ms）
    private long _escapeWindowStartMs = long.MinValue;
    private int _escapeFails;
    private long _dormantUntilMs = long.MinValue;
    private long _lastRewindAtMs = long.MinValue;

    public SwitchArbiter(Func<long>? monoMs = null)
    {
        _monoMs = monoMs ?? (() => Environment.TickCount64 & 0x7FFFFFFFFFFFFFFF);
    }

    /// <summary>主源是否休眠中（逃生口已触发）：休眠期内升档/回主源拒绝，双绿复位后恢复。</summary>
    public bool IsPrimaryDormant
    {
        get
        {
            lock (_gate) return _monoMs() < _dormantUntilMs;
        }
    }

    /// <summary>逃生口失败账（诊断/测试观测用）。</summary>
    public int EscapeFailCount
    {
        get
        {
            lock (_gate) return _escapeFails;
        }
    }

    /// <summary>
    /// 提交切换意图：按优先级覆盖挂账（不排队），返回是否放行。
    /// 放行后动作方须调 <see cref="Consume"/> 销账；未消费的挂账由 SweepPending 超时清。
    /// </summary>
    public bool Submit(SwitchIntentKind kind)
    {
        lock (_gate)
        {
            long now = _monoMs();
            bool hasPending = (int)_pendingKind >= 0;
            if (hasPending && (int)kind < (int)_pendingKind)
            {
                return false; // 低优先级让位在途高优挂账：不排队
            }
            _pendingKind = kind;
            _pendingAtMs = now;
            if (kind == SwitchIntentKind.LifeSaving)
            {
                return true; // 保命直通：任何状态放行
            }
            if (kind == SwitchIntentKind.SoftLanding)
            {
                return true; // 降档软着陆无前置闸（主粮将尽时不赌等）
            }
            // AlignUpgrade / ReturnPrimary：同向升档双闸——休眠期拒绝 + 冷却窗拒绝
            if (now < _dormantUntilMs)
            {
                _pendingKind = (SwitchIntentKind)(-1); // 拒绝即销账：滞留挂账会挡住后续更高优
                return false;
            }
            // 冷却判据（首拍特护）：_lastAlignAtMs 未落过账（long.MinValue）直接放行，
            // 不做 now - MinValue 减法——有符号回绕会把「从未对齐」算成「刚对齐过」，首拍恒拒（统一验证轮自查实雷）
            if (_lastAlignAtMs != long.MinValue && now - _lastAlignAtMs < AlignUpgradeCooldown.TotalMilliseconds)
            {
                _pendingKind = (SwitchIntentKind)(-1);
                return false;
            }
            _lastAlignAtMs = now; // 同向冷却自本拍起算
            return true;
        }
    }

    /// <summary>动作方执行完毕销账（与 Submit 配对；幂等无害）。</summary>
    public void Consume()
    {
        lock (_gate) _pendingKind = (SwitchIntentKind)(-1);
    }

    /// <summary>裁决超时泵：挂账超 3 秒无人消费即自动放行清账（管理器 Tick 驱动）。</summary>
    public void SweepPending()
    {
        lock (_gate)
        {
            if ((int)_pendingKind < 0) return;
            if (_monoMs() - _pendingAtMs < DecisionTimeoutMs) return;
            _pendingKind = (SwitchIntentKind)(-1);
        }
    }

    /// <summary>换台作废：清挂账（动作层已无意义）；逃生口/休眠是源账不清。</summary>
    public void CancelAll()
    {
        lock (_gate) _pendingKind = (SwitchIntentKind)(-1);
    }

    /// <summary>
    /// 升档结果回账：升级后 <see cref="UpgradeFailWindow"/> 内再断=false 计入逃生口账；
    /// 窗内满 <see cref="EscapeFailLimit"/> 次 → 主源休眠 <see cref="Dormancy"/>。
    /// true=回主源站住（回后 5 分钟无再断），逃生口账清零。
    /// </summary>
    public void ReportOutcome(bool success)
    {
        lock (_gate)
        {
            long now = _monoMs();
            if (success)
            {
                _escapeFails = 0;
                return;
            }
            if (_lastAlignAtMs == long.MinValue
                || now - _lastAlignAtMs > UpgradeFailWindow.TotalMilliseconds)
            {
                return; // 与升档无关的断流：不冤枉逃生口账（从未对齐过更不相干）
            }
            if (_escapeWindowStartMs == long.MinValue || now - _escapeWindowStartMs > EscapeWindow.TotalMilliseconds)
            {
                _escapeWindowStartMs = now; // 新窗起算
                _escapeFails = 0;
            }
            _escapeFails++;
            if (_escapeFails >= EscapeFailLimit)
            {
                _dormantUntilMs = now + (long)Dormancy.TotalMilliseconds;
                SessionTrace.Emit($"[逃生口] 主源 1 小时内升档失败满 {EscapeFailLimit} 次，休眠 {Dormancy.TotalMinutes:0} 分钟（双绿复位才提前唤醒）");
            }
        }
    }

    /// <summary>双绿复位（探活绿 + 偷拉池有主源存货，管理器泵判）：休眠提前解除、逃生口账清零。</summary>
    public void ResetDormancy()
    {
        lock (_gate)
        {
            _dormantUntilMs = long.MinValue;
            _escapeFails = 0;
        }
    }

    /// <summary>申请一次自动回退跳（微卡修法①）：防振铃 30 秒内至多放行一次，返回是否放行。</summary>
    public bool TryRequestAutoRewind()
    {
        lock (_gate)
        {
            long now = _monoMs();
            if (_lastRewindAtMs != long.MinValue && now - _lastRewindAtMs < RewindAntiRing.TotalMilliseconds)
            {
                return false;
            }
            _lastRewindAtMs = now;
            return true;
        }
    }
}
