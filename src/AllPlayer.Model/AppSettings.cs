using System;
using System.Collections.Generic;

namespace AllPlayer.Model
{
    /// <summary>
    /// 应用设置实体 (三端共用): 主题 / 保存位置 / 扫描目录 / 续播记录。
    /// 纯数据对象; 文件读写由 AllPlayer.DAL.SettingsFile 负责。
    /// 各端启动时先设置 BaseDir (Windows: %APPDATA%, Android: 应用私有目录),
    /// 再由 DAL 加载后赋给 Current。
    /// </summary>
    public class AppSettings
    {
        /// <summary>主题: FollowSystem / Light / Dark (旧三值, 桌面版沿用; 手机版已改用 TaoThemeId)</summary>
        public string ThemeMode { get; set; } = "FollowSystem";

        /// <summary>Tao 系三象主题编号 (全系统一): 0=水墨(默认) 1=天灰 2=墨夜。
        /// 旧"浅色/深色"命名与实现退役; 越界按水墨处理。</summary>
        public int TaoThemeId { get; set; } = 0;

        /// <summary>磁力下载(边播边存)保存位置</summary>
        public string SaveFolder { get; set; } = "";

        /// <summary>截图保存位置 (桌面版)</summary>
        public string SnapshotFolder { get; set; } = "";

        /// <summary>磁力播放时直接存到保存位置 (关 = 存临时目录, 关闭后清理)</summary>
        public bool AutoSaveMagnet { get; set; } = true;

        /// <summary>录制(边播边存)结束后自动后台无损转换为 MKV</summary>
        public bool AutoConvertRecorded { get; set; } = true;

        /// <summary>智能字幕 (v3.25.23): 开启后自动识别音频生成字幕; 中文片只转写, 日韩英法德转写+英译 (中译二期接)</summary>
        public bool SmartSubtitleEnabled { get; set; } = false;

        /// <summary>无损转换成功后删除原 TS 文件</summary>
        public bool DeleteTsAfterConvert { get; set; } = false;

        /// <summary>ffmpeg 完整路径 (留空则自动在程序目录和 PATH 里找; 电视/手机版无 ffmpeg 时保留 TS)</summary>
        public string FfmpegPath { get; set; } = "";

        /// <summary>主页自动扫描的目录 (由各端注入平台默认值)</summary>
        public List<string> ScanFolders { get; set; } = new List<string>();

        /// <summary>手动从主页移除的文件 (重扫后仍不出现)</summary>
        public HashSet<string> Excluded { get; set; } = new HashSet<string>();

        /// <summary>记忆播放倍速 (重启后沿用上次选择)</summary>
        public double PlaybackRate { get; set; } = 1.0;

        /// <summary>断点续播: 文件路径 -> 毫秒</summary>
        public Dictionary<string, double> Resume { get; set; } = new Dictionary<string, double>();

        /// <summary>已知的视频总时长 (毫秒), 供列表上画观看进度条</summary>
        public Dictionary<string, long> Durations { get; set; } = new Dictionary<string, long>();

        /// <summary>最近播放: 文件路径 -> 最后播放时刻 (Unix 毫秒), 供"最近播放优先"排序</summary>
        public Dictionary<string, long> Recent { get; set; } = new Dictionary<string, long>();

        /// <summary>媒体库排序: 最近播放优先</summary>
        public bool RecentFirst { get; set; } = false;

        /// <summary>列表排列模式 (v3.18.2 落盘): true=图文(带缩略图), false=紧凑(纯文字行); 重启后保持</summary>
        public bool GalleryMode { get; set; } = true;

        /// <summary>播放页自动旋转 (v3.12): 开 = 陀螺仪接管旋转方向, 全屏钮收起;
        /// 关 = 进播放页竖屏持稳, 用全屏钮手动切横屏/还原, 陀螺仪不再打扰</summary>
        public bool AutoRotate { get; set; } = true;

        /// <summary>AI 画质（v3.20.6 用户定: 默认智能自动）: 0=关 1=低档 2=中档 3=高档；
        /// 真超分+插帧后台逐段增强写回，卡顿/发热自动让路。档位按设备分级提示。</summary>
        public int AiEnhanceTier { get; set; } = 3;

        /// <summary>AI 智能自动档 (v3.20.0; v3.20.6 用户定: 默认开): true=档位交给自动策略（按设备能力+实测性能定上限，
        /// 热区/电量/功耗守卫仍生效）; false=用户手选档位为偏好上限。</summary>
        public bool AiAutoGear { get; set; } = true;

        /// <summary>断流自救 (v3.23): 远程网络流 15 秒无进度/内核报错时, 按 2/4/8 秒退避自动重试最多 3 次。
        /// 开=自动重试; 关=退回旧行为(只提示不动手)。手动暂停永不触发。</summary>
        public bool StreamAutoRetry { get; set; } = true;

        /// <summary>网络线路: 用户自配代理 (v3.25.0)。开 = m3u8/磁力网络请求走该代理, 快速通道让路;
        /// 关 = 直连 + 快速通道 (多 DNS 择优 + 连接竞速)。产品不内置任何代理, 只支持用户自配。</summary>
        public bool ProxyEnabled { get; set; } = false;

        /// <summary>代理服务器地址 (IP 或域名, host:port 中拆出)</summary>
        public string ProxyHost { get; set; } = "";

        /// <summary>代理端口</summary>
        public int ProxyPort { get; set; } = 0;

        /// <summary>本地流访问令牌 (v3.23): 开=本机流服务仅凭本次会话令牌放行, 同网设备直拉地址会被拒;
        /// 关=退回旧行为(任何同网设备可访问当前流, 兼容特殊设备)。</summary>
        public bool StreamTokenEnabled { get; set; } = true;

        /// <summary>设置文件所在目录 (各端启动时注入, DAL 读写用)</summary>
        public static string BaseDir { get; set; } = "";

        /// <summary>内存中的当前设置单例</summary>
        public static AppSettings Current { get; set; } = new AppSettings();
    }
}
