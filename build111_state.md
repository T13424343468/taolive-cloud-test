# 道映 v1.0.20 批1 构建状态（断点文件：A 健康账评分升级 + E 预热顺路记账）

## 批1 落码进度（v3.1 冻结稿口径，基线 v1.0.20，已交付止血包行为一律未碰）

- 【A·卡顿率维度闭环：✔ 已落码】
  - ChannelPrecheck.ReportStallObserved(ch, stalled)（BLL 纯逻辑）：巡检节拍对当前播放源观测一次，EMA(α=0.5) 收敛近期卡顿率；只记快慢不改通断（State/CheckedAtMs 原样，判死仍连败 3 次原口径）；HealthStore 脏标记节流落盘。
  - MainActivity.TickGovernor 接线（5 秒巡检节拍）：观测先于自动档守卫（热切换/换台挂起期间照记不漏窗口）；卡顿口径=首帧已出后播放时钟停滞（_frameForCurrentSwitch && !IsAlive）——起播连接期不算卡顿（那段走延迟与失败口径，不污染卡顿账）。零额外请求。
  - 既有底座核对（v1.0.16 已在位，本批未动）：SourceScorer（延迟 0.4+流畅 0.3+连胜 0.3 加权，按距上次使用每小时减半指数衰减不清零）、BestAliveSource 评分选源、SourceHealthEntry/SourceHealthRecord 四字段、MarkUsed 衰减基准、判死淘汰与过期清理原样。
- 【A/E·播放实测首帧延迟记账：✔ 已落码】
  - Play() 起记 _playStartTicks+_awaitFirstFrame；FrameShown 首个首帧落地时（起播窗口内，记一次即收）ReportSourceResult 带 delayMs=Play→首帧全程时长（含取票往返与内核起播，EMA 入账）；稳播期换档/热切帧不记，延迟账不被污染。
  - 预检探测顺路记延迟（v1.0.16 已在位）：RunAsync 计时→WriteSourceResult EMA，本批未动。
  - E 口径核对：预热循环=预检探测循环（ProbeChannelAsync 顺路抓清单入缓存），记账已零新增请求覆盖；取票延迟已含在播放实测全程时长内。
- 【落盘向后兼容：✔ 已核验】旧版账本 JSON（无 A/E 新字段）Load 缺省安全（FirstDelayMs=0/StallRate=0/SuccessStreak=0/LastUsedMs=0），断言在位。

## 改动文件清单
1. src/TaoLive.BLL/ChannelPrecheck.cs——新增 ReportStallObserved（EMA 卡顿观测纯逻辑）
2. src/TaoLive.UI.Android.Tv/MainActivity.cs——新增 _playStartTicks/_awaitFirstFrame 字段；Play 记延迟起点；FrameShown 首帧延迟入账；TickGovernor 卡顿率顺路回写（先于自动档守卫）
3. tests/TaoLive.Tests/Program.cs——新增 9 断言（卡顿账 EMA 三段收敛/只记快慢不改通断/劣迹降分/全卡死垫底；播放实测延迟入账/EMA 平滑；旧账向后兼容）
4. build111_state.md（本文件）

## 测试结果
- 全量 589 PASS / 0 FAIL（基线 580 + 批1 新增 9 断言），连跑两遍全绿
- BLL Release 0 警 0 错；UI 安卓 publish 0 条代码警告（CS），仅 XA0033/XA0141 两条非代码警告（与 v1.0.19/v1.0.20 同口径报备：Java 版本串环境怪癖/上游 libvlc so 未 16KB 对齐）
- 32 位签名包已重验构建通过（publish/com.taolive.tv-Signed.apk，批1 不出包，包随批6/7 总装交）

