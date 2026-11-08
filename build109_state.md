# 道映 v1.0.9 构建状态（断点文件）

## 已完成项
- [确认] 基线测试 230/0 全绿
- [确认完整] 真机反馈①：退出确认弹窗闭环（返回键→ShowExitConfirmDialog→ExitAppCompletely 真停止 _host.Stop + 结束进程；OnPause 统一 _host.Stop；OnResume 用 _resumedOnce 分流首启/回前台重接，OnNewIntent 不再自行起播）
- [确认完整] 真机反馈②：频道列表默认全展开（_expandedInit 首次全置 true，收藏组/无信号组默认展开，手动折叠保留）
- [确认完整] 真机反馈③：对比度拉高/亮度自适应——InkTranslucency 自适应 API（IsLowBrightness/Boost/PanelColor/InfoColor/LoadingColor/CurrentRowFill/TextPrimary/ApplyReadabilityShadow）已落 Shared/InkTranslucency.cs，MainActivity 已接线约 19 处（亮牌/信息条/列表面板/设置面板/无信号组行/当前台整行强调底）；弹窗本身深底浅字对比达标
- [已在树内] 广西 GxtvSource（JstvSource.cs 内，9 路）、Nmtv 包头 streamId 2358（10 路）、StreamResolver gxtv 分支、tests/Program.cs 同步
- [已改] 版本号 csproj ApplicationVersion=10 / DisplayVersion=1.0.9；AndroidManifest versionCode=10 / versionName=1.0.9

## 新增任务⑬（老板 2026-11-08 拍板「按你说的改」+「同时兼容上一条自动检测画面切换源的情况」）
- 预检升级为按频道全源检测：同频道所有来源逐一轻量探测；任一来源通=频道可用；全挂才进无信号组
- 来源级健康表：每源通断单独记录；播放选源与⑫自救换源优先最近验证可用来源
- 与⑫兼容：自救换源消费健康表，成功/失败回写（播放实测覆盖预检结论）；并发互不抢占播放资源
- 与⑧口径统一：分类可连数=至少一个来源可达的频道数；无信号组=全源不可达
- 每轮预检结果实时刷新 UI（沿用 StateChanged），不断焦点不闪烁

## 当前步
- 补全真机反馈③：对比度拉高 + 亮度自适应通用实现（InkTranslucency 升级 + 当前行醒目高亮）
- 然后依次 ④⑤⑥⑦⑧⑨⑩⑪⑫⑬

## 下一步
- 全量测试（不许减断言）→ 出包 android-arm Release（NU1101 兜底：restore -p:RuntimeIdentifiers="" 或 -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true）→ apksigner 验签 → 交付说明.md 增补 → publish_file + attach(project 1101892) + progress

## 关键命令
- 测试：cd tests/TaoLive.Tests && /root/workspace/.local/dotnet9/dotnet run -v q --nologo
- SDK：/root/workspace/.local/dotnet9/dotnet
- 出包：dotnet publish src/TaoLive.UI.Android.Tv -c Release -f net9.0-android35.0 -p:RuntimeIdentifiers=android-arm
- 签名：keystore/debug.keystore（同源密钥，csproj 已配置）；apksigner 验签 v1/v2/v3

## 本轮进展（子代理接手后）
- 已复核：版本号 csproj=10/1.0.9、AndroidManifest versionCode=10/1.0.9，均已更新到位。
- 正在进行：真机反馈③ 对比度拉高 + 亮度自适应（InkTranslucency 自适应 API 已就位，正在接入 MainActivity 各处并加当前台高亮）。
