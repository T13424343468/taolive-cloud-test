using System;
using Android.Content;
using Android.Runtime;
using Android.Views;
using LibVLCSharp.Platforms.Android;
using LibVLCSharp.Shared;
using Org.Videolan.Libvlc;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 表面视图播放载体（v1.0.7 稳定档）：v1.0.5 起真机验证过可正常打开；
/// 纹理载体在部分机型上有崩溃风险，本类作为回退路径保留。
/// 接线方式与官方 VideoView 逐行同构，仅载体为 SurfaceView。
/// </summary>
public class LiveSurfaceView : SurfaceView, AWindow.ISurfaceCallback, IVLCVout.ICallback, IVideoCarrierView
{
    MediaPlayer? _mediaPlayer;
    AWindow? _awindow;
    LayoutChangeListener? _layoutListener;

    public LiveSurfaceView(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public LiveSurfaceView(Context context) : base(context) { }

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
        _awindow.SetVideoView(this); // SurfaceView 载体
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
