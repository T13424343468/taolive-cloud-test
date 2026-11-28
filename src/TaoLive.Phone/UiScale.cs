namespace TaoLive.Phone;

/// <summary>
/// 界面缩放档（vc53 显示大小口径，2.0 自实现）：T(系数)=基准字号×缩放系数，Sp(裸字号)/Px(裸像素)随档缩放。
/// 显示大小档（真机反馈批 #61 尺寸回抬令 2026-11-15 19:3x 老板拍板「中档=肉眼最舒适」）：
/// 大=2/3(24sp)、中=5/9(20sp)、小=1/3(12sp)，默认中；覆盖 #14 的整体×2/3 压缩口径。
/// #68 语义字号令（2026-11-15 老板拍板）：标题/项/说明三类语义字号（Title/Item/Caption），
/// 同角色跨页面同字号；Sp/Px 入参重定标为「中档实值」（#15 时把最终观感值又乘了一遍档系数的错账就此拨正）。
/// #69 屏宽自适应令（2026-11-15 老板拍板）：全部实值随屏宽走（1920px 宽=基准 1:1），防写死兼容性问题。
/// Sp(裸字号)/Px(裸像素)随档缩放（#16-#19 全元素跟档口径保留），prefs 键 ui_scale / taolive_prefs；
/// 改档由 CycleDisplaySize 走 Recreate() 即时生效——真机反馈批#15 原地换皮：会话跨重建存活，正在看的台不断流。
/// </summary>
public static class UiScale
{
    public const float BaseSize = 36f; // 基准字号（sp），系数 1.0 时的实字号
    public const float SpecBase = 20f; // #68 语义字号令：中档实字号基准（#61 拍板「中档=20sp」）
    /// <summary>vc135 双跟随令（2026-11-28 老板拍板「全落」）：Factor=中档基准 17/36 × 首启 fontScale
    /// （OnCreate 装载 FontScaleRef 后由装载块计算），首启所见=小档基准——系统字体滑条停最小档时
    /// 界面所见与旧版完全一致；滑条往右切档只改 TierRatio，不重复放大。</summary>
    public static float Factor { get; set; } = 17f / 36f; // 默认「中」=基准 17sp（#77 档位重定 2026-11-15 老板定值：大21/中17/小13=换算实值÷基准36；存量档位值不匹配走回落「中」）

    /// <summary>vc135 双跟随令：首启 fontScale 基准（prefs 键 ui_fontscale_ref，OnCreate 拍板存档）。
    /// 首启所见=小档基准；此后系统字体滑条相对它的比值切档。</summary>
    public static float FontScaleRef { get; set; } = 1f;

    /// <summary>vc135：当前系统 fontScale（OnCreate/重建时重读）。文字 sp 渲染层自乘的 fontScale
    /// 在 T()/Scale 里归一除掉，滑条只通过 TierRatio 切档，不重复放大。</summary>
    public static float FontScaleNow { get; set; } = 1f;

    /// <summary>vc135：字体滑条档位比例（相对首启基准）。滑条 5 档归并三档：
    /// 比值 ≤1.05→小(1.0)、≤1.2→中(1.1)、>1.2→大(1.2)（老板拍板步距「中10% 高20」）。
    /// 间距/图标/盒子与文字跟同一套档位比例，文字与间距永远同比例。</summary>
    public static float TierRatio { get; private set; } = 1f;

    /// <summary>vc135：装载当前 fontScale 并切档（OnCreate/重建时调用）。fontScaleNow ≤0 回落首启基准。</summary>
    public static void UpdateFontScale(float fontScaleNow)
    {
        FontScaleNow = fontScaleNow > 0f ? fontScaleNow : FontScaleRef;
        float r = FontScaleNow / (FontScaleRef > 0f ? FontScaleRef : 1f);
        TierRatio = r <= 1.05f ? 1f : (r <= 1.2f ? 1.1f : 1.2f);
    }

    /// <summary>vc117 全元素比例换算令（2026-11-28 老板拍板）：所有界面元素（文字/间距/线框盒/图标/圆角）
    /// 统一按屏幕比例换算——短边像素对 390 基准（分辨率比例项），不再分文字/间距两套口径。
    /// ScreenScale=短边px/(390×首启密度)：纯分辨率比例，密度不进比例项。</summary>
    public static float ScreenScale { get; set; } = 1f;

    /// <summary>vc117 跟随系统显示大小令（2026-11-28 老板拍板「应该根据系统设置变化」）：
    /// 系统显示大小改密度，界面等比跟随——SystemScale=当前密度/首启密度（首启时系统档=「中」基准）。
    /// 系统小档→界面缩小、标准=中、大档=放大，三套档随系统走，不单独给设置项（2026-11-27 口径沿用）。</summary>
    public static float SystemScale { get; set; } = 1f;

    /// <summary>首启密度基准（vc117）：首启时拍板存档（ui_density_ref），此后系统显示大小怎么改都以它为「中」。
    /// OnCreate 装载；存量安装首启即拍当前档，与老板「目前这个作为中」口径一致。</summary>
    public static float DensityRef { get; set; } = 1f;

