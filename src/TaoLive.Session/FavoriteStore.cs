using System;

using System.Collections.Generic;

using System.Linq;

using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 收藏顺序存储（vc53 ChannelCatalog.FavoriteStore 逐成员移植，口径以 /workspace vc53 基线原文为准）：
/// 按收藏顺序存频道号列表；排序模式（v1.0.8 第14项用户拍板）用 MoveUp/MoveDown/MoveBy/MoveToTop 挪位，
/// Serialize/Restore 逗号编号串持久化，坏段忽略不因脏数据崩溃。
/// </summary>
public class FavoriteStore
{
    private readonly List<int> _numbers = new(); // 按收藏顺序
    public IReadOnlyList<int> Numbers => _numbers;
    public bool IsFavorite(int channelNumber) => _numbers.Contains(channelNumber);
    public void Toggle(int channelNumber)
    {
        if (IsFavorite(channelNumber)) _numbers.Remove(channelNumber);
        else _numbers.Add(channelNumber);
    }
    public void MoveUp(int channelNumber)
    {
        int i = _numbers.IndexOf(channelNumber);
        if (i > 0) (_numbers[i - 1], _numbers[i]) = (_numbers[i], _numbers[i - 1]);
    }
    public void MoveDown(int channelNumber)
    {
        int i = _numbers.IndexOf(channelNumber);
        if (i >= 0 && i < _numbers.Count - 1) (_numbers[i + 1], _numbers[i]) = (_numbers[i], _numbers[i + 1]);
    }

    /// <summary>按步数挪动（v1.0.8 排序模式：左右键按屏快速挪，步数=一屏行数；正数向下）。</summary>
    public void MoveBy(int channelNumber, int steps)
    {
        int i = _numbers.IndexOf(channelNumber);
        if (i < 0 || steps == 0) return;
        int target = Math.Clamp(i + steps, 0, _numbers.Count - 1);
        if (target == i) return;
        var item = _numbers[i];
        _numbers.RemoveAt(i);
        _numbers.Insert(target, item);
    }

    /// <summary>一键置顶（v1.0.8 排序模式：信息键）。</summary>
    public void MoveToTop(int channelNumber)
    {
        if (!_numbers.Remove(channelNumber)) return;
        _numbers.Insert(0, channelNumber);
    }

    /// <summary>持久化（v1.0.8 第14项：顺序跨启动记忆）：逗号分隔编号串。</summary>
    public string Serialize() => string.Join(",", _numbers);

    /// <summary>恢复持久化顺序；坏段忽略（容错优先，不因脏数据崩溃）。</summary>
    public void Restore(string? state)
    {
        _numbers.Clear();
        if (string.IsNullOrWhiteSpace(state)) return;
        foreach (var part in state.Split(','))
            if (int.TryParse(part.Trim(), out var n) && !_numbers.Contains(n))
                _numbers.Add(n);
    }
}
