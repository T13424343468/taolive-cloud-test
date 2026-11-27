# TaoLive 2.0 版本台账（版本对账令 · 交差必核账）

## 当前版本锚
- **当前版本：vc90 / 1.5（2026-11-26 九件套落码交付，分支 nine-piece-20261126）**
- 三处对账：csproj 90/1.5 = AndroidManifest 90/1.5 = badging versionCode 90 / versionName 1.5 ✓

## vc90 交付记录（九件套定稿口径，老板 2026-11-25 令）
1. 九件套①激活补库存档：BufferPolicy.Default 改 SilentRefetch（先开画后补库存；StartFull 保留回退档）
2. 九件套②主刀·时间平移快启：HLS 起播定位取窗口最老已播画面（TargetOffsetMs=TimeShiftStartMs 24h 内核自钳），1× 恒速不追实时；原播放落后旋钮起播定位语义被取代（仅保留库存触发线联动）
3. 九件套③刀二·首帧超时兜底 12 秒收 8 秒（PrefetchCurve.OpenTimeout），流时钟查活续期一起动（3 秒内有增长放宽慢灌窗 21 秒）
4. 九件套④刀一·选源竞速与取流两次串行网络合成一次：TicketFusedProber（Web 端点=换票即探通，票落进程级 5 分钟缓存；直连端点照旧握手赛马）
5. 九件套⑤刀三·预取圈扩到最近看过的台：PrefetchRecentAsync 直接消费 WatchTimeLedger.Snapshot 台账；壳层接线进程级观看时长账本（出画起算、换台/失败/退壳封账落盘）
6. 九件套⑥刀四·冷启动同口径：boot-warm-last 上次频道开机即预热（DNS 预解析+取票预取，2 秒级延后让首帧先落；boot-warm-all 全表备货保留）
7. 九件套⑦刀六·热备校验流升级秒切备胎：StealthPipeline 校验通过（首次成功拉片）且池满后保温心跳（每 KeepWarmIntervalMs=5s 续拉保鲜，断供判据时钟随心跳刷新；心跳同吃让路闸；Retarget 清旗重新验源）

## 统一验证（vc90，2026-11-26）
- 全测：250/250 全绿 0 失败（dotnet10.0.404，Tests net10.0；vc89 基线 242 + 刀一 2 + 刀三 3 + 刀六 3）
- 壳层编译：dotnet build src/TaoLive.Tv 0 错 0 警（AndroidSdkDirectory + JavaSdkDirectory 口径）
- 遗留待验（真机）：主刀口径（窗口最老画面起播+恒速不追实时）、8 秒兜底、合成探针、最近看台预取、冷启动预热、保温心跳秒切，均待真机验收

## vc89 交付记录（换台平稳四招，老板拍板三招+首帧 7 秒）
1. 首帧门槛 3s→7s（PrefetchCurve.FirstFrameGate）
2. 慢灌窗倍率 ×7→×3（SlowStartWindow=21s 窗值不变）
3. 干库秒切：重灌期「从没攒满过」弱线 3 秒零进度快切备线（FastDrainWindow=3s，WatchdogWindowMs allowFastDrain）
4. 灌速探针+备线预热（WeakFillDetected 事件、阈值 0.25×、最短采样 2s；壳层 TicketResolver 后台焐热）+ 干库「正在换信号」Toast（招③）

## 统一验证（统一验证令，一次跑）
- 全测：242/242 全绿 0 失败（dotnet10.0.404，Tests net10.0）
- 壳层编译：dotnet build src/TaoLive.Tv 0 错 0 警（AndroidSdkDirectory + JdkDirectory 口径）
- 审核纠错：探针测试采样窗时序修正（起拍泵）、CS0266 显式收窄、SettingsUi 缺 using TaoLive.Model
- 云端构建：taolive-cloud-test build-apk run 44061063280 全绿（2m3s）
- 三道闸：v45 版本锚过 / v46 特征关过（label TaoLive、横幅随包、信息行禁词不在）/ v49 基线清单随包（7 文件区间 5c694e6..9c19a10）
- 包内文案特征（坑40）：「正在换信号」于 libassembly-store.so XALZ 解码后 TaoLive.Session.dll UTF-16 串池命中；频道目录在包（m3u8×82 / CCTV / cctv / 卫视）

