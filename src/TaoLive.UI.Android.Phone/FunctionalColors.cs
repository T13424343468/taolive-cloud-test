using System;
using Android.Graphics;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 功能色唯一出处: 危险(删除)/成功(完成)这类带语义的操作色。
    /// 主题色板 (TaoPalette) 之外唯一允许写字面色值的地方, 按三象各配一档保证可辨。
    /// </summary>
    public static class FunctionalColors
    {
        public static Color Danger()
        {
            // 墨夜下纯红偏暗看不清, 提亮度; 浅色主题用深红
            return TaoTheme.Cur.Id == 2 ? Color.ParseColor("#FF7B72") : Color.ParseColor("#C0392B");
        }

        public static Color Ok()
        {
            return TaoTheme.Cur.Id == 2 ? Color.ParseColor("#7BC47F") : Color.ParseColor("#2E7D32");
        }

        // 播放页玻璃圆钮: 半透明白叠在视频画面上, 三象通用 (视频底色不可控, 固定白系最稳)
        public static Color GlassFill() => Color.Argb(28, 255, 255, 255);   // 玻璃底
        public static Color GlassStroke() => Color.Argb(70, 255, 255, 255); // 玻璃描边
        public static Color OnGlass() => Color.Argb(235, 255, 255, 255);    // 玻璃上的图标
    }
}
