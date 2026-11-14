using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    /// vc53 任务13·双来源合并（收录口径，projects/1377423/goal.md 收录账）：
    /// 多来源同台（同分类+归一台名）只留一档——纯 IP 免解析排前、带防盗头适配源次之、
    /// 同分按收录序（先收录优先，编号稳定）；同台同主机流计 1 家族（183.11/gmxw 双端口径）只收一档。
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
            var usable = MergeDuplicates(_sources.SelectMany(s => s.FetchChannels())
                .Where(c => c.Category == cat && !c.Unavailable).ToList());
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

    /// <summary>
    /// 同台合并：按（分类内）归一台名分组，每组按收录优先级取最优档（收录序保持）。
    /// 同台同主机流自然落在同一归一名组内只计一档（183.11/gmxw 双端计 1 家族口径）。
    /// </summary>
    private static List<Channel> MergeDuplicates(List<Channel> channels)
    {
        var result = new List<Channel>();
        foreach (var group in channels.GroupBy(c => NormalizeName(c.Name)))
        {
            Channel best = group.First();
            foreach (var c in group.Skip(1))
                if (SourceRank(c) > SourceRank(best)) best = c;
            result.Add(best);
        }
        return result;
    }

    /// <summary>台名归一：去分隔/装饰符、统一小写；「+」保留（CCTV-5+ 与 CCTV5 是两台，绝不过合并）；
    /// 「综合/频道/高清/超清/标清/HD」等后缀噪音去除（跨来源对齐）。</summary>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch) || ch == '+') sb.Append(char.ToLowerInvariant(ch));
        foreach (var suffix in new[] { "综合", "频道", "超清", "标清", "高清", "hd" })
            while (sb.Length > suffix.Length && sb.ToString().EndsWith(suffix, StringComparison.Ordinal))
                sb.Length -= suffix.Length;
        return sb.ToString();
    }

    /// <summary>收录优先级（收录账口径）：纯 IP 免解析（8）&gt; 带防盗头适配源（4）&gt; 裸流（2）；同分按收录序。</summary>
    private static int SourceRank(Channel c)
    {
        int score = 2;
        if (Uri.TryCreate(c.StreamUrl, UriKind.Absolute, out var uri)
            && IPAddress.TryParse(uri.Host, out _)) score = 8; // 纯 IP 免解析排前
        else if (!string.IsNullOrEmpty(c.Referer) || !string.IsNullOrEmpty(c.UserAgent)) score = 4; // 官方适配源次之
        return score;
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
