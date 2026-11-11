# 道映 B 包（versionCode 20 / 1.0.11-r2 界面优化版）构建状态（断点文件）

## 手机版线（11-11 00:30 起，分身 daoying_phone_release 主战）
- 目标：2026-11-11 06:00 前出手机版完整包（签名 APK+源码+文档），64 位 arm64，装一加 15T / ACE5
- 口径：AI 实时优化主线（分片到手即加工、加工完放行；初始缓冲 10–20s；落后上限 60s；三道保险=降档/过热低电直出/60s 强制直出）；素材/模型/图标/链路全沿用 TaoPlay，只做直播分片与缓冲池适配；播放页沿用 TaoPlay 手机版（砍倍速/数字换台 OSD/进度条拖动预览横滑快进退）；列表页顶部筛选框（数字+台名模糊组合）
- 步骤账：
  - [x] 家底盘查：TaoPlay 快照 32528（phone 工程 7.5k 行 + TaoAI BLL 1.2k 行 + native/taoai arm64 libtaoai.so + Assets/taoai 三模型）全在位；构建链 dotnet9 + jdk17 + android-sdk 在位
  - [x] 移植 TaoPlay Model/DAL/BLL/Phone 四树进道映树（保持 AllPlayer.* 命名空间，最小改动）——commit 2f682d2（00:42 固化，15643 行）
  - [x] TaoAI BLL 类移植已到位（9 类，AllPlayer.BLL/TaoAI/）
  - [x] 直播缓冲池 AiBufferPool 落码（07:35，07:10 前台亲自接管；纯逻辑线程安全：初始目标15s(钳10-20)、60s封顶越线触发后持续直出+换台Reset恢复、速率EWMA降档建议、过热/低电直出；逻辑全测 428 PASS/0 FAIL 零错零警，commit 固化）
  - [ ] 播放页适配（沿用+砍件）与列表页筛选框
  - [ ] 编译全绿（arm64）→ 全测 → 出包 TaoLive-64位.zip → 备份 → 汇报
- 断点纪律：每完成一步在此记账；/tmp 不可存成果

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
- 布局（老板 20:36 确认+加严）：抽屉头部之下第一块=画面信息（帧率+分辨率一行、画质一行，共两行固定占位），第二块=五分类标签行；两块一律固定行高提前占位——「检测中」→实测值、数值跳变（25>30 等）只换文字不换高度，行高以最长文案（如 帧率:25fps>30fps / 分辨率:1080P>2K / 画质:高:生效）一次量足，不因数据变化挤高度、不跳动。
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
- 弹窗改全屏自管（老板 20:37 拍板：整体参照第 9 项退出弹窗做法）：窗底透明+面板 InkTranslucency.PanelColor 半透明水墨+LayoutInScreen；box 根布局显式 MatchParent×MatchParent，scroll 权重铺满。
- 按钮（参照第 9 项选中口径）：「一键清理」=朱砂细边框+朱砂极淡底（危险项，同退出钮口径）；「关闭」=纸白细边框+极淡底（同取消钮口径）；常驻=水墨深底+次色字；去 ROM 系统着色。
- 字体色（参照第 9 项）：统一 InkTranslucency.TextPrimary/TextSecondary+可读性阴影，不再私设三套浅色。
- 操作逻辑：默认焦点落「关闭」（防误清日志，与退出弹窗默认落「取消」同理）；左右键显式互指 NextFocusLeftId/NextFocusRightId，不靠焦点搜索；返回键=关闭（SetCancelable 已兜，Dismiss 复位 _crashDialog）；确认键=执行当前聚焦按钮；日志正文 TextView 不可聚焦，焦点只会在两键间转。

### 13. 能否直接调用电视（TCL）MEMC 运动补偿（老板 20:2x 问，核查完毕，未动码）
核查结论：**不能由 App 主动调用，也不需要调**。
- MEMC 是电视显示芯片（TV SoC 显示后级）的专用插帧引擎（如海思 Hi3751 系内置独立 MEMC 引擎，4K@60Hz 运动补偿），处理位置在显示链路后端——App 渲染的画面进到显示链路后，芯片统一处理。Android 公开 API 没有任何标准接口让应用请求/配置 MEMC（Android TV 硬件能力清单里没有这项；各厂商是私有系统接口，不对普通应用开放）。
- 但系统开关一开就对全画面生效：TCL 系统设置-图像-高级设置里的「MEMC 运动补偿/运动平滑」开关作用于最终显示内容，覆盖包括道映在内的所有应用画面，无需 App 配合。
- 对道映：本机 4K 面板开 TCL 的 MEMC 即可让道映 25/30fps 画面被插帧到面板刷新率，系统级插帧零 App 开销。注意门类取舍：看球/动作片开，慢节奏内容可能出「肥皂剧效应」，老板按内容自调；低配机此项为芯片级处理，不占道映的 CPU 软解预算。
- 结合第 10-12 项总口径：App 层实时增强（滤镜/超分/插帧）在低配机上全部带不动；画质类可动项=TCL 系统画质引擎（MEMC+超分/SR 开关）+ v22 修滤镜参数名（sharpen-sigma）。

### v22 定项拍板（20:22 老板令）
第 10 项方案 a 正式定项：滤镜参数名修正进 v22——`sharpen-amplitude` → `sharpen-sigma`（0.8，合法域 0~2）；`gamma=1.02` 超 adjust 值域（0~1）一并按方案修正。b（改名「画质增强」）、c（打点）未拍板，不落码。至此 19:50 修令下的 v22 待修清单全部定项完毕，开修，修完全测全绿再走出包配方。

