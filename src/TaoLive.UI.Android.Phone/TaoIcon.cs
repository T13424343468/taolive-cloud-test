using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
// 图标画的是矢量折线, 指的固定是绘图 Path; 系统文件 Path 不在此文件使用
using Path = Android.Graphics.Path;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>图标种类: 全部单色矢量自绘, 随主题取色, 保证三象下风格统一 (与 TaoZip 手机版同一体系)。</summary>
    internal enum IconKind
    {
        Home, Play, Pause, Prev, Next, Fullscreen, FullscreenExit,
        Lock, LockOpen, More, Back, Menu, Sort, Theme, Save, Magnet,
        Check, Close, Grid, Recent, Refresh, Gear, Info, Cast,
    }

    /// <summary>
    /// 自绘单色图标视图 (24x24 视口, 2dp 圆帽描边, 水墨笔意)。
    /// 颜色一律取自 TaoTheme 色板, 禁止硬编码色值。
    /// </summary>
    internal sealed class IconView : View
    {
        readonly Paint _p;
        public IconKind Kind { get; private set; } = IconKind.More;
        private Color _color;

        public IconView(Context ctx) : base(ctx)
        {
            _p = new Paint(PaintFlags.AntiAlias)
            {
                StrokeCap = Paint.Cap.Round,
                StrokeJoin = Paint.Join.Round,
            };
        }

        /// <summary>设置图标与颜色 (重绑列表行时复用)。</summary>
        public void Bind(IconKind kind, Color color, float strokeWidthDp = 2f)
        {
            Kind = kind;
            _color = color;
            _p.StrokeWidth = Dp(strokeWidthDp);
            _p.Color = color;
            ContentDescription = Label(kind);   // v3.17.0: 图标带无障碍描述（读屏与云端真人路径都认它）
            Invalidate();
        }

        /// <summary>图标的无障碍描述（云端真人路径 tap_text 以 content-desc 兜底匹配）。</summary>
        internal static string Label(IconKind k) => k switch
        {
            IconKind.Home => "主页",
            IconKind.Play => "播放",
            IconKind.Pause => "暂停",
            IconKind.Prev => "上一集",
            IconKind.Next => "下一集",
            IconKind.Fullscreen => "全屏",
            IconKind.FullscreenExit => "退出全屏",
            IconKind.Lock => "锁定",
            IconKind.LockOpen => "解锁",
            IconKind.More => "更多",
            IconKind.Gear => "更多",
            IconKind.Back => "返回",
            IconKind.Menu => "菜单",
            IconKind.Sort => "排序",
            IconKind.Theme => "主题",
            IconKind.Save => "边播边存",
            IconKind.Magnet => "下载",
            IconKind.Check => "确定",
            IconKind.Close => "关闭",
            IconKind.Grid => "排列",
            IconKind.Recent => "最近",
            IconKind.Refresh => "重扫",
            IconKind.Info => "信息",
            IconKind.Cast => "投屏",
            _ => k.ToString(),
        };

        float Dp(float v) => v * Resources!.DisplayMetrics!.Density;

        // 未给显式尺寸时按 48dp 自量, 再被父级 spec 收小;
        // 修掉默认测量撑满可用空间导致的「整页巨大箭头」
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int def = (int)Dp(48);
            SetMeasuredDimension(Resolve(widthMeasureSpec, def), Resolve(heightMeasureSpec, def));
        }

        static int Resolve(int spec, int def)
        {
            var mode = MeasureSpec.GetMode(spec);
            int size = MeasureSpec.GetSize(spec);
            if (mode == MeasureSpecMode.Exactly) return size;
            if (mode == MeasureSpecMode.AtMost) return Math.Min(def, size);
            return def;
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas == null) return;
            base.OnDraw(canvas);
            // 24x24 视口等比缩放到视图可用区域 (扣除内边距; 未设内边距时与旧版完全一致)
            float availW = Width - PaddingLeft - PaddingRight;
            float availH = Height - PaddingTop - PaddingBottom;
            if (availW <= 0 || availH <= 0) return;
            float s = Math.Min(availW, availH) / 24f;
            canvas.Save();
            canvas.Translate(PaddingLeft + (availW - 24 * s) / 2f, PaddingTop + (availH - 24 * s) / 2f);
            canvas.Scale(s, s);
            _p.StrokeWidth = 2f;
            _p.Color = _color;
            Draw(Kind, canvas, _p);
            canvas.Restore();
        }

        private static Path P(params float[] pts)
        {
            var p = new Path();
            p.MoveTo(pts[0], pts[1]);
            for (int i = 2; i + 1 < pts.Length; i += 2) p.LineTo(pts[i], pts[i + 1]);
            return p;
        }

        private void Draw(IconKind k, Canvas c, Paint p)
        {
            switch (k)
            {
                case IconKind.Home:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(4, 11, 12, 4, 20, 11), p);                       // 屋顶
                    var body = P(6, 10, 6, 20, 18, 20, 18, 10);                   // 房身
                    c.DrawPath(body, p);
                    c.DrawPath(P(10.5f, 20, 10.5f, 15.5f, 13.5f, 15.5f, 13.5f, 20), p); // 门
                    break;
                }

                case IconKind.Play:
                {
                    var tri = P(8.5f, 5, 8.5f, 19, 19, 12);
                    tri.Close();
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawPath(tri, p);
                    break;
                }

                case IconKind.Pause:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    p.StrokeWidth = 3f;
                    c.DrawLine(9, 5.5f, 9, 18.5f, p);
                    c.DrawLine(15, 5.5f, 15, 18.5f, p);
                    break;
                }

                case IconKind.Prev:
                {
                    var tri = P(17, 5.5f, 17, 18.5f, 8, 12);
                    tri.Close();
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawPath(tri, p);
                    c.DrawRect(5.6f, 5.5f, 7.6f, 18.5f, p);
                    break;
                }

                case IconKind.Next:
                {
                    var tri = P(7, 5.5f, 7, 18.5f, 16, 12);
                    tri.Close();
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawPath(tri, p);
                    c.DrawRect(16.4f, 5.5f, 18.4f, 18.5f, p);
                    break;
                }

                case IconKind.Fullscreen:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(4, 9, 4, 4, 9, 4), p);
                    c.DrawPath(P(15, 4, 20, 4, 20, 9), p);
                    c.DrawPath(P(20, 15, 20, 20, 15, 20), p);
                    c.DrawPath(P(9, 20, 4, 20, 4, 15), p);
                    break;
                }

                case IconKind.FullscreenExit:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(7, 17, 17, 7), p);                               // 对角主线
                    c.DrawPath(P(12, 7, 17, 7, 17, 12), p);                       // 右上箭头
                    c.DrawPath(P(7, 12, 7, 17, 12, 17), p);                       // 左下箭头
                    break;
                }

                case IconKind.Lock:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawRoundRect(new RectF(7, 11, 17, 20), 2, 2, p);           // 锁身
                    c.DrawArc(new RectF(8.5f, 4.5f, 15.5f, 11.5f), 180, 180, false, p); // 锁梁
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawCircle(12, 15.5f, 1.2f, p);                             // 锁孔
                    break;
                }

                case IconKind.LockOpen:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawRoundRect(new RectF(7, 11, 17, 20), 2, 2, p);           // 锁身
                    c.DrawArc(new RectF(10.5f, 4.5f, 17.5f, 11.5f), 200, 160, false, p); // 锁梁跳开
                    c.DrawLine(14.6f, 6.2f, 14.6f, 8.5f, p);
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawCircle(12, 15.5f, 1.2f, p);
                    break;
                }

                case IconKind.More:
                {
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawCircle(5.5f, 12, 1.7f, p);
                    c.DrawCircle(12, 12, 1.7f, p);
                    c.DrawCircle(18.5f, 12, 1.7f, p);
                    break;
                }

                case IconKind.Back:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(14.5f, 5, 7.5f, 12, 14.5f, 19), p);
                    break;
                }

                case IconKind.Cast:
                {
                    // 电视屏 + 播放三角 (投屏, v3.13): 屏框四边闭合折线, 底座支杆, 三角实心
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(3, 5, 21, 5, 21, 16, 3, 16, 3, 5), p);
                    c.DrawLine(9, 20, 15, 20, p);
                    c.DrawLine(12, 16, 12, 20, p);
                    var tri = P(10, 8, 15, 10.5f, 10, 13);
                    tri.Close();
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawPath(tri, p);
                    break;
                }

                case IconKind.Menu:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawLine(4, 7, 20, 7, p);
                    c.DrawLine(4, 12, 20, 12, p);
                    c.DrawLine(4, 17, 20, 17, p);
                    break;
                }

                case IconKind.Sort:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawLine(4.5f, 7, 19.5f, 7, p);
                    c.DrawLine(4.5f, 12, 15.5f, 12, p);
                    c.DrawLine(4.5f, 17, 11.5f, 17, p);
                    break;
                }

                case IconKind.Grid:
                {
                    // 网格四宫格: 图文排列 (带缩略图) 的入口图标
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawRoundRect(new RectF(5, 5, 11, 11), 1.5f, 1.5f, p);
                    c.DrawRoundRect(new RectF(13, 5, 19, 11), 1.5f, 1.5f, p);
                    c.DrawRoundRect(new RectF(5, 13, 11, 19), 1.5f, 1.5f, p);
                    c.DrawRoundRect(new RectF(13, 13, 19, 19), 1.5f, 1.5f, p);
                    break;
                }

                case IconKind.Theme:
                {
                    // 阴阳意象: 外圆 + 半圆填 (与 TaoZip 手机版同一枚)
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawCircle(12, 12, 8.5f, p);
                    var half = new Path();
                    half.AddArc(new RectF(3.5f, 3.5f, 20.5f, 20.5f), -90, 180);
                    half.Close();
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawPath(half, p);
                    break;
                }

                case IconKind.Save:
                {
                    // 箭头落盘: 竖箭头 + 底托
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(12, 4, 12, 13), p);
                    c.DrawPath(P(8, 9.5f, 12, 13.5f, 16, 9.5f), p);
                    c.DrawPath(P(5, 16, 5, 20, 19, 20, 19, 16), p);
                    break;
                }

                case IconKind.Magnet:
                {
                    // U 形磁铁: 双腿 + 弧底 + 顶端磁极
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(8, 5, 8, 12), p);
                    c.DrawArc(new RectF(8, 8, 16, 16), 0, 180, false, p);
                    c.DrawPath(P(16, 12, 16, 5), p);
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawRect(6.2f, 3.5f, 9.8f, 6.5f, p);
                    c.DrawRect(14.2f, 3.5f, 17.8f, 6.5f, p);
                    break;
                }

                case IconKind.Check:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(P(5, 12.5f, 10, 17.5f, 19, 6.5f), p);
                    break;
                }

                case IconKind.Close:
                {
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawLine(6, 6, 18, 18, p);
                    c.DrawLine(18, 6, 6, 18, p);
                    break;
                }

                case IconKind.Recent:
                {
                    // 时钟: 最近播放优先开关
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawCircle(12, 12, 8.5f, p);
                    c.DrawPath(P(12, 7.5f, 12, 12, 15.5f, 14), p);
                    break;
                }

                case IconKind.Refresh:
                {
                    // 循环双弧 + 箭头: 重扫媒体库
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawArc(new RectF(5, 5, 19, 19), 150, 120, false, p);
                    c.DrawArc(new RectF(5, 5, 19, 19), 330, 120, false, p);
                    var a1 = P(5.2f, 3.2f, 5.2f, 8.2f, 10.2f, 8.2f); a1.Close();
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawPath(a1, p);
                    var a2 = P(18.8f, 20.8f, 18.8f, 15.8f, 13.8f, 15.8f); a2.Close();
                    c.DrawPath(a2, p);
                    break;
                }

                case IconKind.Gear:
                {
                    // 调节滑块: 设置 (三条横线 + 错落圆点)
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawLine(4.5f, 7, 19.5f, 7, p);
                    c.DrawLine(4.5f, 12, 19.5f, 12, p);
                    c.DrawLine(4.5f, 17, 19.5f, 17, p);
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawCircle(15, 7, 2.2f, p);
                    c.DrawCircle(9, 12, 2.2f, p);
                    c.DrawCircle(13, 17, 2.2f, p);
                    break;
                }

                case IconKind.Info:
                {
                    // 圆圈 + i: 关于
                    p.SetStyle(Paint.Style.Stroke);
                    c.DrawCircle(12, 12, 8.5f, p);
                    p.SetStyle(Paint.Style.Fill);
                    c.DrawCircle(12, 8.2f, 1.3f, p);
                    p.StrokeWidth = 2.4f;
                    c.DrawLine(12, 11.5f, 12, 16.5f, p);
                    break;
                }
            }
        }
    }
}
