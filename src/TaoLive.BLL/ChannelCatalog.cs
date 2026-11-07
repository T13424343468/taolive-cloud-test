using System;
using System.Collections.Generic;
using System.Linq;
using TaoLive.Model;

namespace TaoLive.BLL;

/// <summary>源适配器接口：一路官方源 = 一个实现。摸底数据回填到各实现。</summary>
public interface IChannelSource
{
    string SourceId { get; }
    List<Channel> FetchChannels();
}

/// <summary>取流器见 StreamResolver.cs（换票/签名收口 + 预连接缓存）。</summary>

/// <summary>频道目录：内置台单 + 导入源合并、编号分配、分类分组、收藏置顶。</summary>
public class ChannelCatalog
{
    private readonly List<IChannelSource> _sources = new();
    private readonly FavoriteStore _favorites;

    public ChannelCatalog(FavoriteStore favorites) => _favorites = favorites;

    public void RegisterSource(IChannelSource src) => _sources.Add(src);

    public IReadOnlyList<CategoryGroup> Groups { get; private set; } = new List<CategoryGroup>();
    public IReadOnlyList<Channel> All { get; private set; } = new List<Channel>();

    /// <summary>
    /// 重建目录：六大分类依序编号（接不进的台不占号、单独归档供界面说明），
    /// 自有源接在最后顺次编。编号随台单版本固化，用户记住编号永久直达。
    /// </summary>
    public void Rebuild()
    {
        var all = new List<Channel>();
        var groups = new List<CategoryGroup>();
        int number = 1;
        var titles = new Dictionary<ChannelCategory, string>
        {
            [ChannelCategory.Cctv] = "央视", [ChannelCategory.Cgtn] = "CGTN",
            [ChannelCategory.Special] = "专项", [ChannelCategory.Provincial] = "省台",
            [ChannelCategory.City] = "城市台", [ChannelCategory.UserSource] = "自有源",
        };
        foreach (var cat in titles.Keys)
        {
            var usable = _sources.SelectMany(s => s.FetchChannels())
                .Where(c => c.Category == cat && !c.Unavailable).ToList();
            foreach (var ch in usable) ch.Number = number++;
            all.AddRange(usable);
            groups.Add(new CategoryGroup { Category = cat, Title = titles[cat], Channels = usable });
        }
        var dead = _sources.SelectMany(s => s.FetchChannels())
            .Where(c => c.Unavailable).ToList();
        foreach (var ch in dead) ch.Number = 0;
        UnavailableChannels = dead;
        All = all; Groups = groups;
        FavoriteTop = _favorites.TopGroup(all);
    }

    public List<Channel> UnavailableChannels { get; private set; } = new();
    public List<Channel> FavoriteTop { get; private set; } = new();

    public Channel? ByNumber(int number) => All.FirstOrDefault(c => c.Number == number);
}

/// <summary>收藏：置顶独立组 + 手动排序（硬性验收项）。</summary>
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
    public List<Channel> TopGroup(List<Channel> all)
    {
        var result = new List<Channel>();
        foreach (var n in _numbers)
        {
            var ch = all.FirstOrDefault(c => c.Number == n);
            if (ch != null) result.Add(ch);
        }
        return result;
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
