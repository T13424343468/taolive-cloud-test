using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 自愈5·网络环境变化：断网监测与恢复。连接失败连续达阈值判「断网」（静默等待），
/// 任一次成功即恢复在线（UI 收到恢复沿 → 自动重连当前台 + 预检全量重测，静默自愈）。
/// 总镜子 R1 制度镜红①收口：本文件 SourceFailover/ClockGuard/MemoryWatcher/ResourceProfile/KeyAlias
/// 全仓生产+测试零引用，成建制清除；NetGuard 为生产在用（MainActivity 断网监测接预检回灌），单独保留。
/// </summary>
public class NetGuard
{
    private int _failStreak;

    /// <summary>连续失败多少次判为断网：接通自愈门槛旋钮（老板 2026-11-17 拍板，假旋钮接通第 1 位）——
    /// 判别现读 AdvancedTuning.SelfHealFailGate（出厂 3，原常量 4 为改动前口径），每拍现取，改档即时生效。
    /// 预检/取票各自回灌，避免单次抖动误判。</summary>
    public static int OfflineAfterFails => AdvancedTuning.SelfHealFailGate;

    public bool Offline { get; private set; }

    /// <summary>失败回灌。返回当前是否断网态。</summary>
    public bool OnFailure()
    {
        _failStreak++;
        if (!Offline && _failStreak >= OfflineAfterFails) Offline = true;
        return Offline;
    }

    /// <summary>成功回灌。返回恢复沿（true = 刚从断网恢复，调用方触发重连与预检重测）。</summary>
    public bool OnSuccess()
    {
        bool wasOffline = Offline;
        Offline = false;
        _failStreak = 0;
        return wasOffline;
    }
}
