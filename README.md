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

## 状态（2026-11-08，v1.0.8）

- v1.0.8 二十项修复完成：看门狗防卡死、退出再开黑屏、代数守卫、预检循环、列表翻页/收藏排序/统一设置页等全部落地；
- 全测：LogicTests 214/0 全绿（沙箱 dotnet 9.0.320 本地）；TV 层 .NET Android 35 SDK 编译 0 错误 0 警告；
- 详见《交付说明.md》v1.0.8 章节（含四项「接线缺口」如实声明与 12-01 云端补验口径）；修复任务书见 docs/v1.0.8修复任务书.md。
- 部分第17/18/19/20 项为「逻辑已立、UI/取流链路未接」，如实声明，不冒充已生效。

## 硬指标对照

- 分类：`ChannelCatalog.Rebuild` 六大分类分组（硬性验收）。
- 收藏：`FavoriteStore`（置顶组 + 上/下移排序，硬性验收）。
- 频道号快速定位：`ChannelNumberController`（数字回显、停 1 秒跳台、退格）。
- 快捷键两档：A 档 = MainActivity 收 TCL 自定义键键值绑频道（真机实测）；B 档 = 系统层绑打开 App + `OnNewIntent` 秒开回上次频道。
- 开机自启：`BootReceiver`（默认关；TCL 雷鸟 ROM 不声明 HOME 身份，避免安装校验链拒装）。

## 构建指引

1. `dotnet build src/TaoLive.Model && dotnet build src/TaoLive.BLL`（本机可验）。
2. Android 壳需 dotnet android 工作负载 + libvlc 本机库就位后构建。
