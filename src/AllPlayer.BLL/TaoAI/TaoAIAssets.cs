using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AllPlayer.BLL
{
    /// <summary>
    /// AI 模型资产装配（实验档 v1）：
    /// APK 内 Assets/taoai/ 下的模型（.param 明文 + .bin.gz 权重 gzip 压缩）在首次使用时
    /// 解压到应用缓存目录，供 ncnn 按路径读取。缓存遵守全系缓存自愈标准：
    /// 固定目录 + 内容校验（目标文件存在且大小一致即复用；损坏/缺失重解压）。
    /// </summary>
    public class TaoAIAssets
    {
        private readonly Func<string, Stream?> _openAsset;    // UI 层注入：打开 Assets 流
        private readonly string _cacheDir;                   // 解压目标目录
        private readonly Action<string>? _log;                // 诊断（可空）

        public TaoAIAssets(Func<string, Stream?> openAsset, string cacheDir, Action<string>? log = null)
        {
            _openAsset = openAsset ?? throw new ArgumentNullException(nameof(openAsset));
            _cacheDir = cacheDir;
            _log = log;
        }

        /// <summary>模型文件名（不含扩展名）：ncnn 每模型 param + bin 成对</summary>
        public static readonly string[] Models = new string[]
        {
            "flownet",
            "realesr-animevideov3-x4",
            "RealESRGAN_General_x4_v3"
        };

        /// <summary>
        /// v3.18.5 期望解压后大小（字节）：缓存自愈口径收紧为「存在且大小一致才复用」。
        /// 病根：旧口径只查非空，真机上旧版本残留/断电截断的同名坏文件永远跳过重解压，
        /// 内核 load_param/load_model 对坏文件必败，三次重试共约 15 秒后永久放弃→基准一直「准备中」。
        /// 大小不符即重装（坏文件重解压覆盖，原子替换）。
        /// </summary>
        public static readonly Dictionary<string, long> ExpectedSizes = new Dictionary<string, long>
        {
            { "flownet.param", 12831 },
            { "flownet.bin", 10340416 },
            { "realesr-animevideov3-x4.param", 3077 },
            { "realesr-animevideov3-x4.bin", 1247368 },
            { "RealESRGAN_General_x4_v3.param", 5019 },
            { "RealESRGAN_General_x4_v3.bin", 2435272 }
        };

        /// <summary>确保全部模型就绪；返回模型目录路径。解压失败抛异常（不静默）。</summary>
        public string EnsureReady()
        {
            Directory.CreateDirectory(_cacheDir);
            foreach (var name in Models)
            {
                Prepare(name + ".param", name + ".param", gz: false);
                Prepare(name + ".bin", name + ".bin.gz", gz: true);
            }
            return _cacheDir;
        }

        private void Prepare(string outName, string assetName, bool gz)
        {
            string outPath = Path.Combine(_cacheDir, outName);
            long expected;
            if (ExpectedSizes.TryGetValue(outName, out var exp)) expected = exp;
            else expected = 0;   // 名单外文件退回旧口径：非空即复用
            if (File.Exists(outPath))
            {
                var len = new FileInfo(outPath).Length;
                bool reuse = expected > 0 ? len == expected : len > 0;
                if (reuse) return;   // 校验过即复用
                if (_log != null) _log("taoai model cache invalid: " + outName + " size=" + len + " expect=" + expected + ", re-extract");
                try { File.Delete(outPath); } catch { }   // v3.18.5: 大小不符=残留/损坏，重装
            }
            var tmp = outPath + ".tmp";
            using (Stream? src = _openAsset("taoai/" + assetName))
            {
                if (src == null) throw new IOException("模型资产缺失: " + assetName);
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    if (gz)
                    {
                        using (var gz2 = new GZipStream(src, CompressionMode.Decompress, leaveOpen: true))
                            gz2.CopyTo(dst);
                    }
                    else
                    {
                        src.CopyTo(dst);
                    }
                }
            }
            if (new FileInfo(tmp).Length <= 0)
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException("模型资产解压为空: " + assetName);
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);   // 原子替换
            if (_log != null) _log("taoai model ready: " + outName);
        }
    }
}
