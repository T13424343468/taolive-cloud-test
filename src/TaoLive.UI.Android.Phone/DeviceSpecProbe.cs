using System;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// v3.25.8 手机素质提前探清为常量 (老板 11-10 拍板): 启动即探测并缓存, 之后全局当常量读,
    /// 不反复探测。实时增强封顶 (帧率封设备最高刷新率、尺寸封面板实际像素) 与能力台账都读这份。
    /// MaxHz = 设备最高刷新率; PanelW/PanelH = 面板实际像素; 0 = 探不到 (不瞎封, 原样通过)。
    /// </summary>
    internal static class DeviceSpecProbe
    {
        /// <summary>设备最高刷新率 (Hz); 探不到 = 0。</summary>
        internal static float MaxHz { get; private set; }

        /// <summary>面板实际像素 (取支持模式里像素量最大的); 探不到 = 0。</summary>
        internal static int PanelW { get; private set; }
        internal static int PanelH { get; private set; }

        internal static bool Probed { get; private set; }

        /// <summary>启动时探一次; 重复调用直接短路, 结果恒为常量。</summary>
        internal static void WarmUp(global::Android.Content.Context ctx)
        {
            if (Probed) return;
            try
            {
                var wm = ctx.GetSystemService(global::Android.Content.Context.WindowService);
                // 反射取 getDefaultDisplay() (绑定差异, 编译期不依赖)
                var display = wm?.Class?.GetMethod("getDefaultDisplay")?.Invoke(wm, null);
                // 反射取 getSupportedModes() (绑定差异, 编译期不依赖); 逐元素取刷新率与物理像素
                var modesObj = display?.Class?.GetMethod("getSupportedModes")?.Invoke(display, null);
                if (modesObj != null)
                {
                    int n = global::Java.Lang.Reflect.Array.GetLength(modesObj);
                    for (int i = 0; i < n; i++)
                    {
                        var m = global::Java.Lang.Reflect.Array.Get(modesObj, i);
                        if (m == null) continue;
                        try
                        {
                            float r = 0; int w = 0, h = 0;
                            var getHz = m.Class?.GetMethod("getRefreshRate");
                            var getW = m.Class?.GetMethod("getPhysicalWidth");
                            var getH = m.Class?.GetMethod("getPhysicalHeight");
                            if (getHz != null) r = (float)(global::Java.Lang.Float)getHz.Invoke(m, null)!;
                            if (getW != null) w = (int)(global::Java.Lang.Integer)getW.Invoke(m, null)!;
                            if (getH != null) h = (int)(global::Java.Lang.Integer)getH.Invoke(m, null)!;
                            if (r > MaxHz) MaxHz = r;
                            if ((long)w * h > (long)PanelW * PanelH) { PanelW = w; PanelH = h; }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            Probed = true;
            if (MaxHz > 0)
            {
                string size = PanelW > 0 ? $", 面板 {PanelW}×{PanelH}" : "";
                AppLog.W($"手机素质探明 (常量): 最高刷新 {MaxHz:0.##}Hz{size}");
            }
            else
            {
                AppLog.W("手机素质: 显示模式探不到, 实时增强不封顶");
            }
        }
    }
}
