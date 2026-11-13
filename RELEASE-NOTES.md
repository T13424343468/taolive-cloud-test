# 道映电视版 vc46（1.0.12）交付说明

**发布日期：2026-11-13 · 包名 com.taolive.tv · versionCode 46 / versionName 1.0.12**

## 本批内容（vc46「有问题的全改」+ 死循环专项审计）

- **vc46 五组件接线**：StreamResolver 三本可注入账本（冷却/判死/切换预算）、分类探针（403/429=票据过期、伪200=内容无效、网络失败=超时）、候选遍历账面挡板、连死判死+复检复活、帧级切换预算统一收口、回主驻留 ≥2 分钟、分片级深探。
- **死循环专项审计（老板令：审一轮改一轮至清零）**：全源码 26757 行四路静态审计（TaoLive.BLL / AllPlayer.BLL / Phone UI / Tv UI），第一轮判级 A=0 B=7 C=80+，七处 B 级全部落码修复，第二轮复审七处 diff 零新增发现，全量回归 574 PASS / 0 FAIL 零警零错：
  1. AllPlayer.BLL 三处 Accept 环（HlsStreamService/LocalStreamServer/CastService）：accept 持续失败裸 continue 热自旋 → 加 500ms 退避；
  2. TaoLive.BLL NetSelfHeal：代理自环链兜底（target 指向本机代理端口入口短路 404，与 /ping 同口径）；
  3. TaoLive.BLL StreamDnsPicker：最后一路系统 DNS 无超时挂等 → 独苗路 10s 上限，超时按全挂兜底；
  4. Phone AiSegmentRunner：主编码环解码器卡死无出口 → E11 同款心跳出口（约 30 秒双零判死，整段弃用）；
  5. Phone AiRealtimeBench：idle 只在 EOS 喂入后计数，卡死早于 EOS 时永久空转 → 口径对齐 E11（喂入/产出任一有进展即清零）。
- **vc46B/C/E 等记录级修复**：动态库拒收内置表同 URL（拒收留痕）、PublicListDiscover single-flight、Tv/Phone 回灌先赋 OnChanged 再 Load 时序、StreamLanes 摘除、UTF-16 注释清理、注释勘误。

## 测试

逻辑全测 574 PASS / 0 FAIL，零错零警（含 vc46 新增 13 项断言与死循环审计修复回归）。
打包：aapt2 badging 核对 package/com.taolive.tv、versionCode 46、versionName 1.0.12 一致；apksigner 验签通过。

## 已知事项（不隐瞒）

1. 热备帧级切换预算、驻留回主、分片深探等 vc46 五组件均为新接线，真机表现待验证（清单随交付附）。
2. 16 KB 页对齐告警（libvlc/libtaoai 既有三方内核产物）不变。

## 回滚

如真机异常，卸载即回滚；无系统级改动。

---

# 道映（TaoLive 手机版）1.0.0-r1 交付说明

**发布日期：2026-11-11 · 包名 com.taolive.phone · versionCode 1 / versionName 1.0.0-r1 · 应用名「道映」**

## 包内容

| 文件 | 说明 |
| --- | --- |
| 道映手机版-1.0.0-r1.apk | 签名安装包（C1 证书链，与电视版同源；证书 SHA-256 前缀 c18030d3…） |
| 源码/（zip 内目录） | 完整源码（git 04a2f14 时点，git archive） |
| RELEASE-NOTES.md | 本说明 |

## 适配机型

一加 15T / ACE5（64 位 arm64）。不再提供 32 位手机包；电视版 32 位口径不受影响。

## 功能清单（对照老板 11-11 00:08–00:22 拍板）

- **AI 实时优化主线**：直播分片到手立即 AI 逐段加工（ncnn 超分 4× + RIFE 插帧，沿用 TaoPlay 内核 arm64/libtaoai.so），加工完放行播放；画质档位实时显示（如 1080P→4K）。
- **AI 缓冲池（AiBufferPool）**：初始缓冲目标 15 秒（10–20 区间钳制）、落后 60 秒封顶强制直出（延迟锁定，换台复位）、加工速率 EWMA 追不上自动建议降档、过热/低电退原画质直出；逻辑层全测绿，**直播分片回调的最终接线在真机联调阶段完成**（见下方"已知事项"）。
- **播放页**：沿用 TaoPlay 手机版——顶栏返回/台名/锁定/更多；底行上一台/暂停/下一台；亮度音量手势；全屏锁定横屏；播放锁定纯看片态解锁键 2.5 秒；画中画。
- **砍件（直播口径）**：倍速（含长按 3x）、进度条/拖动预览/横滑快进退整套退场，播放为实时直出。
- **频道列表页**：顶部筛选框——数字命中频道号、文字命中台名、模糊组合通配符，边输边筛点选跳台。

## 测试

逻辑全测 428 PASS / 0 FAIL，零错零警（net9.0 测试工程，含 AI 缓冲池 15 项口径用例）。
编译：arm64 Release 发布成功，apksigner 验签通过，aapt2 badging 核对包名/版本/应用名一致。

## 已知事项（不隐瞒）

1. **AI 缓冲池与直播分片回调的最终接线**：缓冲池治理逻辑（初始缓冲/封顶/降档/热电保险）已落码并全测绿；直播 HLS 分片下载回调位于播放内核层，其挂接在真机联调时完成并复测。当前包内分段加工链路沿用 TaoPlay 家底原样工作。
2. 16 KB 页对齐告警（libvlc/libtaoai 为既有三方内核产物）属 SDK 前瞻提示，Android 14 及以下安装不受影响。
3. 首次启动如遇系统询问"自启/后台权限"，按引导放行即可；默认自启开。

## 回滚

如真机异常，卸载即回滚；无系统级改动。
