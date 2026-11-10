using System;
using Android.Content;
using Android.Runtime;
using Android.Views;
using LibVLCSharp.Platforms.Android;
using LibVLCSharp.Shared;
using Org.Videolan.Libvlc;
using VideoView = LibVLCSharp.Platforms.Android.VideoView;

namespace AllPlayer.UI.Android.Shared
{
    /// <summary>
    /// 画面载体抽象 (v3.22.0 方案A):
    /// 官方 VideoView (SurfaceView) 退后台整层回收, 回前台重建时序在真机上出现
    /// 「画面缩成贴底横条」; TextureView 是普通视图层, 退后台不销毁, 回前台画面原样保留。
    /// 宿主 (VideoHost) 只面向本接口, 载体可插拔: 默认纹理视图, 低配机型自检不过自动回退表面视图。
    /// </summary>
    public interface IVideoCarrierView
    {
        /// <summary>接到此载体上的播放内核 (set 触发载体接线/解线)。</summary>
        LibVLCSharp.Shared.MediaPlayer? MediaPlayer { get; set; }

        /// <summary>载体对应的视图 (用于加入视图树 / Post 调度)。</summary>
        View AsView { get; }

        /// <summary>宿主上下文 (日志目录 / Toast 用)。</summary>
        Context HostContext { get; }

        /// <summary>表面载体的 Holder Surface (纹理载体恒为 null, setFrameRate 类调用据此跳过)。</summary>
        Surface? HolderSurface { get; }
    }

    /// <summary>官方 SurfaceView 版 VideoView 的载体适配: 路径与既有真机验证过的稳定版完全一致。</summary>
    public class VideoCarrierAdapter : IVideoCarrierView
    {
        private readonly VideoView _inner;
        public VideoCarrierAdapter(VideoView inner) { _inner = inner; }
        public MediaPlayer? MediaPlayer
        {
            get => _inner.MediaPlayer;
            set => _inner.MediaPlayer = value;
        }
        public View AsView => _inner;
        public Context HostContext => _inner.Context!;
        public Surface? HolderSurface => _inner.Holder?.Surface;
    }

    /// <summary>
    /// 纹理视图播放载体 (方案A, 参考道映电视版 LiveTextureView 同构做法):
    /// 接线方式与官方 VideoView (SurfaceView 版) 一致——AWindow 挂屏 + 布局监听,
    /// 仅载体换成 TextureView, 退后台画面不回收、回前台不需要重建时序。
    /// v3.22.2 热修 (真机首启白屏「内核加载失败」+ 二次打开闪退):
    /// libvlc AWindow 在 attachViews 时会立刻 new Surface(textureView.getSurfaceTexture()),
    /// 视图还没渲染出 SurfaceTexture 时这一步直接抛「surfaceTexture must not be null」——
    /// 首启在 OnCreate 就接内核必中; 后台销毁纹理后返回重建同路径再中, 空引用直接崩。
    /// 根治: 接线前必须等 IsAvailable (纹理就绪), 未就绪走延迟重试绝不空手挂屏;
    /// 挂屏/重挂全程兜异常, 重试失败安全暂停, 任何路径不许把进程带崩。
    /// </summary>
    public class AllPlayerTextureView : TextureView, AWindow.ISurfaceCallback, IVLCVout.ICallback, IVideoCarrierView
    {
        MediaPlayer? _mediaPlayer;
        AWindow? _awindow;
        LayoutChangeListener? _layoutListener;
        // v3.22.2: 纹理就绪闸与重试调度
        bool _attachPending;      // 已有一队挂屏重试在排队
        bool _attachFailed;       // 上一次挂屏失败 (连续失败时降低重试频率)
        bool _disposed;
        // v3.22.3: 窗口尺寸同步调度
        bool _syncPending;        // 已有一队首帧前尺寸同步在排队

