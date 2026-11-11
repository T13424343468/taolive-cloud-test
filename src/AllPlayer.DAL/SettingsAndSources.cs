using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AllPlayer.Model;

namespace AllPlayer.DAL
{
    /// <summary>数据层: 设置文件的读写 (JSON, 各端注入目录)</summary>
    public static class SettingsFile
    {
        public static string FilePath(string baseDir) =>
            Path.Combine(string.IsNullOrWhiteSpace(baseDir)
                ? AppContext.BaseDirectory
                : baseDir, "settings.json");

        public static AppSettings Load(string baseDir)
        {
            try
            {
                var p = FilePath(baseDir);
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var p = FilePath(AppSettings.BaseDir);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(settings,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>数据层: 文件系统视频源 (目录递归扫描, 跳过隐藏/系统/符号链接)</summary>
    public static class FileSystemVideoSource
    {
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
            ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp", ".ogv", ".rmvb", ".vob",
        };

        /// <summary>递归扫描目录, 每找到一个视频回调一次 (同步阻塞, 调用方放后台线程)</summary>
        public static int Scan(IEnumerable<string> folders, ISet<string> excluded,
            Action<VideoItem> onFound, Action<string> onProgress, CancellationToken ct)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            foreach (var root in folders.Where(Directory.Exists).Distinct())
            {
                if (ct.IsCancellationRequested) break;
                onProgress?.Invoke($"扫描中: {root}");
                try
                {
                    var opt = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                    };
                    var noMediaDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opt))
                    {
                        ct.ThrowIfCancellationRequested();
                        // 藏起来的视频绝不进普通列表: 点开头目录里的、点开头文件、.nomedia 目录里的都算隐藏。
                        if (PathHiding.IsHiddenPath(f.FullName)) continue;
                        var dir = f.DirectoryName ?? "";
                        if (!noMediaDirs.Contains(dir))
                        {
                            try { if (File.Exists(Path.Combine(dir, ".nomedia"))) noMediaDirs.Add(dir); }
                            catch { }
                        }
                        if (noMediaDirs.Contains(dir)) continue;
                        if (f.Name.StartsWith(".", StringComparison.Ordinal)) continue; // 点开头按隐藏处理 (Linux/Android 无 Hidden 属性)
                        if (!VideoExtensions.Contains(f.Extension.ToLowerInvariant())) continue;
                        if (found.Contains(f.FullName)) continue;
                        if (excluded.Contains(f.FullName)) continue;
                        found.Add(f.FullName);
                        total++;
                        onFound?.Invoke(new VideoItem
                        {
                            Path = f.FullName,
                            SizeBytes = f.Length,
                            Modified = f.LastWriteTime,
                        });
                    }
                }
                catch { /* 单个目录失败不影响整体 */ }
            }
            onProgress?.Invoke($"扫描完成, 共 {total} 个视频");
            return total;
        }
    }
}
