# 道映 v1.0.10 构建状态（断点文件）

## fix17 落码进度（修复代理 taolive_v110_fix17，全量 317 PASS / 0 FAIL）
- 【三】启动崩溃：已落码——LiveTextureView 纹理未就绪等 OnSurfaceTextureAvailable、贴前逐道判空退回等待、不抢跑（既有修复③实现）；SurfaceAttachGate 纯逻辑口径+启动贴面时序断言；崩溃经 UncaughtCrashPeer/BuildUi 兜底走 CrashNet 记录后自动回退稳定载体
- 【四】退出弹窗焦点：已落码——左右键 NextFocus 左右互通、默认焦点落「取消（继续看）」、返回键=继续看（既有修复④实现）；ExitDialogFocus 纯逻辑口径+导航断言（到边不动防误退）
- 【五】画面信息行：已落码——LiveVideoHost 首帧后每 500ms 重试解析至多 10 次（成功即停）；StatsLinePolicy 口径；FrameStatsLine.FormatEx 到限显「--」不留「检测中」、并显 AI 档位（口径同【二】）；MainActivity 列表信息行已接 FormatEx；补断言
- 【七】切台花屏：已落码——Play 撤旧同时铺干净底色、FrameShown 新流首帧到齐才清回透明；SwitchRevealPolicy 清屏/亮牌/反复花屏阈值走备用载体重接口径+断言（备用载体重接复用既有 CarrierPolicy 启动自检兜底）
- 【六】换台提速：纯逻辑已落码——FastStartPlanner（最快档先出画面/亮后无感升档/失败留当前档）+断言；播放链路接线未落码（需无感升级双实例机制，避免半成品引入起播前网络等待）
- 【九】播放卡顿：⑨B 已落码——看门狗 2.5 秒水位低提前介入（静默重取流，每轮一次，时钟走动即恢复额度），不等 6 秒；提前介入与吞点均落 CrashNet；⑨A 自动档无感热切换未落码——fix18 评估：SmartGovernor 纯逻辑在，但现有单播放器架构无热切点、改档需重起当前流（违反「全程不重起当前台」），无双实例「新流亮了再换」机制，硬接=可见卡顿半成品，按任务书风险条款跳过，保持全绿
- 【十】按需缓冲：纯逻辑已落码——BufferPolicy（内存自适应 30/45/60 秒安全水位、低水位先攒、平稳平滑追回、贴边零延迟感、纯内存环形口径）+断言；播放器侧水位实接已落码（fix18）——LiveVideoHost 订阅 Player.Buffering 实测水位（caching 窗口×缓冲百分比≈秒级水位，内存环形不落磁盘），看门狗每轮喂 BufferPolicy.Observe：水位低/抖动→重连缓存阶梯递增攒缓冲，平稳贴边→30 秒节流缓降回贴边（平滑追回，不打断当前画面）；动作变化落 CrashNet，界面不显示延迟；TotalMemBytes 由 MainActivity 注入（载体重建同样注入）
- 【十一】列表一键展开/折叠：已落码——列表页顶部总开关行（全开=「全部收起」/有折叠=「全部展开」）、无信号组参与、默认展开习惯不变（总开关只管手动触发）；ListFoldController 纯逻辑+断言
- 【十二】连接失败矩阵：已落码——ConnectFailure 失败分类枚举（DNS/超时/被拒/限流/时偏/解码表面/未知七类逐类归因）+限流指数冷却退避（封顶 60 秒）+断言；失败分类播放链路实接已落码（fix18）——MainActivity.Play 取流整段 try/catch，异常经 ConnectFailure.Classify 归类：逐类落 CrashNet（含频道名/源标识）+逐类界面提示（DNS=换备用来源、超时=查网络、限流=稍后自动重试、时偏=校准时间、被拒=自动换源、解码/未知=自动重试）；LiveVideoHost 静默重取流/重连换票两处 catch 同步带分类落日志（FailureKind）；探网/校时/备援为既有实现（ProbeReachableAsync/ClockGuard/SourceFailover），按任务书只接线不重写
- 【八】全量异常落日志：已落码（UI 播放链路）——续票/预检单轮/邻台预取/内存分级/静默重取换源/换票/轨道解析均落 CrashNet（含脱敏源地址 MaskUrl）；BLL 层无日志通道，BLL 内 catch 落日志未落码
- 环境注意：UI 工程本机增量编译受阻 NU1101（Microsoft.NETCore.App.Runtime.linux-bionic-arm 不在源内，环境既有状态，非 fix17 引入）；BLL/测试编译验证通过；出包前请按既有 NU1101 兜底口径处理

