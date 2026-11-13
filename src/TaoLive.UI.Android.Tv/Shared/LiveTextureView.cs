using System;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using LibVLCSharp.Shared;
using Org.Videolan.Libvlc;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 纹理视图播放载体（雷三根治）：
/// SurfaceView 退后台会被系统整层回收，回来必然对不上（贴底横条/闪黑）；
/// TextureView 是普通视图层，退后台不销毁，回前台画面原样保留。
/// 接线方式与官方 VideoView（SurfaceView 版）逐行同构，仅载体换成 TextureView。
/// v1.0.10 修复项③（用户 12:38 确认真机启动崩溃 IllegalArgumentException surfaceTexture must not be null）：
/// 贴播放器前先验 SurfaceTexture 就绪——未就绪先等 OnSurfaceTextureAvailable 回调再贴，不抢跑；
/// 贴前逐道判空，任何一道不齐改走等待路径，绝不带 null 表面进 AWindow.attachTextureView。
/// fix37（v1.0.16 不满屏根治，2026-11-13 口径拍板）：画面保持比例铺满——保持比例放大到贴合屏幕最大，
/// 不满的边留黑，不裁不拉伸，全台统一。实现=接管 libvlc 视频布局回调（onNewVideoLayout），
/// 按视频可视宽高比自算 contain 适配矩形，居中缩放载体自身；视频尺寸未知时全屏兜底（铺满整屏）。
/// 与 LiveSurfaceView 同口径同构。
/// </summary>
public class LiveTextureView : TextureView, AWindow.ISurfaceCallback, IVLCVout.ICallback, IVideoCarrierView, TextureView.ISurfaceTextureListener, IVLCVout.IOnNewVideoLayoutListener, View.IOnLayoutChangeListener
{
    MediaPlayer? _mediaPlayer;
    AWindow? _awindow;

    // fix37 画幅基准：visible（源自报可视区，已含像素宽高比修正）优先，缺失退回全尺寸；0=未知走全屏兜底
    int _videoAspectW;
    int _videoAspectH;

    public LiveTextureView(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public LiveTextureView(Context context) : base(context) { }

    /// <summary>The MediaPlayer object attached to this view.</summary>
    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        set
        {
            if (_mediaPlayer != value)
            {
                Detach();
                _mediaPlayer = value;

                if (_mediaPlayer != null)
                {
                    Attach();
                }
            }
        }
    }

    void Attach()
    {
        if (_mediaPlayer is null || _awindow is not null) return;
        // v1.0.10 修复③：表面纹理未就绪（视图未布局/未进窗口层级）先挂监听等回调，绝不抢跑贴 null 表面
        if (!IsAvailable || SurfaceTexture is null)
        {
            SurfaceTextureListener = this;
            return;
        }
        AttachNow();
    }

    /// <summary>真正贴面（纹理就绪后调用）：贴前再逐道判空，任何一道不齐留在等待路径，保证不崩。</summary>
    void AttachNow()
    {
        if (_mediaPlayer is null || _awindow is not null) return;
        if (SurfaceTexture is null)
        {
            SurfaceTextureListener = this; // 回调间隙纹理又被回收：退回等待，不崩
            return;
        }

        _awindow = new AWindow(this);
        _awindow.AddCallback(this);
        _awindow.SetVideoView(this); // TextureView 载体（官方 AWindow 支持两个重载）
        _awindow.AttachViews(this); // fix37：带布局回调挂载，视频尺寸变化经 onNewVideoLayout 回来
        _mediaPlayer.SetAndroidContext(_awindow.Handle);

        AddOnLayoutChangeListener(this); // fix37：容器尺寸变化时重报窗口尺寸并重算适配
    }

