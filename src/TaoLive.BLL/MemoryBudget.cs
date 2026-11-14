using System;

namespace TaoLive.BLL;

/// <summary>
/// 统一内存预算（v1.0.22 用户令）：全部内存档判断共用一把尺子——
/// 预算 = 当下可用内存 − 1G（只给系统留 1G 余量），够用才放开，不够才缩。
/// - ReserveBytes：系统保留余量（1G），任何功能占用都不得越过；
/// - BudgetBytes：预算字节数；availBytes≤0 视为「读不到/未知」，返回 0（调用方按各自口径保守兜底）；
/// - Affords：预算是否够覆盖某项占用（neededBytes，含安全系数由调用方给定）。
/// 纯函数可测，不依赖平台 API（可用内存由 UI 侧注入）。
/// </summary>
public static class MemoryBudget
{
    /// <summary>系统保留余量：1G（用户令 2026-11-09：只留 1G 余量空间）。</summary>
    public const long ReserveBytes = 1024L * 1024 * 1024;

    /// <summary>预算 = 当下可用内存 − 1G；未知（≤0）返回 0 不给预算。</summary>
    public static long BudgetBytes(long availBytes) =>
        availBytes <= 0 ? 0 : availBytes - ReserveBytes;

    /// <summary>预算够不够覆盖 neededBytes 的占用（availBytes≤0 一律不够，未知不冒险）。</summary>
    public static bool Affords(long availBytes, long neededBytes) =>
        availBytes > 0 && BudgetBytes(availBytes) >= neededBytes;
}