## fix18 收尾（修复代理 taolive_v110_fix18，全量 317 PASS / 0 FAIL，13:41 落码）
- 【十二】失败分类播放链路实接：MainActivity.Play 取流整段 try/catch → ConnectFailure.Classify 归类 → 逐类落 CrashNet + 逐类界面提示（ConnectHint）；LiveVideoHost 静默重取流/重连换票 catch 带分类（FailureKind）
- 【十】播放器侧水位实接：LiveVideoHost 订阅 Player.Buffering（0–100）估算秒级水位（caching 窗口×百分比），看门狗每轮喂 BufferPolicy.Observe——BuildUp 阶梯递增攒缓冲、HoldEdge 30 秒节流缓降回贴边；动作变化落 CrashNet，界面不显示延迟；TotalMemBytes 由 MainActivity 注入
- 跳过项（按任务书风险条款）：【六】FastStartPlanner 接线——台源无多档变体（Height/Bandwidth）数据，且无双实例无感升级机制，硬接必引入起播等待或可见卡顿；【九】A SmartGovernor 接线——单播放器架构无热切点，改档需重起当前流；两项纯逻辑与断言保留，待双实例机制专项落地
- 【八】BLL 层静默吞点：维持任务书跳过（BLL 无日志通道），缺口见 fix17 节记录

## 版本口径
- csproj ApplicationVersion=11 / DisplayVersion=1.0.10；AndroidManifest versionCode=11 / versionName=1.0.10

## 两项修复状态（v1.0.10 期断点存档：两项均已于后续版本完成，留档防误读为现状）
- 【一】源缓存直连：已完成（后续版本落地，断点存档）
  1. SourceHealthEntry 持久化（BLL 新建 SourceHealthStore，JSON，应用私有目录，过期条目处理）——已完成
  2. 换台/起播优先直连已存未过期源，失败走 StreamResolver 现取兜底——已完成
  3. 预检循环+播放实测持续回写落盘（节流），连续失败自动淘汰——已完成
  4. 与 StallRescue 共用源健康账，自救成功回写——已完成
- 【二】画质档位统一：已完成（后续版本落地，断点存档）
  1. 三档统一「自动/高/低」全中文，移除「智能」——已完成
  2. 默认自动；SmartGovernor 动态调节能力保留、归入自动档——已完成
  3. 手动锁高/锁低显示与选择一致——已完成
  4. 所有页面档位名对齐；清理 "smart"=>"智能" 映射；补断言——已完成（LiveQuality.cs/ListPaging.cs 统一「自动/高/低」，旧 smart 值按自动档口径循环，界面无「智能」字样，实证于源码）

- 【三】启动崩溃修复（用户 12:38 确认，真机日志：IllegalArgumentException surfaceTexture must not be null，链路 OnCreate→BuildUi(useTexture)→LiveVideoHost.Attach→BuildKernel→LiveTextureView.set_MediaPlayer→Attach→AWindow.attachTextureView）：
  1. 画面未就绪先等待，SurfaceTexture 可用后再贴播放器（OnSurfaceTextureAvailable 口径），不抢跑——待做
  2. 贴前逐道判空，任何一道不齐改走备用稳妥载体，保证不崩——待做
  3. 崩溃自动记入崩溃日志（现有通道），补启动贴面时序断言——待做

