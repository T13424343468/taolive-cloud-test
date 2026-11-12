using Android.Util;

namespace TaoLive.UI.Android.Tv.Shared;

/// <summary>
/// 全软件自适应尺寸体系（vc41，老板 2026-11-12 20:33 令、20:34 拍板）：
/// - 一把尺子量全软件：启动按设备屏幕尺寸算定全部令牌并冻结，运行期永不再变——
///   同设备宽度位置恒定，换设备全套等比缩放；
/// - 基准单位 U = 屏宽 / 40（@老板真机横屏 1280px、density 2 → U = 32px = 16dp，
///   现有界面值与令牌一一对应，观感即当前调好的样子）；
/// - 所有间距/构件 ∝ 屏宽（像素），所有字号 ∝ 屏宽（dp 赋值换算）；
/// - 两条原则（老板令）：①行内容一行完整禁换行——面板宽 ∝ 屏宽、内容宽 ∝ 字号 ∝ 屏宽，
///   比值恒定，任何设备同时成立；②宽度位置不随内容/时间变化。
/// </summary>
public static class UiScale
{
    private static float _uPx;  // 基准单位（像素）
    private static float _uDp;  // 基准单位（dp，TextSize 赋值换算用）
    private static bool _frozen;

    /// <summary>列表面板宽比例：屏宽的 2/3（老板 20:17 拍板，定比、每次打开一样宽）。</summary>
    public const float PanelRatio = 2f / 3f;

    /// <summary>启动调用一次并冻结；重复调用不重算（运行期不变原则）。</summary>
    public static void Init(DisplayMetrics m)
    {
        if (_frozen) return;
        _frozen = true;
        _uPx = m.WidthPixels / 40f;
        _uDp = _uPx / m.Density;
    }

    /// <summary>测试/预览注入（等价 Init；重复调用不重算）。</summary>
    public static void InitForTest(int screenW, float density)
    {
        if (_frozen) return;
        _frozen = true;
        _uPx = screenW / 40f;
        _uDp = _uPx / density;
    }

    /// <summary>令牌间距：mul 倍 U（像素）。常用：边距 1.5、行距 1、半距 0.5。</summary>
    public static int P(float mul) => (int)(mul * _uPx);

    /// <summary>令牌字号：mul 倍 U（返回 TextSize 所需 dp 值）。</summary>
    public static float T(float mul) => mul * _uDp;

    /// <summary>历史 dp 值折令牌：16dp = 1U（真机上等值，其他设备 ∝ 屏宽）。</summary>
    public static int FromDp(float dp) => P(dp / 16f);

    /// <summary>面板宽（像素）= 屏宽 × PanelRatio，定比。</summary>
    public static int PanelWidthPx(int screenW) => (int)(screenW * PanelRatio);
}
