# 道映 B 包（versionCode 20 / 1.0.11-r2 界面优化版）构建状态（断点文件）

## 任务口径
- 基线：v1.0.11（从 252c352 重放），不混 A 类六项（1/10/13/5/7/6）
- B 类八项：12 列表页抽屉+五标签+63 台标；14 节目单；9 崩溃日志窗；11 退出弹窗；4 画质行紧凑；8 键码静默；3 IP 限流提示；2 砍设置入口
- 硬边界：不动 /root/workspace/taolive-work/TaoLive 主工作树；worktree=/root/workspace/taolive-b20-wt
- 出包配方（32 位）：
  /root/workspace/.local/dotnet9/dotnet publish src/TaoLive.UI.Android.Tv/TaoLive.UI.Android.Tv.csproj -f net9.0-android -c Release -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true -p:AndroidLinkMode=None -p:AndroidSdkDirectory=/root/workspace/.local/android-sdk -p:JavaSdkDirectory=/root/workspace/.local/jdk/jdk-17.0.21.0.2+1 -p:UseMonoRuntime=true -p:RuntimeIdentifier=android-arm
- 测试：cd tests/TaoLive.Tests && /root/workspace/.local/dotnet9/dotnet run -v q --nologo

## 步骤账
- [x] worktree 开立（252c352 → /root/workspace/taolive-b20-wt）
- [x] 第一步·v1.0.11 状态重建：csproj/manifest 已升 20 / 1.0.11-r2（csproj 与 AndroidManifest 同步）；fix17-19 UI 收尾核对 build110_state.md——fix19 编译尾巴由当时父代理亲修，252c352 基线含 v1.0.11 全部代码口径；逻辑全测基线 317 PASS / 0 FAIL（17:24 实测）
- [ ] 第二步·B 类八项逐项落码（每项落码后在此记账）
- [ ] 第三步·全测全绿 → 32 位出包 → apksigner 验签 → SHA-256 指纹档 → zip
- [ ] 备份 worktree 到 /root/workspace/存档/TaoLive/<时间戳>/
- [ ] parent_result 汇报

## 八项落码账
（随做随记）

## 版本口径
- 目标：csproj ApplicationVersion=20 / DisplayVersion=1.0.11-r2；AndroidManifest versionCode=20 / versionName=1.0.11-r2

## 17:49 口径更新
- 老板终版拍板：双包「B / A+B」。本线恢复单独出包（v20 界面优化版，versionCode 20 不变）
- 硬边界新增：不碰 /root/workspace/taolive-a20-wt（A 类落码线并行，只落码不出包）
- 其余步骤不变：八项落码 → 全测全绿 → 32 位出包 → 验签 → 指纹 → 备份 → zip

## 19:15 v21 总装收口（A+B 全量包）
- versionCode 21 / versionName 1.0.11-r3；A 类 13 项并入 B 树，全测三连跑 375 PASS / 0 FAIL
- publish 后显式 apksigner 补签（keystore/debug.keystore，口令 android，alias androiddebugkey）：v1/v2/v3 全 true，证书 SHA-256 c18030d3…（与历史同源）
- 产物：dist/v21/道映-tv-v1.0.11-r3-32位.zip（内套 com.taolive.tv-Signed.apk + SHA256SUMS）
  - APK sha256: fae254875625b1883389842e57b4eca7b7ac4126cee21001e3951bb92dfd731a
  - zip sha256: 98f87a7e0a031be1bcd8b0d8cef54379e64aac3971bdb63d04803f1abb332803
- 备份：/root/workspace/存档/TaoLive/20261110-1915-AB-v1.0.11-r3（指纹复验一致）
- A 树注记：/root/workspace/taolive-a20-wt 的 MainActivity.cs 曾被脚本误清空（19:08），A 树不可单独出包，交付以本合并树为准；合并树曾于 19:12 同样误清空、已从 B 包存档恢复并重演全部 A 改动
- 事故教训：脚本收尾严禁裸 open(p,'w')；合并树已修 CS0104 歧义（System.IO.Path / System.Environment）