### 14. 退出确认弹窗配色对齐列表页（真机第十图，老板 20:28 令：查码再定，未动码）
查码实锤（MainActivity.cs:1462-1515 ShowExitConfirmDialog、InkTranslucency.cs）：
1. 浮层割裂：AlertDialog 用 Activity 主题但 ROM 弹窗窗口底是自带钢蓝灰，box 实色 #2B333B 只盖内容、四周露 ROM 底（第六图崩溃弹窗同病灶）；且列表页面板早改成半透明水墨（InkTranslucency.PanelColor=#2B333B @76、暗屏自动 +32），弹窗还是实色深板，观感不同源。
2. 焦点色撞车：StyleExitButton(取消 聚焦#8A3B34/常驻#3A4750) + 退出 常驻#8A3B34——「取消聚焦色==退出常驻色」，两钮永远一粉一粉，焦点看不出在哪（第十图两块粉即此）；且 #8A3B34 与列表页强调色 #8A5C3D（TaoPalette 水墨 Accent）不是同一个色，弹窗这套朱砂系私货不合基线。注：StyleExitButton 系 9833531（20:23）入树，晚于 v21 出包（19:15），真机跑的是无焦点态旧版。
3. 字体色杂：msg=#E8E2D6、按钮字 #D8E2EC/#F2EDE4/#F7F5EE 三种浅色并存；列表页已统一 TextPrimary(#F7F5EE/暗屏纯白)+TextSecondary(#D8E2EC)+可读性阴影。
方案（定稿口径，全按列表页 Token，不再私设色）：
- 浮层：dialog.Window.SetBackgroundDrawable 透明 + box 底改 InkTranslucency.PanelColor(this)（半透明水墨，与列表面板同源同号同亮度自适应）+ LayoutInScreen 全屏定位；露出的是播放画面，与列表页浮在画面上观感一致。
- 选中（老板 20:32 拍板微调）：「退出」=朱砂细边框+朱砂极淡底（朱砂取列表页强调色 #8A5C3D，与 TaoPalette 水墨 Accent/当前台高亮同源，另起一份 FocusDrawable 同构参数：强调色 @64 底+@160 细边框）；「取消」=纸白细边框+极淡底（即现有 InkTranslucency.FocusDrawable() 原样）；常驻=水墨深底+次色字。焦点导航、默认焦点落「取消」、返回键=继续看，全不动。
- 字体：统一 InkTranslucency.TextPrimary(ctx)/TextSecondary + ApplyReadabilityShadow，暗屏自动提白。
- 崩溃日志弹窗（第 3 项）同套 Token 一起做，两窗一套样式。

### 15. 开机自启安装默认开启（老板 20:39 新增，未动码）
拍板：AppSettings.AutoStartOnBoot 默认值 false→true——全新安装/无设置文件时自启默认开；老用户已存设置不受升级影响，不改已选状态。
配套口径如实：接收器在 App 内默认开，但 TV ROM 若在系统层禁自启（TCL 自启动管理），仍需第 16 项跳转指引放行一次；App 内开关与系统开关是两层。
测试：默认值断言更新（新装默认 true、反序列化旧值不变）。

### 16-18. 挂账三项全部拍板（老板 20:41 令，定稿，未动码）

#### 16. 地方台 18 路挂链+图标+分类（定稿）
- 台单：63 路表扩容——15 路新挂（七台河新闻综合/万荣综合/上虞新闻综合/东丰综合/东莞新闻综合/乐至综合/伊犁经济法制/清河经济综艺/烟台经济科技/苏州社会经济/东阳影视生活/南宁影视娱乐/哈尔滨影视 + 甘肃经济/福建经济），3 路浙江系已在表中只核归类；台账 verify_tool/city_channel_candidates.json（清单以该文件为准，落码前逐一比对源与台名）。
- 分类：13 路城市/县级台归 City，「地方台」标签直接点亮；甘肃经济/福建经济为省属频道，归「卫视」标签口径（省级台），老板有异议再调。
- 图标：APK 内置水墨风文字台标（频道简称+水墨底，18 路齐），按频道名映射，与整体风格一致；官方 logo 后续可换；无台标频道回退现有占位。
- 全测补断言：18 路挂链后分类过滤（Local 只含 City/UserSource、卫视含 2 省台经济）。

#### 17. 「AI画质」改名「画质增强」（定稿）
- 全处显示名统一改「画质增强」：设置页行、起播日志文案、相关 UI 文案；设置项键值（auto/low/high/off）不动，旧配置兼容；测试文案断言同步。

#### 18. 滤镜打点补齐（定稿）
- 起播打点补「滤镜参数注入串原样」（OptionsFor 生成的参数整串入日志），配合参数名修正可在真机日志直接对账生效性；档位/帧率/负载打点已有不动。

### 19. 出包流程增补+交付命名改版（老板 20:44 令，随开修令一并生效）
- 增设工序：全部落码完成后，对照 buildB20_state.md 定稿清单**逐项核对源码三遍**（三遍全过、无错漏），才允许跑全测；全测全绿后按配方出包。
- 交付命名（拍板）：软件名用英文名，文件名去掉版本号——zip 与内套 APK 统一命名「TaoLive-32位」；APK 内部版本标识照常（versionCode 22 / 1.0.11-r4，系统升级识别所需）。

## 落码断点账（21:4x 起，父级直接落码，后台代理已停用）
- [x] 第 5 项 起播日志空滤镜补「无」（MainActivity.cs:531）
- [x] 第 6 项 LiveVideoHost 时钟注释 2.5→4 秒（LiveVideoHost.cs:233）
- [x] 第 11 项 滤镜参数修正：sharpen-sigma 0.8 + gamma→brightness 0.02（LiveQuality.cs OptionsFor）
- [x] 第 15 项 AutoStartOnBoot 默认 true（AppSettings.cs）
- [x] 第 17 项 显示名改「画质增强」：FrameStatsLine 画面信息行（设置页行原本已是「画质增强」）
- [x] 第 18 项 起播打点参数串原样：QualityFilterKeysForLog 同步 sharpen-sigma/brightness；空显「无」
- [x] 测试断言同步：低/高档滤镜参数、画质增强显示名、P1 合并键名（Program.cs）
- [ ] 第 1-4、7-14、16、19 项待落

## 落码断点账（续，11-10 深夜收口）
- [x] 第 10 项 崩溃日志弹窗改版：全屏自管（win 底透明+LayoutInScreen+SetLayout(-1,-1)）、box 铺满+PanelColor 半透明水墨、scroll 权重铺满、一键清理=朱砂 AccentFocusDrawable/关闭=纸白 FocusDrawable、常驻=水墨深底@96+次色字、TextPrimary/TextSecondary+阴影、默认焦点落「关闭」、左右键互指、正文不可聚焦、按钮 TextView 自绘、返回键=关闭
- [x] CityCandidatesSource 注册：MainActivity OnCreate 在 GxtvSource 后 RegisterSource（citycdn 15 路新挂：13 城市+2 省经济；目录总台数 97→112）
- [x] 测试断言同步（Program.cs）：BuildRows 四参新签名+画质行撤出校验；央视标签含 CGTN/专项、地方台只收 City/UserSource、卫视只收 Provincial；空态两条（分类空/收藏空）；FpsSegment/ResSegment/TierSegment 共 12 条；真实目录挂链对账（地方台 26、卫视含甘肃/福建经济）；目录总数 97+15=112、城市候选 15 路全带直连+EpgKey 空；ChannelLogoMap.Count==78、新台标映射命中/回退
- [x] 全测全绿：404 PASS / 0 FAIL
- [ ] 第 19 项：三遍对照核对源码（19 项定稿清单）→ 按配方出包（TaoLive-32位，versionCode 22 / 1.0.11-r4）
- [x] 三遍对照核对（11-10 深夜）：①逐项代码点扫描（滤镜参数/显示名/空滤镜/参数串打点/短格式/EPG 7秒/z序/台标PNG 78枚）②自启默认 true、z序（epgPanel 先入、listPanel 后入=抽屉盖节目单）、限流文案、退避打点、候选源地址与台账逐字一致（15/15）③接收器改名 WarmupBootReceiver、自启三级跳、双弹窗统一水墨 Token。三遍全过、无错漏。
512d05813382cb2f714a7897b3ca887f1da0c8905a990897a218c39d90b27415  TaoLive-32位.apk

## v22 出包收口（11-10 22:27）
- [x] 修复编译错误一批（退出/崩溃弹窗 using、命名空间遮蔽 global::、SetVisibility→Visibility、SetTextColor 漏 ParseColor、SetBackground→Background）；AndroidManifest 版本写死 21/r3 → 改 22/1.0.11-r4 后重发布
- [x] 三遍对照核对 + 全测 404 PASS / 0 FAIL（403 通过+1 修复后）
- [x] 出包：dist/v22/TaoLive-32位.zip（内套 TaoLive-32位.apk），versionCode 22 / 1.0.11-r4，debug.keystore 补签，apksigner+aapt2+unzip 三重验签过
- [x] APK SHA-256: 512d05813382cb2f714a7897b3ca887f1da0c8905a990897a218c39d90b27415
- [x] 备份：/root/workspace/存档/TaoLive/20261110-2227-v1.0.11-r4/

## v23 真机反馈账（11-10 22:5x 起，持续追加；已与存档副本 20261110-2227 账合并）
1. [真机图·11-10 22:5x] 列表页「全部收起/全部展开」折叠切换整行退场（老板 22:57 令「先记录」）：标签行下方那行 foldToggle 不再显示——列表默认铺开，折叠交互连同该行占位一起清，不折叠也不留空行。落码等老板发话。
2. [真机·11-10 22:58] 设置页焦点回归（老板令，先记录）：设置页无论哪一行，确认操作后光标都应停在当前行——现在操作后焦点跑偏（推测操作后刷新行/重排把焦点让了出去）。修法待查：RefreshSettingsRows 若整段重建视图，改为原位刷新并在重建后 RequestFocus 回原行。落码等老板发话。
   追加口径（老板 22:59 补）：设置页焦点回原行规则唯一例外=打开崩溃日志页——该行确认后弹窗全屏接管、焦点按弹窗自己的默认（落「关闭」），不算跑偏；其余行一律回原行。
3. [真机·11-10 23:02] 列表页选台后自动收起（老板令，先记录）：列表页选中频道按确认切台后，列表页应自动收起——现状疑似切台后列表仍开着（或焦点行为不对），确认切台=收抽屉+落焦回播放画面。修时核对确认切台路径是否全程收抽屉。落码等老板发话。
4. [真机·11-10 23:05] 切后台返回后原频道连接不上（老板令：查因）。查码初判（未动码）：
   - 重接路径本身在位：退后台 OnPause 停播落健康账 → 回前台 OnResume 统一 Play(_current) 重接，不依赖载体状态。
   - 最可疑=取流退避账冻结：任何一次取流失败/403（含探测）都让该源入账，冻结最长 2 小时；回前台重接时竞速直接跳过冻结源（绝不绕闸）、预热探测也挂起，频道只剩「没接上」。与家庭 IP 403 信誉结论相互印证（央视频源风险最大）。
   - 定音手段：复现后开 设置→崩溃日志，找 [ConnectFailure]/[退避] 打点——有「入账：冻结 N 分钟」即坐实；没有则另查表面重挂。
   - 补充（11-10 23:17 老板口述）：第 4 条「切后台返回连不上」——过一会就自己连上了。倾向支持「重接路径在位、只是有等待窗口」：非死链，最终自愈，与「退避冻结/重试间隔」的表现吻合（但日志打点仍未拿到，不定音）。下次复现时：连不上的那几分钟里开崩溃日志看有没有 [ConnectFailure]/[退避] 打点，连上后再截一段，即可定音。

### 5. 真机日志照片（11-10 23:0x，湖南卫视播放中，只记录）
- 绿：崩溃日志弹窗真机可用——能打开、正文可读、一键清理/关闭两钮可见，水墨样式生效。
- 红（播放停滞实锤）：23:04:16–23:04:52 共 36 秒内 4 次「⑧B/⑨B 水位低提前介入：时钟 4244/5132/4076/5362ms 未走，静默重取流」——静默重取流连续介入未止住停滞。
- 调档链路按设计工作：23:01 起湖南卫视多次起播档位低（设置值高）、滤镜 sharpen 生效 → 23:02:56 水位低攒缓冲目标 2500ms → 23:02:57 自动调档 低→关（播放停滞，会话临时档平缓 10 分钟后回档）→ 23:04:06 湖南卫视起播档位关、滤镜无。中间 23:02:53 切过湖北卫视一次。
- 23:01:10 [Key] 未接管键值 4079 一次（单条，暂不记问题）。
- 关键定音信息：整份日志无 [ConnectFailure]/[退避] 打点 → 第 4 条「切后台返回连不上=取流退避冻结」假设未被证实；本图为播放停滞类证据（对上长播冻帧老问题），与连接失败是两件事。
- 待办：第 4 条查因仍需老板复现时的连接失败段日志；「时钟未走+静默重取流无效」停滞类入 v23 修复池。

### 查因定稿初判（11-10 23:1x，老板令「重点查是不是切后台导致」，已通读源码，未动码）
- 切后台本身不是坏因：OnPause→Stop（代数推进作废未决重连）→OnResume→Play 置 _userStopped=false，链路无状态残留，与正常换台同一条 Play 路径。
- 真正嫌疑链条（源码依据）：
  ① 回前台瞬间设备网络往往未就绪（退后台系统挂网/连接池空闲 30 秒回收/DoH 缓存过期）——回前台 Play 发起取流，头几轮网络级失败。
  ② 央视频源：网络级失败也回灌整源拉黑账本（StreamResolver.cs:274 OnFailure Transport）；同台连败 2 次即整源拉黑 10 分钟（SourceBlackoutLedger.PerChannelLimit=2、ObservationPeriod=10min），半开探测失败再续 10 分钟。
  ③ 拉黑期间重连每 32 秒一轮（ScheduleConnectRetry），每轮都撞拉黑墙返回无流，无任何失败打点——表现即「一直连不上」；10 分钟后半开探测成功才连上 =「过一会就连上了」。
- 与日志照片互证：[ConnectFailure] 只在取流异常分支打；拉黑导致的静默失败不落打点，只有每 32 秒一条「[ConnectRetry] 首连失败冷却到期，主动重连」。照片时段（23:01–23:04）无该打点属正常——那时段是停滞事件，不是重连事件；照片不构成反证。
- 定音手段：下次连不上时开日志——若见「[ConnectRetry] …主动重连」约 32 秒一条、持续十来分钟后自愈，即坐实「网络冷启动×整源拉黑」组合。
- 修法方向：A. 回前台重接前先探网络就绪；B. 网络级失败不回灌整源拉黑（拉黑只认服务器明确拒绝 403/429）；C. 或回前台首次重接强制 force 绕过冻结。定稿取 A+B。

### 6. 切后台重接优化（老板 11-10 23:23 令「先记录 加项」，定稿入 v23 修复池，未动码）
- 由第 4 条查因初判派生，两件一起落：
  - A. 回前台重接前先探网络就绪——复用预检握手现成能力，就绪才 Play；网络没就绪就等，不空打取流。
  - B. 网络级失败不回灌整源拉黑账本——拉黑只认服务器明确拒绝（403/429），避免网络冷启动把好源拉黑 10 分钟。
- 落码口径：A+B 组合；B 保留换路账本（StreamRouteLedger）现有换 IP 逻辑不动。
- 验收：真机复现切后台返回，原频道应在数秒内重接成功，不再出现十来分钟「暂时没接上」。

## v23 修复批次令稿（11-10 23:27 老板令「修」，落码断点账）
1. [x] 列表折叠切换整行退场：移除 OpenChannelList 中标签行下方的 foldToggle 总开关行（_foldToggle 块），连同 ToggleAllGroups/ListGroupKeys/_expanded/_expandedInit 字段一并清（MainActivity.cs）；列表默认铺开不留空行。BLL 的 ListFoldController 类保留（tests/TaoLive.Tests/Program.cs 断言原样通过）；DrawerSupport.BuildRows 五标签抽屉口径不变，分组行本就纯展示不可折叠。
2. [x] 设置页确认操作后光标停当前行：RefreshSettingsRows 重建前用 CurrentFocus 在 _settingsPanel 子视图中找焦点行索引，重建后 RequestFocus 回原行（行数固定 7 行索引稳定：标签+5 行+尾标签，确认行均 Focusable）；唯一例外=崩溃日志行（ShowCrashLog 不经 RefreshSettingsRows，全屏弹窗接管焦点落「关闭」，弹窗关闭后焦点自然回到面板行）。
3. [x] 列表页选台确认后自动收起：已核对——Play() 内已有 CloseChannelList()（MainActivity.cs:452），切台路径全程收抽屉；已落「焦点回播放画面」小步（CloseChannelList 中 _listPanel.FindFocus()?.ClearFocus()）。DeadOnly/判死台点击按 v1.0.8 拍板保持「暂无信号」不切台、抽屉不收，未改。
4. [x] 切后台返回原频道连不上（修法 A+B，已拍板）：
   - A=回前台重接前先探网络就绪（MainActivity.OnResume→ResumeReconnectAsync：ConnectivityManager ActiveNetwork+NetCapability.Internet，未就绪 500ms×6≈3 秒步进等待（ResumeNetWaitPolicy），等待期间切台让位，超时照常 Play 绝不无限等，落 [ConnectRetry] 打点）。
   - B=网络级失败不回灌整源拉黑账：StreamResolver 增 ProbeVerdict 分级（Reachable/Rejected/Unreachable）+ HttpGetStringClassifiedAsync；RunDueProbesAsync 只认 403/429 明确拒绝入账，网络级失败「不入拉黑账」落 [退避] 打点；竞速路径本就不回灌（Outcome 无 Limited 通路）；StreamRouteLedger 换 IP 逻辑保留不动。注：b20-wt 树与旧树不同——本树无 OnFailure(Transport) 回灌点，拉黑账=SourceBackoffLedger，唯一回灌点在预检探测循环，已分级。
5. [x] 播放停滞实锤：⑨B 静默重取流连续 3 次介入无效→第 3 次起升级走 HandleStall 自救阶梯（静默重取流→降清晰度→软重启内核），打点「连续第 N 次」「升级分级自救阶梯」；计数清零三口=持续走钟 30 秒（EarlyRescueResetMs，假活微动不清零）/真首帧/换台；常数 EarlyRescueEscalateAt=3 入 BLL StallRescue 策略类并补断言。
6. [x] 重接优化 A+B（=第 4 条同码）：验收口径=真机切后台返回数秒内重接成功。
收尾：[x] 三遍对照核对 → [x] 全测全绿（409 PASS / 0 FAIL，含 v23 新断言 5 条）→ [x] 出包（TaoLive-32位，versionCode 23 / 1.0.11-r5）→ [x] publish + 备份存档

## v23 出包收口（11-10 23:47）
- [x] 编译尾巴一批：HttpGetStringClassifiedAsync 补 static（CS0120）；NetworkReady 的 ConnectivityManager 补 global::Android.Net 全名（CS0246）
- [x] 三遍对照核对全过（六条代码点逐项扫描/例外口径核对/git 差异总览+版本号三处一致）
- [x] 全测全绿：409 PASS / 0 FAIL（404 基线 + v23 新断言 5 条）
- [x] 出包：dist/v23/TaoLive-32位.zip（内套 TaoLive-32位/：TaoLive-32位.apk + SHA256SUMS + .idsig），versionCode 23 / 1.0.11-r5，debug.keystore 补签，apksigner v1/v2/v3 全 true + aapt2（versionCode 23/1.0.11-r5 核对）+ unzip 三重验签过
- [x] APK SHA-256: ddcee5e47913c14329ecfc05b64c9ec2fed973e63ce4a15c8523d8917635a3e2
- [x] ZIP SHA-256: d79d2b8012c4fc50ae0ea008d20e8ee2cbaf8abe4a6ba8ba3a40bbf4393413be
- [x] 证书 SHA-256: c18030d3…（与历史同源）
- [x] 备份：/root/workspace/存档/TaoLive/20261110-2344-v1.0.11-r5/
====

## 后台支援·打点缺口核查（11-10 23:45，源码逐行，v23 出包前请核对）
- v22 现装包静默路径（第 4 条定音截不到的根源）：①probe 循环冻结期跳过无打点（MainActivity ~658 行 continue）；②取票冷却静默 return null（StreamResolver 161）；③取流 resolved=null 无异常分支无打点（MainActivity ~533）。
- v23 树上已补：[退避]网络级失败不入账、探测 200 恢复、[ConnectRetry]回前台网络未就绪——够第 4 条 A 侧定音。
- 建议出包前再补 3 行纯打点（零行为变化）：冻结期跳过记「[退避] {SourceId} 冻结中，本轮跳过（剩余 X 分钟）」；取票冷却跳过记「[取票] {EpgKey} 冷却中跳过」；resolved=null 记「[ConnectFailure][ResolvedNull] {台名}」。
- 依据：boss 23:04 照片有 4 条静默重取流打点但无一条连接失败/退避 → 定音手段靠这些路径补齐。

## 00:37 巡检快照（后台支援线）
- 分身 daoying_phone_release 收到 NEW_TASK 已续跑；四树移植已固化（commit 2f682d2，15643 行）
- 已在位：TaoAI BLL 9 类、libtaoai.so arm64、三模型、Phone 工程全树
- 待做：AiBufferPool 缓冲池、播放页砍件（倍速/数字换台 OSD/进度条拖预览/横滑快进退）、列表页筛选框、编译全绿、全测、出包 TaoLive-64位.zip
- 巡检提示：MainActivity.Player.cs 仍含进度条/拖预览代码（砍件未做）；MainActivity.Home.cs 未见直播频道列表接入——移植版"主页"还是 TaoPlay 点播主页，直播频道页与筛选框是必须新建的块

## 07:40 后台支援·手机版加速包（快照 32528 对照 + 64 位配方预写）
### 播放页功能对照清单（MainActivity.Player.cs 1435 行 → 道映手机版处置）
- 手势层与单击呼出（55-73，控制 UI 恒在手势层之上）：沿用
- 手势区（724-780）：左滑上下=亮度（750-757）、右滑上下=音量（759-767）沿用；横滑快进（777 OnSeekChunkAware）砍
- 中央播放浮钮（127、974-998 统一状态刷）：沿用
- 底部控制条（191：上一集/播放暂停/下一集/倍速四钮）：上一集/下一集改绑「上台/下台」直播切台；播放暂停沿用；倍速砍
- 控制条自动收（999，默认 1.2s）：沿用
- 画中画（612-640 EnterPictureInPictureMode + 按片源宽高比窗口；更多菜单入口 1073）：沿用
- 锁定（683-722：手势/呼出全失效、解锁键亮 2.5s、返回两次确认防误退）：沿用
- 倍速（301 记忆倍速、938 长按 3x、1035-1052 对话框、1170 文案）：整套砍
- 进度条/拖动预览（151、OnSeekChunkAware 全链）：整套砍（直播实时直出）
- 物理音量键（782-784 交系统默认）：沿用
- 更多对话框（1067-1096）：按直播口径裁剪，倍速项除名
- 再次提醒（00:37 快照已记）：MainActivity.Home.cs 仍是 TaoPlay 点播主页——直播频道列表页 + 顶部筛选框须新建，不能在 Home 上小改
### 64 位出包配方（预写，落码完成即用）
- publish：/root/workspace/.local/dotnet9/dotnet publish src/TaoLive.UI.Android.Phone/TaoLive.UI.Android.Phone.csproj -f net9.0-android -c Release -p:RunAOTCompilation=false -p:AndroidLinkMode=None -p:AndroidSdkDirectory=/root/workspace/.local/android-sdk -p:JavaSdkDirectory=/root/workspace/.local/jdk/jdk-17.0.21.0.2+1 -p:RuntimeIdentifier=android-arm64（csproj 已内置 arm64 与 ApplicationId com.taolive.phone；64 位直接用现成内核，不带 UseMonoRuntime/UseInterpreter）
- 出包前定版项：Phone csproj ApplicationVersion=1 / ApplicationDisplayVersion=1.0.0 未定版，需前台定版后再 publish（清单 Properties/AndroidManifest.xml 为空壳，版本全由 csproj 注入）
- 签名与验签：publish 后按 v21 收口同款 apksigner 补签（debug.keystore 同源、alias 同款），三重验签 apksigner/aapt2/unzip，证书指纹与 v21 c18030d3… 同源核对
- 产物：dist/phone/TaoLive-64位.zip（内套 com.taolive.phone-Signed.apk + SHA256SUMS），备份 /root/workspace/存档/TaoLive/<时间戳>-phone/
- 全测：cd tests/TaoLive.Tests && /root/workspace/.local/dotnet9/dotnet run -v q --nologo（428 PASS 基线只增不减）

## 07:50 后台支援·源早间复测（22 挂链源）
- 21/22 全活（清单 200+首分片 200，双 UA）；湖南 token 型源跨夜 >13 小时仍活，时效风险暂缓但真机验收仍先疑源腐化
- 宁夏卫视（120.76.248.139/bfgd）：25 秒超时三轮 1 过，间歇抖动，较昨日「8s 偏紧偶发」劣化——验收若黑先疑源侧，必要时后台补找备用源
- 结果档：verify_tool/morning_recheck_1111.json（脚本 morning_recheck_1111.py，含唯一 URL 数防闭包错校验）

## 08:50 后台支援·手机版首轮编译预演实录（隔离副本，零冲突）
- **环境回收根因实锤（重要）**：`nohup &` 分离进程在本轮工具调用结束后被环境收割（两次实测：首轮 38 行日志止于 BLL 编译后即死）；改用托管后台（受控后台任务）后全程存活至 APK 出炉。分身 6 次被清极可能同款机制——重派载体应改托管后台或前台直落，勿再用裸分离进程。
- 三轮预演（副本 b20-compile-probe，758M 树拷 149M）：
  - 轮1：8 错（6×CS0234 + 2×CS8130），全部 MainActivity.Player.cs，指向 TopBarPolicy / PlaybackUiPolicy / DeviceCapabilityPolicy 三类（快照 AllPlayer.Model 中本就存在，移植漏了命名空间适配）
  - 轮2：`using Model = AllPlayer.Model;` 别名方案无效——`TaoLive.Model` 是真实存在的命名空间，外层命名空间成员查找优先于 using 别名，`Model.` 仍解析到 TaoLive.Model
  - 轮3：六处改全限定 `AllPlayer.Model.Xxx` → **0 错，APK 出炉**：com.taolive.phone-Signed.apk 54MB（08:42），aapt2 验证 package com.taolive.phone / versionCode 1 / 1.0.0 / native-code arm64-v8a，apksigner 证书 SHA-256 c18030d3… 与 v21 血缘同源
- 已落真实工作树（commit 1966b00）：Player.cs 六处全限定 + 清单 label TaoPlay→道映
- **出包前余项（零警口径）**：
  1. 版本定版：csproj 仍 1/1.0.0，versionCode/versionName 待定
  2. XA4301×3：libvlc/libc++_shared/libtaoai 重复打包警告，需收口为零警
  3. XA0141：Android 16 目标要求警告；XA0033：JDK 版本号解析警告（jdk17 输出格式）
  4. 全测预演未跑（tests 为纯逻辑工程，AiBufferPool 428 绿基线不受 UI 编译影响）
- **前台状态观察**：07:26 记账后 80+ 分钟无新落码、无新记账；若前台会话已中断，按防打断纪律由后台接管播放页砍件与筛选框落码（断点=本台账加速包节的功能对照清单，砍件范围已逐行定位）。

## 09:35 手机版 r2 出包收口
- r2 三件全落码（commit 839352a）：铺满修复（比例新增铺满档=默认，布局变化+起播1.2s双补刷，载体实际宽高喂内核拉满不留黑边）；换装（电视版 ic_launcher_fg/bg → phone ic_fg/ic_bg，mipmap 48/72/96/144/192 重出，anydpi-v26 同步生效）；首页品牌字改「道映」（Home.cs 46）
- 版本：versionCode 1 / versionName 1.0.0-r2；编译 0 错（警告全为 r1 基线既有，无新增）；全测 428 PASS / 0 FAIL
- 签名：apksigner 补签，v1/v2/v3 全 true，SHA-256 c18030d3… 同源
- 完整包：dist/道映-phone-v1.0.0-r2.zip（136MB：Signed APK+RELEASE-NOTES+源码 git archive+SHA256SUMS）；备份 /root/workspace/存档/TaoLive/20261111-0935-phone-r2/
- 待真机：铺满效果（长屏横屏黑边应消失）、换装图标、点屏呼出复点

## 10:15 手机版 r3 出包收口（本轮含 TaoPlay 残留全清令）
- r3 全量落码（commit 2937c6e）：桌面名根治（Activity Label TaoPlay→道映）；残留授权/关于/恢复目录文案改道映；直播主线接线（LiveHlsRelay.cs + MainActivity.Live.cs：7源目录/取流failover/本地HLS中转/AiBufferPool过账/过热低电守卫/降档回写；播放页上台下台=直播上一台下一台；退出播放页 StopLivePlayback 统一收口；AI状态字直播分支=池实时账；主页新增「📺 直播频道」入口行）
- TaoPlay 残留全清（10:08 老板令「除了AI增强部分、播放页部分按钮样式作参考外全部清理」）：类头注释×3、下载文件名前缀、MediaSession名、Wifi/Wake锁名×2、组播锁名、FrameStatsLine口径注释、恢复目录注释、TV侧注释×3——全部改道映/去TaoPlay字样；保留 AI 参考口径 4 处（TaoAIKernel、LiveQuality×2、csproj AI内核注释）+ 播放页按钮样式原样
- 版本：versionCode 1 / versionName 1.0.0-r3；编译 0 错（修 Live.cs 4 类首编错：AiBufferPool缺using、AppSettings双套需全限定AllPlayer.Model、LinearLayout.Gravity只读改SetGravity、_livePanel类型FrameLayout→LinearLayout）；全测 428 PASS / 0 FAIL
- 签名：apksigner 补签，v1/v2/v3 全 true，证书 SHA-256 c18030d3… 同源；aapt2 核 1/1.0.0-r3 / application-label「道映」
- 交付：dist 仅 zip 安装包口径（10:04 令）——zip 内只装 Signed APK + SHA256SUMS（54.3MB：道映-phone-v1.0.0-r3.zip，APK sha256 5b424fae…）；备份 /root/workspace/存档/TaoLive/20261111-1015-phone-r3/
- 如实口径：ts 直播分片走池账目+保险路径=直出（AiSegmentRunner 只吃 MP4 封装，加工插槽已留）；ReportPlayed=送达即近似已播
- 待真机：直播频道列表（筛选/换台）、直播起播、AI池状态字、桌面名显示「道映」

## v24 / 1.0.12 落码收口（11-11 11:40，子代理 daoying_1012_coding）
- 七条拍板清单全部落码：修法 B（30 秒冷却→指数退避 2/4/8 分钟封顶 2 小时、成功即恢复）、备援接入重连链路（发现 A：换票委托分级切，第 1 次同源第 2 次起 ResolveWithFailoverAsync+健康表）、⑨B 清零窗 60 秒、Buffering 主信号+5 秒去重窗、90 秒介入上限 2 次、定格最后一帧+「正在换信号」角标、3 秒库存两档（BufferInventory，默认档=起播即 3 秒）、水位只涨不降（取消 HoldEdge 降档）、UI 三项（崩溃日志按钮固定底栏+窗口次序修正、Tab 改序 收藏插二+默认定位当前台、DrawerPad=48 对齐+首标签 LeftMargin=0）
- 全测全绿：442 PASS / 0 FAIL（409 基线 + 33 新增断言）；编译零错误（警告全为既有基线）
- 出包：dist/v24/TaoLive-32位.zip（内套 TaoLive-32位.apk+SHA256SUMS+.idsig），versionCode 24 / 1.0.12，v1/v2/v3 全签，证书 c18030d3… 同源
- APK SHA-256: df84cce1d9a2d71dcd711d4216a021de748229d14fdbd6c98479555fc723fd70
- 备份：/root/workspace/存档/TaoLive/20261111-1140-v1.0.12/
- 遗留真机验证：定格保帧、静默重接黑帧（默认档已按拍板=起播即 3 秒；验证后 InventoryMode 一行可切）

## 11:41 v24 交付口径改安装包（老板令「目前只要安装包」）
- 安装包：dist/v24/TaoLive-32位.zip 重出，内只装 Signed APK + SHA256SUMS（去 .idsig），75.8MB，zip sha256 483b6ed6…eba33；APK sha256 不变 df84cce1…23fd70
- 原三件套 zip 改名 TaoLive-32位-完整包.zip 留档；备份 存档/TaoLive/20261111-1140-v1.0.12/ 不动

## 11:55 后台支援·手机版五层缺口核实（分析师候选①②，file:line 实锤）
### 候选①：手机版 r3 复现电视版 v23 卡死模式——属实，四缺口
- 手机版起播链有备援（MainActivity.Live.cs 260-262：主取流 4 秒快失败→ResolveWithFailoverAsync），但断流后全链无备援无退避：
  1. 中转服务 LiveHlsRelay.cs FetchText/FetchBytes（243/258 行）上游失败静默返 null——单发不重试、不退避、不落健康账，403 半死源一次撞不通即断供
  2. 断流自救 VideoHost.TryStreamRetry（AllPlayer.UI.Android.Shared/VideoHost.cs 168 行）只重播同一中转地址，不重新取流不切源；MaxAttempts 用尽即交还用户
  3. 电视版 v24 五层的 UI 层件（Buffering 主信号/60 秒窗/节流/定格保帧/预备票/3 秒库存/水位只涨不降）全在 TaoLive.UI.Android.Tv 的 LiveVideoHost.cs，手机版是 TaoPlay 系自带宿主，一样没接
  4. BLL 层件（退避账 YspCkey、EndpointRacer、StreamResolver.Failover、StallRescue、BufferPolicy）手机版引用即得但调用点未接
- 推演：老板手机装 r3 播央视频源，会撞上与电视版 v23 同款「介入→几秒→卡→介入」循环
- 落码蓝图（BLL 家底全现成，改动集中在手机版三处）：①LiveHlsRelay 上游取流接退避账+多次尝试；②直播 TryStreamRetry 改「重新取流走 Failover+健康表」而非重播旧地址；③停滞探针接 StallRescue 分级
### 候选②：AI 主线对直播 ts 分片未生效——属实，改造条件已核清
- AiSegmentRunner.cs 16/66/107 行：只吃 MP4 封装（注释明示 ts/mkv MediaMuxer 不支持，如实跳过）；直播 ts 分片全走直出兜底，1080P→4K 主线在手机直播上当前不发生
- 改造路径家底现成：TaoPlay TsRemuxer.cs（TS→MP4 无损转封装，H.264/H.265+AAC 即可，不重编码零画质损失）——中转落盘 ts 分片→TsRemuxer→AiSegmentRunner 加工→放行即可闭环老板主线
- 风险点：逐分片转封装的吞吐与时延须真机标定（老板真机实测 2 秒内 1080P→4K 是点播素材口径，直播逐段口径未实测）；H.265 央视源兼容性抽验
### 候选③：两包真机验证统一对照清单已备（projects/1377423/两包真机验证对照清单.md）

## 13:20 后台支援·r4 蓝图 vs v25 定稿口径漂移核对（手机版暂停线，恢复即用）
- 结论：11:47 蓝图三处改动方向不变，恢复时需对齐 v25 定稿三处口径：
  1. 账本建在 v25 并发安全版上（方案轮 37/38：源账/票账改锁或 ConcurrentDictionary）——手机中转重试+自救会是并发写方，恰好是 v25 并发安全的直接受益者；同时沿用全局让路闸与「网络级失败不计负」计负口径（网络抖动不入账防误冻）
  2. 健康表/台账用台×源粒度（12:32 老板拍板 1e）
  3. 新增恢复时拍板项：修法七热备并行取流是否随 r4 进手机版（电视版专属落点，老板未定）
- 不受 v25 影响的部分：AI ts 接线蓝图（TsRemuxer→AiSegmentRunner，独立子系统）；手机版无后台预热/邻台预取机制，让路修法一~五无手机侧对应物
- 另：本 worktree HEAD 仍为 cefde77（v24 线），v25 落码与 99 轮核验在别处进行，恢复 r4 时须先确认 BLL 基线已含 v25

## v25 / 1.0.13 落码收口（11-11 13:25，方案 v25 七修法全落）
- 落码：修法一（首见登记完整口径×jitter、全局让路闸原子占位、邻台预取改入队收死参数口子）、修法二（票账联动顺延不发起、网络变更/时钟跳变清账 receiver）、修法三（屏内先探：列表呼出/滚动停稳触发，≥3s 节流，8 台立刻批，共享闸+票账联动）、修法五（BLL 并发账 ConcurrentDictionary+锁）、修法六（台级表现账 ChannelPaceLedger：EpgKey×SourceId 一格账、窗口 3 门槛 10 封顶、三档钳制、时段系数总闸只降不升、失效台独立档 30min/2h、整表源真聚合 60s TTL）、修法七（HotStandbyKeeper 备援轻校验、帧级切换 LiveVideoHost 独立窗 4 次、热备与预热共账、Play 即停旧代数）
- 全测全绿：464 PASS / 0 FAIL（442 基线 + 22 v25 新断言）；99 轮源码巡逻清 0（patrol_v25_99.md，每轮=全测 464+锚点 22+指纹比对；修复落码即重建重跑口径 v2）
- 修复轮：出包编译发现 4 处（HotStandbyKeeper 命名空间、热备签名 nullable 元组、MergeOptions 实参、ConnectivityManager 限定名）+ 清单 versionCode 24 硬编码→25/1.0.13；修复后重建重跑 99 轮清 0
- 出包：dist/v25/TaoLive-32位.zip（APK+SHA256SUMS+.idsig），versionCode 25 / 1.0.13，v1/v2/v3 全签，证书 c18030d3… 同源
- APK SHA-256: 5ec5141d7996a0bcd58e4880f15c965d970ad3cfcafb7194484ee843e7572d81
- zip SHA-256: 0511f244efd9568625fd9c0c1b70c5157fa9252612ccd262b86c5aee2a3e5c3a
- 备份：/root/workspace/存档/TaoLive/20261111-1325-v1.0.13/
- 真机验收点：起播后 5 秒起热备校验流（[热备] 打点）、断流帧级切备援（[热备] 主路断到切换 ms）、网络切换清账（[账本] 打点）、列表呼出屏内先探、失效台独立档（[预热] 判死/复测恢复打点）

## 15:05 后台支援·v26 包体审计+兜底交互复核+断点固化
- **v26 英文化包体审计（全绿）**：aapt2 badging=com.taolive.tv / 26 / 1.0 / label TaoLive；二进制全扫（resources.arsc+classes.dex+res 全部二进制文件）中文串 0 条——安装名/桌面名/横幅/图标/媒体会话全英文实锤，老板装包前回归面已兜住
- **兜底循环 × ⑨B 限频交互复核（候选②，结论=设计自洽，两处边界观察）**：节流分支确不挡自救（_earlyRescueStreak 累计到阈值即 HandleStall 升级，LiveVideoHost 316-334 行实核）；耗尽兜底循环（641-682 行）每 60 秒 1 次取流+每 3 轮软重启，绕过 90 秒窗预算属设计内（慢车道+退避账限流，画像有界）。观察①：每周期重武装快速阶梯（_retriesLeft=3，2/4/6 秒三连换票），死源最坏 ~4 次/分钟发起，真实网络请求由退避账弹回兜住，建议真机日志核对联动；观察②：退避弹回返回同 URL 时兜底轮仍 StartInternal 重播同地址，2 轮后由软重启兜底，行为有界但可在日志中辨识
- **断点固化（最高价值动作）**：前台 v25+v26 的 41 个文件此前全部未提交（git log 停在后台支援线提交），环境回收即失；已保护性 commit c650225，工作树清零。防清杀纪律恢复完整覆盖

## 15:5x 后台支援·定版复现补口+巡逻v3重启+r4蓝图第二次对齐
- **定版复现补口（新发现缺口，已修）**：树内 csproj 仍 25/1.0.13/道映，v26 包内 26/1.0/TaoLive 是构建期 -p 覆盖出来的——从树直接重建会退回旧版本号（可复现性隐患）。已把 csproj 对齐官方口径 26/1.0/TaoLive（commit 214fd04）。此前 15:05 审计只扫包体未对树，教训：出包后应对树核对版本字段
- **99 轮巡逻 v3 重启（托管后台载体）**：前班 14:47 重启实为 nohup 类载体，14:44 跑完第 1 轮即死（再次实锤教训）；本轮用托管后台重跑，版本锚点同步 26/1.0（脚本 v3 口径），输出仍 patrol_v25_99.md。清零后报明细
- **r4 蓝图第二次对齐（v25 真机四修法入蓝图，恢复手机版即用）**：
  1. 耗尽兜底循环：手机版缺口「TryStreamRetry 只重播同地址+上游失败静默返 null」修完后，仍须移植电视版 LiveVideoHost 641-682 行耗尽常驻兜底模式（30 秒起/60 秒重武装/每 3 轮软重启/首帧恢复或换台收摊），否则重试耗尽照样躺平——r4 蓝图三处改动扩为四处
  2. 让路闸限定：手机侧全局让路闸落码时限定只管取流/预取，不挡自救升级行进（同 ⑨B 教训）
  3. 清账口径：手机版健康表计负沿用「真换地址才清账」，同 URL 假成功不清账（防备援永远轮不到）
  4. 档位对账：手机 64 位无 32 位内存地板问题，但降档回写打点同样补内存值便于对账
- **BLL 基线确认（取代 13:20 节旧注）**：本 worktree HEAD 已含 v25 全部落码（c650225 固化）+ 台账（40de59e）+ 复现补口（214fd04），恢复 r4 前的「BLL 基线须含 v25」条件已满足
- **宁夏源复测（15:5x）**：3 轮 1 过（round1 ok 200B/200，round2-3 URLError），与早间口径一致仍间歇抖动，验收黑屏先疑源侧结论维持
- **AI ts 前置验证已派后台分身（只读+独立目录）**：TsRemuxer（taoplay-live/src/AllPlayer.UI.Android.Phone/TsRemuxer.cs）对直播 ts 分片 TS→MP4 无损转封装的正确性/吞吐/边界沙箱实测，产物落 projects/1377423/ai_ts_probe/，把拍板依据从推断变数据

## 16:2x 后台支援·AI ts 前置验证收口+巡逻v3.2清零+评分×竞速边界预析
- **AI ts 前置验证（分身交货，产物 projects/1377423/ai_ts_probe/）**：结论=可行。真实直播 CCTV12（H.264 1080p25+AAC 48k）15 连续分片 15/15 无损（帧数逐一对账一致）；100 分片 0 失败、聚合 30.4MB/s（沙箱口径，5MB/5s 分片 remux 仅占时长 ~3%，SoC 慢 3-5 倍仍余量足，瓶颈在 AI 超分不在转封装）。边界：4KB/16KB 失败（门槛应提 ≥64KB）、无音轨/HEVC/半截分片均可过。改造点 file:line 五处（TsRemuxer.TryRemux 提 public、LiveHlsRelay ServeSegment/GetSegAsync 挂点+MIME/URI 改 mp4、AiSegmentRunner 零改动、AiWiring 369 行闸门自然放行、内存 byte[] 需落盘暂存或 MediaDataSource）。诚实口径：沙箱以 ffmpeg -c copy 作容器级代理（MediaMuxer 沙箱不可跑），真机标定四项=真机吞吐、MediaMuxer 时间戳绝对基值（~21h）是否归一、播放器对逐分片 fMP4 HLS 兼容性（最大剩余风险）、SegmentWriter 短段容差+央视 4K HEVC 真源抽验
- **99 轮巡逻 v3.2 清零**：99/99 全绿（468/0、锚点 26/26、版本锚 27/1.1，前台已扩 FILES+VideoHost.cs 日志攒批锚点）；本轮复核确认，明细 patrol_v25_99.md
- **评分×竞速边界预析（开工令前备案，待并入方案）**：竞速保持「实测优先」不按分排，但沿用既有退避账生死口径——冷却期（_deadUntilMs）内的死源不进竞速候选，冷却结束回场即实测；即评分管排序、退避账管生死、竞速管同时起跑，三权分立互不掺和。可堵「已知死源白耗起播时间」缺口，且不新增规则只复用现账
- **宁夏源预案（candidate ④）**：curated 仅 1 条候选无备选源；红线不擅自新增收录。预案=验收遇黑屏先以其他省台对照排除源侧归因；如需备选源，老板点头后派源调研（找路子+全链路核验再入挂链）
- **A/B 对比操作注意（已入对照清单 v5）**：A=26/1.0、B=27/1.1 同包名，A→B 正常覆盖升级；B→A 属降级系统会拒绝，须先卸载（丢收藏/设置/三本账）。建议先 A 跑基线再覆盖升 B；对照清单已同步 A 包（bfc8aeb7…1e82，官方包 v2 的升级替代）与 B 包四项对比验收表

## 17:2x 后台支援·开工支援包备齐（第二波双包，开工令即用）
- **支援包落档**：projects/1377423/开工支援包-第二波双包.md——①AI 基准自测定档蓝图：既有底子（AiRealtimeBench.Run 各档帧耗时+TaoAIKernel.GpuReady Vulkan 在岗探测）不用新写，补四条=基准结果独立小 JSON 持久化（三本账同款家法）、自动重测三+一触发（版本升级/超 14 天/上次失败×3 走 CPU 保守档/手动，24h 防频，手动锁定时基准只更新天花板）、测量时机避让（首帧后+播放中推迟+不动播放线程）、32 位盒子口径（起步档只信本机实测，Vulkan 不在岗 CPU 档）；验收断言增量 6 条备好。②A/B 对比口径提醒：26/27 对比的是编译方式，28/1.2 瘦身收益单独记（下载/安装时长），别混进启动计时结论（对照清单 v6 已加注）。③裁剪回归初稿：源码级 VB/System.Data/WebSockets 零直接引用，第一层风险低于预估，残余在 IL 级传递引用，回归=对照清单全条目在裁剪包重跑。
- 待开工令，本包与评分×竞速边界（a0e2a22）、AI ts 改造点（ai_ts_probe/）共同构成开工即用支援面