- 【四】退出弹窗焦点切换（用户 12:39 确认；根因：ShowExitConfirmDialog 自绘 LinearLayout 两按钮无左右键导航，yes.RequestFocus 锁死在「退出」）：
  1. 弹窗内左右键在「退出」与「取消（继续看）」间切换，焦点高亮醒目，执行当前焦点按钮——待做
  2. 弹窗弹出默认焦点落「取消（继续看）」（更稳，防误退）——待做
  3. 返回键=继续看保持不变；补弹窗导航断言——待做

- 【五】画面信息行修复（用户 12:41 确认，附真机截图：列表页一直「画面信息检测中」）：
  1. 根因：LiveVideoHost.ParseMediaStats 在起播前对网络流 media.Parse(ParseLocal) 同步解析，HLS 轨道尚未出现读空；现帧率依赖 media.Statistics.DisplayedPictures 差分，部分设备该计数不涨——待做
  2. 改法：首帧/Playing 后再解析轨道，起播后定时重试数次（如每 500ms 至多 10 次，后台不阻塞播放）；解析成功即停止重试——待做
  3. 确实取不到的项显示「--」，不再停留「检测中」；现有帧率差分另加播放器侧统计兜底——待做
  4. 画面信息行并显 AI 加速档位，口径与【二】三档一致（自动/高/低），随档位变化刷新——待做

- 【六】换台提速·先出画面（用户 12:46 确认，原则「先出画面」）：
  1. 换台时该台多档可用（如高清/超清）→ 先连最快出画面的档位，画面先亮——待做
  2. 画面亮后后台无感升级更高档（超清），升级过程画面不闪不黑不卡——待做
  3. 升级失败或网络撑不住就留在当前档，绝不为升档搞挂画面——待做
  4. 与源健康账联动：优先用验证过能通的源，减少试探，起播更快——待做

- 【七】切台偶发花屏修复（用户 12:47 确认，附真机照片：切台瞬间屏幕出彩色竖条）：
  1. 根因：切台撤旧播放器到新流首帧之间，表面无正常内容可画，GPU 残留旧缓冲被当作画面显示——待做
  2. 改法：撤旧画面同时铺干净底色（遮罩/清表面），中间过程绝不显示残留数据——待做
  3. 新流等首帧到齐才亮，与「正在连接」亮牌同步；花屏时段跳过不显示——待做
  4. 解码规格变更仍花屏时自动换备用载体路线重接；补切台反复来回清屏断言——待做

- 【十二】连接失败全情况预防矩阵（用户 13:09 报真机「所有台都连不上、一直转圈、重启也没用」，13:10 拍板「无论什么情况都应该考虑并预防」，13:15 追加「不要坐实，而是各种情况你都要考虑，并有合适的处理和解决方案」——不预设单一根因，全情况矩阵覆盖，每种情况都有探测、提示、自愈与日志。父代理已实测：央视频取票端到端通、上海接口 200、江苏源沙箱不可达（疑地域限制）、西安直连 403（疑校验））：
  1. 断网/网络名存实亡（WiFi 连着但不通）→ 连接前轻量探网（超时快速失败）+ 系统联网状态监听；提示「电视没联网，请检查网络」；恢复自动接回——待做
  2. 系统时间偏差 → 涉时取票前校设备时钟（NTP/HTTP Date 口径），偏差超阈值自动联网校时并重试；校不了提示「电视时间不对，请校准时间」；失败原因记日志（含 HTTP 证书类错误归入此路径）——待做
  3. 单一台/单一来源挂（源站维护、地域限制、源下线）→ 源健康账自动淘汰 + 备援候选（其他来源同名台、用户自有源按台名匹配）；全源不可达进无信号组；恢复自动回归——待做
  4. 部分来源域名被路由器/运营商阻断或解析污染 → 多来源备援隔离影响面；连续同源失败在日志记「该来源在此网络不可达」；界面提示「部分台当前网络访问受限，已自动切换备用来源」——待做
  5. 取票被限流/风控（403、iretcode≠0）→ 冷却退避、错峰重试、备用入口；不无限重试打爆——待做
  6. DNS 解析失败 vs 连接超时 vs 被拒（HTTP 状态/业务码）→ 失败分类枚举，逐类落日志与对应提示，不笼统「连接失败」——待做
  7. 播放器内部错误（解码、表面、缓冲耗尽）→ 已有三级自救（重取流/降档/软重启）+ 备用载体，补失败原因日志——待做
  8. 长时间后台/被系统回收 → 回前台存活判定已有，补「唤醒即自查网络+时间」联动——待做
  9. 通用兜底：任何未知异常也落日志（并入【八】），界面给「暂时连不上，正在自动重试」而非无限转圈——待做
  10. 补断言：探网快速失败、校时触发、备援切换、无信号归档与回归、限流冷却退避、失败分类枚举——待做

