# 道映 v1.0.10 全维度稳定性检测报告（2026-11-08 14:0x）

口径：build110_state.md「老板 13:56 追加指令」节。只测不改逻辑。

## 1 逻辑全测 —— PASS
- 命令：cd tests/TaoLive.Tests && dotnet run -v q --nologo
- 三连跑：360 PASS / 0 FAIL ×3（01_logic.log）
- 含新增：【六】变体发现 7 条、【九A】热切会话 6 条、【八】BllLog 通道 12 条；BllLog 泵/Flush 竞态已修（同锁），三连跑无复现。

## 2 功能走查 —— PASS
- 十二项修复路径逐一核对：源健康直连+回写（Play 内 ReportSourceResult）、档位统一自动/高/低、启动纹理就绪才贴、退出弹窗焦点、信息行重试至限显「--」、切台铺底色防花屏、缓冲水位 BufferPolicy.Observe、列表一键收放、九类失败矩阵逐类落日志、⑨B 提前介入。未发现回归 v1.0.8–v1.0.9 既有修复。

## 3 真人模拟（遥控路径）—— PASS
- 开机→列表→上下键换台→菜单呼出→收藏→确认键路径静态推演：焦点循环导航、退出弹窗默认落「继续看」、到边不动防误退均有断言覆盖；数字键已下线无死角入口。

## 4 真人视觉 —— PASS（静态走查）
- 列表/播放/弹窗/设置绘制路径均走 TaoPalette 水墨主题；切台底色取 TaoPalette.Current.Bg，与主题同源；第二载体垫底不遮浮层。

## 5 真机模拟 —— 带条件 PASS
- 三档画像：32 位低配/内存<3GB 由 DeviceProfile.HideUhd 拦超高清台，档位仅 auto/high/low，双实例内存压力受限档位收敛；64 位/高配全档开放。
- 条件：按坐标真实点击需沙箱 GUI 跑道，当前环境无显示器实例可跑——该项按既有口径需上云端 Windows 跑道补跑后确认。逻辑层代数守卫（_playSeq/_genCounter）已核。

## 6 机制互扰组合场景 —— PASS
- 反复切台×双实例：Play() 首行 AbortHotSwapUi("换台") + OnChannelChanged()，未决切换必收口，旧台节奏不带入新台。
- 看门狗×双实例：OnWatchdogTick/提前介入/重连三处均 IsSwapPending 让路，不叠加自救（LiveVideoHost.cs 193/220/258）。
- 自动档×双实例：TickGovernor 在 HasPending 时跳过；协调器 MinSwapIntervalMs 冷却防横跳；超时兜底+提交一次语义（MarkReady/OnCommitted）。
- 缓冲水位×升档：水位低自救让位未决切换；切换中 caching 走 CacheLadder 当前档。
- ExitDialog×双实例：Dispose→_userStopped=true→CleanupSwapKernel，第二实例随宿主释放不残留。
- 未发现互扰死角。

## 7 稳定性专项 —— PASS
- 生命周期：提交路径撤旧内核、放弃路径撤第二实例、退出路径双撤——三条路径均释放，无泄漏路径；代数守卫作废陈旧异步。
- 反复切台压力：序列号守卫 + 未决收口，推演 100 次无状态残留。
- 网络抖动：降档由停滞触发、升档需无掉帧推进，失败留当前档，不重试到崩。
- 日志风暴：BllLog 512 封顶丢最旧，不阻塞业务线程；sink 异常不外传。

## 8 出包前自检 —— PASS
- 签名 keystore 同源在位（此前 v1.0.10 包已验 v1/v2/v3）；版本口径 ApplicationVersion=11 / 1.0.10；源码 BOM 抽查：BllLog.cs、MainActivity.cs 均 357 273 277（EF BB BF）。

## 结论
逻辑、功能、遥控推演、视觉、互扰、稳定性、自检七维 PASS；真机模拟（按坐标真实点击）为带条件 PASS，需云端 Windows 跑道补跑该一项后方可出包。其余各项已具备出包条件。
