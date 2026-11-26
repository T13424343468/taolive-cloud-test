# 覆盖缺口可达性标注台账（gaps-vc90）

- 依据：`.verify/gaps-vc90.txt`（30 个文件的未覆盖语句行 + partial branches）
- 标注口径（三选一）：
  - 【可达-可测】纯逻辑/注入桩即可在单测触发，给触发条件一句话；
  - 【不可达-防御】并发重试/异常兜底等防御路径，单线程确定性测试无法触达；
  - 【UI壳层】需真机或 UI 自动化的壳层/平台回调。
- 纪律：全部标注基于逐文件通读 `src/` 源码（证据落点见文末），未运行任何测试、未改任何产品/测试码。
- 本台账共 30 个文件；partial branch 逐项标注，未覆盖语句行按方法分组标注。

---

## TaoLive.Session/PlaybackSessionManager.cs

**未覆盖语句行（116 行）**
- L82/88（SegmentLoader/MainDispatcher 自动属性）：【可达-可测】测试内 setter 赋值即覆盖。
- L118-126/134-150（FallbackSourceProvider、PrimaryAliveConfirmed、QueueOrderer 的 getter/setter 与 foreach 转配）：【可达-可测】先建会话再设属性，foreach 转配即走。
- L209-234（PrepareStandby：当前台即目标/已在备/旧备让位/会话表复用/新建会话各分支体）：【可达-可测】注入 hotStandbyEnabled:true + 假内核工厂，按「备同台→备新台→重复备」序列驱动。
- L260-261（SwitchTo 无效频道抛参）：【可达-可测】传 Sources 为空的 Channel。
- L349（ShrinkForMemoryPressure 释放失败 catch）：【可达-可测】让位会话的假内核 Dispose 抛异常。
- L389-442（PumpStealth 全体：管道懒建、SegmentPulled→ReportGood、SupplyFrozen/RetakeListNeeded 打点与作废、掉速喂账、码率闸下发、换向）：【可达-可测】接 HealthLine+假 SegmentLoader（同步返回 byte[]/null），Tick 数拍。
- L450-456（PumpAlignOutcome 站住回账）：【可达-可测】接 Arbiter，会话 AlignedSwitched 至 0 路后推时钟过 UpgradeFailWindow。
- L468-494（PumpRaceProbe：候选收集、单飞旗、竞速回填、防串作废）：【可达-可测】接假 RaceProber+同步 MainDispatcher，Failed 态多线会话 Tick。
- L551-566（TryReturnToPrimary 软切口径：休眠拒绝/池粮不足/冷却拒绝/对齐切）：【可达-可测】接 Arbiter+StealthPipeline，稳定窗+主源 OK 判据满足后推单调钟。
- L601/625（超龄清理与 LRU 淘汰的 ReleaseKernel catch）：【可达-可测】假内核 Dispose 抛异常。
- L658-669（WireSession 的 Failover→逃生口失败回账、AlignedSwitched→升档起账）：【可达-可测】接 Arbiter 后触发会话 Failover/AlignedSwitched 事件。

**Partial branches（52 处）**
- L94（monotonicMs ?? 默认）：【可达-可测】构造传 null。
- L111（channelId ?? ""）：【可达-可测】TryManualRetry 传 null。
- L122/146（setter foreach）：【可达-可测】先建会话再设属性。
- L208/212/217/221/225（PrepareStandby 前置守卫与让位分支）：【可达-可测】见上语句行 L209-234 触发序列。
- L259/266/289（SwitchTo 无效台/热切就绪/热备未就绪冷切）：【可达-可测】无效台抛参；备台 Playing 后 SwitchTo 走热切；备台 Buffering 中 SwitchTo 走冷切。
- L374（Tick 无 Current）：【可达-可测】不 Begin 直接 Tick。
- L387/388/399/402/403/405/413/422/424/432/434/438（PumpStealth 各闸与换向分支）：【可达-可测】见 L389-442。
- L449/451/452（PumpAlignOutcome 守卫与站住判定）：【可达-可测】见 L450-456。
- L466/467/469/471/472/479/480/484/486/488（PumpRaceProbe 守卫、全败、防串、winner>0）：【可达-可测】见 L468-494。
- L508（TryStandbyTimeout 攒流超时）：【可达-可测】备台 Buffering 态推钟过 7 秒窗。
- L530/536/542/545/550/552/555/560/564（TryReturnToPrimary 各闸）：【可达-可测】见 L551-566。
- L656/666/670（WireSession 钩子分支）：【可达-可测】见 L658-669。
- L703（TrackReturnStability 失稳清账）：【可达-可测】在播会话离开 Playing（推 Reconnecting）。

## TaoLive.Session/PlaybackSession.cs