## 19:50 老板令：先记录、修（v22 一批修完，未动码先记账）
1. [ ] 残影：换台后底部节目单条叠上一台残字 + 卫视列表整层 ghost（无台标旧行/旧「全部展开·画质增强」未清）——抽屉与节目单重绘清旧帧
2. [ ] 限流提示接同一本退避账：文案「约 1 分钟后自动重试」写死 → 按退避账剩余时长动态给预期（口径三）
3. [ ] 开机自启接收器名实不符：BootReceiver 改名 WarmupBootReceiver + 补 Name 属性，清清单死条目
4. [ ] 退避打点：403 入账/恢复处补日志（真机可验收「退避期内不发起」）
5. [ ] 起播日志「滤镜」空时补「无」
6. [ ] LiveVideoHost「时钟 2.5 秒」注释改 4 秒（A⑨）
7. [ ] 自启开关旁补跳系统自启动管理页指引（细案 §二）
8. [ ] 口径记账不改码：A⑥「首轮推迟 3 分钟」实现为按源保鲜期（最早 10 分钟，严于文档），方向一致
9. [ ] 版本 22 / 1.0.11-r4；全测全绿 → publish → apksigner 补签 → 验签 → 指纹 → zip（内套 APK）→ 备份 → 发布

## 19:55 查因与方案（未动码，等老板发话）

### 1. 残影根因（静态查因结论）
- 代码侧排除：抽屉每次呼出 RemoveAllViews 后整层重装，无第二套列表控件；行渲染无异步台标竞态（ResourceName 同步查表）；底部节目单换台有作废保护（旧回丢弃、文本直接覆盖、无数据隐藏）。代码内不存在两套并存渲染。
- 指向：ghost 内容恰是「上一帧渲染结果」（ghost 行无台标=台标就绪前的形态；ghost 节目单=上一台的条），同帧旧内容只能存在于——①换台/内核重建时视频表面旧合成帧未清，半透明面板（水墨 PanelAlpha）透出；②显示端余辉（拍照时序）。ghost 是「另一种渲染」非同帧淡影，更指向 ①。
- 方案：
  a) 对照试验一锤定音：临时版面板底改不透明（一行），ghost 消失=表面残帧透出；仍在=余辉，app 无责。
  b) 确定性清场（无论结论都做）：换台/重取流/内核重建时显式清视频表面（TextureView 释放 SurfaceTexture/SurfaceView 清黑），首帧前遮罩维持；底部节目单换台先清空+隐藏再拉数；抽屉重开已有 RemoveAllViews，补重入防抖。

### 2. 其余修法
- 限流文案：改读同一本退避账——DrawerSupport.RateLimitHint(剩余分钟) 纯函数可测，≤0 显「即将自动重试」，否则「约 N 分钟后自动重试」；ConnectHint 去掉写死的一分钟。
- 接收器：BootReceiver 改名 WarmupBootReceiver + 补 [BroadcastReceiver(Name="com.taolive.tv.WarmupBootReceiver")]，死条目消除。
- 退避打点：RecordLimited 两处调用点补日志（源+退避至何时）；成功处补 Recover+解除打点。
- 空滤镜日志补「无」；「时钟 2.5 秒」注释改 4 秒；自启行长按跳应用详情页（失败退文字指引）。
- 口径不改码：A⑥ 首轮推迟实为按源保鲜期（最早 10 分钟，严于文档的 3 分钟），方向一致，记口径。
- 节目单条生命周期（20:0x 老板问）：进台成功才拉官方接口、拉到就亮、换台即刷新（seq 作废保护）、失败/映射外/无数据静默隐藏；显示后无自动收起计时器，常驻至下次换台刷新或退出——口径文档只定「按需拉取、自带节流」，未定自动收起。已拍板：补「亮出 7 秒无操作自动收起」（v22 落码，随批修）。
- 版本 22 / 1.0.11-r4；全测补断言（限流文案/退避打点/接收器名）→ 全测全绿 → publish → apksigner 补签 → 验签 → 指纹 → zip 内套 APK → 备份 → 发布。

### 4. 列表抽屉叠节目单条（第七图「列」，未动码）
现象：抽屉打开时底部节目单条仍亮着，文字叠在列表最后一行上（CCTV13 行下混出「CCTV6电影 19:47 / CCTV13新闻 21:04…」），另有同行文字双影。
根因（静态）：
- z 序颠倒：_listPanel（383 行）先入根布局，_epgPanel（401 行）后入——节目单条永远盖在抽屉之上；抽屉虽是全屏盖板，压不住节目单条。
- OpenChannelList 不动 _epgPanel：抽屉打开时节目单条照旧常驻（第 3 项已拍板 7 秒自动收，但抽屉开着它还在收起前叠着）。
- 同行双影=第 1 项表面残帧，方案已定稿，不重复。
方案：抽屉打开时先收节目单条（隐藏+停计时），关抽屉后重新亮出并按 7 秒计时收起；z 序顺带修正（节目单条插到抽屉层之下，双保险）。

