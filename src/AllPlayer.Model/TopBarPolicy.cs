using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// v3.22.5 顶栏布局纯逻辑 (无平台依赖可测试):
    /// ①顶部右侧按钮组顺序: 投屏 | 锁定 | 更多 (锁定居中、更多保持最右, 老板 11:00 拍板);
    /// ②按钮组与遮罩底边的间距 = 状态栏/挖孔安全区以下到按钮的间距 (上下留白对称, 视觉舒适);
    /// ③按钮组在遮罩内垂直居中——遮罩内容高度 = 安全区 + 上间隙 + 按钮 + 下间隙。
    /// </summary>
    public static class TopBarPolicy
    {
        /// <summary>顶部右侧按钮组顺序 (从左到右): 投屏、锁定、更多。</summary>
        public static readonly string[] RightOrder = { "cast", "lock", "more" };

        /// <summary>顶部安全高度 (px): 状态栏与挖孔/刘海二者取较大者, 避免按钮压进开孔区。</summary>
        public static int SafeTopPx(int statusBarPx, int cutoutPx)
            => Math.Max(Math.Max(0, statusBarPx), Math.Max(0, cutoutPx));

        /// <summary>顶栏内边距 (px): 上=安全高度+间隙, 下=间隙 → 按钮离遮罩底边的距离
        /// 与安全区以下到按钮的距离一致, 上下留白对称、按钮在遮罩内垂直居中。</summary>
        public static (int Top, int Bottom) Padding(int safeTopPx, int gapPx)
            => (SafeTopPx(safeTopPx, 0) + Math.Max(0, gapPx), Math.Max(0, gapPx));
    }
}