## 与冻结稿逐条对照
- A「健康条目加两个维度：首包延迟/近期卡顿率」→ FirstDelayMs（预检探测+播放实测双源）、StallRate（巡检 5 秒顺路）双维度齐备
- A「顺路记时长，零额外请求」→ 预检探测计时既有 + 播放实测首帧计时新增，均零额外请求
- A「评分=延迟、卡顿率、连续成功加权，指数衰减、久不用沉底不清零」→ SourceScorer 既有实现逐条吻合（权重 0.4/0.3/0.3 为已交付冻结参数，本批未动）
- A「判死仍连败 3 次，手动重试复位；淘汰/落盘/过期清理规则全保留」→ 原口径未动，断言在位
- A「落盘字段扩展向后兼容旧账」→ SourceHealthRecord 缺省反序列化，断言在位
- E「预热探测与取票顺路记延迟，不加任何新请求」→ 预检探测计时+播放实测首帧计时，零新增请求
- E「失败只落日志」→ 预检探测异常落 BllLog（既有），本批未动

## 遗留与风险
- 冻结稿场景⑬「评分里卡顿率权重占主导」与已交付权重 0.4/0.3/0.3（延迟主导）存在文字张力：按「已定稿参数不动」纪律维持现权重，卡顿观测 EMA(α=0.5) 已保证「更信刚测结果」；如需权重调整须走冻结参数变更流程报老板拍板。
- TickGovernor 卡顿观测在 IsSwapPending（热切换进行中）期间仍读当前 _host 快照——双实例提交瞬间可能记到旧流末帧状态，属单窗毛刺，EMA α=0.5 两次即稀释，风险可忽略。
- _awaitFirstFrame 若首帧永不落地（黑屏判死路径）保持 true 直到下次 Play 重置——判死路径走 ReportSourceResult(false) 不带延迟，无污染。
- XA0141（上游 libvlc 未 16KB 对齐）：Android 16 设备风险沿用 v1.0.20 报备口径。

## 当前步
- ✔ 批1 完成（2026-11-09 11:24）：A/E 落码、断言 9 项新增、全量 589/0 两遍、BLL 0 警 0 错、UI publish 0 代码警告
- 待小光批1 验收（独立复跑重建断言）；批2（B 换台竞速）11-10 12:00 前

## 【线1】批2 B 换台探测竞速（2026-11-09）

- 核对结论：B 竞速已在 v1.0.16 落码（RushNet.cs 中 RacePreconnector：评分前 3 路参选、单路 1.2s / 总窗 1.5s、同源 30s 间隔、被拒交冷却、挂播放代数作废、全败退现行链路），MainActivity 已接线（换台任务内 _rushEnabled 开启时先竞速，胜者直取并 MarkUsed）。冻结稿 B 要求逐条核对均在位，无需重做。
- 本批补齐：①MainActivity 接线新增场景⑫内存档收缩——RaceSwitchSourceAsync 在 Pick 后按 RaceConcurrencyPolicy.Cap(可用内存) 经 Shrink 截选，低配设备竞速并发 3→2；②tests/Program.cs 新增 6 断言（Cap 三档与未知兜底、Shrink 降序截取与达上限不截断），补齐 RaceConcurrencyPolicy 此前的断言空缺。
- 测试结果：全量 597 PASS / 0 FAIL 两遍全绿（589 + 新增 6）；BLL Release 0 警 0 错。
- 未动文件：RushNet.cs、StreamResolver.cs、ChannelPrecheck.cs 均未修改（线2/批1 所有物）。
- 当前步：✔ 批2 完成（2026-11-09 午），待 12:45 与线2 合流后走批6 走查与全测。

## 【线1】收口确认（2026-11-09 11:45，13:00 死线轮）
- 复跑全量 606 PASS / 0 FAIL（589 基线 + 批1 9 断言 + 批2 6 断言 + 此前批内累计断言），全绿一遍确认。
- 批2 状态：✔ 完成并收口。竞速逻辑（RushNet.cs RacePreconnector）+ MainActivity 接线（含场景⑫内存档收缩 RaceConcurrencyPolicy）+ 6 断言齐备；未动线2 所有物。
- 线1 无阻塞。12:10 收口口径满足：改动清单=RaceConcurrencyPolicy.cs（新）/MainActivity.cs（接线+收缩）/tests（+6 断言）；断点=build111_state.md【线1】段落。
- 待 12:20 合流后批6：22 场景走查+恶意专项+九层全测（本批免双检，小光转支援）。
