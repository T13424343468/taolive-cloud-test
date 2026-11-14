using System;
using System.Collections.Generic;

namespace TaoLive.BLL;

/// <summary>
/// A 类 P1·选项合并（缺键才补，纯函数可测；宿主层与测试共用）：
/// 现有表为 <c>:key=value</c> 字符串列表；新取的键值对并入——新表已给的键以新值为准，
/// 现有表里新表没给的键（典型：http-referrer / http-user-agent 请求头）原样保留。
/// 空现有表全按新表；键名解析兼容 VLC 前导冒号；无等号条目按纯键保留。
/// </summary>
public static class MediaOptionMerge
{
    public static List<string> Merge(IReadOnlyList<string> existing, IReadOnlyList<(string Key, string Value)> fresh)
    {
        // 先把现有表解析为 (原始串, 键) 保序账本
        var keys = new List<string>(existing.Count);
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in existing)
        {
            var body = raw.StartsWith(':') ? raw[1..] : raw;
            var eq = body.IndexOf('=');
            var key = eq < 0 ? body : body[..eq];
            if (!byKey.ContainsKey(key)) keys.Add(key); // 首现为准，不重复收
            byKey[key] = raw;
        }
        // 新表并入：已给的键更新值，未给的键最后补上
        var result = new List<string>(existing.Count + fresh.Count);
        foreach (var (k, v) in fresh)
        {
            if (byKey.ContainsKey(k)) byKey[k] = $":{k}={v}";
            else { keys.Add(k); byKey[k] = $":{k}={v}"; }
        }
        foreach (var key in keys) result.Add(byKey[key]);
        return result;
    }
}
