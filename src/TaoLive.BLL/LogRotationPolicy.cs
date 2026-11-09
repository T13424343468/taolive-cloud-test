using System;
using System.IO;

namespace TaoLive.BLL;

/// <summary>
/// 崩溃日志轮转策略（v1.0.20 ⑨，纯逻辑）：日志超上限挪一代（.1），至多两代——
/// 约 2MB 封顶，绝不限时涨到占满存储。轮转失败静默吞掉，绝不影响日志主流程。
/// </summary>
public static class LogRotationPolicy
{
    /// <summary>单文件上限（字节）：1MB。</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>保留代数：主档 + 1 代旧档（至多两代约 2MB）。</summary>
    public const int Generations = 2;

    /// <summary>是否需要轮转：主档超过上限即轮转。</summary>
    public static bool ShouldRotate(long bytes) => bytes > MaxBytes;

    /// <summary>轮转代文件名：主档 taolive_crash.log → 代 taolive_crash.log.1。</summary>
    public static string RotatedName(string mainName) => mainName + ".1";

    /// <summary>
    /// 轮转执行器（v1.0.20 ⑨ 接线，CrashNet 每次写日志前调用）：
    /// 主档存在且超上限时，删旧代、主档挪为代文件；未超限或主档不存在不动。
    /// 任一 IO 失败静默返回 false，不抛不误主流程。返回真表示发生了轮转。
    /// </summary>
    public static bool RotateIfNeeded(string mainPath)
    {
        try
        {
            var fi = new FileInfo(mainPath);
            if (!fi.Exists || !ShouldRotate(fi.Length)) return false;
            var mainName = Path.GetFileName(mainPath);
            var rotatedPath = Path.Combine(Path.GetDirectoryName(mainPath)!, RotatedName(mainName));
            if (File.Exists(rotatedPath)) File.Delete(rotatedPath);
            File.Move(mainPath, rotatedPath);
            return true;
        }
        catch
        {
            return false; // 轮转失败静默：日志主流程照写，下次写入再试轮转
        }
    }
}
