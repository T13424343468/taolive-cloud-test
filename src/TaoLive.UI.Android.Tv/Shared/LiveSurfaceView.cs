using System;
using Android.Content;
using Android.Runtime;
using Android.Views;
using LibVLCSharp.Shared;
using Org.Videolan.Libvlc;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 表面视图播放载体（v1.0.7 稳定档）：v1.0.5 起真机验证过可正常打开；
/// 纹理载体在部分机型上有崩溃风险，本类作为回退路径保留。
/// 接线方式与官方 VideoView 逐行同构，仅载体为 SurfaceView。
/// fix37（v1.0.16 不满屏根治，2026-11-13 口径拍板）：画面保持比例铺满——保持比例放大到贴合屏幕最大，
/// 不满的边留黑，不裁不拉伸，全台统一。实现=接管 libvlc 视频布局回调（onNewVideoLayout），
/// 按视频可视宽高比自算 contain 适配矩形，居中缩放载体自身；视频尺寸未知时全屏兜底（铺满整屏）。
/// </summary>
public class LiveSurfaceView : SurfaceView, AWindow.ISurfaceCallback, IVLCVout.ICallback, IVideoCarrierView, IVLCVout.IOnNewVideoLayoutListener, View.IOnLayoutChangeListener
{
    MediaPlayer? _mediaPlayer;
    AWindow? _awindow;

    // fix37 画幅基准：visible（源自报可视区，已含像素宽高比修正）优先，缺失退回全尺寸；0=未知走全屏兜底
    int _videoAspectW;
    int _videoAspectH;

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
        _awindow.AttachViews(this); // fix37：带布局回调挂载，视频尺寸变化经 onNewVideoLayout 回来
        _mediaPlayer.SetAndroidContext(_awindow.Handle);

        AddOnLayoutChangeListener(this); // fix37：容器尺寸变化时重报窗口尺寸并重算适配
    }

    void Detach()
    {
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
