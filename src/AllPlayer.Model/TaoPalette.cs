using System;

namespace AllPlayer.Model
{
    /// <summary>
    /// Tao 系三象主题色板 (纯数据, 各端自行映射为平台颜色):
    /// 0=水墨(默认, 暖宣浅) / 1=天灰(中调冷灰) / 2=墨夜(以黑为主, 暖炭)。
    /// 编号语义全系统一; 数值母本 TaoZip v59 亮度阶梯 (水墨>天灰>墨夜),
    /// 旧"浅色/深色"命名与实现退役。八键为四支柱(底/主字/点缀/标题栏)+控件面/描边/次要字/标题栏字。
    /// </summary>
    public class TaoPalette
    {
        public int Id;                      // 0水墨 1天灰 2墨夜
        public string Name = "";            // 水墨/天灰/墨夜
        public int Bg;                      // 页面底
        public int Face;                    // 控件面(卡片/输入框)
        public int Stroke;                  // 描边/分隔线
        public int TextMain;                // 主字
        public int TextSub;                 // 次要字
        public int Accent;                  // 点缀(进度条/选中态/强调)
        public int BarBg;                   // 标题栏底
        public int BarText;                 // 标题栏字

        public static readonly TaoPalette[] All =
        {
            new TaoPalette
            {
                Id = 0, Name = "水墨",
                Bg      = unchecked((int)0xFFF7F5EE),   // 暖宣浅 (R>G>B, L>0.8)
                Face    = unchecked((int)0xFFFFFFFF),   // 控件面亮于底
                Stroke  = unchecked((int)0xFFA69C86),   // 描边: 暖灰褐
                TextMain= unchecked((int)0xFF2E2A22),   // 深墨主字
                TextSub = unchecked((int)0xFF6E6759),   // 次要字
                Accent  = unchecked((int)0xFF52708A),   // 青灰蓝点缀 (母本)
                BarBg   = unchecked((int)0xFFF7F5EE),
                BarText = unchecked((int)0xFF2E2A22),
            },
            new TaoPalette
            {
                Id = 1, Name = "天灰",
                Bg      = unchecked((int)0xFFA8B2BC),   // 中灰冷调 (B>G>R, L 0.3–0.6)
                Face    = unchecked((int)0xFFC6CDD5),   // 控件面亮于底 (母本)
                Stroke  = unchecked((int)0xFF5E6870),   // 描边 (母本)
                TextMain= unchecked((int)0xFF1C242C),   // 深墨青 (母本)
                TextSub = unchecked((int)0xFF3D4750),   // 深色次字 (母本)
                Accent  = unchecked((int)0xFF0F4C9E),   // 深藏蓝点缀 (母本)
                BarBg   = unchecked((int)0xFFA8B2BC),
                BarText = unchecked((int)0xFF1C242C),
            },
            new TaoPalette
            {
                Id = 2, Name = "墨夜",
                Bg      = unchecked((int)0xFF131211),   // 近黑暖炭 (R>G>B, L<0.03, 以黑为主)
                Face    = unchecked((int)0xFF211E1B),   // 控件面 (母本)
                Stroke  = unchecked((int)0xFF6E675C),   // 描边 (母本)
                TextMain= unchecked((int)0xFFE8E3D5),   // 宣纸白 (母本)
                TextSub = unchecked((int)0xFFABA595),   // 次要字 (母本)
                Accent  = unchecked((int)0xFF6FA3BC),   // 黛青 (母本)
                BarBg   = unchecked((int)0xFF211E1B),
                BarText = unchecked((int)0xFFE8E3D5),
            },
        };

        /// <summary>主题编号是否合法 (0=水墨 1=天灰 2=墨夜)</summary>
        public static bool IsValidId(int id) => id >= 0 && id < All.Length;

        /// <summary>按编号取色板; 越界回落水墨 (预防性编码, 不抛异常)</summary>
        public static TaoPalette Get(int id) => IsValidId(id) ? All[id] : All[0];
    }
}