    /// <summary>手机版间距档（2026-11-27 真机整改令）：Px 入参按「390dp 基准 1:1」的 dp 值计。
    /// vc117 起统一口径：PxScale=ScreenScale×SystemScale——间距/线框盒/图标与文字同一套比例换算
    /// （分辨率比例×系统显示大小跟随），密度不再单独进间距（热区由 390 基准设计值保证可点）。
    /// OnCreate 装载。</summary>
    public static float PxScale { get; set; } = 1f;

    /// <summary>总缩放系数（#68 重定标+#69 屏宽自适应+vc135 双跟随）：裸最终值 × Scale = 当前档实值；
    /// 中档 1920 宽=1.0 原值直出。TierRatio 进比例项、渲染层自乘的 fontScale 归一除掉。</summary>
    public static float Scale => BaseSize / SpecBase * Factor * ScreenScale * TierRatio / FontScaleNow;

    public static float T(float f) => f * BaseSize * Factor * ScreenScale * TierRatio / FontScaleNow;

    /// <summary>#68 语义字号令：标题/项/说明三类，同角色同字号，列表/设置/弹窗观感一致。</summary>
    public static float Title => T(1.4f);   // 标题：23.8sp@中档17（抽屉页头/设置面板标题/退出弹窗标题）
    public static float Item => T(1.1f);    // 项：18.7sp@中档17（频道行/页签/设置行/按钮/当前节目）
    public static float Caption => T(0.9f); // 说明：15.3sp@中档17（台数/帮助行/次要说明/下档节目）

    /// <summary>
    /// 裸最终 sp 字号随档缩放（真机反馈批#15 尺寸令 2026-11-15 老板拍板：所有页面所有元素跟三档走；
    /// #68 重定标：入参按「中档实字号」计——#15 把最终观感值又乘了一遍档系数，字缩了一半多，就此拨正）。
    /// </summary>
    public static float Sp(float v) => v * Scale;

    /// <summary>vc120 全元素去写死令（2026-11-28 老板拍板「全应用不允许有写死」）：
    /// 字号档比例项——中档=1.0，大档大于1、小档小于1。Px 口径并入：间距/盒子/图标与文字同一套
    /// 缩放基准（屏宽比例 × 系统显示大小 × 字号档），全应用不再有不跟档的固定值。
    /// vc135 双跟随令：字号档比例项改由 TierRatio 承担（滑条 5 档归 3 档），FontScaleRatio 退场。</summary>
    /// <summary>裸最终 px 尺寸（2026-11-27 手机整改令）：入参按 390dp 基准的 dp 值计，乘间距档转像素。
    /// vc120 起并入字号档比例项（全元素去写死令），与文字同一套缩放；vc135 起跟 TierRatio。</summary>
    public static int Px(float v) => (int)(v * PxScale * TierRatio + 0.5f);

    /// <summary>vc127 令（2026-11-28 老板点名落「提示胶囊左内边距偏窄约7dp」）：物理 dp 直读口径。
    /// 根因实锤：PxScale=短边px/(390×首启密度)×系统档 ≈1，Px(20) 出来是 20 裸像素 ≈7dp 物理宽
    /// （真机截图实测吻合）——「390 基准 1:1」的设计单位不等于物理 dp。HUD 胶囊这类要按物理 dp
    /// 观感计的尺寸走本口径：入参 dp × 当前密度（首启密度×系统显示大小）× 字号档，随档跟缩不写死。</summary>
    public static int Dp(float v) => (int)(v * DensityRef * SystemScale * TierRatio + 0.5f);

    /// <summary>vc120 肉眼对齐令（2026-11-28 老板拍板）：同字号单行 TextView 实测线高（px），
    /// 按钮盒/对齐基准用——字号档位、系统字体缩放自动跟，不写死数值。</summary>
    public static int TextLineHeight(Android.Content.Context ctx, float size)
    {
        var tv = new Android.Widget.TextView(ctx) { TextSize = size, Text = "测" };
        tv.SetSingleLine(true);
        tv.Measure(Android.Views.View.MeasureSpec.MakeMeasureSpec(0, Android.Views.MeasureSpecMode.Unspecified),
                   Android.Views.View.MeasureSpec.MakeMeasureSpec(0, Android.Views.MeasureSpecMode.Unspecified));
        return tv.MeasuredHeight;
    }

    // —— vc119 全局间距口径（2026-11-28 老板拍板：块间距/行间距各一个数，全页面统一）——
    /// <summary>左右边距：所有页面统一按设置页口径 24dp。</summary>
    public const float EdgePadDp = 24f;
    /// <summary>块间距：卡片/区块之间统一 20dp（大于行间距，块与块之间松）。</summary>
    public const float BlockGapDp = 20f;
    /// <summary>行间距：块内部行与行统一 10dp（块内更紧凑）。</summary>
    public const float RowGapDp = 10f;
    /// <summary>按钮间距：相邻按钮统一 8dp。</summary>
    public const float BtnGapDp = 8f;
    /// <summary>vc120 肉眼对齐令：按钮线框盒不再写死——盒高=旁边文字实际渲染高（控制条跟台名 Item、
    /// 列表页头跟标题 Title），触控热区透明外扩 BtnTouchPadDp。BtnBoxDp 退场仅留档位参考。</summary>
    public const float BtnBoxDp = 44f;
    /// <summary>vc120：按钮可见盒外的透明触控外扩边（dp），保证小盒好按。</summary>
    public const float BtnTouchPadDp = 10f;
}
