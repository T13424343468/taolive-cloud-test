using System;
using System.IO;
using System.Threading;
using AllPlayer.BLL;
using Android.App;

namespace TaoLive.UI.Android.Phone
{
    /// <summary>
    /// 实时增强基准 UI 入口（v3.17.0 partial MainActivity，播放层零改动只加）：
    /// 更多菜单「实时增强基准」→ 后台线程跑 AiRealtimeBench → 结果对话框 +
    /// 落盘 CacheDir/realtime-bench.json（拿真机数据，为自渲染实时管线定档）。
    /// </summary>
    public partial class MainActivity
    {
        internal void RunRealtimeBenchEntry(bool force = false)
        {
            try
            {
                var path = _host?.CurrentPath;
                if (string.IsNullOrEmpty(path) || path.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    TaoUi.Toast(this, "先播放一个本地视频文件再测");
                    return;
                }

                // v3.18.8: 已测过的片源直接回上次结果, 不再让用户干等十几秒重测;
                // 想重测用结果框里的「重新测量」按钮, 明确动作才重新跑
                if (!force)
                {
                    var cached = TryLoadBenchCache();
                    if (cached != null && cached.Ok && cached.Source == System.IO.Path.GetFileName(path))
                    {
                        ShowBenchResult(cached, fromCache: true);
                        return;
                    }
                }

                if (_aiModelsDir == null)
                {
                    // v3.18.5: 装配没好不再干等——点按即触发装配（幂等），装好主动提醒再点;
                    // 病根(残留坏文件挡重解压+旧版重试三次即永久放弃)已在装配侧修复
                    AppLog.W("基准入口: AI 模型未就绪, 点按即装+就绪提醒");
                    EnsureAiAssembly();
                    AiAssemblyReady -= OnAiAssemblyReadyForBench;
                    AiAssemblyReady += OnAiAssemblyReadyForBench;
                    TaoUi.Toast(this, "增强功能还在准备中，装好会提醒你");
                    return;
                }
                TaoUi.Toast(this, "实时增强基准测量中…（约半分钟，不影响播放）");
                var modelsDir = _aiModelsDir;
                var t = new Thread(() =>
                {
                    var r = AiRealtimeBench.Run(path, modelsDir);
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(CacheDir!.AbsolutePath, "realtime-bench.json"),
                            r.Serialize());
                    }
                    catch { }
                    RunOnUiThread(() => ShowBenchResult(r));
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "taoai-bench" };
                t.Start();
            }
            catch { }
        }

        /// <summary>v3.18.8: 读上次基准结果缓存 (CacheDir/realtime-bench.json), 读不到返回 null。</summary>
        private AiRealtimeBenchLog? TryLoadBenchCache()
        {
            try
            {
                var f = Path.Combine(CacheDir!.AbsolutePath, "realtime-bench.json");
                if (!File.Exists(f)) return null;
                return AiRealtimeBenchLog.TryParse(File.ReadAllText(f), out var r) ? r : null;
            }
            catch { return null; }
        }

        /// <summary>v3.18.5 装配就绪提醒（UI 线程）：订阅一次，提醒后自动退订。</summary>
        private void OnAiAssemblyReadyForBench()
        {
            try
            {
                AiAssemblyReady -= OnAiAssemblyReadyForBench;
                if (_aiModelsDir != null)
                    TaoUi.Toast(this, "增强功能准备完成，请再点一次实时增强基准");
            }
            catch { }
        }

        private void ShowBenchResult(AiRealtimeBenchLog r, bool fromCache = false)
        {
            try
            {
                string msg;
                if (!r.Ok)
                {
                    msg = $"基准没跑成：{r.Note}";
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"设备: {r.Device}");
                    // v3.20.0: 如实展示算力在岗口径（Vulkan GPU 在岗=1 / 无 Vulkan 回退 CPU=0）
                    sb.AppendLine($"算力: {(TaoAIKernel.GpuReady() == 1 ? "GPU（Vulkan）在岗" : "CPU 兜底（本机无可用 Vulkan）")}");
                    sb.AppendLine($"片源: {r.Source} ({r.SrcW}×{r.SrcH})");
                    sb.AppendLine($"基准输入: {r.SrInW}×{r.SrInH}（与增强管线同口径）");
                    sb.AppendLine();
                    foreach (var it in r.Items)
                        sb.AppendLine(string.IsNullOrEmpty(it.Note) && it.AvgMs <= 0
                            ? $"{it.Label}: 没测出来"
                            : AiRealtimeBenchPlan.SummaryLine(it));
                    sb.AppendLine();
                    sb.AppendLine(AiRealtimeBenchPlan.Headline(r));
                    sb.AppendLine(fromCache
                        ? "(上次测量结果, 已存缓存目录 realtime-bench.json)"
                        : "(明细已存缓存目录 realtime-bench.json)");
                    msg = sb.ToString();
                }
                // 构建器绑定点位标注可空：拆开语句逐环判空（局部变量解引用后状态即非空）
                var dlg = new AlertDialog.Builder(this);
                dlg.SetTitle("实时增强基准");
                dlg.SetMessage(msg);
                dlg.SetPositiveButton("好", (s, e) => { });
                dlg.SetNeutralButton("重新测量", (s, e) => RunRealtimeBenchEntry(force: true));   // v3.18.8: 明确动作才重跑
                dlg.Show();
            }
            catch { }
        }
    }
}
