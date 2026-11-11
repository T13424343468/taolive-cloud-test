using System;

namespace TaoLive.BLL;

/// <summary>
/// 启动贴面时序闸（v1.0.10 修复③断言口径）：纹理载体贴播放器前逐道判定的纯逻辑，
/// 与 LiveTextureView.Attach/AttachNow 一一对应：
/// - 播放器未给、表面纹理未就绪 → 一律等待 OnSurfaceTextureAvailable，绝不抢跑；
/// - 纹理就绪 → 才允许真正贴面（AWindow.attachTextureView 口径）；
/// - 等待期间纹理被回收 → 退回等待，绝不带 null 表面进 AWindow（真机崩溃根因）。
/// </summary>
public enum AttachAction { WaitSurface, AttachNow }

public static class SurfaceAttachGate
{
    /// <summary>playerAttached=true 表示播放器已给载体但 AWindow 未建；textureReady 表示视图可用且 SurfaceTexture 非空。</summary>
    public static AttachAction Decide(bool playerPresent, bool windowAbsent, bool textureReady)
    {
        if (!playerPresent || !windowAbsent) return AttachAction.WaitSurface; // 无播放器/已贴：不动作
        if (!textureReady) return AttachAction.WaitSurface;                   // 未就绪：等回调，不抢跑
        return AttachAction.AttachNow;
    }
}
