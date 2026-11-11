using System;
using System.Runtime.InteropServices;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 英译中推理内核 P/Invoke (v3.25.24): libtaotranslate.so
    /// opus-mt-en-zh ONNX int8 + sentencepiece, 贪心解码; 模型运行时下载。
    /// </summary>
    internal static class TaotranslateApi
    {
        const string Lib = "taotranslate";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tao_trans_init(string modelDir);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int tao_trans_run(IntPtr ctx, string text, byte[] outBuf, int outCap);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void tao_trans_free(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tao_trans_err(IntPtr ctx);

        private static IntPtr _ctx = IntPtr.Zero;

        /// <summary>加载模型 (幂等: 已加载直接成功)。返回 null 表示失败, 错误走 LastError。</summary>
        public static string? LastError { get; private set; }

        public static bool EnsureLoaded(string modelDir)
        {
            if (_ctx != IntPtr.Zero) return true;
            var p = tao_trans_init(modelDir);
            if (p == IntPtr.Zero) { LastError = Marshal.PtrToStringAnsi(tao_trans_err(IntPtr.Zero)); return false; }
            _ctx = p;
            return true;
        }

        /// <summary>翻译一句英文 -> 中文; 失败返回 null。</summary>
        public static string? Translate(string text)
        {
            if (_ctx == IntPtr.Zero || string.IsNullOrWhiteSpace(text)) return null;
            var buf = new byte[8192];
            if (tao_trans_run(_ctx, text, buf, buf.Length) != 0)
            {
                LastError = Marshal.PtrToStringAnsi(tao_trans_err(_ctx));
                return null;
            }
            int end = Array.IndexOf(buf, (byte)0);
            if (end < 0) end = buf.Length;
            return System.Text.Encoding.UTF8.GetString(buf, 0, end).Trim();
        }

        public static void Free()
        {
            if (_ctx != IntPtr.Zero) { tao_trans_free(_ctx); _ctx = IntPtr.Zero; }
        }
    }
}