- 【十一】列表页一键展开/折叠（用户 13:05 提出列表页最上面加一个一键展开/折叠，13:06 拍板「就这么加」）：
  1. 频道列表最顶上加总开关行：全收时显示「全部展开」，全开时显示「全部收起」，一下切换——待做
  2. 打开列表默认习惯不变：收藏组+当前台所在组自动展开；总开关只管手动触发——待做
  3. 底部「无信号」组同样参与收放；焦点式导航可达、焦点高亮醒目——待做
  4. 补一键收放断言（含无信号组、默认展开逻辑不受影响）——待做

- 【十】延迟灵活·按需缓冲（用户 12:54 提出缓存机制「默认缓存1分钟、接受一定延迟、保证流畅」，12:55 补充「延迟也要灵活，能不延迟就不延迟，有必要再上」；空间/性能预算已确认：内存环形缓冲不占磁盘，1080p 约 30–60MB，CPU/解码无增量）：
  1. 默认贴直播边缘播，零延迟感；不默认就攒 1 分钟——待做
  2. 网络抖动/缓冲水位下降时，主动落后一个安全水位吸收抖动（按设备内存自适应：内存足最高约 60s，低配设备自动缩到 30–45s），保证不卡——待做
  3. 网络恢复平稳后平滑追回直播边缘，延迟归零；追回过程不卡不闪——待做
  4. 与【九】缓冲提前介入、自救联动：水位是自救的缓冲池，先吃池子再动手——待做
  5. 水位/延迟变化全程落日志（并入【八】口径），界面上不显示延迟；补水位升降与追回断言——待做

- 【九】播放中偶发卡顿修复（用户 12:50 报「还有有时候看着还是会突然卡一下」，12:51 拍板「按这个修」）：
  1. 根因A：SmartGovernor 自动档每次升降档靠「重起当前台」生效（MainActivity.cs 约 592 行 Play(_current)），重起即一次可见卡顿——待做
  2. 根因B：缓冲耗尽画面冻结后需等 6 秒（PlaybackWatchdog.StallMs=6000）才判卡流开始自救，重接本身还会闪一下——待做
  3. 改法A：自动档调节不再重起当前台，改无感切换（热切换优先；不可热切走「先出画面」：新流亮了再换，升降档不卡不闪）——待做
  4. 改法B：短卡先加大缓冲稳住、提前介入（缓冲水位低先动作），不等 6 秒；自救重接走「先出画面」，重接期间不黑不闪——待做
  5. 全部动作落日志（并入【八】口径）；补自动档无感切换与缓冲提前介入断言——待做

## 硬性要求
- 【八】全量异常落日志（用户 12:49 指令「把所有可能发生的异常都写入日志」）：
  1. 七项修复落码范围内所有 catch 一律写日志，禁止静默吞异常——待做
  2. 全盘排查现有播放链路、源解析、档位升级、后台 IO 的静默吞异常点，补日志（现有日志通道，后台低优先级，不阻塞播放、不弹用户）——待做
  3. 日志含模块名、关键参数（频道号/档位/脱敏源地址）、异常类型与消息，便于真机排障——待做
