using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;

namespace TaoLive.Phone;

/// <summary>
/// 全局透明化视觉基线（vc53 完整版整套搬运；v1.0.8 第15项，用户 23:25 拍板）：
/// - 所有焦点/确认选中效果按透明做——效果层=低透明度细边框+极淡底色，绝不复用旧实色高亮；
/// - 面板/列表底色同样极淡（播放画面可见），越透明越好；
/// - 底线：低配电视上肉眼可辨，不因过度透明看不见焦点；
/// - 统一一套参数，覆盖频道列表、分类树、设置面板、收藏排序、FlashInfo，与水墨主题一致。
/// vc53 增补（v1.0.9 第③项）：亮度自适应（暗房底色加深一档）、信息条/亮牌/当前台高亮独立档。
/// </summary>
public static class InkTranslucency
{
    // 水墨底色（与主题一致，只调透明度）
    public const byte InkR = 0x2B, InkG = 0x33, InkB = 0x3B;
    public const byte PaperR = 0xF7, PaperG = 0xF5, PaperB = 0xEE; // 效果层浅色（细边框/极淡底）
    // 强调色（水墨 Accent #8A5C3D）：当前台高亮底、可读性兜底共用
    public const byte AccentR = 0x8A, AccentG = 0x5C, AccentB = 0x3D;

    /// <summary>面板/列表底色透明度：vc119 老板令（真机「透出太强」）76→208——压深保文字干净，
    /// 仍留一档水墨透光；低亮度 Boost 再加深。</summary>
    public const int PanelAlpha = 208;

    /// <summary>进阶调优页底色：加深一档，不跟画面亮度走（vc41 口径；vc119 老板令 120→232 压深保可读）。</summary>
    public const int PanelDeepAlpha = 232;

    /// <summary>焦点/确认效果层底色透明度：v1.0.9 第③项加深（40→64），高亮更醒目。</summary>
    public const int FocusFillAlpha = 64;

    /// <summary>焦点细边框透明度：v1.0.9 第③项加深（104→160），低配电视肉眼清晰可辨。</summary>
    public const int FocusStrokeAlpha = 160;

    /// <summary>FlashInfo/信息条底色透明度：#66 老板令 2026-11-15 起改按系统亮度连续自适应——
    /// 原固定 200 过闷；亮屏（亮画面上白字需深底托住）取 152、暗房取 88 高透，中间线性插值，
    /// 文字清晰由可读性阴影兜底。范围钳制 64–200。</summary>
    public const int InfoAlphaBright = 152;
    public const int InfoAlphaDark = 88;

    /// <summary>起播亮牌底色透明度。</summary>
    public const int LoadingAlpha = 216;

    /// <summary>当前台高亮底色透明度：v1.0.9 第③项新增，强调色半透明底，一眼认出正在看的台。</summary>
    public const int CurrentFillAlpha = 76;

    /// <summary>次要文字色：v1.0.9 第③项提亮（原 #7C8894 偏灰看不清）。</summary>
    public const string TextSecondary = "#D8E2EC";

    /// <summary>亮度自适应（v1.0.9 第③项）：系统亮度 ≤ 30% 视为夜间/暗房，底色再加深一档。</summary>
    public static bool IsLowBrightness(Context ctx)
    {
        try
        {
            var v = global::Android.Provider.Settings.System.GetInt(ctx.ContentResolver,
                global::Android.Provider.Settings.System.ScreenBrightness, 128);
            return v >= 0 && v <= 77; // 系统亮度 0-255，≤30% 视为低光
        }
        catch { return false; }
    }

    private static int Boost(Context ctx, int alpha) =>
        IsLowBrightness(ctx) ? System.Math.Min(255, alpha + 32) : alpha;

    /// <summary>自适应面板/列表底色：暗屏时加深一档，文字更容易看清。</summary>
    public static Color PanelColor(Context ctx) =>
        Color.Argb(Boost(ctx, PanelAlpha), InkR, InkG, InkB);

