package crc64a1b52d66edecd4d3;


public class LiveSurfaceView
	extends android.view.SurfaceView
	implements
		mono.android.IGCUserPeer,
		org.videolan.libvlc.AWindow.SurfaceCallback,
		org.videolan.libvlc.IVLCVout.Callback
{
/** @hide */
	public static final String __md_methods;
	static {
		__md_methods = 
			"n_onSurfacesCreated:(Lorg/videolan/libvlc/AWindow;)V:GetOnSurfacesCreated_Lorg_videolan_libvlc_AWindow_Handler:Org.Videolan.Libvlc.AWindow/ISurfaceCallbackInvoker, LibVLCSharp.Android.AWindowModern\n" +
			"n_onSurfacesDestroyed:(Lorg/videolan/libvlc/AWindow;)V:GetOnSurfacesDestroyed_Lorg_videolan_libvlc_AWindow_Handler:Org.Videolan.Libvlc.AWindow/ISurfaceCallbackInvoker, LibVLCSharp.Android.AWindowModern\n" +
			"n_onSurfacesCreated:(Lorg/videolan/libvlc/IVLCVout;)V:GetOnSurfacesCreated_Lorg_videolan_libvlc_IVLCVout_Handler:Org.Videolan.Libvlc.IVLCVout/ICallbackInvoker, LibVLCSharp.Android.AWindowModern\n" +
			"n_onSurfacesDestroyed:(Lorg/videolan/libvlc/IVLCVout;)V:GetOnSurfacesDestroyed_Lorg_videolan_libvlc_IVLCVout_Handler:Org.Videolan.Libvlc.IVLCVout/ICallbackInvoker, LibVLCSharp.Android.AWindowModern\n" +
			"";
		mono.android.Runtime.register ("TaoLive.UI.Android.Tv.Shared.LiveSurfaceView, TaoLive.UI.Android.Tv", LiveSurfaceView.class, __md_methods);
	}

	public LiveSurfaceView (android.content.Context p0, android.util.AttributeSet p1, int p2, int p3)
	{
		super (p0, p1, p2, p3);
		if (getClass () == LiveSurfaceView.class) {
			mono.android.TypeManager.Activate ("TaoLive.UI.Android.Tv.Shared.LiveSurfaceView, TaoLive.UI.Android.Tv", "Android.Content.Context, Mono.Android:Android.Util.IAttributeSet, Mono.Android:System.Int32, System.Private.CoreLib:System.Int32, System.Private.CoreLib", this, new java.lang.Object[] { p0, p1, p2, p3 });
		}
	}

	public LiveSurfaceView (android.content.Context p0, android.util.AttributeSet p1, int p2)
	{
		super (p0, p1, p2);
		if (getClass () == LiveSurfaceView.class) {
			mono.android.TypeManager.Activate ("TaoLive.UI.Android.Tv.Shared.LiveSurfaceView, TaoLive.UI.Android.Tv", "Android.Content.Context, Mono.Android:Android.Util.IAttributeSet, Mono.Android:System.Int32, System.Private.CoreLib", this, new java.lang.Object[] { p0, p1, p2 });
		}
	}

	public LiveSurfaceView (android.content.Context p0, android.util.AttributeSet p1)
	{
		super (p0, p1);
		if (getClass () == LiveSurfaceView.class) {
			mono.android.TypeManager.Activate ("TaoLive.UI.Android.Tv.Shared.LiveSurfaceView, TaoLive.UI.Android.Tv", "Android.Content.Context, Mono.Android:Android.Util.IAttributeSet, Mono.Android", this, new java.lang.Object[] { p0, p1 });
		}
	}

	public LiveSurfaceView (android.content.Context p0)
	{
		super (p0);
		if (getClass () == LiveSurfaceView.class) {
			mono.android.TypeManager.Activate ("TaoLive.UI.Android.Tv.Shared.LiveSurfaceView, TaoLive.UI.Android.Tv", "Android.Content.Context, Mono.Android", this, new java.lang.Object[] { p0 });
		}
	}

	public void onSurfacesCreated (org.videolan.libvlc.AWindow p0)
	{
		n_onSurfacesCreated (p0);
	}

	private native void n_onSurfacesCreated (org.videolan.libvlc.AWindow p0);

	public void onSurfacesDestroyed (org.videolan.libvlc.AWindow p0)
	{
		n_onSurfacesDestroyed (p0);
	}

	private native void n_onSurfacesDestroyed (org.videolan.libvlc.AWindow p0);

	public void onSurfacesCreated (org.videolan.libvlc.IVLCVout p0)
	{
		n_onSurfacesCreated (p0);
	}

	private native void n_onSurfacesCreated (org.videolan.libvlc.IVLCVout p0);

	public void onSurfacesDestroyed (org.videolan.libvlc.IVLCVout p0)
	{
		n_onSurfacesDestroyed (p0);
	}

	private native void n_onSurfacesDestroyed (org.videolan.libvlc.IVLCVout p0);

	private java.util.ArrayList refList;
	public void monodroidAddReference (java.lang.Object obj)
	{
		if (refList == null)
			refList = new java.util.ArrayList ();
		refList.add (obj);
	}

	public void monodroidClearReferences ()
	{
		if (refList != null)
			refList.clear ();
	}
}