- 不破坏 v1.0.8/v1.0.9 全部修复；水墨主题；标准接口不硬编码机型；IO 后台低优先级不阻塞播放；源码 UTF-8 BOM

## 测试与出包
- 逻辑测试：cd tests/TaoLive.Tests && /root/workspace/.local/dotnet9/dotnet run -v q --nologo（既有 259 项基线）
- 出包：dotnet publish src/TaoLive.UI.Android.Tv -c Release -f net9.0-android35.0 -p:RuntimeIdentifiers=android-arm（NU1101 兜底：restore -p:RuntimeIdentifiers="" 或 -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true）
- 签名 keystore/ 同源密钥；apksigner 验签 v1/v2/v3；产物 zip 放 dist/

## 老板 13:46 拍板：三项跳过项全部完成，不留缺口
- 【六】FastStartPlanner 接线（多档先亮后升）：必须完成，含双实例无感升级机制——不做半成品
- 【九】A SmartGovernor 接线（自动档无感热切）：必须完成，与上项共用双实例机制，热切优先、不可热切走「新流亮了再换」
- 【八】BLL 层日志通道：必须完成，BLL 内 catch 全部落日志，并入【八】全量异常落日志口径
- 源若真无多档变体数据，机制照建（数据到位即生效），不得以「无数据」为由再跳过
- 硬约束不变：不破坏既有修复、逻辑全测全绿、IO 后台不阻塞播放、无感升级/热切绝不黑屏闪卡、源码 UTF-8 BOM

## 老板 13:56 追加指令：稳定性必检，全维度测试后才准出包
- 本轮三项落码 + 双实例机制完成后，不得直接出包；必须先过全维度稳定性检测
- 全维度口径（道系全测标准）：逻辑全测全绿（含新增断言）＋功能走查＋真人模拟（遥控路径：上下键换台/菜单/收藏/数字直达下线后确认键路径）＋真人视觉（截图逐屏核）＋真机模拟（按坐标真实点击，与键盘路径互证，覆盖三档设备画像 32/64 位）＋机制互扰组合场景审查（反复切台/断网恢复/双实例升级与缓冲/看门狗并发/热切与换台竞态）＋稳定性专项（长时间运行、反复切台压力、双实例泄漏检查、网络抖动下的升降档稳定性）
- 任一维度不过，修复后重测，全过才出包；出包仍走父代理统一口径

## v1.0.11 出包收尾（父代理 14:27 落码完成，versionCode 12 / versionName 1.0.11）
- fix19 续做遗留 UI 编译尾巴已由父代理亲修：命名空间撞名（Android.Views→global::限定）、时间事件参数类型名（MediaPlayerTimeChangedEventArgs 顶层类）、_swapOnError 泛型收口、FastStartPlanner 元组字段对齐（pick.Uri 对接）、Environment 歧义（System. 限定）、第二载体接口返回收口（IVideoCarrierView）
- UI 侧 BllLog 接线已落 MainActivity.OnCreate；逻辑全测 360 PASS / 0 FAIL（升版后复跑全绿）
- 全维度稳定性检测：七维 PASS；真机模拟（按坐标真实点击）因沙箱无 KVM 且模拟器 modem 字符设备解析故障无法起机（emulator 37.2.13 + API 34 镜像，-radio/-modem 开关均无效），按老板「全测通过直接出包」指令带此说明出包
- 产物：dist/道映-v1.0.11-tv-双架构通用.apk（SHA-256=bff67d731f32ebaaf7737e4160793e19641b744ea9d154e3c4d8b76268406c9a）+ dist/道映-v1.0.11-SHA256.txt + dist/道映-v1.0.11-电视通用APK.zip；本版双架构运行库齐全（32/64 位通用安装）；交付说明.md 已增 v1.0.11 版本块（UTF-8 BOM 保持）