    /// <summary>进阶调优页底色：固定加深一档，不跟画面亮度走（vc41 口径）。</summary>
    public static Color PanelColorDeep(Context ctx) => Color.Argb(PanelDeepAlpha, InkR, InkG, InkB);

    /// <summary>vc119 丝滑过渡令：贴底面板背景——同 PanelColor 底色，底部两角自长圆角（半径=系统圆角），
    /// 弧线贴合屏幕角落；内容不再整体上移避让，圆角处由背景顺过去。</summary>
    public static Android.Graphics.Drawables.Drawable PanelBgWithBottomCorner(Context ctx, int bottomRadiusPx)
    {
        var d = new Android.Graphics.Drawables.GradientDrawable();
        d.SetColor(PanelColor(ctx));
        d.SetCornerRadii(new float[] { 0f, 0f, 0f, 0f, bottomRadiusPx, bottomRadiusPx, bottomRadiusPx, bottomRadiusPx });
        return d;
    }

    /// <summary>近实色水墨面板底（手机版整改令 2026-11-27）：抽屉/设置面板在手机亮画面（台标）上
    /// 半透明 76 必叠字不可读，改 232 近实色，保留水墨色调。</summary>
    public static Color PanelSolidColor(Context ctx) => Color.Argb(232, InkR, InkG, InkB);

    /// <summary>#66 提示底自适应透明度：按系统亮度 0–255 连续插值——亮屏取 InfoAlphaBright（亮画面上
    /// 白字需深底托住），暗房取 InfoAlphaDark（高透），中间线性过渡；钳制 64–200。</summary>
    private static int HintAlphaFor(Context ctx)
    {
        int bright = 128;
        try { bright = global::Android.Provider.Settings.System.GetInt(ctx.ContentResolver,
            global::Android.Provider.Settings.System.ScreenBrightness, 128); } catch { }
        float t = Math.Clamp(bright / 255f, 0f, 1f);
        return Math.Clamp((int)(InfoAlphaDark + (InfoAlphaBright - InfoAlphaDark) * t + 0.5f), 64, 200);
    }

    /// <summary>自适应信息条底色：#66 起改高透+按系统亮度连续自适应（亮屏深托字、暗房高透），文字由可读性阴影兜底。</summary>
    public static Color InfoColor(Context ctx) => Color.Argb(HintAlphaFor(ctx), InkR, InkG, InkB);

