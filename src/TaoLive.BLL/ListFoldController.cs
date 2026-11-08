using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoLive.BLL;

/// <summary>
/// 列表页一键展开/折叠（v1.0.10 修复⑪）：顶部总开关行，全收显示「全部展开」、
/// 全开显示「全部收起」；打开列表默认习惯不变（收藏组+当前台所在组自动展开），
/// 总开关只管手动触发；「无信号」组同样参与收放。
/// </summary>
public class ListFoldController
{
    private readonly HashSet<string> _collapsed = new();

    /// <summary>默认展开习惯：收藏组与当前台所在组展开，其余折叠（打开列表时的初始口径）。</summary>
    public static IReadOnlyList<string> DefaultExpanded(
        IEnumerable<string> allGroups, string? favoriteGroup, string? currentGroup)
    {
        var set = new List<string>();
        foreach (var g in allGroups)
            if (g == favoriteGroup || g == currentGroup) set.Add(g);
        return set;
    }

    /// <summary>总开关：全开→全部收起；有任何折叠→全部展开。返回收起集合（空=全开）。</summary>
    public IReadOnlyList<string> ToggleAll(IEnumerable<string> allGroups)
    {
        var groups = allGroups.Distinct().ToList();
        if (_collapsed.Count == 0)
        {
            _collapsed.Clear();
            foreach (var g in groups) _collapsed.Add(g);
        }
        else
        {
            _collapsed.Clear(); // 有折叠（含全收）→ 一键全展开
        }
        return _collapsed.ToList();
    }

    /// <summary>单组收放（原有点头逻辑保持不变）。</summary>
    public void ToggleGroup(string group)
    {
        if (!_collapsed.Remove(group)) _collapsed.Add(group);
    }

    public bool IsCollapsed(string group) => _collapsed.Contains(group);

    /// <summary>总开关行文案：全开=「全部收起」，否则「全部展开」（含全收与部分折叠）。</summary>
    public static string ToggleLabel(int collapsedCount) => collapsedCount == 0 ? "全部收起" : "全部展开";
}
