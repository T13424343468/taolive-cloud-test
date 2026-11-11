// v1.0.8 收尾修复: BLL 多个 v1.0.8 新增文件引用 Model 层类型 (Channel/ChannelCategory 等)
// 却缺少 using, 源码入库时未编译验证导致 CS0246。全局 using 一次性补齐。
global using TaoLive.Model;
