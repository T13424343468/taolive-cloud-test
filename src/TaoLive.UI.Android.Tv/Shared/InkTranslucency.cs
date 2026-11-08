using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 全局透明化视觉基线（v1.0.8 第15项，用户 23:25 拍板）：
/// - 所有焦点/确认选中效果按透明做——效果层=低透明度细边框+极淡底色，绝不复用旧实色高亮；
/// - 面板/列表底色同样极淡（播放画面可见），越透明越好；
/// - 底线：低配电视上肉眼可辨，不因过度透明看不见焦点；
/// - 统一一套参数，覆盖频道列表、分类树、设置面板、收藏排序、FlashInfo，与水墨主题一致。
/// </summary>
public static class InkTranslucency
{
    // 水墨底色（与主题一致，只调透明度）
    public const byte InkR = 0x2B, InkG = 0x33, InkB = 0x3B;
    public const byte PaperR = 0xF7, PaperG = 0xF5, PaperB = 0xEE; // 效果层浅色（细边框/极淡底）
    // 强调色（与 TaoPalette 水墨主题 Accent #8A5C3D 一致）：当前台高亮底、可读性兜底共用
    public const byte AccentR = 0x8A, AccentG = 0x5C, AccentB = 0x3D;

    /// <summary>面板/列表底色透明度（0-255）：v1.0.9 第③项调深（56→76），
    /// 亮画面上文字仍清晰可读，同时保持半透明水墨质感。</summary>
    public const int PanelAlpha = 76;

    /// <summary>焦点/确认效果层底色透明度：v1.0.9 第③项加深（40→64），高亮更醒目。</summary>
    public const int FocusFillAlpha = 64;

    /// <summary>焦点细边框透明度：v1.0.9 第③项加深（104→160），低配电视肉眼清晰可辨。</summary>
    public const int FocusStrokeAlpha = 160;

    /// <summary>FlashInfo/信息条底色透明度：v1.0.9 第③项调深（150→200），亮画面上文字保持可读。</summary>
    public const int InfoAlpha = 200;

    /// <summary>起播亮牌底色透明度。</summary>
    public const int LoadingAlpha = 216;

    /// <summary>当前台高亮底色透明度：v1.0.9 第③项新增，强调色半透明底，一眼认出正在看的台。</summary>
    public const int CurrentFillAlpha = 76;

    /// <summary>次要文字色：v1.0.9 第③项提亮（原 #7C8894 偏灰看不清）。</summary>
    public const string TextSecondary = "#D8E2EC";

    public static Color PanelColor() => Color.Argb(PanelAlpha, InkR, InkG, InkB);

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

    /// <summary>自适应信息条底色：暗屏时加深一档，文字保持可读。</summary>
    public static Color InfoColor(Context ctx) => Color.Argb(Boost(ctx, InfoAlpha), InkR, InkG, InkB);

    /// <summary>自适应亮牌底色：暗屏时加深一档。</summary>
    public static Color LoadingColor(Context ctx) => Color.Argb(Boost(ctx, LoadingAlpha), InkR, InkG, InkB);

    /// <summary>当前台高亮底色（v1.0.9 第③项）：强调色半透明底，一眼认出正在看的台，暗屏时略加深。</summary>
    public static Color CurrentRowFill(Context ctx) =>
        Color.Argb(Boost(ctx, CurrentFillAlpha), AccentR, AccentG, AccentB);

    /// <summary>主文字色（v1.0.9 第③项「亮度自适应」）：暗屏（夜间/暗房）自动提到纯白。</summary>
    public static Color TextPrimary(Context ctx) =>
        IsLowBrightness(ctx) ? Color.White : Color.Argb(255, PaperR, PaperG, PaperB);

    /// <summary>
    /// 可读性兜底（v1.0.9 第③项「亮度自适应」）：给文字加深色投影，
    /// 无论底下画面偏亮偏暗，文字始终与背景拉开对比度；标准 TextView API，全机型可用。
    /// </summary>
    public static void ApplyReadabilityShadow(TextView tv)
    {
        tv.SetShadowLayer(2f, 0, 1f, Color.Argb(180, 0x1A, 0x1F, 0x24));
    }

    /// <summary>焦点/确认效果层：极淡底 + 细边框（统一一套，全局复用）。</summary>
    public static GradientDrawable FocusDrawable()
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Argb(FocusFillAlpha, PaperR, PaperG, PaperB));
        d.SetStroke(1, Color.Argb(FocusStrokeAlpha, PaperR, PaperG, PaperB));
        d.SetCornerRadius(4f * 0); // 直角细边框，水墨风
        return d;
    }
}