### 5. 分类标签间距大、「收藏」被盖（第七图，未动码）
现象：抽屉五标签行（全部/央视/卫视/地方台/收藏）横向留白过大，「收藏」被挤出/视觉被盖（真机图右缘只见残字）；标签行与画面信息行贴得过近，行间还有叠影干扰。
根因（静态，OpenChannelList tabRow）：每个标签左右 padding 各 24px+LeftMargin 12px，五标签横向总占位偏大；tab 行底 padding 仅 12px，与画面信息行距离过近；面板半透明+残帧放大视觉混乱（第 1 项已定稿）。
方案：标签左右 padding 24→12、LeftMargin 12→8（五标签横向收窄约 1/3）；tab 行与画面信息行之间间距拉开（tab 行底 padding 12→24）；量测五标签总宽不超屏，「收藏」完整可见；确认键焦点态不受影响。

### 6. 「地方台往右切」后列表几乎全空（第八图，未动码）
现象：从「地方台」再按右键后，抽屉只剩画质增强行，无任何频道行，也无提示。
根因（静态）：「地方台」往右一格是「收藏」。收藏夹为空时 BuildRows 走 Favorite 分支（DrawerSupport.cs:70-74）只回画质增强行，既不铺频道也不给空态提示；又因当前标签「收藏」被挤出屏右缘（第 5 项），老板看不到朱砂高亮在哪，误以为切到了地方台。空态缺失是主因。
方案：任意标签行装配后若仅剩画质增强行，补一行空态文案——收藏空显「收藏夹是空的，对着频道按确认可收藏」，分类空显「该分类暂无频道」；配合第 5 项把「收藏」标签收回屏内，当前标签高亮可见。

### 7. 「地方台」名实不符（第九图，未动码，已拍板定稿）
现象：地方台标签下出现 CGTN西班牙语/CGTN纪录、CCTV风云剧场/第一剧场/怀旧剧场、CETV1、国学频道——全不是地方台。
根因（静态）：五标签过滤沿用 v4 口径，Tab.Local 定义为 City+Special+Cgtn+UserSource 四类并集（DrawerSupport.cs:64）；而当前台单（央视频 63 路表）没有任何 City 类台，专项=央视系付费/教育频道，CGTN=国家级国际频道。「地方台」实为「非央视非卫视」杂烩，名实不符。
候选（待老板拍板）：
a) CGTN 并入「央视」（同为国家级），专项留央视或并入；「地方台」保留给 City/UserSource，暂空时走第 6 项空态。
b) 标签改名「其他」，内容口径不变。
已拍板（20:04）：按 a——CGTN 并回「央视」（Tab.Cctv 过滤改 Cctv or Cgtn）；「地方台」过滤收窄为 City+UserSource，暂空走第 6 项空态。专项（风云/第一/怀旧剧场、CETV、国学）随拍板口径一并并入「央视」（同央视系/国家级，待老板有异议再调）。

### 8. 画质增强按钮撤出列表页（老板 20:05 问，未动码）
问题：画质增强是操作按钮，不该占频道列表首行——设置页已有同功能行（MainActivity.cs:747），列表页再放是重复入口。
方案（定稿口径）：BuildRows 去掉常驻 AiTier 首行，抽屉只放频道；画质增强切换保留设置页一处；连带清理 _drawerAiTierRowIndex、ItemClick __aitier__ 分支、CycleAiTier 后重建列表逻辑；测试同步去 AiTier 行断言（375 基线口径调整）。

### 9. 抽屉两块换位+画面信息改短格式（老板 20:09，未动码）
老板指令：分类行与画面信息行换位、各自独占一块提前占位；画面信息改简短格式——帧率与分辨率一行（如 帧率:25fps>30fps、分辨率:1080P>2K），画质独占一行（如 画质:高:生效），一律真实画面真实数据。
方案（定稿口径）：
- 布局：抽屉头部之下第一块=画面信息（帧率+分辨率一行、画质一行，共两行固定占位），第二块=五分类标签行；切换标签时两块高度不变不跳动（提前占位）。
- 文案（FrameStatsLine.FormatEx 改）：帧率 `帧率:25fps>30fps`（原>现，实测同值时只显 `帧率:25fps`，不用「＝」句式）；分辨率 `分辨率:1080P>2K`（同值只显 `分辨率:1080P`，宽高并入档位文案 2K/1080P/720P）；画质 `画质:高:生效` 独占一行，「生效」= 当前实际档与设置档一致，实测未生效如实显「未生效/检测中」；测量缺失仍显「检测中」，绝不编数。
- 数据口径：原/现帧率、分辨率全用播放器实测值（2 秒节拍刷新不变）。

