using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// 画面载体启动决策纯逻辑 (v3.22.0 方案A, 无平台依赖可测试):
    /// mode: 1=纹理载体(默认) 0=表面载体(已永久回退); attempt: 1=上次纹理启动未清标记。
    /// 规则: mode=1 且 attempt≠1 → 用纹理; 否则一律表面视图。
    /// </summary>
    public static class CarrierDecision
    {
        public static bool UseTexture(int mode, int attempt) => mode == 1 && attempt != 1;
    }
}
