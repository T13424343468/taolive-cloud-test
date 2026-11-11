using System;
using Android.Graphics;
using AllPlayer.Model;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// Tao 系三象主题 (手机版): 0=水墨(默认) / 1=天灰 / 2=墨夜, 编号语义全系统一。
    /// 色板数值以 AllPlayer.Model.TaoPalette 为唯一事实源 (亮度阶梯母本 TaoZip v59);
    /// 本类只负责映射为 Android 颜色并在启动即钉基调 (OnCreate 里先 Apply 再 BuildUi)。
    /// 换肤后整页 Recreate, 旧色不残留。
    /// </summary>
    public static class TaoTheme
    {
        /// <summary>当前生效色板 (启动与换肤时更新; 永不为空)</summary>
        public static TaoPalette Cur { get; private set; } = TaoPalette.Get(0);

        /// <summary>主题名 (水墨/天灰/墨夜), 供界面显示</summary>
        public static string Name => Cur.Name;

        /// <summary>应用指定编号的主题; 越界按水墨 (预防性编码)</summary>
        public static void Apply(int id)
        {
            Cur = TaoPalette.Get(id);
            Changed?.Invoke();
        }

        /// <summary>从设置读取并应用主题 (启动即钉基调: 先于 BuildUi 调用)</summary>
        public static void ApplyFromSettings()
        {
            Apply(AppSettings.Current.TaoThemeId);
        }

        /// <summary>保存主题编号到设置并落盘 (走调用方既有的串行落盘入口)</summary>
        public static void Persist(int id)
        {
            AppSettings.Current.TaoThemeId = TaoPalette.IsValidId(id) ? id : 0;
            AppSettings.Current.ThemeMode = Cur.Name;   // 旧字段留痕, 不再作为依据
        }

        /// <summary>主题切换通知 (界面重建时重新读 Cur, 事件供轻量刷新用)</summary>
        public static event Action? Changed;

        // ---- 平台映射: Android Color ----
        public static Color Col(int argb) => new Color(argb);
        public static Color Bg => Col(Cur.Bg);
        public static Color Face => Col(Cur.Face);
        public static Color Stroke => Col(Cur.Stroke);
        public static Color TextMain => Col(Cur.TextMain);
        public static Color TextSub => Col(Cur.TextSub);
        public static Color Accent => Col(Cur.Accent);
        public static Color BarBg => Col(Cur.BarBg);
        public static Color BarText => Col(Cur.BarText);
    }
}