        public AllPlayerTextureView(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

        public AllPlayerTextureView(Context context) : base(context) { }

        public MediaPlayer? MediaPlayer
        {
            get => _mediaPlayer;
            set
            {
                if (_mediaPlayer == value) return;
                // v3.22.1 (0004 评审 P1-4): 交接顺序改为「先挂新再释放旧」——
                // 旧 AWindow 只先摘画面出口 (时序与既有真机验证过的版本一致), 但 Dispose 挪到
                // 新内核 SetAndroidContext 之后, 杜绝旧内核残余 native 引用撞上已释放 AWindow 的 use-after-free。
                var oldWindow = _awindow;
                var oldListener = _layoutListener;
                if (oldWindow != null)
                {
                    try { oldWindow.RemoveCallback(this); } catch { }
                    try { oldWindow.DetachViews(); } catch { }
                }
                _mediaPlayer = value;
                _awindow = null;
                _layoutListener = null;
                if (value != null) Attach();   // 新 AWindow + SetAndroidContext(新内核); 纹理未就绪则排队重试
                if (oldListener != null) RemoveOnLayoutChangeListener(oldListener);
                oldListener?.Dispose();
                try { oldWindow?.Dispose(); } catch { }
            }
        }

        public View AsView => this;
        public Context HostContext => Context!;
        public Surface? HolderSurface => null;   // 纹理载体无 Holder, setFrameRate 走跳过路径

        private void Attach()
        {
            if (_disposed) return;
            if (_mediaPlayer is null || _awindow is not null) return;
            // v3.22.2 时序闸: 纹理没就绪绝不挂屏 (AWindow 会立刻 new Surface(getSurfaceTexture()))。
            // 未就绪 → 排队延迟重试, 等纹理就绪后自动完成接线, 任何路径不许空引用崩溃。
            if (!IsAvailable)
            {
                ScheduleRetryAttach();
                return;
            }
            try
            {
                _awindow = new AWindow(this);
                _awindow.AddCallback(this);
                _awindow.SetVideoView(this);   // 纹理载体重载
                _awindow.AttachViews();
                _mediaPlayer.SetAndroidContext(_awindow.Handle);
                _layoutListener = new LayoutChangeListener(_awindow);
                AddOnLayoutChangeListener(_layoutListener);
                // v3.22.3 首开尺寸修复: 布局监听是布局定型后才挂上的, 错过那次 onLayoutChange
                // 就再没人喂尺寸, AWindow 按默认窗口输出 → 画面缩错位置。
                // 挂屏成功立即用当前实际宽高显式同步一次; 尚未布局(宽高为0)则由
                // OnSizeChanged / 首帧前 Post 兜底, 绝不丢首开尺寸。
                SyncWindowSize();
                PostSyncWindowSizeOnce();
                _attachFailed = false;
            }
            catch (Exception ex)
            {
                // v3.22.2 安全暂停 + 自动重试: 挂屏任何异常不许把进程带崩。
                // 清掉残留接线后再排队重试; 反复失败拉长间隔 (稳定态由下次启动的载体自检兜底)。
                try { ClearAttachedState(); } catch { }
                _attachFailed = true;
                Notice("纹理挂屏异常, 进入安全重试: " + ex.Message);
                ScheduleRetryAttach();
            }
        }

        /// <summary>排队一次延迟重试; 同一时刻只排一队, 绝不堆积。</summary>
        private void ScheduleRetryAttach()
        {
            if (_disposed || _attachPending || _mediaPlayer is null) return;
            _attachPending = true;
            var delay = _attachFailed ? 1000 : 50;   // 失败过就拉长间隔, 别空转
            try { PostDelayed(new Java.Lang.Runnable(TryRetryAttachOnce), delay); }
            catch { _attachPending = false; }
        }

        private void TryRetryAttachOnce()
        {
            _attachPending = false;
            if (_disposed) return;
            if (_mediaPlayer is null || _awindow is not null) return;
            if (!IsAvailable) { ScheduleRetryAttach(); return; }
            Attach();
        }

        // v3.22.3: 窗口尺寸显式同步——AWindow 拿到视图真实宽高才不会把画面输出到错误窗口。
        private void SyncWindowSize()
        {
            if (_disposed || _awindow == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;   // 尚未布局, 等 OnSizeChanged / 首帧前兜底
            try { _awindow.SetWindowSize(w, h); }
            catch (Exception ex) { Notice("窗口尺寸同步异常: " + ex.Message); }
        }

        /// <summary>布局完成后、首帧绘制前再同步一次窗口尺寸 (Post 一帧, 只排一队)。</summary>
        private void PostSyncWindowSizeOnce()
        {
            if (_disposed || _syncPending) return;
            _syncPending = true;
            try { Post(new Java.Lang.Runnable(() => { _syncPending = false; SyncWindowSize(); })); }
            catch { _syncPending = false; }
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            // v3.22.3: 视图尺寸变化即同步, 杜绝挂屏后错过唯一一次布局事件
            SyncWindowSize();
            // v3.22.8 (老板反馈竖切横偶发画面错位): 旋转过程布局连跳几档,
            // 当帧尺寸可能是中间态——隔一帧再同步一次, 布局定型后按最终尺寸定版
            PostSyncWindowSizeOnce();
        }

        private void Detach()
        {
            if (_awindow != null)
            {
                _awindow.RemoveCallback(this);
                _awindow.DetachViews();
            }
            if (_layoutListener != null) RemoveOnLayoutChangeListener(_layoutListener);
            _layoutListener?.Dispose();
            _layoutListener = null;
            _awindow?.Dispose();
            _awindow = null;
        }

        /// <summary>把可能残留的 AWindow / 布局监听清干净 (重试自救用), 全程吞异常。</summary>
        private void ClearAttachedState()
        {
            try { Detach(); } catch { }
        }

        /// <summary>强制重挂画面出口 (v3.22.1, 0004 评审 P1-3): AWindow 画面层摘下再挂上,
        /// 逼画面输出重走创建链; 纹理载体回前台轻路径的兜底, 防极端机型回前台画面不恢复。
        /// v3.22.2: 纹理未就绪时改为排队重试, 不再直接重挂 (同根空引用风险)。</summary>
        public void ForceRemount()
        {
            if (_disposed) return;
            if (_awindow == null) { TryRetryAttachOnce(); return; }
            if (!IsAvailable)
            {
                try { _awindow.DetachViews(); } catch { }
                ScheduleRetryAttach();
                return;
            }
            try { _awindow.DetachViews(); } catch { }
            try
            {
                _awindow.AttachViews();
                // v3.22.3: 重挂后同样显式补一次尺寸同步 (后台返回路径)
                SyncWindowSize();
                PostSyncWindowSizeOnce();
            }
            catch (Exception ex)
            {
                // 重挂失败安全重试: 清干净后等纹理就绪再接
                try { ClearAttachedState(); } catch { }
                Notice("纹理重挂异常, 进入安全重试: " + ex.Message);
                ScheduleRetryAttach();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                Detach();
            }
            base.Dispose(disposing);
        }

        /// <summary>载体自救日志: 落到宿主 carrier_notice.log (拿不到上下文就静默)。</summary>
        private void Notice(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(Context!.FilesDir!.AbsolutePath!, "carrier_notice.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
            }
            catch { }
        }

        /// <summary>IVLCVout 回调: 无附加动作。</summary>
        public void OnSurfacesCreated(IVLCVout? vout) { }

        /// <summary>IVLCVout 回调: 无附加动作。</summary>
        public void OnSurfacesDestroyed(IVLCVout? vout) { }

        void AWindow.ISurfaceCallback.OnSurfacesCreated(AWindow? aWindow) { }

        void AWindow.ISurfaceCallback.OnSurfacesDestroyed(AWindow? aWindow) { }
    }

    /// <summary>
    /// 画面载体启动自检 (v3.22.0, 沿用道映 v1.0.7 同款自愈):
    /// 纹理载体在个别低配机型上有崩溃风险, 用「启动—首帧」存活判定兜底:
    /// - 每次以纹理载体启动前打 attempt 标记;
    /// - 首帧落地或正常退后台即清标记 (载体兼容);
    /// - 上一次启动的标记未清 (进程死在起播过程) → 永久回退表面视图, 绝不二次闪退。
    /// 决策纯逻辑在 CarrierDecision (可测试), 本类只做存取与落日志。
    /// </summary>
    public static class CarrierPolicy
    {
        const string PrefName = "carrier";

        static ISharedPreferences Prefs(Context c) =>
            c.GetSharedPreferences(PrefName, FileCreationMode.Private)!;

        /// <summary>写整型偏好并落盘：编辑器绑定点位标注可空，Android 实现恒返回编辑器，判空兜底跳过。</summary>
        static void PutPrefsInt(ISharedPreferences p, params (string Key, int Value)[] items)
        {
            var ed = p.Edit();
            if (ed == null) return;
            foreach (var (key, value) in items) ed.PutInt(key, value);
            ed.Apply();
        }

        /// <summary>启动时决定载体: 纹理可用返回 true; 上一次纹理启动没活到首帧则回退表面视图。</summary>
        public static bool ResolveStartupCarrier(Context c)
        {
            var p = Prefs(c);
            var mode = p.GetInt("mode", 1);
            var attempt = p.GetInt("attempt", 0);
            bool useTexture = AllPlayer.Model.CarrierDecision.UseTexture(mode, attempt);
            if (!useTexture && mode == 1)
            {
                // 上一次纹理启动没活到首帧: 判定不兼容, 永久回退 (落盘 mode=0)
                PutPrefsInt(p, ("mode", 0), ("attempt", 0));
                Notice(c, "上次纹理载体启动未存活到首帧，自动回退稳定画面载体");
            }
            return useTexture;
        }

        public static void MarkAttempt(Context c) =>
            PutPrefsInt(Prefs(c), ("attempt", 1));

        /// <summary>首帧落地 (或正常退后台) = 载体兼容, 清标记。
        /// (0004 复验修正) 仅当有起播尝试在测 (attempt==1) 才写盘, 防 TimeUpdated 每帧落盘。</summary>
        public static void MarkOk(Context c)
        {
            var p = Prefs(c);
            if (p.GetInt("attempt", 0) != 1) return;
            PutPrefsInt(p, ("attempt", 0), ("ok", 1));
        }

        /// <summary>纹理接线当场报错: 永久回退, 并把原因写进日志。</summary>
        public static void DisableTexture(Context c, string reason)
        {
            PutPrefsInt(Prefs(c), ("mode", 0), ("attempt", 0));
            Notice(c, "画面载体回退稳定模式: " + reason);
        }

        static void Notice(Context c, string msg)
        {
            try
            {
                File.AppendAllText(NoticePath(c),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
            }
            catch { }
        }

        static string NoticePath(Context c) =>
            Path.Combine(c.FilesDir!.AbsolutePath!, "carrier_notice.log");
    }
}
