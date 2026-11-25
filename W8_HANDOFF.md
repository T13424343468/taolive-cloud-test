# W8 功能批交接（w8-features 分支）

## 交付内容（对照一期电视端功能清单）

### 1. 手动换线路（画质/线路切换的 2.0 形态）
- `src/TaoLive.Session/PlaybackSession.cs`（最小扩展，未动既有结构）：
  - `SwitchLine(int endpointIndex)`：手动切到本频道 0 基序号的源端点。顺序契约与换台一致（停旧流→Idle→Buffering→Play→重挂 Surface）；播放中选同一路为无害操作；未起播抛 InvalidOperationException；越界抛 ArgumentOutOfRangeException。FailoverCount 归零。
  - `LineCount` / `CurrentLineIndex`：线路数与当前线路只读访问器（OSD 显示用）。
- 画质说明：一期 AI 画质档（LiveQuality）是 LibVLC 滤镜口径（sharpen/adjust 参数），ExoPlayer 内核无对应滤镜参数；2.0 清晰度由 HLS ABR 自适应承担（FastStartPlanner 口径见下）。素材沿用令的「功能保留」以线路切换 + 自适应清晰度落地。

### 2. 换台过渡与 OSD（新文件 `src/TaoLive.Session/SwitchPolicies.cs`，一期纯逻辑逐字移植）
- `SwitchRevealPolicy`：换台即铺底色遮罩（ShouldVeilOnSwitch 恒真），新流首帧才亮（ShouldReveal）；同台反复花屏 ≥2 次走备用载体重接（CorruptionLimit）。
- `SwitchDebouncer`：换台去抖，窗口 400ms，目标台以最后一次按键为准；停稳才起流，中间台不发真实连接。
- `FastStartPlanner`：同台多档先连最快出画面档（带宽升序、同带宽取矮档），亮后无感升最高档，升级失败留当前档。
- `SwitchHud`：OSD 文案口径（台号+台名 / 正在连接… / 切换到线路 N / 信号中断按 OK 重试）。

### 3. 预热/预检（新文件 `src/TaoLive.Catalog/ChannelWarmup.cs`，对接 2.0 TicketResolver）
- `WarmupPolicy` + `SourcePace`：逐源节拍表逐字移植（yangshipin 最慢 10min/2s 让路；nmtv/gxtv 整表聚合按源节拍；xatv/gxtv 6h 长保鲜）。
- `SourceBackoffLedger`：403 源级指数退避账（×4 步进，2min 起步封顶 2h，200 全额恢复）——预热/预检/竞速共用同一本账的口径不变。
- `ChannelPrecheck`：开机预检状态机（Unknown/Checking/Alive/Dead），同名频道各来源变体归并（台名去空格），任一来源通=整台可用；探测异常按失效；keepKnownState 复测不闪烁；状态变化才发事件。
- `ChannelWarmer`：后台整表预热器——对 Kind=Web 取票口端点按台单顺序预取真实流（命中 TicketResolver 5 分钟缓存，换台零等待）；解析失败回写退避账，退避期跳过不烧请求；纯直连频道视为就绪。`WarmAllAsync` 开机第一轮口径。

## 壳层挂接说明（W8 收口时由调度核心合并进 MainActivity，最小挂接）
1. 换台：按键事件先入 `SwitchDebouncer`（Tap），泵里 `HasTarget && IsDue(nowMs)` 才调 `SessionManager.SwitchTo`；Stop 稳后 `Clear()`。
2. 清屏/亮牌：`SessionStateChanged` 收到 Buffering → 铺底色遮罩 + 显示 `SwitchHud.Connecting`；Playing → 撤遮罩。换台瞬间同时显示 `SwitchHud.ChannelChanged(编号, 台名)`（编号=列表序号+1）。
3. 手动换线路：壳层菜单（长按菜单键或 OK 长按已占用时的入口，由调度核心定）列出 `Channel.Sources`，选中调 `session.SwitchLine(i)`，OSD 显示 `SwitchHud.LineSwitching(i)`。
4. 失败提示文案统一换 `SwitchHud.AllLinesFailed`。
5. 预热：壳层启动后 `Task.Run(() => warmer.WarmAllAsync(ChannelTable.Build()))`，与预检循环同跑；看门狗泵照旧。

## 测试（`src/TaoLive.Tests/W8FeatureTests.cs`，29 例，全绿）
- 去抖：窗口内不真切/停稳即真切/目标以最后一次按键为准/Clear 清态。
- 清屏亮牌：换台恒铺、首帧才亮；花屏阈值。
- 先出画面：带宽升序+高度并列、升级目标、失败留档。
- OSD 文案：编号/无编号/连接中/换线路/全断。
- 换线路：起播目标端点、stop 先于 play、同线无操作、越界/未起播抛错、FailoverCount 归零。
- 退避账：冻结/到期解冻/×4 加深封顶/Recover/Clear。
- 节拍表：注册源逐项对拍、未注册源保守节拍。
- 预检：任一通整台通、全断整台断、异常按断、重复标记不发事件、Requeue、归并键归一。
- 预热：Web 预取成功恢复退避、失败冻结退避+冻结期零请求、直连跳过、整表第二轮零新增请求。

## 验证
- `dotnet test src/TaoLive.Tests/TaoLive.Tests.csproj`：58/58 全绿（W8 新增 29 + 既有 29），零错零警（TreatWarningsAsErrors）。
- 本地无 android workload，壳层编译走云端流水线唯一真闸（调度核心合并挂接后验证）。
- 绑定 API 均经 ilspycmd 对拍（ExoPlayerBuilder/IExoPlayer/MimeTypes.ApplicationM3u8/BasePlayer.InterfaceConsts），无凭记忆书写。

## 边界遵守
- 未 push、未动 taolive-2p0 主工作树与主分支（worktree 独立分支 w8-features）。
- 未改 ExoPlayerPlayer.cs；MainActivity.cs 未动（挂接只落本说明）。
- NFS 幽灵文件：提交前已核查排除。