**未覆盖语句行（33 行）**
- L119-122（GuardKernel catch→KernelCallbackFailed）：【可达-可测】StateChanged 订阅者抛异常后触发内核回调。
- L159（BufferedStockMs）：【可达-可测】直接读属性。
- L187-195（NotifyVideoFault 全体）：【可达-可测】接 QualityBook/HealthLine 后调用。
- L237/243（状态矩阵 Buffering→Reconnecting、Reconnecting→Buffering 合法臂）：【可达-可测】内核 OnBufferingStart/OnBufferingEnd 事件驱动。
- L323-324（Start 非 Idle/Failed 抛异常）：【可达-可测】Playing 态调 Start。
- L364-365（ChangeChannel 无效台抛参）：【可达-可测】传空源频道。
- L379-383（ChangeChannel 全端点无效→Failed）：【可达-可测】频道 Sources 全 IsValid=false。
- L410/412（MarkBlindZoneIfAny 盲区上账）：【可达-可测】接 QualityBook，起播非 .m3u8 端点。
- L444-453（AlignedSwitchTo 守卫）：【可达-可测】未起播调用抛异常；同线 Playing 调用空操作。
- L536（Kernel 属性）：【可达-可测】直接读。
- L541-546（ForceFailover）：【可达-可测】Playing 态调用。
- L1007-1008（ReleaseKernel 在播会话抛异常）：【可达-可测】未 Supersede 的会话调用。

**Partial branches（48 处）**
- L99/100（构造 null 校验）：【可达-可测】传 null player/clock。
- L121（KernelCallbackFailed 调用点）：【可达-可测】见 L119-122。
- L150/163（CurrentEndpoint/PeekQueueEndpoint 越界臂）：【可达-可测】未起播读、越界窥视。
- L174（ReorderQueueWinnerFirst 在播路右移臂）：【可达-可测】在播线路 1，胜者 2 号回填。
- L188/189/191/193（NotifyVideoFault 守卫）：【可达-可测】见 L187-195。
- L232（状态矩阵剩余非法迁移臂）：【可达-可测】逐对非法迁移断言抛 InvalidOperationException（如 Failed→Playing）。
- L308/322/330/363/378/386/408/409/411/443/447/451/456/466（Start/ChangeChannel/SwitchLine/AlignedSwitchTo 各守卫与排序分支）：【可达-可测】QueueOrderer 注入排序委托、QualityBook 注入后按对应入口驱动。
- L522（NotifyFirstFrame 非 Buffering 臂）：【可达-可测】Playing 态再发首帧事件。
- L542/543（ForceFailover 守卫）：【可达-可测】见 L541-546。
- L556（NotifyStalled 非 Playing 臂）：【可达-可测】Buffering 态发 OnBufferingStart。
- L582（TryMicrolagSeekBack 防振铃窗内臂）：【可达-可测】盲区源 30 秒内第二次进缓冲。
- L636（NotifyVideoStall 非 Playing 臂）：【可达-可测】非出画态上报。
- L679/702/710/721/730（CheckWatchdog 自愈满额轻试/慢轮重试/起播超时/慢连宽限/重灌超时各臂）：【可达-可测】FixedClock 推进+假内核，分别构造全断满 6 次、Buffering 超时、Reconnecting 超时。
- L896（CheckProactiveFailover 非 Playing 臂）：【可达-可测】非 Playing 态泵。
- L934（lowInventory 三元臂）：【可达-可测】内核 BufferedMs 给 0 与给低值各一拍。
- L1006（ReleaseKernel active 守卫）：【可达-可测】见 L1007-1008。
- L1036（短命票窗判定）：【可达-可测】起播 15 秒内报 HTTP 错+接 QualityBook。
- L1079/1085/1091（IsTimeoutClassError/慢连宽限预算/进度闸）：【可达-可测】错误码带/不带 timeout、预算耗尽、零进度各一例。
- L1130/1137（TryFailover 备源耗尽/限速冻结臂）：【可达-可测】单线频道报错；60 秒窗内换源满 FailoverCapPerMin 次。
- L1185（DescribeChannel 无名退 Id/未知频道臂）：【可达-可测】Name 空台、未起播会话打点。
- L1194/1201（AppendFallbacks 无供给/同址去重臂）：【可达-可测】不设 FallbackSourceProvider；供给返回重复 URL。

## TaoLive.Catalog/TicketResolver.cs

