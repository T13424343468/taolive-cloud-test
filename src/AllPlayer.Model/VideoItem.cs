using System;
using System.Collections.Generic;

namespace AllPlayer.Model
{
    /// <summary>视频文件条目 (主页媒体库列表项)</summary>
    public class VideoItem
    {
        public string Path { get; set; } = "";
        /// <summary>列表显示名 (v3.10): 磁力条目没有真实路径, 检测结果直接给出文件名; 普通文件仍从路径推导</summary>
        public string? NameOverride { get; set; }
        public string Name => NameOverride ?? System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public long SizeBytes { get; set; }
        public DateTime Modified { get; set; }
        // v3.24.2: 网络流 (m3u8 等) 没有文件大小, 不再显示误导性的 "0.0 MB"
        public string SizeText => SizeBytes <= 0
            ? "网络流"
            : SizeBytes >= 1 << 30
            ? $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
            : $"{SizeBytes / 1024.0 / 1024:F1} MB";
    }
}
