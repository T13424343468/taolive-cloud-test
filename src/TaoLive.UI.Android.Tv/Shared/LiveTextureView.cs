using System;
using Android.Content;
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
/// </summary>
public class LiveTextureView : TextureView, AWindow.ISurfaceCallback, IVLCVout.ICallback, IVideoCarrierView
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

    /// <summary>IVLCVout 回调：无附加动作。</summary>
    public void OnSurfacesCreated(IVLCVout? vout) { }

    /// <summary>IVLCVout 回调：无附加动作。</summary>
    public void OnSurfacesDestroyed(IVLCVout? vout) { }

    void AWindow.ISurfaceCallback.OnSurfacesCreated(AWindow? aWindow) { }

    void AWindow.ISurfaceCallback.OnSurfacesDestroyed(AWindow? aWindow) { }
}