**未覆盖语句行（48 行）**
- L116-118（Invalidate）：【可达-可测】PutCache 后调用再查缓存为空。
- L128-151（ReissueAsync 全体）：【可达-可测】预置缓存票后以 failingUrl 反查；未命中分支传陌生 URL。重取路径走注入的假 Http（TicketResolver.Http 测试替换口径，ChannelWarmup 注释在案）。
- L170-181（ResolveYspAsync 网络失败/403 冷却/成功入账）：【可达-可测】假 Http 分别返回抛错/403/iretcode=0 报文。
- L203（ParseYspResponse 正常解析返回）：【可达-可测】纯函数直测。
- L212-215（ResolveCztvAsync）：【可达-可测】假 Http 返回含 m3u8 的 playInfo 页。
- L256-270（ResolveKankanAsync catch/Match 失败、ResolveNmtvAsync）：【可达-可测】假 Http 空体/正常表。
- L285-293（FetchNmtvTableRawAsync）：【可达-可测】假 Http 返回 base64+XXTEA 样本（NmtvXxtea.Encrypt 可造）。
- L302/317（ResolveGxtvAsync/FetchGxtvTableRawAsync）：【可达-可测】假 Http 返回 rows JSON。
- L503-511（NmtvRow 兜底2 旧口径）：【可达-可测】纯函数，构造 title 前无独立流但后文有 m3u8 的 JSON。
- L538/559（GxtvRow 命中返回/JsonBlockEnd 越界返回 -1）：【可达-可测】纯函数直测。

**Partial branches（37 处）**
- L47/52/60/77/84/86（ResolveAsync 入口守卫、双查缓存、失败打点、直连放行、成功打点）：【可达-可测】直连端点与 Web 端点各一例+假 Http。
- L99（TryGetCache 过期臂）：【可达-可测】注入过期时间戳。
- L132/134/136/143/145/148（ReissueAsync 反查各分支）：【可达-可测】见 L128-151。
- L159（ParseYspParams 参数缺失臂）：【可达-可测】纯函数，去参 URL。
- L171/178（ResolveYspAsync body null/iretcode 臂）：【可达-可测】见 L170-181。
- L192（ParseYspParams 正则命中臂）：【可达-可测】纯函数。
- L201/202（ParseYspResponse code 缺失/url 缺失臂）：【可达-可测】纯函数。
- L211/213（ResolveCztvAsync body null/无 m3u8 臂）：【可达-可测】见 L212-215。
- L233（kankan channelId 缺失臂）：【可达-可测】无 channel_id 的 api URL。
- L255/259（kankan 请求异常/无匹配臂）：【可达-可测】假 Http 抛错/空体。
- L269（ResolveNmtvAsync 空表臂）：【可达-可测】假 Http 空体。
- L284（FetchNmtvTableRawAsync 请求异常臂）：【可达-可测】假 Http 抛错。
- L301（ResolveGxtvAsync 空表臂）：【可达-可测】假 Http 空体。
- L368（FetchTableCachedAsync 缓存判定臂）：【可达-可测】两次调用间隔推钟。
- L497/499/501/507/510（NmtvRow 主口径与兜底分支）：【可达-可测】纯函数。
- L526/536/547/552（GxtvRow 无 rows/无 name 行/无 encodeM3u8/括号越界臂）：【可达-可测】纯函数。

## TaoLive.Session/ChannelPrecheck.cs

**未覆盖语句行（31 行）**
- L51/54/56（Reporter 属性与 Report try/catch）：【可达-可测】注入会抛异常的 Reporter。
- L89-92（AttachHealthStore 合并循环）：【可达-可测】预置会话内结论后挂账。
- L175-192（BestAliveSource 全体）：【可达-可测】纯逻辑，构造同台多变体目录+健康账。
- L205（MarkState 订阅方异常 catch）：【可达-可测】StateChanged 订阅者抛异常。
- L238-249（连挂淘汰与绿门槛跨越补发）：【可达-可测】同源连报 3 次失败；Oks 从 0→1 时断言补发事件。
- L283/290/296/297（RunAsync 车道取消/探测异常/限速延迟/整台定论）：【可达-可测】注入假 probe 与 ct。
- L314-328（ListMetrics 纯函数）：【可达-可测】直测。

**Partial branches（23 处）**
- L55（Report catch 臂）：【可达-可测】见 L51-56。
- L69（IsStale null/过期臂）：【可达-可测】纯函数。
- L85/88/90（AttachHealthStore 守卫与合并取新臂）：【可达-可测】见 L89-92。
- L98（构造 probe null 抛参）：【可达-可测】传 null。
- L109/110（NormalizeName 空名臂）：【可达-可测】纯函数。
- L128（DeriveFromLedger 无账臂）：【可达-可测】空健康表查询。
- L138（SourceHealthOf 无账臂）：【可达-可测】未探台查询。
- L159/166（MarkOf Alive 无绿源臂、AnyGroupSourceGreen 谓词）：【可达-可测】健康账只有 Dead 条目时查询。
- L180/182/184/185/191（BestAliveSource 各守卫与兜底臂）：【可达-可测】见 L175-192。
- L230/234/237/240（WriteSourceResult 时间兜底/playback 直达/淘汰/脏标记分支）：【可达-可测】nowMs 传 0、playback:true、连败 3 次。
- L272/280（RunAsync 取消臂）：【可达-可测】探测中取消 ct。

## TaoLive.Session/StealthPipeline.cs

