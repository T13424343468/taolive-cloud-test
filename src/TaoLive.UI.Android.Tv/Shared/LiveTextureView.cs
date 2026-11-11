using System;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using LibVLCSharp.Platforms.Android;
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
/// </summary>
public class LiveTextureView : TextureView, AWindow.ISurfaceCallback, IVLCVout.ICallback, IVideoCarrierView, TextureView.ISurfaceTextureListener
{
    MediaPlayer? _mediaPlayer;
    AWindow? _awindow;
    LayoutChangeListener? _layoutListener;

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
        _awindow.AttachViews();
        _mediaPlayer.SetAndroidContext(_awindow.Handle);

        _layoutListener = new LayoutChangeListener(_awindow);
        AddOnLayoutChangeListener(_layoutListener);
    }

    void Detach()
    {
        SurfaceTextureListener = null; // 摘掉监听：不完整的贴面流程不收回调
        _awindow?.RemoveCallback(this);
        _awindow?.DetachViews();

        if (_layoutListener != null)
            RemoveOnLayoutChangeListener(_layoutListener);

        _layoutListener?.Dispose();
        _layoutListener = null;

        _awindow?.Dispose();
        _awindow = null;
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

    /// <summary>IVLCVout 回调：无附加动作。</summary>
    public void OnSurfacesCreated(IVLCVout? vout) { }

    /// <summary>IVLCVout 回调：无附加动作。</summary>
    public void OnSurfacesDestroyed(IVLCVout? vout) { }

    void AWindow.ISurfaceCallback.OnSurfacesCreated(AWindow? aWindow) { }

    void AWindow.ISurfaceCallback.OnSurfacesDestroyed(AWindow? aWindow) { }
}
