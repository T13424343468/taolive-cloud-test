using System;
using System.Collections.Generic;
using System.Linq;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>
/// 数字键直达控制器：按 0-9 累积输入，右下角大号数字回显，
/// 停 1 秒自动跳台；左键退格。频道号与台单编号一致（硬约定）。
/// </summary>
public class ChannelNumberController
{
    private readonly ChannelCatalog _catalog;
    private readonly Action<int> _jumpTo;      // 跳台回调（编号）
    private readonly Action<string> _echo;     // 回显回调（如 "0-0-1"）
    private readonly System.Timers.Timer _commitTimer = new(1000) { AutoReset = false };
    private string _buffer = "";

    public ChannelNumberController(ChannelCatalog catalog, Action<int> jumpTo, Action<string> echo)
    {
        _catalog = catalog; _jumpTo = jumpTo; _echo = echo;
        _commitTimer.Elapsed += (_, _) => Commit();
    }

    /// <summary>遥控器数字键按下（keycode 7~16 → 数字 0~9）。</summary>
    public void OnDigit(int digit)
    {
        if (digit < 0 || digit > 9) return;
        _buffer = (_buffer + digit) switch
        {
            var b when b.Length > 3 => b[^3..],   // 超三位滚动
            var b => b,
        };
        _echo(string.Join("-", _buffer.Select(c => c)));
        _commitTimer.Stop(); _commitTimer.Start();
        if (_catalog.All.All(c => c.Number.ToString() != _buffer))
            return; // 无此号，等下一位或超时
        if (_buffer.Length == 3 || _buffer.Length > _catalog.All.Max(c => c.Number).ToString().Length)
            Commit();
    }

    /// <summary>左键退格。</summary>
    public void OnBackspace()
    {
        if (_buffer.Length == 0) return;
        _buffer = _buffer[..^1];
        _echo(string.Join("-", _buffer.Select(c => c)));
        if (_buffer.Length == 0) _commitTimer.Stop();
    }

    private void Commit()
    {
        var num = int.TryParse(_buffer, out var n) ? n : -1;
        _buffer = "";
        _echo("");
        if (num > 0 && _catalog.All.Any(c => c.Number == num)) _jumpTo(num);
    }
}