**未覆盖语句行（33 行）**
- L20-70（HttpSegmentLoader 全体）：【可达-可测】需本地 HTTP 桩（HttpListener/测试内嵌服务器）：盲区直拉、清单→首分片、非 200、异常、1MB 封顶各一例。纯注入不可达（静态 HttpClient），但本地桩即可测，归可达。
- L203-204（Tick 收摊态返回）：【可达-可测】Retarget(null) 后 Tick。
- L265（拉片续接 catch 臂）：【可达-可测】loader 返回 faulted task。

**Partial branches（23 处）**
- L26/32/34/37/49/53/58/62/64（HttpSegmentLoader 各分支）：【可达-可测】本地 HTTP 桩（见上）。
- L133/134（构造 loader null/monoMs null）：【可达-可测】传 null。
- L173（Retarget endpoint null 臂）：【可达-可测】Retarget(channel, null)。
- L202/213/222/223/231/232/233/240/241（Tick 冻结宣告/收割代次/成功入池/连败重取各分支）：【可达-可测】注入同步 loader，推 monoMs 过 2×切片周期、成功与失败交替。
- L251/264（闸门判定与续接 catch）：【可达-可测】UpdateGates 给水位 0/足、allowFactor 0/1 各一拍。

## TaoLive.Foundation/SourceProfileStore.cs

**未覆盖语句行（5 行）**
- L89/93（Load 文件不存在返回/反序列化空）：【可达-可测】临时目录无文件/空数组 JSON。
- L117（Register CAS 失败重试）：【不可达-防御】并发改档重试路径，单线程测试 TryUpdate 恒成功；多线程压测才可触达。
- L148（ObserveSample CAS 失败重试）：【不可达-防御】同上。
- L174（ClassifyUrl Unknown 臂）：【可达-可测】无签名参数的域名 URL。

**Partial branches（15 处）**
- L85/88/90/91/92/94（Load 路径拼接、文件存在、损坏 catch、空 SourceId 过滤）：【可达-可测】临时目录+损坏 JSON 各一例。
- L99（ProfileOf 未登记臂）：【可达-可测】查未注册源。
- L102（KindOf 解析失败臂）：【可达-可测】档案 Kind 写非法值。
- L116/142（CAS 重试分支）：【不可达-防御】见 L117/148。
- L127（sourceId ?? ""）：【可达-可测】ObserveSample 传 null sourceId。
- L169/172/180/222（ClassifyUrl 非法 URL/签名参数、UrlFingerprint 非法 URL、FlushNow 空路径臂）：【可达-可测】纯函数/临时目录。

## TaoLive.Session/ChannelPrefetcher.cs

**未覆盖语句行（13 行）**
- L37-43（PrefetchNeighborsAsync 守卫与邻台预取）：【可达-可测】注入 resolve 委托，首台/末台/中间台各一例。
- L79-82（DNS 预解析与异常吞）：【可达-可测】prewarmDns 注入抛错委托。
- L89-95（TryHost）：【可达-可测】纯函数（IP 字面量/域名/非法 URL）。
- L106（IndexOf EpgKey+SourceId 匹配臂）：【可达-可测】传同键不同实例。

**Partial branches（13 处）**
- L38/40/41/42（PrefetchNeighborsAsync 空表/未命中/越界侧臂）：【可达-可测】见 L37-43。
- L54/63（PrefetchRecentAsync 守卫/已覆盖跳过臂）：【可达-可测】账本快照含当前台与邻台。
- L74/77/78（PrefetchChannelAsync null 台/StreamUrl 取舍/DNS 判定臂）：【可达-可测】两台分别带/不带 StreamUrl。
- L91/92（TryHost 非法 URL/IP 臂）：【可达-可测】纯函数。
- L99/103（IndexOf 引用相等/键相等臂）：【可达-可测】见 L106。

## TaoLive.Catalog/ChannelWarmup.cs

**未覆盖语句行（7 行）**
- L181-182/195-198（WarmChannelAsync 退避挂起跳过、让路间隔延迟与取消）：【可达-可测】预挂退避账+MinGapOverrideMs 调小+取消 ct。
- L222（SourceKeyOf 非法 URL catch）：【可达-可测】传 "::::" 端点。

**Partial branches（9 处）**
- L55（PaceOf 未注册源臂）：【可达-可测】查未知 SourceId。
- L74（RecordLimited pace null 臂）：【可达-可测】不传 pace。
- L144（构造 nowMs null 臂）：【可达-可测】传 null。
- L167/175/187/194/203/213（WarmChannelAsync 取消/直连跳过/失败挂账/成功恢复/整台未就绪/WarmAllAsync 取消各臂）：【可达-可测】假 resolver+假 Http 按序驱动。

## TaoLive.Session/BackgroundLane.cs

**未覆盖语句行（13 行）**
- L59（DrawerOpen 属性）：【可达-可测】SetDrawerOpen(true)。
- L113-116（Rescue 档直通）：【可达-可测】tier:Rescue 调 RunAsync。
- L123-136（Scout 播放中停摸、首帧门控等待、水位稳闸等待）：【可达-可测】置 Playing/FirstFrameLanded/WaterStable 各态后起任务，短延迟轮询。