    void Detach()
    {
        SurfaceTextureListener = null; // 摘掉监听：不完整的贴面流程不收回调
        RemoveOnLayoutChangeListener(this);
        _awindow?.RemoveCallback(this);
        _awindow?.DetachViews();

        _awindow?.Dispose();
        _awindow = null;

        // fix37：换档/重挂从干净状态开始，画幅基准清零，回到全屏兜底
        _videoAspectW = 0;
        _videoAspectH = 0;
        Post(RestoreFullScreen);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Detach();
        }
        base.Dispose(disposing);
    }

    // ---- TextureView.ISurfaceTextureListener（v1.0.10 修复③）：纹理就绪才贴播放器 ----

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        if (_mediaPlayer != null && _awindow is null) AttachNow();
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        // 视图销毁走 Dispose/Detach 通道，这里如实放行系统回收
        return true;
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    // ---- fix37 保持比例铺满（2026-11-13 口径拍板，全台统一）----

    /// <summary>libvlc 视频布局回调：拿视频尺寸算适配。回调在 libvlc 线程，改布局必须回 UI 线程。</summary>
    public void OnNewVideoLayout(IVLCVout? vout, int width, int height, int visibleWidth, int visibleHeight, int videoWidth, int videoHeight)
    {
        var aw = visibleWidth > 0 && visibleHeight > 0 ? visibleWidth : videoWidth;
        var ah = visibleWidth > 0 && visibleHeight > 0 ? visibleHeight : videoHeight;
        _videoAspectW = aw;
        _videoAspectH = ah;
        Post(ApplyAspectFill);
    }

    /// <summary>contain 适配：等比放大到贴合容器最大，不满的边留黑，不裁不拉伸；
    /// 视频尺寸未知或父容器非 FrameLayout 时全屏兜底（铺满整屏）。</summary>
    void ApplyAspectFill()
    {
        var pw = Width;
        var ph = Height;
        if (pw <= 0 || ph <= 0) return; // 容器未完成布局，等下一轮 OnLayoutChange

        if (_videoAspectW <= 0 || _videoAspectH <= 0)
        {
            RestoreFullScreen();
            return;
        }

        if (Parent is not FrameLayout)
        {
            RestoreFullScreen(); // 挂载点异常时保守兜底，不越权接管
            return;
        }

        var scale = Math.Min((double)pw / _videoAspectW, (double)ph / _videoAspectH);
        var newW = (int)Math.Round(_videoAspectW * scale);
        var newH = (int)Math.Round(_videoAspectH * scale);
        if (newW < 1 || newH < 1) return;

        if (LayoutParameters is FrameLayout.LayoutParams cur && cur.Width == newW && cur.Height == newH)
            return; // 无变化不动，避免布局回调互激

        LayoutParameters = new FrameLayout.LayoutParams(newW, newH, GravityFlags.Center);
    }

    void RestoreFullScreen()
    {
        if (LayoutParameters is FrameLayout.LayoutParams cur && cur.Width == -1 && cur.Height == -1)
            return;
        LayoutParameters = new FrameLayout.LayoutParams(-1, -1, GravityFlags.Center);
    }

    // ---- View.IOnLayoutChangeListener（fix37）：容器尺寸变化时重报窗口并重算适配 ----

    public void OnLayoutChange(View? v, int left, int top, int right, int bottom, int oldLeft, int oldTop, int oldRight, int oldBottom)
    {
        var w = right - left;
        var h = bottom - top;
        if (w > 0 && h > 0)
        {
            _awindow?.SetWindowSize(w, h); // 窗口尺寸上报口径不变
            ApplyAspectFill(); // 布局回调在 UI 线程，直接重算
        }
    }

    /// <summary>IVLCVout 回调：无附加动作。</summary>
    public void OnSurfacesCreated(IVLCVout? vout) { }

    /// <summary>IVLCVout 回调：无附加动作。</summary>
    public void OnSurfacesDestroyed(IVLCVout? vout) { }

    void AWindow.ISurfaceCallback.OnSurfacesCreated(AWindow? aWindow) { }

    void AWindow.ISurfaceCallback.OnSurfacesDestroyed(AWindow? aWindow) { }
}
