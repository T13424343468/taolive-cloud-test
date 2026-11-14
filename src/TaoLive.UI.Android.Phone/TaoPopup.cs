using System;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 卡片弹层 (v3.3): 菜单/对话框全部改成圆角卡片, 对齐 ColorOS 的观感与操作逻辑。
    /// · 菜单: 半透明遮罩 + 圆角卡片, 在唤起图标的锚点处出现 (下左对齐 / 上右对齐), 点遮罩或选项收起;
    /// · 对话框: 居中圆角卡片, 标题 + 内容 + 文字按钮行;
    /// · 颜色全部取自 TaoTheme, 遮罩与玻璃质感色走 FunctionalColors。
    /// </summary>
    internal static class TaoPopup
    {
        private static FrameLayout? _current;

        private static ViewGroup RootOf(Activity act)
        {
            if (act is MainActivity m && m._root != null) return m._root;
            return (ViewGroup)act.Window!.DecorView!;
        }

        /// <summary>收起当前弹层; 有弹层被收起时返回 true (返回键优先消费)。</summary>
        public static bool DismissAll()
        {
            if (_current == null) return false;
            var cur = _current;
            _current = null;
            if (cur.Parent is ViewGroup p) p.RemoveView(cur);
            return true;
        }

        private static GradientDrawable CardBg(Activity act, float cornerDp = 28)
        {
            var gd = new GradientDrawable();
            gd.SetCornerRadius(TaoUi.Dp(act, cornerDp));
            gd.SetColor(TaoTheme.Face);   // v3.5 ColorOS 弹层: 无描边, 靠投影分层
            return gd;
        }

        // ---------------- 卡片菜单 (锚点唤起) ----------------

        /// <summary>v3.25.7 菜单落位四向: 下方/上方 × 左对齐/右对齐 (长按菜单跟随卡片四向浮出)。</summary>
        public enum PopupSide { BelowLeft, BelowRight, AboveLeft, AboveRight }

        /// <summary>
        /// 卡片菜单: 在 anchor 旁弹出圆角卡片。
        /// belowLeft=true: 锚点下方、与锚点左缘对齐 (主页左上菜单);
        /// belowLeft=false: 锚点上方、与锚点右缘对齐 (播放页右下按钮的功能页)。
        /// </summary>
        public static void ShowMenu(Activity act, View anchor, (string text, Action onClick)[] items,
            bool belowLeft = true, string? title = null, View? extraContent = null)
        {
            var wrapped = new (IconKind?, string, Action)[items.Length];
            for (int i = 0; i < items.Length; i++)
                wrapped[i] = (null, items[i].text, items[i].onClick);
            ShowMenuCore(act, anchor, wrapped,
                belowLeft ? PopupSide.BelowLeft : PopupSide.AboveRight,
                title, extraContent);
        }

        /// <summary>v3.25.7: 指定四向落位的菜单 (长按列表行时用, 菜单浮向长按点一侧)。</summary>
        public static void ShowMenu(Activity act, View anchor, (string text, Action onClick)[] items,
            PopupSide side, string? title = null)
        {
            var wrapped = new (IconKind?, string, Action)[items.Length];
            for (int i = 0; i < items.Length; i++)
                wrapped[i] = (null, items[i].text, items[i].onClick);
            ShowMenuCore(act, anchor, wrapped, side, title, null);
        }

        /// <summary>带图标卡片菜单 (v3.10): 每行左图标右文字, 图标随主题取色。
        /// belowRight=true: 锚点下方、与锚点右缘对齐 (右上角设置钮呼出的主页菜单);
        /// belowLeft/belowRight 都为 false: 锚点上方、右缘对齐。belowRight 优先于 belowLeft。
        /// v3.20.1: extraContent 非空时追加在菜单行下方 (实时效果预览等自定义区块, 占满卡宽)。</summary>
        public static void ShowMenu(Activity act, View anchor, (IconKind icon, string text, Action onClick)[] items,
            bool belowLeft = true, bool belowRight = false, string? title = null, View? extraContent = null)
        {
            var wrapped = new (IconKind?, string, Action)[items.Length];
            for (int i = 0; i < items.Length; i++)
                wrapped[i] = (items[i].icon, items[i].text, items[i].onClick);
            ShowMenuCore(act, anchor, wrapped,
                belowRight ? PopupSide.BelowRight : belowLeft ? PopupSide.BelowLeft : PopupSide.AboveRight,
                title, extraContent);
        }

        private static void ShowMenuCore(Activity act, View anchor, (IconKind? icon, string text, Action onClick)[] items,
            PopupSide side, string? title, View? extraContent = null)
        {
            DismissAll();
            var root = RootOf(act);
            var overlay = new FrameLayout(act) { Clickable = true };
            overlay.SetBackgroundColor(Color.Argb(90, 0, 0, 0));
            overlay.Click += (s, e) => DismissAll();

            var card = new LinearLayout(act) { Orientation = Orientation.Vertical, Clickable = true };
            card.Background = CardBg(act);
            card.Elevation = 10;

            // v3.22.8 (老板反馈横屏下更多卡片内容显示不完整): 菜单行装进可滚动区——
            // 该有滚动条就要有, 该能滑动就要支持; 横竖屏都不会再被裁
            var body = new LinearLayout(act) { Orientation = Orientation.Vertical };

            // v3.11: 菜单卡收窄到内容实际需要的宽度 (264 太宽了, 右上呼出时更突兀)
            int cardW = Math.Min(TaoUi.Dp(act, 224), root.Width - TaoUi.Dp(act, 28));
            if (title != null)
            {
                var tv = new TextView(act) { Text = title, TextSize = 15, Typeface = Typeface.DefaultBold };
                tv.SetTextColor(TaoTheme.TextSub);
                tv.SetPadding(TaoUi.Dp(act, 20), TaoUi.Dp(act, 16), TaoUi.Dp(act, 20), TaoUi.Dp(act, 6));
                card.AddView(tv);
            }
            foreach (var (icon, text, onClick) in items)
            {
                View it;
                if (icon != null)
                {
                    // v3.10 菜单行: 左图标 20dp + 右文字, 颜色随主题
                    var row = new LinearLayout(act) { Orientation = Orientation.Horizontal, Clickable = true };
                    row.SetGravity(GravityFlags.CenterVertical);
                    var ic = TaoUi.MakeIcon(act, icon.Value, 20, TaoTheme.TextMain);
                    ic.Clickable = false; ic.Focusable = false;
                    row.AddView(ic, new LinearLayout.LayoutParams(TaoUi.Dp(act, 20), TaoUi.Dp(act, 20))
                    { LeftMargin = TaoUi.Dp(act, 20), RightMargin = TaoUi.Dp(act, 14) });
                    var lbl = new TextView(act) { Text = text, TextSize = 16 };
                    lbl.SetTextColor(TaoTheme.TextMain);
                    row.AddView(lbl, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
                    { RightMargin = TaoUi.Dp(act, 20) });
                    it = row;
                }
                else
                {
                    var tv = new TextView(act) { Text = text, TextSize = 16, Clickable = true };
                    tv.SetTextColor(TaoTheme.TextMain);
                    tv.SetPadding(TaoUi.Dp(act, 20), TaoUi.Dp(act, 13), TaoUi.Dp(act, 20), TaoUi.Dp(act, 13));
                    it = tv;
                }
                it.Click += (s, e) => { DismissAll(); onClick(); };
                // v3.11: 菜单行统一 44dp 高 (图标 20 + 上下留白), 排版节奏与整卡风格一致
                body.AddView(it, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                { TopMargin = icon != null ? TaoUi.Dp(act, 6) : 0, BottomMargin = icon != null ? TaoUi.Dp(act, 6) : 0 });
            }
            // v3.20.1: 自定义区块 (实时效果预览等) 追加在菜单行下方, 占满卡宽
            if (extraContent != null)
                body.AddView(extraContent, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                { TopMargin = TaoUi.Dp(act, 8) });
            // v3.23.1 (真机反馈主页菜单卡片过长): 卡片高度贴合内容——先量内容实际高度,
            // 未超限高 (屏幕 60%) 就按内容实际高度出卡, 超了才封顶进入可滑动, 不再撑起大半屏
            body.Measure((int)MeasureSpecMode.Unspecified, (int)MeasureSpecMode.Unspecified);
            int scrollH = Math.Min(Math.Max(body.MeasuredHeight, 1), (int)(root.Height * 0.6));
            var scroll = new ScrollView(act) { VerticalScrollBarEnabled = true, VerticalFadingEdgeEnabled = false };
            scroll.AddView(body, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            card.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, scrollH));
            overlay.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            // 锚点定位 (v3.6 修闪烁): 先量好卡片高度、算好落点, 摆好位置再挂上屏;
            // 以前先挂上屏再 Post 挪位, 第一帧画在左上角, 肉眼可见地闪一下。
            // v3.10 三态: belowRight=下方右对齐 / belowLeft=下方左对齐 / 都否=上方右对齐。
            // v3.25.7 四向: BelowLeft/BelowRight/AboveLeft/AboveRight —— 长按点在上半屏菜单浮下方,
            // 在下半屏浮上方; 在右半屏右对齐, 在左半屏左对齐 (跟随卡片, 不遮长按的手指)。
            // v3.12: 卡片宽度贴合内容 (不再定宽留出大片右侧空白), 左右内边距对称;
            // 距屏幕边缘收到 28dp, 避开屏幕大圆角区域。
            int[] aloc = new int[2]; anchor.GetLocationOnScreen(aloc);
            int[] rloc = new int[2]; root.GetLocationOnScreen(rloc);
            int ax = aloc[0] - rloc[0], ay = aloc[1] - rloc[1];
            card.Measure((int)MeasureSpecMode.Unspecified, (int)MeasureSpecMode.Unspecified);
            int cw = Math.Clamp(card.MeasuredWidth, TaoUi.Dp(act, 150), root.Width - TaoUi.Dp(act, 56));
            int ch = Math.Max(card.MeasuredHeight, 1);
            var cardLp = new FrameLayout.LayoutParams(cw, ViewGroup.LayoutParams.WrapContent);
            bool below = side == PopupSide.BelowLeft || side == PopupSide.BelowRight;
            bool alignRight = side == PopupSide.BelowRight || side == PopupSide.AboveRight;
            int mgLeft = alignRight ? ax + anchor.Width - cw : ax;
            int mgTop = below ? ay + anchor.Height + TaoUi.Dp(act, 8) : ay - ch - TaoUi.Dp(act, 8);
            mgLeft = Math.Clamp(mgLeft, TaoUi.Dp(act, 28), Math.Max(TaoUi.Dp(act, 28), root.Width - cw - TaoUi.Dp(act, 28)));
            mgTop = Math.Clamp(mgTop, TaoUi.Dp(act, 24), Math.Max(TaoUi.Dp(act, 24), root.Height - ch - TaoUi.Dp(act, 28)));
            cardLp.LeftMargin = mgLeft;
            cardLp.TopMargin = mgTop;
            card.LayoutParameters = cardLp;
            root.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _current = overlay;
        }

        // ---------------- 卡片对话框 (居中) ----------------

        /// <summary>居中卡片对话框: 标题 + 内容视图 + 文字按钮行; 点遮罩收起。</summary>
        public static void ShowDialog(Activity act, string title, View? content,
            (string text, Action? onClick, bool danger)[] buttons)
        {
            DismissAll();
            var root = RootOf(act);
            var overlay = new FrameLayout(act) { Clickable = true };
            overlay.SetBackgroundColor(Color.Argb(90, 0, 0, 0));
            overlay.Click += (s, e) => DismissAll();

            var card = new LinearLayout(act) { Orientation = Orientation.Vertical, Clickable = true };
            card.Background = CardBg(act, 28);
            card.Elevation = 12;

            var tv = new TextView(act) { Text = title, TextSize = 18, Typeface = Typeface.DefaultBold };
            tv.SetTextColor(TaoTheme.TextMain);
            tv.SetPadding(TaoUi.Dp(act, 22), TaoUi.Dp(act, 20), TaoUi.Dp(act, 22), TaoUi.Dp(act, 8));
            card.AddView(tv);

            if (content != null)
            {
                var wrap = new FrameLayout(act);
                wrap.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                card.AddView(wrap, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                { LeftMargin = TaoUi.Dp(act, 22), RightMargin = TaoUi.Dp(act, 22) });
            }

            if (buttons.Length > 0)
            {
                var row = new LinearLayout(act) { Orientation = Orientation.Horizontal };
                row.SetPadding(TaoUi.Dp(act, 14), TaoUi.Dp(act, 10), TaoUi.Dp(act, 14), TaoUi.Dp(act, 10));
                foreach (var (text, onClick, danger) in buttons)
                {
                    var b = new TextView(act) { Text = text, TextSize = 16, Gravity = GravityFlags.Center };
                    b.SetTextColor(danger ? FunctionalColors.Danger() : TaoTheme.Accent);
                    b.SetPadding(TaoUi.Dp(act, 12), TaoUi.Dp(act, 12), TaoUi.Dp(act, 12), TaoUi.Dp(act, 12));
                    b.Click += (s, e) => { DismissAll(); onClick?.Invoke(); };
                    row.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                }
                card.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                { TopMargin = TaoUi.Dp(act, 4) });
            }

            overlay.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
            { LeftMargin = TaoUi.Dp(act, 24), RightMargin = TaoUi.Dp(act, 24) });
            root.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _current = overlay;
        }

        /// <summary>纯文字提示卡片对话框。</summary>
        public static void ShowMessage(Activity act, string title, string message,
            (string text, Action? onClick, bool danger)[] buttons)
        {
            var msg = new TextView(act) { Text = message, TextSize = 15 };
            msg.SetTextColor(TaoTheme.TextMain);
            ShowDialog(act, title, msg, buttons);
        }

        /// <summary>居中卡片选项列表 (原系统列表对话框的卡片版): 标题 + 逐行选项 + 取消。</summary>
        public static void ShowListDialog(Activity act, string title, (string text, Action onClick)[] items)
        {
            DismissAll();
            var root = RootOf(act);
            var overlay = new FrameLayout(act) { Clickable = true };
            overlay.SetBackgroundColor(Color.Argb(90, 0, 0, 0));
            overlay.Click += (s, e) => DismissAll();

            var card = new LinearLayout(act) { Orientation = Orientation.Vertical, Clickable = true };
            card.Background = CardBg(act, 28);
            card.Elevation = 12;

            var tv = new TextView(act) { Text = title, TextSize = 18, Typeface = Typeface.DefaultBold };
            tv.SetTextColor(TaoTheme.TextMain);
            tv.SetPadding(TaoUi.Dp(act, 22), TaoUi.Dp(act, 20), TaoUi.Dp(act, 22), TaoUi.Dp(act, 6));
            card.AddView(tv);

            // v3.22.8: 列表项装进滚动区 (横屏矮屏选项多不再被裁), 该有滚动条就有、能滑就滑
            var listBody = new LinearLayout(act) { Orientation = Orientation.Vertical };
            foreach (var (text, onClick) in items)
            {
                var it = new TextView(act) { Text = text, TextSize = 16 };
                it.SetTextColor(TaoTheme.TextMain);
                it.SetPadding(TaoUi.Dp(act, 22), TaoUi.Dp(act, 13), TaoUi.Dp(act, 22), TaoUi.Dp(act, 13));
                it.Click += (s, e) => { DismissAll(); onClick(); };
                listBody.AddView(it);
            }
            var listScroll = new ScrollView(act) { VerticalScrollBarEnabled = true, VerticalFadingEdgeEnabled = false };
            listScroll.AddView(listBody, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            card.AddView(listScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(root.Height * 0.6)));

            var cancel = new TextView(act) { Text = "取消", TextSize = 15, Gravity = GravityFlags.Center };
            cancel.SetTextColor(TaoTheme.TextSub);
            cancel.Background = CardBg(act, 20);
            cancel.SetPadding(0, TaoUi.Dp(act, 12), 0, TaoUi.Dp(act, 12));
            cancel.Click += (s, e) => DismissAll();
            var cancelWrap = new FrameLayout(act);
            cancelWrap.AddView(cancel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            card.AddView(cancelWrap, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { LeftMargin = TaoUi.Dp(act, 22), RightMargin = TaoUi.Dp(act, 22), TopMargin = TaoUi.Dp(act, 10) });
            ((ViewGroup.MarginLayoutParams)cancelWrap.LayoutParameters!).BottomMargin = TaoUi.Dp(act, 18);

            overlay.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
            { LeftMargin = TaoUi.Dp(act, 24), RightMargin = TaoUi.Dp(act, 24) });
            root.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _current = overlay;
        }
    }
}