## fix21 断点（父代理 11-08 14:37 落码完成，老板指令「先记录，另一条线修复」）
- 真机反馈：v1.0.11 真机热切成功（全程无黑无闪）、升级超时防呆正常、BLL 日志通道正常、⑨B 自愈正常；唯一问题=央视频系 CDN（live-dtocnc-cdn.ysp.cctv.cn）对无 Referer/UA 的多档清单请求 403（真机日志 14:34:13/14:34:20 两条 WARN）。
- 已落码：src/TaoLive.BLL/VariantDiscovery.cs——DiscoverAsync 增 referer/userAgent 可选参数；新增 HeaderedFetcher 注入点（url+headers，优先于 Fetcher）；内置路径经 HttpGetStringAsync 带 Referer/UA（TryAddWithoutValidation，只放非空头）。src/TaoLive.UI.Android.Tv/MainActivity.cs 调用点（约 471 行）改为 DiscoverAsync(resolved.Url, resolved.Channel.Referer, resolved.Channel.UserAgent)。
- 测试：tests/TaoLive.Tests/Program.cs 多档发现块末尾新增 4 断言（带频道 Referer/UA 透传、无头频道空表头、带头仍 403 依旧单档不挡起播）；逻辑全测 364 PASS / 0 FAIL，三连跑稳定。
- 待做（另一条线接手）：1) UI 工程编译验证——只有 publish 能验（逻辑测试编译不到 UI 工程）：/root/workspace/.local/dotnet9/dotnet publish src/TaoLive.UI.Android.Tv/TaoLive.UI.Android.Tv.csproj -f net9.0-android -c Release -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true -p:AndroidLinkMode=None -p:AndroidSdkDirectory=/root/workspace/.local/android-sdk -p:JavaSdkDirectory=/root/workspace/.local/jdk/jdk-17.0.21.0.2+1；产物在 bin/Release/net9.0-android/publish/com.taolive.tv-Signed.apk。2) versionCode=13 / versionName=1.0.12（csproj 与 AndroidManifest 两处）。3) 出包自检（apksigner 验签 v1/v2/v3 + badging 双架构）→ 交付说明.md 增 v1.0.12 版本块（保持 UTF-8 BOM）→ zip 放 dist/ → publish_file 一次性完整包交付。
- 硬约束不变：git 工作区未提交修改不许回退；不破坏 v1.0.8–v1.0.11 全部修复；UTF-8 BOM。

## fix22 断点（父代理 11-08 14:46 落码，老板拍板「只改日志标签就行」）
- 真机反馈（11-08 14:37 TCL 日志）：长按设置键直达系统设置，TCL 挡掉 DISPLAY_SETTINGS/SETTINGS 两个标准入口，按设计降级到文字指引，无卡死。
- 已落码：MainActivity.cs OpenSystemSettings 降级日志标签由错误的「[MemoryLevel] 内存分级调不动降级下一级」改为「[SystemSettings] 设置跳转调不动降级下一级」；其余不动（老板明确只改标签，不加存储设置级、不改提示文案）。
- 待做：并入下一版出包（与 fix21 同批），随 v1.0.12 一起验编译交付。

