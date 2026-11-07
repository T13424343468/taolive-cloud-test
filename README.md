# TaoLive（道映）源码骨架

安卓电视端直播软件「道映」· 内核 .NET 9 Android + LibVLCSharp（复用 TaoPlay 电视版内核路线，见项目档案《道映第一版技术方案》）

## 结构

```
src/
  TaoLive.Model          数据模型（Channel/分类/收藏设置 + 水墨主题常量）
  TaoLive.BLL            业务层（源适配层/取流器/目录编号/收藏/数字键直达/EPG/m3u 解析）
  TaoLive.UI.Android.Tv  电视端壳（焦点 UI 骨架/遥控映射/开机自启接收器）
docs/
  icon-kit/              全尺寸正式图标（水墨圆印「映」：5 档 launcher 尺寸 +
                         自适应前景/背景 + 320×180 TV banner + 预览拼图）
```

## 状态（2026-11-07）

- 骨架阶段：类结构与关键交互已立，**未编译**（首编译目标：BLL+Model 纯 C# 部分先绿，再上 Android 壳）。
- `Shared/VideoHost.cs` 为 TaoPlay 同源播放壳的适配起点（含适配说明头注），csproj 中暂不参与编译。
- libvlc 本机库未就位：构建前从 TaoPlay `native/vlc365/arm64/` 就位（`NativeVlcDir` 属性或直接复制）。
- `CctvSource` 台单表为占位口径，待国家级源摸底数据回填真实流地址与判定。

## 硬指标对照

- 分类：`ChannelCatalog.Rebuild` 六大分类分组（硬性验收）。
- 收藏：`FavoriteStore`（置顶组 + 上/下移排序，硬性验收）。
- 频道号快速定位：`ChannelNumberController`（数字回显、停 1 秒跳台、退格）。
- 快捷键两档：A 档 = MainActivity 收 TCL 自定义键键值绑频道（真机实测）；B 档 = 系统层绑打开 App + `OnNewIntent` 秒开回上次频道。
- 开机自启：`BootReceiver`（默认关；TCL 雷鸟 ROM 不声明 HOME 身份，避免安装校验链拒装）。

## 构建指引

1. `dotnet build src/TaoLive.Model && dotnet build src/TaoLive.BLL`（本机可验）。
2. Android 壳需 dotnet android 工作负载 + libvlc 本机库就位后构建。
