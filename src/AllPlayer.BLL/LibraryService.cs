using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AllPlayer.DAL;
using AllPlayer.Model;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 业务层 - 主页媒体库: 后台扫描目录里的视频文件, 支持手动增删
    /// (移除会记住, 重扫不再出现)。扫描实现由数据层 FileSystemVideoSource 提供。
    /// </summary>
    public static class LibraryService
    {
        /// <summary>后台扫描, 每找到一个文件回调一次 (带累计进度文字)</summary>
        public static Task ScanAsync(IEnumerable<string> folders, ISet<string> excluded,
            Action<VideoItem> onFound, Action<string> onProgress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                FileSystemVideoSource.Scan(folders, excluded, onFound, onProgress, ct);
            }, ct);
        }
    }
}