    /// <summary>vc116 提示胶囊皮（界面元素统一皮口径）：全透明填充 + 胶囊描边（纸白 FocusStrokeAlpha），
    /// 去水墨实底——提示条不再自带深底，可读性由文字投影兜底。每次亮条重取。</summary>
    public static GradientDrawable PillBackground(Context ctx)
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Transparent);
        d.SetStroke(Math.Max(1, UiScale.Px(1)), Color.Argb(FocusStrokeAlpha, PaperR, PaperG, PaperB)); // vc134 统一换算令：细线也跟档（基准≈1px 观感不变）
        d.SetCornerRadius(999f); // 胶囊
        return d;
    }

    /// <summary>提示胶囊皮（vc132 老板令「圆角弧度不用那么大」）：指定圆角半径（dp）的胶囊描边——
    /// 横屏竖放胶囊收小弧度用；传大值即全胶囊形。
    /// vc134 统一换算令（2026-11-28 老板拍板「统一」）：圆角改走 UiScale.Dp 统一口径——
    /// 原 cornerDp×Density 少乘字号档，切字号档圆角不跟；并入后与间距/文字同一套比例。</summary>
    public static GradientDrawable PillBackground(Context ctx, float cornerDp)
    {
        var d = PillBackground(ctx);
        d.SetCornerRadius(UiScale.Dp(cornerDp));
        return d;
    }

    /// <summary>vc116 界面元素统一线框皮：全透明填充 + 圆角方形描边（纸白，透明度入参）——
    /// 控制条按钮/页头 ⟳✕ 钮/筛选框统一用皮；按压态传高透明度即强描边版。</summary>
    public static GradientDrawable OutlineBox(Context ctx, float cornerDp, int strokeAlpha)
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Transparent);
        d.SetCornerRadius(UiScale.Dp(cornerDp)); // vc134 统一换算令：与胶囊皮同口径，补齐字号档
        d.SetStroke(Math.Max(1, UiScale.Px(1)), Color.Argb(strokeAlpha, PaperR, PaperG, PaperB));
        return d;
    }

    /// <summary>信息条完整皮：自适应水墨底 + 纸白细边框圆角（同 FocusDrawable 效果层同构），
    /// #66 风格统一口径——画面上的提示与面板/焦点效果同一套水墨语言。每次亮条重取，亮度变化即生效。</summary>
    public static GradientDrawable HintBackground(Context ctx)
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(InfoColor(ctx));
        d.SetStroke(Math.Max(1, UiScale.Px(1)), Color.Argb(FocusStrokeAlpha, PaperR, PaperG, PaperB));
        d.SetCornerRadius(UiScale.Dp(3)); // vc134 统一换算令：原 8 裸像素≈3dp，改走统一口径随档缩放，观感不变
        return d;
    }

    /// <summary>自适应亮牌底色：暗屏时加深一档。</summary>
    public static Color LoadingColor(Context ctx) => Color.Argb(Boost(ctx, LoadingAlpha), InkR, InkG, InkB);

    /// <summary>换台遮罩底色：固定不透明水墨深底（真机反馈批#2 修 2026-11-15，vc53「铺干净底色遮住旧内容」
    /// SwitchRevealPolicy 口径）——亮牌底 216 仍透旧画面，盖不住 GPU 残留；遮罩盖底不跟亮度走。</summary>
    public static Color SwitchVeilColor() => Color.Argb(255, InkR, InkG, InkB);

    /// <summary>当前台高亮底色：强调色半透明底，一眼认出正在看的台，暗屏时略加深。</summary>
    public static Color CurrentRowFill(Context ctx) =>
        Color.Argb(Boost(ctx, CurrentFillAlpha), AccentR, AccentG, AccentB);

    /// <summary>主文字色（v1.0.9 第③项「亮度自适应」）：暗屏（夜间/暗房）自动提到纯白。</summary>
    public static Color TextPrimary(Context ctx) =>
        IsLowBrightness(ctx) ? Color.White : Color.Argb(255, PaperR, PaperG, PaperB);

    /// <summary>可读性兜底：文字加深色投影，亮暗底上都清晰（标准 TextView API，全机型可用）。</summary>
    public static void ApplyReadabilityShadow(TextView tv) =>
        tv.SetShadowLayer(2f, 0, 1f, Color.Argb(180, 0x1A, 0x1F, 0x24));

    /// <summary>焦点/确认效果层：极淡底 + 细边框圆角（统一一套，面板行/按钮全局复用）。</summary>
    public static GradientDrawable FocusDrawable()
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Argb(FocusFillAlpha, PaperR, PaperG, PaperB));
        d.SetStroke(Math.Max(1, UiScale.Px(1)), Color.Argb(FocusStrokeAlpha, PaperR, PaperG, PaperB));
        d.SetCornerRadius(UiScale.Dp(3)); // vc134 统一换算令：原 8 裸像素≈3dp，改走统一口径随档缩放，观感不变
        return d;
    }

    /// <summary>危险项/改动项聚焦效果层：朱砂细边框+朱砂极淡底（同 FocusDrawable 同构换色）。</summary>
    public static GradientDrawable AccentFocusDrawable()
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Argb(FocusFillAlpha, AccentR, AccentG, AccentB));
        d.SetStroke(Math.Max(1, UiScale.Px(1)), Color.Argb(FocusStrokeAlpha, AccentR, AccentG, AccentB));
        d.SetCornerRadius(UiScale.Dp(3)); // vc134 统一换算令：原 8 裸像素≈3dp，改走统一口径随档缩放，观感不变
        return d;
    }
}