### 10. AI 实时画质核查（老板 20:11 令，未动码）
核查链路（静态）：设置档→LiveQuality.Pick（内存分档+32位封顶）→SmartGovernor 运行中按掉帧降档（平稳 10 分钟回位）→OptionsFor 生成滤镜→media.AddOption 注入 LibVLC→起播打点可对账。链条代码层是通的，打点齐全。
三个要害：
1. **没有 AI 模型**：所谓 AI 实时画质是 LibVLC 内建视频滤镜（低=sharpen 锐化，高=sharpen+adjust 对比/饱和微调），传统逐帧处理，无神经网络/超分模型。起播日志「滤镜 sharpen…」只证明参数注入，不证明 AI 模型存在——本来就不存在。
2. **真机证据（第六图日志）**：起播打点「档位 关（设置值 高），滤镜」空——显式高档按 Pick 至少是 Low，实际档为关只能是 SmartGovernor 掉帧连降两档。真机为低配 32 位机，软解 1080p 掉帧率易超 2% 阈值 → AI 增强在这台设备大概率常态压关，用户观感=没开。
3. **参数名不匹配（生效性缺口）**：`sharpen-amplitude` 不是 LibVLC sharpen 参数（LibVLC 用 `sharpen-sigma`）——锐化强度从未真正调上，跑的是默认强度；`gamma=1.02` 超 LibVLC adjust gamma 值域（0~1）可能被钳制。即使档位开起来，效果也打折扣。
方案（待拍板）：
a) 参数改正：sharpen-amplitude→sharpen-sigma（0.8 合法，0~2）；gamma 1.02→1.0 内（或去掉）。
b) 命名如实：「AI画质」改「画质增强」（无模型，名实一致）；TaoPlay 离线超分线才是真模型，直播线没有。
c) 低配保护与生效兼顾：掉帧降档照旧，但起播打点补「滤镜注入后是否被内核接受」（LibVLC 无直接回执，退而求其次：档位非关+实测帧率/负载打点已在，可观测）。

### 11. TaoPlay AI 模型能否用于道映直播（老板 20:12 令，核查完毕，未动码）
TaoPlay 有真 AI：libtaoai.so（ncnn）+ 三模型——flownet 插帧、realesr-animevideov3-x4 动漫超分、RealESRGAN_General_x4_v3 通用超分，均在真机点播上工作过。
但用法是**离线分段增强写回**（SegmentWriter：解码整段→超分→重编码写回文件，播放器读增强后的文件）；AiTierPicker 设备分级 ≥6GB+2020 SoC 才开高档。
结论：搬到道映直播实时链路**不可行**——①直播无文件可写回，实时超分单帧推理几十~几百毫秒，远超 30fps 的 33ms 帧预算；②道映真机为 32 位低配 TV，按 TaoPlay 自家分级直接 Off；③LibVLC 播放链路无逐帧挂钩可接 ncnn（需换播放内核或 Surface 级中间层，工程量新立项）。
建议：道映直播线维持「画质增强=滤镜」口径（修参数名、改如实命名）；真 AI 超分属于「高配设备实时增强」新课题，需新立项评估，不并入 v22。

### 12. 实时超帧/超分模型可行性（老板 20:16 问，口径回复）
- 行业现状：能实时跑的超分只有轻量级（FSRCNN/Real-CUGAN 动漫向/Anime4K GPU 着色器），且要求输入分辨率低（720p 级）、芯片有像样 GPU 或 NPU；RealESRGAN 这类效果好的模型实时必卡。
- 道映真机（32 位低配 TV，Mali-450 级核显，CPU 软解已占大半）：连轻量模型都带不动，插帧（RIFE 系）算力需求更大，更没戏。
- 现实路径：fhd 源输出已是 1080p，上 4K 屏靠系统硬件缩放（现成的），增益有限但零成本。
- 结论：低配机没有能带动的实时模型；高配 4K TV（≥6GB+近年 SoC）勉强可跑轻量模型，需换播放内核接帧回调，新立项评估。
- 4K 屏「拉到 4K」口径（老板 20:18 问）：1080p 流在 4K 屏显示时，系统 GPU 本来就在把画面硬件拉伸到 4K——这步已在发生，App 管不到也改善不了算法（系统级）。App 层可选只有：①指引老板开 TV 系统自带画质引擎（TCL 系统设置-画质，超分/SR 类开关，系统级效果好于 App 层）；②播放内核换自渲染链路接 Lanczos/Anime4K 着色器——新立项，不进 v22。

