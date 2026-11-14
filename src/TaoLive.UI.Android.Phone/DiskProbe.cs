using System;
using System.IO;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>磁盘空间探测 (v3.25.26): 安卓必须走 StatFs——DriveInfo 对 "/" 报的是 rootfs,
    /// 用户数据盘再空它也几乎没空间, v3.25.25 真机误报「存储空间不足」实锤。
    /// 探测失败返回 null, 调用方跳过预检, 交给真实写盘的 ENOSPC 中文分类兜底。</summary>
    internal static class DiskProbe
    {
        /// <summary>dirPath 上的可用字节数 (该目录所在分区口径); 探测失败 null。</summary>
        public static long? FreeBytes(string dirPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dirPath)) return null;
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                return new global::Android.OS.StatFs(dirPath).AvailableBytes;
            }
            catch { return null; }
        }
    }
}