## 待真机验证清单（待验销项令）
1. 换台放几秒没库存→应 3 秒内切备线（原黑十来秒病根）；日志关键字「[看门狗] 卡流超时」间隔≈3s
2. 弱线起播期日志「[探针] 灌速 …×实时」+「[预热] …备线票焐热完成」
3. 干库瞬间画面出「正在换信号」提示条（不再纯黑）
4. 首帧约 7 秒出画（换台遮罩深底，无旧帧感知）；画面出后不再几秒即黑
5. 回归：正常好线换台/换源行为不变（好线走原 24s 重灌窗，日志可辨）

## 断点（断点接力令）
- 交付完成即断点：本版无未落码事项；下一步=等真机验收反馈，红即修走下一版（1.5/vc90）
- 发布通道 publish_file 仍被平台坏记录卡死（execution_runtime 字段缺失，575558 同族）；恢复信号=对任意新文件重试成功；本版走阿里云盘通道交付

## vc91 / 1.6（2026-11-26 覆盖冲刺收口）
- 版本锚：csproj 91/1.6 = manifest 91/1.6 = badging 91/1.6（三处对账一致，亲验）
- 全测：360/360 全绿 0 失败；行覆盖 91.10%、分支覆盖 78.92%（基线 84.20%/70.39%）
- 随版修复：健康线水位项符号反了（闸门反开）、掉速 EMA 首拍哨兵、微卡窗口零哨兵；测试侧车道护栏
- 交付包：TaoLive-1.6-vc91.zip（SHA-256 b70a0aeef930ebbf6020f49900c6ab99fe82511b21a0971930a6f1d9906a6ca0）

## vc92 / 1.7（2026-11-26 订阅扩编收口）
- 版本锚：csproj 92/1.7 = manifest 92/1.7 = badging 92/1.7（三处对账一致，亲验）
- 全测：372/372 全绿 0 失败（基线 367 + 新增 5）；0 错 0 警
- 内容：订阅源扩编 suxuang/myIPTV（942 台，补 16/18 缺口）；目录外主流台新收录并入「自有源」页签（限额 40 台×2 路）；CCTV 基础名别名归位；归一扩空格+横杠口径
- 出包三道闸：v45 ✓ v46 ✓ v49（a40d889→c7ef13b）✓
- 交付包：.verify/pkg92/TaoLive-1.7-vc92.zip（SHA-256 10d70dfaa89b8b74217caea3afc918615cda2d0a6a0252cd6fc78ef32af40c21）
- 云测：build-apk 45782386520 / emulator 45782392592 / cloud-real-device 45782397589（2026-11-26 16:20 发起）
- 更正（2026-11-26 16:44）：首发包误用 Debug 测试档（10.7MB 无预编译），已重出 Release 档（APK 22,169,067 字节，SHA-256 6ab58825…8a6e，与 vc91 同口径）；三闸重过，交付包 artifact v4

## vc114 / 1.0.4（2026-11-28 手机版原型对照整改收口）
- 版本锚：csproj 114/1.0.4 = manifest 114/1.0.4 = badging 114/1.0.4（三处对账一致，亲验；manifest 补齐 vc113 欠账两版）
- 内容：v31 原型逐屏亲看对照整改——控制条竖屏三行/横屏一行横排+mini「1080·25fps」读数（VideoSize.Height+Format.FrameRate，0=未知哨兵）、空态橙框横幅+「第X/6次」+可点链接、列表页头「频道列表」+顺序重排+空态三分支、设置页分组卡片+真实 Switch 控件（SwitchView 自绘）
- 全测：406/406 全绿 0 失败；Debug 编译 0 错 0 警
- 交付包：build/vc114/TaoLive.Phone-1.0.4-vc114-signed.apk（Release 档 22,757,172 字节，SHA-256 2da51376b91b7a541a3642dbe657d1a183b24057f9fd0fc2ee848ca4261a069b）+ 同名 zip 双件

