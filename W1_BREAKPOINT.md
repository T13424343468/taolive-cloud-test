# W1 断点（2026-11-15 03:07 更新 · 调度核心自担）

## 已完成
- 七工程骨架齐全，Tv/Phone csproj 移植 v31 裁剪保全名单（坑 7A.8），版本锚 com.taolive.tv / 2.0.0(2) 字面量在 AndroidManifest。
- 纯库 Tests 5/5 全绿（03:07 调度核心复核）；环境口径 export DOTNET_ROOT=/root/workspace/.local/dotnet10，构建锁 /tmp/taolive-build.lock 串行。
- 云端三连败根因收口：①APT2260 mipmap/ic_launcher 缺失 → 按素材沿用令从一期树搬入双端图标（ac60794，build-apk 已绿）；②install-and-launch 班 avdmanager 不在 PATH → SDK 工具路径写入 GITHUB_ENV（f75849a）。
- 教训入档：本地无法真编安卓壳（aapt2 阶段），壳一律以云端为唯一真闸；「本地全量构建 0 错」结论不可靠。

## 当前状态（03:45 更新 · 老板三令后）
- 内核换向（老板 03:37 令）：直接上 ExoPlayer（Media3），不 LibVLC 打底。已落地：Players→net10.0-android，Xamarin.AndroidX.Media3.ExoPlayer/Hls/DataSource 1.11.1，ExoPlayerPlayer.cs（PlayerListener 桥，事件映射 Buffering/Ready/Error），LibVlcPlayer.cs 已删（commit 70dc8c1）。云端 build-apk 绿（run 43633283008），install-and-launch 在跑。
- 不分期（老板 03:37 令）：M1/M2/M3 节奏作废，全功能一次做完整（电视端优先不变）。
- 触达约定（老板 03:38 令）：后台连续推进，每小时报一次（:40 整），不打扰。
- 测试：19/19 全绿（SessionTests 断言修复 5eac3e4）。

## 待办（04:05 更新，按序续做）
1. ~~W4 台单+取票移植~~ DONE（commit abd9609）：TaoLive.Catalog 八源 123 台逐字移植（YSP63/cztv/gzstv/kankan/nmtv/jstv/xatv/gxtv/直连15），TicketResolver 按主机分发+5分钟缓存+YSP 指数退避；cKey 对拍向量入 Tests（29/29 绿）。
   - 注：交接提到的「备援 58 路直连源」在库内无清单文件（一期 UserFallback 从未注入，为用户自有 m3u 运行时导入口）——2.0 内置台单先落已核实的直连 15 路，待老板/台账补其余清单。
2. ~~W5 壳接线~~ DONE（commit abd9609）：Tv 列表（编号+分类头）+播放页 SurfaceView+数字直达+上下换台+返回回列表+看门狗泵。
3. 盯 run 43633845582（abd9609）云编译绿；run 43633283008 install-and-launch 终态（ExoPlayer 版包在模拟器装起）。
4. 全功能清单对照一期逐项落：EPG 节目单（CntvEpgClient 移植）、收藏置顶、退出对话框、备源耗尽重试提示、屏幕信息行（特征关：不在）。
5. 九层全测+特征关→云端出包→云真机绿→10:00 交付。

## 倒排
- 内部 07:30 出包过闸；09:00 云真机绿；10:00 交付老板。