**Partial branches（9 处）**
- L55/60（DrawerOpen setter 分支）：【可达-可测】开/关各一次。
- L77/80/81（PendingWaitMs 首帧未落/水位不稳/间隔计算臂）：【可达-可测】置位组合驱动。
- L112（WaitTurnAsync 非 Rescue 臂）：【可达-可测】Standard 档任务。
- L122/128/133（Scout 停摸/首帧门控/水位闸循环臂）：【可达-可测】见 L123-136。

## TaoLive.Foundation/WatchTimeLedger.cs

**未覆盖语句行（19 行）**
- L35-53（Load 全体）：【可达-可测】临时目录写 watchtime.json 后 Load；损坏文件/较小值不覆盖各一例。
- L76-80（Snapshot）：【可达-可测】记账后取快照。
- L118（WriteThrough 落盘失败 catch）：【可达-可测】目录指向不可写路径。

**Partial branches（7 处）**
- L36/42/44/45/48（Load 空目录/无文件/null 反序列化/较小值臂）：【可达-可测】见 L35-53。
- L58（Record 非法段臂）：【可达-可测】deltaMs≤0 或空键。
- L71（TotalMs 未知台臂）：【可达-可测】查未记账台。

## TaoLive.Session/LinkHealthLine.cs

**未覆盖语句行（16 行）**
- L25（DropEmaHalfLife 静态字段）：【可达-可测】触碰类型即初始化。
- L42-66（ReportStockDrop/ReportSegmentInterval/ReportThroughput/ReportBadFrames）：【可达-可测】注入 monoMs 后直接喂账。

**Partial branches（7 处）**
- L37（构造 monoMs null 臂）：【可达-可测】传 null。
- L43/50（sinceMs≤0 臂/EMA 首拍臂）：【可达-可测】喂 0 间隔；首拍前喂账。
- L58（ReportSegmentInterval ≤0 臂）：【可达-可测】喂 0。
- L85/86/100（Margin 无间隔账/无吞吐账/AllowFactor 停臂）：【可达-可测】不喂间隔/吞吐直接取 Margin；喂负项后取 AllowFactor。

## TaoLive.Session/HandshakeProber.cs

**未覆盖语句行（4 行）**
- L63（HttpHandshakeProber 状态码判定）：【可达-可测】本地 HTTP 桩返回 2xx/4xx。
- L110-112（TicketFusedProber 单路异常臂）：【可达-可测】resolve 委托抛异常。

**Partial branches（6 处）**
- L55/90/91（两实现空候选/构造 null 校验臂）：【可达-可测】空候选表；传 null 依赖。
- L64（状态码 2xx-4xx 内外臂）：【可达-可测】本地 HTTP 桩。
- L97/108（TicketFusedProber Web/直连分支臂）：【可达-可测】两类端点混编候选。

## TaoLive.Catalog/EpgClient.cs

**未覆盖语句行（30 行）**
- L22-39（CntvIds 映射表初始化）：【可达-可测】触碰 MappedCount/IdMap/Fetch 即触发静态初始化。
- L44/46/101（MappedCount/构造/IdMap）：【可达-可测】直接访问。
- L50-62（Fetch 全体）：【可达-可测】映射外键返回 null（无网络）；映射内键需本地 HTTP 桩或子类覆写（Fetch 为 virtual，测试可覆写网络段）。

**Partial branches（5 处）**
- L51（映射外/空键臂）：【可达-可测】传 "c_unknown"。
- L71/75/76/77（ParseWithKey 缺 program/缺 t/缺 st/et/et≤st 臂）：【可达-可测】纯函数，构造残缺 JSON。

## TaoLive.Scheduler/BackgroundScheduler.cs

**未覆盖语句行（9 行）**
- L86-91（Unregister）：【可达-可测】登记后注销，断言 RegisteredCount。
- L106（RunAsync 取消收场）：【可达-可测】取消 ct 后跑循环。
- L144/154（PumpAsync 执行取消返回/跑完让一拍）：【可达-可测】executor 内取消；正常跑一轮观察节拍。

**Partial branches（5 处）**
- L57（构造 executor null 抛参）：【可达-可测】传 null。
- L76（TriggerNow 未登记/已跑完臂）：【可达-可测】对陌生名与已 Done 的 Job 触发。
- L112（PumpAsync 取消退出臂）：【可达-可测】见 L106。
- L126/127（排序同档最久未跑/档位优先臂）：【可达-可测】登记多路不同档位 Job 推钟竞争。

## TaoLive.Session/QualityHealthStore.cs

**未覆盖语句行（34 行）**
- L35/39（MicrolagWindow/RecentGoodBonus 静态字段）：【可达-可测】触碰类型即初始化。
- L52（Entries 属性）：【可达-可测】直接读。
- L59-90（ReportBufferingTrip/ReportGood 全体）：【可达-可测】注入时钟后喂账：窗内 3 次坐实、跨窗重攒、成功入账。
- L101-105（ReportBadFrames）：【可达-可测】直接喂。
- L139-141（SortScore 近期成功提权臂）：【可达-可测】预置 LastGoodUtcMs=现在。

