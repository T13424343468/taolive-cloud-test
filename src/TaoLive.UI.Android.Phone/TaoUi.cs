using System;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AllPlayer.Model;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// Tao 界面工厂 (v3.0 重做版): 主页/播放页/下载面板所有控件统一从这里出。
    /// 颜色一律取自 TaoTheme ( TaoPalette 唯一事实源), 界面层不许出现写死的色值;
    /// 按钮=纯图标加大、整格命中; 输入框=圆角卡片; 提示=说人话。
    /// </summary>
    internal static class TaoUi
    {
        /// <summary>dp -> px</summary>
        public static int Dp(Activity act, float dp) => (int)(dp * act.Resources!.DisplayMetrics!.Density);

        public static float Density(Activity act) => act.Resources!.DisplayMetrics!.Density;

        /// <summary>圆角面板底 (控件面, 无描边), 输入框/卡片统一外观。
        /// v3.5 对齐 ColorOS: 分层靠大圆角 + 柔和投影, 不再画细描边 (道系色板不变)。</summary>
        public static GradientDrawable Card(Activity act, float cornerDp = 24, float strokeDp = 0)
        {
            var gd = new GradientDrawable();
            gd.SetColor(TaoTheme.Face);
            if (strokeDp > 0) gd.SetStroke(Math.Max(1, (int)Math.Ceiling(strokeDp * Density(act))), TaoTheme.Stroke);
            gd.SetCornerRadius(cornerDp * Density(act));
            return gd;
        }

        /// <summary>Tao 图标: 全部自绘单色矢量 (TaoIcon.IconView, 与 TaoZip 手机版同一体系),
        /// 不依赖手机字库也不依赖资源文件, 任何机型不会出现方框;
        /// 统一主题色着色; 尺寸居中不放大, 符合「不用大, 要合适」。</summary>
        public static IconView MakeIcon(Activity act, IconKind kind, float sizeDp, Color? color = null)
        {
            var iv = new IconView(act);
            iv.Bind(kind, color ?? TaoTheme.TextMain);
            // v3.11 关键修复: 图标自身绝不可点 —— 玻璃圆钮里图标一旦可点, 点图标那一下被它吃掉,
            // 外层按钮的 Click 收不到, 表现就是「只有中间图标按着灵, 整块按不动」。
            // 挂了 Click 的视图框架会自动恢复可点, 这里只管当纯图案用。
            iv.Clickable = false;
            iv.Focusable = false;
            iv.FocusableInTouchMode = false;
            return iv;
        }

        /// <summary>换图标并保持主题色着色 (播放/暂停、全屏进出这类状态切换用)。</summary>
        public static void SetIcon(IconView iv, IconKind kind, Color? color = null)
            => iv.Bind(kind, color ?? TaoTheme.TextMain);

        /// <summary>换玻璃圆钮里的图标 (播放/暂停、全屏进出这类状态切换用)。</summary>
        public static void SetGlassIcon(FrameLayout btn, IconKind kind, Color? color = null)
        {
            if (btn.GetChildAt(0) is IconView iv) iv.Bind(kind, color ?? FunctionalColors.OnGlass());
        }

        /// <summary>描边胶囊按钮 (图标 + 文字), 主页动作条用; v3.5 全圆角胶囊对齐 ColorOS。</summary>
        public static LinearLayout MakePill(Activity act, IconKind kind, string label, Action click)
        {
            float d = Density(act);
            var pill = new LinearLayout(act) { Orientation = Orientation.Horizontal };
            pill.SetGravity(GravityFlags.Center);
            pill.Clickable = true;
            pill.Background = Card(act, 24);

            var ic = MakeIcon(act, kind, 16, TaoTheme.Accent);
            var lb = new TextView(act) { Text = label, TextSize = 14 };
            lb.SetIncludeFontPadding(false);
            lb.SetTextColor(TaoTheme.TextMain);
            pill.AddView(ic, new LinearLayout.LayoutParams(Dp(act, 18), Dp(act, 18)));
            pill.AddView(lb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            ((LinearLayout.LayoutParams)lb.LayoutParameters!).LeftMargin = Dp(act, 7);
            pill.Click += (s, e) => click();
            return pill;
        }

        /// <summary>圆角卡片输入框 (去系统下划线)</summary>
        public static EditText MakeInput(Activity act, string hint)
        {
            var et = new EditText(act) { Hint = hint, TextSize = 15 };
            et.SetSingleLine(true);
            et.SetTextColor(TaoTheme.TextMain);
            et.SetHintTextColor(TaoTheme.TextSub);
            et.Background = Card(act, 22);
            et.SetPadding(Dp(act, 16), Dp(act, 13), Dp(act, 16), Dp(act, 13));
            return et;
        }

        /// <summary>玻璃质感小圆钮 (播放页控制行): 半透明玻璃底 + 居中小图标, 图标显式尺寸不撑满。</summary>
        public static FrameLayout MakeGlassBtn(Activity act, IconKind kind, float iconDp, Action click)
        {
            var btn = new FrameLayout(act);
            var gd = new GradientDrawable();
            gd.SetShape(ShapeType.Oval);
            gd.SetColor(FunctionalColors.GlassFill());
            gd.SetStroke((int)Math.Ceiling(1 * Density(act)), FunctionalColors.GlassStroke());
            btn.Background = gd;
            btn.Clickable = true;

            var iv = MakeIcon(act, kind, iconDp, FunctionalColors.OnGlass());
            var lp = new FrameLayout.LayoutParams(Dp(act, iconDp), Dp(act, iconDp));
            lp.Gravity = GravityFlags.Center;
            btn.AddView(iv, lp);
            btn.Click += (s, e) => click();
            return btn;
        }

        /// <summary>列表行内的观看进度细条: 轨道 + 点缀色填充; v3.5 圆头细条对齐 ColorOS。</summary>
        public static LinearLayout MakeProgressTrack(Activity act)
        {
            var track = new LinearLayout(act) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
            var trackBg = new GradientDrawable();
            trackBg.SetColor(TaoTheme.Stroke);
            trackBg.SetCornerRadius(Dp(act, 2));   // 圆头轨道, 填充随裁切一起圆
            track.Background = trackBg;
            track.OutlineProvider = ViewOutlineProvider.Background;
            track.ClipToOutline = true;
            var fill = new View(act) { Background = new ColorDrawable(TaoTheme.Accent) };
            var rest = new View(act);
            track.AddView(fill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent) { Width = 0, Weight = 0 });
            track.AddView(rest, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent) { Width = 0, Weight = 1 });
            return track;
        }

        /// <summary>系统状态栏高度 (px); 取不到给 24dp 兜底</summary>
        public static int StatusBarHeight(Activity act)
        {
            var res = act.Resources!;
            int sid = res.GetIdentifier("status_bar_height", "dimen", "android");
            return sid > 0 ? res.GetDimensionPixelSize(sid) : Dp(act, 24);
        }

        /// <summary>挖孔/刘海安全区高度 (px, v3.22.5): API 28 以下或取不到给 0。
        /// 顶栏安全高度取状态栏与挖孔二者的较大值, 避免按钮压进开孔区。</summary>
        public static int TopCutoutInset(Activity act)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return 0;
            try
            {
                var cutout = act.Window!.DecorView!.RootWindowInsets?.DisplayCutout;
                return Math.Max(cutout?.SafeInsetTop ?? 0, 0);
            }
            catch { return 0; }
        }

        /// <summary>挖孔/刘海左右安全区 (px, v3.25.19): 横屏挖孔在侧边机型的顶栏左右内边距依据。
        /// 取左右两侧较大值; API 28 以下或取不到给 0。</summary>
        public static int SideCutoutInset(Activity act)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return 0;
            try
            {
                var cutout = act.Window!.DecorView!.RootWindowInsets?.DisplayCutout;
                return Math.Max(cutout?.SafeInsetLeft ?? 0, cutout?.SafeInsetRight ?? 0);
            }
            catch { return 0; }
        }

        /// <summary>系统导航栏高度 (px); 取不到给 12dp 兜底</summary>
        public static int NavBarHeight(Activity act)
        {
            var res = act.Resources!;
            int bid = res.GetIdentifier("navigation_bar_height", "dimen", "android");
            return bid > 0 ? res.GetDimensionPixelSize(bid) : Dp(act, 12);
        }

        /// <summary>轻提示: 短时自动消失的浮层文字</summary>
        public static void Toast(Activity act, string msg, bool long_ = false)
        {
            global::Android.Widget.Toast.MakeText(act, msg, long_ ? ToastLength.Long : ToastLength.Short)!.Show();
        }
    }
}
