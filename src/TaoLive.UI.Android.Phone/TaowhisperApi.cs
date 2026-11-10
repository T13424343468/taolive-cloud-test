using System;
using System.Runtime.InteropServices;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// whisper.cpp 包装层 P/Invoke (v3.25.23): libtaowhisper.so 随包分发 (arm64)。
    /// 时间单位: 包装层返回 10ms tick; PCM 为 16kHz 单声道 float。
    /// </summary>
    internal static class TaowhisperApi
    {
        private const string Lib = "taowhisper";

        [DllImport(Lib)]
        private static extern IntPtr tao_init(string modelPath);

        public static IntPtr Init(string modelPath) => tao_init(modelPath);

        [DllImport(Lib)]
        private static extern void tao_free(IntPtr ctx);

        public static void Free(IntPtr ctx) => tao_free(ctx);

        [DllImport(Lib)]
        private static extern int tao_detect(IntPtr ctx, float[] pcm, int n, byte[] lang, int langCap);

        /// <summary>自动检测语种 (取前段音频), 返回如 "en"/"ja"; 失败返回 null</summary>
        public static string? Detect(IntPtr ctx, float[] pcm, int n)
        {
            var buf = new byte[16];
            var rc = tao_detect(ctx, pcm, n, buf, buf.Length);
            if (rc != 0) return null;
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            return System.Text.Encoding.ASCII.GetString(buf, 0, len);
        }

        [DllImport(Lib)]
        private static extern int tao_run(IntPtr ctx, float[] pcm, int n, int task, string langHint, byte[] outBuf, int outCap);

        /// <summary>跑识别; task 0=原文 1=英译。返回段数, 文本行 "t0\tt1\ttext" (t 为 10ms tick) 写入 outText。</summary>
        public static int Run(IntPtr ctx, float[] pcm, int n, int task, string? langHint, out string outText)
        {
            var buf = new byte[1 << 20];   // 1MB 足够单窗口段文本
            var rc = tao_run(ctx, pcm, n, task, langHint ?? "", buf, buf.Length);
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            outText = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Max(0, len));
            return rc;
        }

        [DllImport(Lib)]
        private static extern IntPtr tao_err();

        public static string? LastError => Marshal.PtrToStringAnsi(tao_err());
    }
}
