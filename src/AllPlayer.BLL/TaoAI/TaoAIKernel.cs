using System;
using System.Runtime.InteropServices;

namespace AllPlayer.BLL
{
    /// <summary>
    /// libtaoai.so 推理内核桥（TaoPlay AI 画质实验档）。
    /// 内核侧红线：不做文件 IO、不做 UI；一切输入输出走内存缓冲。
    /// 模型常驻内存一次加载；所有调用线程由编排层（EnhancementCoordinator）控制，
    /// 优先级永远让路播放。字段与 native taoai.cpp 导出签名一一对应。
    /// </summary>
    public static class TaoAIKernel
    {
        private const string Lib = "libtaoai.so";

        /// <summary>超分模型编号：0 = realesr-animevideov3-x4（动漫/动画视频特化），1 = RealESRGAN_General_x4_v3（通用真人）</summary>
        public const int SrModelAnime = 0;
        public const int SrModelGeneral = 1;

        public static int Version()
        {
            return TaoAI.taoai_version();
        }

        /// <summary>初始化内核并常驻装载插帧模型。幂等；重复调用安全。</summary>
        public static int Init(string modelsDir, int threadNum)
        {
            if (string.IsNullOrEmpty(modelsDir)) throw new ArgumentNullException(nameof(modelsDir));
            return TaoAI.taoai_init(modelsDir, threadNum);
        }

        /// <summary>按需装载超分模型（换模型会清缓存重新读盘，勿在增强中频繁切换）。</summary>
        public static int LoadSrModel(string modelsDir, int modelId)
        {
            if (string.IsNullOrEmpty(modelsDir)) throw new ArgumentNullException(nameof(modelsDir));
            return TaoAI.taoai_sr_model(models_dir: modelsDir, model_id: modelId);
        }

        /// <summary>超分 4 倍。输入输出均 RGBA；w/h 任意（内部 tile 补边），输出 4w×4h。
        /// 返回 0 成功；负数为内核错误码。</summary>
        public static int SuperResolve(byte[] rgba, int w, int h, int tile, int modelId, byte[] outRgba)
        {
            if (rgba == null || outRgba == null) throw new ArgumentNullException("rgba");
            long needIn = (long)w * h * 4;
            long needOut = (long)w * 4 * h * 4 * 4;
            if (rgba.LongLength < needIn) throw new ArgumentException("输入缓冲不足", nameof(rgba));
            if (outRgba.LongLength < needOut) throw new ArgumentException("输出缓冲不足", nameof(outRgba));
            return TaoAI.taoai_sr(rgba, w, h, tile, modelId, outRgba);
        }

        /// <summary>RIFE 插帧：输入两帧 RGBA（同尺寸），t 为时间步 [0,1]，输出与输入同尺寸。</summary>
        public static int Interpolate(byte[] frame0, byte[] frame1, float t, int w, int h, byte[] outRgba)
        {
            if (frame0 == null || frame1 == null || outRgba == null) throw new ArgumentNullException("frame");
            long need = (long)w * h * 4;
            if (frame0.LongLength < need || frame1.LongLength < need || outRgba.LongLength < need)
                throw new ArgumentException("缓冲尺寸不足");
            return TaoAI.taoai_rife(frame0, frame1, t, w, h, outRgba);
        }

        public static void Release()
        {
            TaoAI.taoai_release();
        }

        /// <summary>GPU 是否在岗（v3.20.0）：1=Vulkan 已开 0=无 Vulkan 设备回退 CPU。
        /// 读不到导出（旧包）返回 0，展示侧按 CPU 口径如实显示。</summary>
        public static int GpuReady()
        {
            try { return TaoAI.taoai_gpu_ready(); }
            catch { return 0; }
        }

        // ---------- P/Invoke（与 native/taoai/taoai.cpp 严格对齐） ----------
        private static class TaoAI
        {
            [DllImport(Lib, EntryPoint = "taoai_init")]
            internal static extern int taoai_init(string models_dir, int thread_num);

            [DllImport(Lib, EntryPoint = "taoai_sr_model")]
            internal static extern int taoai_sr_model(string models_dir, int model_id);

            [DllImport(Lib, EntryPoint = "taoai_sr")]
            internal static extern int taoai_sr(byte[] rgba, int w, int h, int tile, int model_id, byte[] out_rgba);

            [DllImport(Lib, EntryPoint = "taoai_rife")]
            internal static extern int taoai_rife(byte[] f0, byte[] f1, float t, int w, int h, byte[] out_rgba);

            [DllImport(Lib, EntryPoint = "taoai_release")]
            internal static extern void taoai_release();

            [DllImport(Lib, EntryPoint = "taoai_version")]
            internal static extern int taoai_version();

            [DllImport(Lib, EntryPoint = "taoai_gpu_ready")]
            internal static extern int taoai_gpu_ready();
        }
    }
}