**Partial branches（5 处）**
- L55（Key 三元空值臂）：【可达-可测】传 null 字段。
- L67（微卡窗口已清哨兵臂）：【可达-可测】坐实一次后再喂。
- L102（ReportBadFrames count≤0 臂）：【可达-可测】喂 0。
- L117（IsBlindZoneUrl 空 URL 臂）：【可达-可测】纯函数。
- L137（SortScore 无 Good 臂）：【可达-可测】空条目打分。

## TaoLive.Session/SwitchArbiter.cs

**未覆盖语句行（6 行）**
- L146-148（CancelAll）：【可达-可测】Submit 后 CancelAll，断言 SweepPending 不再放行。
- L161-163（ReportOutcome 成功清账臂）：【可达-可测】记 2 次失败后报成功，断言账清零。

**Partial branches（5 处）**
- L64（构造 monoMs null 臂）：【可达-可测】传 null。
- L138/139（SweepPending 无挂账/未超时臂）：【可达-可测】空账泵；挂账后立即泵。
- L160/165（ReportOutcome success 臂/与升档无关臂）：【可达-可测】见 L161-163；从未对齐时报失败。

## TaoLive.Session/SourceHealthStore.cs

**未覆盖语句行（15 行）**
- L77（Load 无文件返回）：【可达-可测】空目录 Load。
- L90-93（Load 损坏 catch）：【可达-可测】写坏 JSON 后 Load。
- L106-114（MarkDirty 节流回调体）：【可达-可测】MarkDirty 后等 FlushDebounceMs（测试可等待或临时调小节拍）。
- L146-149（FlushNow 落盘失败 catch）：【可达-可测】目录指向不可写路径。

**Partial branches（4 处）**
- L57（dir null 臂）：【可达-可测】传 null。
- L68/72/73（Load 记录过滤/过期/状态映射臂）：【可达-可测】构造过期与非法状态记录。

## TaoLive.Foundation/BufferPolicy.cs

**未覆盖语句行（8 行）**
- L21（MaxWatermarkSeconds）：【可达-可测】直测（读 AdvancedTuning 旋钮）。
- L30-36（Observe 三档）：【可达-可测】纯函数：低水位/未满/贴边各一例。
- L67/75（BufferInventory.Default/FillWindowCachingMs）：【可达-可测】直测。

**Partial branches（3 处）**
- L33/34/71（Observe 不稳臂/未满臂/StartCachingMs 两档臂）：【可达-可测】纯函数。

## TaoLive.Foundation/FallbackSourceRegistry.cs

**未覆盖语句行（1 行）**
- L184（Table 静态数组初始化）：【可达-可测】触碰 Table/Has/GetAll 即初始化。

**Partial branches（3 处）**
- L188（ContainsUrl 命中/未命中臂）：【可达-可测】纯函数。
- L201（Index 分组初始化）：【可达-可测】触碰即执行。
- L217（OrderByDiscipline 观察档垫底臂）：【可达-可测】纯函数。

## TaoLive.Model/Channel.cs

**未覆盖语句行（6 行）**
- L27（Unavailable 属性）：【可达-可测】构造时赋值。
- L44（CategoryName 默认臂）：【可达-可测】Category=UserSource。
- L60/69-71（构造器 switch 臂、CategoryGroup 属性）：【可达-可测】"自有源" 字符串构造；直接 new CategoryGroup。

**Partial branches（3 处）**
- L34（IsValid 各假臂）：【可达-可测】空 Id/空名/空端点各一例。
- L37/53（CategoryName/构造器 switch 剩余臂）：【可达-可测】逐分类字符串。

## TaoLive.Session/DocChainGuard.cs

**未覆盖语句行（5 行）**
- L53-65（AdaptRule 各属性）：【可达-可测】构造含 Rules 的文档序列化/反序列化。

**Partial branches（3 处）**
- L34/41（TryParse 空体/解析 null 臂）：【可达-可测】纯函数。
- L83（Sha256Hex null 臂）：【可达-可测】传 null。

## TaoLive.Session/SwitchPolicies.cs

**未覆盖语句行（1 行）**
- L117（SwitchHud.RetryBackoff）：【可达-可测】纯函数。

**Partial branches（3 处）**
- L36/43/80（StartOrder 单元素臂/First 空表臂/SwitchDebouncer.Tap null 键臂）：【可达-可测】纯函数。

## TaoLive.Session/CommandReplay.cs

**未覆盖语句行（11 行）**
- L15-25（KeyIds 字典初始化）：【可达-可测】触碰 KeyIds 即初始化。

**Partial branches（2 处）**
- L33（Parse null 输入臂）：【可达-可测】传 null。
- L37（空行/注释行臂）：【可达-可测】JSONL 含空行与 // 注释。