## vc115 / 1.0.5（2026-11-28 手机版原型布局定稿令）
- 版本锚：csproj 115/1.0.5 = manifest 115/1.0.5 = badging 115/1.0.5（三处对账一致，亲验）
- 内容（老板三次澄清定稿：透明化保留最大化、要原型布局设计感、帧率不显示）：
  1. A1 空态横幅与控制条互斥：失败横幅可见时控制条不出（ShowControlBar/ToggleControlBar 守卫），横幅出场先收控制条（ShowEmptyState→HideControlBar）
  2. A2 档位弹层每个选项行自带托底胶囊（原型 .opt 同款：未选中墨 36 底+纸 64 细边，选中保持强化样式），圆角 Dp(10)
  3. C2 竖屏节目名拆段：节目名占余宽省略+「· 画面时刻：HH:mm:ss」完整；「实时水位：X秒」完整+接下来占余宽省略；横屏四段横排段间 Px(10)
  4. C3 横屏分段间距 Px(10)，不再粘连；mini 与节目名间 Px(10)
  5. D1 列表行台名块补 weight=1（修 vc113 宽 0 不可见）
  6. D2 当前台高亮改 deep #3D6E9E 描边（原型 .chan-row.current），去棕色 Accent 填充
  7. 设置页删帮助行「点按切换 · 点 ← 或返回键收起」
  8. mini 只显分辨率（老板令 2026-11-28「帧率可以不显示」，撤 vc114 帧率读数）
- 统一验证：Debug 编译 0 错 0 警；全测 406/406 全绿 0 失败；Release -m:1 编译 0 错 0 警；badging 亲验 115/1.0.5
- 交付包：build/vc115/TaoLive.Phone-1.0.5-vc115-signed.apk（Release 档 22,761,268 字节，SHA-256 eb0af7c2c539a1eccb95e7bb3fa86df6b5cd6fbc143d8c8563fbcd1749a5a146）+ 同名 zip

## vc116 / 1.0.6（2026-11-28 老板口述 25 条令落码）
- 版本锚：csproj 116/1.0.6 = manifest 116/1.0.6 = badging 116/1.0.6（三处对账一致，亲验）
- 内容（老板口述 25 条令全部落码，归并 16 个落码项，逐条对照见会话档案 vc116-老板口述修改令.md）：
  ①亮度指示条落位修复（BuildHud 不再传新 lp 覆盖 PositionHud）②控制条六钮+页头 ⟳✕ 改圆角方形线框皮（全透明填充，默认 45% 白描边/按压 95%）③控制条横竖屏统一三行信息分块（行1 台名+mini/行2 节目名+画面时刻/行3 水位+接下来，读数段完整不截断）④相邻按钮统一 8dp 间距⑤去冗余提示弹条五处（方向锁×2/翻转钮/横屏边缘手势/系统返回）⑥锁图标重画（解锁态锁梁上提、右腿短悬空不进体，消灭「0」散块；锁体放大 56%×42%）⑦列表统一 16dp 靠边（页头顶 20、行内距 48→8、行高 20→12、ListView 8dp 内距+SetClipToPadding(false) 防描边裁切）⑧页头 ⟳✕ 定尺寸 40×40 线框钮⑨筛选框全透明+圆角线框⑩横屏列表改左侧贴边⑪空态横幅全透明填充保留橙描边⑫提示胶囊最多两行自适应+全透明胶囊描边（PillBackground）⑬五个子页面统一顶部安全区避让（SafeTopInset）⑭HandleSystemBack 去提示⑮设置分组卡片全透明+描边线框⑯进阶调优档位弹层隔断（底 216+描边+圆角 14dp+margin 12dp、选项行 6dp 间距）
- 挂起观察：列表抽屉半透明视频透出（待老板后续口述）
- 统一验证：Debug 编译 0 错 0 警；全测 406/406 全绿 0 失败；Release -m:1 出包（清 obj/Release 防增量）；badging 亲验 116/1.0.6
- 交付包：build/vc116/TaoLive-1.0.6-vc116.apk（22,761,268 字节）+ TaoLive手机版-1.0.6-vc116完整交付包.zip
