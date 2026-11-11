using System;

namespace TaoLive.BLL;

/// <summary>
/// 退出弹窗双按钮焦点导航（v1.0.10 修复④断言口径）：
/// - 弹出默认焦点落「取消（继续看）」，防误退；
/// - 左右键在「退出」与「取消（继续看）」间来回切换，到边不动；
/// - 返回键=继续看（取消），与默认焦点同一出口。
/// </summary>
public enum ExitButton { Cancel, Exit }

public static class ExitDialogFocus
{
    public static ExitButton Initial => ExitButton.Cancel; // 默认焦点：取消（继续看）

    /// <summary>左右键移动：界面上「取消」在左、「退出」在右；到边不动（不环绕，防误触退出）。</summary>
    public static ExitButton Move(ExitButton current, int dx)
    {
        if (dx > 0) return current == ExitButton.Cancel ? ExitButton.Exit : ExitButton.Exit;
        if (dx < 0) return current == ExitButton.Exit ? ExitButton.Cancel : ExitButton.Cancel;
        return current;
    }

    /// <summary>确认键执行当前焦点按钮；返回键按取消处理（继续看）。</summary>
    public static ExitButton Confirm(ExitButton current) => current;

    public static ExitButton Back() => ExitButton.Cancel;
}
