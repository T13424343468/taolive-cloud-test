using System;
using System.IO;

namespace TaoLive.BLL;

/// <summary>
/// A 类第 7 项·设备取票身份持久化：央视频 guid 首次生成后落盘，进程重启复用同一身份。
/// 背景：随机 guid 每次启动翻新，在「IP 被 CDN 画像风控」的实锤结论下持续扰动画像；
/// v4.0.13 已同口径持久化（老板拍板并入）。纯逻辑可测：路径由调用方注入，缺省行为退化为随机。
/// </summary>
public static class DeviceIdentity
{
    private static string? _dir;              // 应用私有目录（由 MainActivity 启动时注入；null=不落盘）
    private static readonly object _gate = new();
    private static string? _cached;

    /// <summary>测试与运行注入点：应用私有目录；传 null 关闭持久化（退化为随机，不抛错）。</summary>
    public static void Attach(string? dir) { lock (_gate) { _dir = dir; _cached = null; } }

    /// <summary>取稳定 guid：已有落盘文件直接复用；没有则生成并写盘；无目录注入时现场随机（与旧行为一致）。</summary>
    public static string GetGuid()
    {
        lock (_gate)
        {
            if (_cached != null) return _cached;
            var dir = _dir;
            if (string.IsNullOrEmpty(dir)) return _cached = Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "device_guid.txt");
                if (File.Exists(path))
                {
                    var v = File.ReadAllText(path).Trim();
                    if (v.Length is 32 && v.All(Uri.IsHexDigit)) return _cached = v; // 旧文件防脏：仅收 32 位十六进制
                }
                var guid = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, guid);        // 原子性可接受：单次小文件，损坏按 32 位校验丢弃重生成
                return _cached = guid;
            }
            catch { return _cached = Guid.NewGuid().ToString("N"); } // 落盘失败不挡起播：退回随机
        }
    }

    /// <summary>测试用：重置（连同落盘文件路径）。</summary>
    internal static void Reset() { lock (_gate) { _dir = null; _cached = null; } }
}