## TaoLive.Session/FallbackQueueSource.cs

**未覆盖语句行**：无。

**Partial branches（2 处）**
- L29（注册表与订阅源双空臂）：【可达-可测】无名台+空订阅库。
- L47（healthOf null 臂）：【可达-可测】不注入 healthOf。

## TaoLive.Session/ListTabs.cs

**未覆盖语句行（1 行）**
- L35（Label 默认臂）：【可达-可测】枚举全覆盖后不可达的防御臂，但可强转非法枚举值触发——【可达-可测】(Tab)99 强转。

**Partial branches（2 处）**
- L27（Label switch 剩余臂）：【可达-可测】同上。
- L99（OrderFavorites 收藏号无对应台臂）：【可达-可测】favoriteOrder 含目录外编号。

## TaoLive.Session/NetReadiness.cs

**未覆盖语句行（9 行）**
- L79-99（TcpProbeAsync 全体）：【可达-可测】本地 TcpListener 桩（监听 127.0.0.1 随机端口，WaitAsync 注入 probe 时不可达，但直测 TcpProbeAsync 经反射/或经默认 probe 指向本地桩）。归可达（本地桩）。

**Partial branches（2 处）**
- L49（probe null 臂）：【可达-可测】不注入 probe（配合本地桩）。
- L65（单探异常 report 臂）：【可达-可测】注入抛错 probe+report 收集器。

## TaoLive.Catalog/DeviceIdentity.cs

**未覆盖语句行（2 行）**
- L36（GetGuid 旧文件复用路径）：【可达-可测】临时目录预写 32 位十六进制文件后取值。
- L45（落盘失败退随机 catch）：【可达-可测】目录指向不可写路径。

**Partial branches（1 处）**
- L35（旧文件校验失败臂）：【可达-可测】预写非 32 位/非十六进制内容。

## TaoLive.Catalog/NmtvXxtea.cs

**未覆盖语句行**：无。

**Partial branches（1 处）**
- L25（Decrypt 长度不合法全量输出臂）：【可达-可测】Encrypt/Decrypt 回环+构造末 dword 越界样本。

## TaoLive.Catalog/YspCkey.cs

**未覆盖语句行（21 行）**
- L271-277（RemainingCooldownMs）：【可达-可测】MarkCooldown 后查剩余；未冻结查为 0。
- L281-287（ClearAll）：【可达-可测】入账后清空，断言 InCooldown=false。
- L291-293（InvalidateChannel）：【可达-可测】入缓存后作废。
- L300-305（ClearCooldowns）：【可达-可测】入退避账后清账。

**Partial branches（1 处）**
- L274（RemainingCooldownMs 未冻结臂）：【可达-可测】见 L271-277。

## TaoLive.Foundation/AdvancedTuning.cs

**未覆盖语句行（2 行）**
- L57/61（Apply 四参/六参重载）：【可达-可测】直接调用重载。

**Partial branches（1 处）**
- L75（RetryMaxMs<RetryBaseMs 收敛臂）：【可达-可测】Apply 传 retryMax<retryBase。

## TaoLive.Session/BitrateGovernor.cs

**未覆盖语句行（5 行）**
- L82-86（Reset）：【可达-可测】降档后 Reset，断言 CurrentCeiling=null。

**Partial branches（1 处）**
- L64（Update 升档窗未满臂）：【可达-可测】裕度≥0.6 不足 30 秒即喂负值。

## TaoLive.Session/FavoriteStore.cs

**未覆盖语句行**：无。

**Partial branches（1 处）**
- L34（MoveDown 已在末位臂）：【可达-可测】对最后一个收藏 MoveDown。

## TaoLive.Session/FramePulse.cs

**未覆盖语句行**：无。

**Partial branches（1 处）**
- L103（WindowP95MsAt 样本不足臂）：【可达-可测】窗内不足 2 拍取值。

## TaoLive.Session/SelfHeal.cs

**未覆盖语句行**：无。

**Partial branches（1 处）**
- L27（OnFailure 已离线臂）：【可达-可测】连续失败超过阈值后继续回灌。

## TaoLive.Session/ServiceCollectionExtensions.cs

**未覆盖语句行（6 行）**
- L23-26（kernelFactory 注入分支）：【可达-可测】AddTaoLiveSession 传 kernelFactory 后 BuildServiceProvider 取 PlaybackSessionManager。
- L37/40（SystemClock 两属性）：【可达-可测】直接读。

**Partial branches（1 处）**
- L22（kernelFactory null 臂）：【可达-可测】不传 factory，注册 IPlayer 后解析。

## TaoLive.Session/SessionTrace.cs

**未覆盖语句行（1 行）**
- L17（Emit catch）：【可达-可测】Sink 注入抛异常委托。

**Partial branches（1 处）**
- L16（Sink null 臂）：【可达-可测】不接线直接 Emit。

