using System;
using System.Collections.Generic;

namespace AllPlayer.Model
{
    /// <summary>
    /// v3.22.7 真设备能力统一策略 (老板拍板「设备给什么就用什么, 不设人为上限」):
    /// ① 帧率: 目标=视频帧率与设备最高刷新率取大者, 废除任何 fps>120 一类的人为封顶;
    /// ② 分辨率: 输出缩放恒 1.0, 画面输出对齐面板实际分辨率, 不人为降采样;
    /// ③ 色彩: 设备探得什么就开什么 (广色域/HDR), 探不到或启用失败安全回退 SDR/标准;
    /// ④ 杜比: AC-3/E-AC-3 解码默认开; 杜比视界设备支持就原样播, 不支持降 HDR10, 再不行降 SDR。
    /// </summary>
    public static class DeviceCapabilityPolicy
    {
        /// <summary>目标帧率: 取大者, 无人为上限。videoFps<=0 时按设备最高刷新率走。</summary>
        public static float TargetFps(float videoFps, float deviceMaxHz)
        {
            float hz = deviceMaxHz > 0 ? deviceMaxHz : 0;
            float fps = videoFps > 0 ? videoFps : 0;
            return Math.Max(fps, hz);   // 无封顶: 165Hz、175Hz 照样全速
        }

        /// <summary>画面输出缩放: 恒 1.0 (渲染分辨率=载体实际像素, 不人为降采样)。</summary>
        public const float OutputScale = 1.0f;

        /// <summary>色彩档位: 设备探得 HDR/广色域就启用, 否则标准 SDR; contentHdr=true 而设备不支持时也回退。</summary>
        public static string ColorMode(bool deviceSupportsHdr, bool deviceSupportsWideGamut, bool contentHdr)
        {
            if (contentHdr && deviceSupportsHdr) return "HDR";
            if (deviceSupportsWideGamut) return "广色域";
            return "SDR";   // 安全回退, 永不硬凑
        }

        /// <summary>杜比音频: AC-3/E-AC-3 硬解默认开启 (设备解码器探得即用, 无开关门槛)。</summary>
        public static bool DolbyAudioEnabled => true;

        /// <summary>杜比视界处理: 设备原样播 → 降 HDR10 → 降 SDR, 三级回退。</summary>
        public static string DolbyVisionFallback(bool deviceSupportsDolbyVision, bool deviceSupportsHdr10)
        {
            if (deviceSupportsDolbyVision) return "杜比视界原样";
            if (deviceSupportsHdr10) return "HDR10";
            return "SDR";
        }

        /// <summary>能力台账一行: 实际生效档位如实记录, 不编造。</summary>
        public static string CapabilityLine(float targetFps, string colorMode, string dolbyMode)
            => $"生效: 帧率{targetFps:0.#}Hz | 色彩{colorMode} | 杜比{dolbyMode}";

        // ---- v3.25.8 实时增强上限 (老板 11-10 拍板): 实时增强以手机最大支持能力封顶, 不无限增强;
        //      后台回写 (离线段增强) 不受此限, 继续拉满。封顶值探不到就不瞎封 (原样通过)。 ----

        /// <summary>实时增强输出帧率: 不超过设备最高刷新率 (探不到 deviceMaxHz 时原样通过)。</summary>
        public static int RealtimeCapFps(int wantFps, float deviceMaxHz)
        {
            if (wantFps <= 0 || deviceMaxHz <= 0) return wantFps;
            return (int)Math.Min(wantFps, Math.Floor(deviceMaxHz));
        }

        /// <summary>实时增强输出尺寸: 不超过面板实际像素, 超出按比例缩回并对齐偶数; 探不到面板尺寸原样通过。</summary>
        public static (int W, int H) RealtimeCapSize(int wantW, int wantH, int panelW, int panelH)
        {
            if (wantW <= 0 || wantH <= 0 || panelW <= 0 || panelH <= 0) return (wantW, wantH);
            if (wantW <= panelW && wantH <= panelH) return (wantW, wantH);
            double k = Math.Min((double)panelW / wantW, (double)panelH / wantH);
            int w = (int)(wantW * k) & ~1, h = (int)(wantH * k) & ~1;
            return (w, h);
        }

        // ---- v3.25.10 动态插帧 (老板 11-10 拍板「输出拉到设备最高刷新率」): ----
        // ---- 倍率不再固定 4×/2× 两档, 按源帧率与设备最高刷新率倒推; 探不到屏幕回退内核固定档。 ----

        /// <summary>插帧倍率: 设备最高刷新率已知时取 round(刷新率/源帧率) (源已达上限=1 不插, 实时算力封口 8×);
        /// 探不到刷新率时回退内核固定档 (支持 4× 用 4×, 否则 2×)。</summary>
        public static int InterpMultiplier(int srcFps, float deviceMaxHz, bool kernelQuad)
        {
            if (srcFps <= 0) return 1;
            if (deviceMaxHz > 0)
            {
                int n = (int)Math.Round(deviceMaxHz / srcFps);
                if (n <= 1) return 1;   // 源帧率已达或超屏幕上限, 插帧无意义
                return Math.Min(n, 8);
            }
            return kernelQuad ? 4 : 2;
        }
    }
}
