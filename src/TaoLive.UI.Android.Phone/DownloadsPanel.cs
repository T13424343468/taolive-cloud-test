using System;
using System.Linq;
using Android.App;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AllPlayer.Model;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 下载管理面板 (v3.0 重做版): 任务列表 (进度条 + 大小 + 状态), 单个暂停/继续/删除,
    /// 底部 全部暂停 / 全部开始 / 清理完成。应用重开后任务还在, 可断点续传。
    /// v3.0: 颜色全部改由 TaoTheme 色板出, 不再写死深色值; 深浅主题下都按当前色板渲染。
    /// </summary>
    public class DownloadsPanel : LinearLayout
    {
        public DownloadsPanel(Activity act, AllPlayer.BLL.DownloadService service) : base(act)
        {
            Orientation = global::Android.Widget.Orientation.Vertical;
            SetBackgroundColor(TaoTheme.Bg);

            // 顶栏 (让出系统状态栏)
            var top = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            top.SetPadding(TaoUi.Dp(act, 20), TaoUi.StatusBarHeight(act) + TaoUi.Dp(act, 6), TaoUi.Dp(act, 20), TaoUi.Dp(act, 10));
            top.SetBackgroundColor(TaoTheme.BarBg);
            var back = TaoUi.MakeIcon(act, IconKind.Back, 18); // v3.12 与播放页圆钮标准对齐: 40dp 按钮 + 18 图标
            back.Click += (s, e) => Hide();
            top.AddView(back, TaoUi.Dp(act, 40), TaoUi.Dp(act, 40));
            var title = new TextView(act) { Text = "下载管理", TextSize = 17 };
            title.SetTextColor(TaoTheme.BarText);
            top.AddView(title);
            AddView(top, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // 保存位置卡片 (v3.7 按用户要求挪到页面顶部, 顶栏下方第一张卡):
            // 标题 + 修改按钮一行, 完整路径整行铺开在下; 以前塞成一行小字, 长路径换行后和"修改"挤在一起, 又小又乱。
            var saveCard = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Vertical };
            saveCard.Background = TaoUi.Card(act, 18);
            saveCard.SetPadding(TaoUi.Dp(act, 16), TaoUi.Dp(act, 12), TaoUi.Dp(act, 12), TaoUi.Dp(act, 14));
            var saveHead = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Horizontal };
            saveHead.SetGravity(GravityFlags.CenterVertical);
            var saveTitle = new TextView(act) { Text = "保存位置", TextSize = 15, Typeface = Typeface.DefaultBold };
            saveTitle.SetTextColor(TaoTheme.TextMain);
            saveHead.AddView(saveTitle, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var changeBtn = new TextView(act) { Text = "修改", TextSize = 13 };
            changeBtn.SetTextColor(TaoTheme.Accent);
            var pillBg = new global::Android.Graphics.Drawables.GradientDrawable();
            pillBg.SetColor(TaoTheme.Bg);
            pillBg.SetCornerRadius(TaoUi.Dp(act, 16));
            changeBtn.Background = pillBg;
            changeBtn.SetPadding(TaoUi.Dp(act, 18), TaoUi.Dp(act, 7), TaoUi.Dp(act, 18), TaoUi.Dp(act, 7));
            var saveLabel = new TextView(act) { TextSize = 12, Text = AppSettings.Current.SaveFolder };
            saveLabel.SetTextColor(TaoTheme.TextSub);
            changeBtn.Click += (s, e) => ShowChangeSaveFolder(act, service, saveLabel);
            saveHead.AddView(changeBtn, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            saveCard.AddView(saveHead, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            saveCard.AddView(saveLabel, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(act, 6) });
            AddView(saveCard, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { LeftMargin = TaoUi.Dp(act, 20), RightMargin = TaoUi.Dp(act, 20), TopMargin = TaoUi.Dp(act, 6), BottomMargin = TaoUi.Dp(act, 8) });

            // v3.25.0: 网络线路卡片 (直连 = 内置网络优化; 或用户自配代理; 产品不内置代理)
            var lineCard = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Vertical };
            lineCard.Background = TaoUi.Card(act, 18);
            lineCard.SetPadding(TaoUi.Dp(act, 16), TaoUi.Dp(act, 12), TaoUi.Dp(act, 12), TaoUi.Dp(act, 14));
            var lineHead = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Horizontal };
            lineHead.SetGravity(GravityFlags.CenterVertical);
            var lineTitle = new TextView(act) { Text = "网络线路", TextSize = 15, Typeface = Typeface.DefaultBold };
            lineTitle.SetTextColor(TaoTheme.TextMain);
            lineHead.AddView(lineTitle, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var lineBtn = new TextView(act) { Text = "修改", TextSize = 13 };
            lineBtn.SetTextColor(TaoTheme.Accent);
            var linePill = new global::Android.Graphics.Drawables.GradientDrawable();
            linePill.SetColor(TaoTheme.Bg);
            linePill.SetCornerRadius(TaoUi.Dp(act, 16));
            lineBtn.Background = linePill;
            lineBtn.SetPadding(TaoUi.Dp(act, 18), TaoUi.Dp(act, 7), TaoUi.Dp(act, 18), TaoUi.Dp(act, 7));
            var lineLabel = new TextView(act) { TextSize = 12, Text = DescribeNetworkLine() };
            lineLabel.SetTextColor(TaoTheme.TextSub);
            lineBtn.Click += (s, e) => ShowNetworkLine(act, lineLabel);
            lineHead.AddView(lineBtn, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            lineCard.AddView(lineHead, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            lineCard.AddView(lineLabel, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { TopMargin = TaoUi.Dp(act, 6) });
            AddView(lineCard, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { LeftMargin = TaoUi.Dp(act, 20), RightMargin = TaoUi.Dp(act, 20), BottomMargin = TaoUi.Dp(act, 8) });

            // 任务列表
            var adapter = new TaskAdapter(act, service);
            var list = new ListView(act)
            {
                Adapter = adapter,
                Divider = new ColorDrawable(TaoTheme.Stroke),
                DividerHeight = 0,   // v3.3 卡片化: 行与行之间用卡片留白分隔, 不再画细线
            };
            // 滚动条画到右侧留白槽里, 不压列表分隔线
            list.ScrollBarStyle = ScrollbarStyles.OutsideOverlay;
            list.SetPadding(0, 0, TaoUi.Dp(act, 14), 0);
            service.Changed += () => act.RunOnUiThread(adapter.NotifyDataSetChanged);
            AddView(list, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            // 底部操作栏
            var bottom = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Horizontal };
            bottom.SetGravity(GravityFlags.Center);
            bottom.SetBackgroundColor(TaoTheme.BarBg);
            bottom.SetPadding(TaoUi.Dp(act, 16), TaoUi.Dp(act, 10), TaoUi.Dp(act, 16), TaoUi.Dp(act, 24)); // v3.12 底部加余量避开屏幕圆角
            MakeBottomBtn(act, bottom, "全部暂停", () => service.PauseAll());
            MakeBottomBtn(act, bottom, "全部开始", () => { service.ResumeAll(); TaoUi.Toast(act, "继续下载, 从断点接着传"); });
            MakeBottomBtn(act, bottom, "清理完成", () => service.ClearFinished());
            AddView(bottom, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        /// <summary>改保存位置: 卡片弹窗输入目录, 校验/创建成功才生效; 立即落盘并通知下载服务, 已在下的任务不断</summary>
        private static void ShowChangeSaveFolder(Activity act, AllPlayer.BLL.DownloadService service, TextView label)
        {
            var input = TaoUi.MakeInput(act, "输入保存目录, 如 /storage/emulated/0/Download/我的视频");
            input.Text = AppSettings.Current.SaveFolder;
            var note = new TextView(act) { TextSize = 13, Text = "之后下载的文件都存到这个目录 (已有的文件不动)。" };
            note.SetTextColor(TaoTheme.TextSub);
            var wrap = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Vertical };
            wrap.AddView(note, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = TaoUi.Dp(act, 10) });
            wrap.AddView(input, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            TaoPopup.ShowDialog(act, "修改保存位置", wrap, new (string, Action?, bool)[]
            {
                ("取消", null, false),
                ("保存", () =>
                {
                    var dir = input.Text?.Trim() ?? "";
                    if (dir.Length == 0) { TaoUi.Toast(act, "目录不能是空的, 没改"); return; }
                    try
                    {
                        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    }
                    catch (Exception ex)
                    {
                        TaoUi.Toast(act, $"这个目录用不了: {ex.Message}", long_: true);
                        return;
                    }
                    AppSettings.Current.SaveFolder = dir;
                    service.UpdateSaveDir(dir);
                    SaveSettingsFor(act);
                    label.Text = dir;
                    TaoUi.Toast(act, "保存位置已更新");
                }, false),
            });
        }

        private static void SaveSettingsFor(Activity act)
        {
            // 面板没有设置落盘入口, 借活动的后台落盘 (避免在主线程写盘)
            if (act is TaoLive.UI.Android.Phone.MainActivity m) m.SaveSettingsSafe();
            else System.Threading.Tasks.Task.Run(() => { try { AllPlayer.DAL.SettingsFile.Save(AllPlayer.Model.AppSettings.Current); } catch { } });
        }

        /// <summary>网络线路当前状态一句话 (卡片副标题用)</summary>
        private static string DescribeNetworkLine()
        {
            return AppSettings.Current.ProxyEnabled && !string.IsNullOrWhiteSpace(AppSettings.Current.ProxyHost)
                ? $"代理 {AppSettings.Current.ProxyHost}:{AppSettings.Current.ProxyPort}"
                : "直连 (多 DNS 择优 + 连接竞速 + 快速通道)";
        }

        /// <summary>
        /// v3.25.0 网络线路设置: 输入 host:port 走代理 (HTTP 代理, 如本机 Clash 端口), 留空 = 直连。
        /// 产品不内置任何代理, 只支持用户自配; 直连时内置网络优化 (多 DNS 择优 + 连接竞速 + 快速通道) 自动生效。
        /// </summary>
        private static void ShowNetworkLine(Activity act, TextView label)
        {
            var input = TaoUi.MakeInput(act, "host:port, 如 192.168.1.2:7890; 留空 = 直连");
            input.Text = AppSettings.Current.ProxyEnabled && !string.IsNullOrWhiteSpace(AppSettings.Current.ProxyHost)
                ? $"{AppSettings.Current.ProxyHost}:{AppSettings.Current.ProxyPort}"
                : "";
            var note = new TextView(act) { TextSize = 13, Text = "直连 = 内置网络优化 (多 DNS 择优 + 连接竞速 + 快速通道), 弱网自动单步降码率保流畅 (不影响边播边存画质); 填代理 = m3u8 与网页类请求走该代理 (HTTP 代理)。注意: 磁力下载走 BT 协议, 不经 HTTP 代理, 当前版本也不提供磁力代理接口。本产品不内置代理, 只支持自配。" };
            note.SetTextColor(TaoTheme.TextSub);
            var wrap = new LinearLayout(act) { Orientation = global::Android.Widget.Orientation.Vertical };
            wrap.AddView(note, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = TaoUi.Dp(act, 10) });
            wrap.AddView(input, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            TaoPopup.ShowDialog(act, "网络线路", wrap, new (string, Action?, bool)[]
            {
                ("取消", null, false),
                ("保存", () =>
                {
                    var text = input.Text?.Trim() ?? "";
                    if (text.Length == 0)
                    {
                        AppSettings.Current.ProxyEnabled = false;
                        AppSettings.Current.ProxyHost = "";
                        AppSettings.Current.ProxyPort = 0;
                    }
                    else
                    {
                        var colon = text.LastIndexOf(':');
                        var portOk = colon > 0 && int.TryParse(text.Substring(colon + 1), out var port) && port > 0 && port <= 65535;
                        if (!portOk) { TaoUi.Toast(act, "格式不对, 要写成 host:port"); return; }
                        AppSettings.Current.ProxyEnabled = true;
                        AppSettings.Current.ProxyHost = text.Substring(0, colon);
                        AppSettings.Current.ProxyPort = int.Parse(text.Substring(colon + 1));
                    }
                    SaveSettingsFor(act);
                    label.Text = DescribeNetworkLine();
                    TaoUi.Toast(act, AppSettings.Current.ProxyEnabled ? "网络线路已切到代理" : "网络线路已切回直连");
                }, false),
            });
        }

        /// <summary>底部操作按钮 (v3.7 按风格重做): 等宽胶囊底 + 卡片面 + 描边, 文案用主题字色, 与主页胶囊按钮同一气质。</summary>
        private static void MakeBottomBtn(Activity act, LinearLayout bar, string text, Action click)
        {
            var b = new TextView(act) { Text = text, TextSize = 14, Gravity = GravityFlags.Center };
            b.SetTextColor(TaoTheme.TextMain);
            // 胶囊底: 卡片面填充 + 细描边, 按下去换 Accent 描边示意有反馈
            var bg = new global::Android.Graphics.Drawables.GradientDrawable();
            bg.SetCornerRadius(TaoUi.Dp(act, 22));
            bg.SetColor(TaoTheme.Face);
            bg.SetStroke(TaoUi.Dp(act, 1), TaoTheme.Stroke);
            b.Background = bg;
            b.Click += (s, e) => click();
            bar.AddView(b, new LinearLayout.LayoutParams(0, TaoUi.Dp(act, 42), 1f)
            { LeftMargin = TaoUi.Dp(act, 6), RightMargin = TaoUi.Dp(act, 6) });
        }

        public void Show() => Visibility = ViewStates.Visible;
        public void Hide() => Visibility = ViewStates.Gone;

        /// <summary>返回键: 面板开着就收起</summary>
        public bool HandleBack()
        {
            if (Visibility != ViewStates.Visible) return false;
            Hide();
            return true;
        }

        // ===== 任务行 =====
        private class TaskAdapter : BaseAdapter
        {
            private readonly Activity _act;
            private readonly AllPlayer.BLL.DownloadService _svc;

            public TaskAdapter(Activity act, AllPlayer.BLL.DownloadService svc) { _act = act; _svc = svc; }

            public override Java.Lang.Object GetItem(int position) => null!;
            public override long GetItemId(int position) => position;
            public override int Count => _svc.Tasks.Count;

            public override View GetView(int position, View? convertView, ViewGroup? parent)
            {
                var tasks = _svc.Tasks;
                if (position >= tasks.Count) return new View(_act);
                var t = tasks[position];

                LinearLayout row;
                TextView name, status, btnToggle, btnDel;
                ProgressBar prog;
                if (convertView is LinearLayout r && r.Tag is RowViews v)
                {
                    row = r; name = v.Name; status = v.Status; prog = v.Prog; btnToggle = v.Toggle; btnDel = v.Del;
                }
                else
                {
                    row = new LinearLayout(_act) { Orientation = global::Android.Widget.Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    row.SetPadding(TaoUi.Dp(_act, 14), TaoUi.Dp(_act, 10), TaoUi.Dp(_act, 10), TaoUi.Dp(_act, 10));
                    row.Background = TaoUi.Card(_act, 24);   // v3.5 ColorOS 卡片: 大圆角无描边 + 投影分层
                    row.Elevation = 4;
                    var col = new LinearLayout(_act) { Orientation = global::Android.Widget.Orientation.Vertical };
                    name = new TextView(_act) { TextSize = 15 };
                    name.SetTextColor(TaoTheme.TextMain);
                    name.SetSingleLine(true);
                    prog = new ProgressBar(_act, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
                    {
                        Max = 10000,
                        Indeterminate = false,
                    };
                    prog.ProgressTintList = ColorStateList.ValueOf(TaoTheme.Accent);
                    prog.ProgressBackgroundTintList = ColorStateList.ValueOf(TaoTheme.Stroke);
                    status = new TextView(_act) { TextSize = 12 };
                    status.SetTextColor(TaoTheme.TextSub);
                    col.AddView(name, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                    col.AddView(prog, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, TaoUi.Dp(_act, 5)) { TopMargin = TaoUi.Dp(_act, 6) });
                    col.AddView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = TaoUi.Dp(_act, 4) });

                    btnToggle = new TextView(_act) { TextSize = 16, Gravity = GravityFlags.Center };
                    btnToggle.SetPadding(TaoUi.Dp(_act, 14), TaoUi.Dp(_act, 10), TaoUi.Dp(_act, 14), TaoUi.Dp(_act, 10));
                    btnToggle.SetTextColor(TaoTheme.Accent);
                    btnDel = new TextView(_act) { Text = "✕", TextSize = 18, Gravity = GravityFlags.Center };
                    btnDel.SetPadding(TaoUi.Dp(_act, 14), TaoUi.Dp(_act, 10), TaoUi.Dp(_act, 14), TaoUi.Dp(_act, 10));
                    btnDel.SetTextColor(FunctionalColors.Danger());

                    var btns = new LinearLayout(_act) { Orientation = global::Android.Widget.Orientation.Horizontal };
                    btns.AddView(btnToggle);
                    btns.AddView(btnDel);
                    row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                    row.AddView(btns, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                    row.Tag = new RowViews { Name = name, Status = status, Prog = prog, Toggle = btnToggle, Del = btnDel };
                }

                name.Text = t.Name;
                prog.Visibility = t.Status == "已完成" ? ViewStates.Gone : ViewStates.Visible;
                if (t.Total > 0)
                {
                    prog.Indeterminate = false;
                    prog.Progress = (int)(10000.0 * t.Downloaded / t.Total);
                }
                else
                {
                    prog.Indeterminate = t.Status == "下载中";
                    prog.Progress = 0;
                }

                var sizeText = t.Total > 0
                    ? $"{AllPlayer.BLL.DownloadService.FormatSize(t.Downloaded)} / {AllPlayer.BLL.DownloadService.FormatSize(t.Total)}"
                    : AllPlayer.BLL.DownloadService.FormatSize(t.Downloaded);
                status.Text = t.Status switch
                {
                    "下载中" => $"下载中 {sizeText}",
                    "已暂停" => $"已暂停 {sizeText}" + (t.Error.Length > 0 ? " · " + t.Error : ""),
                    "已完成" => "已完成 · " + sizeText,
                    "失败" => "失败 · " + t.Error,
                    _ => "排队中…",
                };
                status.SetTextColor(t.Status switch
                {
                    "下载中" => TaoTheme.Accent,
                    "已完成" => OkColor(),
                    "失败" => ErrColor(),
                    _ => TaoTheme.TextSub,
                });

                // 按钮: 暂停/继续 二选一, 完成的隐藏
                btnToggle.Text = t.Status is "下载中" or "排队中" ? "暂停" : "继续";
                btnToggle.Visibility = t.Status == "已完成" ? ViewStates.Gone : ViewStates.Visible;
                btnToggle.Click -= RowToggleClick;
                btnToggle.Click += RowToggleClick;
                btnToggle.Tag = t.Id;
                btnDel.Click -= RowDelClick;
                btnDel.Click += RowDelClick;
                btnDel.Tag = t.Id;
                return row;

                void RowToggleClick(object? s, EventArgs e)
                {
                    var id = (string)((TextView)s!).Tag!;
                    var task = _svc.Tasks.FirstOrDefault(x => x.Id == id);
                    if (task == null) return;
                    if (task.Status is "下载中" or "排队中") _svc.Pause(id);
                    else { _svc.Resume(id); TaoUi.Toast(_act, "从断点继续下载"); }
                }

                void RowDelClick(object? s, EventArgs e)
                {
                    var id = (string)((TextView)s!).Tag!;
                    var task = _svc.Tasks.FirstOrDefault(x => x.Id == id);
                    var delFile = task?.Status == "已完成";
                    TaoPopup.ShowMessage(_act, "删除任务",
                        delFile ? "同时删除已下载的文件?" : "未完成的部分会一并清掉, 继续吗?",
                        new (string, Action?, bool)[]
                        {
                            ("取消", null, false),
                            ("删除", () => _svc.Delete(id, delFile), true),
                        });
                }
            }

            internal static Color OkColor() => FunctionalColors.Ok();
            internal static Color ErrColor() => FunctionalColors.Danger();

            private class RowViews : Java.Lang.Object
            {
                public TextView Name = null!, Status = null!, Toggle = null!, Del = null!;
                public ProgressBar Prog = null!;
            }
        }
    }
}