## TaoLive.Foundation/IPlayer.cs

**未覆盖语句行（4 行）**
- L46/51/55（BufferedMsKnown/SeekBack/ApplyBitrateCeiling 默认接口实现）：【可达-可测】用不覆写这三个成员的测试替身经接口调用。
- L106（PlayerFrameTickEventArgs）：【可达-可测】直接构造。

**Partial branches**：无。

## TaoLive.Model/PlaybackParams.cs

**未覆盖语句行（2 行）**
- L14/17（StartupThreshold/PrefillWaterline 静态字段）：【可达-可测】触碰类型即初始化。

**Partial branches**：无。

---

## 汇总统计

### Partial branches（三分类）

| 分类 | 处数 | 说明 |
|---|---|---|
| 【可达-可测】 | 304 | 注入桩/假时钟/本地 HTTP·TCP 桩即可触发 |
| 【不可达-防御】 | 2 | SourceProfileStore L116/L142 CAS 并发重试臂 |
| 【UI壳层】 | 0 | 无 Android 壳层/平台回调专属分支 |

合计 306 处 partial branches。

### 未覆盖语句行（三分类）

| 分类 | 行数 | 说明 |
|---|---|---|
| 【可达-可测】 | 515 | 含需本地 HTTP/TCP 桩的 BLL 网络封装（HttpSegmentLoader、HttpHandshakeProber、TcpProbeAsync、EpgClient.Fetch 等），均可在单测环境触达 |
| 【不可达-防御】 | 2 | SourceProfileStore L117/L148 CAS 重试体 |
| 【UI壳层】 | 0 | — |

合计 517 行未覆盖语句。

### 可达类全补齐后分支覆盖预估

- 当前分支覆盖：78.92%（1535/1945）。
- 可达-可测 partial branches 全补齐新增覆盖臂：304 处分支的未覆盖侧全部转覆盖（防御 2 处保持未覆盖）。
- 估算式：(1535 + 304) / 1945 ≈ **94.6%**。
- 剩余缺口 ≈ 2 处防御臂（并发 CAS 重试）+ 各分支内部更细的组合路径，属长尾；若并发压测补齐防御臂，理论上限 (1535+306)/1945 ≈ 94.8%。

---

## 证据落点（实际通读的源码文件）

1. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/PlaybackSessionManager.cs`（全文 720 行）
2. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/PlaybackSession.cs`（全文 1237 行）
3. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Catalog/TicketResolver.cs`（全文 561 行）
4. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/ChannelPrecheck.cs`（全文 329 行）
5. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/StealthPipeline.cs`（全文 285 行）
6. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Foundation/SourceProfileStore.cs`（全文 225 行）
7. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/ChannelPrefetcher.cs`（全文 108 行）
8. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Catalog/ChannelWarmup.cs`（全文 224 行）
9. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/BackgroundLane.cs`（全文 143 行）
10. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Foundation/WatchTimeLedger.cs`（全文 121 行）
11. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/LinkHealthLine.cs`（全文 102 行）
12. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/HandshakeProber.cs`（全文 117 行）
13. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Catalog/EpgClient.cs`（全文 102 行）
14. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Scheduler/BackgroundScheduler.cs`（全文 155 行）
15. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/QualityHealthStore.cs`（全文 148 行）
16. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/SwitchArbiter.cs`（全文 208 行）
17. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/SourceHealthStore.cs`（全文 151 行）
18. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Foundation/BufferPolicy.cs`（全文 76 行）
19. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Foundation/FallbackSourceRegistry.cs`（全文 224 行）
20. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Model/Channel.cs`（全文 72 行）
21. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/DocChainGuard.cs`（全文 113 行）
22. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/SwitchPolicies.cs`（全文 121 行）
23. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/CommandReplay.cs`（全文 56 行）
24. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/FallbackQueueSource.cs`（全文 60 行）
25. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/ListTabs.cs`（全文 107 行）
26. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/NetReadiness.cs`（全文 101 行）
27. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Catalog/DeviceIdentity.cs`（全文 51 行）
28. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Catalog/NmtvXxtea.cs`（全文 107 行）
29. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Catalog/YspCkey.cs`（L240-306 区段，覆盖缺口全部落于此）
30. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Foundation/AdvancedTuning.cs`（全文 84 行）
31. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/BitrateGovernor.cs`（全文 87 行）
32. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/FavoriteStore.cs`（全文 68 行）
33. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/FramePulse.cs`（全文 114 行）
34. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/SelfHeal.cs`（全文 39 行）
35. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/ServiceCollectionExtensions.cs`（全文 41 行）
36. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Session/SessionTrace.cs`（全文 19 行）
37. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Foundation/IPlayer.cs`（全文 107 行）
38. `/root/workspace/taolive-work/taolive-2p0/src/TaoLive.Model/PlaybackParams.cs`（全文 18 行）

辅助核对：`.verify/gaps-vc90.txt`（缺口清单本体）。
