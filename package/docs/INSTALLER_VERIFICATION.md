# 安装器依赖修复验证（1.8.26-r1）

- 基线：在独立副本的 Managed 中放入相同身份、缺少目标类型的 Cecil，旧 worker 复现 `Mono.Cecil.DefaultReflectionImporter could not be loaded`，worker 退出 85。
- 修改版：同一个冲突 DLL 保持不动，Bootstrap 绑定私有依赖，初始化、全量安装、男女形变收据、分类表、JSON 树及真实 BC7 编解码通过。
- 目标 Managed 完全没有 Cecil：检查通过，实际依赖来源为 `private-worker`。
- 私有 Cecil 缺失／损坏：退出 70；游戏核心、Mono、VAR、已有缓存哈希及事务数量保持。
- 私有 Cecil 身份不变但缺少类型：实际预检退出 70，保留具体类型异常和 worker 日志，不进入安装文件阶段。
- 未知核心原件拒绝、重复安装不改 JSON 时间戳、后续分类失败自动回滚、独立副本完整哈希还原通过。
- 安装器原有 7 个事务／配置／进程保护／链接还原单元测试通过。

测试只使用空 `VaM.exe` 标记的独立副本；本机游戏未被启动、关闭或部署。全量副本测试使用测试专用 runner，仅隔离全局游戏进程检查，真实 Mono 和文件事务保持。正式 BAT 的全局进程保护不变。

本次没有新增加载速度、画面或 VR 验收结论；冻结运行组件与差分字节保持。


## 1.8.26-r2（2026-10-19）

沿用 r1 安装器和私有依赖，只有 UI／Memory 两载荷及其清单哈希更新。重新执行独立中文／空格路径全量回归：基线检查、真实 BC7 编解码、Naturalis 定点修改、男女形变表和 JSON 树、独立副本还原、重复安装、未知核心拒绝、故障后事务回滚；准确发行 ZIP 另解压检查／安装／还原并核对全部原始文件哈希。

桌面／BA 三臂结果与测试边界见 [COMPATIBILITY_REVISION.md](COMPATIBILITY_REVISION.md)。本次未处理 `meta.json not found`／空白预览，没有新增实机速度或 Unity 桌面交互验收结论。
