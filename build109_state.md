# 道映 v1.0.9 构建状态（断点文件）

## 十三项修复状态（①–⑬ 全部落地，见交付说明逐项）

- ①退出确认弹窗闭环：✔（返回键→ShowExitConfirmDialog→ExitAppCompletely；OnPause 统一停播；OnResume 分流重接）
- ②频道列表默认全展开：✔（_expandedInit 首次全置 true，手动折叠保留）
- ③全页面对比度拉高+亮度自适应：✔（InkTranslucency 自适应 API 落地，MainActivity 接线：亮牌/信息条/列表面板/设置面板/当前台整行强调底；弹窗深底浅字）
- ④列表旁操作提示全部去掉→实时画面信息行：✔（FrameStatsLine 纯逻辑+LiveVideoHost.SnapshotStats；2 秒节拍刷新；测量缺失显示「画面信息检测中」绝不编数字；SmartGovernor 原有 AiEffectHeader 保留但 UI 不再使用旧口径）
- ⑤列表长按拖动调快：✔（按用户 10:04 拍板被⑥取代：去掉与上下键重合的翻页/拖动逻辑）
- ⑥列表左右键=分类间跳转、上下=选台、确认=播放：✔（JumpCategory 沿方向找最近分类头）
- ⑦展开/收起分类光标锚定当前分类位置：✔（_pendingFocusAnchor=分类键，重建后 SetSelection 归位）
- ⑧分类标题统计加可连数：✔（ListHeaderStats「央视(21·可连18)」；口径=至少一来源可达；未出结果先显总数；StateChanged 静默刷新不断焦点）
- ⑨「去系统设置」三级跳：✔（显示设置页→系统设置总入口→文字指引，分级降级）
- ⑩崩溃日志一键清理：✔（SetNeutralButton 清空+空态提示「暂无崩溃日志」）
- ⑪全局焦点锚定：✔（CurrentFocusKey/锚点消费：收藏切换、分类展开收起、预检重建均锚原位；排序模式维持跟随被排序台）
- ⑫播放卡住分级快速自救：✔（StallRescue 纯逻辑：静默重取流→降清晰度(AI 降档)→软重启播放内核 RestartKernel；看门狗 6 秒停滞触发；静默级不发状态不占重连计数）
- ⑬预检按频道全源检测+来源级健康表：✔（ChannelPrecheck 同名频道变体逐一探测任一通=可用；SourceHealthEntry 逐源记录；BestAliveSource 供播放选源与自救换源；播放实测/取流失败 ReportSourceResult 回写覆盖预检结论）

## 全量测试
- 259 PASS / 0 FAIL（含 v1.0.9 新增 13 断言：⑫分级自救 6 项 + ④画面信息行 5 项 + ⑧统计 2 项）

## 出包口径
- csproj ApplicationVersion=10 / DisplayVersion=1.0.9；AndroidManifest versionCode=10 / versionName=1.0.9（均已确认）
- 出包：dotnet publish src/TaoLive.UI.Android.Tv -c Release -f net9.0-android35.0 -p:RuntimeIdentifiers=android-arm
- 签名：keystore/debug.keystore 同源密钥；apksigner 验签 v1/v2/v3
- NU1101 兜底：restore -p:RuntimeIdentifiers="" 或 -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true

## 当前步
- ✔ 交付完成（2026-11-08 11:50）：
  - 最终 APK：dist/道映-v1.0.9-tv-32位专用.apk（79.2MB 未裁剪签名版）+ 道映-v1.0.9-32位电视专用APK.zip
  - SHA-256：b6bbad200a486da13d55a66f19381ba7824897b6d3225890f09f6cfd4929a06f
  - apksigner verify：v1/v2/v3 全通过；badging versionCode=10 / versionName=1.0.9 核对一致
  - 交付说明.md 已增补 v1.0.9 十三项清单+哈希；产物已 publish 并 attach 至事项 1101892；progress 已记录
  - 已向父会话汇报。真机复测按 12-01 跑道恢复后统一补验
