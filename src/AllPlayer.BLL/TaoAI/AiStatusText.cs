using System;

namespace AllPlayer.BLL
{
    /// <summary>
    /// 播放页增强状态两行小字文案 (v3.20.6, 纯逻辑可测):
    /// 第一行 帧率或档位状态, 第二行 分辨率或原因说明。
    /// 用户诉求「增强开了半天没有帧率显示」——把每个不跑的断点如实亮在界面上:
    /// 装模型中 / 磁力流不增强 / 格式不支持 / 处理中未出规格 / 规格已出。
    /// v3.21.1: 接入协调器任务状态——排队/处理中/暂停/失败如实上屏, 不再与待机提示混淆。
    /// v3.25.9: 输出不设上限 (老板 11-10 拍板「恢复拉满」); 输出帧率超过屏幕可显示能力时
    /// 在分辨率行尾标注「屏幕可显示≤N」——回写文件保留最高规格, 上屏多少如实告知。
    /// </summary>
    public static class AiStatusText
    {
        /// <summary>组合状态两行小字。kernelReady = 内核已装配且编排器就绪;
        /// taskState = 当前播放视频在协调器里的任务状态, Idle 表示没有对应任务。</summary>
        public static (string Fps, string Res) Compose(
            int tier, bool autoGear,
            (int SrcFps, bool Interp, int SrcW, int SrcH, int OutW, int OutH, int OutFps) spec,
            bool kernelReady, bool magnetActive, bool isMp4,
            AiEnhanceState taskState = AiEnhanceState.Idle,
            (int Done, int Total) progress = default,
            float screenMaxHz = 0)
        {
            if (!kernelReady) return ("增强:装模型中…", "装好自动开始, 无需操作");
            if (magnetActive) return ("增强:磁力流中", "边下边播暂不增强");
            if (tier == 0 && !autoGear) return ("增强:关", "");
            if (spec.SrcFps > 0)
            {
                // v3.25.6: 输出帧率由规格快照带出 (源帧率×插帧倍率), 不再硬编码 ×2
                int outFps = spec.OutFps > 0 ? spec.OutFps : (spec.Interp ? spec.SrcFps * 2 : spec.SrcFps);
                // v3.25.9: 输出永不封顶; 超出屏幕可显示能力时如实标注, 探不到屏幕 (0) 不标注
                string res = $"{spec.SrcW}×{spec.SrcH} → {spec.OutW}×{spec.OutH}";
                int screenCap = (int)Math.Floor(screenMaxHz);
                if (screenCap > 0 && outFps > screenCap)
                    res += $" | 屏幕可显示≤{screenCap}";
                return ($"{spec.SrcFps}帧 → {outFps}帧", res);
            }
            if (!isMp4) return ("增强:该格式暂不支持", "仅 MP4 源可增强");
            string gear = autoGear
                ? "智能自动"
                : tier switch { 1 => "低档", 2 => "中档", _ => "高档" };
            switch (taskState)
            {
                case AiEnhanceState.Queued:
                    return ("增强:排队中…", "轮到这部就自动开始");
                case AiEnhanceState.Running:
                    return ("增强:处理中…",
                        progress.Total > 0 ? $"已完成 {progress.Done}/{progress.Total} 段" : "处理完显示帧率分辨率");
                case AiEnhanceState.PausedGuard:
                    return ("增强:暂停中", "播放卡顿让路, 恢复后自动继续");
                case AiEnhanceState.PausedThermal:
                    return ("增强:暂停中", "设备发热, 凉下来自动继续");
                case AiEnhanceState.PausedByUser:
                    return ("增强:已暂停", "到设置里可继续");
                case AiEnhanceState.Failed:
                    return ("增强:这次没成功", "原文件完好, 可稍后再试");
            }
            return ("增强:" + gear, "开始处理后显示帧率分辨率");
        }
    }
}