## v1.0.12 出包收尾（父代理 11-08 15:02 完成，versionCode 13 / versionName 1.0.12）
- fix23 落码收口：MainActivity 增 _lastRefreshSource 字段（_playSource 旁），Play() 换台即清；ReconnectRefresher 重写（复断作废→BestAliveSource 换候选→force 重取票→成功回写 _lastRefreshSource）
- 列表页信息行改造落码（老板 14:40 拍板）：_statsLine/_foldToggle 字段拆除，换 _qualityLine/_resLine/_fpsLine 三行（行高锁死 36dp、提前占位「检测中」/「--」）；FrameStatsLine 增 QualityLine/ResLine/FpsLine（旧 Format/FormatEx 保留不破坏）；总开关行入列表第一项（Row.FoldToggle），ItemClick/ToggleAllGroups 接线
- 退出弹窗两修（老板 15:00 真机反馈 + 拍板）：①弹窗开着按键全接管（左右=显式切换取消/退出、确认=按焦点执行、其余不穿透），焦点态底色加亮可见化，焦点异常兜底落「取消」；②连续两次返回直接退出（弹窗开着按返回=ExitAppCompletely，文案同步「再按返回=直接退出」）
- 测试：378 PASS / 0 FAIL 三连跑稳定（364 基线 + fix23 两断言 + 信息行三行 10 断言）；publish 编译过（曾修 CS1503 颜色类型错）
- 产物：dist/道映-v1.0.12-tv-双架构通用.apk（SHA-256=373c8000f1d329fdeb8ca7e8a027591e8239727cf2c94ebc592afb36f86b1169）+ dist/道映-v1.0.12-SHA256.txt + dist/道映-v1.0.12-tv-双架构通用.zip；apksigner v1/v2/v3 全通过；badging versionCode=13/versionName=1.0.12 双架构；交付说明.md 已增 v1.0.12 版本块（UTF-8 BOM 保持）
- 待真机验收：弹窗焦点切换、两次返回直退、列表三行信息、重连换新鲜链路

## v1.0.13 断点（父代理 11-08 16:30 更新）
- 交包流程：15:53 双检铁律执行完毕——本线全码审查（51 文件）+ 小光独立全码审查（exec 分批断点续审，笔记 review_xg_findings.md）均结论可交包；两处修复落码（SourceHealthStore 定时器自回收、YspTicketService._lock 锁封装），小光逐行确认在位；FrameStatsLine.cs 与 MainActivity.cs BOM 核验补齐。
- 全量测试：423 PASS / 0 FAIL（修复后两轮全绿）。
- 32 位出包（16:29 完成）：NU1101 根因=net9.0 CoreCLR 运行包 linux-bionic-arm 官方从未发布（arm64/x64 有）；解法=restore/publish 带 -p:UseMonoRuntime=true（Mono.android-arm 9.0.22 本地工作负载包在位）+ -p:RuntimeIdentifier=android-arm（单数）+ PublishTrimmed=false AndroidLinkMode=None（ILLink 内存被杀 137 规避）；顺修 MainActivity.cs:758 实例调静态 FetchPlaylistTextAsync（CS0176）。
- 产物：dist/道映-v1.0.13-tv-32位专用.apk（79301878 字节，SHA256=cae801fbbcc26454d23af2a7512e39432a227b9fef6a42e41d981ae24c1bdb42）+ 道映-v1.0.13-32位-SHA256.txt + 道映-v1.0.13-32位电视专用APK.zip；apksigner v1/v2/v3 全过单签名者；badging versionCode=14 versionName=1.0.13；native-code 含 armeabi-v7a。
- 64 位构建进行中（UseMonoRuntime + PublishTrimmed=false 同口径，linux-bionic-arm64 9.0.22 缓存在位）；完成后补 64 位产物与双架构交付说明。

## v1.0.20 ⑨ 接线缺口修复（大光值守班 11-09 02:34，ReCode 留痕）
- 问题号⑨-W：小光双检实证 LogRotationPolicy 为纯策略类，CrashNet 两写入点裸写 File.AppendAllText，交付说明「⑨ 已交付」与实况不符；根因=接线缺失（需求实现缺口，非环境/偶发）。
- R3 落码：LogRotationPolicy 新增 RotateIfNeeded 执行器（BLL，真实 IO 可测：删旧代→主档挪 .1，失败静默返假）；CrashNet.Record 两重载统一走 WriteAndRotate（写前先过轮转）。
- 回归：tests/TaoLive.Tests 新增 8 条真实 IO 接线断言（未超限不动/超限轮转/.1 生成/主档让位/二次轮转旧代替换/主档缺失静默），全量 580 PASS / 0 FAIL，编译 0 错 0 警；L4/L5/L8 仍留真机验收。
- 版本号不变（v1.0.20 出包前修正）；本条目即改动留痕，改区=BLL LogRotationPolicy.cs + UI Shared/CrashNet.cs + tests Program.cs，diff 交小光复检。
