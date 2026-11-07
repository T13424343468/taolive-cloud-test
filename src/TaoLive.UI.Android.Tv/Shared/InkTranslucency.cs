using Android.Graphics;

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

    /// <summary>面板/列表底色透明度（0-255，越低越透明）：极淡底，画面仍可见。</summary>
    public const int PanelAlpha = 56;

    /// <summary>焦点/确认效果层底色透明度：极淡，仅与底部区分。</summary>
    public const int FocusFillAlpha = 40;

    /// <summary>焦点细边框透明度：低配电视肉眼可辨的底线（1px 细边框）。</summary>
    public const int FocusStrokeAlpha = 104;

    /// <summary>FlashInfo/信息条底色透明度（原 160 大幅调淡，文字保持可读）。</summary>
    public const int InfoAlpha = 96;

    public static Color PanelColor() => Color.Argb(PanelAlpha, InkR, InkG, InkB);

    /// <summary>焦点/确认效果层：极淡底 + 细边框（统一一套，全局复用）。</summary>
    public static GradientDrawable FocusDrawable()
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftTop, new int[] { 0, 0 });
        d.SetColor(Color.Argb(FocusFillAlpha, PaperR, PaperG, PaperB));
        d.SetStroke(1, Color.Argb(FocusStrokeAlpha, PaperR, PaperG, PaperB));
        d.SetCornerRadius(4f * 0); // 直角细边框，水墨风
        return d;
    }
}