### 3. 崩溃日志弹窗样式乱（真机第六图，未动码，方案已定稿）
现象：深色面板只占上半屏、下方大片白/浅色；日志文字挤顶部；按钮行在面板中部；按钮呈系统蓝/红块。
根因（静态，MainActivity.cs ShowCrashLog）：
- AlertDialog 沿用 Activity 浅色主题：dlg.Show() 后 SetLayout(-1,-1) 只把窗口拉满屏，但 SetView(box) 的内容区按 wrap_content 计量——box 内 scroll 用 (−1,0,1f) 权重，在 wrap 父布局里权重失效、缩回文字自然高度，面板整体只到上半屏；剩余窗口区域露出主题默认浅色 windowBackground（下方大白）。
- 按钮是原生 Button：TV ROM 默认 Material 主题下带系统着色/焦点态（蓝块即 ROM 焦点高亮叠底），关闭钮代码即红底 #8A3B34，与水墨风格不符。
方案：
- 弹窗改全屏自管：保留 AlertDialog 可，但需 ①dlg.Window.SetBackgroundDrawable 置透明/深色；②SetLayout(-1,-1, WindowManagerFlags.LayoutInScreen)；③box 根布局显式 MatchParent×MatchParent，让 scroll 权重真正铺满。
- 按钮去系统样式：自绘圆角深灰底（水墨配色），焦点态用 TaoKeeper 釉面描边；关闭钮弃红底改深色次级样式，与「一键清理」仅以文字/描边区分；保留返回键关闭与 close.RequestFocus()。
- 选中效果（对齐退出弹窗既有 StyleExitButton 模式，MainActivity.cs:1490）：聚焦=朱砂底 #8A3B34+浅字 #F7F5EE，失焦=水墨深灰 #3A4750+次色 #D8E2EC，不靠 ROM 默认裸按钮；FocusChange 换色，两键一致。
- 操作逻辑：默认焦点落「关闭」（防误清日志，与退出弹窗默认落「取消」同理）；左右键显式互指 NextFocusLeftId/NextFocusRightId，不靠焦点搜索；返回键=关闭（SetCancelable 已兜，Dismiss 复位 _crashDialog）；确认键=执行当前聚焦按钮；日志正文 TextView 不可聚焦，焦点只会在两键间转。

### 13. 能否直接调用电视（TCL）MEMC 运动补偿（老板 20:2x 问，核查完毕，未动码）
核查结论：**不能由 App 主动调用，也不需要调**。
- MEMC 是电视显示芯片（TV SoC 显示后级）的专用插帧引擎（如海思 Hi3751 系内置独立 MEMC 引擎，4K@60Hz 运动补偿），处理位置在显示链路后端——App 渲染的画面进到显示链路后，芯片统一处理。Android 公开 API 没有任何标准接口让应用请求/配置 MEMC（Android TV 硬件能力清单里没有这项；各厂商是私有系统接口，不对普通应用开放）。
- 但系统开关一开就对全画面生效：TCL 系统设置-图像-高级设置里的「MEMC 运动补偿/运动平滑」开关作用于最终显示内容，覆盖包括道映在内的所有应用画面，无需 App 配合。
- 对道映：本机 4K 面板开 TCL 的 MEMC 即可让道映 25/30fps 画面被插帧到面板刷新率，系统级插帧零 App 开销。注意门类取舍：看球/动作片开，慢节奏内容可能出「肥皂剧效应」，老板按内容自调；低配机此项为芯片级处理，不占道映的 CPU 软解预算。
- 结合第 10-12 项总口径：App 层实时增强（滤镜/超分/插帧）在低配机上全部带不动；画质类可动项=TCL 系统画质引擎（MEMC+超分/SR 开关）+ v22 修滤镜参数名（sharpen-sigma）。

### v22 定项拍板（20:22 老板令）
第 10 项方案 a 正式定项：滤镜参数名修正进 v22——`sharpen-amplitude` → `sharpen-sigma`（0.8，合法域 0~2）；`gamma=1.02` 超 adjust 值域（0~1）一并按方案修正。b（改名「画质增强」）、c（打点）未拍板，不落码。至此 19:50 修令下的 v22 待修清单全部定项完毕，开修，修完全测全绿再走出包配方。
