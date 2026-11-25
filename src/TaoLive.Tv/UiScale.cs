namespace TaoLive.Tv;

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
    public static float Factor { get; set; } = 17f / 36f; // 默认「中」=基准 17sp（#77 档位重定 2026-11-15 老板定值：大21/中17/小13=换算实值÷基准36；存量档位值不匹配走回落「中」）

    /// <summary>#69 屏宽自适应定标（2026-11-15 老板令）：实值随屏宽走，1920px 宽=基准 1:1；
    /// 更宽的屏整体放大、更窄的屏整体缩小，档位在基准上照常叠加可调。OnCreate 装载。</summary>
    public static float ScreenScale { get; set; } = 1f;

    /// <summary>总缩放系数（#68 重定标+#69 屏宽自适应）：裸最终值 × Scale = 当前档实值；中档 1920 宽=1.0 原值直出。</summary>
    public static float Scale => BaseSize / SpecBase * Factor * ScreenScale;

    public static float T(float f) => f * BaseSize * Factor * ScreenScale;

    /// <summary>#68 语义字号令：标题/项/说明三类，同角色同字号，列表/设置/弹窗观感一致。</summary>
    public static float Title => T(1.4f);   // 标题：23.8sp@中档17（抽屉页头/设置面板标题/退出弹窗标题）
    public static float Item => T(1.1f);    // 项：18.7sp@中档17（频道行/页签/设置行/按钮/当前节目）
    public static float Caption => T(0.9f); // 说明：15.3sp@中档17（台数/帮助行/次要说明/下档节目）

    /// <summary>
    /// 裸最终 sp 字号随档缩放（真机反馈批#15 尺寸令 2026-11-15 老板拍板：所有页面所有元素跟三档走；
    /// #68 重定标：入参按「中档实字号」计——#15 把最终观感值又乘了一遍档系数，字缩了一半多，就此拨正）。
    /// </summary>
    public static float Sp(float v) => v * Scale;

    /// <summary>裸最终 px 尺寸随档缩放（#68 重定标同口径）：内边距/间距/行高等裸 px 值统一过这里。</summary>
    public static int Px(float v) => (int)(v * Scale + 0.5f);
}
