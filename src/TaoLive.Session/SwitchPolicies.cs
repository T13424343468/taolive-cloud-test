using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 切台清屏与亮牌口径（v1.0.10 修复⑦逐字移植，素材沿用令）：撤旧播放器到新流首帧之间，
/// GPU 残留旧缓冲可能被当作画面（彩色竖条花屏）。口径：换台即铺干净底色遮住旧内容；
/// 新流首帧到齐才亮；同台反复花屏（解码规格变更类）改走备用载体重接。
/// 2.0 落点：壳层订阅 SessionStateChanged——Buffering 即铺底色遮罩，Playing（首帧）才撤。
/// </summary>
public static class SwitchRevealPolicy
{
    /// <summary>换台瞬间应立即铺底色（撤旧画面的同时），用户不见残留。</summary>
    public static bool ShouldVeilOnSwitch(bool frameEverShown) => true;

    /// <summary>新流首帧到齐才撤底色亮画面（与「正在连接」亮牌同步撤，不再单独早亮）。</summary>
    public static bool ShouldReveal(bool newStreamFirstFrame) => newStreamFirstFrame;

    /// <summary>换台后短期内反复花屏（计数达阈值）：判定解码规格变更路线不可靠，走备用载体重接。</summary>
    public const int CorruptionLimit = 2;

    public static bool ShouldFallbackCarrier(int corruptionCount) => corruptionCount >= CorruptionLimit;
}

/// <summary>
/// 换台提速·先出画面（v1.0.10 修复⑥逐字移植）：同台多档可用时先连最快出画面的档位（带宽优先，
/// 低带宽先亮），画面亮后后台无感升到更高档；升级失败留在当前档，绝不为升档搞挂画面。
/// 2.0 落点：备源排序入口（会话层选首源/壳层手动换线路共用）。
/// </summary>
public static class FastStartPlanner
{
    /// <summary>变体按带宽升序=出画面最快次序（同带宽取矮的先出）；空表按原样。</summary>
    public static IReadOnlyList<(int Height, long Bandwidth)> StartOrder(
        IReadOnlyList<(int Height, long Bandwidth)> variants)
    {
        if (variants.Count <= 1) return variants;
        return variants.OrderBy(v => v.Bandwidth).ThenBy(v => v.Height).ToList();
    }

    /// <summary>首档（先出画面档）。</summary>
    public static (int Height, long Bandwidth)? First(IReadOnlyList<(int Height, long Bandwidth)> variants)
    {
        if (variants.Count == 0) return null;
        return StartOrder(variants)[0];
    }

    /// <summary>画面亮后的无感升级目标：最高档；已是最高档返回 null（不折腾）。</summary>
    public static (int Height, long Bandwidth)? UpgradeTarget(
        IReadOnlyList<(int Height, long Bandwidth)> variants, int currentHeight)
    {
        if (variants.Count <= 1) return null;
        var top = variants.OrderByDescending(v => v.Height).First();
        if (top.Height > currentHeight) return top;
        return null;
    }

    /// <summary>升级失败或网络撑不住：留在当前档（不回退重试到崩）。</summary>
    public static (int Height, long Bandwidth) OnUpgradeFailed(
        IReadOnlyList<(int Height, long Bandwidth)> variants, int currentHeight) =>
        variants.FirstOrDefault(v => v.Height == currentHeight);
}

/// <summary>
/// 换台去抖（v1.0.13 老板 15:38 拍板逐字移植）：遥控器上下键连按不真切——
/// 按键即时更新台号显示，停稳 WindowMs 后才真正起流；中间台不发真实连接，不打断、不误触发重连。
/// 目标台以最后一次按键为准。2.0 落点：壳层按键事件先进去抖器，停稳后才调 SessionManager.SwitchTo。
/// </summary>
public sealed class SwitchDebouncer
{
    /// <summary>去抖窗口：连续快按的静止等待时长（毫秒）。单次按一下同样等足窗口，统一手感。</summary>
    public const int WindowMs = 400;

    private long _lastTapMs;
    private string _targetKey = string.Empty;

    /// <summary>记录一次按键；目标台以最后一次按键为准。</summary>
    public void Tap(long nowMs, string channelKey)
    {
        _lastTapMs = nowMs;
        _targetKey = channelKey ?? string.Empty;
    }

    /// <summary>窗口是否已停稳（距最后一次按键 ≥ WindowMs）。</summary>
    public bool IsDue(long nowMs) => nowMs - _lastTapMs >= WindowMs;

    /// <summary>当前目标台键（最后一次按键的目标）。</summary>
    public string Target => _targetKey;

    /// <summary>是否有待执行目标。</summary>
    public bool HasTarget => _lastTapMs > 0;

    /// <summary>清空（目标台已起流或作废）。</summary>
    public void Clear()
    {
        _lastTapMs = 0;
        _targetKey = string.Empty;
    }
}

/// <summary>
/// 换台 OSD 提示文案口径（2.0 W8，延续一期「正在连接」亮牌/台号浮层文案）：
/// 纯函数产出 OSD 文案，壳层只管显示与消失时机，不掺口径。
/// </summary>
public static class SwitchHud
{
    /// <summary>换台瞬间 OSD：台号+台名（编号 0 表示无编号台，只显台名）。</summary>
    public static string ChannelChanged(int channelNumber, string channelName) =>
        channelNumber > 0 ? $"{channelNumber}  {channelName}" : channelName;

    /// <summary>起播中 OSD：换台后到首帧之间。文案=1.x 特征关口径（坑40 老板可见特征「正在换信号」，方案书 v11/v65 定谳随素材沿用令进包，Toast/OSD 自动消隐 ≤2 秒语义同致）。</summary>
    public const string Connecting = "正在换信号";

    /// <summary>手动换线路 OSD：线路序号从 1 计。</summary>
    public static string LineSwitching(int lineIndex) => $"切换到线路 {lineIndex + 1}";

    /// <summary>重试退避中 OSD（收口批③）：距下次放行重试还差的秒数（向上取整）。</summary>
    public static string RetryBackoff(int seconds) => $"重试太频繁，约 {seconds} 秒后再试";

    /// <summary>全部线路耗尽 OSD（与壳层 Failed 提示同一口径）。</summary>
    public const string AllLinesFailed = "信号中断，按 OK 重试";
}
